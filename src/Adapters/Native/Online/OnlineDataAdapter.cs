using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Online;
#if ONLINE_FINGERPRINTS
using Siemens.Engineering.Online.Configurations;
#endif
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
#if ONLINE_BLOCK_INTERFACE
using Siemens.Engineering.SW.Blocks.Interface;
#endif
#if ONLINE_FINGERPRINTS
using Siemens.Engineering.FingerprintData;
#endif
#if ONLINE_LOADABLE
using Siemens.Engineering.SW.Loader;
#endif
#if PLC_UNITS
using Siemens.Engineering.SW.Units;
#endif
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.Siemens;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Plc;

namespace TiaMcp.Adapters.Native.Online;

internal sealed partial class OnlineDownloadAdapter
{
    private OnlineRequest _request = new OnlineRequest();
    internal void Bind(OnlineRequest request) => _request = request;
#if ONLINE_DOWNLOAD
        private static object RequireEngineeringService(object owner, string typeName)
        {
            // Type lookup by name so a member absent from V20 compiles on both targets and degrades to NotSupported.
            var type = typeof(DataBlock).Assembly.GetType(typeName) ?? throw new NotSupportedException("Native service type unavailable in this Openness version: " + typeName);
            if (owner is not IEngineeringServiceProvider) throw new NotSupportedException("Selected object is not a service provider.");
            try { return typeof(IEngineeringServiceProvider).GetMethod("GetService")!.MakeGenericMethod(type).Invoke(owner, null) ?? throw new NotSupportedException("Service unavailable on selected object: " + typeName); }
            catch (System.Reflection.TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw(); throw; }
        }
#endif

#if ONLINE_DOWNLOAD
        public HmiMessage ManagePlcDataBlockSnapshot(string softwarePath, string blockPath, string action, string filePath = "", bool confirmValueChange = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManagePlcDataBlockSnapshot", meta => {
                bool writing = OnlineDataPolicy.ValidateSnapshotRequest(action, filePath, confirmValueChange, dryRun);
                bool exporting = action == "exportSnapshot";
                var file = exporting ? PlcSourceFileOutput.Plan(filePath) : null;
                using var access = writing && !exporting ? _session.AcquireHmiEditAccess() : null;
                // Snapshot/load semantics need the PLC already online in TIA, so Offline is not demanded here; this tool never changes the online state.
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                var db = _session.ExactMasterCopyPlcSource(softwarePath, blockPath, true) as DataBlock ?? throw new ArgumentException("blockPath must identify a data block.");
#if ONLINE_BLOCK_INTERFACE
                object iface = OnlinePrimitives.Interface(db) ?? throw new NotSupportedException("Data block exposes no interface.");
#else
                object? iface = null;
#endif
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false; meta["blockPath"] = blockPath; meta["action"] = action;
                meta["onlineState"] = OnlinePrimitives.StateOrNull(_session.ResolvePlcService<OnlineProvider>(softwarePath, plc))?.ToString();
                meta["before"] = HmiScalar.Read(db);
                const string valueServiceName = "Siemens.Engineering.SW.Blocks.Interface.ValueService";
                meta["valueServiceTypeAvailable"] = typeof(DataBlock).Assembly.GetType(valueServiceName) != null;
                object? valueService = null; InterfaceSnapshot? snapshot = null;
                if (!exporting && action != "read") valueService = RequireEngineeringService(iface, valueServiceName);
                else { try { valueService = RequireEngineeringService(iface, valueServiceName); } catch (NotSupportedException ex) { meta["valueServiceUnavailable"] = ex.Message; } }
                if (exporting || action == "read")
                {
                    var attempts = new HmiArray();
                    foreach (var (source, owner) in new (string, object?)[] { ("DataBlock.Interface", iface), ("DataBlock", db), ("ValueService", valueService) })
                    {
                        if (owner == null || snapshot != null) continue;
                        try { snapshot = OnlinePrimitives.SnapshotOrNull(owner as IEngineeringServiceProvider); if (snapshot != null) meta["snapshotServiceSource"] = source; }
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
                    OnlinePrimitives.Export(snapshot!, file!, ExportOptions.None);
                    meta["apiCallSuccess"] = true; meta["file"] = PlcSourceFileOutput.Verify(file!); meta["dataComplete"] = false;
                    return "Snapshot values exported by TIA and hashed; content semantics not verified; no project change.";
                }
                meta["mayHaveChanged"] = true;
                PlcGroupOperations.Call(valueService!, nativeMethod, Type.EmptyTypes);
                meta["apiCallSuccess"] = true; meta["after"] = HmiScalar.Read(db);
                meta["readbackVerified"] = false; meta["readbackScope"] = "Openness exposes no snapshot/actual value readback; only the native call return and block scalars are reported.";
                return "Native " + nativeMethod + " returned; values not independently verified; no save/compile/download.";
            });
#endif

#if ONLINE_FINGERPRINTS
        public HmiMessage ReadPlcBlockFingerprints(string softwarePath, string targetIpAddress, string pgPcInterface = "", string password = "", int offset = 0, int limit = 100, bool dryRun = true)
            => _session.RunHmiStepTool("GetPlcBlockFingerprints", meta => {
                if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset>=0, limit 1..500 required.");
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                var provider = _session.ResolvePlcService<FingerprintDataProvider>(softwarePath, plc) ?? throw new NotSupportedException("FingerprintDataProvider unavailable for this PLC/version.");
                var configuration = OnlinePrimitives.Configuration(provider) ?? throw new PortalException(PortalErrorCode.InvalidState, "No connection configuration; configure the CPU network interface first.");
                var candidates = new List<OnlineDataPolicy.RouteCandidate>();
                foreach (var mode in _session.EnumerateReflectedProperty(configuration, "Modes"))
                    foreach (var pcInterface in _session.EnumerateReflectedProperty(mode, "PcInterfaces"))
                    {
                        foreach (var target in _session.EnumerateReflectedProperty(pcInterface, "TargetInterfaces"))
                            foreach (var address in _session.EnumerateReflectedProperty(target, "Addresses"))
                                if (address is ConfigurationAddress native)
                                    candidates.Add(new OnlineDataPolicy.RouteCandidate { ModeName = _session.ReadReflectedString(mode, "Name"), PcInterfaceName = _session.ReadReflectedString(pcInterface, "Name"), TargetName = _session.ReadReflectedString(target, "Name"), Address = OnlinePrimitives.Address(native) ?? "", NativeAddress = native });
                        // The CPU's configured IP is listed under the PC interface's subnets / gateways (the target interface stays empty until the adapter sees the CPU)
                        foreach (var subnet in _session.EnumerateReflectedProperty(pcInterface, "Subnets"))
                        {
                            foreach (var address in _session.EnumerateReflectedProperty(subnet, "Addresses"))
                                if (address is ConfigurationAddress native)
                                    candidates.Add(new OnlineDataPolicy.RouteCandidate { ModeName = _session.ReadReflectedString(mode, "Name"), PcInterfaceName = _session.ReadReflectedString(pcInterface, "Name"), TargetName = "subnet " + _session.ReadReflectedString(subnet, "Name"), Address = OnlinePrimitives.Address(native) ?? "", NativeAddress = native });
                            foreach (var gateway in _session.EnumerateReflectedProperty(subnet, "Gateways"))
                                foreach (var address in _session.EnumerateReflectedProperty(gateway, "Addresses"))
                                    if (address is ConfigurationAddress native)
                                        candidates.Add(new OnlineDataPolicy.RouteCandidate { ModeName = _session.ReadReflectedString(mode, "Name"), PcInterfaceName = _session.ReadReflectedString(pcInterface, "Name"), TargetName = "gateway " + _session.ReadReflectedString(gateway, "Name"), Address = OnlinePrimitives.Address(native) ?? "", NativeAddress = native });
                        }
                    }
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["contactedPlc"] = false; meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                meta["candidateRoutes"] = new HmiArray(candidates.Take(100).Select(c => (object)HmiScalar.Json(c.Describe())!).ToArray());
                var route = OnlineDataPolicy.SelectFingerprintRoute(candidates, targetIpAddress, pgPcInterface);
                meta["selectedRoute"] = route.Describe();
                if (dryRun) return "Fingerprint read preview: route resolved, PLC not contacted.";
                using var secure = string.IsNullOrEmpty(password) ? null : OnlineDataPolicy.ToSecureString(password);
                OnlineConfigurationDelegate handler = OnlinePrimitives.Handler(cfg =>
                {
                    if (secure != null && cfg is OnlinePasswordConfiguration pwd) OnlinePrimitives.Password(pwd, secure);
#if ONLINE_TLS
                    else if (cfg is TlsVerificationConfiguration tls)   // FW >= 2.9 CPUs ask for certificate trust before any online read
                    {
                        var before = OnlinePrimitives.Selection(tls).ToString();
                        if (TiaMcp.Adapters.Contracts.EngineeringCredentialRules.TlsSelectionToApply(true, before) != null) OnlinePrimitives.Selection(tls, TlsVerificationConfigurationSelection.Trusted);
                        meta["tlsVerification"] = new HmiObject { ["plcName"] = OnlinePrimitives.PlcName(tls), ["verificationInfo"] = OnlinePrimitives.VerificationInfo(tls), ["selectionBefore"] = before, ["selectionAfter"] = OnlinePrimitives.Selection(tls).ToString() };
                    }
#endif
                });
                meta["contactedPlc"] = true;
                var result = OnlinePrimitives.Fingerprints(provider, (ConfigurationAddress)route.NativeAddress!, handler) ?? throw new InvalidOperationException("GetFingerprintData returned no result.");
                meta["apiCallSuccess"] = true;
                var items = PlcGroupOperations.Items(OnlinePrimitives.Items(result)).Cast<FingerprintDataItem>().ToArray();
                var window = OnlineDataPolicy.Paginate(meta, items.Length, offset, limit);
                meta["records"] = new HmiArray(items.Skip(window.Skip).Take(window.Take).Select(i => (object)new HmiObject { ["identifier"] = OnlinePrimitives.Identifier(i), ["value"] = OnlinePrimitives.Value(i) }).ToArray());
                return "Fingerprint data read from the PLC via the selected route; no project or PLC change.";
            });
#endif

#if ONLINE_LOADABLE
        public HmiMessage GeneratePlcLoadableFile(string softwarePath, string objectPathsJson, string objectKind, string targetOption, string filePath, bool dryRun = true)
            => _session.RunHmiStepTool("GeneratePlcLoadableFile", meta => {
                if (objectKind == "block" || objectKind == "unit") objectKind += "s";
                if (objectKind != "blocks" && objectKind != "units") throw new ArgumentException("objectKind must be blocks/units.");
                var names = _request.ObjectPaths; var file = PlcSourceFileOutput.Plan(filePath);
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                if (!Enum.GetNames(typeof(TargetOption)).Contains(targetOption)) throw new ArgumentException("targetOption must be one of: " + string.Join("/", Enum.GetNames(typeof(TargetOption))) + " (Siemens.Engineering.SW.Loader.TargetOption).");
                var option = (TargetOption)Enum.Parse(typeof(TargetOption), targetOption)!;
                var service = plc.GetService<LoadableProvider>() ?? throw new NotSupportedException("LoadableProvider unavailable.");
                var blocks = objectKind == "blocks" ? names.Select(n => (PlcBlock)_session.ExactMasterCopyPlcSource(softwarePath, n, true)).ToArray() : Array.Empty<PlcBlock>();
#if PLC_UNITS
                var unitRoot = objectKind == "units" ? plc.GetService<PlcUnitProvider>()?.UnitGroup.Units : null;
                var units = objectKind == "units" ? names.Select(n => unitRoot?.Find(n) ?? throw new InvalidOperationException("Exact software unit not found: " + n)).ToArray() : Array.Empty<PlcUnit>();
#endif
                meta["dryRun"] = dryRun; meta["mayHaveWrittenFiles"] = false; meta["targetOption"] = targetOption; meta["objectPaths"] = new HmiArray(_request.ObjectPaths.Cast<object?>().ToArray());
                if (!dryRun)
                {
                    meta["mayHaveWrittenFiles"] = true;
                    if (objectKind == "blocks") service.GenerateLoadable(file, blocks, option);
#if PLC_UNITS
                    else service.GenerateLoadable(file, units, option);
#else
                    else throw new NotSupportedException("Software-unit loadable generation requires the V18 or later PLC unit overload.");
#endif
                    meta["file"] = PlcSourceFileOutput.Verify(file); meta["apiCallSuccess"] = true;
                }
                return dryRun ? "Loadable generation preview; no PLC contacted." : "Loadable file generated and hashed. Not downloaded; deployment/readiness not asserted.";
            });
#endif
}
