using System;
using System.Collections.Generic;
using System.Threading;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Online;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcpServer.Siemens;

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
        // Every connection names its address. V21 uses the address overload; earlier releases apply the address first.
        // trustDeviceCertificate answers the TLS prompt of FW >= 2.9 CPUs (meta.tlsVerification records the decision and the
        // certificates the device presented); allowNonTlsLogin lets a password go over a connection that is not TLS-secured.
        public OnlineStateResult GoOnline(string softwarePath, string? ipAddress, string? password, string? userName, string? userType, string rhTarget, string? pgPcInterface, bool trustDeviceCertificate, bool allowNonTlsLogin = false)
        {
            OnlineTransitionPolicy.RequireAddress(ipAddress, nameof(ipAddress));
            TiaMcp.Adapters.Contracts.EngineeringCredentialRules.ValidateOnlineCredentials(userName ?? "", password ?? "", userType ?? "");
            rhTarget = TiaMcp.Adapters.Contracts.EngineeringCredentialRules.ValidateRhTarget(rhTarget);
            var meta = new HmiObject { ["trustDeviceCertificate"] = trustDeviceCertificate, ["writeAttempted"] = false };
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

            Func<string>? readState = null;
            try
            {
#if ONLINE_RH
                if (rhTarget.Length > 0)
                {
                    RHOnlineProvider rh = _session.ResolvePlcService<RHOnlineProvider>(softwarePath, plcSoftware)
                        ?? throw new PortalException(PortalErrorCode.NotFound,
                                "RHOnlineProvider service not available on this PLC (rhTarget applies to R/H systems only).");
                    readState = () => (rhTarget == "primary" ? rh.PrimaryState : rh.BackupState).ToString();
                    using var rhScope =
                        AttachOnlineLegitimationHandler(rh.Configuration, password, userName, userType, meta, trustDeviceCertificate, allowNonTlsLogin);
                    // ConfigurationAddress has no public constructor: the address object comes from the route tree (target interfaces).
                    ConfigurationAddress rhAddress = FindConfigurationAddress(rh.Configuration, ipAddress!)
                        ?? throw new PortalException(PortalErrorCode.NotFound, "Exact R/H target address not found: " + ipAddress);
#if !ONLINE_ADDRESS_OVERLOAD
                    bool rhApplied = false;
                    string? rhApplyError = null;
                    meta["mayHaveChanged"] = true;
                    try { rhApplied = rh.Configuration.ApplyConfiguration(rhAddress); }
                    catch (Exception ex) { rhApplyError = ex.GetBaseException().Message; }
                    var rhRefusal = OnlineTransitionPolicy.RouteApplyRefusal(true, rhApplied, rhApplyError, ipAddress!);
                    if (rhRefusal != null) throw new InvalidOperationException(rhRefusal);
                    meta["writeAttempted"] = true;
                    OnlineState rhState = rhTarget == "primary" ? rh.GoOnlineToPrimary() : rh.GoOnlineToBackup();
#else
                    meta["writeAttempted"] = true;
                    OnlineState rhState = rhTarget == "primary" ? rh.GoOnlineToPrimary(rhAddress) : rh.GoOnlineToBackup(rhAddress);
#endif
                    var rhName = OnlineTransitionPolicy.WaitWhileConnecting(rhState.ToString(),
                        () => (rhTarget == "primary" ? rh.PrimaryState : rh.BackupState).ToString(), Thread.Sleep);
                    // envelope: legacy-single-verdict
                    meta["success"] = rhName == "Online";
                    return new OnlineStateResult { State = rhName, IsOnline = rhName == "Online", IsReachable = rhName == "Online" || rhName == "Protected",
                        Message = BuildOnlineStateMessage(rhName, softwarePath) + $" (R/H {rhTarget}; primary={rh.PrimaryState}, backup={rh.BackupState})",
                            Meta = meta };
                }
#endif
                var provider = _session.ResolvePlcService<OnlineProvider>(softwarePath, plcSoftware);
                if (provider == null)
                {
                    // 缺少在线服务时状态未知，不能报告为已确认离线。
                    LegacyResponseMetadata.Complete(meta, false);
                    return new OnlineStateResult
                    {
                        Meta = meta,
                        State = "Unknown",
                        IsOnline = false,
                        IsReachable = false,
                        Message = "OnlineProvider service not available on this PLC, so the online state was NOT measured. "
                                + "State 'Unknown' means undetermined — it does NOT mean the CPU is offline."
                    };
                }

                using var legitimationScope =
                    AttachOnlineLegitimationHandler(provider.Configuration, password, userName, userType, meta, trustDeviceCertificate, allowNonTlsLogin);

                string routeNote = "";
                readState = () => provider.State.ToString();
                string before = readState();
                meta["onlineStateBefore"] = before;
                if (before == "Online")
                {
                    // ApplyConfiguration throws while the device is online, so an established connection is kept as it is.
                    meta["success"] = true;
                    return new OnlineStateResult { State = before, IsOnline = true, IsReachable = true, Meta = meta,
                        Message = BuildOnlineStateMessage(before, softwarePath)
                            + " It was already online, so the requested route was not applied (ApplyConfiguration throws while the device is online); "
                            + "call DisconnectOnlinePlc first to change the route." };
                }
                var selection = SelectDownloadRoute(provider.Configuration, pgPcInterface, ipAddress, deferApply: true);
                if (selection.WriteAttempted) meta["mayHaveChanged"] = true;
                if (selection.Unreadable) meta["writeOutcomeKnown"] = false;
                if (selection.Error != null || selection.Address == null)
                {
                    meta["success"] = false;
                    return new OnlineStateResult {
                        State = "NotReachable", IsOnline = false, IsReachable = false,
                        Message = "ConnectOnlinePlc not attempted: " + (selection.Error ?? "Exact target address not found on the selected route."),
                        Meta = meta };
                }
                ConfigurationAddress address = selection.Address;
                string? applyError = null;
                meta["mayHaveChanged"] = true;
                try { selection.Applied = provider.Configuration.ApplyConfiguration(address); }
                catch (Exception ex) { applyError = ex.GetBaseException().Message; }
                var routeRefusal = OnlineTransitionPolicy.RouteApplyRefusal(true, selection.Applied, applyError, selection.Description);
                meta["route"] = selection.Description;
                meta["routeApplied"] = selection.Applied;
                if (routeRefusal != null)
                {
                    meta["success"] = false;
                    return new OnlineStateResult {
                        State = before, IsOnline = false, IsReachable = false,
                        Message = "ConnectOnlinePlc not attempted: " + routeRefusal, Meta = meta };
                }
                routeNote = " Route: " + selection.Description + ".";

                meta["writeAttempted"] = true;
                OnlineState resultState;
#if !ONLINE_ADDRESS_OVERLOAD
                resultState = provider.GoOnline();
#else
                resultState = provider.GoOnline(address);
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
                for (var e = ex; e != null && chain.Count < 6; e = e.InnerException) if (!string.IsNullOrWhiteSpace(e.Message) && !chain.Contains(e.Message))
                    chain.Add(e.Message);
                string stateNow = "";
                try { stateNow = readState?.Invoke() ?? ""; }
                catch (Exception) { /* swallow(probe-optional): an unavailable state cannot settle the transition. */ }
                if (stateNow.Length == 0 || !NativeRefusalProbe.AllowsRefusalProof(ex)) meta["writeOutcomeKnown"] = false;
                meta["success"] = false; meta["error"] = string.Join(" <- ", chain);
                meta["nativeMessage"] = meta["error"];
                meta["phase"] = meta["writeAttempted"] is true ? "goOnline" : "routeSelection";
                if (stateNow.Length > 0) meta["onlineStateAfter"] = stateNow;
                var hint = stateNow == "Incompatible"
                    ? " TIA reports the connection as Incompatible (device / firmware / program mismatch): download first (DownloadPlc), then go online." : "";
                return new OnlineStateResult {
                    State = stateNow.Length > 0 ? stateNow : "Unknown", IsOnline = stateNow == "Online",
                    IsReachable = stateNow is "Online" or "Incompatible" or "Protected",
                    Message = $"GoOnline failed: {string.Join(" <- ", chain)}{hint}", Meta = meta
                };
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

        private string BuildOnlineStateMessage(string state, string softwarePath)
        {
            return state switch
            {
                "Online" => $"'{softwarePath}' is online and reachable.",
                "Offline" => $"'{softwarePath}' is offline. " + (_session.CanConnectOnline ? "Call ConnectOnlinePlc first." : "Go online in TIA Portal."),
                "Connecting" => $"'{softwarePath}' is connecting...",
                "Incompatible" => $"'{softwarePath}' online but firmware/config mismatch. Download required.",
                "NotReachable" => $"'{softwarePath}' not reachable. Check IP address and network.",
                "Protected" => $"'{softwarePath}' is password-protected. Authentication required.",
                "Disconnecting" => $"'{softwarePath}' is disconnecting.",
                _ => $"'{softwarePath}' online state: {state}."
            };
        }

    }
}
