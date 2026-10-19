using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net;
using System.Reflection;
using System.Threading;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Plc;
using TiaMcpServer.Siemens;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Online;
#if ONLINE_LEGITIMATION
using Siemens.Engineering.Online.Configurations;
#endif
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
#if ONLINE_DOWNLOAD
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Upload;
using Siemens.Engineering.Upload.Configurations;
#endif
namespace TiaMcp.Adapters.Native.Online
{
    // Historical native observations lack a recorded PLCSIM version/date; see docs/reference/real-machine-ledger.md.
    internal sealed partial class OnlineDownloadAdapter
    {
        private readonly OnlineSession _session;

        public OnlineDownloadAdapter(OnlineSession session) => _session = session;

        public OnlineStateResult GetOnlineState(string softwarePath)
        {
            // 未连接工程或路径无效时，在线状态未经测量，不能报告为 Offline。
            if (_session.IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "GetOnlineState: no project is open, so the online state was NOT measured. "
                    + "Call ConnectPortal + OpenProject (or AttachOpenProject) first.");
            }

            var plcSoftware = _session.GetPlcSoftware(softwarePath);
            if (plcSoftware == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"GetOnlineState: PLC software not found at '{softwarePath}', so the online state was NOT measured."
                    + _session.AvailablePlcPathsSuffix());
            }

            try
            {
                var provider = _session.ResolvePlcService<OnlineProvider>(softwarePath, plcSoftware);
                if (provider == null)
                {
                    // 缺少在线服务时状态未知，不能报告为已确认离线。
                    return new OnlineStateResult
                    {
                        State = "Unknown",
                        IsOnline = false,
                        IsReachable = false,
                        Message = "OnlineProvider service not available on this PLC, so the online state was NOT measured. "
                                + "State 'Unknown' means undetermined — it does NOT mean the CPU is offline."
                    };
                }

                var state = provider.State;
                var stateName = state.ToString();
                bool isOnline = stateName == "Online";
                bool isReachable = isOnline || stateName == "Protected";

                return new OnlineStateResult
                {
                    State = stateName,
                    IsOnline = isOnline,
                    IsReachable = isReachable,
                    Message = BuildOnlineStateMessage(stateName, softwarePath)
                };
            }
            catch (Exception ex)
            {
                _session.Log.Error(ex, "GetOnlineState failed for {SoftwarePath}", softwarePath);
                return new OnlineStateResult { State = "Unknown", IsOnline = false, IsReachable = false, Message = $"Error: {ex.Message}" };
            }
        }

        public OnlineStateResult GoOnline(string softwarePath, string? ipAddress = null, string? password = null) => GoOnline(softwarePath, ipAddress, password, null, null, "", null);

        public OnlineStateResult GoOnline(string softwarePath, string? ipAddress, string? password, string? userName, string? userType, string rhTarget) => GoOnline(softwarePath, ipAddress, password, userName, userType, rhTarget, null);

        public OnlineStateResult GoOnline(string softwarePath, string? ipAddress, string? password, string? userName, string? userType, string rhTarget, string? pgPcInterface) => GoOnline(softwarePath, ipAddress, password, userName, userType, rhTarget, pgPcInterface, false, false);

        // userName/userType answer OnlineAuthenticationConfiguration (UMAC-protected PLCs); rhTarget primary|backup
        // goes online through RHOnlineProvider.GoOnlineToPrimary/Backup on R/H systems.
        // Historical native observation: OnlineProvider.GoOnline() uses whatever route TIA last applied - on a fresh
        // project nothing is applied and TIA answers "The connection cannot be established". The route is now selected like a
        // download (pgPcInterface + ipAddress -> ConnectionConfiguration.ApplyConfiguration) and an explicit address goes through
        // the official GoOnline(ConfigurationAddress) overload (V21; V20 applies the address and calls GoOnline()). Without a route
        // the official sample's ConnectionConfiguration.IsConfigured check comes first, and the state is polled while Connecting.
        // trustDeviceCertificate answers the TLS prompt of FW >= 2.9 CPUs (meta.tlsVerification records the decision and the
        // certificates the device presented); allowNonTlsLogin lets a password go over a connection that is not TLS-secured.
        public OnlineStateResult GoOnline(string softwarePath, string? ipAddress, string? password, string? userName, string? userType, string rhTarget, string? pgPcInterface, bool trustDeviceCertificate, bool allowNonTlsLogin = false)
        {
            TiaMcp.Adapters.Contracts.EngineeringCredentialRules.ValidateOnlineCredentials(userName ?? "", password ?? "", userType ?? "");
            rhTarget = TiaMcp.Adapters.Contracts.EngineeringCredentialRules.ValidateRhTarget(rhTarget);
            var meta = new HmiObject { ["trustDeviceCertificate"] = trustDeviceCertificate };
            // 未连接工程或路径无效时，在线状态未经测量，不能报告为 Offline。
            if (_session.IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "ConnectOnlinePlc: no project is open, so the online state was NOT measured. "
                    + "Call ConnectPortal + OpenProject (or AttachOpenProject) first.");
            }

            // An online transition acts on the exact PLC in any connection state and on R/H systems, so it never takes the
            // offline-export precondition (an online PLC can be connected again or taken offline).
            var plcSoftware = _session.ResolvePlc(softwarePath, false);
            if (plcSoftware == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"ConnectOnlinePlc: PLC software not found at '{softwarePath}', so the online state was NOT measured."
                    + _session.AvailablePlcPathsSuffix());
            }

            try
            {
#if ONLINE_RH
                if (rhTarget.Length > 0)
                {
                    RHOnlineProvider rh = _session.ResolvePlcService<RHOnlineProvider>(softwarePath, plcSoftware)
                        ?? throw new PortalException(PortalErrorCode.NotFound, "RHOnlineProvider service not available on this PLC (rhTarget applies to R/H systems only).");
                    using var rhScope = AttachOnlineLegitimationHandler(rh.Configuration, password, userName, userType, meta, trustDeviceCertificate, allowNonTlsLogin);
                    // ConfigurationAddress has no public constructor: the address object comes from the route tree (target interfaces).
                    ConfigurationAddress? rhAddress = string.IsNullOrWhiteSpace(ipAddress) ? null : FindConfigurationAddress(rh.Configuration, ipAddress!);
                    if (!string.IsNullOrWhiteSpace(ipAddress) && rhAddress == null) _session.Log.Warning("ConnectOnlinePlc R/H: no ConfigurationAddress {Ip} in the route tree; using the configured address", ipAddress);
#if !ONLINE_ADDRESS_OVERLOAD
                    if (rhAddress != null) _session.Log.Warning("ConnectOnlinePlc R/H: this release's RHOnlineProvider has no address overload; using the configured address");
                    OnlineState rhState = rhTarget == "primary" ? rh.GoOnlineToPrimary() : rh.GoOnlineToBackup();
#else
                    OnlineState rhState = rhAddress == null
                        ? (rhTarget == "primary" ? rh.GoOnlineToPrimary() : rh.GoOnlineToBackup())
                        : (rhTarget == "primary" ? rh.GoOnlineToPrimary(rhAddress) : rh.GoOnlineToBackup(rhAddress));
#endif
                    var rhName = OnlineTransitionPolicy.WaitWhileConnecting(rhState.ToString(),
                        () => (rhTarget == "primary" ? rh.PrimaryState : rh.BackupState).ToString(), Thread.Sleep);
                    // envelope: legacy-single-verdict
                    meta["success"] = rhName == "Online";
                    return new OnlineStateResult { State = rhName, IsOnline = rhName == "Online", IsReachable = rhName == "Online" || rhName == "Protected",
                        Message = BuildOnlineStateMessage(rhName, softwarePath) + $" (R/H {rhTarget}; primary={rh.PrimaryState}, backup={rh.BackupState})", Meta = meta };
                }
#endif
                var provider = _session.ResolvePlcService<OnlineProvider>(softwarePath, plcSoftware);
                if (provider == null)
                {
                    // 缺少在线服务时状态未知，不能报告为已确认离线。
                    return new OnlineStateResult
                    {
                        State = "Unknown",
                        IsOnline = false,
                        IsReachable = false,
                        Message = "OnlineProvider service not available on this PLC, so the online state was NOT measured. "
                                + "State 'Unknown' means undetermined — it does NOT mean the CPU is offline."
                    };
                }

                using var legitimationScope = AttachOnlineLegitimationHandler(provider.Configuration, password, userName, userType, meta, trustDeviceCertificate, allowNonTlsLogin);

                string routeNote = "";
                ConfigurationAddress? address = null;
                bool routeRequested = !string.IsNullOrWhiteSpace(ipAddress) || !string.IsNullOrWhiteSpace(pgPcInterface);
                string before = provider.State.ToString();
                meta["onlineStateBefore"] = before;
                if (before == "Online")
                {
                    // ApplyConfiguration throws while the device is online, so an established connection is kept as it is.
                    meta["success"] = true;
                    return new OnlineStateResult { State = before, IsOnline = true, IsReachable = true, Meta = meta,
                        Message = BuildOnlineStateMessage(before, softwarePath) + (routeRequested
                            ? " It was already online, so the requested route was not applied (ApplyConfiguration throws while the device is online); call DisconnectOnlinePlc first to change the route."
                            : " It was already online.") };
                }
                bool isConfigured = provider.Configuration.IsConfigured;
                meta["isConfigured"] = isConfigured;
                var notConfigured = OnlineTransitionPolicy.ConnectRefusal(routeRequested, isConfigured);
                if (notConfigured != null)
                {
                    meta["success"] = false;
                    return new OnlineStateResult { State = before, IsOnline = false, IsReachable = false, Message = "ConnectOnlinePlc not attempted: " + notConfigured, Meta = meta };
                }
                if (routeRequested)
                {
                    var selection = SelectDownloadRoute(provider.Configuration, pgPcInterface, ipAddress);
                    if (selection.Error != null)
                    {
                        meta["success"] = false;
                        return new OnlineStateResult { State = "NotReachable", IsOnline = false, IsReachable = false, Message = "ConnectOnlinePlc not attempted: " + selection.Error, Meta = meta };
                    }
                    address = selection.Address;
                    routeNote = " Route: " + selection.Description + ".";
                    meta["route"] = selection.Description;
                    meta["routeApplied"] = selection.Applied;
                }

                OnlineState resultState;
#if !ONLINE_ADDRESS_OVERLOAD
                // V20 has GoOnline() only; the address (when any) was applied through ApplyConfiguration by the route selection.
                resultState = provider.GoOnline();
#else
                resultState = address != null ? provider.GoOnline(address) : provider.GoOnline();
#endif

                // GoOnline returns before the connection is established; poll while it reads Connecting.
                var stateName = OnlineTransitionPolicy.WaitWhileConnecting(resultState.ToString(), () => provider.State.ToString(), Thread.Sleep);
                bool isOnline = stateName == "Online";
                meta["success"] = isOnline;
                return new OnlineStateResult
                {
                    State = stateName,
                    IsOnline = isOnline,
                    IsReachable = isOnline || stateName == "Protected",
                    Message = BuildOnlineStateMessage(stateName, softwarePath) + routeNote,
                    Meta = meta
                };
            }
            catch (Exception ex)
            {
                _session.Log.Error(ex, "GoOnline failed for {SoftwarePath}", softwarePath);
                // Historical native observation: EngineeringTargetInvocationException carries only "Error when calling method 'GoOnline'" - the
                // reason ("Incompatible" against a never-downloaded PLCSIM instance) sits in the inner chain; surface it and the state TIA holds.
                var chain = new List<string>();
                for (var e = ex; e != null && chain.Count < 6; e = e.InnerException) if (!string.IsNullOrWhiteSpace(e.Message) && !chain.Contains(e.Message)) chain.Add(e.Message);
                string stateNow = "";
                try { stateNow = _session.ResolvePlcService<OnlineProvider>(softwarePath, plcSoftware)?.State.ToString() ?? ""; } catch (Exception) { /* swallow(probe-optional): a failed state probe must preserve the original GoOnline failure */ }
                meta["success"] = false; meta["error"] = string.Join(" <- ", chain);
                if (stateNow.Length > 0) meta["onlineStateAfter"] = stateNow;
                var hint = stateNow == "Incompatible" ? " TIA reports the connection as Incompatible (device / firmware / program mismatch): download first (DownloadPlc), then go online." : "";
                return new OnlineStateResult { State = stateNow.Length > 0 && stateNow != "Online" ? stateNow : "NotReachable", IsOnline = false, IsReachable = stateNow == "Incompatible" || stateNow == "Protected", Message = $"GoOnline failed: {string.Join(" <- ", chain)}{hint}", Meta = meta };
            }
        }

        // Typed walk of Modes -> PcInterfaces -> TargetInterfaces -> Addresses for an exact IP (R/H online targets).
        private ConfigurationAddress? FindConfigurationAddress(ConnectionConfiguration configuration, string ipAddress)
        {
            foreach (ConfigurationMode mode in PlcGroupOperations.Items(configuration.Modes).Cast<ConfigurationMode>())
                foreach (ConfigurationPcInterface pcInterface in PlcGroupOperations.Items(mode.PcInterfaces).Cast<ConfigurationPcInterface>())
                {
                    foreach (ConfigurationTargetInterface target in PlcGroupOperations.Items(pcInterface.TargetInterfaces).Cast<ConfigurationTargetInterface>())
#if ONLINE_TARGET_ADDRESSES
                        foreach (ConfigurationAddress address in PlcGroupOperations.Items(target.Addresses).Cast<ConfigurationAddress>())
#else
                        foreach (ConfigurationAddress address in PlcGroupOperations.Items(OnlineRoutePrimitives.TargetAddresses(target)).Cast<ConfigurationAddress>())
#endif
#if ONLINE_ADDRESSES
                            if (string.Equals(address.Address, ipAddress, StringComparison.OrdinalIgnoreCase)) return address;
#else
                            if (string.Equals(OnlineRoutePrimitives.Address(address), ipAddress, StringComparison.OrdinalIgnoreCase)) return address;
#endif
                    var viaSubnet = FindSubnetOrGatewayAddress(pcInterface, ipAddress, out _);
                    if (viaSubnet != null) return viaSubnet;
                }
            return null;
        }

        public HmiMessage GoOffline(string softwarePath)
        {
            // 未找到工程、PLC 或在线服务时，不能报告下线成功。
            if (_session.IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "DisconnectOnlinePlc: no project is open, so nothing was taken offline. "
                    + "Call ConnectPortal + OpenProject (or AttachOpenProject) first.");
            }

            // GoOffline needs no configuration (Siemens online-and-download skill) and acts on an online PLC by definition, so the
            // offline-export precondition of a write resolution must not run here.
            var plcSoftware = _session.ResolvePlc(softwarePath, false);
            if (plcSoftware == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"DisconnectOnlinePlc: PLC software not found at '{softwarePath}', so nothing was taken offline."
                    + _session.AvailablePlcPathsSuffix());
            }

            try
            {
                var provider = _session.ResolvePlcService<OnlineProvider>(softwarePath, plcSoftware);
                if (provider == null)
                {
                    throw new PortalException(PortalErrorCode.OpennessError,
                        $"DisconnectOnlinePlc: OnlineProvider service is not available on '{softwarePath}', "
                        + "so the offline transition was NOT performed. Any live online session is still open — "
                        + "disconnect it in the TIA Portal UI before compiling or exporting.");
                }

                var before = provider.State.ToString();
                provider.GoOffline();
                // GoOffline returns void: the state read back decides the result, as DisconnectOnlinePlcs does.
                var after = provider.State.ToString();
                var outcome = OnlineTransitionPolicy.GoOfflineOutcome(softwarePath, before, after);
                return new HmiMessage { Message = outcome.Message, Meta = HmiMeta.Basic(outcome.Success, ("before", before), ("after", after)) };
            }
            catch (Exception ex)
            {
                _session.Log.Error(ex, "GoOffline failed for {SoftwarePath}", softwarePath);
                return new HmiMessage { Message = $"GoOffline error: {ex.Message}" };
            }
        }

        // Take EVERY PLC in the project offline, not just one. A UI-initiated online session,
        // or a project with more than one online PLC, will NOT release the compile/export/import
        // lock when GoOffline is called for a single softwarePath. This iterates all PLCs using
        // the same provider resolution as GoOffline, so the agent never has to ask the user to
        // toggle online/offline in the TIA UI. Returns per-PLC before/after state.
        public HmiObject GoOfflineAll()
        {
            var plcs = new HmiArray();
            if (_session.IsProjectNull())
                return new HmiObject { ["message"] = "No project open.", ["allOffline"] = false, ["v4Rejection"] = "PROJECT_NOT_BOUND", ["plcs"] = plcs };

            bool allOffline = true;
            foreach (var plc in _session.GetAllPlcSoftware())
            {
                var entry = new HmiObject { ["name"] = plc.Name };
                try
                {
                    var provider = _session.ResolvePlcService<OnlineProvider>(plc.Name, plc);
                    entry["before"] = provider?.State.ToString() ?? "Unknown";
                    entry["writeAttempted"] = provider != null;
                    provider?.GoOffline();
                    var after = provider?.State.ToString() ?? "Unknown";
                    entry["after"] = after;
                    entry["ok"] = after == "Offline";
                    if (after != "Offline") allOffline = false;
                }
                catch (Exception ex)
                {
                    entry["ok"] = false;
                    entry["error"] = ex.Message;
                    allOffline = false;
                }
                plcs.Add(entry);
            }

            return new HmiObject
            {
                ["message"] = $"DisconnectOnlinePlcs: {plcs.Count} PLC(s) processed; allOffline={allOffline}.",
                ["allOffline"] = allOffline,
                ["plcs"] = plcs
            };
        }

        public OnlineCompareResult CompareSoftwareToOnline(string softwarePath, int maxDepth = 4, int maxEntries = 200)
        {
            // 缺少工程或 PLC 时没有比对结果，不能解释为在线离线一致。
            if (_session.IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "CompareSoftwareToOnline: no project is open, so NO comparison was performed. "
                    + "Do not read this as 'offline and online are identical'. "
                    + "Call ConnectPortal + OpenProject (or AttachOpenProject) first.");
            }

            var plcSoftware = _session.GetPlcSoftware(softwarePath);
            if (plcSoftware == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"CompareSoftwareToOnline: PLC software not found at '{softwarePath}', "
                    + "so NO comparison was performed. Do not read this as 'identical'."
                    + _session.AvailablePlcPathsSuffix());
            }

            try
            {
                // Note: don't gate on OnlineProvider here. The provider service is sometimes
                // unavailable on PlcSoftware even when the PLC is online via the TIA Portal UI
                // (depends on hardware variant / project layout). Let CompareToOnline itself
                // throw if the connection isn't actually live — the exception path below
                // captures that with the original TIA error message.
                var provider = _session.ResolvePlcService<OnlineProvider>(softwarePath, plcSoftware);
                var probeState = provider?.State.ToString() ?? "Unknown";

                // Typed PlcSoftware.CompareToOnline (all eight releases); CompareResultState is read by name because
                // CompareIrrelevant and the folder own-state values exist from V15.1 only.
                var result = plcSoftware.CompareToOnline();
                if (result == null)
                    return new OnlineCompareResult { Message = "CompareToOnline returned null.", IsOnline = true };

                var entries = new List<OnlineCompareEntry>();
                var summary = new Dictionary<string, int>();
                bool truncated = WalkCompareTree(result.RootElement, "", 0, maxDepth, maxEntries, entries, summary);

                return new OnlineCompareResult
                {
                    Message = $"Compare complete: {entries.Count} differences" + (truncated ? " (truncated)." : "."),
                    IsOnline = true,
                    Entries = entries.ToArray(),
                    Summary = summary,
                    Truncated = truncated
                };
            }
            // 比对失败必须抛出原生错误，不能返回空差异或声称在线。
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _session.Log.Error(ex, "CompareSoftwareToOnline failed for {SoftwarePath}", softwarePath);
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"CompareSoftwareToOnline failed for '{softwarePath}': {ex.Message} "
                    + "NO comparison result is available — do not read this as 'offline and online are identical'. "
                    + "Go online first (ConnectOnlinePlc) and retry.", null, ex);
            }
        }

        private static bool WalkCompareTree(global::Siemens.Engineering.Compare.CompareResultElement? element, string path, int depth, int maxDepth, int maxEntries,
            List<OnlineCompareEntry> entries, Dictionary<string, int> summary)
        {
            if (element == null) return false;
            if (entries.Count >= maxEntries) return true;

            string leftName = element.LeftName ?? string.Empty;
            string rightName = element.RightName ?? string.Empty;
            string status = element.ComparisonResult.ToString();
            string? details = element.DetailedInformation;

            string displayName = !string.IsNullOrEmpty(leftName) ? leftName : rightName;
            string fullPath = string.IsNullOrEmpty(path)
                ? displayName
                : (string.IsNullOrEmpty(displayName) ? path : path + "/" + displayName);

            if (summary.ContainsKey(status)) summary[status]++;
            else summary[status] = 1;

            // Only differences are reported.
            // 实测出现过的真差异状态："RightMissing"（只在离线侧，没下载过）、
            // "FolderContentsDifferent"；同族还有 "LeftMissing" / "ObjectsDifferent"。
            // 以 "Identical" 结尾的状态和 CompareIrrelevant 是**一致**，漏掉会把一致报成
            // 「Compare complete: N differences」，诱发一次不必要的下载。
            bool isDifference = OnlineTransitionPolicy.IsCompareDifference(status);
            if (depth > 0 && isDifference)
            {
                entries.Add(new OnlineCompareEntry
                {
                    Path = fullPath,
                    LeftName = leftName,
                    RightName = rightName,
                    Status = status,
                    Details = string.IsNullOrEmpty(details) ? null : details
                });
                if (entries.Count >= maxEntries) return true;
            }

            if (depth >= maxDepth) return false;

            if (element.Elements == null) return false;
            foreach (global::Siemens.Engineering.Compare.CompareResultElement child in element.Elements)
            {
                if (WalkCompareTree(child, fullPath, depth + 1, maxDepth, maxEntries, entries, summary))
                    return true;
            }
            return false;
        }

        private static string BuildOnlineStateMessage(string state, string softwarePath)
        {
            return state switch
            {
                "Online" => $"'{softwarePath}' is online and reachable.",
                "Offline" => $"'{softwarePath}' is offline. Call ConnectOnlinePlc first.",
                "Connecting" => $"'{softwarePath}' is connecting...",
                "Incompatible" => $"'{softwarePath}' online but firmware/config mismatch. Download required.",
                "NotReachable" => $"'{softwarePath}' not reachable. Check IP address and network.",
                "Protected" => $"'{softwarePath}' is password-protected. Authentication required.",
                "Disconnecting" => $"'{softwarePath}' is disconnecting.",
                _ => $"'{softwarePath}' online state: {state}."
            };
        }

        #region download

#if ONLINE_DOWNLOAD
        public OnlineDownloadResult DownloadToPlc(OnlineRequest request)
        {
            string softwarePath = request.SoftwarePath;
            string? password = EmptyToNull(request.Password), pgPcInterface = EmptyToNull(request.PgPcInterface), targetIpAddress = EmptyToNull(request.TargetIpAddress);
            bool trustDeviceCertificate = request.TrustDeviceCertificate;
            var legitimation = new HmiObject { ["trustDeviceCertificate"] = trustDeviceCertificate };   // TLS prompt record
            _session.Log.Information(
                "DownloadPlc: softwarePath={SoftwarePath} consistentOnly={C} keepDB={K} start={S} stop={T} hasPassword={P} pgPc={I} targetIp={A} userMgmt={U} scope={O}",
                softwarePath, request.ConsistentBlocksOnly, request.KeepActualValues, request.StartAfterDownload, request.StopBeforeDownload, !string.IsNullOrEmpty(password), pgPcInterface, targetIpAddress, request.UserManagementMode, request.DownloadScope);
            string rhTarget;
            try { rhTarget = TiaMcp.Adapters.Contracts.EngineeringCredentialRules.ValidateRhTarget(request.RhTarget); }
            catch (ArgumentException ex) { return new OnlineDownloadResult { Ok = false, Message = ex.Message, Errors = new[] { ex.Message } }; }
            if (!OnlineCapabilities.DownloadScopes.Contains(request.DownloadScope))
            {
                const string scopeError = "downloadScope must be software or softwareOnlyChanges.";
                return new OnlineDownloadResult { Ok = false, Message = scopeError, Errors = new[] { scopeError } };
            }

            if (_session.IsProjectNull())
                return new OnlineDownloadResult { Ok = false, Message = "No project open.", Meta = new HmiObject { ["v4Rejection"] = "PROJECT_NOT_BOUND" } };

            DownloadPromptPolicy promptPolicy;
            try
            {
                promptPolicy = BuildDownloadPromptPolicy(request);
            }
            catch (ArgumentException ex)
            {
                return new OnlineDownloadResult { Ok = false, Message = "Invalid download prompt parameters: " + ex.Message, Errors = new[] { ex.Message } };
            }

            // The exact PLC without the standard-target precondition: R/H systems download through RHDownloadProvider, and the
            // download's own offline requirement below names the documented reason.
            var plcSoftware = _session.ResolvePlc(softwarePath, false);
            if (plcSoftware == null)
                return new OnlineDownloadResult { Ok = false, Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix() };
            RequireDownloadOffline(softwarePath, plcSoftware, rhTarget);

            // Declared outside the try so the catch can report which PG/PC route was used.
            DownloadRouteSelection? routeDiagnostics = null;

            try
            {
#if ONLINE_RH
                // R/H systems download to one CPU at a time through RHDownloadProvider; its own Configuration enumerates the routes
                // and raises the legitimation prompts (targets are bound to the provider instance that enumerated them).
                RHDownloadProvider? rh = rhTarget.Length == 0 ? null : _session.ResolvePlcService<RHDownloadProvider>(softwarePath, plcSoftware)
                    ?? throw new PortalException(PortalErrorCode.NotFound, "RHDownloadProvider service not available on this PLC (rhTarget applies to R/H systems only).");
#endif
                DownloadProvider? downloadProvider = rhTarget.Length == 0 ? _session.ResolvePlcService<DownloadProvider>(softwarePath, plcSoftware) : null;
                if (rhTarget.Length == 0 && downloadProvider == null)
                    return new OnlineDownloadResult
                    {
                        Ok = false,
                        Message = "DownloadProvider service not available for this PLC. Ensure hardware configuration has network settings."
                    };

#if ONLINE_RH
                ConnectionConfiguration? configuration = rh != null ? rh.Configuration : downloadProvider!.Configuration;
#else
                ConnectionConfiguration? configuration = downloadProvider!.Configuration;
#endif
                if (configuration == null)
                    return new OnlineDownloadResult
                    {
                        Ok = false,
                        Message = "No connection configuration found. Configure the PLC's PROFINET/IP address in hardware configuration first."
                    };

                using var legitimationScope = AttachOnlineLegitimationHandler(configuration, password, legitimation, trustDeviceCertificate, request.AllowNonTlsLogin);

                DownloadConfigurationDelegate preDelegate = (config) => ApplyDownloadPrompt(config, promptPolicy);
                DownloadConfigurationDelegate postDelegate = (config) => ApplyDownloadPrompt(config, promptPolicy);

                // ConnectionConfiguration does NOT implement IConfiguration; a ConfigurationTargetInterface (Modes -> PcInterfaces ->
                // TargetInterfaces) or a ConfigurationAddress does. Select one (applying its route) and pass THAT to Download().
                routeDiagnostics = SelectDownloadRoute(configuration, pgPcInterface, targetIpAddress);
                if (routeDiagnostics.Error != null)
                    return new OnlineDownloadResult
                    {
                        Ok = false,
                        Message = routeDiagnostics.Error,
                        Errors = new[] { routeDiagnostics.Error }
                    };
                if (routeDiagnostics.Configuration is not IConfiguration downloadConfig)
                {
                    const string noRoute = "No download route: the connection configuration lists no Modes -> PcInterfaces -> TargetInterfaces route (IConfiguration) to download through. "
                        + "Check the PG/PC interface and the CPU's network settings; CheckDownloadReadiness lists the routes.";
                    return new OnlineDownloadResult { Ok = false, Message = noRoute, Errors = new[] { noRoute } };
                }
                legitimation["routeApplied"] = routeDiagnostics.Applied;
                var options = DownloadScopeOption(request.DownloadScope);
                legitimation["downloadOptions"] = options.ToString();
                _session.Log.Information("DownloadPlc: PG/PC route = {Route}", routeDiagnostics.Description);

#if ONLINE_RH
                if (rh != null)
                {
                    DownloadResult rhResult;
#if ONLINE_TARGET_ADDRESSES
                    if (routeDiagnostics.Address != null && routeDiagnostics.AddressSource == "created" && routeDiagnostics.Target is IConfiguration rhTargetConfiguration)
                        rhResult = rhTarget == "primary"
                            ? rh.DownloadToPrimary(rhTargetConfiguration, routeDiagnostics.Address, preDelegate, postDelegate, options)
                            : rh.DownloadToBackup(rhTargetConfiguration, routeDiagnostics.Address, preDelegate, postDelegate, options);
                    else
#endif
                    rhResult = rhTarget == "primary"
                        ? rh.DownloadToPrimary(downloadConfig, preDelegate, postDelegate, options)
                        : rh.DownloadToBackup(downloadConfig, preDelegate, postDelegate, options);
                    var rhResponse = BuildDownloadResponse(rhResult, softwarePath, routeDiagnostics, promptPolicy, legitimation);
                    rhResponse.Message = "[R/H " + rhTarget + "] " + rhResponse.Message;
                    return rhResponse;
                }
#endif

#if ONLINE_TARGET_ADDRESSES
                if (routeDiagnostics.Address != null && routeDiagnostics.AddressSource == "created"
                    && routeDiagnostics.Target is IConfiguration targetConfiguration)
                {
                    // "Download Hardware and Software to a target with specific IP-Address" - the official overload for an
                    // address TIA has not seen on the target yet (PLCSIM Advanced instance before its first download, new CPU).
                    DownloadResult createdResult = downloadProvider!.Download(targetConfiguration, routeDiagnostics.Address, preDelegate, postDelegate, options);
                    return BuildDownloadResponse(createdResult, softwarePath, routeDiagnostics, promptPolicy, legitimation);
                }
#endif

                // Typed Download(IConfiguration, pre, post, DownloadOptions), present on every release with DownloadProvider (V15.1+).
                DownloadResult result = downloadProvider!.Download(downloadConfig, preDelegate, postDelegate, options);
                return BuildDownloadResponse(result, softwarePath, routeDiagnostics, promptPolicy, legitimation);
            }
            catch (Exception ex)
            {
                // A native failure can arrive wrapped (reflection or the delegate boundary). Unwrap it so the caller sees the
                // actual reason (connection/route error, not-reachable CPU, a prompt refused by the policy, etc.).
                var real = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null
                    ? tie.InnerException : ex;
                _session.Log.Error(real, "DownloadPlc failed for {SoftwarePath}", softwarePath);

                // A connection failure is usually the wrong PG/PC adapter on a multi-NIC PC, so
                // always show which route was used and what else was available (issue #14).
                var routeHint = routeDiagnostics == null
                    ? string.Empty
                    : $" Route used: {routeDiagnostics.Description}."
                      + (routeDiagnostics.Candidates.Count > 1
                          ? $" Available routes: {DescribeRoutes(routeDiagnostics.Candidates)}."
                            + " Pass pgPcInterface / targetIpAddress to DownloadPlc to pick one explicitly."
                          : string.Empty);

                var failureMeta = promptPolicy.Summary();
                foreach (var kv in legitimation) failureMeta[kv.Key] = kv.Value == null ? null : HmiValues.DeepClone(kv.Value);
                failureMeta["success"] = false;
                return new OnlineDownloadResult
                {
                    Ok = false,
                    Message = $"Download failed: {real.Message}{routeHint}{promptPolicy.RefusedSummary()}{promptPolicy.ReinitializationSummary()}{promptPolicy.UnansweredSummary()}",
                    Errors = new[] { real.Message },
                    Meta = failureMeta
                };
            }
        }

        // DownloadPlc applies its route with ConnectionConfiguration.ApplyConfiguration, which throws while the device is online, and
        // DownloadProvider connects by itself: the download starts from Offline (R/H: both CPUs through RHOnlineProvider).
        private void RequireDownloadOffline(string softwarePath, PlcSoftware plc, string rhTarget)
        {
            var states = new List<string?>();
#if ONLINE_RH
            if (rhTarget.Length > 0)
            {
                var rh = _session.ResolvePlcService<RHOnlineProvider>(softwarePath, plc);
                if (rh != null) { states.Add(rh.PrimaryState.ToString()); states.Add(rh.BackupState.ToString()); }
            }
            else
#endif
            {
                var provider = _session.ResolvePlcService<OnlineProvider>(softwarePath, plc);
                if (provider != null) states.Add(provider.State.ToString());
            }
            var refusal = OnlineTransitionPolicy.DownloadStateRefusal(softwarePath, states);
            if (refusal != null) throw new AdapterPreconditionException(refusal, "offline-state", false);
        }

        // downloadScope software -> DownloadOptions.Software ("Download software all"); softwareOnlyChanges ->
        // DownloadOptions.SoftwareOnlyChanges ("Download software only changes"), a V17+ member resolved by name.
        private static DownloadOptions DownloadScopeOption(string scope)
            => scope == "softwareOnlyChanges"
                ? (DownloadOptions)Enum.Parse(typeof(DownloadOptions), "SoftwareOnlyChanges")
                : DownloadOptions.Software;

        private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
#endif

#if ONLINE_DOWNLOAD
        private DownloadPromptPolicy BuildDownloadPromptPolicy(OnlineRequest request)
            => DownloadPromptPolicy.ForRequest(request, _session.PromptAnswers(request.PromptAnswersJson));
#endif

        // 每个提示对象按其真实形态应答：枚举型 CurrentSelection、布尔型 Checked 或 SetPassword(SecureString)。
        // 决策来自 DownloadPromptPolicy；无法应答的提示连同 TIA 的提示文本一起记录，永远不记录密码。
#if ONLINE_DOWNLOAD
        private void ApplyDownloadPrompt(object config, DownloadPromptPolicy policy)
        {
            var type = config.GetType();
            var typeName = type.Name;
            string? message = null;
            // the official base classes carry the prompt text; the 43 concrete V21 prompt classes derive from them.
            try { message = config switch { DownloadConfiguration download => download.Message, UploadConfiguration upload => upload.Message, _ => type.GetProperty("Message")?.GetValue(config) as string }; } catch /* swallow(probe-optional): prompt text is diagnostic; a missing message must not prevent answering the prompt */ { }
            _session.Log.Debug("ApplyDownloadPrompt: {TypeName}", typeName);
            try
            {
                var selection = type.GetProperty("CurrentSelection");
                var selectionValues = selection != null && selection.PropertyType.IsEnum && selection.CanWrite
                    ? Enum.GetNames(selection.PropertyType) : Array.Empty<string>();
                var checkedProp = type.GetProperty("Checked");
                bool hasChecked = config is DownloadCheckConfiguration || checkedProp != null && checkedProp.CanWrite && checkedProp.PropertyType == typeof(bool);
                bool hasPassword = config is DownloadPasswordConfiguration || config is UploadPasswordConfiguration || type.GetMethod("SetPassword", new[] { typeof(SecureString) }) != null;

                var answer = policy.Decide(typeName, selectionValues, hasChecked, hasPassword);
                switch (answer.Kind)
                {
                    case DownloadPromptPolicy.AnswerKind.Selection:
                        #if ONLINE_SELECTIVE_DELETE
                        if (config is SelectiveDeleteDownload selectiveDelete) selectiveDelete.CurrentSelection = (SelectiveDeleteDataSelections)Enum.Parse(typeof(SelectiveDeleteDataSelections), answer.Value!, ignoreCase: true);
                        else
#endif
                        selection!.SetValue(config, Enum.Parse(selection.PropertyType, answer.Value!, ignoreCase: true));
                        break;
                    case DownloadPromptPolicy.AnswerKind.Checked:
                        // the Startdrive prompts are typed (V21 Siemens.Engineering.Startdrive.dll): the two upload checks derive from
                        // UploadConfiguration (not UploadCheckConfiguration; V21 only), the three download checks from DownloadCheckConfiguration.
#if ONLINE_PARAMETERS
                        if (config is OverrideTelegramMismatch telegramMismatch) telegramMismatch.Checked = answer.Value == "true";
                        else if (config is OverwriteOfflineConfiguration overwriteOffline) overwriteOffline.Checked = answer.Value == "true";
                        else
#endif
                        if (config is StartDriveDownloadCheckConfiguration startdriveCheck) startdriveCheck.Checked = answer.Value == "true";
#if ONLINE_DOWNLOAD_SENSITIVE
                        else if (config is AcceptDownloadOfUnencryptedSensitiveData unencrypted) unencrypted.Checked = answer.Value == "true";
                        else if (config is ReplaceDownloadedData replaceDownloaded) replaceDownloaded.Checked = answer.Value == "true";

#endif
                        else if (config is DownloadCheckConfiguration check) check.Checked = answer.Value == "true";
                        else checkedProp!.SetValue(config, answer.Value == "true");
                        break;
                    case DownloadPromptPolicy.AnswerKind.Password:
                    {
                        var secret = typeName switch
                        {
                            "BlockBindingPassword" => policy.BlockBindingPassword,
                            "PlcMasterSecretPassword" => policy.MasterSecretPassword,
                            _ => policy.ModuleAccessPassword
                        };
                        // The official pattern checks IsSecureCommunication before entering a password (V17+ on these prompts).
                        bool? secureChannel = null;
#if ONLINE_TLS
                        if (config is DownloadPasswordConfiguration securedDownload) secureChannel = securedDownload.IsSecureCommunication;
                        else if (config is UploadPasswordConfiguration securedUpload) secureChannel = securedUpload.IsSecureCommunication;
#endif
                        var nonTlsRefusal = DownloadPromptPolicy.NonTlsLoginRefusal(secureChannel, policy.AllowNonTlsLogin);
                        if (nonTlsRefusal != null)
                        {
                            answer = new DownloadPromptPolicy.Answer { Kind = DownloadPromptPolicy.AnswerKind.Unanswered, Source = "unanswered", Note = nonTlsRefusal };
                            break;
                        }
                        using var secure = TiaMcp.Adapters.Contracts.EngineeringCredentialRules.ToSecureString(secret!);
                        if (secureChannel != null) answer.Note = "secureCommunication=" + secureChannel.Value;
                        if (config is DownloadPasswordConfiguration downloadPassword) downloadPassword.SetPassword(secure);
                        else if (config is UploadPasswordConfiguration uploadPassword) uploadPassword.SetPassword(secure);
                        else type.GetMethod("SetPassword", new[] { typeof(SecureString) })!.Invoke(config, new object[] { secure });
                        break;
                    }
                    case DownloadPromptPolicy.AnswerKind.Refused:
                        // Record first: the download stops at this prompt even when its documented "no" selection cannot be set.
                        policy.Record(typeName, message, answer);
                        if (answer.Value != null)
                        {
                            try { selection!.SetValue(config, Enum.Parse(selection.PropertyType, answer.Value, ignoreCase: true)); }
                            catch (Exception ex) { _session.Log.Warning(ex, "Refused download prompt {TypeName}: the {Value} selection could not be set", typeName, answer.Value); }
                        }
                        throw new DownloadPromptPolicy.PromptRefusedException("Download stopped at the refused prompt " + typeName
                            + (string.IsNullOrEmpty(message) ? "" : " ('" + message + "')") + "; pass " + answer.AllowWith + "=true to allow it.");
                }
                policy.Record(typeName, message, answer);
            }
            catch (Exception ex) when (ex is not DownloadPromptPolicy.PromptRefusedException)
            {
                var real = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                _session.Log.Warning(real, "Download prompt {TypeName} could not be answered", typeName);
                policy.Record(typeName, message, new DownloadPromptPolicy.Answer { Kind = DownloadPromptPolicy.AnswerKind.Unanswered, Source = "unanswered", Note = "answer failed: " + real.Message });
            }
        }
#endif

        // ---- PG/PC route selection (issue #14) --------------------------------------------------
        // V21: ConnectionConfiguration.ApplyConfiguration(ConfigurationTargetInterface) returns a
        // bool (whether the online route applied) — it does NOT return the IConfiguration. The
        // ConfigurationTargetInterface itself IS an IConfiguration (verified against the V21
        // PublicAPI), so we hand the target to Download().
        //
        // The route tree is Modes -> PcInterfaces -> TargetInterfaces. Picking the FIRST applicable
        // target was enough on a single-NIC PC, but on a multi-NIC PC (WLAN + VPN + PLCSIM virtual
        // adapter) TIA enumerates the wrong adapter first and ApplyConfiguration "succeeds" on it
        // too — it does not verify reachability. The download then leaves through an adapter that
        // cannot see the CPU and fails with "connection to the target module cannot be established".
        // So: enumerate every route, rank by IP proximity to the CPU, and apply the best one.

        // One flattened Modes -> PcInterfaces -> TargetInterfaces route, with the addresses on both
        // ends so the adapter facing the CPU can be identified (and reported back to the caller).
        private sealed class DownloadRoute
        {
            public object Target = null!;
            public string ModeName = string.Empty;
            public string PcInterfaceName = string.Empty;
            public int PcInterfaceNumber;
            public List<string> PcAddresses = new List<string>();
            public string TargetName = string.Empty;
            public List<string> TargetAddresses = new List<string>();
            public int Score;

            public string Describe() =>
                $"{ModeName} / {PcInterfaceName}"
                + (PcAddresses.Count > 0 ? $" [{string.Join(", ", PcAddresses)}]" : " [no IP]")
                + $" -> {TargetName}"
                + (TargetAddresses.Count > 0 ? $" [{string.Join(", ", TargetAddresses)}]" : string.Empty);
        }

        private sealed class DownloadRouteSelection
        {
            public object? Configuration;   // what to hand to Download(); null = fall back to the raw configuration
            public string Description = "(no route selected — raw connection configuration)";
            public string? Error;           // set when an explicit pgPcInterface/targetIpAddress filter matched nothing
            public List<DownloadRoute> Candidates = new List<DownloadRoute>();
            // the exact ConfigurationAddress when the caller named a target IP. It comes from the target interface,
            // from the PC interface's subnet / gateway (where TIA lists the CPU's configured IP - the target interface itself
            // stays empty until the PG adapter can see the CPU), or it is created on the target interface (official
            // ConfigurationAddressComposition.Create; first download to a PLCSIM Advanced instance / a factory-new CPU).
            public ConfigurationAddress? Address;
            public string AddressSource = "";   // targetInterface | subnet | gateway | created | not-created (preview)
            public object? Target;              // the ConfigurationTargetInterface the address belongs to (for the 5-arg Download overload)
            public bool Applied;                // ConnectionConfiguration.ApplyConfiguration returned true for the selection
        }

        // Typed walk of one PC interface's subnets and gateways for an exact address (historical native observation: the route tree of a
        // PLCSIM Advanced target listed 192.168.0.1 only under PcInterface.Subnets["MCP_PN"].Addresses, never under 1 X1).
        private ConfigurationAddress? FindSubnetOrGatewayAddress(object? pcInterface, string ipAddress, out string source)
        {
            source = "";
            if (pcInterface is not ConfigurationPcInterface typed) return null;
            try
            {
                foreach (ConfigurationSubnet subnet in PlcGroupOperations.Items(typed.Subnets).Cast<ConfigurationSubnet>())
                {
                    foreach (ConfigurationAddress address in PlcGroupOperations.Items(subnet.Addresses).Cast<ConfigurationAddress>())
#if ONLINE_ADDRESSES
                        if (string.Equals(address.Address, ipAddress, StringComparison.OrdinalIgnoreCase)) { source = "subnet " + subnet.Name; return address; }
#else
                        if (string.Equals(OnlineRoutePrimitives.Address(address), ipAddress, StringComparison.OrdinalIgnoreCase)) { source = "subnet " + subnet.Name; return address; }
#endif
                    foreach (ConfigurationGateway gateway in PlcGroupOperations.Items(subnet.Gateways).Cast<ConfigurationGateway>())
                        foreach (ConfigurationAddress address in PlcGroupOperations.Items(gateway.Addresses).Cast<ConfigurationAddress>())
#if ONLINE_ADDRESSES
                            if (string.Equals(address.Address, ipAddress, StringComparison.OrdinalIgnoreCase)) { source = "gateway " + gateway.Name + " of subnet " + subnet.Name; return address; }
#else
                            if (string.Equals(OnlineRoutePrimitives.Address(address), ipAddress, StringComparison.OrdinalIgnoreCase)) { source = "gateway " + gateway.Name + " of subnet " + subnet.Name; return address; }
#endif
                }
            }
            catch /* swallow(native-fallback): an unavailable subnet or gateway address leaves target-interface address creation available */ { }
            return null;
        }

        // Official ConfigurationAddressComposition.Create(address) on a target interface (V20 and V21). Returns null when the
        // composition refuses (the exception text is handed back for the error message).
        private static ConfigurationAddress? TryCreateTargetAddress(object? target, string ipAddress, out string error)
        {
#if ONLINE_TARGET_ADDRESSES
            error = "";
            if (target is not ConfigurationTargetInterface typed) { error = "target interface is not a ConfigurationTargetInterface"; return null; }
            try
            {
                var existing = typed.Addresses.Find(ipAddress);
                if (existing != null) return existing;
                return typed.Addresses.Create(ipAddress);
            }
            catch (Exception ex) { error = ex.Message; return null; }
        
#else
            error = "Target-interface address creation requires V17 or later.";
            return null;
#endif
        }

        private List<DownloadRoute> EnumerateDownloadRoutes(object? connectionConfiguration)
        {
            var routes = new List<DownloadRoute>();
            if (connectionConfiguration == null) return routes;
            foreach (var mode in _session.EnumerateReflectedProperty(connectionConfiguration, "Modes"))
                foreach (var pcInterface in _session.EnumerateReflectedProperty(mode, "PcInterfaces"))
                    foreach (var target in _session.EnumerateReflectedProperty(pcInterface, "TargetInterfaces"))
                    {
                        if (target == null) continue;
                        routes.Add(new DownloadRoute
                        {
                            Target = target,
                            ModeName = _session.ReadReflectedString(mode, "Name"),
                            PcInterfaceName = _session.ReadReflectedString(pcInterface, "Name"),
                            PcInterfaceNumber = ReadReflectedInt(pcInterface, "Number"),
                            PcAddresses = ReadConfigurationAddresses(pcInterface),
                            TargetName = _session.ReadReflectedString(target, "Name"),
                            TargetAddresses = ReadConfigurationAddresses(target)
                        });
                    }
            return routes;
        }

        private static object? ReadReflectedParent(object? owner)
        {
            try { return owner?.GetType().GetProperty("Parent")?.GetValue(owner); }
            catch /* swallow(probe-optional): an unavailable parent leaves route selection on the target interface */ { return null; }
        }

        private static int ReadReflectedInt(object? owner, string propertyName)
        {
            try { return owner?.GetType().GetProperty(propertyName)?.GetValue(owner) is int number ? number : 0; }
            catch /* swallow(probe-optional): an unavailable interface number retains the default route diagnostic */ { return 0; }
        }

        // ConfigurationPcInterface.Addresses = the PG/PC adapter's own IPs.
        // ConfigurationTargetInterface.Addresses = the CPU interface's IPs.
        // Both are ConfigurationAddress compositions whose Address property holds the IP string.
        private List<string> ReadConfigurationAddresses(object? owner)
        {
            var addresses = new List<string>();
            foreach (var address in _session.EnumerateReflectedProperty(owner, "Addresses"))
            {
                var value = _session.ReadReflectedString(address, "Address");
                if (!string.IsNullOrWhiteSpace(value)) addresses.Add(value);
            }
            return addresses;
        }

        // Rough "can these two adapters see each other" test. ConfigurationAddress exposes only the
        // address, never the mask, so /24 is an assumption — it holds for the usual 192.168.x.y /
        // 10.x.y.z engineering subnets, and it is only ever used to RANK candidates, never to reject
        // a download outright.
        private static bool SameIpv4Subnet24(string a, string b)
        {
            var left = a.Split('.');
            var right = b.Split('.');
            if (left.Length != 4 || right.Length != 4) return false;
            return left[0] == right[0] && left[1] == right[1] && left[2] == right[2];
        }

        private static void ScoreDownloadRoutes(List<DownloadRoute> routes, string? preferredTargetIp)
        {
            foreach (var route in routes)
            {
                var score = 0;
                if (!string.IsNullOrWhiteSpace(preferredTargetIp))
                {
                    if (route.TargetAddresses.Any(t => string.Equals(t, preferredTargetIp, StringComparison.OrdinalIgnoreCase)))
                        score += 8;
                    if (route.PcAddresses.Any(p => SameIpv4Subnet24(p, preferredTargetIp!)))
                        score += 4;
                }
                // No explicit target IP: the CPU address on the route itself is the reference point.
                // Prefer the adapter sitting in the same subnet as the CPU it has to reach — that is
                // exactly what separates the PLCSIM virtual adapter from a WLAN/VPN adapter.
                if (route.PcAddresses.Any(p => route.TargetAddresses.Any(t => SameIpv4Subnet24(p, t))))
                    score += 2;
                route.Score = score;
            }
        }

        private static string DescribeRoutes(IEnumerable<DownloadRoute> routes)
            => string.Join(" | ", routes.Select(r => r.Describe()));

        // Returns the IConfiguration to pass to Download() / GoOnline(), or a selection carrying an Error when an explicit filter
        // matched nothing or an explicitly requested route was not applied (ApplyConfiguration returned false or threw).
        // Configuration stays null when no route exists at all; callers refuse then (a raw ConnectionConfiguration is not an
        // IConfiguration). apply=false is the side-effect-free preview: it enumerates and ranks only - no ApplyConfiguration and no
        // ConfigurationAddressComposition.Create - and reports what execution would apply or create.
        private DownloadRouteSelection SelectDownloadRoute(
            object? connectionConfiguration,
            string? pgPcInterface,
            string? targetIpAddress,
            bool apply = true)
        {
            var selection = new DownloadRouteSelection();
            if (connectionConfiguration == null) return selection;
            bool explicitRoute = !string.IsNullOrWhiteSpace(pgPcInterface) || !string.IsNullOrWhiteSpace(targetIpAddress);

            try
            {
                selection.Candidates = EnumerateDownloadRoutes(connectionConfiguration);
                if (selection.Candidates.Count == 0) return selection;

                var pool = selection.Candidates;

                if (!string.IsNullOrWhiteSpace(pgPcInterface))
                {
                    var byName = pool
                        .Where(r => r.PcInterfaceName.IndexOf(pgPcInterface, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();
                    if (byName.Count == 0)
                    {
                        selection.Error =
                            $"No PG/PC interface matches '{pgPcInterface}'. Available routes: {DescribeRoutes(pool)}";
                        return selection;
                    }
                    pool = byName;
                }

                if (!string.IsNullOrWhiteSpace(targetIpAddress))
                {
                    var byIp = pool
                        .Where(r => r.TargetAddresses.Any(t => string.Equals(t, targetIpAddress, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    if (byIp.Count > 0)
                    {
                        pool = byIp;
                        foreach (var route in pool)
                            foreach (var candidate in _session.EnumerateReflectedProperty(route.Target, "Addresses"))
#if ONLINE_ADDRESSES
                                if (selection.Address == null && candidate is ConfigurationAddress typed && string.Equals(typed.Address, targetIpAddress, StringComparison.OrdinalIgnoreCase))
#else
                                if (selection.Address == null && candidate is ConfigurationAddress typed && string.Equals(OnlineRoutePrimitives.Address(typed), targetIpAddress, StringComparison.OrdinalIgnoreCase))
#endif
                                { selection.Address = typed; selection.AddressSource = "targetInterface"; selection.Target = route.Target; }
                    }
                    else
                    {
                        // the CPU's configured IP is usually listed under the PC interface's subnet (or a gateway) while the
                        // target interface carries no address at all; a subnet / gateway ConfigurationAddress is an IConfiguration.
                        var byPcInterface = pool.GroupBy(r => r.PcInterfaceName + "#" + r.PcInterfaceNumber).ToList();
                        foreach (var group in byPcInterface)
                        {
                            var first = group.First();
                            var pcInterface = ReadReflectedParent(first.Target);
                            var found = FindSubnetOrGatewayAddress(pcInterface, targetIpAddress!, out var source);
                            if (found == null) continue;
                            selection.Address = found; selection.AddressSource = source; selection.Target = first.Target;
                            pool = group.ToList();
                            break;
                        }
                        if (selection.Address == null)
                        {
                            // Nothing lists the address: create it on the first target interface of the (filtered) pool - official
                            // ConfigurationAddressComposition.Create. This is the first download to a PLCSIM Advanced instance or a
                            // factory-new CPU whose address TIA has not seen yet.
                            var ordered = pool.OrderBy(r => r.TargetName.IndexOf("X1", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1).ToList();
                            if (!apply)
                            {
                                // Preview: the address would be created on the first ordered target interface after approval.
                                selection.AddressSource = "not-created";
                                selection.Target = ordered[0].Target;
                                selection.Configuration = ordered[0].Target;
                                selection.Description = ordered[0].Describe() + " -> address " + targetIpAddress + " (not in the route tree; execution creates it with ConfigurationAddressComposition.Create; preview: nothing applied or created)";
                                return selection;
                            }
                            var errors = new List<string>();
                            foreach (var route in ordered)
                            {
                                var created = TryCreateTargetAddress(route.Target, targetIpAddress!, out var error);
                                if (created == null) { errors.Add(route.TargetName + ": " + error); continue; }
                                selection.Address = created; selection.AddressSource = "created"; selection.Target = route.Target;
                                route.TargetAddresses.Add(targetIpAddress!);
                                pool = new List<DownloadRoute> { route };
                                break;
                            }
                            if (selection.Address == null)
                            {
                                selection.Error =
                                    $"No download route reaches target IP '{targetIpAddress}'. The address could not be created on a target interface either ({string.Join("; ", errors)}). Available routes: {DescribeRoutes(pool)}";
                                return selection;
                            }
                        }
                    }
                }

                ScoreDownloadRoutes(pool, targetIpAddress);

                if (!apply)
                {
                    var best = pool.OrderByDescending(r => r.Score).First();
                    selection.Target ??= best.Target;
                    selection.Configuration = selection.Address != null && selection.AddressSource != "targetInterface" ? selection.Address : selection.Target;
#if ONLINE_ADDRESSES
                    string addressText = selection.Address == null ? "" : " -> address " + selection.Address.Address + " (" + selection.AddressSource + ")";
#else
                    string addressText = selection.Address == null ? "" : " -> address " + OnlineRoutePrimitives.Address(selection.Address) + " (" + selection.AddressSource + ")";
#endif
                    selection.Description = best.Describe() + addressText + " (preview: ApplyConfiguration not called)";
                    return selection;
                }

                // A subnet / gateway / created address is applied as such (ConnectionConfiguration.ApplyConfiguration(ConfigurationAddress))
                // and handed to Download / GoOnline as the IConfiguration; the target interface is kept for the 5-arg Download overload.
                // An address listed on the target interface keeps the field-verified path below (apply + hand over the target interface).
                // Such an address is only selected for an explicit target IP, so a route that is not applied is refused.
                if (selection.Address != null && selection.AddressSource != "targetInterface" && connectionConfiguration is ConnectionConfiguration typedConfiguration)
                {
                    bool applied = false;
                    string? applyError = null;
                    try { applied = typedConfiguration.ApplyConfiguration(selection.Address); }
                    catch (Exception ex) { applyError = ex.GetBaseException().Message; }
                    selection.Configuration = selection.Address;
                    selection.Applied = applied;
                    var route = pool.OrderByDescending(r => r.Score).First();
#if ONLINE_ADDRESSES
                    selection.Description = route.Describe() + " -> address " + selection.Address.Address + " (" + selection.AddressSource + (applied ? "" : "; not confirmed by ApplyConfiguration") + ")";
#else
                    selection.Description = route.Describe() + " -> address " + OnlineRoutePrimitives.Address(selection.Address) + " (" + selection.AddressSource + (applied ? "" : "; not confirmed by ApplyConfiguration") + ")";
#endif
                    selection.Error = OnlineTransitionPolicy.RouteApplyRefusal(explicitRoute, applied, applyError, selection.Description);
                    return selection;
                }

                var applyMethod = connectionConfiguration.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "ApplyConfiguration"
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType.Name == "ConfigurationTargetInterface");

                // OrderByDescending is a stable sort, so equal scores keep the original enumeration
                // order — i.e. the old first-wins behaviour whenever nothing distinguishes adapters.
                var ranked = pool.OrderByDescending(r => r.Score).ToList();
                var applyErrors = new List<string>();

                foreach (var route in ranked)
                {
                    try
                    {
                        if (applyMethod?.Invoke(connectionConfiguration, new[] { route.Target }) is bool ok && ok)
                        {
                            selection.Configuration = route.Target;
                            selection.Description = route.Describe();
                            selection.Applied = true;
                            return selection;
                        }
                        applyErrors.Add(route.Describe() + ": ApplyConfiguration returned false");
                    }
                    catch (Exception ex)
                    {
                        // Try the next ranked route; the reason is kept for the refusal of an explicit route.
                        var real = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                        applyErrors.Add(route.Describe() + ": " + real.Message);
                    }
                }

                var refusal = OnlineTransitionPolicy.RouteApplyRefusal(explicitRoute, false, string.Join("; ", applyErrors), DescribeRoutes(ranked));
                if (refusal != null)
                {
                    selection.Error = refusal;
                    return selection;
                }
                // Automatic pick only: nothing applied cleanly, so hand back the best-ranked target anyway (it IS an
                // IConfiguration), marked as not confirmed. Mirrors the previous fallback to the first target.
                selection.Configuration = ranked[0].Target;
                selection.Description = ranked[0].Describe() + " (not confirmed by ApplyConfiguration" + (applyErrors.Count > 0 ? ": " + string.Join("; ", applyErrors) : "") + ")";
                return selection;
            }
            catch (Exception ex)
            {
                // The route tree could not be walked; callers refuse instead of handing the raw configuration to the native call.
                selection.Error ??= "Route selection failed: " + ex.GetBaseException().Message;
            }
            return selection;
        }

#if ONLINE_DOWNLOAD
        public OnlineReadinessResult CheckDownloadReadiness(string softwarePath)
        {
            var issues = new List<string>();

            if (_session.IsProjectNull())
                return new OnlineReadinessResult { Ready = false, Issues = new[] { "No project open." }, Meta = new HmiObject { ["v4Rejection"] = "PROJECT_NOT_BOUND" } };

            var plcSoftware = _session.GetPlcSoftware(softwarePath);
            if (plcSoftware == null)
                return new OnlineReadinessResult { Ready = false, Issues = new[] { $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix() } };

            bool hasProvider = false;
            bool hasConfig = false;
            bool? isConsistent = null;
            var routes = new List<DownloadRoute>();

            try
            {
                var provider = _session.ResolvePlcService<DownloadProvider>(softwarePath, plcSoftware);
                hasProvider = provider != null;
                if (!hasProvider)
                    issues.Add("DownloadProvider service not available. Check hardware/network configuration.");
                else
                {
                    hasConfig = provider!.Configuration != null;
                    if (!hasConfig)
                        issues.Add("No network configuration for this PLC. Set the IP address in hardware configuration.");
                    else
                        // Read-only: enumerate the PG/PC routes WITHOUT applying any of them, so a
                        // multi-NIC PC can be diagnosed before touching the CPU (issue #14).
                        routes = EnumerateDownloadRoutes(provider.Configuration);
                }
            }
            catch (Exception ex)
            {
                issues.Add($"Error accessing DownloadProvider: {ex.Message}");
            }

            try
            {
                var consistency = _session.ReadPlcConsistency(softwarePath);
                isConsistent = consistency.Any(x => x.Consistent == false) ? false : consistency.Count > 0 && consistency.All(x => x.Consistent == true) ? true : (bool?)null;
                if (isConsistent == false) issues.Add("One or more root/unit blocks or types are inconsistent; compilation must be reviewed.");
                if (isConsistent == null) issues.Add("Compile consistency is unknown (no complete block/type evidence).");
            }
            catch (Exception ex) when (_session.RecoverableAuditError(ex)) { issues.Add("Consistency read failed: " + ex.GetBaseException().Message); }

            ScoreDownloadRoutes(routes, null);
            var routesJson = new HmiArray();
            foreach (var route in routes.OrderByDescending(r => r.Score))
                routesJson.Add(new HmiObject
                {
                    ["mode"] = route.ModeName,
                    ["pgPcInterface"] = route.PcInterfaceName,
                    ["pgPcInterfaceNumber"] = route.PcInterfaceNumber,
                    ["pgPcAddresses"] = string.Join(", ", route.PcAddresses),
                    ["targetInterface"] = route.TargetName,
                    ["targetAddresses"] = string.Join(", ", route.TargetAddresses),
                    ["preferred"] = route.Score > 0
                });

            bool? ready = !hasProvider || !hasConfig || isConsistent == false || issues.Count != 0 ? false : (bool?)null;
            return new OnlineReadinessResult
            {
                Ready = ready,
                HasDownloadProvider = hasProvider,
                HasConfiguration = hasConfig,
                IsConsistent = isConsistent,
                Message = ready == false ? $"PLC '{softwarePath}' has {issues.Count} readiness issue(s)."
                    : "Offline configuration checks passed; actual download readiness remains unknown.",
                Issues = issues.Count > 0 ? issues.ToArray() : null,
                Meta = new HmiObject
                {
                    ["success"] = true,   // the check itself ran; Ready carries the verdict
                    ["configurationReady"] = hasProvider && hasConfig,
                    ["consistencyScope"] = "root, software units and safety units: blocks and types",
                    ["unchecked"] = new HmiArray("deviceReachability", "accessAuthorization", "hardwareConsistency", "downloadPrompts"),
                    ["downloadRouteCount"] = routes.Count,
                    // Ordered best-first — the same ranking DownloadToPlc applies. preferred=true
                    // means the PG/PC adapter shares an IPv4 /24 with the CPU it has to reach.
                    ["downloadRoutes"] = routesJson,
                    ["note"] = "Override the automatic pick with DownloadPlc(pgPcInterface:…) or DownloadPlc(targetIpAddress:…)."
                }
            };
        }
#endif

#if ONLINE_DOWNLOAD
        private OnlineDownloadResult BuildDownloadResponse(
            DownloadResult result,
            string softwarePath,
            DownloadRouteSelection? route,
            DownloadPromptPolicy? prompts = null,
            HmiObject? legitimation = null)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            CollectDownloadMessages(result.Messages, errors, warnings);

            bool ok = result.State == DownloadResultState.Success
                   || result.State == DownloadResultState.Information
                   || result.State == DownloadResultState.Warning;

            var meta = new HmiObject
            {
                ["softwarePath"] = softwarePath,
                ["timestamp"] = DateTime.Now,
                ["downloadState"] = result.State.ToString(),
                // Which PG/PC adapter the download actually left through — the thing you need
                // to see first when a multi-NIC PC downloads "successfully" to the wrong place.
                ["pgPcRoute"] = route?.Description ?? string.Empty,
                ["pgPcRouteCandidates"] = route?.Candidates.Count ?? 0,
                ["targetAddress"] = route?.Address?.Address,
                ["targetAddressSource"] = route?.AddressSource ?? string.Empty
            };
            if (prompts != null)
                foreach (var kv in prompts.Summary()) meta[kv.Key] = kv.Value == null ? null : HmiValues.DeepClone(kv.Value);
            if (legitimation != null)
                foreach (var kv in legitimation) meta[kv.Key] = kv.Value == null ? null : HmiValues.DeepClone(kv.Value);
            meta["success"] = ok;
            PlcB5Result.Record(meta, result.State, true, messages: new HmiArray(errors.Concat(warnings).Select(m => (object)HmiScalar.Json(m)!).ToArray()));

            return new OnlineDownloadResult
            {
                Ok = ok,
                Message = $"Download {result.State}: {result.ErrorCount} error(s), {result.WarningCount} warning(s)." + (prompts?.ReinitializationSummary() ?? string.Empty) + (prompts?.UnansweredSummary() ?? string.Empty),
                State = result.State.ToString(),
                ErrorCount = result.ErrorCount,
                WarningCount = result.WarningCount,
                Errors = errors.Count > 0 ? errors.ToArray() : null,
                Warnings = warnings.Count > 0 ? warnings.ToArray() : null,
                Meta = meta
            };
        }
#endif

#if ONLINE_DOWNLOAD
        private static void CollectDownloadMessages(
            IEnumerable? messages,
            List<string> errors,
            List<string> warnings)
        {
            if (messages == null) return;
            foreach (var obj in messages)
            {
                if (obj == null) continue;
                try
                {
                    // typed result messages (DownloadResultMessage / UploadResultMessage carry State, Message, counts, nested Messages).
                    if (obj is DownloadResultMessage downloadMessage)
                    {
                        var text = downloadMessage.Message ?? string.Empty; var state = downloadMessage.State;
                        if (state == DownloadResultState.Error && text.Length > 0) errors.Add(text + (downloadMessage.ErrorCount > 1 ? " [" + downloadMessage.ErrorCount + " errors]" : ""));
                        else if (state == DownloadResultState.Warning && text.Length > 0) warnings.Add(text + (downloadMessage.WarningCount > 1 ? " [" + downloadMessage.WarningCount + " warnings]" : ""));
                        CollectDownloadMessages(downloadMessage.Messages, errors, warnings);
                        continue;
                    }
                    if (obj is UploadResultMessage uploadMessage)
                    {
                        var text = uploadMessage.Message ?? string.Empty; var state = uploadMessage.State;
                        if (state == UploadResultState.Error && text.Length > 0) errors.Add(text + (uploadMessage.ErrorCount > 1 ? " [" + uploadMessage.ErrorCount + " errors]" : ""));
                        else if (state == UploadResultState.Warning && text.Length > 0) warnings.Add(text + (uploadMessage.WarningCount > 1 ? " [" + uploadMessage.WarningCount + " warnings]" : ""));
                        CollectDownloadMessages(uploadMessage.Messages, errors, warnings);
                        continue;
                    }
                    var msgText = obj.GetType().GetProperty("Message")?.GetValue(obj) as string ?? string.Empty;
                    var stateObj = obj.GetType().GetProperty("State")?.GetValue(obj);
                    var stateName = stateObj?.ToString() ?? string.Empty;

                    if (stateName == "Error" && !string.IsNullOrWhiteSpace(msgText))
                        errors.Add(msgText);
                    else if (stateName == "Warning" && !string.IsNullOrWhiteSpace(msgText))
                        warnings.Add(msgText);

                    // Recurse into nested Messages
                    var nested = obj.GetType().GetProperty("Messages")?.GetValue(obj) as IEnumerable;
                    if (nested != null)
                        CollectDownloadMessages(nested, errors, warnings);
                }
                catch /* swallow(enumerate-optional): one unreadable result message must not discard the other download or upload diagnostics */ { }
            }
        }
#endif

        #endregion

        private sealed class PcInterfacePick
        {
            public object Interface = null!;
            public string ModeName = "";
            public string Name = "";
            public int Number;
            public List<string> Addresses = new List<string>();
        }

        private List<PcInterfacePick> EnumeratePcInterfaces(object? connectionConfiguration)
        {
            var list = new List<PcInterfacePick>();
            if (connectionConfiguration == null) return list;
            foreach (var mode in _session.EnumerateReflectedProperty(connectionConfiguration, "Modes"))
                foreach (var pcInterface in _session.EnumerateReflectedProperty(mode, "PcInterfaces"))
                {
                    if (pcInterface == null) continue;
                    list.Add(new PcInterfacePick
                    {
                        Interface = pcInterface, ModeName = _session.ReadReflectedString(mode, "Name"), Name = _session.ReadReflectedString(pcInterface, "Name"),
                        Number = ReadReflectedInt(pcInterface, "Number"), Addresses = ReadConfigurationAddresses(pcInterface)
                    });
                }
            return list;
        }

        private static HmiArray DescribePcInterfaces(IEnumerable<PcInterfacePick> picks)
            => new HmiArray(picks.Select(p => (object)new HmiObject { ["mode"] = p.ModeName, ["name"] = p.Name, ["number"] = p.Number, ["addresses"] = string.Join(", ", p.Addresses) }).ToArray());

        // 精确按名称或编号选择 PG/PC 接口；只有一个时允许省略。绝不做子串猜测。
        private static PcInterfacePick PickPcInterface(List<PcInterfacePick> picks, string pgPcInterface)
        {
            if (picks.Count == 0) throw new PortalException(PortalErrorCode.NotFound, "No PG/PC interface available in this connection configuration.");
            if (string.IsNullOrWhiteSpace(pgPcInterface))
            {
                var single = SameAdapter(picks);
                if (single != null) return single;
                throw new ArgumentException("pgPcInterface is required because several PG/PC interfaces exist: " + string.Join("; ", picks.Select(p => $"{p.Name} (#{p.Number}, {p.ModeName})")));
            }
            var matches = picks.Where(p => string.Equals(p.Name, pgPcInterface, StringComparison.Ordinal) || (int.TryParse(pgPcInterface, out var n) && p.Number == n)).ToList();
            if (matches.Count == 1) return matches[0];
            if (matches.Count == 0) throw new PortalException(PortalErrorCode.NotFound, "PG/PC interface not found (exact name or number required): " + pgPcInterface);
            var same = SameAdapter(matches);
            if (same != null) return same;
            throw new ArgumentException("pgPcInterface is ambiguous; use the interface number: " + string.Join("; ", matches.Select(p => $"{p.Name} (#{p.Number}, {p.ModeName})")));
        }

        // Historical native observation: the project-level StationUploadProvider configuration lists the same adapter once per
        // connection mode ("PLCSIM (#1)" under PN/IE, PROFIBUS and MPI), so an exact name matched three picks and was refused as
        // ambiguous. Picks that share name and number are one adapter; the PN/IE mode wins, otherwise the first.
        private static PcInterfacePick? SameAdapter(List<PcInterfacePick> picks)
        {
            if (picks.Count == 0) return null;
            if (picks.Select(p => p.Name + "#" + p.Number).Distinct(StringComparer.Ordinal).Count() != 1) return null;
            return picks.FirstOrDefault(p => string.Equals(p.ModeName, "PN/IE", StringComparison.OrdinalIgnoreCase)) ?? picks[0];
        }

        // 在所选 PG/PC 接口的 TargetInterfaces.Addresses 中精确匹配目标地址。扫描后 TIA 才会填充可达目标。
        private ConfigurationAddress? FindTargetAddress(PcInterfacePick pick, string targetIpAddress, List<string> seen)
        {
            foreach (var target in _session.EnumerateReflectedProperty(pick.Interface, "TargetInterfaces"))
                foreach (var address in _session.EnumerateReflectedProperty(target, "Addresses"))
                {
                    var value = _session.ReadReflectedString(address, "Address");
                    if (!string.IsNullOrWhiteSpace(value)) seen.Add(value);
                    if (string.Equals(value, targetIpAddress, StringComparison.OrdinalIgnoreCase) && address is ConfigurationAddress typed) return typed;
                }
            // subnet / gateway addresses of the PC interface (where TIA lists configured CPU addresses)
            var viaSubnet = FindSubnetOrGatewayAddress(pick.Interface, targetIpAddress, out _);
            if (viaSubnet != null) return viaSubnet;
            return null;
        }

        // an address that ScanAccessibleDevices reported on the same PG/PC interface (IP or MAC of a station TIA has not
        // seen in the route tree) is created on the first target interface - or, without target interfaces (project-level
        // StationUploadProvider), on the first subnet - through the official ConfigurationAddressComposition.Create.
        private static ConfigurationAddress? CreateScannedAddress(PcInterfacePick pick, string targetAddress, out string note)
        {
#if ONLINE_SCAN
            note = "";
            if (pick.Interface is not ConfigurationPcInterface typed) { note = "PC interface is not typed"; return null; }
            var errors = new List<string>();
            try
            {
                var accessible = typed.GetAccessibleDevices() ?? new List<ConfigurationAccessibleDevice>();
                if (!accessible.Any(d => string.Equals(d.Address, targetAddress, StringComparison.OrdinalIgnoreCase) || string.Equals(d.MACAddress, targetAddress, StringComparison.OrdinalIgnoreCase)))
                { note = "address not reported by the network scan on this PG/PC interface"; return null; }
            }
            catch (Exception ex) { note = "network scan failed: " + ex.Message; return null; }
            foreach (ConfigurationTargetInterface target in PlcGroupOperations.Items(typed.TargetInterfaces).Cast<ConfigurationTargetInterface>())
            {
                try { var created = target.Addresses.Find(targetAddress) ?? target.Addresses.Create(targetAddress); note = "created on target interface " + target.Name; return created; }
                catch (Exception ex) { errors.Add(target.Name + ": " + ex.Message); }
            }
            foreach (ConfigurationSubnet subnet in PlcGroupOperations.Items(typed.Subnets).Cast<ConfigurationSubnet>())
            {
                try { var created = subnet.Addresses.Find(targetAddress) ?? subnet.Addresses.Create(targetAddress); note = "created on subnet " + subnet.Name; return created; }
                catch (Exception ex) { errors.Add("subnet " + subnet.Name + ": " + ex.Message); }
            }
            // Historical native observation: the project-level StationUploadProvider lists the PC interface with neither target interfaces nor
            // subnets, so the last composition left is the PC interface's own ConfigurationPcInterface.Addresses.
            try { var created = typed.Addresses.Find(targetAddress) ?? typed.Addresses.Create(targetAddress); note = "created on PC interface " + typed.Name; return created; }
            catch (Exception ex) { errors.Add("PC interface " + typed.Name + ": " + ex.Message); }
            note = errors.Count == 0 ? "no target interface, subnet or PC interface composition to create the address on" : string.Join("; ", errors);
            return null;
        
#else
            note = "Accessible-device scanning requires V19 or later; use an existing configured route.";
            return null;
#endif
        }

#if ONLINE_DOWNLOAD
        private ConnectionConfiguration ResolveScanConfiguration(string softwarePath, HmiObject meta)
        {
            if (!string.IsNullOrWhiteSpace(softwarePath))
            {
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                var provider = _session.ResolvePlcService<DownloadProvider>(softwarePath, plc) ?? throw new PortalException(PortalErrorCode.NotFound, "DownloadProvider unavailable for " + softwarePath);
                meta["configurationSource"] = "DownloadProvider:" + softwarePath;
                return provider.Configuration ?? throw new PortalException(PortalErrorCode.InvalidState, "PLC has no connection configuration.");
            }
            var upload = _session.CurrentProject!.GetService<StationUploadProvider>() ?? throw new NotSupportedException("StationUploadProvider service unavailable on this project/version; pass softwarePath to scan through an existing PLC instead.");
            meta["configurationSource"] = "StationUploadProvider";
            return upload.Configuration ?? throw new PortalException(PortalErrorCode.InvalidState, "StationUploadProvider has no connection configuration.");
        }
#endif

#if ONLINE_SCAN
        public HmiMessage ScanAccessibleDevices(string pgPcInterface = "", string softwarePath = "", int offset = 0, int limit = 100)
            => _session.RunHmiStepTool("ScanAccessibleDevices", meta => {
                if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset >= 0 and 1 <= limit <= 500 required.");
                var configuration = ResolveScanConfiguration(softwarePath, meta);
                var picks = EnumeratePcInterfaces(configuration);
                meta["pgPcInterfaces"] = DescribePcInterfaces(picks);
                var pick = PickPcInterface(picks, pgPcInterface);
                meta["pgPcInterface"] = new HmiObject { ["mode"] = pick.ModeName, ["name"] = pick.Name, ["number"] = pick.Number };
                if (pick.Interface is not ConfigurationPcInterface typed) throw new NotSupportedException("Selected PG/PC interface is not a ConfigurationPcInterface.");
                var devices = typed.GetAccessibleDevices() ?? new List<ConfigurationAccessibleDevice>();
                meta["apiCallSuccess"] = true;
                var rows = devices.Select(d => (object)new HmiObject { ["name"] = d.Name, ["address"] = d.Address, ["macAddress"] = d.MACAddress, ["deviceSeries"] = d.DeviceSeries }).ToList();
                meta["total"] = rows.Count; meta["offset"] = offset; meta["limit"] = limit;
                meta["devices"] = new HmiArray(rows.Skip(offset).Take(limit).ToArray());
                meta["dataComplete"] = offset + limit >= rows.Count;
                return rows.Count == 0 ? "Network scan returned no accessible devices on the selected PG/PC interface." : $"{rows.Count} accessible device(s) found by a live network scan; no project change.";
            });
#endif

#if ONLINE_DOWNLOAD
        // The preview only reads the route tree. A station TIA has not listed needs the live DCP scan and the route address creation;
        // both run only after approval (dryRun=false, confirmUpload=true), because the host runs this preview before asking.
        public HmiMessage UploadStationFromPlc(OnlineRequest request)
            => _session.RunHmiStepTool("UploadStationFromPlc", meta => {
                string targetIpAddress = request.TargetIpAddress, pgPcInterface = request.PgPcInterface, password = request.Password;
                bool confirmUpload = request.ConfirmUpload, dryRun = request.DryRun;
                if (string.IsNullOrWhiteSpace(targetIpAddress)) throw new ArgumentException("targetIpAddress is required (exact IP address of the station).");
                var policy = BuildDownloadPromptPolicy(request);
                var provider = _session.CurrentProject!.GetService<StationUploadProvider>() ?? throw new NotSupportedException("StationUploadProvider service unavailable on this project/version.");
                var picks = EnumeratePcInterfaces(provider.Configuration);
                var pick = PickPcInterface(picks, pgPcInterface);
                var seen = new List<string>();
                var address = FindTargetAddress(pick, targetIpAddress, seen);
                meta["pgPcInterface"] = new HmiObject { ["mode"] = pick.ModeName, ["name"] = pick.Name, ["number"] = pick.Number };
                meta["knownTargetAddresses"] = string.Join(", ", seen.Distinct());
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                meta["trustDeviceCertificate"] = request.TrustDeviceCertificate;
                meta["devicesBefore"] = _session.CurrentProject.Devices.Count;
                // Historical native observation: ConfigurationAddressComposition.Create takes IP addresses
                // only ("'02-C0-A8-00-C8-00' does not specify a valid address"); the official page creates "192.68.0.1". A MAC from the scan is
                // therefore never a valid target, and a virtual PLC that has not been downloaded to (IP 0.0.0.0) cannot be uploaded from.
                if (address == null && LooksLikeMacAddress(targetIpAddress))
                    throw new PortalException(PortalErrorCode.NotFound, "'" + targetIpAddress + "' is a MAC address: ConfigurationAddressComposition.Create accepts IP addresses only, so pass the device's IP as listed by ScanAccessibleDevices (a PLC that shows only a MAC, e.g. a PLCSIM Advanced instance before its first download, has no IP yet - assign one by downloading first).");
                if (address != null) { meta["targetAddressSource"] = "route tree"; meta["targetAddress"] = AddressText(address); }
                if (dryRun)
                {
                    if (address != null) return "Station upload preview: provider, PG/PC interface and target address resolved from the route tree; no PLC contact, no project change.";
#if ONLINE_SCAN
                    meta["targetAddressSource"] = "not in the route tree"; meta["targetAddress"] = targetIpAddress;
                    meta["executionEffects"] = new HmiArray("DCP network scan (ConfigurationPcInterface.GetAccessibleDevices)", "route address creation (ConfigurationAddressComposition.Create)");
                    return "Station upload preview: '" + targetIpAddress + "' is not in the route tree of the selected PG/PC interface. Execution (after approval) runs a DCP network scan on it and creates the route address before uploading; this preview sent nothing to the network and changed nothing.";
#else
                    throw new PortalException(PortalErrorCode.NotFound, "Target address not present on the selected PG/PC interface. This release has no accessible-device scan (V19+), so only an address already listed in its route tree, subnets or gateways can be uploaded from.");
#endif
                }
                if (!confirmUpload) throw new InvalidOperationException("Station upload adds a new device to the project from the live PLC; set confirmUpload=true to execute.");
                using var access = _session.AcquireHmiEditAccess();
                if (address == null)
                {
                    meta["mayHaveChanged"] = true;   // the scan and the address creation may change the route configuration
                    address = CreateScannedAddress(pick, targetIpAddress, out var addressSource);
                    meta["addressCreation"] = addressSource;
                    if (address == null)
                        throw new PortalException(PortalErrorCode.NotFound, "Target address not present on the selected PG/PC interface and not creatable from the network scan (" + addressSource + "). "
                            + "Run ScanAccessibleDevices on the same interface first; only the exact IP address listed there is accepted.");
                    meta["targetAddressSource"] = "network scan"; meta["targetAddress"] = AddressText(address);
                }
                using var legitimationScope = AttachOnlineLegitimationHandler(provider.Configuration, password, meta, request.TrustDeviceCertificate, request.AllowNonTlsLogin);
                meta["mayHaveChanged"] = true;
                UploadConfigurationDelegate handler = config => ApplyDownloadPrompt(config, policy);
                var result = provider.StationUpload(address, handler);
                meta["apiCallSuccess"] = true;
                foreach (var kv in policy.Summary()) meta[kv.Key] = kv.Value == null ? null : HmiValues.DeepClone(kv.Value);
                var errors = new List<string>(); var warnings = new List<string>();
                CollectDownloadMessages(result?.Messages, errors, warnings);
                meta["uploadState"] = result?.State.ToString(); meta["errorCount"] = result?.ErrorCount; meta["warningCount"] = result?.WarningCount;
                meta["errors"] = new HmiArray(errors.Select(e => (object)e).ToArray()); meta["warnings"] = new HmiArray(warnings.Select(w => (object)w).ToArray());
                var station = result?.UploadedStation;
                PlcB5Result.Record(meta, result?.State, true, messages: new HmiArray(errors.Concat(warnings).Select(m => (object)HmiScalar.Json(m)!).ToArray()));
                meta["uploadedStation"] = station?.Name; meta["devicesAfter"] = _session.CurrentProject.Devices.Count;
                bool ok = result != null && TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(result.State) && station != null && _session.CurrentProject.Devices.Any(d => d.Name == station.Name);
                meta["operationSuccess"] = ok; meta["dataComplete"] = false;
                if (!ok) throw new InvalidOperationException("Station upload did not yield a verified device: " + (result?.State.ToString() ?? "no result") + policy.UnansweredSummary());
                return $"Station uploaded as device '{station!.Name}' and verified in the project; not saved, compiled or downloaded." + policy.UnansweredSummary();
            });

#if ONLINE_ADDRESSES
        private static string AddressText(ConfigurationAddress address) => address.Address;
#else
        private static string AddressText(ConfigurationAddress address) => OnlineRoutePrimitives.Address(address);
#endif
#endif

        private static bool LooksLikeMacAddress(string value)
            => System.Text.RegularExpressions.Regex.IsMatch((value ?? "").Trim(), "^([0-9A-Fa-f]{2}[-:]){5}[0-9A-Fa-f]{2}$");

#if ONLINE_PARAMETERS
        // ParameterUploadProvider exists only on SINAMICS StartDrive head modules (DeviceItemClassifications.HM; a CPU returns null).
        // The preview selects the route without applying it; execution applies it after approval. An empty targetIpAddress passes a
        // null ConfigurationAddress, the official ParameterUpload(configuration, null, delegate) pattern.
        public HmiMessage UploadDeviceParameters(OnlineRequest request)
            => _session.RunHmiStepTool("UploadDeviceParameters", meta => {
                string targetIpAddress = request.TargetIpAddress ?? "", pgPcInterface = request.PgPcInterface, password = request.Password;
                bool confirmUpload = request.ConfirmUpload, dryRun = request.DryRun;
                bool addressRequested = !string.IsNullOrWhiteSpace(targetIpAddress);
                var policy = BuildDownloadPromptPolicy(request);
                var owner = _session.ExactEngineeringHardware("", "");
                // ParameterUploadProvider 只在 V21 PublicAPI 中存在；按名解析并经泛型 GetService 反射取得，V20 明确 NotSupported。
                var providerType = typeof(StationUploadProvider).Assembly.GetType("Siemens.Engineering.Upload.ParameterUploadProvider")
                    ?? throw new NotSupportedException("ParameterUploadProvider is not part of this TIA version's PublicAPI (V21 only).");
                if (owner is not IEngineeringServiceProvider serviceProvider) throw new NotSupportedException("Hardware object is not a service provider.");
                var getService = typeof(IEngineeringServiceProvider).GetMethod("GetService")!.MakeGenericMethod(providerType);
                var provider = getService.Invoke(serviceProvider, null) ?? throw new NotSupportedException("ParameterUploadProvider service unavailable on this hardware object: it exists only on SINAMICS StartDrive head modules (HM), not on CPUs or other device items.");
                var uploadMethod = providerType.GetMethod("ParameterUpload", new[] { typeof(IConfiguration), typeof(ConfigurationAddress), typeof(UploadConfigurationDelegate) })
                    ?? throw new NotSupportedException("ParameterUpload(IConfiguration, ConfigurationAddress, delegate) unavailable.");
                var providerConfiguration = providerType.GetProperty("Configuration")?.GetValue(provider);
                (DownloadRouteSelection Route, ConfigurationAddress? Address) Resolve(bool apply)
                {
                    var selected = SelectDownloadRoute(providerConfiguration, EmptyToNull(pgPcInterface), EmptyToNull(targetIpAddress), apply);
                    if (selected.Error != null) throw new PortalException(PortalErrorCode.NotFound, selected.Error);
                    if (selected.Configuration == null) throw new PortalException(PortalErrorCode.NotFound, "No PG/PC route to the device; run ScanAccessibleDevices first.");
                    ConfigurationAddress? exact = selected.Address;   // target interface, subnet / gateway or created address
                    if (addressRequested)
                        foreach (var candidate in _session.EnumerateReflectedProperty(selected.Target ?? selected.Configuration, "Addresses"))
                            if (exact == null && string.Equals(_session.ReadReflectedString(candidate, "Address"), targetIpAddress, StringComparison.OrdinalIgnoreCase) && candidate is ConfigurationAddress typed) exact = typed;
                    if (addressRequested && exact == null && (apply || selected.AddressSource != "not-created"))
                        throw new PortalException(PortalErrorCode.NotFound, "Exact target address not found on the selected route: " + selected.Description);
                    return (selected, exact);
                }
                var preview = Resolve(false);
                meta["route"] = preview.Route.Description; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                meta["targetAddress"] = preview.Address != null ? AddressText(preview.Address) : addressRequested ? targetIpAddress : null;
                meta["addressMode"] = addressRequested ? (preview.Route.AddressSource.Length == 0 ? "targetInterface" : preview.Route.AddressSource) : "null address (provider default)";
                meta["trustDeviceCertificate"] = request.TrustDeviceCertificate;
                meta["before"] = HmiScalar.Read(owner);
                if (dryRun) return "Parameter upload preview: provider, route and address resolved without applying the route (ApplyConfiguration not called); no PLC contact, no project change.";
                if (!confirmUpload) throw new InvalidOperationException("Parameter upload overwrites offline hardware parameters from the live device; set confirmUpload=true to execute.");
                using var access = _session.AcquireHmiEditAccess();
                meta["mayHaveChanged"] = true;
                var (route, address) = Resolve(true);
                meta["route"] = route.Description; meta["routeApplied"] = route.Applied;
                if (address != null) meta["targetAddress"] = AddressText(address);
                UploadConfigurationDelegate handler = config => ApplyDownloadPrompt(config, policy);
                if ((route.Target ?? route.Configuration) is not IConfiguration configuration) throw new NotSupportedException("Selected route is not an IConfiguration.");
                using var legitimationScope = AttachOnlineLegitimationHandler(providerConfiguration, password, meta, request.TrustDeviceCertificate, request.AllowNonTlsLogin);
                try { uploadMethod.Invoke(provider, new object?[] { configuration, address, handler }); }
                catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null) { throw tie.InnerException; }
                meta["apiCallSuccess"] = true;
                foreach (var kv in policy.Summary()) meta[kv.Key] = kv.Value == null ? null : HmiValues.DeepClone(kv.Value);
                meta["after"] = HmiScalar.Read(owner); meta["dataComplete"] = false;
                return "Parameter upload completed; compare before/after scalars for changes. Not saved, compiled or downloaded." + policy.UnansweredSummary();
            });
#endif

#if ONLINE_FOLDER
        public HmiMessage DownloadPlcToFolder(OnlineRequest request)
            => _session.RunHmiStepTool("DownloadPlcToFolder", meta => {
                string softwarePath = request.SoftwarePath, destinationDirectory = request.DestinationDirectory, targetForSoftware = request.TargetForSoftware;
                bool overwriteOnMemoryCard = request.OverwriteOnMemoryCard, confirmDownload = request.ConfirmDownload, dryRun = request.DryRun;
                if (!new[] { "CPU", "PlcSimulationAdvanced" }.Contains(targetForSoftware)) throw new ArgumentException("targetForSoftware must be CPU or PlcSimulationAdvanced.");
                if (!Path.IsPathRooted(destinationDirectory)) throw new ArgumentException("Absolute destinationDirectory required.");
                var directory = new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(destinationDirectory));
                if (directory.Exists && directory.EnumerateFileSystemInfos().Any()) throw new IOException("destinationDirectory must be new or empty; merging into existing content is refused.");
                // TargetForSoftware and OverwriteOnMemoryCard are answered from this request's own parameters.
                var policy = BuildDownloadPromptPolicy(request);
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                var provider = _session.ResolvePlcService<DownloadProvider>(softwarePath, plc) ?? throw new PortalException(PortalErrorCode.NotFound, "DownloadProvider unavailable for " + softwarePath);
                if (provider.GetType().GetMethod("Download", new[] { typeof(DirectoryInfo), typeof(DownloadConfigurationDelegate) }) == null) throw new NotSupportedException("Download(DirectoryInfo, delegate) unavailable on this version.");
                meta["dryRun"] = dryRun; meta["mayHaveWrittenFiles"] = false; meta["destination"] = directory.FullName; meta["targetForSoftware"] = targetForSoftware; meta["overwriteOnMemoryCard"] = overwriteOnMemoryCard;
                if (dryRun) return "Folder download preview: PLC, provider and destination validated; no files written.";
                if (!confirmDownload) throw new InvalidOperationException("Writing a memory-card image is an explicit action; set confirmDownload=true to execute.");
                if (!directory.Exists) directory.Create();
                meta["mayHaveWrittenFiles"] = true;
                DownloadConfigurationDelegate handler = config => ApplyDownloadPrompt(config, policy);
                DownloadResult result;
                try { result = provider.Download(directory, handler); }
                catch (Exception ex) when (policy.Refused.Count > 0 || policy.ReinitializationSummary().Length > 0)
                {
                    foreach (var kv in policy.Summary()) meta[kv.Key] = kv.Value == null ? null : HmiValues.DeepClone(kv.Value);
                    throw new InvalidOperationException("Folder download stopped." + policy.RefusedSummary() + policy.ReinitializationSummary(), ex);
                }
                meta["apiCallSuccess"] = true;
                foreach (var kv in policy.Summary()) meta[kv.Key] = kv.Value == null ? null : HmiValues.DeepClone(kv.Value);
                var errors = new List<string>(); var warnings = new List<string>();
                CollectDownloadMessages(result?.Messages, errors, warnings);
                meta["downloadState"] = result?.State.ToString(); meta["errorCount"] = result?.ErrorCount; meta["warningCount"] = result?.WarningCount;
                meta["errors"] = new HmiArray(errors.Select(e => (object)e).ToArray()); meta["warnings"] = new HmiArray(warnings.Select(w => (object)w).ToArray());
                directory.Refresh();
                var files = directory.Exists ? directory.GetFiles("*", SearchOption.AllDirectories) : Array.Empty<FileInfo>();
                meta["filesWritten"] = files.Length; meta["bytesWritten"] = files.Sum(f => f.Length); meta["dataComplete"] = false;
                PlcB5Result.Record(meta, result?.State, false, messages: new HmiArray(errors.Concat(warnings).Select(m => (object)HmiScalar.Json(m)!).ToArray()));
                meta["targetFiles"] = new HmiArray(files.Select(f => (object)PlcB5Result.FileRow(f.FullName)).ToArray());
                bool ok = result != null && TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(result.State) && files.Length > 0;
                meta["operationSuccess"] = ok;
                if (!ok) throw new InvalidOperationException("Folder download did not produce a verified image: " + (result?.State.ToString() ?? "no result") + policy.ReinitializationSummary() + policy.UnansweredSummary());
                return $"Memory-card image written: {files.Length} file(s) in {directory.FullName}. Content semantics not verified; no PLC contacted." + policy.UnansweredSummary();
            });
#endif

        /// <summary>
        /// Subscribes the OnlineLegitimation handler of a ConnectionConfiguration for the duration of a GoOnline / Download /
        /// Upload call: password prompts (legacy and UMAC) are answered from the given credentials, and the TLS trust prompt of
        /// S7-1500 FW >= 2.9 CPUs (TlsVerificationConfiguration) is answered Trusted only when trustDeviceCertificate is set. A
        /// password goes to a prompt that reports IsSecureCommunication=false only with allowNonTlsLogin. Returns
        /// an IDisposable that unsubscribes when disposed; callers MUST dispose to avoid handler leaks. Returns null when the
        /// configuration object is not a ConnectionConfiguration (no event to hook).
        /// </summary>
        private static IDisposable? AttachOnlineLegitimationHandler(object? configuration, string? password, HmiObject? meta, bool trustDeviceCertificate, bool allowNonTlsLogin)
            => AttachOnlineLegitimationHandler(configuration, password, null, null, meta, trustDeviceCertificate, allowNonTlsLogin);

        // besides the legacy password prompt, UMAC-protected PLCs raise OnlineAuthenticationConfiguration (user name +
        // password + UserType, IsSecureCommunication, GetSupportedAuthenticationTypes); the answered prompts are reported to meta.
        // Subscribe even without a password so the TLS trust decision is answered and recorded.
        // Historical PLCSIM Advanced Softbus observation (version/date not recorded): an unanswered TLS prompt
        // refuses the first connection; see docs/reference/real-machine-ledger.md.
        private static IDisposable? AttachOnlineLegitimationHandler(object? configuration, string? password, string? userName, string? userType, HmiObject? meta, bool trustDeviceCertificate, bool allowNonTlsLogin)
        {
#if ONLINE_LEGITIMATION
            if (configuration is not ConnectionConfiguration conn)
                return null;

            // Build the SecureString once. The same instance can be reused across multiple
            // legitimation prompts within the same call (e.g., read-then-write access).
            SecureString? secure = null;
            if (!string.IsNullOrEmpty(password))
            {
                secure = TiaMcp.Adapters.Contracts.EngineeringCredentialRules.ToSecureString(password!);
            }

            OnlineConfigurationDelegate handler = (cfg) =>
            {
#if ONLINE_TLS
                if (cfg is TlsVerificationConfiguration tls)
                {
                    var before = tls.CurrentSelection.ToString();
                    var apply = TiaMcp.Adapters.Contracts.EngineeringCredentialRules.TlsSelectionToApply(trustDeviceCertificate, before);
                    if (apply != null) tls.CurrentSelection = TlsVerificationConfigurationSelection.Trusted;
                    if (meta != null) meta["tlsVerification"] = TlsRecord(tls, before, trustDeviceCertificate, apply != null);
                }
                else
#endif
                if (secure == null) return;

#if ONLINE_AUTHENTICATION
                else if (cfg is OnlineAuthenticationConfiguration auth)
                {
                    OnlineCredentials credentials = auth.OnlineCredentials;
                    var supported = new HmiArray();
                    try { foreach (AuthenticationType type in auth.GetSupportedAuthenticationTypes()) supported.Add(type.CurrentUserType.ToString()); } catch { /* swallow(enumerate-optional): supported authentication types are diagnostic; credentials still answer the prompt */ }
                    var nonTlsRefusal = DownloadPromptPolicy.NonTlsLoginRefusal(auth.IsSecureCommunication, allowNonTlsLogin);
                    if (nonTlsRefusal != null)
                    {
                        if (meta != null) meta["onlineAuthentication"] = new HmiObject { ["isSecureCommunication"] = false, ["answered"] = false, ["note"] = nonTlsRefusal, ["supportedUserTypes"] = supported, ["userName"] = userName ?? "" };
                        return;
                    }
                    if (!string.IsNullOrEmpty(userName)) credentials.Name = userName;
                    if (!string.IsNullOrEmpty(userType)) credentials.Type = (UserType)Enum.Parse(typeof(UserType), userType);
                    else if (!string.IsNullOrEmpty(userName)) credentials.Type = UserType.ProjectUser;
                    credentials.SetPassword(secure);
                    if (meta != null) meta["onlineAuthentication"] = new HmiObject { ["isSecureCommunication"] = auth.IsSecureCommunication, ["supportedUserTypes"] = supported, ["userName"] = userName ?? "", ["userType"] = credentials.Type.ToString() };
                }
#endif
                else if (cfg is OnlinePasswordConfiguration pwdCfg)
                {
#if !ONLINE_PASSWORD_SECURITY
                    pwdCfg.SetPassword(secure);
                    if (meta != null) meta["onlinePassword"] = new HmiObject { ["answered"] = true };
#else
                    var nonTlsRefusal = DownloadPromptPolicy.NonTlsLoginRefusal(pwdCfg.IsSecureCommunication, allowNonTlsLogin);
                    if (nonTlsRefusal != null)
                    {
                        if (meta != null) meta["onlinePassword"] = new HmiObject { ["isSecureCommunication"] = false, ["answered"] = false, ["note"] = nonTlsRefusal };
                        return;
                    }
                    pwdCfg.SetPassword(secure);
                    if (meta != null) meta["onlinePassword"] = new HmiObject { ["isSecureCommunication"] = pwdCfg.IsSecureCommunication };
#endif
                }
            };
            conn.OnlineLegitimation += handler;
            return new HandlerScope(() => { conn.OnlineLegitimation -= handler; secure?.Dispose(); });

#else
            return null;
#endif
        }

#if ONLINE_TLS
        // The TLS decision record. The certificates the device presented are read where the API offers them (V21
        // TlsVerificationConfiguration.Certificates -> VerificationCertificate.Certificate, an X509Certificate2); the property is
        // resolved by name so the earlier releases compile and simply report none.
        private static HmiObject TlsRecord(object tls, string before, bool trustDeviceCertificate, bool trusted)
        {
            var typed = (TlsVerificationConfiguration)tls;
            var record = new HmiObject
            {
                ["plcName"] = typed.PlcName, ["verificationInfo"] = typed.VerificationInfo,
                ["selectionBefore"] = before, ["selectionAfter"] = typed.CurrentSelection.ToString(),
                ["trustDeviceCertificate"] = trustDeviceCertificate,
                ["note"] = trusted ? "Certificate trusted for this call on the caller's decision (trustDeviceCertificate=true); the same prompt TIA shows in the UI."
                    : trustDeviceCertificate ? "Already trusted."
                    : before == "Trusted" ? "Already trusted; no decision needed."
                    : "Left untrusted (trustDeviceCertificate=false); TIA refuses the connection. Check the presented certificate, then pass trustDeviceCertificate=true to trust it for this call."
            };
            var certificates = PresentedCertificates(tls);
            if (certificates != null) record["certificates"] = certificates;
            return record;
        }

        private static HmiArray? PresentedCertificates(object tls)
        {
            try
            {
                if (tls.GetType().GetProperty("Certificates")?.GetValue(tls) is not IEnumerable certificates) return null;
                var rows = new HmiArray();
                foreach (var item in certificates)
                {
                    if (item?.GetType().GetProperty("Certificate")?.GetValue(item) is not X509Certificate2 certificate) continue;
                    using var sha256 = SHA256.Create();
                    rows.Add(new HmiObject
                    {
                        ["subject"] = certificate.Subject, ["issuer"] = certificate.Issuer, ["serialNumber"] = certificate.SerialNumber,
                        ["notBefore"] = certificate.NotBefore.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                        ["notAfter"] = certificate.NotAfter.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                        ["thumbprintSha1"] = certificate.Thumbprint,
                        ["sha256"] = BitConverter.ToString(sha256.ComputeHash(certificate.RawData)).Replace("-", "")
                    });
                    if (rows.Count >= 8) break;
                }
                return rows;
            }
            catch (Exception ex)
            {
                // The record is diagnostic; an unreadable certificate list must not fail the TIA callback.
                return new HmiArray(new HmiObject { ["unreadable"] = ex.GetType().Name });
            }
        }
#endif

        private sealed class HandlerScope : IDisposable
        {
            private Action? _detach;
            public HandlerScope(Action detach) { _detach = detach; }
            public void Dispose() { _detach?.Invoke(); _detach = null; }
        }


        // ---- transfer routes and R/H providers --------------------------------------------------------------------------------
#if ONLINE_ADDRESSES
        private static HmiArray AddressRows(IEnumerable<ConfigurationAddress> addresses) => new HmiArray(PlcGroupOperations.Items(addresses).Cast<ConfigurationAddress>().Select(a => (object)new HmiObject { ["name"] = a.Name, ["address"] = a.Address }).ToArray());
#else
        private static HmiArray AddressRows(IEnumerable<ConfigurationAddress> addresses) => new HmiArray(PlcGroupOperations.Items(addresses).Cast<ConfigurationAddress>().Select(a => (object)new HmiObject { ["name"] = a.Name, ["address"] = OnlineRoutePrimitives.Address(a) }).ToArray());
#endif

        public HmiMessage ReadTransferRoutes(string softwarePath, int maxItems = 500)
            => _session.RunHmiStepTool("ListTransferRoutes", meta => {
                if (maxItems < 1 || maxItems > 500) throw new ArgumentException("offset>=0, limit 1..500 required.");
                var plc = _session.GetPlcSoftware(softwarePath) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact PLC software not found: " + softwarePath + _session.AvailablePlcPathsSuffix());
#if ONLINE_DOWNLOAD
                var download = _session.ResolvePlcService<DownloadProvider>(softwarePath, plc);
                meta["downloadProviderAvailable"] = download != null;
                meta["routeProvider"] = "DownloadProvider";
#else
                // V14 SP1 has no DownloadProvider; the route tree is read from OnlineProvider.Configuration.
                var download = _session.ResolvePlcService<OnlineProvider>(softwarePath, plc);
                meta["downloadProviderAvailable"] = false;
                meta["routeProvider"] = "OnlineProvider";
#endif
                int items = 0; bool truncated = false;
                if (download != null)
                {
                    ConnectionConfiguration configuration = download.Configuration;
                    var modes = new HmiArray(); meta["modes"] = modes;
                    foreach (ConfigurationMode mode in PlcGroupOperations.Items(configuration.Modes).Cast<ConfigurationMode>())
                    {
                        var modeRow = new HmiObject { ["name"] = mode.Name }; var interfaces = new HmiArray(); modeRow["pcInterfaces"] = interfaces; modes.Add(modeRow);
                        foreach (ConfigurationPcInterface pcInterface in PlcGroupOperations.Items(mode.PcInterfaces).Cast<ConfigurationPcInterface>())
                        {
                            if (++items > maxItems) { truncated = true; break; }
                            var row = new HmiObject { ["name"] = pcInterface.Name, ["number"] = pcInterface.Number };
                            try
                            {
#if ONLINE_ADDRESSES
                                row["addresses"] = AddressRows(pcInterface.Addresses);
#else
                                row["addresses"] = AddressRows(OnlineRoutePrimitives.PcAddresses(pcInterface));
#endif
                            }
                            catch (Exception ex) { row["addressesError"] = ex.GetBaseException().Message; }
                            try
                            {
                                row["subnets"] = new HmiArray(PlcGroupOperations.Items(pcInterface.Subnets).Cast<ConfigurationSubnet>().Select(s => (object)new HmiObject
                                {
                                    ["name"] = s.Name, ["addresses"] = AddressRows(s.Addresses),
                                    ["gateways"] = new HmiArray(PlcGroupOperations.Items(s.Gateways).Cast<ConfigurationGateway>().Select(g => (object)new HmiObject { ["name"] = g.Name, ["addresses"] = AddressRows(g.Addresses) }).ToArray())
                                }).ToArray());
                            }
                            catch (Exception ex) { row["subnetsError"] = ex.GetBaseException().Message; }
                try
                {
#if ONLINE_TARGET_ADDRESSES
                    row["targetInterfaces"] = new HmiArray(PlcGroupOperations.Items(pcInterface.TargetInterfaces).Cast<ConfigurationTargetInterface>().Select(t => (object)new HmiObject { ["name"] = t.Name, ["addresses"] = AddressRows(t.Addresses) }).ToArray());
#else
                    row["targetInterfaces"] = new HmiArray(PlcGroupOperations.Items(pcInterface.TargetInterfaces).Cast<ConfigurationTargetInterface>().Select(t => (object)new HmiObject { ["name"] = t.Name, ["addresses"] = AddressRows(OnlineRoutePrimitives.TargetAddresses(t)) }).ToArray());
#endif
                }
                            catch (Exception ex) { row["targetInterfacesError"] = ex.GetBaseException().Message; }
                            interfaces.Add(row);
                        }
                        if (truncated) break;
                    }
                }
                // R/H systems expose the redundant providers instead of / next to the standard ones; the CPU on the reference project is not R/H.
#if ONLINE_RH
                var rhDownload = _session.ResolvePlcService<RHDownloadProvider>(softwarePath, plc); meta["rhDownloadProviderAvailable"] = rhDownload != null;
                var rhOnline = _session.ResolvePlcService<RHOnlineProvider>(softwarePath, plc); meta["rhOnlineProviderAvailable"] = rhOnline != null;
                if (rhOnline != null) { try { meta["rhOnline"] = new HmiObject { ["primaryState"] = rhOnline.PrimaryState.ToString(), ["backupState"] = rhOnline.BackupState.ToString() }; } catch (Exception ex) { meta["rhOnlineError"] = ex.GetBaseException().Message; } }
#else
                meta["rhDownloadProviderAvailable"] = false; meta["rhOnlineProviderAvailable"] = false;
#endif
                var onlineProvider = _session.ResolvePlcService<OnlineProvider>(softwarePath, plc); meta["onlineProviderAvailable"] = onlineProvider != null;
                if (onlineProvider != null) { try { meta["onlineState"] = onlineProvider.State.ToString(); } catch (Exception ex) { meta["onlineStateError"] = ex.GetBaseException().Message; } }
                meta["compileProviderNote"] = "Siemens.Engineering.Compiler.CompileProvider is internal in the supplied PublicAPI (documented, not public); compilation goes through ICompilable.";
                meta["truncated"] = truncated; meta["apiCallSuccess"] = true; meta["dataComplete"] = !truncated;
                meta["scope"] = "ConnectionConfiguration route tree of the download provider (V14 SP1: the online provider; routeProvider names it) (Modes -> PcInterfaces with Addresses / Subnets (Gateways) / TargetInterfaces) plus availability of RHDownloadProvider / RHOnlineProvider (with Primary/BackupState) / OnlineProvider. Read-only; nothing is applied.";
                return "Transfer routes read; no route applied, no modification.";
            });

    }
}
