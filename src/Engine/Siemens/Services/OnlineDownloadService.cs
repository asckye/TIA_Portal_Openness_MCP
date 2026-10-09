using TiaMcp.Logic.V4;
using Microsoft.Extensions.Logging;
using Siemens.Engineering.Cax;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Download;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HW.Utilities;
using Siemens.Engineering.HW;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.OpcUa;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW;
using Siemens.Engineering.Safety;
using Siemens.Engineering.Upload.Configurations;
using Siemens.Engineering.Upload;
using Siemens.Engineering;
using System.Collections.Generic;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text;
using System.Xml.Linq;
using System;
using TiaMcpServer.ModelContextProtocol;
using static TiaMcpServer.Siemens.EngineeringSessionHelpers;

namespace TiaMcpServer.Siemens.Services
{
    // Historical native observations lack a recorded PLCSIM version/date; see docs/reference/real-machine-ledger.md.
    internal sealed partial class OnlineDownloadService
    {
        private readonly IEngineeringSession _session;

        public OnlineDownloadService(IEngineeringSession session) => _session = session;

        public ResponseOnlineState GetOnlineState(string softwarePath)
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
                    return new ResponseOnlineState
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

                return new ResponseOnlineState
                {
                    State = stateName,
                    IsOnline = isOnline,
                    IsReachable = isReachable,
                    Message = BuildOnlineStateMessage(stateName, softwarePath)
                };
            }
            catch (Exception ex)
            {
                _session.Logger?.LogError(ex, "GetOnlineState failed for {SoftwarePath}", softwarePath);
                return new ResponseOnlineState { State = "Unknown", IsOnline = false, IsReachable = false, Message = $"Error: {ex.Message}" };
            }
        }

        public ResponseOnlineState GoOnline(string softwarePath, string? ipAddress = null, string? password = null) => GoOnline(softwarePath, ipAddress, password, null, null, "", null);

        public ResponseOnlineState GoOnline(string softwarePath, string? ipAddress, string? password, string? userName, string? userType, string rhTarget) => GoOnline(softwarePath, ipAddress, password, userName, userType, rhTarget, null);

        public ResponseOnlineState GoOnline(string softwarePath, string? ipAddress, string? password, string? userName, string? userType, string rhTarget, string? pgPcInterface) => GoOnline(softwarePath, ipAddress, password, userName, userType, rhTarget, pgPcInterface, true);

        // userName/userType answer OnlineAuthenticationConfiguration (UMAC-protected PLCs); rhTarget primary|backup
        // goes online through RHOnlineProvider.GoOnlineToPrimary/Backup on R/H systems.
        // Historical native observation: OnlineProvider.GoOnline() uses whatever route TIA last applied - on a fresh
        // project nothing is applied and TIA answers "The connection cannot be established". The route is now selected like a
        // download (pgPcInterface + ipAddress -> ConnectionConfiguration.ApplyConfiguration) and an explicit address goes through
        // the official GoOnline(ConfigurationAddress) overload (V21; V20 applies the address and calls GoOnline()).
        // trustDeviceCertificate answers the TLS prompt of FW >= 2.9 CPUs (meta.tlsVerification records the decision).
        public ResponseOnlineState GoOnline(string softwarePath, string? ipAddress, string? password, string? userName, string? userType, string rhTarget, string? pgPcInterface, bool trustDeviceCertificate)
        {
            EngineeringCredentialRules.ValidateOnlineCredentials(userName ?? "", password ?? "", userType ?? "");
            rhTarget = EngineeringCredentialRules.ValidateRhTarget(rhTarget);
            var meta = new JsonObject { ["trustDeviceCertificate"] = trustDeviceCertificate };
            // 未连接工程或路径无效时，在线状态未经测量，不能报告为 Offline。
            if (_session.IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "ConnectOnlinePlc: no project is open, so the online state was NOT measured. "
                    + "Call ConnectPortal + OpenProject (or AttachOpenProject) first.");
            }

            var plcSoftware = _session.ResolvePlc(softwarePath, PlcAccess.Write);
            if (plcSoftware == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"ConnectOnlinePlc: PLC software not found at '{softwarePath}', so the online state was NOT measured."
                    + _session.AvailablePlcPathsSuffix());
            }

            try
            {
                if (rhTarget.Length > 0)
                {
                    RHOnlineProvider rh = _session.ResolvePlcService<RHOnlineProvider>(softwarePath, plcSoftware)
                        ?? throw new PortalException(PortalErrorCode.NotFound, "RHOnlineProvider service not available on this PLC (rhTarget applies to R/H systems only).");
                    using var rhScope = AttachOnlineLegitimationHandler(rh.Configuration, password, userName, userType, meta, trustDeviceCertificate);
                    // ConfigurationAddress has no public constructor: the address object comes from the route tree (target interfaces).
                    ConfigurationAddress? rhAddress = string.IsNullOrWhiteSpace(ipAddress) ? null : FindConfigurationAddress(rh.Configuration, ipAddress!);
                    if (!string.IsNullOrWhiteSpace(ipAddress) && rhAddress == null) _session.Logger?.LogWarning("ConnectOnlinePlc R/H: no ConfigurationAddress {Ip} in the route tree; using the configured address", ipAddress);
#if TIA_V20
                    if (rhAddress != null) _session.Logger?.LogWarning("ConnectOnlinePlc R/H: the V20 RHOnlineProvider has no address overload; using the configured address");
                    OnlineState rhState = rhTarget == "primary" ? rh.GoOnlineToPrimary() : rh.GoOnlineToBackup();
#else
                    OnlineState rhState = rhAddress == null
                        ? (rhTarget == "primary" ? rh.GoOnlineToPrimary() : rh.GoOnlineToBackup())
                        : (rhTarget == "primary" ? rh.GoOnlineToPrimary(rhAddress) : rh.GoOnlineToBackup(rhAddress));
#endif
                    var rhName = rhState.ToString();
                    // envelope: legacy-single-verdict
                    meta["success"] = rhName == "Online";
                    return new ResponseOnlineState { State = rhName, IsOnline = rhName == "Online", IsReachable = rhName == "Online" || rhName == "Protected",
                        Message = BuildOnlineStateMessage(rhName, softwarePath) + $" (R/H {rhTarget}; primary={rh.PrimaryState}, backup={rh.BackupState})", Meta = meta };
                }
                var provider = _session.ResolvePlcService<OnlineProvider>(softwarePath, plcSoftware);
                if (provider == null)
                {
                    // 缺少在线服务时状态未知，不能报告为已确认离线。
                    return new ResponseOnlineState
                    {
                        State = "Unknown",
                        IsOnline = false,
                        IsReachable = false,
                        Message = "OnlineProvider service not available on this PLC, so the online state was NOT measured. "
                                + "State 'Unknown' means undetermined — it does NOT mean the CPU is offline."
                    };
                }

                using var legitimationScope = AttachOnlineLegitimationHandler(provider.Configuration, password, userName, userType, meta, trustDeviceCertificate);

                string routeNote = "";
                ConfigurationAddress? address = null;
                if (!string.IsNullOrWhiteSpace(ipAddress) || !string.IsNullOrWhiteSpace(pgPcInterface))
                {
                    var selection = SelectDownloadRoute(provider.Configuration, pgPcInterface, ipAddress);
                    if (selection.Error != null)
                    {
                        meta["success"] = false;
                        return new ResponseOnlineState { State = "NotReachable", IsOnline = false, IsReachable = false, Message = "ConnectOnlinePlc not attempted: " + selection.Error, Meta = meta };
                    }
                    address = selection.Address;
                    routeNote = " Route: " + selection.Description + ".";
                    meta["route"] = selection.Description;
                }

                OnlineState resultState;
#if TIA_V20
                // V20 has GoOnline() only; the address (when any) was applied through ApplyConfiguration by the route selection.
                resultState = provider.GoOnline();
#else
                resultState = address != null ? provider.GoOnline(address) : provider.GoOnline();
#endif

                var stateName = resultState.ToString();
                bool isOnline = stateName == "Online";
                meta["success"] = isOnline;
                return new ResponseOnlineState
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
                _session.Logger?.LogError(ex, "GoOnline failed for {SoftwarePath}", softwarePath);
                // Historical native observation: EngineeringTargetInvocationException carries only "Error when calling method 'GoOnline'" - the
                // reason ("Incompatible" against a never-downloaded PLCSIM instance) sits in the inner chain; surface it and the state TIA holds.
                var chain = new List<string>();
                for (var e = ex; e != null && chain.Count < 6; e = e.InnerException) if (!string.IsNullOrWhiteSpace(e.Message) && !chain.Contains(e.Message)) chain.Add(e.Message);
                string stateNow = "";
                try { stateNow = _session.ResolvePlcService<OnlineProvider>(softwarePath, plcSoftware)?.State.ToString() ?? ""; } catch (Exception) { /* swallow(probe-optional): a failed state probe must preserve the original GoOnline failure */ }
                meta["success"] = false; meta["error"] = string.Join(" <- ", chain);
                if (stateNow.Length > 0) meta["onlineStateAfter"] = stateNow;
                var hint = stateNow == "Incompatible" ? " TIA reports the connection as Incompatible (device / firmware / program mismatch): download first (DownloadPlc), then go online." : "";
                return new ResponseOnlineState { State = stateNow.Length > 0 && stateNow != "Online" ? stateNow : "NotReachable", IsOnline = false, IsReachable = stateNow == "Incompatible" || stateNow == "Protected", Message = $"GoOnline failed: {string.Join(" <- ", chain)}{hint}", Meta = meta };
            }
        }

        // Typed walk of Modes -> PcInterfaces -> TargetInterfaces -> Addresses for an exact IP (R/H online targets).
        private ConfigurationAddress? FindConfigurationAddress(ConnectionConfiguration configuration, string ipAddress)
        {
            foreach (ConfigurationMode mode in EngineeringGroupOperations.Items(configuration.Modes).Cast<ConfigurationMode>())
                foreach (ConfigurationPcInterface pcInterface in EngineeringGroupOperations.Items(mode.PcInterfaces).Cast<ConfigurationPcInterface>())
                {
                    foreach (ConfigurationTargetInterface target in EngineeringGroupOperations.Items(pcInterface.TargetInterfaces).Cast<ConfigurationTargetInterface>())
                        foreach (ConfigurationAddress address in EngineeringGroupOperations.Items(target.Addresses).Cast<ConfigurationAddress>())
                            if (string.Equals(address.Address, ipAddress, StringComparison.OrdinalIgnoreCase)) return address;
                    var viaSubnet = FindSubnetOrGatewayAddress(pcInterface, ipAddress, out _);
                    if (viaSubnet != null) return viaSubnet;
                }
            return null;
        }

        public ResponseMessage GoOffline(string softwarePath)
        {
            // 未找到工程、PLC 或在线服务时，不能报告下线成功。
            if (_session.IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "DisconnectOnlinePlc: no project is open, so nothing was taken offline. "
                    + "Call ConnectPortal + OpenProject (or AttachOpenProject) first.");
            }

            var plcSoftware = _session.ResolvePlc(softwarePath, PlcAccess.Write);
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

                provider.GoOffline();
                return new ResponseMessage { Message = $"'{softwarePath}' is now offline.", Meta = ResponseMeta.Basic(true) };
            }
            catch (Exception ex)
            {
                _session.Logger?.LogError(ex, "GoOffline failed for {SoftwarePath}", softwarePath);
                return new ResponseMessage { Message = $"GoOffline error: {ex.Message}" };
            }
        }

        // Take EVERY PLC in the project offline, not just one. A UI-initiated online session,
        // or a project with more than one online PLC, will NOT release the compile/export/import
        // lock when GoOffline is called for a single softwarePath. This iterates all PLCs using
        // the same provider resolution as GoOffline, so the agent never has to ask the user to
        // toggle online/offline in the TIA UI. Returns per-PLC before/after state.
        public JsonObject GoOfflineAll()
        {
            var plcs = new JsonArray();
            if (_session.IsProjectNull())
                return new JsonObject { ["message"] = "No project open.", ["allOffline"] = false, ["v4Rejection"] = "PROJECT_NOT_BOUND", ["plcs"] = plcs };

            bool allOffline = true;
            foreach (var plc in _session.GetAllPlcSoftware())
            {
                var entry = new JsonObject { ["name"] = plc.Name };
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

            return new JsonObject
            {
                ["message"] = $"DisconnectOnlinePlcs: {plcs.Count} PLC(s) processed; allOffline={allOffline}.",
                ["allOffline"] = allOffline,
                ["plcs"] = plcs
            };
        }

        public ResponseCompare CompareSoftwareToOnline(string softwarePath, int maxDepth = 4, int maxEntries = 200)
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

                var compareMethod = plcSoftware.GetType().GetMethod("CompareToOnline", Type.EmptyTypes);
                if (compareMethod == null)
                    return new ResponseCompare { Message = "CompareToOnline method not found on PlcSoftware (TIA Openness API mismatch)." };

                var result = compareMethod.Invoke(plcSoftware, null);
                if (result == null)
                    return new ResponseCompare { Message = "CompareToOnline returned null.", IsOnline = true };

                var rootElement = result.GetType().GetProperty("RootElement")?.GetValue(result);
                var entries = new List<CompareEntry>();
                var summary = new Dictionary<string, int>();
                bool truncated = WalkCompareTree(rootElement, "", 0, maxDepth, maxEntries, entries, summary);

                return new ResponseCompare
                {
                    Message = $"Compare complete: {entries.Count} differences" + (truncated ? " (truncated)." : "."),
                    IsOnline = true,
                    Entries = entries.ToArray(),
                    Summary = summary,
                    Truncated = truncated
                };
            }
            // 比对失败必须抛出原生错误，不能返回空差异或声称在线。
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                _session.Logger?.LogError(tie.InnerException, "CompareSoftwareToOnline failed for {SoftwarePath}", softwarePath);
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"CompareSoftwareToOnline failed for '{softwarePath}': {tie.InnerException.Message} "
                    + "NO comparison result is available — do not read this as 'offline and online are identical'. "
                    + "Go online first (ConnectOnlinePlc) and retry.", null, tie.InnerException);
            }
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _session.Logger?.LogError(ex, "CompareSoftwareToOnline failed for {SoftwarePath}", softwarePath);
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"CompareSoftwareToOnline failed for '{softwarePath}': {ex.Message} "
                    + "NO comparison result is available — do not read this as 'identical'.", null, ex);
            }
        }

        private static bool WalkCompareTree(object? element, string path, int depth, int maxDepth, int maxEntries,
            List<CompareEntry> entries, Dictionary<string, int> summary)
        {
            if (element == null) return false;
            if (entries.Count >= maxEntries) return true;

            var t = element.GetType();
            string leftName = t.GetProperty("LeftName")?.GetValue(element)?.ToString() ?? string.Empty;
            string rightName = t.GetProperty("RightName")?.GetValue(element)?.ToString() ?? string.Empty;
            string status = t.GetProperty("ComparisonResult")?.GetValue(element)?.ToString() ?? "Unknown";
            string? details = t.GetProperty("DetailedInformation")?.GetValue(element)?.ToString();

            string displayName = !string.IsNullOrEmpty(leftName) ? leftName : rightName;
            string fullPath = string.IsNullOrEmpty(path)
                ? displayName
                : (string.IsNullOrEmpty(displayName) ? path : path + "/" + displayName);

            if (summary.ContainsKey(status)) summary[status]++;
            else summary[status] = 1;

            // Skip "Equal"/"None" entries; only report differences
            // 实测出现过的真差异状态："RightMissing"（只在离线侧，没下载过）、
            // "FolderContentsDifferent"；同族还有 "LeftMissing" / "ObjectsDifferent"。
            // 以 "Identical" 结尾的状态是**一致**，漏掉这一条会把一致报成
            // 「Compare complete: N differences」，诱发一次不必要的下载。
            bool isDifference = status != "Equal" && status != "None" && status != "Unknown"
                && !status.EndsWith("Identical", StringComparison.Ordinal);
            if (depth > 0 && isDifference)
            {
                entries.Add(new CompareEntry
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

            var children = t.GetProperty("Elements")?.GetValue(element);
            if (children is IEnumerable enumerable)
            {
                foreach (var child in enumerable)
                {
                    if (WalkCompareTree(child, fullPath, depth + 1, maxDepth, maxEntries, entries, summary))
                        return true;
                }
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

        public ResponseDownload DownloadToPlc(
            string softwarePath,
            bool consistentBlocksOnly = true,
            bool keepActualValues = true,
            bool startAfterDownload = true,
            bool stopBeforeDownload = true,
            string? password = null,
            string? pgPcInterface = null,
            string? targetIpAddress = null,
            string userManagementMode = "keep",
            string promptAnswersJson = "{}",
            string? moduleAccessPassword = null,
            string? blockBindingPassword = null,
            string? masterSecretPassword = null,
            string rhTarget = "",
            bool trustDeviceCertificate = true)
        {
            var legitimation = new JsonObject { ["trustDeviceCertificate"] = trustDeviceCertificate };   // TLS prompt record
            _session.Logger?.LogInformation(
                "DownloadPlc: softwarePath={SoftwarePath} consistentOnly={C} keepDB={K} start={S} stop={T} hasPassword={P} pgPc={I} targetIp={A} userMgmt={U}",
                softwarePath, consistentBlocksOnly, keepActualValues, startAfterDownload, stopBeforeDownload, !string.IsNullOrEmpty(password), pgPcInterface, targetIpAddress, userManagementMode);
            try { rhTarget = EngineeringCredentialRules.ValidateRhTarget(rhTarget); }
            catch (ArgumentException ex) { return new ResponseDownload { Ok = false, Message = ex.Message, Errors = new[] { ex.Message } }; }

            if (_session.IsProjectNull())
                return new ResponseDownload { Ok = false, Message = "No project open.", Meta = new JsonObject { ["v4Rejection"] = "PROJECT_NOT_BOUND" } };

            DownloadPromptPolicy promptPolicy;
            try
            {
                promptPolicy = BuildDownloadPromptPolicy(consistentBlocksOnly, keepActualValues, startAfterDownload, stopBeforeDownload,
                    userManagementMode, promptAnswersJson, moduleAccessPassword ?? password, blockBindingPassword, masterSecretPassword);
            }
            catch (ArgumentException ex)
            {
                return new ResponseDownload { Ok = false, Message = "Invalid download prompt parameters: " + ex.Message, Errors = new[] { ex.Message } };
            }

            var plcSoftware = _session.ResolvePlc(softwarePath, PlcAccess.Write);
            if (plcSoftware == null)
                return new ResponseDownload { Ok = false, Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix() };

            // Declared outside the try so the catch can report which PG/PC route was used.
            DownloadRouteSelection? routeDiagnostics = null;

            try
            {
                var downloadProvider = _session.ResolvePlcService<DownloadProvider>(softwarePath, plcSoftware);
                if (downloadProvider == null)
                    return new ResponseDownload
                    {
                        Ok = false,
                        Message = "DownloadProvider service not available for this PLC. Ensure hardware configuration has network settings."
                    };

                object? configuration = downloadProvider.Configuration;
                if (configuration == null)
                    return new ResponseDownload
                    {
                        Ok = false,
                        Message = "No connection configuration found. Configure the PLC's PROFINET/IP address in hardware configuration first."
                    };

                using var legitimationScope = AttachOnlineLegitimationHandler(configuration, password, legitimation, trustDeviceCertificate);

                DownloadConfigurationDelegate preDelegate = (config) => ApplyDownloadPrompt(config, promptPolicy);
                DownloadConfigurationDelegate postDelegate = (config) => ApplyDownloadPrompt(config, promptPolicy);

                // V21 fix: ConnectionConfiguration does NOT implement IConfiguration, but a
                // ConfigurationTargetInterface (Modes -> PcInterfaces -> TargetInterfaces) DOES.
                // Select a target interface (applying its route) and pass THAT to Download();
                // fall back to the raw configuration if no route is selectable.
                routeDiagnostics = SelectDownloadRoute(configuration, pgPcInterface, targetIpAddress);
                if (routeDiagnostics.Error != null)
                    return new ResponseDownload
                    {
                        Ok = false,
                        Message = routeDiagnostics.Error,
                        Errors = new[] { routeDiagnostics.Error }
                    };

                object? downloadConfig = routeDiagnostics.Configuration ?? configuration;
                _session.Logger?.LogInformation("DownloadPlc: PG/PC route = {Route}", routeDiagnostics.Description);

                if (routeDiagnostics.Address != null && routeDiagnostics.AddressSource == "created" && rhTarget.Length == 0
                    && routeDiagnostics.Target is IConfiguration targetConfiguration)
                {
                    // "Download Hardware and Software to a target with specific IP-Address" - the official overload for an
                    // address TIA has not seen on the target yet (PLCSIM Advanced instance before its first download, new CPU).
                    DownloadResult createdResult = downloadProvider.Download(targetConfiguration, routeDiagnostics.Address, preDelegate, postDelegate, DownloadOptions.Software);
                    return BuildDownloadResponse(createdResult, softwarePath, routeDiagnostics, promptPolicy, legitimation);
                }

                if (rhTarget.Length > 0)
                {
                    // R/H systems download to one CPU at a time through RHDownloadProvider (typed overloads; no reflection).
                    RHDownloadProvider rh = _session.ResolvePlcService<RHDownloadProvider>(softwarePath, plcSoftware)
                        ?? throw new PortalException(PortalErrorCode.NotFound, "RHDownloadProvider service not available on this PLC (rhTarget applies to R/H systems only).");
                    var rhConfiguration = downloadConfig as IConfiguration ?? throw new PortalException(PortalErrorCode.InvalidState, "No applicable route (IConfiguration) for the R/H download; give pgPcInterface/targetIpAddress.");
                    DownloadResult rhResult = rhTarget == "primary"
                        ? rh.DownloadToPrimary(rhConfiguration, preDelegate, postDelegate, DownloadOptions.Software)
                        : rh.DownloadToBackup(rhConfiguration, preDelegate, postDelegate, DownloadOptions.Software);
                    var rhResponse = BuildDownloadResponse(rhResult, softwarePath, routeDiagnostics, promptPolicy, legitimation);
                    rhResponse.Message = "[R/H " + rhTarget + "] " + rhResponse.Message;
                    return rhResponse;
                }

                // Resolve the 4-arg overload Download(IConfiguration, pre, post, DownloadOptions)
                // and invoke via reflection (the parameter is typed IConfiguration).
                var downloadMethod = downloadProvider.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "Download") return false;
                        var p = m.GetParameters();
                        return p.Length == 4
                            && p[1].ParameterType.Name == "DownloadConfigurationDelegate";
                    });

                if (downloadMethod == null)
                    return new ResponseDownload
                    {
                        Ok = false,
                        Message = "Download(IConfiguration,…) method not found on DownloadProvider. TIA Portal version mismatch?"
                    };

                var rawResult = downloadMethod.Invoke(
                    downloadProvider,
                    new object[] { downloadConfig!, preDelegate, postDelegate, DownloadOptions.Software });

                if (rawResult is not DownloadResult result)
                    return new ResponseDownload { Ok = false, Message = "Download returned an unexpected result type." };

                return BuildDownloadResponse(result, softwarePath, routeDiagnostics, promptPolicy, legitimation);
            }
            catch (Exception ex)
            {
                // The Download call is invoked via reflection, so a real failure arrives wrapped in
                // TargetInvocationException ("调用的目标发生了异常"). Unwrap it so the caller sees the
                // actual reason (connection/route error, not-reachable CPU, etc.).
                var real = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null
                    ? tie.InnerException : ex;
                _session.Logger?.LogError(real, "DownloadPlc failed for {SoftwarePath}", softwarePath);

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
                foreach (var kv in legitimation) failureMeta[kv.Key] = kv.Value?.DeepClone();
                failureMeta["success"] = false;
                return new ResponseDownload
                {
                    Ok = false,
                    Message = $"Download failed: {real.Message}{routeHint}{promptPolicy.UnansweredSummary()}",
                    Errors = new[] { real.Message },
                    Meta = failureMeta
                };
            }
        }

        private static DownloadPromptPolicy BuildDownloadPromptPolicy(bool consistentBlocksOnly, bool keepActualValues, bool startAfterDownload,
            bool stopBeforeDownload, string userManagementMode, string promptAnswersJson, string? moduleAccessPassword, string? blockBindingPassword, string? masterSecretPassword)
        {
            if (!DownloadPromptPolicy.UserManagementModes.Contains(userManagementMode))
                throw new ArgumentException("userManagementMode must be keep / updateKeepPassword / resetToProject.");
            var policy = new DownloadPromptPolicy
            {
                ConsistentBlocksOnly = consistentBlocksOnly, KeepActualValues = keepActualValues, StartAfterDownload = startAfterDownload,
                StopBeforeDownload = stopBeforeDownload, UserManagementMode = userManagementMode,
                ModuleAccessPassword = moduleAccessPassword, BlockBindingPassword = blockBindingPassword, MasterSecretPassword = masterSecretPassword
            };
            foreach (var kv in DownloadPromptPolicy.ParseExplicitAnswers(promptAnswersJson)) policy.Explicit[kv.Key] = kv.Value;
            return policy;
        }

        // 每个提示对象按其真实形态应答：枚举型 CurrentSelection、布尔型 Checked 或 SetPassword(SecureString)。
        // 决策来自 DownloadPromptPolicy；无法应答的提示连同 TIA 的提示文本一起记录，永远不记录密码。
        private void ApplyDownloadPrompt(object config, DownloadPromptPolicy policy)
        {
            var type = config.GetType();
            var typeName = type.Name;
            string? message = null;
            // the official base classes carry the prompt text; the 43 concrete V21 prompt classes derive from them.
            try { message = config switch { DownloadConfiguration download => download.Message, UploadConfiguration upload => upload.Message, _ => type.GetProperty("Message")?.GetValue(config) as string }; } catch /* swallow(probe-optional): prompt text is diagnostic; a missing message must not prevent answering the prompt */ { }
            _session.Logger?.LogDebug("ApplyDownloadPrompt: {TypeName}", typeName);
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
                        if (config is SelectiveDeleteDownload selectiveDelete) selectiveDelete.CurrentSelection = (SelectiveDeleteDataSelections)Enum.Parse(typeof(SelectiveDeleteDataSelections), answer.Value!, ignoreCase: true);
                        else selection!.SetValue(config, Enum.Parse(selection.PropertyType, answer.Value!, ignoreCase: true));
                        break;
                    case DownloadPromptPolicy.AnswerKind.Checked:
                        // the Startdrive prompts are typed (V21 Siemens.Engineering.Startdrive.dll): the two upload checks derive from
                        // UploadConfiguration (not UploadCheckConfiguration; V21 only), the three download checks from DownloadCheckConfiguration.
#if !TIA_V20
                        if (config is OverrideTelegramMismatch telegramMismatch) telegramMismatch.Checked = answer.Value == "true";
                        else if (config is OverwriteOfflineConfiguration overwriteOffline) overwriteOffline.Checked = answer.Value == "true";
                        else
#endif
                        if (config is StartDriveDownloadCheckConfiguration startdriveCheck) startdriveCheck.Checked = answer.Value == "true";
                        else if (config is AcceptDownloadOfUnencryptedSensitiveData unencrypted) unencrypted.Checked = answer.Value == "true";
                        else if (config is ReplaceDownloadedData replaceDownloaded) replaceDownloaded.Checked = answer.Value == "true";
                        else if (config is DownloadCheckConfiguration check) check.Checked = answer.Value == "true";
                        else checkedProp!.SetValue(config, answer.Value == "true");
                        break;
                    case DownloadPromptPolicy.AnswerKind.Password:
                        var secret = typeName switch
                        {
                            "BlockBindingPassword" => policy.BlockBindingPassword,
                            "PlcMasterSecretPassword" => policy.MasterSecretPassword,
                            _ => policy.ModuleAccessPassword
                        };
                        var secure = new SecureString();
                        foreach (var c in secret!) secure.AppendChar(c);
                        secure.MakeReadOnly();
                        if (config is DownloadPasswordConfiguration downloadPassword) { answer.Note = "secureCommunication=" + downloadPassword.IsSecureCommunication; downloadPassword.SetPassword(secure); }
                        else if (config is UploadPasswordConfiguration uploadPassword) { answer.Note = "secureCommunication=" + uploadPassword.IsSecureCommunication; uploadPassword.SetPassword(secure); }
                        else type.GetMethod("SetPassword", new[] { typeof(SecureString) })!.Invoke(config, new object[] { secure });
                        break;
                }
                policy.Record(typeName, message, answer);
            }
            catch (Exception ex)
            {
                var real = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                _session.Logger?.LogWarning(real, "Download prompt {TypeName} could not be answered", typeName);
                policy.Record(typeName, message, new DownloadPromptPolicy.Answer { Kind = DownloadPromptPolicy.AnswerKind.Unanswered, Source = "unanswered", Note = "answer failed: " + real.Message });
            }
        }

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
            public string AddressSource = "";   // targetInterface | subnet | gateway | created
            public object? Target;              // the ConfigurationTargetInterface the address belongs to (for the 5-arg Download overload)
        }

        // Typed walk of one PC interface's subnets and gateways for an exact address (historical native observation: the route tree of a
        // PLCSIM Advanced target listed 192.168.0.1 only under PcInterface.Subnets["MCP_PN"].Addresses, never under 1 X1).
        private ConfigurationAddress? FindSubnetOrGatewayAddress(object? pcInterface, string ipAddress, out string source)
        {
            source = "";
            if (pcInterface is not ConfigurationPcInterface typed) return null;
            try
            {
                foreach (ConfigurationSubnet subnet in EngineeringGroupOperations.Items(typed.Subnets).Cast<ConfigurationSubnet>())
                {
                    foreach (ConfigurationAddress address in EngineeringGroupOperations.Items(subnet.Addresses).Cast<ConfigurationAddress>())
                        if (string.Equals(address.Address, ipAddress, StringComparison.OrdinalIgnoreCase)) { source = "subnet " + subnet.Name; return address; }
                    foreach (ConfigurationGateway gateway in EngineeringGroupOperations.Items(subnet.Gateways).Cast<ConfigurationGateway>())
                        foreach (ConfigurationAddress address in EngineeringGroupOperations.Items(gateway.Addresses).Cast<ConfigurationAddress>())
                            if (string.Equals(address.Address, ipAddress, StringComparison.OrdinalIgnoreCase)) { source = "gateway " + gateway.Name + " of subnet " + subnet.Name; return address; }
                }
            }
            catch /* swallow(native-fallback): an unavailable subnet or gateway address leaves target-interface address creation available */ { }
            return null;
        }

        // Official ConfigurationAddressComposition.Create(address) on a target interface (V20 and V21). Returns null when the
        // composition refuses (the exception text is handed back for the error message).
        private static ConfigurationAddress? TryCreateTargetAddress(object? target, string ipAddress, out string error)
        {
            error = "";
            if (target is not ConfigurationTargetInterface typed) { error = "target interface is not a ConfigurationTargetInterface"; return null; }
            try
            {
                var existing = typed.Addresses.Find(ipAddress);
                if (existing != null) return existing;
                return typed.Addresses.Create(ipAddress);
            }
            catch (Exception ex) { error = ex.Message; return null; }
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

        // Returns the IConfiguration to pass to Download(), or a selection carrying an Error when an
        // explicit filter matched nothing. Configuration stays null when no route exists at all —
        // the caller then falls back to the raw connection configuration (old behaviour).
        private DownloadRouteSelection SelectDownloadRoute(
            object? connectionConfiguration,
            string? pgPcInterface,
            string? targetIpAddress)
        {
            var selection = new DownloadRouteSelection();
            if (connectionConfiguration == null) return selection;

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
                                if (selection.Address == null && candidate is ConfigurationAddress typed && string.Equals(typed.Address, targetIpAddress, StringComparison.OrdinalIgnoreCase))
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

                // A subnet / gateway / created address is applied as such (ConnectionConfiguration.ApplyConfiguration(ConfigurationAddress))
                // and handed to Download / GoOnline as the IConfiguration; the target interface is kept for the 5-arg Download overload.
                // An address listed on the target interface keeps the field-verified path below (apply + hand over the target interface).
                if (selection.Address != null && selection.AddressSource != "targetInterface" && connectionConfiguration is ConnectionConfiguration typedConfiguration)
                {
                    bool applied = false;
                    try { applied = typedConfiguration.ApplyConfiguration(selection.Address); } catch /* swallow(native-fallback): the selected address is still passed to the native operation when applying it is not confirmed */ { }
                    selection.Configuration = selection.Address;
                    var route = pool.OrderByDescending(r => r.Score).First();
                    selection.Description = route.Describe() + " -> address " + selection.Address.Address + " (" + selection.AddressSource + (applied ? "" : "; not confirmed by ApplyConfiguration") + ")";
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

                foreach (var route in ranked)
                {
                    try
                    {
                        if (applyMethod?.Invoke(connectionConfiguration, new[] { route.Target }) is bool ok && ok)
                        {
                            selection.Configuration = route.Target;
                            selection.Description = route.Describe();
                            return selection;
                        }
                    }
                    catch /* swallow(native-fallback): try the next ranked route when this target cannot be applied */ { }
                }

                // Nothing applied cleanly — hand back the best-ranked target anyway (it IS an
                // IConfiguration). Mirrors the previous fallback to the first target.
                selection.Configuration = ranked[0].Target;
                selection.Description = ranked[0].Describe() + " (not confirmed by ApplyConfiguration)";
                return selection;
            }
            catch /* swallow(native-fallback): return the accumulated selection so the caller preserves its raw-configuration fallback */ { }
            return selection;
        }

        public ResponseCheckDownload CheckDownloadReadiness(string softwarePath)
        {
            var issues = new List<string>();

            if (_session.IsProjectNull())
                return new ResponseCheckDownload { Ready = false, Issues = new[] { "No project open." }, Meta = new JsonObject { ["v4Rejection"] = "PROJECT_NOT_BOUND" } };

            var plcSoftware = _session.GetPlcSoftware(softwarePath);
            if (plcSoftware == null)
                return new ResponseCheckDownload { Ready = false, Issues = new[] { $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix() } };

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
                isConsistent = EngineeringAuditLogic.Consistency(consistency.Select(x => x.Consistent), true);
                if (isConsistent == false) issues.Add("One or more root/unit blocks or types are inconsistent; compilation must be reviewed.");
                if (isConsistent == null) issues.Add("Compile consistency is unknown (no complete block/type evidence).");
            }
            catch (Exception ex) when (_session.RecoverableAuditError(ex)) { issues.Add("Consistency read failed: " + ex.GetBaseException().Message); }

            ScoreDownloadRoutes(routes, null);
            var routesJson = new JsonArray();
            foreach (var route in routes.OrderByDescending(r => r.Score))
                routesJson.Add(new JsonObject
                {
                    ["mode"] = route.ModeName,
                    ["pgPcInterface"] = route.PcInterfaceName,
                    ["pgPcInterfaceNumber"] = route.PcInterfaceNumber,
                    ["pgPcAddresses"] = string.Join(", ", route.PcAddresses),
                    ["targetInterface"] = route.TargetName,
                    ["targetAddresses"] = string.Join(", ", route.TargetAddresses),
                    ["preferred"] = route.Score > 0
                });

            bool? ready = EngineeringAuditLogic.DownloadReady(hasProvider, hasConfig, isConsistent, issues.Count != 0);
            return new ResponseCheckDownload
            {
                Ready = ready,
                HasDownloadProvider = hasProvider,
                HasConfiguration = hasConfig,
                IsConsistent = isConsistent,
                Message = ready == false ? $"PLC '{softwarePath}' has {issues.Count} readiness issue(s)."
                    : "Offline configuration checks passed; actual download readiness remains unknown.",
                Issues = issues.Count > 0 ? issues.ToArray() : null,
                Meta = new JsonObject
                {
                    ["success"] = true,   // the check itself ran; Ready carries the verdict
                    ["configurationReady"] = hasProvider && hasConfig,
                    ["consistencyScope"] = "root, software units and safety units: blocks and types",
                    ["unchecked"] = new JsonArray("deviceReachability", "accessAuthorization", "hardwareConsistency", "downloadPrompts"),
                    ["downloadRouteCount"] = routes.Count,
                    // Ordered best-first — the same ranking DownloadToPlc applies. preferred=true
                    // means the PG/PC adapter shares an IPv4 /24 with the CPU it has to reach.
                    ["downloadRoutes"] = routesJson,
                    ["note"] = "Override the automatic pick with DownloadPlc(pgPcInterface:…) or DownloadPlc(targetIpAddress:…)."
                }
            };
        }

        private ResponseDownload BuildDownloadResponse(
            DownloadResult result,
            string softwarePath,
            DownloadRouteSelection? route,
            DownloadPromptPolicy? prompts = null,
            JsonObject? legitimation = null)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            CollectDownloadMessages(result.Messages, errors, warnings);

            bool ok = result.State == DownloadResultState.Success
                   || result.State == DownloadResultState.Information
                   || result.State == DownloadResultState.Warning;

            var meta = new JsonObject
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
                foreach (var kv in prompts.Summary()) meta[kv.Key] = kv.Value?.DeepClone();
            if (legitimation != null)
                foreach (var kv in legitimation) meta[kv.Key] = kv.Value?.DeepClone();
            meta["success"] = ok;
            NativeResultState.Record(meta, result.State.ToString(), true, messages: new JsonArray(errors.Concat(warnings).Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()));

            return new ResponseDownload
            {
                Ok = ok,
                Message = $"Download {result.State}: {result.ErrorCount} error(s), {result.WarningCount} warning(s)." + (prompts?.UnansweredSummary() ?? string.Empty),
                State = result.State.ToString(),
                ErrorCount = result.ErrorCount,
                WarningCount = result.WarningCount,
                Errors = errors.Count > 0 ? errors.ToArray() : null,
                Warnings = warnings.Count > 0 ? warnings.ToArray() : null,
                Meta = meta
            };
        }

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

        private static JsonArray DescribePcInterfaces(IEnumerable<PcInterfacePick> picks)
            => new JsonArray(picks.Select(p => (JsonNode)new JsonObject { ["mode"] = p.ModeName, ["name"] = p.Name, ["number"] = p.Number, ["addresses"] = string.Join(", ", p.Addresses) }).ToArray());

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
            foreach (ConfigurationTargetInterface target in EngineeringGroupOperations.Items(typed.TargetInterfaces).Cast<ConfigurationTargetInterface>())
            {
                try { var created = target.Addresses.Find(targetAddress) ?? target.Addresses.Create(targetAddress); note = "created on target interface " + target.Name; return created; }
                catch (Exception ex) { errors.Add(target.Name + ": " + ex.Message); }
            }
            foreach (ConfigurationSubnet subnet in EngineeringGroupOperations.Items(typed.Subnets).Cast<ConfigurationSubnet>())
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
        }

        private ConnectionConfiguration ResolveScanConfiguration(string softwarePath, JsonObject meta)
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

        public ResponseMessage ScanAccessibleDevices(string pgPcInterface = "", string softwarePath = "", int offset = 0, int limit = 100)
            => _session.RunHmiStepTool("ScanAccessibleDevices", meta => {
                if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset >= 0 and 1 <= limit <= 500 required.");
                var configuration = ResolveScanConfiguration(softwarePath, meta);
                var picks = EnumeratePcInterfaces(configuration);
                meta["pgPcInterfaces"] = DescribePcInterfaces(picks);
                var pick = PickPcInterface(picks, pgPcInterface);
                meta["pgPcInterface"] = new JsonObject { ["mode"] = pick.ModeName, ["name"] = pick.Name, ["number"] = pick.Number };
                if (pick.Interface is not ConfigurationPcInterface typed) throw new NotSupportedException("Selected PG/PC interface is not a ConfigurationPcInterface.");
                var devices = typed.GetAccessibleDevices() ?? new List<ConfigurationAccessibleDevice>();
                meta["apiCallSuccess"] = true;
                var rows = devices.Select(d => (JsonNode)new JsonObject { ["name"] = d.Name, ["address"] = d.Address, ["macAddress"] = d.MACAddress, ["deviceSeries"] = d.DeviceSeries }).ToList();
                meta["total"] = rows.Count; meta["offset"] = offset; meta["limit"] = limit;
                meta["devices"] = new JsonArray(rows.Skip(offset).Take(limit).ToArray());
                meta["dataComplete"] = offset + limit >= rows.Count;
                return rows.Count == 0 ? "Network scan returned no accessible devices on the selected PG/PC interface." : $"{rows.Count} accessible device(s) found by a live network scan; no project change.";
            });

        public ResponseMessage UploadStationFromPlc(string targetIpAddress, string pgPcInterface = "", string password = "", string promptAnswersJson = "{}", bool confirmUpload = false, bool dryRun = true)
            => _session.RunHmiStepTool("UploadStationFromPlc", meta => {
                if (string.IsNullOrWhiteSpace(targetIpAddress)) throw new ArgumentException("targetIpAddress is required (exact address as listed by ScanAccessibleDevices).");
                var policy = BuildDownloadPromptPolicy(true, true, false, false, "keep", promptAnswersJson, string.IsNullOrEmpty(password) ? null : password, null, null);
                var provider = _session.CurrentProject!.GetService<StationUploadProvider>() ?? throw new NotSupportedException("StationUploadProvider service unavailable on this project/version.");
                var picks = EnumeratePcInterfaces(provider.Configuration);
                var pick = PickPcInterface(picks, pgPcInterface);
                var seen = new List<string>();
                var address = FindTargetAddress(pick, targetIpAddress, seen);
                meta["pgPcInterface"] = new JsonObject { ["mode"] = pick.ModeName, ["name"] = pick.Name, ["number"] = pick.Number };
                meta["knownTargetAddresses"] = string.Join(", ", seen.Distinct());
                string addressSource = "route tree";
                if (address == null) { address = CreateScannedAddress(pick, targetIpAddress, out addressSource); meta["addressCreation"] = addressSource; }
                // Historical native observation: ConfigurationAddressComposition.Create takes IP addresses
                // only ("'02-C0-A8-00-C8-00' does not specify a valid address"); the official page creates "192.68.0.1". A MAC from the scan is
                // therefore never a valid target, and a virtual PLC that has not been downloaded to (IP 0.0.0.0) cannot be uploaded from.
                if (address == null)
                    throw new PortalException(PortalErrorCode.NotFound, "Target address not present on the selected PG/PC interface and not creatable from the network scan (" + addressSource + "). "
                        + (LooksLikeMacAddress(targetIpAddress)
                            ? "'" + targetIpAddress + "' is a MAC address: ConfigurationAddressComposition.Create accepts IP addresses only, so pass the device's IP as listed by ScanAccessibleDevices (a PLC that shows only a MAC, e.g. a PLCSIM Advanced instance before its first download, has no IP yet - assign one by downloading first)."
                            : "Run ScanAccessibleDevices on the same interface first; only the exact IP address listed there is accepted."));
                meta["targetAddressSource"] = addressSource;
                meta["targetAddress"] = address.Address; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                meta["devicesBefore"] = _session.CurrentProject.Devices.Count;
                if (dryRun) return "Station upload preview: provider, PG/PC interface and target address resolved (" + addressSource + "); " + (addressSource == "route tree" ? "no PLC contact" : "only a DCP network scan was sent") + ", no project change.";
                if (!confirmUpload) throw new InvalidOperationException("Station upload adds a new device to the project from the live PLC; set confirmUpload=true to execute.");
                using var access = _session.AcquireHmiEditAccess();
                using var legitimationScope = AttachOnlineLegitimationHandler(provider.Configuration, password, meta, true);   // TLS trust prompt of FW >= 2.9 CPUs
                meta["mayHaveChanged"] = true;
                UploadConfigurationDelegate handler = config => ApplyDownloadPrompt(config, policy);
                var result = provider.StationUpload(address, handler);
                meta["apiCallSuccess"] = true;
                foreach (var kv in policy.Summary()) meta[kv.Key] = kv.Value?.DeepClone();
                var errors = new List<string>(); var warnings = new List<string>();
                CollectDownloadMessages(result?.Messages, errors, warnings);
                meta["uploadState"] = result?.State.ToString(); meta["errorCount"] = result?.ErrorCount; meta["warningCount"] = result?.WarningCount;
                meta["errors"] = new JsonArray(errors.Select(e => (JsonNode)e).ToArray()); meta["warnings"] = new JsonArray(warnings.Select(w => (JsonNode)w).ToArray());
                var station = result?.UploadedStation;
                NativeResultState.Record(meta, result?.State.ToString(), true, messages: new JsonArray(errors.Concat(warnings).Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()));
                meta["uploadedStation"] = station?.Name; meta["devicesAfter"] = _session.CurrentProject.Devices.Count;
                bool ok = result != null && result.State != UploadResultState.Error && station != null && _session.CurrentProject.Devices.Any(d => d.Name == station.Name);
                meta["operationSuccess"] = ok; meta["dataComplete"] = false;
                if (!ok) throw new InvalidOperationException("Station upload did not yield a verified device: " + (result?.State.ToString() ?? "no result") + policy.UnansweredSummary());
                return $"Station uploaded as device '{station!.Name}' and verified in the project; not saved, compiled or downloaded." + policy.UnansweredSummary();
            });

        private static bool LooksLikeMacAddress(string value)
            => System.Text.RegularExpressions.Regex.IsMatch((value ?? "").Trim(), "^([0-9A-Fa-f]{2}[-:]){5}[0-9A-Fa-f]{2}$");

        public ResponseMessage UploadDeviceParameters(string devicePathJson, string itemPathJson, string targetIpAddress, string pgPcInterface = "", string password = "", string promptAnswersJson = "{}", bool confirmUpload = false, bool dryRun = true)
            => _session.RunHmiStepTool("UploadDeviceParameters", meta => {
                if (string.IsNullOrWhiteSpace(targetIpAddress)) throw new ArgumentException("targetIpAddress is required.");
                var policy = BuildDownloadPromptPolicy(true, true, false, false, "keep", promptAnswersJson, string.IsNullOrEmpty(password) ? null : password, null, null);
                var owner = _session.ExactEngineeringHardware(devicePathJson, itemPathJson);
                // ParameterUploadProvider 只在 V21 PublicAPI 中存在；按名解析并经泛型 GetService 反射取得，V20 明确 NotSupported。
                var providerType = typeof(StationUploadProvider).Assembly.GetType("Siemens.Engineering.Upload.ParameterUploadProvider")
                    ?? throw new NotSupportedException("ParameterUploadProvider is not part of this TIA version's PublicAPI (V21 only).");
                if (owner is not IEngineeringServiceProvider serviceProvider) throw new NotSupportedException("Hardware object is not a service provider.");
                var getService = typeof(IEngineeringServiceProvider).GetMethod("GetService")!.MakeGenericMethod(providerType);
                var provider = getService.Invoke(serviceProvider, null) ?? throw new NotSupportedException("ParameterUploadProvider service unavailable on this hardware object.");
                var uploadMethod = providerType.GetMethod("ParameterUpload", new[] { typeof(IConfiguration), typeof(ConfigurationAddress), typeof(UploadConfigurationDelegate) })
                    ?? throw new NotSupportedException("ParameterUpload(IConfiguration, ConfigurationAddress, delegate) unavailable.");
                var providerConfiguration = providerType.GetProperty("Configuration")?.GetValue(provider);
                var route = SelectDownloadRoute(providerConfiguration, string.IsNullOrWhiteSpace(pgPcInterface) ? null : pgPcInterface, targetIpAddress);
                if (route.Error != null) throw new PortalException(PortalErrorCode.NotFound, route.Error);
                if (route.Configuration == null) throw new PortalException(PortalErrorCode.NotFound, "No PG/PC route to the target address; run ScanAccessibleDevices first.");
                ConfigurationAddress? address = route.Address;   // target interface, subnet / gateway or created address
                foreach (var candidate in _session.EnumerateReflectedProperty(route.Target ?? route.Configuration, "Addresses"))
                    if (address == null && string.Equals(_session.ReadReflectedString(candidate, "Address"), targetIpAddress, StringComparison.OrdinalIgnoreCase) && candidate is ConfigurationAddress typed) address = typed;
                if (address == null) throw new PortalException(PortalErrorCode.NotFound, "Exact target address not found on the selected route: " + route.Description);
                meta["route"] = route.Description; meta["targetAddress"] = address.Address; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                meta["before"] = EngineeringScalarProperties.Read(owner);
                if (dryRun) return "Parameter upload preview: provider, route and address resolved; no PLC contact, no project change.";
                if (!confirmUpload) throw new InvalidOperationException("Parameter upload overwrites offline hardware parameters from the live device; set confirmUpload=true to execute.");
                using var access = _session.AcquireHmiEditAccess();
                meta["mayHaveChanged"] = true;
                UploadConfigurationDelegate handler = config => ApplyDownloadPrompt(config, policy);
                if ((route.Target ?? route.Configuration) is not IConfiguration configuration) throw new NotSupportedException("Selected route is not an IConfiguration.");
                using var legitimationScope = AttachOnlineLegitimationHandler(providerConfiguration, password, meta, true);   // TLS trust prompt of FW >= 2.9 CPUs
                try { uploadMethod.Invoke(provider, new object[] { configuration, address, handler }); }
                catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null) { throw tie.InnerException; }
                meta["apiCallSuccess"] = true;
                foreach (var kv in policy.Summary()) meta[kv.Key] = kv.Value?.DeepClone();
                meta["after"] = EngineeringScalarProperties.Read(owner); meta["dataComplete"] = false;
                return "Parameter upload completed; compare before/after scalars for changes. Not saved, compiled or downloaded." + policy.UnansweredSummary();
            });

        public ResponseMessage DownloadPlcToFolder(string softwarePath, string destinationDirectory, string targetForSoftware = "CPU", bool overwriteOnMemoryCard = false,
            bool keepActualValues = true, string userManagementMode = "keep", string promptAnswersJson = "{}", bool confirmDownload = false, bool dryRun = true)
            => _session.RunHmiStepTool("DownloadPlcToFolder", meta => {
                if (!new[] { "CPU", "PlcSimulationAdvanced" }.Contains(targetForSoftware)) throw new ArgumentException("targetForSoftware must be CPU or PlcSimulationAdvanced.");
                if (!Path.IsPathRooted(destinationDirectory)) throw new ArgumentException("Absolute destinationDirectory required.");
                var directory = new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(destinationDirectory));
                if (directory.Exists && directory.EnumerateFileSystemInfos().Any()) throw new IOException("destinationDirectory must be new or empty; merging into existing content is refused.");
                var policy = BuildDownloadPromptPolicy(true, keepActualValues, false, false, userManagementMode, promptAnswersJson, null, null, null);
                policy.Explicit["TargetForSoftware"] = targetForSoftware;
                policy.Explicit["OverwriteOnMemoryCard"] = overwriteOnMemoryCard ? "Load" : "NoAction";
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                var provider = _session.ResolvePlcService<DownloadProvider>(softwarePath, plc) ?? throw new PortalException(PortalErrorCode.NotFound, "DownloadProvider unavailable for " + softwarePath);
                if (provider.GetType().GetMethod("Download", new[] { typeof(DirectoryInfo), typeof(DownloadConfigurationDelegate) }) == null) throw new NotSupportedException("Download(DirectoryInfo, delegate) unavailable on this version.");
                meta["dryRun"] = dryRun; meta["mayHaveWrittenFiles"] = false; meta["destination"] = directory.FullName; meta["targetForSoftware"] = targetForSoftware; meta["overwriteOnMemoryCard"] = overwriteOnMemoryCard;
                if (dryRun) return "Folder download preview: PLC, provider and destination validated; no files written.";
                if (!confirmDownload) throw new InvalidOperationException("Writing a memory-card image is an explicit action; set confirmDownload=true to execute.");
                if (!directory.Exists) directory.Create();
                meta["mayHaveWrittenFiles"] = true;
                DownloadConfigurationDelegate handler = config => ApplyDownloadPrompt(config, policy);
                var result = provider.Download(directory, handler);
                meta["apiCallSuccess"] = true;
                foreach (var kv in policy.Summary()) meta[kv.Key] = kv.Value?.DeepClone();
                var errors = new List<string>(); var warnings = new List<string>();
                CollectDownloadMessages(result?.Messages, errors, warnings);
                meta["downloadState"] = result?.State.ToString(); meta["errorCount"] = result?.ErrorCount; meta["warningCount"] = result?.WarningCount;
                meta["errors"] = new JsonArray(errors.Select(e => (JsonNode)e).ToArray()); meta["warnings"] = new JsonArray(warnings.Select(w => (JsonNode)w).ToArray());
                directory.Refresh();
                var files = directory.Exists ? directory.GetFiles("*", SearchOption.AllDirectories) : Array.Empty<FileInfo>();
                meta["filesWritten"] = files.Length; meta["bytesWritten"] = files.Sum(f => f.Length); meta["dataComplete"] = false;
                NativeResultState.Record(meta, result?.State.ToString(), false, messages: new JsonArray(errors.Concat(warnings).Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()));
                meta["targetFiles"] = new JsonArray(files.Select(f => (JsonNode)NativeResultState.FileRow(f.FullName)).ToArray());
                bool ok = result != null && result.State != DownloadResultState.Error && files.Length > 0;
                meta["operationSuccess"] = ok;
                if (!ok) throw new InvalidOperationException("Folder download did not produce a verified image: " + (result?.State.ToString() ?? "no result") + policy.UnansweredSummary());
                return $"Memory-card image written: {files.Length} file(s) in {directory.FullName}. Content semantics not verified; no PLC contacted." + policy.UnansweredSummary();
            });

        /// <summary>
        /// Subscribes the OnlineLegitimation handler of a ConnectionConfiguration for the duration of a GoOnline / Download /
        /// Upload call: password prompts (legacy and UMAC) are answered from the given credentials, and the TLS trust prompt of
        /// S7-1500 FW >= 2.9 CPUs (TlsVerificationConfiguration) is answered Trusted when trustDeviceCertificate is set. Returns
        /// an IDisposable that unsubscribes when disposed; callers MUST dispose to avoid handler leaks. Returns null when the
        /// configuration object is not a ConnectionConfiguration (no event to hook).
        /// </summary>
        private static IDisposable? AttachOnlineLegitimationHandler(object? configuration, string? password, JsonObject? meta, bool trustDeviceCertificate)
            => AttachOnlineLegitimationHandler(configuration, password, null, null, meta, trustDeviceCertificate);

        // besides the legacy password prompt, UMAC-protected PLCs raise OnlineAuthenticationConfiguration (user name +
        // password + UserType, IsSecureCommunication, GetSupportedAuthenticationTypes); the answered prompts are reported to meta.
        // Subscribe even without a password so the TLS trust decision is answered and recorded.
        // Historical PLCSIM Advanced Softbus observation (version/date not recorded): an unanswered TLS prompt
        // refuses the first connection; see docs/reference/real-machine-ledger.md.
        private static IDisposable? AttachOnlineLegitimationHandler(object? configuration, string? password, string? userName, string? userType, JsonObject? meta, bool trustDeviceCertificate)
        {
            if (configuration is not ConnectionConfiguration conn)
                return null;

            // Build the SecureString once. The same instance can be reused across multiple
            // legitimation prompts within the same call (e.g., read-then-write access).
            SecureString? secure = null;
            if (!string.IsNullOrEmpty(password))
            {
                secure = new SecureString();
                foreach (var c in password!) secure.AppendChar(c);
                secure.MakeReadOnly();
            }

            OnlineConfigurationDelegate handler = (cfg) =>
            {
                if (cfg is TlsVerificationConfiguration tls)
                {
                    var before = tls.CurrentSelection.ToString();
                    var apply = EngineeringCredentialRules.TlsSelectionToApply(trustDeviceCertificate, before);
                    if (apply != null) tls.CurrentSelection = TlsVerificationConfigurationSelection.Trusted;
                    if (meta != null)
                        meta["tlsVerification"] = new JsonObject
                        {
                            ["plcName"] = tls.PlcName, ["verificationInfo"] = tls.VerificationInfo,
                            ["selectionBefore"] = before, ["selectionAfter"] = tls.CurrentSelection.ToString(),
                            ["trustDeviceCertificate"] = trustDeviceCertificate,
                            ["note"] = apply != null ? "Certificate trusted for this call on the caller's decision (trustDeviceCertificate=true); the same prompt TIA shows in the UI." : trustDeviceCertificate ? "Already trusted." : "Left untrusted (trustDeviceCertificate=false); TIA refuses the connection."
                        };
                }
                else if (secure == null) return;
                else if (cfg is OnlineAuthenticationConfiguration auth)
                {
                    OnlineCredentials credentials = auth.OnlineCredentials;
                    var supported = new JsonArray();
                    try { foreach (AuthenticationType type in auth.GetSupportedAuthenticationTypes()) supported.Add(type.CurrentUserType.ToString()); } catch { /* swallow(enumerate-optional): supported authentication types are diagnostic; credentials still answer the prompt */ }
                    if (!string.IsNullOrEmpty(userName)) credentials.Name = userName;
                    if (!string.IsNullOrEmpty(userType)) credentials.Type = (UserType)Enum.Parse(typeof(UserType), userType);
                    else if (!string.IsNullOrEmpty(userName)) credentials.Type = UserType.ProjectUser;
                    credentials.SetPassword(secure);
                    if (meta != null) meta["onlineAuthentication"] = new JsonObject { ["isSecureCommunication"] = auth.IsSecureCommunication, ["supportedUserTypes"] = supported, ["userName"] = userName ?? "", ["userType"] = credentials.Type.ToString() };
                }
                else if (cfg is OnlinePasswordConfiguration pwdCfg)
                {
                    pwdCfg.SetPassword(secure);
#if TIA_V20
                    if (meta != null) meta["onlinePassword"] = new JsonObject { ["answered"] = true };
#else
                    if (meta != null) meta["onlinePassword"] = new JsonObject { ["isSecureCommunication"] = pwdCfg.IsSecureCommunication };
#endif
                }
            };
            conn.OnlineLegitimation += handler;
            return new HandlerScope(() => { conn.OnlineLegitimation -= handler; secure?.Dispose(); });
        }

        private sealed class HandlerScope : IDisposable
        {
            private Action? _detach;
            public HandlerScope(Action detach) { _detach = detach; }
            public void Dispose() { _detach?.Invoke(); _detach = null; }
        }


        // ---- transfer routes and R/H providers --------------------------------------------------------------------------------
        private static JsonArray AddressRows(ConfigurationAddressComposition addresses) => new JsonArray(EngineeringGroupOperations.Items(addresses).Cast<ConfigurationAddress>().Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["address"] = a.Address }).ToArray());

        public ResponseMessage ReadTransferRoutes(string softwarePath, int maxItems = 500)
            => _session.RunHmiStepTool("ListTransferRoutes", meta => {
                LibraryDeepLogic.ValidateBounds(1, maxItems);
                var plc = _session.GetPlcSoftware(softwarePath) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact PLC software not found: " + softwarePath + _session.AvailablePlcPathsSuffix());
                var download = _session.ResolvePlcService<DownloadProvider>(softwarePath, plc);
                meta["downloadProviderAvailable"] = download != null;
                int items = 0; bool truncated = false;
                if (download != null)
                {
                    ConnectionConfiguration configuration = download.Configuration;
                    var modes = new JsonArray(); meta["modes"] = modes;
                    foreach (ConfigurationMode mode in EngineeringGroupOperations.Items(configuration.Modes).Cast<ConfigurationMode>())
                    {
                        var modeRow = new JsonObject { ["name"] = mode.Name }; var interfaces = new JsonArray(); modeRow["pcInterfaces"] = interfaces; modes.Add(modeRow);
                        foreach (ConfigurationPcInterface pcInterface in EngineeringGroupOperations.Items(mode.PcInterfaces).Cast<ConfigurationPcInterface>())
                        {
                            if (++items > maxItems) { truncated = true; break; }
                            var row = new JsonObject { ["name"] = pcInterface.Name, ["number"] = pcInterface.Number };
                            try { row["addresses"] = AddressRows(pcInterface.Addresses); } catch (Exception ex) { row["addressesError"] = ex.GetBaseException().Message; }
                            try
                            {
                                row["subnets"] = new JsonArray(EngineeringGroupOperations.Items(pcInterface.Subnets).Cast<ConfigurationSubnet>().Select(s => (JsonNode)new JsonObject
                                {
                                    ["name"] = s.Name, ["addresses"] = AddressRows(s.Addresses),
                                    ["gateways"] = new JsonArray(EngineeringGroupOperations.Items(s.Gateways).Cast<ConfigurationGateway>().Select(g => (JsonNode)new JsonObject { ["name"] = g.Name, ["addresses"] = AddressRows(g.Addresses) }).ToArray())
                                }).ToArray());
                            }
                            catch (Exception ex) { row["subnetsError"] = ex.GetBaseException().Message; }
                            try { row["targetInterfaces"] = new JsonArray(EngineeringGroupOperations.Items(pcInterface.TargetInterfaces).Cast<ConfigurationTargetInterface>().Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["addresses"] = AddressRows(t.Addresses) }).ToArray()); }
                            catch (Exception ex) { row["targetInterfacesError"] = ex.GetBaseException().Message; }
                            interfaces.Add(row);
                        }
                        if (truncated) break;
                    }
                }
                // R/H systems expose the redundant providers instead of / next to the standard ones; the CPU on the reference project is not R/H.
                var rhDownload = _session.ResolvePlcService<RHDownloadProvider>(softwarePath, plc); meta["rhDownloadProviderAvailable"] = rhDownload != null;
                var rhOnline = _session.ResolvePlcService<RHOnlineProvider>(softwarePath, plc); meta["rhOnlineProviderAvailable"] = rhOnline != null;
                if (rhOnline != null) { try { meta["rhOnline"] = new JsonObject { ["primaryState"] = rhOnline.PrimaryState.ToString(), ["backupState"] = rhOnline.BackupState.ToString() }; } catch (Exception ex) { meta["rhOnlineError"] = ex.GetBaseException().Message; } }
                var onlineProvider = _session.ResolvePlcService<OnlineProvider>(softwarePath, plc); meta["onlineProviderAvailable"] = onlineProvider != null;
                if (onlineProvider != null) { try { meta["onlineState"] = onlineProvider.State.ToString(); } catch (Exception ex) { meta["onlineStateError"] = ex.GetBaseException().Message; } }
                meta["compileProviderNote"] = "Siemens.Engineering.Compiler.CompileProvider is internal in the V20/V21 PublicAPI (documented, not public); compilation goes through ICompilable.";
                meta["truncated"] = truncated; meta["apiCallSuccess"] = true; meta["dataComplete"] = !truncated;
                meta["scope"] = "ConnectionConfiguration route tree of the download provider (Modes -> PcInterfaces with Addresses / Subnets (Gateways) / TargetInterfaces) plus availability of RHDownloadProvider / RHOnlineProvider (with Primary/BackupState) / OnlineProvider. Read-only; nothing is applied.";
                return "Transfer routes read; no route applied, no modification.";
            });

    }
}
