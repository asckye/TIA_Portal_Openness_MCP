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

namespace TiaMcpServer.Siemens
{
    // Phase 4 sub-batch 1 (2.7.34): Step7 software units and the small PlcSoftware service providers. Official pages:
    // "Accessing software unit" / "Working with software unit" / "Accessing software unit underlying objects" / "Updating
    // software unit properties" / "Accessing namespaces for software units" / "Units as mastercopies", "Accessing the
    // SafetyUnit" / "Creating/deleting SafetyUnit relations", "Accessing name value type document", "Exporting UDT as
    // document" / "Importing UDT from document", "Accessing Software Checksum", "Changing blocks using fingerprints",
    // "Setting up write protection of blocks", "Updating project properties" (simulation / virtual PLC support) and
    // "Accessing attributes of an address object" (process image assignment). Everything is the official V20/V21 API;
    // V21-only members (PlcUnitSystemGroup.Name, PlcDocumentComposition.CreateFrom, PlcBlockWriteProtectionProvider,
    // PlcSimulationSettingsProvider / VirtualPlcSettingsProvider, PlcTagProvider, ProcessImageProvider) are #if-guarded.
    public partial class Portal
    {
        // ---- unit resolution ----------------------------------------------------------------------------------------------------
        private static PlcUnitProvider RequireUnitProvider(PlcSoftware plc)
            => plc.GetService<PlcUnitProvider>() ?? throw new NotSupportedException("PlcUnitProvider unavailable: this PLC family does not support software units.");
        private static PlcUnitBase ExactUnit(PlcUnitSystemGroup group, string unitKind, string name)
        {
            if (unitKind == "safety")
            {
                PlcSafetyUnitComposition safetyUnits = group.SafetyUnits;
                return safetyUnits.Find(name) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact safety unit not found: " + name + " (available: " + string.Join(", ", EngineeringGroupOperations.Items(safetyUnits).Cast<PlcSafetyUnit>().Select(u => u.Name)) + ").");
            }
            PlcUnitComposition units = group.Units;
            return units.Find(name) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact software unit not found: " + name + " (available: " + string.Join(", ", EngineeringGroupOperations.Items(units).Cast<PlcUnit>().Select(u => u.Name)) + ").");
        }
        // Object roots for paths: the PLC itself, or a unit's own block / type system group.
        private PlcUnitBase? OptionalUnit(PlcSoftware plc, string unitName, string unitKind)
            => string.IsNullOrEmpty(unitName) ? null : ExactUnit(RequireUnitProvider(plc).UnitGroup, unitKind, unitName);
        private static PlcBlockGroup BlockRootOf(PlcSoftware plc, PlcUnitBase? unit) => unit == null ? plc.BlockGroup : unit.BlockGroup;
        private static PlcTypeGroup TypeRootOf(PlcSoftware plc, PlcUnitBase? unit) => unit == null ? plc.TypeGroup : unit.TypeGroup;
        private static object ExactObjectUnder(object root, string objectPath, string collection, string label)
        {
            var parts = EngineeringGroupOperations.Parts(objectPath);
            var group = EngineeringGroupOperations.Group(root, string.Join("/", parts.Take(parts.Length - 1)));
            return EngineeringGroupOperations.Find(EngineeringGroupOperations.Get(group, collection), parts.Last()) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact " + label + " not found: " + objectPath);
        }

        // ---- rows ------------------------------------------------------------------------------------------------------------------
        private static JsonObject RelationRow(PlcUnitRelation relation) => new JsonObject { ["relatedObject"] = relation.RelatedObject, ["relationType"] = relation.RelationType.ToString() };
        private static JsonArray Names(object composition, int max = 200)
            => new JsonArray(EngineeringGroupOperations.Items(composition).Take(max).Select(x => (JsonNode)EngineeringGroupOperations.Get(x, "Name").ToString()!).ToArray());
        private static void Safe(JsonObject row, string key, Func<JsonNode?> read)
        {
            try { row[key] = read(); }
            catch (Exception ex) { row[key] = null; row[key + "Error"] = ex.GetBaseException().Message; }
        }
        private static JsonObject UnitRow(PlcUnitBase unit, string kind, bool includeContents)
        {
            var row = new JsonObject { ["name"] = unit.Name, ["kind"] = kind, ["unitClass"] = unit.GetType().Name, ["author"] = unit.Author, ["namespacePreset"] = unit.NamespacePreset };
            Safe(row, "comment", () => MultilingualJson(unit.Comment));
            Safe(row, "relations", () => { PlcUnitRelationComposition relations = unit.Relations; return new JsonArray(EngineeringGroupOperations.Items(relations).Cast<PlcUnitRelation>().Select(r => (JsonNode)RelationRow(r)).ToArray()); });
            if (!includeContents) return row;
            Safe(row, "blockGroup", () => { PlcBlockSystemGroup blocks = unit.BlockGroup; return new JsonObject { ["name"] = blocks.Name, ["blocks"] = Names(blocks.Blocks), ["groups"] = Names(blocks.Groups), ["systemBlockGroups"] = Names(blocks.SystemBlockGroups) }; });
            Safe(row, "typeGroup", () => { PlcTypeSystemGroup types = unit.TypeGroup; PlcDocumentComposition documents = types.Documents; return new JsonObject { ["name"] = types.Name, ["types"] = Names(types.Types), ["groups"] = Names(types.Groups), ["documents"] = Names(documents) }; });
            Safe(row, "tagTableGroup", () => { PlcTagTableSystemGroup tables = unit.TagTableGroup; return new JsonObject { ["name"] = tables.Name, ["tagTables"] = Names(tables.TagTables), ["groups"] = Names(tables.Groups) }; });
            Safe(row, "externalSourceGroup", () => { PlcExternalSourceSystemGroup sources = unit.ExternalSourceGroup; return new JsonObject { ["name"] = sources.Name, ["externalSources"] = Names(sources.ExternalSources), ["groups"] = Names(sources.Groups) }; });
            Safe(row, "alarmTextLists", () => { PlcAlarmTextlistGroup lists = unit.PlcAlarmTextlistGroup; return new JsonObject { ["system"] = Names(lists.PlcAlarmSystemTextlists), ["user"] = Names(lists.PlcAlarmUserTextlists) }; });
            return row;
        }
        private static JsonObject DocumentRow(PlcDocument document) => new JsonObject { ["name"] = document.Name, ["documentClass"] = document.GetType().Name };
        private static JsonArray DocumentMessages(DocumentResultMessageComposition? messages)
            => messages == null ? new JsonArray() : new JsonArray(EngineeringGroupOperations.Items(messages).Cast<DocumentResultMessage>().Select(m => (JsonNode)m.Message).ToArray());
        private static JsonObject DocumentExportRow(DocumentExportResult result)
            => new JsonObject { ["state"] = result.State.ToString(), ["exportedDocuments"] = new JsonArray((result.ExportedDocuments ?? Enumerable.Empty<FileInfo>()).Select(f => (JsonNode)SoftwareUnitDeepLogic.FileRow(f)).ToArray()), ["messages"] = DocumentMessages(result.Messages) };
        private static JsonObject DocumentImportRow(DocumentImportResult result, JsonArray imported)
            => new JsonObject { ["state"] = result.State.ToString(), ["imported"] = imported, ["messages"] = DocumentMessages(result.Messages) };

        // ---- ReadPlcSoftwareUnits ----------------------------------------------------------------------------------------------------
        public ResponseMessage ReadPlcSoftwareUnits(string softwarePath, string unitName = "", string unitKind = "all", bool includeContents = true, int offset = 0, int limit = 50)
            => RunHmiStepTool("ReadPlcSoftwareUnits", meta => {
                SoftwareUnitDeepLogic.RequireOneOf(unitKind, SoftwareUnitDeepLogic.UnitListKinds, "unitKind");
                HardwareServicesLogic.ValidatePagination(offset, limit);
                var plc = ExactPlcForEngineering(softwarePath, false);
                PlcUnitProvider provider = RequireUnitProvider(plc);
                PlcUnitSystemGroup group = provider.UnitGroup;
                PlcUnitComposition units = group.Units; PlcSafetyUnitComposition safetyUnits = group.SafetyUnits;
                var groupRow = new JsonObject { ["unitCount"] = units.Count, ["safetyUnitCount"] = safetyUnits.Count };
#if TIA_V20
                groupRow["name"] = null; groupRow["nameNote"] = "PlcUnitSystemGroup.Name exists in the V21 PublicAPI only.";
#else
                groupRow["name"] = group.Name;
#endif
                meta["unitGroup"] = groupRow;
                var rows = new List<JsonNode>();
                if (unitKind != "safety") rows.AddRange(EngineeringGroupOperations.Items(units).Cast<PlcUnit>().Where(u => string.IsNullOrEmpty(unitName) || string.Equals(u.Name, unitName, StringComparison.OrdinalIgnoreCase)).Select(u => (JsonNode)UnitRow(u, "unit", includeContents)));
                if (unitKind != "unit") rows.AddRange(EngineeringGroupOperations.Items(safetyUnits).Cast<PlcSafetyUnit>().Where(u => string.IsNullOrEmpty(unitName) || string.Equals(u.Name, unitName, StringComparison.OrdinalIgnoreCase)).Select(u => (JsonNode)UnitRow(u, "safety", includeContents)));
                if (!string.IsNullOrEmpty(unitName) && rows.Count == 0) throw new PortalException(PortalErrorCode.NotFound, "Exact unit not found: " + unitName);
                Page(rows.ToArray(), offset, limit, meta);
                meta["apiCallSuccess"] = true;
                meta["scope"] = "PlcUnitBase scalars (Name, Author, NamespacePreset), Comment per culture, relations, and with includeContents the names in BlockGroup / TypeGroup (types + named value type documents) / TagTableGroup / ExternalSourceGroup / PlcAlarmTextlistGroup (first 200 each). No modification.";
                return "Software units read; no modification.";
            });

        // ---- ManagePlcSoftwareUnit (extends the 2.7.x tool: safety units, master copies, per-culture comments) ---------------------
        public ResponseMessage ManagePlcSoftwareUnit(string softwarePath, string action, string name = "", string relatedUnit = "",
            string relationType = "", string propertiesJson = "{}", bool dryRun = true, string unitKind = "unit", string commentsJson = "{}",
            string libraryName = "", string masterCopyPath = "", string copyMode = "")
            => RunHmiStepTool("ManagePlcSoftwareUnit", meta => {
                var request = SoftwareUnitDeepLogic.ValidateUnitRequest(action, unitKind, name, relatedUnit, relationType, propertiesJson, commentsJson, libraryName, masterCopyPath, copyMode, dryRun);
                bool writing = request.Writing;
                using var access = writing ? AcquireHmiEditAccess() : null;
                var plc = ExactPlcForEngineering(softwarePath, writing);
                PlcUnitProvider provider = RequireUnitProvider(plc);
                PlcUnitSystemGroup group = provider.UnitGroup;
                PlcUnitComposition units = group.Units; PlcSafetyUnitComposition safetyUnits = group.SafetyUnits;
                meta["action"] = action; meta["unitKind"] = unitKind; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (action == "list")
                {
                    meta["units"] = new JsonArray(EngineeringGroupOperations.Items(units).Cast<PlcUnit>().Select(u => (JsonNode)UnitRow(u, "unit", false)).ToArray());
                    meta["safetyUnits"] = new JsonArray(EngineeringGroupOperations.Items(safetyUnits).Cast<PlcSafetyUnit>().Select(u => (JsonNode)UnitRow(u, "safety", false)).ToArray());
                    return "Software units listed (standard and safety).";
                }
                if (action == "create" || action == "createFromMasterCopy")
                {
                    if (units.Find(name) != null) throw new InvalidOperationException("Unit already exists: " + name);
                    MasterCopy? source = null; MasterCopyMode? mode = null;
                    if (action == "createFromMasterCopy")
                    {
                        source = ExactMasterCopy(libraryName, masterCopyPath); meta["masterCopy"] = EngineeringScalarProperties.Read(source);
                        mode = string.IsNullOrEmpty(copyMode) ? null : (MasterCopyMode)EngineeringScalarProperties.ConvertValue(JsonValue.Create(copyMode), typeof(MasterCopyMode))!;
                        meta["copyMode"] = mode?.ToString();
                        if (!string.Equals(source.Name, name, StringComparison.OrdinalIgnoreCase)) meta["note"] = "The created unit keeps the master copy name (" + source.Name + "); name is only the expected result used for the existence check.";
                    }
                    if (!writing) return "Unit creation preview; no changes.";
                    meta["mayHaveChanged"] = true;
                    PlcUnit created = source == null ? units.Create(name) : mode == null ? units.CreateFrom(source) : units.CreateFrom(source, mode.Value);
                    meta["apiCallSuccess"] = true;
                    if (units.Find(created.Name) == null) throw new InvalidOperationException("Created unit not found by readback.");
                    meta["after"] = UnitRow(created, "unit", false);
                    return "Software unit created and verified by readback; project not saved.";
                }
                PlcUnitBase unit = ExactUnit(group, unitKind, name);
                meta["before"] = UnitRow(unit, unitKind, false);
                if (action == "read") { meta["before"] = UnitRow(unit, unitKind, true); return "Software unit read; no changes."; }
                if (action == "update")
                {
                    var prepared = EngineeringScalarProperties.Prepare(unit.GetType(), request.Properties);
                    foreach (var (culture, _) in request.Comments)
                    {
                        MultilingualTextItem? item = EngineeringGroupOperations.Items(unit.Comment.Items).Cast<MultilingualTextItem>().FirstOrDefault(i => string.Equals(i.Language?.Culture?.Name, culture, StringComparison.OrdinalIgnoreCase));
                        if (item == null) throw new PortalException(PortalErrorCode.NotFound, "Comment culture not active in the project: " + culture + " (active: " + string.Join(", ", EngineeringGroupOperations.Items(unit.Comment.Items).Cast<MultilingualTextItem>().Select(i => i.Language?.Culture?.Name)) + ").");
                    }
                    if (!writing) return "Unit update preview; no changes.";
                    meta["mayHaveChanged"] = true;
                    if (prepared.Count > 0) EngineeringScalarProperties.Apply(unit, prepared, meta);
                    foreach (var (culture, text) in request.Comments)
                    {
                        MultilingualTextItem item = EngineeringGroupOperations.Items(unit.Comment.Items).Cast<MultilingualTextItem>().First(i => string.Equals(i.Language?.Culture?.Name, culture, StringComparison.OrdinalIgnoreCase));
                        item.Text = text;
                        if (item.Text != text) throw new InvalidOperationException("Comment readback differs for culture " + culture + ".");
                    }
                    meta["apiCallSuccess"] = true; meta["after"] = UnitRow(unit, unitKind, false);
                    return "Software unit scalars / comments written and read back; project not saved.";
                }
                if (action == "delete")
                {
                    meta["dependencyImpact"] = "Deletes the unit and contained engineering objects; references are not analyzed.";
                    if (!writing) return "Unit deletion preview; no changes.";
                    meta["mayHaveChanged"] = true;
                    ((PlcUnit)unit).Delete(); meta["apiCallSuccess"] = true;
                    if (units.Find(name) != null) throw new InvalidOperationException("Unit remains after Delete.");
                    meta["verifiedAbsent"] = true;
                    return "Software unit deleted and absence verified; project not saved.";
                }
                PlcUnitRelationComposition relations = unit.Relations;
                PlcUnitRelation? existing = relations.Find(relatedUnit);
                meta["relationBefore"] = existing == null ? null : RelationRow(existing);
                if (action == "createRelation")
                {
                    if (existing != null) throw new InvalidOperationException("Relation already exists to " + relatedUnit + " (" + existing.RelationType + ").");
                    var kind = (UnitRelationType)EngineeringScalarProperties.ConvertValue(JsonValue.Create(relationType), typeof(UnitRelationType))!;
                    if (!writing) return "Relation creation preview; no changes.";
                    meta["mayHaveChanged"] = true;
                    PlcUnitRelation created = relations.Create(relatedUnit, kind); meta["apiCallSuccess"] = true;
                    if (relations.Find(relatedUnit) == null) throw new InvalidOperationException("Relation absent after Create.");
                    meta["relationAfter"] = RelationRow(created);
                    return "Unit relation created and verified by readback; project not saved.";
                }
                if (existing == null) throw new PortalException(PortalErrorCode.NotFound, "Relation not found to " + relatedUnit + ".");
                if (!writing) return "Relation deletion preview; no changes.";
                meta["mayHaveChanged"] = true;
                existing.Delete(); meta["apiCallSuccess"] = true;
                if (relations.Find(relatedUnit) != null) throw new InvalidOperationException("Relation remains after Delete.");
                meta["verifiedAbsent"] = true;
                return "Unit relation deleted and absence verified; project not saved.";
            });

        // ---- ManagePlcDocuments (named value type documents and UDT documents) ----------------------------------------------------------
        public ResponseMessage ManagePlcDocuments(string softwarePath, string action, string objectKind = "document", string name = "", string unitName = "", string unitKind = "unit",
            string groupPath = "", string directoryPath = "", string importOption = "Override", string libraryName = "", string masterCopyPath = "", string copyMode = "",
            string typePath = "", string version = "", string updatePathsMode = "", bool dryRun = true)
            => RunHmiStepTool("ManagePlcDocuments", meta => {
                bool writing = SoftwareUnitDeepLogic.ValidateDocumentRequest(action, objectKind, name, unitName, unitKind, directoryPath, importOption, libraryName, masterCopyPath, copyMode, typePath, version, updatePathsMode, dryRun);
                bool exporting = action == "export";
                using var access = writing ? AcquireHmiEditAccess() : null;
                var plc = ExactPlcForEngineering(softwarePath, writing);
                var unit = OptionalUnit(plc, unitName, unitKind);
                PlcTypeGroup typeGroup = (PlcTypeGroup)EngineeringGroupOperations.Group(TypeRootOf(plc, unit), groupPath);
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
                    var directory = new DirectoryInfo(directoryPath);
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
                        meta["apiCallSuccess"] = true; meta["result"] = DocumentExportRow(result); meta["newFiles"] = SoftwareUnitDeepLogic.NewFilesSince(directory.FullName, before);
                        if (result.State == DocumentResultState.Failure) throw new PortalException(PortalErrorCode.ExportFailed, "ExportAsDocuments reported Failure: " + string.Join(" | ", DocumentMessages(result.Messages).Select(m => m?.ToString())));
                        return "Documents exported and hashed; no project change.";
                    }
                    var inputs = before.Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (inputs.Length == 0) throw new FileNotFoundException("No file named '" + name + ".*' in directoryPath.", directoryPath);
                    meta["inputFiles"] = new JsonArray(inputs.Select(f => (JsonNode)SoftwareUnitDeepLogic.FileRow(new FileInfo(f))).ToArray());
                    meta["existingBefore"] = target != null;
                    var option = (ImportDocumentOptions)EngineeringScalarProperties.ConvertValue(JsonValue.Create(importOption), typeof(ImportDocumentOptions))!;
                    if (target != null && option == ImportDocumentOptions.None) throw new InvalidOperationException("A " + objectKind + " named '" + name + "' already exists; pass importOption Override to replace it.");
                    if (!writing) return "Document import preview; no changes.";
                    meta["mayHaveChanged"] = true;
                    if (document)
                    {
                        DocumentImportResultForSplDocument result = documents.ImportFromDocuments(directory, name, option);
                        meta["apiCallSuccess"] = true; meta["result"] = DocumentImportRow(result, Names(result.ImportedDocuments));
                        if (result.State == DocumentResultState.Failure) throw new PortalException(PortalErrorCode.ImportFailed, "ImportFromDocuments reported Failure: " + string.Join(" | ", DocumentMessages(result.Messages).Select(m => m?.ToString())));
                    }
                    else
                    {
                        DocumentImportResultForTypes result = types.ImportFromDocuments(directory, name, option);
                        meta["apiCallSuccess"] = true; meta["result"] = DocumentImportRow(result, Names(result.ImportedPlcTypes));
                        if (result.State == DocumentResultState.Failure) throw new PortalException(PortalErrorCode.ImportFailed, "ImportFromDocuments reported Failure: " + string.Join(" | ", DocumentMessages(result.Messages).Select(m => m?.ToString())));
                    }
                    if ((document ? (object?)documents.Find(name) : types.Find(name)) == null) throw new InvalidOperationException("Imported " + objectKind + " not found by readback.");
                    return "Documents imported and verified by readback; project not saved / compiled.";
                }
                // createFromMasterCopy / createFromLibraryType
                if (target != null) throw new InvalidOperationException("A " + objectKind + " named '" + name + "' already exists; creation refused.");
                if (action == "createFromMasterCopy")
                {
                    var source = ExactMasterCopy(libraryName, masterCopyPath); meta["masterCopy"] = EngineeringScalarProperties.Read(source);
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
                var library = ExactOpenEngineeringLibrary(libraryName);
                var libraryType = ExactLibraryType(library, typePath);
                var selected = ExactTypeVersion(libraryType, version);
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

        // ---- ReadPlcChecksums ---------------------------------------------------------------------------------------------------------
        public ResponseMessage ReadPlcChecksums(string softwarePath)
            => RunHmiStepTool("ReadPlcChecksums", meta => {
                var plc = ExactPlcForEngineering(softwarePath, false);
                PlcChecksumProvider? provider = plc.GetService<PlcChecksumProvider>();
                meta["supported"] = provider != null;
                if (provider == null) { meta["software"] = null; meta["textLists"] = null; return "PlcChecksumProvider unavailable: this PLC does not support checksum calculation (GetService returned null)."; }
                meta["software"] = provider.Software; meta["textLists"] = provider.TextLists; meta["apiCallSuccess"] = true;
                meta["note"] = "Both attributes are read-only and null until the program is compiled.";
                return provider.Software == null ? "Checksums read: Software is null (program not compiled)." : "Software and text list checksums read; no modification.";
            });

        // ---- ReadPlcObjectFingerprints ------------------------------------------------------------------------------------------------
        public ResponseMessage ReadPlcObjectFingerprints(string softwarePath, string objectKind, string objectPath, string unitName = "", string unitKind = "unit")
            => RunHmiStepTool("ReadPlcObjectFingerprints", meta => {
                SoftwareUnitDeepLogic.ValidateFingerprintRequest(objectKind, objectPath, unitName, unitKind);
                var plc = ExactPlcForEngineering(softwarePath, false);
                var unit = OptionalUnit(plc, unitName, unitKind);
                var target = objectKind == "block" ? ExactObjectUnder(BlockRootOf(plc, unit), objectPath, "Blocks", "block") : ExactObjectUnder(TypeRootOf(plc, unit), objectPath, "Types", "type");
                meta["objectKind"] = objectKind; meta["objectPath"] = objectPath; meta["unit"] = unit?.Name; meta["objectClass"] = target.GetType().Name;
                FingerprintProvider provider = (target as IEngineeringServiceProvider)?.GetService<FingerprintProvider>() ?? throw new NotSupportedException("FingerprintProvider unavailable on this object (available for blocks and UDTs, not tag tables).");
                IList<Fingerprint> fingerprints;
                try { fingerprints = provider.GetFingerprints(); }
                catch (EngineeringTargetInvocationException ex) { throw new PortalException(PortalErrorCode.InvalidState, "GetFingerprints refused (the object must be consistent; compile first): " + ex.Message, null, ex); }
                meta["records"] = new JsonArray(fingerprints.Select(f => (JsonNode)new JsonObject { ["id"] = f.Id.ToString(), ["value"] = f.Value }).ToArray());
                meta["count"] = fingerprints.Count; meta["apiCallSuccess"] = true;
                meta["scope"] = "Every Fingerprint returned natively (Id / Value); fingerprints consider user input only, never compilation results. Online CPU fingerprints: ReadPlcBlockFingerprints.";
                return "Offline object fingerprints read; no modification.";
            });

        // ---- ManagePlcBlockWriteProtection (V21) ----------------------------------------------------------------------------------------
        public ResponseMessage ManagePlcBlockWriteProtection(string softwarePath, string blockPath, string action, string password = "", string newPassword = "",
            bool confirmProtectionChange = false, bool dryRun = true, string unitName = "", string unitKind = "unit")
            => RunHmiStepTool("ManagePlcBlockWriteProtection", meta => {
                bool writing = SoftwareUnitDeepLogic.ValidateWriteProtectionRequest(action, password, newPassword, confirmProtectionChange, dryRun);
                if (!string.IsNullOrEmpty(unitName)) SoftwareUnitDeepLogic.RequireOneOf(unitKind, SoftwareUnitDeepLogic.UnitKinds, "unitKind");
                using var access = writing ? AcquireHmiEditAccess() : null;
                var plc = ExactPlcForEngineering(softwarePath, writing);
                var unit = OptionalUnit(plc, unitName, unitKind);
                var block = (PlcBlock)ExactObjectUnder(BlockRootOf(plc, unit), blockPath, "Blocks", "block");
                meta["action"] = action; meta["blockPath"] = blockPath; meta["unit"] = unit?.Name; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["passwordProvided"] = !string.IsNullOrEmpty(password); meta["newPasswordProvided"] = !string.IsNullOrEmpty(newPassword);
#if TIA_V20
                throw new NotSupportedException("PlcBlockWriteProtectionProvider exists in the V21 PublicAPI only; V20 offers know-how protection (ManagePlcBlockProtection) but no block write protection.");
#else
                PlcBlockWriteProtectionProvider provider = block.GetService<PlcBlockWriteProtectionProvider>() ?? throw new NotSupportedException("PlcBlockWriteProtectionProvider unavailable on this block.");
                JsonObject State() => new JsonObject { ["name"] = block.Name, ["isDefined"] = provider.IsDefined, ["isProtected"] = provider.IsProtected, ["isKnowHowProtected"] = block.IsKnowHowProtected };
                meta["before"] = State();
                if (action == "read") return "Block write-protection state read.";
                var refusal = SoftwareUnitDeepLogic.WriteProtectionStateRefusal(action, provider.IsDefined, provider.IsProtected);
                if (refusal != null) throw new InvalidOperationException(refusal);
                var invalid = provider.GetInvalidPasswordCharacters()?.ToArray() ?? Array.Empty<char>();
                if ((action == "define" && PlcBlockServicesLogic.ContainsInvalidPasswordCharacter(password, invalid)) || (action == "change" && PlcBlockServicesLogic.ContainsInvalidPasswordCharacter(newPassword, invalid)))
                    throw new ArgumentException("Password contains characters rejected by the native password policy.");
                if (!writing) return "Write-protection preview; no changes.";
                meta["mayHaveChanged"] = true;
                using (var secure = PlcBlockServicesLogic.ToSecureString(password))
                using (var next = action == "change" ? PlcBlockServicesLogic.ToSecureString(newPassword) : null)
                {
                    switch (action)
                    {
                        case "define": provider.Define(secure); break;
                        case "protect": provider.Protect(secure); break;
                        case "unprotect": provider.Unprotect(secure); break;
                        case "change": provider.Change(secure, next!); break;
                        default: provider.Change(secure, null); break;   // remove: official "Change(pwd, null)" drops password and protection
                    }
                }
                meta["apiCallSuccess"] = true; meta["after"] = State();
                bool expectedDefined = action != "remove", expectedProtected = action == "protect" || (action == "change" && provider.IsProtected);
                if (action == "remove" && (provider.IsDefined || provider.IsProtected)) throw new InvalidOperationException("Write protection remains after Change(pwd, null).");
                if (action != "remove" && action != "change" && (provider.IsDefined != expectedDefined || (action != "define" && provider.IsProtected != expectedProtected))) throw new InvalidOperationException("Write-protection readback differs from the requested state.");
                return "Block write protection changed and verified by IsDefined / IsProtected readback; no save/compile/download.";
#endif
            });

        // ---- ManageProjectCompilationSettings ------------------------------------------------------------------------------------------
        public ResponseMessage ManageProjectCompilationSettings(string action = "read", string propertiesJson = "{}", bool dryRun = true)
            => RunHmiStepTool("ManageProjectCompilationSettings", meta => {
                var (writing, values) = SoftwareUnitDeepLogic.ValidateCompilationSettingsRequest(action, propertiesJson, dryRun);
                using var access = writing ? AcquireHmiEditAccess() : null;
                var project = _project as global::Siemens.Engineering.Project ?? throw new NotSupportedException("The bound project is a " + _project?.GetType().Name + "; the compilation settings live on Siemens.Engineering.Project.");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
#if TIA_V20
                meta["access"] = "Project.IsSimulationDuringBlockCompilationEnabled / IsVirtualPlcDuringBlockCompilationEnabled (V20 properties; V21 replaced them by service providers)";
                JsonObject State() => new JsonObject { ["IsSimulationDuringBlockCompilationEnabled"] = project.IsSimulationDuringBlockCompilationEnabled, ["IsVirtualPlcDuringBlockCompilationEnabled"] = project.IsVirtualPlcDuringBlockCompilationEnabled };
                void Write(string key, bool value) { if (key == "IsSimulationDuringBlockCompilationEnabled") project.IsSimulationDuringBlockCompilationEnabled = value; else project.IsVirtualPlcDuringBlockCompilationEnabled = value; }
#else
                // V21 removed the two Project properties; the SW service providers of the project are the only access.
                PlcSimulationSettingsProvider simulation = project.GetService<PlcSimulationSettingsProvider>() ?? throw new NotSupportedException("PlcSimulationSettingsProvider unavailable on the project (V21 service replacing Project.IsSimulationDuringBlockCompilationEnabled).");
                VirtualPlcSettingsProvider virtualPlc = project.GetService<VirtualPlcSettingsProvider>() ?? throw new NotSupportedException("VirtualPlcSettingsProvider unavailable on the project (V21 service replacing Project.IsVirtualPlcDuringBlockCompilationEnabled).");
                meta["access"] = "Project.GetService<PlcSimulationSettingsProvider>() / GetService<VirtualPlcSettingsProvider>() (V21: the Project properties were removed)";
                JsonObject State() => new JsonObject { ["IsSimulationDuringBlockCompilationEnabled"] = simulation.IsSimulationDuringBlockCompilationEnabled, ["IsVirtualPlcDuringBlockCompilationEnabled"] = virtualPlc.IsVirtualPlcDuringBlockCompilationEnabled };
                void Write(string key, bool value) { if (key == "IsSimulationDuringBlockCompilationEnabled") simulation.IsSimulationDuringBlockCompilationEnabled = value; else virtualPlc.IsVirtualPlcDuringBlockCompilationEnabled = value; }
#endif
                meta["before"] = State();
                if (action == "read") return "Project simulation / virtual PLC compilation settings read.";
                meta["requested"] = new JsonObject(values.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create(p.Value))));
                if (!writing) return "Compilation settings update preview; no changes.";
                meta["mayHaveChanged"] = true;
                foreach (var pair in values) Write(pair.Key, pair.Value);
                meta["apiCallSuccess"] = true; meta["after"] = State();
                foreach (var pair in values) if (meta["after"]![pair.Key]!.GetValue<bool>() != pair.Value) throw new InvalidOperationException(pair.Key + " readback differs from the requested value.");
                return "Compilation settings written and read back; project not saved.";
            });

        // ---- channel linked tags (ReadDeviceItemChannels includeLinkedTags) ---------------------------------------------------------------
        private static JsonNode? LinkedTagRows(Channel channel, JsonObject row)
        {
#if TIA_V20
            row["linkedTagsNote"] = "PlcTagProvider.GetLinkedTags exists in the V21 PublicAPI only.";
            return null;
#else
            PlcTagProvider? provider = channel.GetService<PlcTagProvider>();
            if (provider == null) { row["linkedTagsNote"] = "PlcTagProvider unavailable on this channel."; return null; }
            IList<PlcTag> tags = provider.GetLinkedTags();
            return new JsonArray(tags.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["dataTypeName"] = t.DataTypeName, ["logicalAddress"] = t.LogicalAddress, ["tagTable"] = (t.Parent as PlcTagTable)?.Name }).ToArray());
#endif
        }
    }
}
