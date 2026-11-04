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
                        ?? throw new PortalException(PortalErrorCode.NotFound,
                                "RHOnlineProvider service not available on this PLC (rhTarget applies to R/H systems only).");
                    using var rhScope =
                        AttachOnlineLegitimationHandler(rh.Configuration, password, userName, userType, meta, trustDeviceCertificate, allowNonTlsLogin);
                    // ConfigurationAddress has no public constructor: the address object comes from the route tree (target interfaces).
                    ConfigurationAddress? rhAddress = string.IsNullOrWhiteSpace(ipAddress) ? null : FindConfigurationAddress(rh.Configuration, ipAddress!);
                    if (!string.IsNullOrWhiteSpace(ipAddress) && rhAddress == null) _session.Log
                        .Warning("ConnectOnlinePlc R/H: no ConfigurationAddress {Ip} in the route tree; using the configured address", ipAddress);
#if !ONLINE_ADDRESS_OVERLOAD
                    if (rhAddress != null) _session.Log
                        .Warning("ConnectOnlinePlc R/H: this release's RHOnlineProvider has no address overload; using the configured address");
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
                        Message = BuildOnlineStateMessage(rhName, softwarePath) + $" (R/H {rhTarget}; primary={rh.PrimaryState}, backup={rh.BackupState})",
                            Meta = meta };
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

                using var legitimationScope =
                    AttachOnlineLegitimationHandler(provider.Configuration, password, userName, userType, meta, trustDeviceCertificate, allowNonTlsLogin);

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
                            ? " It was already online, so the requested route was not applied (ApplyConfiguration throws while the device is online); call " +
                                "DisconnectOnlinePlc first to change the route."
                            : " It was already online.") };
                }
                bool isConfigured = provider.Configuration.IsConfigured;
                meta["isConfigured"] = isConfigured;
                var notConfigured = OnlineTransitionPolicy.ConnectRefusal(routeRequested, isConfigured);
                if (notConfigured != null)
                {
                    meta["success"] = false;
                    return new OnlineStateResult {
                        State = before, IsOnline = false, IsReachable = false, Message = "ConnectOnlinePlc not attempted: " + notConfigured, Meta = meta };
                }
                if (routeRequested)
                {
                    var selection = SelectDownloadRoute(provider.Configuration, pgPcInterface, ipAddress);
                    if (selection.Error != null)
                    {
                        meta["success"] = false;
                        return new OnlineStateResult {
                            State = "NotReachable", IsOnline = false, IsReachable = false, Message = "ConnectOnlinePlc not attempted: " + selection.Error,
                                Meta = meta
                        };
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
                for (var e = ex; e != null && chain.Count < 6; e = e.InnerException) if (!string.IsNullOrWhiteSpace(e.Message) && !chain.Contains(e.Message))
                    chain.Add(e.Message);
                string stateNow = "";
                try { stateNow = _session.ResolvePlcService<OnlineProvider>(softwarePath, plcSoftware)?.State.ToString() ?? ""; } catch (Exception) { /* swallow(probe-optional): a failed state probe must preserve the original GoOnline failure */ }
                meta["success"] = false; meta["error"] = string.Join(" <- ", chain);
                if (stateNow.Length > 0) meta["onlineStateAfter"] = stateNow;
                var hint = stateNow == "Incompatible"
                    ? " TIA reports the connection as Incompatible (device / firmware / program mismatch): download first (DownloadPlc), then go online." : "";
                return new OnlineStateResult {
                    State = stateNow.Length > 0 && stateNow != "Online" ? stateNow : "NotReachable", IsOnline = false, IsReachable = stateNow == "Incompatible"
                        || stateNow == "Protected", Message = $"GoOnline failed: {string.Join(" <- ", chain)}{hint}", Meta = meta
                };
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
                        foreach (ConfigurationAddress address in PlcGroupOperations.Items(OnlineRoutePrimitives.TargetAddresses(target))
                                .Cast<ConfigurationAddress>())
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

    }
}
