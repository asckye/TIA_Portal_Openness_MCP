using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcpServer.ModelContextProtocol;
using EngineeringGroupOperations = TiaMcp.Adapters.Native.Plc.PlcGroupOperations;
using EngineeringScalarProperties = TiaMcp.Adapters.Native.Plc.PlcB5Scalar;
using NativeResultState = TiaMcp.Adapters.Native.Plc.PlcB5Result;
using NativeFileOutput = TiaMcp.Adapters.Native.Plc.PlcSourceFileOutput;
using ResponseMeta = TiaMcp.Adapters.Native.Plc.PlcB5Meta;
using PortalException = TiaMcp.Adapters.Contracts.PlcSoftwareException;

namespace TiaMcp.Adapters.Native.Plc
{
    internal sealed partial class PlcB5Adapter
    {
#if PLC_SUPERVISION
        public PlcB5Reply ManagePlcSupervision(string softwarePath, string action, string blockPath = "", string providerKind = "supervision", string compositionName = "",
            string entryName = "", string typeName = "", string filePath = "", string attributesJson = "{}", int offset = 0, int limit = 100, bool confirmDelete = false, bool dryRun = true)
            => RunTool("ManagePlcSupervision", meta => {
                RequireAction(action, SupervisionActions);
                if (providerKind != "supervision" && providerKind != "settings") throw new ArgumentException("providerKind must be supervision/settings.");
                RequirePagination(offset, limit);
                bool write = action is "createEntry" or "deleteEntry" or "setAttributes" or "importSettings";
                bool writing = write && !dryRun;
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var plc = _session.ExactPlcForEngineering(softwarePath, writing);
                object owner = string.IsNullOrEmpty(blockPath) ? plc : _session.ExactMasterCopyPlcSource(softwarePath, blockPath, true);
                meta["softwarePath"] = softwarePath; meta["blockPath"] = blockPath; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                meta["nativeLimits"] = "Openness V20/V21 expose no typed supervision composition; entries are reachable only through IEngineeringObject composition metadata of the providers or through XLSX exchange (ExchangePlcSupervisions).";
                var supervision = OptionalOfficialService(owner, SupervisionProvider, out var supervisionState);
                var settings = OptionalOfficialService(owner, SupervisionSettingsProvider, out var settingsState);
                meta["providerStates"] = new PlcB5Object { ["SupervisionProvider"] = supervisionState, ["SupervisionSettingsProvider"] = settingsState };
                if (action == "read")
                {
                    if (supervision == null && settings == null) throw new NotSupportedException("No ProDiag supervision provider on this object/version (" + supervisionState + "/" + settingsState + ").");
                    bool complete = true;
                    foreach (var (key, provider) in new[] { ("supervisionProvider", supervision), ("settingsProvider", settings) })
                    {
                        if (provider == null) continue;
                        var row = PlcB5Dynamic.Read(provider); row["compositions"] = new PlcB5Array(PlcB5Dynamic.CompositionNames(provider).Select(n => (object)n).ToArray());
                        row["clrProperties"] = EngineeringScalarProperties.Read(provider); meta[key] = row; complete &= row["dataComplete"]!.GetValue<bool>();
                    }
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = complete;
                    return "ProDiag provider metadata read; use readComposition for an advertised composition.";
                }
                if (action == "exportSettings" || action == "importSettings")
                {
                    var provider = settings ?? throw new NotSupportedException("SupervisionSettingsProvider is " + settingsState + ".");
                    bool import = action == "importSettings";
                    var file = import ? new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath)) : NativeFileOutput.Plan(filePath);
                    if (import && !file.Exists) throw new FileNotFoundException("Supervision settings input (.dat) not found.");
                    var method = import ? "Import" : "Export";
                    if (provider.GetType().GetMethod(method, new[] { typeof(FileInfo) }) == null) throw new NotSupportedException("Native SupervisionSettingsProvider." + method + "(FileInfo) unavailable.");
                    if (dryRun) return "Supervision settings " + method.ToLowerInvariant() + " preview; no file or project change.";
                    meta["mayHaveChanged"] = import; meta["mayHaveWrittenFiles"] = !import;
                    var result = EngineeringGroupOperations.Call(provider, method, new[] { typeof(FileInfo) }, file);
                    AttachResult(meta, result);
                    // Typed SupervisionSettingsExportImportResult (State / ErrorCount / WarningCount / Messages of SupervisionSettingsExportImportResultMessage).
                    if (result is global::Siemens.Engineering.SW.Supervision.SupervisionSettingsExportImportResult typed)
                    {
                        meta["errorCount"] = typed.ErrorCount; meta["warningCount"] = typed.WarningCount;
                        meta["messages"] = new PlcB5Array(EngineeringGroupOperations.Items(typed.Messages).Cast<global::Siemens.Engineering.SW.Supervision.SupervisionSettingsExportImportResultMessage>().Select(m => (object)m.Message).ToArray());
                    }
                    var state = result?.GetType().GetProperty("State")?.GetValue(result)?.ToString();
                    meta["nativeState"] = state; meta["nativeSuccessVerified"] = TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(TiaMcp.Adapters.Contracts.NativeResultStates.SupervisionSettings, state);
                    if (!TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(TiaMcp.Adapters.Contracts.NativeResultStates.SupervisionSettings, state)) meta["operationSuccess"] = false;
                    if (!import) meta["file"] = NativeFileOutput.Verify(file);
                    return "Native supervision settings " + method.ToLowerInvariant() + " returned; inspect native state and messages. No save/compile/download.";
                }
                var host = providerKind == "settings" ? settings : supervision;
                if (host == null) throw new NotSupportedException("Selected provider is " + (providerKind == "settings" ? settingsState : supervisionState) + ".");
                if (action == "setAttributes")
                {
                    var prepared = PlcB5Dynamic.Prepare(host, Attributes(attributesJson, true));
                    meta["before"] = PlcB5Dynamic.Read(host);
                    if (!writing) return "Provider attribute update preview; no changes.";
                    PlcB5Dynamic.Apply(host, prepared, meta); meta["apiCallSuccess"] = true;
                    meta["after"] = PlcB5Dynamic.Read(host);
                    return "Provider attributes changed and verified; no save/compile/download.";
                }
                var composition = PlcB5Dynamic.Composition(host, compositionName);
                meta["compositionName"] = compositionName;
                if (action == "readComposition")
                {
                    Attributes(attributesJson, false);
                    var items = EngineeringGroupOperations.Items(composition).ToArray();
                    var rows = items.Skip(offset).Take(limit).Select(x => { var row = PlcB5Dynamic.Read(x); row["clrProperties"] = EngineeringScalarProperties.Read(x); row["name"] = PlcB5Dynamic.Name(x); return row; }).ToArray();
                    meta["records"] = new PlcB5Array(rows); meta["expectedCount"] = items.Length; meta["actualCount"] = rows.Length;
                    meta["nextOffset"] = offset + rows.Length < items.Length ? offset + rows.Length : (int?)null;
                    try { meta["creationTypes"] = new PlcB5Array(PlcB5Dynamic.CreationTypes(composition).Select(t => (object)t.FullName!).ToArray()); }
                    catch (NotSupportedException ex) { meta["creationTypes"] = null; meta["creationTypesError"] = ex.Message; }
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = offset == 0 && rows.Length == items.Length && rows.All(r => r!["dataComplete"]!.GetValue<bool>());
                    return "Advertised supervision composition read with live pagination.";
                }
                if (action == "createEntry")
                {
                    var type = PlcB5Dynamic.CreationType(composition, typeName);
                    var attributes = Attributes(attributesJson, true);
                    meta["typeName"] = type.FullName; meta["requestedAttributes"] = attributes;
                    if (!writing) return "Supervision entry creation preview; advertised type validated, no changes.";
                    meta["mayHaveChanged"] = true;
                    var created = PlcB5Dynamic.Create(composition, type, attributes); meta["apiCallSuccess"] = true;
                    meta["after"] = PlcB5Dynamic.Read(created);
                    if (attributes.ContainsKey("Name") && !string.Equals(PlcB5Dynamic.Name(created), attributes["Name"]?.GetValue<string>(), StringComparison.Ordinal)) throw new InvalidOperationException("Created entry Name readback differs.");
                    return "Supervision entry created through the official dynamic composition; no save/compile/download.";
                }
                // deleteEntry
                Attributes(attributesJson, false); RequireName(entryName, "entryName");
                var entry = PlcB5Dynamic.FindByName(composition, entryName);
                meta["before"] = PlcB5Dynamic.Read(entry);
                if (!confirmDelete) throw new ArgumentException("confirmDelete=true is required to delete a supervision entry.");
                if (!PlcB5Dynamic.CanDelete(entry)) throw new NotSupportedException("Native Delete is not advertised for this entry type.");
                if (!writing) return "Supervision entry deletion preview; no changes.";
                meta["mayHaveChanged"] = true;
                PlcB5Dynamic.Delete(entry); meta["apiCallSuccess"] = true;
                if (EngineeringGroupOperations.Items(composition).Any(x => string.Equals(PlcB5Dynamic.Name(x), entryName, StringComparison.Ordinal))) throw new InvalidOperationException("Entry remains after Delete.");
                meta["verifiedAbsent"] = true;
                return "Supervision entry deleted and verified absent; no save/compile/download.";
            });

        public PlcB5Reply ExportPlcProDiagInfo(string softwarePath, string blockPath, string directoryPath, string unitName = "", string unitKind = "unit", bool dryRun = true)
            => RunTool("ExportPlcProDiagInfo", meta => {
                ValidateRequest();
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                var unit = _session.OptionalUnit(plc, unitName, unitKind);
                var block = (PlcBlock)_session.ExactObjectUnder(_session.BlockRootOf(plc, unit), blockPath, "Blocks", "block");
                CodeBlock code = block as CodeBlock ?? throw new ArgumentException("blockPath must name a code block (FB); " + block.GetType().Name + " has no ExportProDIAGInfo.");
                meta["block"] = new PlcB5Object { ["name"] = code.Name, ["blockClass"] = code.GetType().Name, ["programmingLanguage"] = code.ProgrammingLanguage.ToString(), ["isConsistent"] = code.IsConsistent, ["unit"] = UnitName(unit) };
                var refusal = ProDiagRefusal(code.ProgrammingLanguage.ToString(), code.IsConsistent);
                if (refusal != null) throw new PortalException("InvalidState", refusal);
                var directory = new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(directoryPath));
                if (!directory.Exists) throw new DirectoryNotFoundException("directoryPath must already exist: " + directoryPath);
                meta["dryRun"] = dryRun; meta["mayHaveWrittenFiles"] = false;
                if (dryRun) return "ProDiag CSV export preview; no files written.";
                var before = SnapshotDirectory(directory.FullName);
                meta["mayHaveWrittenFiles"] = true;
                code.ExportProDIAGInfo(directory); meta["apiCallSuccess"] = true;
                meta["newFiles"] = NewFilesSince(directory.FullName, before);
                return "ProDiag alarm messages exported as CSV (new files hashed); no project change.";
            });
#endif
    }
}
