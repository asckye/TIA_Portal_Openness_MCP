using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.Connection;
using Siemens.Engineering.FingerprintData;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.Alarm.TextLists;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Blocks.Interface;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private static object RequireEngineeringService(object owner, string typeName)
        {
            // Type lookup by name so a member absent from V20 compiles on both targets and degrades to NotSupported.
            var type = typeof(PlcBlockInterface).Assembly.GetType(typeName) ?? throw new NotSupportedException("Native service type unavailable in this Openness version: " + typeName);
            if (owner is not IEngineeringServiceProvider) throw new NotSupportedException("Selected object is not a service provider.");
            try { return typeof(IEngineeringServiceProvider).GetMethod("GetService")!.MakeGenericMethod(type).Invoke(owner, null) ?? throw new NotSupportedException("Service unavailable on selected object: " + typeName); }
            catch (System.Reflection.TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw(); throw; }
        }
        public ResponseMessage ManagePlcBlockProtection(string softwarePath, string blockPath, string action, string password = "", bool confirmProtectionChange = false, bool dryRun = true)
            => RunHmiStepTool("ManagePlcBlockProtection", meta => {
                bool writing = PlcBlockServicesLogic.ValidateProtectionRequest(action, password, confirmProtectionChange, dryRun);
                using var access = writing ? AcquireHmiEditAccess() : null;
                ExactPlcForEngineering(softwarePath, writing);
                var block = ExactMasterCopyPlcSource(softwarePath, blockPath, true) as PlcBlock ?? throw new ArgumentException("blockPath must identify a block.");
                var provider = block.GetService<PlcBlockProtectionProvider>() ?? throw new NotSupportedException("PlcBlockProtectionProvider unavailable on this block/version.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["blockPath"] = blockPath; meta["action"] = action;
                meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                meta["before"] = new JsonObject { ["name"] = block.Name, ["isKnowHowProtected"] = block.IsKnowHowProtected, ["programmingLanguage"] = block.ProgrammingLanguage.ToString() };
                var invalid = provider.GetInvalidPasswordCharacters()?.ToArray() ?? Array.Empty<char>();
                meta["invalidPasswordCharacters"] = new JsonArray(invalid.Select(c => (JsonNode)JsonValue.Create(c.ToString())!).ToArray());
                if (action == "read") return "Block know-how protection state read.";
                bool expected = action == "protect";
                if (block.IsKnowHowProtected == expected) throw new InvalidOperationException(expected ? "Block is already know-how protected; unprotect first." : "Block is not know-how protected.");
                if (expected && PlcBlockServicesLogic.ContainsInvalidPasswordCharacter(password, invalid)) throw new ArgumentException("Password contains characters rejected by the native password policy.");
                if (!writing) return "Block protection preview; no changes.";
                meta["mayHaveChanged"] = true;
                using (var secure = PlcBlockServicesLogic.ToSecureString(password))
                {
                    if (expected) provider.Protect(secure); else provider.Unprotect(secure);
                }
                meta["apiCallSuccess"] = true;
                meta["after"] = new JsonObject { ["isKnowHowProtected"] = block.IsKnowHowProtected };
                if (block.IsKnowHowProtected != expected) throw new InvalidOperationException("Protection readback differs from the requested state.");
                return "Block know-how protection changed and verified by readback; no save/compile/download.";
            });
        public ResponseMessage ManagePlcDataBlockSnapshot(string softwarePath, string blockPath, string action, string filePath = "", bool confirmValueChange = false, bool dryRun = true)
            => RunHmiStepTool("ManagePlcDataBlockSnapshot", meta => {
                bool writing = PlcBlockServicesLogic.ValidateSnapshotRequest(action, filePath, confirmValueChange, dryRun);
                bool exporting = action == "exportSnapshot";
                var file = exporting ? NativeFileOutput.Plan(filePath) : null;
                using var access = writing && !exporting ? AcquireHmiEditAccess() : null;
                // Snapshot/load semantics need the PLC already online in TIA, so Offline is not demanded here; this tool never changes the online state.
                var plc = ExactPlcForEngineering(softwarePath, false);
                var db = ExactMasterCopyPlcSource(softwarePath, blockPath, true) as DataBlock ?? throw new ArgumentException("blockPath must identify a data block.");
                var iface = db.Interface ?? throw new NotSupportedException("Data block exposes no interface.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false; meta["blockPath"] = blockPath; meta["action"] = action;
                meta["onlineState"] = ResolvePlcService<OnlineProvider>(softwarePath, plc)?.State.ToString();
                meta["before"] = EngineeringScalarProperties.Read(db);
                const string valueServiceName = "Siemens.Engineering.SW.Blocks.Interface.ValueService";
                meta["valueServiceTypeAvailable"] = typeof(PlcBlockInterface).Assembly.GetType(valueServiceName) != null;
                object? valueService = null; InterfaceSnapshot? snapshot = null;
                if (!exporting && action != "read") valueService = RequireEngineeringService(iface, valueServiceName);
                else { try { valueService = RequireEngineeringService(iface, valueServiceName); } catch (NotSupportedException ex) { meta["valueServiceUnavailable"] = ex.Message; } }
                if (exporting || action == "read")
                {
                    var attempts = new JsonArray();
                    foreach (var (source, owner) in new (string, object?)[] { ("DataBlock.Interface", iface), ("DataBlock", db), ("ValueService", valueService) })
                    {
                        if (owner == null || snapshot != null) continue;
                        try { snapshot = (owner as IEngineeringServiceProvider)?.GetService<InterfaceSnapshot>(); if (snapshot != null) meta["snapshotServiceSource"] = source; }
                        catch (Exception ex) { attempts.Add(source + ": " + ex.GetBaseException().Message); }
                    }
                    meta["snapshotServiceAttempts"] = attempts; meta["snapshotServiceAvailable"] = snapshot != null;
                    if (exporting && snapshot == null) throw new NotSupportedException("InterfaceSnapshot service unavailable on this data block/version.");
                }
                string nativeMethod = action == "createSnapshot" ? "CreateSnapshot" : action == "loadSnapshotAsActualValues" ? "LoadSnapshotAsActualValues" : action == "loadStartValuesAsActualValues" ? "LoadStartValuesAsActualValues" : "";
                if (nativeMethod.Length > 0 && valueService!.GetType().GetMethod(nativeMethod, Type.EmptyTypes) == null) throw new NotSupportedException("ValueService." + nativeMethod + "() unavailable.");
                meta["semantics"] = "Siemens semantics: CreateSnapshot reads actual values from the CPU; load actions write values into the CPU's actual values. The PLC must already be online in TIA; this tool never goes online/offline.";
                if (action == "read") return "Data block snapshot services inspected; no changes.";
                if (exporting) meta["filePath"] = file!.FullName;
                if (!writing) return "Data block snapshot preview; no native call.";
                if (exporting)
                {
                    meta["mayHaveWrittenFiles"] = true;
                    snapshot!.Export(file!, ExportOptions.None);
                    meta["apiCallSuccess"] = true; meta["file"] = NativeFileOutput.Verify(file!); meta["dataComplete"] = false;
                    return "Snapshot values exported by TIA and hashed; content semantics not verified; no project change.";
                }
                meta["mayHaveChanged"] = true;
                EngineeringGroupOperations.Call(valueService!, nativeMethod, Type.EmptyTypes);
                meta["apiCallSuccess"] = true; meta["after"] = EngineeringScalarProperties.Read(db);
                meta["readbackVerified"] = false; meta["readbackScope"] = "Openness exposes no snapshot/actual value readback; only the native call return and block scalars are reported.";
                return "Native " + nativeMethod + " returned; values not independently verified; no save/compile/download.";
            });
        public ResponseMessage UpdatePlcProgram(string softwarePath, bool confirmUpdate = false, bool dryRun = true)
            => RunHmiStepTool("UpdatePlcProgram", meta => {
                bool writing = !dryRun;
                if (writing && !confirmUpdate) throw new ArgumentException("Real program update requires confirmUpdate=true besides dryRun=false.");
                using var access = writing ? AcquireHmiEditAccess() : null;
                var plc = ExactPlcForEngineering(softwarePath, writing);
                var method = plc.GetType().GetMethod("UpdateProgram", Type.EmptyTypes) ?? throw new NotSupportedException("PlcSoftware.UpdateProgram() unavailable.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["softwarePath"] = softwarePath;
                meta["nativeReturnType"] = method.ReturnType.FullName;
                meta["before"] = EngineeringScalarProperties.Read(plc);
                if (!writing) return "PLC program update preview; no native call.";
                meta["mayHaveChanged"] = true;
                var result = method.Invoke(plc, null);
                meta["apiCallSuccess"] = true; meta["nativeResult"] = result == null ? null : EngineeringScalarProperties.Read(result);
                meta["after"] = EngineeringScalarProperties.Read(plc);
                meta["readbackVerified"] = false; meta["readbackScope"] = "UpdateProgram returns void; program content changes are not enumerated by this tool.";
                return "Native PlcSoftware.UpdateProgram returned; no save/compile/download.";
            });
        public ResponseMessage ReadPlcBlockFingerprints(string softwarePath, string targetIpAddress, string pgPcInterface = "", string password = "", int offset = 0, int limit = 100, bool dryRun = true)
            => RunHmiStepTool("ReadPlcBlockFingerprints", meta => {
                if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset>=0, limit 1..500 required.");
                var plc = ExactPlcForEngineering(softwarePath, false);
                var provider = ResolvePlcService<FingerprintDataProvider>(softwarePath, plc) ?? throw new NotSupportedException("FingerprintDataProvider unavailable for this PLC/version.");
                var configuration = provider.Configuration ?? throw new PortalException(PortalErrorCode.InvalidState, "No connection configuration; configure the CPU network interface first.");
                var candidates = new List<PlcBlockServicesLogic.RouteCandidate>();
                foreach (var mode in EnumerateReflectedProperty(configuration, "Modes"))
                    foreach (var pcInterface in EnumerateReflectedProperty(mode, "PcInterfaces"))
                    {
                        foreach (var target in EnumerateReflectedProperty(pcInterface, "TargetInterfaces"))
                            foreach (var address in EnumerateReflectedProperty(target, "Addresses"))
                                if (address is ConfigurationAddress native)
                                    candidates.Add(new PlcBlockServicesLogic.RouteCandidate { ModeName = ReadReflectedString(mode, "Name"), PcInterfaceName = ReadReflectedString(pcInterface, "Name"), TargetName = ReadReflectedString(target, "Name"), Address = native.Address ?? "", NativeAddress = native });
                        // 2.7.49: the CPU's configured IP is listed under the PC interface's subnets / gateways (the target interface stays empty until the adapter sees the CPU)
                        foreach (var subnet in EnumerateReflectedProperty(pcInterface, "Subnets"))
                        {
                            foreach (var address in EnumerateReflectedProperty(subnet, "Addresses"))
                                if (address is ConfigurationAddress native)
                                    candidates.Add(new PlcBlockServicesLogic.RouteCandidate { ModeName = ReadReflectedString(mode, "Name"), PcInterfaceName = ReadReflectedString(pcInterface, "Name"), TargetName = "subnet " + ReadReflectedString(subnet, "Name"), Address = native.Address ?? "", NativeAddress = native });
                            foreach (var gateway in EnumerateReflectedProperty(subnet, "Gateways"))
                                foreach (var address in EnumerateReflectedProperty(gateway, "Addresses"))
                                    if (address is ConfigurationAddress native)
                                        candidates.Add(new PlcBlockServicesLogic.RouteCandidate { ModeName = ReadReflectedString(mode, "Name"), PcInterfaceName = ReadReflectedString(pcInterface, "Name"), TargetName = "gateway " + ReadReflectedString(gateway, "Name"), Address = native.Address ?? "", NativeAddress = native });
                        }
                    }
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["contactedPlc"] = false; meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                meta["candidateRoutes"] = new JsonArray(candidates.Take(100).Select(c => (JsonNode)JsonValue.Create(c.Describe())!).ToArray());
                var route = PlcBlockServicesLogic.SelectFingerprintRoute(candidates, targetIpAddress, pgPcInterface);
                meta["selectedRoute"] = route.Describe();
                if (dryRun) return "Fingerprint read preview: route resolved, PLC not contacted.";
                using var secure = string.IsNullOrEmpty(password) ? null : PlcBlockServicesLogic.ToSecureString(password);
                OnlineConfigurationDelegate handler = cfg =>
                {
                    if (secure != null && cfg is OnlinePasswordConfiguration pwd) pwd.SetPassword(secure);
                    else if (cfg is TlsVerificationConfiguration tls)   // 2.7.52: FW >= 2.9 CPUs ask for certificate trust before any online read
                    {
                        var before = tls.CurrentSelection.ToString();
                        if (BaseLeftoversLogic.TlsSelectionToApply(true, before) != null) tls.CurrentSelection = TlsVerificationConfigurationSelection.Trusted;
                        meta["tlsVerification"] = new JsonObject { ["plcName"] = tls.PlcName, ["verificationInfo"] = tls.VerificationInfo, ["selectionBefore"] = before, ["selectionAfter"] = tls.CurrentSelection.ToString() };
                    }
                };
                meta["contactedPlc"] = true;
                var result = provider.GetFingerprintData((ConfigurationAddress)route.NativeAddress!, handler) ?? throw new InvalidOperationException("GetFingerprintData returned no result.");
                meta["apiCallSuccess"] = true;
                var items = EngineeringGroupOperations.Items(result.FingerprintDataItems).Cast<FingerprintDataItem>().ToArray();
                var window = PlcBlockServicesLogic.Paginate(meta, items.Length, offset, limit);
                meta["records"] = new JsonArray(items.Skip(window.Skip).Take(window.Take).Select(i => (JsonNode)new JsonObject { ["identifier"] = i.FingerprintDataIdentifier, ["value"] = i.FingerprintDataValue }).ToArray());
                return "Fingerprint data read from the PLC via the selected route; no project or PLC change.";
            });
        public ResponseMessage ImportPlcAlarmInstanceTexts(string softwarePath, string filePath, string culturesJson, bool dryRun = true)
            => RunHmiStepTool("ImportPlcAlarmInstanceTexts", meta => {
                var file = new FileInfo(filePath); if (!Path.IsPathRooted(filePath) || !file.Exists) throw new FileNotFoundException("Absolute existing xlsx file required.");
                var cultures = PlcBlockServicesLogic.ParseCultureNames(culturesJson);
                using var access = dryRun ? null : AcquireHmiEditAccess();
                var plc = ExactPlcForEngineering(softwarePath, !dryRun);
                var provider = plc.GetService<PlcAlarmTextProvider>() ?? throw new NotSupportedException("PlcAlarmTextProvider unavailable for this PLC/version.");
                var signature = new[] { typeof(FileInfo), typeof(IEnumerable<Language>) };
                if (provider.GetType().GetMethod("ImportInstanceTextsFromXlsx", signature) == null) throw new NotSupportedException("Native ImportInstanceTextsFromXlsx(FileInfo, IEnumerable<Language>) unavailable.");
                var settings = _project!.LanguageSettings;
                var languages = cultures.Select(c => settings.Languages.Find(c) ?? throw new NotSupportedException("Language not supported by this project: " + c.Name)).ToArray();
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["filePath"] = file.FullName;
                meta["cultures"] = new JsonArray(languages.Select(l => (JsonNode)new JsonObject { ["culture"] = l.Culture.Name, ["active"] = settings.ActiveLanguages.Any(a => a.Culture.Name == l.Culture.Name) }).ToArray());
                if (dryRun) return "Alarm instance text import preview; file contents not applied.";
                meta["mayHaveChanged"] = true;
                var result = (PlcAlarmTextXlsxResult)EngineeringGroupOperations.Call(provider, "ImportInstanceTextsFromXlsx", signature, file, languages);
                meta["apiCallSuccess"] = true; meta["nativeResult"] = EngineeringScalarProperties.Read(result);
                meta["nativeState"] = result.State.ToString(); meta["logFilePath"] = result.LogFilePath?.FullName;
                meta["operationSuccess"] = result.State.ToString() != "Error"; meta["dataComplete"] = false;
                return "Native alarm instance text import returned; inspect nativeState/logFilePath. Text changes require separate export/readback; no save/compile/download.";
            });
        public ResponseMessage ManagePlcAlarmTextList(string softwarePath, string action = "read", string name = "", string libraryName = "", string masterCopyPath = "", string copyMode = "", bool confirmDelete = false, int offset = 0, int limit = 100, bool dryRun = true)
            => RunHmiStepTool("ManagePlcAlarmTextList", meta => {
                bool writing = PlcBlockServicesLogic.ValidateTextListRequest(action, name, libraryName, masterCopyPath, confirmDelete, dryRun);
                using var access = writing ? AcquireHmiEditAccess() : null;
                var plc = ExactPlcForEngineering(softwarePath, writing);
                var group = plc.PlcAlarmTextlistGroup ?? throw new NotSupportedException("PlcAlarmTextlistGroup unavailable on this PLC.");
                JsonObject Row(PlcAlarmTextlist list, string kind) { var row = EngineeringScalarProperties.Read(list); row["kind"] = kind; return row; }
                var system = EngineeringGroupOperations.Items(group.PlcAlarmSystemTextlists).Cast<PlcAlarmTextlist>().Select(l => (l, "system"));
                var user = EngineeringGroupOperations.Items(group.PlcAlarmUserTextlists).Cast<PlcAlarmTextlist>().Select(l => (l, "user"));
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["action"] = action;
                if (action == "read")
                {
                    var all = system.Concat(user).ToArray();
                    if (!string.IsNullOrEmpty(name)) { all = all.Where(x => string.Equals(x.Item1.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray(); if (all.Length == 0) throw new PortalException(PortalErrorCode.NotFound, "Exact text list not found: " + name); if (all.Length > 1) throw new InvalidOperationException("Ambiguous text list name: " + name); }
                    var window = PlcBlockServicesLogic.Paginate(meta, all.Length, offset, limit);
                    meta["records"] = new JsonArray(all.Skip(window.Skip).Take(window.Take).Select(x => (JsonNode)Row(x.Item1, x.Item2)).ToArray());
                    meta["apiCallSuccess"] = true; meta["scope"] = "Text list scalar properties (Name, ID, ListRange); entries are not exposed by the Openness API.";
                    return "PLC alarm text lists read; no changes.";
                }
                if (action == "delete")
                {
                    if (system.Any(x => string.Equals(x.Item1.Name, name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("System text lists cannot be deleted.");
                    var target = (PlcAlarmUserTextlist)(EngineeringGroupOperations.Find(group.PlcAlarmUserTextlists, name) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact user text list not found: " + name));
                    meta["before"] = Row(target, "user");
                    if (!writing) return "Text list deletion preview; no changes.";
                    meta["mayHaveChanged"] = true;
                    target.Delete(); meta["apiCallSuccess"] = true;
                    if (EngineeringGroupOperations.Find(group.PlcAlarmUserTextlists, name) != null) throw new InvalidOperationException("Text list remains after Delete.");
                    meta["verifiedAbsent"] = true;
                    return "User text list deleted and absence verified; no save/compile/download.";
                }
                var source = ExactMasterCopy(libraryName, masterCopyPath);
                meta["masterCopy"] = EngineeringScalarProperties.Read(source);
                MasterCopyMode? mode = string.IsNullOrEmpty(copyMode) ? null : (MasterCopyMode)EngineeringScalarProperties.ConvertValue(JsonValue.Create(copyMode), typeof(MasterCopyMode))!;
                meta["copyMode"] = mode?.ToString();
                if (mode == null && EngineeringGroupOperations.Find(group.PlcAlarmUserTextlists, source.Name) != null) throw new InvalidOperationException("A user text list with the master copy name already exists; pass copyMode Rename/Replace explicitly.");
                if (!writing) return "Text list creation preview; no changes.";
                meta["mayHaveChanged"] = true;
                var created = mode == null ? group.PlcAlarmUserTextlists.CreateFrom(source) : group.PlcAlarmUserTextlists.CreateFrom(source, mode.Value);
                meta["apiCallSuccess"] = true;
                if (created == null || EngineeringGroupOperations.Find(group.PlcAlarmUserTextlists, created.Name) == null) throw new InvalidOperationException("Created text list not found by readback.");
                meta["after"] = Row(created, "user");
                return "User text list created from master copy and verified by readback; no save/compile/download.";
            });
    }
}
