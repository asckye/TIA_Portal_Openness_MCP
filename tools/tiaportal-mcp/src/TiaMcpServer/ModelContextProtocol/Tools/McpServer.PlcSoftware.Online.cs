using TiaMcpServer.Siemens.Services;
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
    public static partial class McpServer
    {
        #region plc software - Online

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
                try { ((OnlineDownloadService)EngineServices.Get(typeof(OnlineDownloadService))).GoOfflineAll(); } catch { /* swallow(native-fallback): preserve the one retry so the original operation reports its own failure */ }
                return op(); // retry once, now fully offline
            }
        }

        #endregion
    }
}
