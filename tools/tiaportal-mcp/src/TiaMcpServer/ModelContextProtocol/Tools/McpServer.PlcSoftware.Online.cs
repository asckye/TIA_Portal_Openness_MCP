using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcpServer.Siemens;


namespace TiaMcpServer.ModelContextProtocol
{
    // Partial: plc software. Family file split out of McpServer.PlcSoftware.cs (2.8.0); behavior unchanged.
    public static partial class McpServer
    {
        #region plc software - Online

        [McpServerTool(Name = "GetOnlineState"), Description(
            "[L1][Category:PLC-Online][PreCondition:Connect+OpenProject]" +
            " Read the current online connection state of a PLC (Offline/Connecting/Online/Incompatible/NotReachable/Protected/Disconnecting)." +
            " Does NOT change state — purely a read operation." +
            " Use before GoOnline to check current state, or after DownloadToPlc to verify the CPU is reachable." +
            " State=Online means the PC is communicating with the physical CPU." +
            " State=Incompatible means online but firmware/config mismatch — download required." +
            " State=NotReachable means network or IP configuration issue." +
            " NOTE: This reports Openness connection state, NOT the CPU operating mode (RUN/STOP)." +
            " The TIA Portal public API does not expose CPU operating mode — check the CPU front panel LEDs or HMI for RUN/STOP status.")]
        public static ResponseOnlineState GetOnlineState(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath)
        {
            try
            {
                return Portal.GetOnlineState(softwarePath);
            }
            catch (PortalException pex)
            {
                // 路径解析不到是调用方的参数问题，不是服务器内部意外错误。
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error reading online state for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GoOnline"), Description(
            "[L1][Category:PLC-Online][ONLINE][PreCondition:Connect+OpenProject]" +
            " Establish an online connection from TIA Portal to the physical PLC." +
            " Required before DownloadToPlc to confirm reachability, or for future online monitoring tools." +
            " Returns State=Online on success." +
            " If ipAddress is omitted, uses the IP address configured in the project's hardware configuration." +
            " If ipAddress is provided, the matching ConfigurationAddress of the route tree (target interface, subnet or gateway; created on the target interface when TIA has not seen it yet) is applied and GoOnline(ConfigurationAddress) is used - this is also how a PLCSIM Advanced instance is reached (pgPcInterface 'PLCSIM Virtual Ethernet Adapter' / 'PLCSIM')." +
            " Common failures: NotReachable (wrong IP / no cable), Protected (CPU requires authentication — supply password), Incompatible (firmware mismatch)." +
            " S7-1500 FW >= 2.9 CPUs (incl. PLCSIM Advanced) ask for certificate trust on the first contact (TlsVerificationConfiguration); trustDeviceCertificate=true (default) answers Trusted - the same prompt TIA shows in the UI - and Meta.tlsVerification records PlcName / VerificationInfo / the selection; with false the connection is refused by TIA ('The device is not trusted').")]
        public static ResponseOnlineState GoOnline(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("ipAddress: optional IP address override, e.g. '192.168.1.10'. Leave empty to use the project's configured IP.")] string ipAddress = "",
            [Description("password: optional CPU access password. Required when the CPU has read/write protection configured. Leave empty for unprotected CPUs.")] string password = "",
            [Description("userName: optional user for UMAC-protected PLCs (answers OnlineAuthenticationConfiguration with password); leave empty for legacy password-only protection.")] string userName = "",
            [Description("userType: optional OnlineCredentials.Type (None/AnonymousUser/GlobalUser/ProjectUser/SingleSignOnUser/PasswordOnly); default ProjectUser when userName is given.")] string userType = "",
            [Description("rhTarget: empty for standard CPUs; primary or backup goes online to that CPU of an R/H system through RHOnlineProvider.")] string rhTarget = "",
            [Description("pgPcInterface: optional PG/PC adapter name substring (as listed by ReadTransferRoutes / ScanAccessibleDevices, e.g. 'PLCSIM Virtual Ethernet Adapter'); with ipAddress the route is applied before going online (ConnectionConfiguration.ApplyConfiguration).")] string pgPcInterface = "",
            [Description("trustDeviceCertificate: true (default) answers the TLS certificate prompt of FW >= 2.9 CPUs with Trusted for this call; false leaves it unanswered and TIA refuses the connection.")] bool trustDeviceCertificate = true)
        {
            try
            {
                return Portal.GoOnline(
                    softwarePath,
                    string.IsNullOrWhiteSpace(ipAddress) ? null : ipAddress,
                    string.IsNullOrWhiteSpace(password) ? null : password,
                    string.IsNullOrWhiteSpace(userName) ? null : userName,
                    string.IsNullOrWhiteSpace(userType) ? null : userType,
                    rhTarget ?? "",
                    string.IsNullOrWhiteSpace(pgPcInterface) ? null : pgPcInterface,
                    trustDeviceCertificate);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error going online for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GoOffline"), Description(
            "[L1][Category:PLC-Online][PreCondition:Connect+OpenProject]" +
            " Disconnect the online session between TIA Portal and the physical PLC." +
            " Safe to call even if not currently online. Always go offline when monitoring or download is complete.")]
        public static ResponseMessage GoOffline(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath)
        {
            try
            {
                return Portal.GoOffline(softwarePath);
            }
            catch (PortalException pex)
            {
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error going offline for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GoOfflineAll"), Description(
            "[L1][Category:PLC-Online][PreCondition:Connect+OpenProject]" +
            " Take EVERY PLC in the open project offline in one call and report each PLC's before/after online state." +
            " Use this whenever CompileSoftware/Export*/Import* is blocked by 'operation not permitted in online mode':" +
            " a UI-initiated online session or a second online PLC is NOT released by GoOffline on a single softwarePath." +
            " Fully autonomous — never ask the user to toggle online/offline in the TIA UI, and never OCR the toolbar.")]
        public static ResponseJsonReport GoOfflineAll()
        {
            try
            {
                var data = Portal.GoOfflineAll();
                bool all = data["allOffline"]?.GetValue<bool>() ?? false;
                return new ResponseJsonReport
                {
                    Ok = all,
                    Message = data["message"]?.ToString() ?? "GoOfflineAll completed.",
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = all }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"GoOfflineAll failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetDeviceIpAddress"), Description(
            "[L1][Category:Hardware][PreCondition:Connect+OpenProject]" +
            " Read a device's configured IP address straight from the TIA project (Openness PROFINET node) —" +
            " NOT by probing the CPU over S7 and NOT by exporting/parsing AML. Returns the primary IE IP plus all network nodes" +
            " (address, subnet, type). This is the correct, fast way to discover a PLC's IP before GoOnline/ReadPlcLiveValuesS7.")]
        public static ResponseJsonReport GetDeviceIpAddress(
            [Description("devicePath: device name from GetProjectTree, e.g. 'PLC_1'.")] string devicePath)
        {
            try
            {
                var data = Portal.GetDeviceIpAddress(devicePath);
                bool found = data["found"]?.GetValue<bool>() ?? false;
                var ip = data["ipAddress"]?.ToString() ?? string.Empty;
                return new ResponseJsonReport
                {
                    Ok = found,
                    Message = found
                        ? $"{devicePath} IP: {(string.IsNullOrEmpty(ip) ? "(no address configured on any node)" : ip)}"
                        : (data["message"]?.ToString() ?? "Device not found."),
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = found }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"GetDeviceIpAddress failed for '{devicePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetProjectTopology"), Description(
            "[L1][Category:Hardware][PreCondition:Connect+OpenProject]" +
            " One-shot, read-only project topology from Openness: every device with its network nodes (IP, subnet, node type)." +
            " Call this early to understand the project's devices and subnets at a glance, instead of probing S7 or parsing AML.")]
        public static ResponseJsonReport GetProjectTopology()
        {
            try
            {
                var data = Portal.GetProjectTopology();
                int count = data["deviceCount"]?.GetValue<int>() ?? 0;
                return new ResponseJsonReport
                {
                    Ok = count > 0,
                    Message = $"Project topology: {count} device(s).",
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = count > 0 }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"GetProjectTopology failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DumpDeviceAttributes"), Description(
            "[L2][Category:Hardware][PreCondition:Connect+OpenProject]" +
            " Read-only inventory of EVERY Openness attribute exposed on a device's items (CPU, modules, interfaces, ports):" +
            " name, access mode (read-only vs read/write), current value, value type." +
            " Run this ONCE per CPU/firmware to learn what is actually exposed, then drive hardware reads/writes from that" +
            " ground truth instead of guessing attribute names. Optional nameFilter narrows to attributes whose name contains" +
            " a substring; several alternatives can be given separated by '|' or ',' (e.g. 'protection', 'putget|webserver', 'ip'). NOTE: GetAttributeInfos() does not enumerate every gettable" +
            " attribute on all CPUs, so absence here means 'not enumerated', not a guaranteed 'no interface'.")]
        public static ResponseJsonReport DumpDeviceAttributes(
            [Description("devicePath: device name, CPU/program name, or full name (e.g. 'S7-1200 station_3', '安全PLC', 'S7-1500/ET200MP station_1').")] string devicePath,
            [Description("nameFilter: optional case-insensitive substring to narrow attribute names (e.g. 'protection'). Empty = all.")] string? nameFilter = null)
        {
            try
            {
                var data = Portal.DumpDeviceAttributes(devicePath, nameFilter);
                bool found = data["found"]?.GetValue<bool>() ?? false;
                int items = data["itemCount"]?.GetValue<int>() ?? 0;
                int attrs = data["totalAttributes"]?.GetValue<int>() ?? 0;
                int writable = data["writableAttributes"]?.GetValue<int>() ?? 0;
                return new ResponseJsonReport
                {
                    Ok = found,
                    Message = found
                        ? $"{devicePath}: {attrs} attribute(s) across {items} item(s) ({writable} writable)."
                        : (data["message"]?.ToString() ?? "Not found."),
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = found }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"DumpDeviceAttributes failed for '{devicePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetPutGetAccess"), Description(
            "[L2][Category:Hardware][PreCondition:Connect+OpenProject]" +
            " Read whether a CPU permits remote PUT/GET access — the precondition for ReadPlcLiveValuesS7 on DB areas." +
            " If enabled=false, S7 absolute reads of DBs will fail; enable with SetPutGetAccess (then hardware DownloadToPlc).")]
        public static ResponseJsonReport GetPutGetAccess(
            [Description("devicePath: device name from GetProjectTree, e.g. 'PLC_1'.")] string devicePath)
        {
            try
            {
                var data = Portal.GetPutGetAccess(devicePath);
                bool found = data["found"]?.GetValue<bool>() ?? false;
                bool enabled = data["enabled"]?.GetValue<bool>() ?? false;
                return new ResponseJsonReport
                {
                    Ok = found,
                    Message = found
                        ? $"PUT/GET access on {devicePath}: {(enabled ? "ENABLED" : "DISABLED")} (attribute '{data["attributeName"]}')."
                        : (data["message"]?.ToString() ?? "Not found."),
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = found }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"GetPutGetAccess failed for '{devicePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SetPutGetAccess"), Description(
            "[L2][Category:Hardware][WRITE][PreCondition:Connect+OpenProject]" +
            " Enable or disable remote PUT/GET access on a CPU (the precondition for S7 DB reads)." +
            " This is a hardware-configuration change — you must run DownloadToPlc afterwards for it to take effect on the live CPU." +
            " Returns before/after readback evidence.")]
        public static ResponseJsonReport SetPutGetAccess(
            [Description("devicePath: device name from GetProjectTree, e.g. 'PLC_1'.")] string devicePath,
            [Description("enable: true to permit remote PUT/GET access, false to forbid it.")] bool enable = true)
        {
            try
            {
                var data = Portal.SetPutGetAccess(devicePath, enable);
                bool ok = data["ok"]?.GetValue<bool>() ?? false;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok
                        ? $"PUT/GET access on {devicePath} set to {enable}. Download hardware config to apply."
                        : (data["message"]?.ToString() ?? "SetPutGetAccess failed."),
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = ok }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"SetPutGetAccess failed for '{devicePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        // Autonomy helper: when a compile/export/import is blocked because TIA is in online mode,
        // take ALL PLCs offline via Openness and retry once — never hand the toggle back to the user.
        internal static bool IsOnlineModeError(Exception ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                var m = e.Message ?? string.Empty;
                if (m.IndexOf("online mode", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (m.IndexOf("not permitted", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    m.IndexOf("online", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        internal static T WithAutoOffline<T>(Func<T> op)
        {
            try { return op(); }
            catch (Exception ex) when (IsOnlineModeError(ex))
            {
                try { Portal.GoOfflineAll(); } catch { }
                return op(); // retry once, now fully offline
            }
        }

        [McpServerTool(Name = "CompareSoftwareToOnline"), Description(
            "[L2][Category:PLC-Online][PreCondition:Connect+OpenProject+GoOnline]" +
            " Compare the offline PLC software in the project against the program currently running on the physical CPU." +
            " Use after editing blocks to confirm what differs from the live CPU before downloading," +
            " or after a download to verify offline/online consistency." +
            " Returns a tree-walked list of differences (only entries where ComparisonResult is not 'Equal' are reported)." +
            " Requires GoOnline to be called first; will return IsOnline=false with guidance otherwise.")]
        public static ResponseCompare CompareSoftwareToOnline(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("maxDepth: maximum tree depth to walk (default 4). Lower = faster but less detail.")] int maxDepth = 4,
            [Description("maxEntries: cap on differences returned (default 200). Truncated=true in response if reached.")] int maxEntries = 200)
        {
            try
            {
                return Portal.CompareSoftwareToOnline(softwarePath, maxDepth, maxEntries);
            }
            catch (PortalException pex)
            {
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error comparing '{softwarePath}' to online: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "CheckDownloadReadiness"), Description(
            "[L1][Category:PLC-Online][PreCondition:Connect+OpenProject+CompileSoftware]" +
            " Check whether a PLC is ready to receive a program download WITHOUT actually downloading." +
            " Verifies: DownloadProvider service is available, a network/IP configuration exists in the hardware config." +
            " Returns Ready=true only when all checks pass." +
            " Use this before DownloadToPlc to surface problems early (missing IP, no hardware config, etc.)." +
            " Meta.downloadRoutes lists every PG/PC interface -> CPU route (best-ranked first, preferred=true when the" +
            " adapter shares a subnet with the CPU) — check it on a multi-NIC PC (WLAN/VPN/PLCSIM) before downloading." +
            " Does NOT compile — run CompileSoftware first to ensure blocks are consistent.")]
        public static ResponseCheckDownload CheckDownloadReadiness(
            [Description("softwarePath: path to the PLC software in the project tree, e.g. 'PLC_1'")] string softwarePath)
        {
            try
            {
                return Portal.CheckDownloadReadiness(softwarePath);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error checking download readiness for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DownloadToPlc"), Description(
            "[L1][Category:PLC-Online][ONLINE-WRITE][PreCondition:Connect+OpenProject+CompileSoftware+CheckDownloadReadiness]" +
            " Download the compiled PLC program to the physical CPU over the network." +
            " The CPU will stop briefly during download and restart automatically (controlled by startAfterDownload)." +
            " SAFETY: Verify no personnel are near the machine before downloading. This changes live PLC behavior." +
            " Workflow: Connect → OpenProject → CompileSoftware → CheckDownloadReadiness → DownloadToPlc → GetOnlineState." +
            " On success State=Success or Warning. On Error check Errors[] for details." +
            " Default options (keepActualValues=true, consistentBlocksOnly=true) are safe for most scenarios." +
            " Set keepActualValues=false only when DB initial values must be reset — this is irreversible." +
            " On a multi-NIC PC the PG/PC interface is picked automatically (the adapter sharing a subnet with the CPU);" +
            " Meta.pgPcRoute reports which one was used. Override with pgPcInterface / targetIpAddress when the pick is wrong." +
            " S7-1500 FW >= 2.9 CPUs (incl. PLCSIM Advanced) ask for certificate trust on the first contact; trustDeviceCertificate=true (default) answers Trusted and Meta.tlsVerification records it, false makes TIA refuse the connection.")]
        public static ResponseDownload DownloadToPlc(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("consistentBlocksOnly: true=download only consistent blocks (safe default), false=download all blocks even inconsistent ones")] bool consistentBlocksOnly = true,
            [Description("keepActualValues: true=preserve current DB actual values (safe default), false=reset all DB values to initial values (irreversible)")] bool keepActualValues = true,
            [Description("startAfterDownload: true=automatically set CPU to RUN after download (default), false=leave CPU in STOP")] bool startAfterDownload = true,
            [Description("stopBeforeDownload: true=automatically stop CPU before download (required for most downloads), false=attempt online download without stopping")] bool stopBeforeDownload = true,
            [Description("password: optional CPU access password. Required when the CPU has download protection configured. Leave empty for unprotected CPUs.")] string password = "",
            [Description("pgPcInterface: optional PG/PC interface name (substring, case-insensitive), e.g. 'PLCSIM' or 'Realtek'. Leave empty to auto-pick the adapter that shares a subnet with the CPU. Run CheckDownloadReadiness to see the available names.")] string pgPcInterface = "",
            [Description("targetIpAddress: optional CPU IP to download to, e.g. '192.168.0.1'. Disambiguates which route to use when the project has several CPU interfaces. Leave empty to auto-pick.")] string targetIpAddress = "",
            [Description("userManagementMode: how the UserManagementDownload prompt is answered: keep (default, keeps the online user management data), updateKeepPassword (updates data but keeps online passwords), resetToProject (downloads all user management data and resets to project).")] string userManagementMode = "keep",
            [Description("promptAnswersJson: optional JSON object of explicit answers for download prompts by type name, e.g. {\"ResetModule\":\"DeleteAll\",\"OverwriteHmiData\":true}. Selection prompts take an enum name, checkbox prompts take true/false. Without an entry, destructive prompts (InitializeMemory, OverwriteOnMemoryCard, OverwriteSystemData, ResetModule, SwitchBackupToPrimary, ProtectionLevelChanged) default to NoAction/NoChange and prompts without a known default stay unanswered; Meta.promptsAnswered / Meta.promptsUnanswered list what happened.")] string promptAnswersJson = "{}",
            [Description("moduleAccessPassword: optional password for ModuleReadAccessPassword / ModuleWriteAccessPassword prompts; defaults to 'password' when empty. Never logged.")] string moduleAccessPassword = "",
            [Description("blockBindingPassword: optional password for the BlockBindingPassword prompt (know-how protected blocks bound to a CPU/card). Never logged.")] string blockBindingPassword = "",
            [Description("masterSecretPassword: optional password for the PlcMasterSecretPassword prompt. Never logged.")] string masterSecretPassword = "",
            [Description("rhTarget: empty for standard CPUs; primary or backup downloads to that CPU of an R/H system through RHDownloadProvider.DownloadToPrimary/DownloadToBackup.")] string rhTarget = "",
            [Description("trustDeviceCertificate: true (default) answers the TLS certificate prompt of FW >= 2.9 CPUs with Trusted for this call; false leaves it unanswered and TIA refuses the connection.")] bool trustDeviceCertificate = true)
        {
            try
            {
                var result = Portal.DownloadToPlc(
                    softwarePath,
                    consistentBlocksOnly,
                    keepActualValues,
                    startAfterDownload,
                    stopBeforeDownload,
                    string.IsNullOrWhiteSpace(password) ? null : password,
                    string.IsNullOrWhiteSpace(pgPcInterface) ? null : pgPcInterface,
                    string.IsNullOrWhiteSpace(targetIpAddress) ? null : targetIpAddress,
                    string.IsNullOrWhiteSpace(userManagementMode) ? "keep" : userManagementMode,
                    string.IsNullOrWhiteSpace(promptAnswersJson) ? "{}" : promptAnswersJson,
                    string.IsNullOrWhiteSpace(moduleAccessPassword) ? null : moduleAccessPassword,
                    string.IsNullOrWhiteSpace(blockBindingPassword) ? null : blockBindingPassword,
                    string.IsNullOrWhiteSpace(masterSecretPassword) ? null : masterSecretPassword,
                    rhTarget ?? "",
                    trustDeviceCertificate);

                if (result.Ok == false && result.Errors != null && result.Errors.Length > 0)
                    throw new McpException(
                        $"Download to '{softwarePath}' failed: {result.Message}",
                        McpErrorCode.InternalError);

                return result;
            }
            catch (McpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new McpException($"Unexpected error downloading to '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        #endregion
    }
}
