using Microsoft.Extensions.Logging;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Online;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    // Historical native observations below lack a recorded PLCSIM version/date;
    // see docs/reference/real-machine-ledger.md for the native acceptance boundary.
    public partial class Portal
    {
        public ResponseOnlineState GetOnlineState(string softwarePath)
        {
            // 未连接工程或路径无效时，在线状态未经测量，不能报告为 Offline。
            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "GetOnlineState: no project is open, so the online state was NOT measured. "
                    + "Call Connect + OpenProject (or AttachToOpenProject) first.");
            }

            var plcSoftware = GetPlcSoftware(softwarePath);
            if (plcSoftware == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"GetOnlineState: PLC software not found at '{softwarePath}', so the online state was NOT measured."
                    + AvailablePlcPathsSuffix());
            }

            try
            {
                var provider = ResolvePlcService<OnlineProvider>(softwarePath, plcSoftware);
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
                _logger?.LogError(ex, "GetOnlineState failed for {SoftwarePath}", softwarePath);
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
            BaseLeftoversLogic.ValidateOnlineCredentials(userName ?? "", password ?? "", userType ?? "");
            rhTarget = BaseLeftoversLogic.ValidateRhTarget(rhTarget);
            var meta = new JsonObject { ["trustDeviceCertificate"] = trustDeviceCertificate };
            // 未连接工程或路径无效时，在线状态未经测量，不能报告为 Offline。
            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "GoOnline: no project is open, so the online state was NOT measured. "
                    + "Call Connect + OpenProject (or AttachToOpenProject) first.");
            }

            var plcSoftware = ResolvePlc(softwarePath, PlcAccess.Write);
            if (plcSoftware == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"GoOnline: PLC software not found at '{softwarePath}', so the online state was NOT measured."
                    + AvailablePlcPathsSuffix());
            }

            try
            {
                if (rhTarget.Length > 0)
                {
                    RHOnlineProvider rh = ResolvePlcService<RHOnlineProvider>(softwarePath, plcSoftware)
                        ?? throw new PortalException(PortalErrorCode.NotFound, "RHOnlineProvider service not available on this PLC (rhTarget applies to R/H systems only).");
                    using var rhScope = AttachOnlineLegitimationHandler(rh.Configuration, password, userName, userType, meta, trustDeviceCertificate);
                    // ConfigurationAddress has no public constructor: the address object comes from the route tree (target interfaces).
                    ConfigurationAddress? rhAddress = string.IsNullOrWhiteSpace(ipAddress) ? null : FindConfigurationAddress(rh.Configuration, ipAddress!);
                    if (!string.IsNullOrWhiteSpace(ipAddress) && rhAddress == null) _logger?.LogWarning("GoOnline R/H: no ConfigurationAddress {Ip} in the route tree; using the configured address", ipAddress);
#if TIA_V20
                    if (rhAddress != null) _logger?.LogWarning("GoOnline R/H: the V20 RHOnlineProvider has no address overload; using the configured address");
                    OnlineState rhState = rhTarget == "primary" ? rh.GoOnlineToPrimary() : rh.GoOnlineToBackup();
#else
                    OnlineState rhState = rhAddress == null
                        ? (rhTarget == "primary" ? rh.GoOnlineToPrimary() : rh.GoOnlineToBackup())
                        : (rhTarget == "primary" ? rh.GoOnlineToPrimary(rhAddress) : rh.GoOnlineToBackup(rhAddress));
#endif
                    var rhName = rhState.ToString();
                    meta["success"] = rhName == "Online";
                    return new ResponseOnlineState { State = rhName, IsOnline = rhName == "Online", IsReachable = rhName == "Online" || rhName == "Protected",
                        Message = BuildOnlineStateMessage(rhName, softwarePath) + $" (R/H {rhTarget}; primary={rh.PrimaryState}, backup={rh.BackupState})", Meta = meta };
                }
                var provider = ResolvePlcService<OnlineProvider>(softwarePath, plcSoftware);
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
                        return new ResponseOnlineState { State = "NotReachable", IsOnline = false, IsReachable = false, Message = "GoOnline not attempted: " + selection.Error, Meta = meta };
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
                _logger?.LogError(ex, "GoOnline failed for {SoftwarePath}", softwarePath);
                // Historical native observation: EngineeringTargetInvocationException carries only "Error when calling method 'GoOnline'" - the
                // reason ("Incompatible" against a never-downloaded PLCSIM instance) sits in the inner chain; surface it and the state TIA holds.
                var chain = new List<string>();
                for (var e = ex; e != null && chain.Count < 6; e = e.InnerException) if (!string.IsNullOrWhiteSpace(e.Message) && !chain.Contains(e.Message)) chain.Add(e.Message);
                string stateNow = "";
                try { stateNow = ResolvePlcService<OnlineProvider>(softwarePath, plcSoftware)?.State.ToString() ?? ""; } catch (Exception) { /* swallow(probe-optional): a failed state probe must preserve the original GoOnline failure */ }
                meta["success"] = false; meta["error"] = string.Join(" <- ", chain);
                if (stateNow.Length > 0) meta["onlineStateAfter"] = stateNow;
                var hint = stateNow == "Incompatible" ? " TIA reports the connection as Incompatible (device / firmware / program mismatch): download first (DownloadToPlc), then go online." : "";
                return new ResponseOnlineState { State = stateNow.Length > 0 && stateNow != "Online" ? stateNow : "NotReachable", IsOnline = false, IsReachable = stateNow == "Incompatible" || stateNow == "Protected", Message = $"GoOnline failed: {string.Join(" <- ", chain)}{hint}", Meta = meta };
            }
        }

        // Typed walk of Modes -> PcInterfaces -> TargetInterfaces -> Addresses for an exact IP (R/H online targets).
        private static ConfigurationAddress? FindConfigurationAddress(ConnectionConfiguration configuration, string ipAddress)
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
            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "GoOffline: no project is open, so nothing was taken offline. "
                    + "Call Connect + OpenProject (or AttachToOpenProject) first.");
            }

            var plcSoftware = ResolvePlc(softwarePath, PlcAccess.Write);
            if (plcSoftware == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"GoOffline: PLC software not found at '{softwarePath}', so nothing was taken offline."
                    + AvailablePlcPathsSuffix());
            }

            try
            {
                var provider = ResolvePlcService<OnlineProvider>(softwarePath, plcSoftware);
                if (provider == null)
                {
                    throw new PortalException(PortalErrorCode.OpennessError,
                        $"GoOffline: OnlineProvider service is not available on '{softwarePath}', "
                        + "so the offline transition was NOT performed. Any live online session is still open — "
                        + "disconnect it in the TIA Portal UI before compiling or exporting.");
                }

                provider.GoOffline();
                return new ResponseMessage { Message = $"'{softwarePath}' is now offline." };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "GoOffline failed for {SoftwarePath}", softwarePath);
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
            if (IsProjectNull())
                return new JsonObject { ["message"] = "No project open.", ["allOffline"] = true, ["plcs"] = plcs };

            bool allOffline = true;
            foreach (var plc in GetAllPlcSoftware())
            {
                var entry = new JsonObject { ["name"] = plc.Name };
                try
                {
                    var provider = ResolvePlcService<OnlineProvider>(plc.Name, plc);
                    entry["before"] = provider?.State.ToString() ?? "Unknown";
                    provider?.GoOffline();
                    var after = provider?.State.ToString() ?? "Unknown";
                    entry["after"] = after;
                    entry["ok"] = after != "Online";
                    if (after == "Online") allOffline = false;
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
                ["message"] = $"GoOfflineAll: {plcs.Count} PLC(s) processed; allOffline={allOffline}.",
                ["allOffline"] = allOffline,
                ["plcs"] = plcs
            };
        }

        public ResponseCompare CompareSoftwareToOnline(string softwarePath, int maxDepth = 4, int maxEntries = 200)
        {
            // 缺少工程或 PLC 时没有比对结果，不能解释为在线离线一致。
            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "CompareSoftwareToOnline: no project is open, so NO comparison was performed. "
                    + "Do not read this as 'offline and online are identical'. "
                    + "Call Connect + OpenProject (or AttachToOpenProject) first.");
            }

            var plcSoftware = GetPlcSoftware(softwarePath);
            if (plcSoftware == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"CompareSoftwareToOnline: PLC software not found at '{softwarePath}', "
                    + "so NO comparison was performed. Do not read this as 'identical'."
                    + AvailablePlcPathsSuffix());
            }

            try
            {
                // Note: don't gate on OnlineProvider here. The provider service is sometimes
                // unavailable on PlcSoftware even when the PLC is online via the TIA Portal UI
                // (depends on hardware variant / project layout). Let CompareToOnline itself
                // throw if the connection isn't actually live — the exception path below
                // captures that with the original TIA error message.
                var provider = ResolvePlcService<OnlineProvider>(softwarePath, plcSoftware);
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
                _logger?.LogError(tie.InnerException, "CompareSoftwareToOnline failed for {SoftwarePath}", softwarePath);
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"CompareSoftwareToOnline failed for '{softwarePath}': {tie.InnerException.Message} "
                    + "NO comparison result is available — do not read this as 'offline and online are identical'. "
                    + "Go online first (GoOnline) and retry.", null, tie.InnerException);
            }
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "CompareSoftwareToOnline failed for {SoftwarePath}", softwarePath);
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
                "Offline" => $"'{softwarePath}' is offline. Call GoOnline first.",
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
