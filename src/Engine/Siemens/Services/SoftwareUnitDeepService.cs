using TiaMcp.Logic.V4;
using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Alarm.TextLists;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    // Step7 software units and the small PlcSoftware service providers. Official pages:
    // "Accessing software unit" / "Working with software unit" / "Accessing software unit underlying objects" / "Updating
    // software unit properties" / "Accessing namespaces for software units" / "Units as mastercopies", "Accessing the
    // SafetyUnit" / "Creating/deleting SafetyUnit relations", "Accessing name value type document", "Exporting UDT as
    // document" / "Importing UDT from document", "Accessing Software Checksum", "Changing blocks using fingerprints",
    // "Setting up write protection of blocks", "Updating project properties" (simulation / virtual PLC support) and
    // "Accessing attributes of an address object" (process image assignment). Everything is the official V20/V21 API;
    // V21-only members (PlcUnitSystemGroup.Name, PlcDocumentComposition.CreateFrom, PlcBlockWriteProtectionProvider,
    // PlcSimulationSettingsProvider / VirtualPlcSettingsProvider, PlcTagProvider, ProcessImageProvider) are #if-guarded.
    internal sealed class SoftwareUnitDeepService
    {
        private readonly IEngineeringSession _session;

        public SoftwareUnitDeepService(IEngineeringSession session) => _session = session;

        private static JsonObject DocumentRow(PlcDocument document) => new JsonObject { ["name"] = document.Name, ["documentClass"] = document.GetType().Name };
        // ---- ManagePlcDocuments (named value type documents and UDT documents) ----------------------------------------------------------
        public ResponseMessage ManagePlcDocuments(string softwarePath, string action, string objectKind = "document", string name = "", string unitName = "", string unitKind = "unit",
            string groupPath = "", string directoryPath = "", string importOption = "Override", string libraryName = "", string masterCopyPath = "", string copyMode = "",
            string typePath = "", string version = "", string updatePathsMode = "", bool dryRun = true)
            => _session.RunHmiStepTool("ManagePlcDocuments", meta => {
                meta["mayHaveChanged"] = false;
                bool writing = SoftwareUnitDeepLogic.ValidateDocumentRequest(action, objectKind, name, unitName, unitKind, directoryPath, importOption, libraryName, masterCopyPath, copyMode, typePath, version, updatePathsMode, dryRun);
                bool exporting = action == "export";
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var plc = _session.ExactPlcForEngineering(softwarePath, writing);
                var unit = _session.OptionalUnit(plc, unitName, unitKind);
                PlcTypeGroup typeGroup = (PlcTypeGroup)EngineeringGroupOperations.Group(_session.TypeRootOf(plc, unit), groupPath);
                PlcDocumentComposition documents = typeGroup.Documents; PlcTypeComposition types = typeGroup.Types;
                bool document = objectKind == "document";
                meta["action"] = action; meta["objectKind"] = objectKind; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                meta["container"] = new JsonObject { ["unit"] = unit?.Name, ["typeGroup"] = typeGroup.Name, ["groupPath"] = groupPath };
                if (action == "list")
                {
                    meta["records"] = document ? new JsonArray(EngineeringGroupOperations.Items(documents).Cast<PlcDocument>().Select(d => (JsonNode)DocumentRow(d)).ToArray())
                                               : new JsonArray(EngineeringGroupOperations.Items(types).Cast<PlcType>().Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["namespace"] = t.Namespace, ["isConsistent"] = t.IsConsistent }).ToArray());
                    meta["apiCallSuccess"] = true;
                    return document ? "Named value type documents listed; no changes." : "PLC data types of the group listed; no changes.";
                }
                object? target = document ? (object?)documents.Find(name) : types.Find(name);
                if (action == "read")
                {
                    if (target == null) throw new PortalException(PortalErrorCode.NotFound, "Exact " + objectKind + " not found: " + name);
                    meta["record"] = document ? DocumentRow((PlcDocument)target) : EngineeringScalarProperties.Read(target);
                    if (document) meta["scope"] = "PlcDocument exposes only Name in the PublicAPI (V20+); export the document to read its content.";
                    return objectKind + " read; no changes.";
                }
                if (exporting || action == "import")
                {
                    var directory = new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(directoryPath));
                    if (!directory.Exists) throw new DirectoryNotFoundException("directoryPath must already exist: " + directoryPath);
                    var before = SoftwareUnitDeepLogic.SnapshotDirectory(directory.FullName);
                    if (exporting)
                    {
                        if (target == null) throw new PortalException(PortalErrorCode.NotFound, "Exact " + objectKind + " not found: " + name);
                        var clash = before.Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase)).ToArray();
                        if (clash.Length > 0) throw new IOException("Files named '" + name + ".*' already exist in directoryPath; overwrite refused: " + string.Join(", ", clash.Select(Path.GetFileName)));
                        if (dryRun) return "Document export preview; no files written.";
                        meta["mayHaveWrittenFiles"] = true;
                        DocumentExportResult result = document ? ((PlcDocument)target).ExportAsDocuments(directory, name) : ((PlcType)target).ExportAsDocuments(directory, name);
                        meta["apiCallSuccess"] = true; meta["result"] = _session.DocumentExportRow(result); meta["newFiles"] = SoftwareUnitDeepLogic.NewFilesSince(directory.FullName, before);
                        NativeResultState.Record(meta, result.State, false, messages: _session.DocumentMessages(result.Messages));
                        meta["targetFiles"] = new JsonArray(new[] { ".s7dcl", ".s7res", ".nvt" }.Select(ext => (JsonNode)NativeResultState.FileRow(Path.Combine(directory.FullName, name + ext))).ToArray());
                        if (result.State == DocumentResultState.Failure) throw new PortalException(PortalErrorCode.ExportFailed, "ExportAsDocuments reported Failure: " + string.Join(" | ", _session.DocumentMessages(result.Messages).Select(m => m?.ToString())));
                        return "Documents exported and hashed; no project change.";
                    }
                    var inputs = before.Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (inputs.Length == 0) throw new FileNotFoundException("No file named '" + name + ".*' in directoryPath.", directoryPath);
                    meta["inputFiles"] = new JsonArray(inputs.Select(f => (JsonNode)SoftwareUnitDeepLogic.FileRow(new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(f)))).ToArray());
                    meta["existingBefore"] = target != null;
                    var option = (ImportDocumentOptions)EngineeringScalarProperties.ConvertValue(JsonValue.Create(importOption), typeof(ImportDocumentOptions))!;
                    if (target != null && option == ImportDocumentOptions.None) throw new InvalidOperationException("A " + objectKind + " named '" + name + "' already exists; pass importOption Override to replace it.");
                    if (!writing) return "Document import preview; no changes.";
                    meta["mayHaveChanged"] = true;
                    if (document)
                    {
                        DocumentImportResultForSplDocument result = documents.ImportFromDocuments(directory, name, option);
                        meta["apiCallSuccess"] = true; meta["result"] = _session.DocumentImportRow(result, Names(result.ImportedDocuments));
                        NativeResultState.Record(meta, result.State, true, messages: _session.DocumentMessages(result.Messages));
                        if (result.State == DocumentResultState.Failure) throw new PortalException(PortalErrorCode.ImportFailed, "ImportFromDocuments reported Failure: " + string.Join(" | ", _session.DocumentMessages(result.Messages).Select(m => m?.ToString())));
                    }
                    else
                    {
                        DocumentImportResultForTypes result = types.ImportFromDocuments(directory, name, option);
                        meta["apiCallSuccess"] = true; meta["result"] = _session.DocumentImportRow(result, Names(result.ImportedPlcTypes));
                        NativeResultState.Record(meta, result.State, true, messages: _session.DocumentMessages(result.Messages));
                        if (result.State == DocumentResultState.Failure) throw new PortalException(PortalErrorCode.ImportFailed, "ImportFromDocuments reported Failure: " + string.Join(" | ", _session.DocumentMessages(result.Messages).Select(m => m?.ToString())));
                    }
                    if ((document ? (object?)documents.Find(name) : types.Find(name)) == null) throw new InvalidOperationException("Imported " + objectKind + " not found by readback.");
                    return "Documents imported and verified by readback; project not saved / compiled.";
                }
                // createFromMasterCopy / createFromLibraryType
                if (target != null) throw new InvalidOperationException("A " + objectKind + " named '" + name + "' already exists; creation refused.");
                if (action == "createFromMasterCopy")
                {
                    var source = _session.ExactMasterCopy(libraryName, masterCopyPath); meta["masterCopy"] = EngineeringScalarProperties.Read(source);
                    MasterCopyMode? mode = string.IsNullOrEmpty(copyMode) ? null : (MasterCopyMode)EngineeringScalarProperties.ConvertValue(JsonValue.Create(copyMode), typeof(MasterCopyMode))!;
                    meta["copyMode"] = mode?.ToString();
                    if (!writing) return "Creation from master copy preview; no changes.";
                    meta["mayHaveChanged"] = true;
                    if (document)
                    {
#if TIA_V20
                        throw new NotSupportedException("PlcDocumentComposition.CreateFrom(MasterCopy) exists in the V21 PublicAPI only.");
#else
                        PlcDocument created = mode == null ? documents.CreateFrom(source) : documents.CreateFrom(source, mode.Value);
                        meta["apiCallSuccess"] = true; meta["after"] = DocumentRow(created);
#endif
                    }
                    else
                    {
                        PlcType created = mode == null ? types.CreateFrom(source) : types.CreateFrom(source, mode.Value);
                        meta["apiCallSuccess"] = true; meta["after"] = EngineeringScalarProperties.Read(created);
                    }
                    return objectKind + " created from master copy; project not saved / compiled.";
                }
                var library = _session.ExactOpenEngineeringLibrary(libraryName);
                var libraryType = _session.ExactLibraryType(library, typePath);
                var selected = _session.ExactTypeVersion(libraryType, version);
                meta["libraryTypeVersion"] = new JsonObject { ["type"] = libraryType.Name, ["version"] = selected.VersionNumber?.ToString(), ["versionClass"] = selected.GetType().Name, ["state"] = selected.State.ToString() };
                UpdatePathsMode? paths = string.IsNullOrEmpty(updatePathsMode) ? null : (UpdatePathsMode)EngineeringScalarProperties.ConvertValue(JsonValue.Create(updatePathsMode), typeof(UpdatePathsMode))!;
                meta["updatePathsMode"] = paths?.ToString();
                if (document)
                {
                    if (selected is not PlcDocumentLibraryTypeVersion documentVersion) throw new ArgumentException("The selected version is a " + selected.GetType().Name + ", not a PlcDocumentLibraryTypeVersion.");
                    if (!writing) return "Creation from library type preview; no changes.";
                    meta["mayHaveChanged"] = true;
#if TIA_V20
                    throw new NotSupportedException("PlcDocumentComposition.CreateFrom(PlcDocumentLibraryTypeVersion) exists in the V21 PublicAPI only.");
#else
                    PlcDocument created = paths == null ? documents.CreateFrom(documentVersion) : documents.CreateFrom(documentVersion, paths.Value);
                    meta["apiCallSuccess"] = true; meta["after"] = DocumentRow(created);
#endif
                }
                else
                {
                    if (selected is not PlcTypeLibraryTypeVersion typeVersion) throw new ArgumentException("The selected version is a " + selected.GetType().Name + ", not a PlcTypeLibraryTypeVersion.");
                    if (!writing) return "Creation from library type preview; no changes.";
                    meta["mayHaveChanged"] = true;
                    PlcType created = paths == null ? types.CreateFrom(typeVersion) : types.CreateFrom(typeVersion, paths.Value);
                    meta["apiCallSuccess"] = true; meta["after"] = EngineeringScalarProperties.Read(created);
                }
                return objectKind + " instantiated from the library type version; project not saved / compiled.";
            });

    }
}
