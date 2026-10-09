using System.Text.Json;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcpServer.Siemens;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class HardwareServicesTools
    {
        private readonly HardwareServicesService _hardware;

        public HardwareServicesTools(HardwareServicesService hardware) => _hardware = hardware;





        [McpServerTool(Name="ManageWatchForceTableWebAccess"), Description("[L2][Hardware][WRITE] Read/assign/unassign web-server access rules for PLC watch/force tables (WatchAndForceTableAccessManager on the exact CPU DeviceItem). read lists both rule sets. assign/unassign need softwarePath (exact PLC, must be Offline for a real run), tableKind watch|force, exact tablePath (group/table), access None|Read|Write and confirmChange=true. Existing rule with read-only Access is refused (unassign first). Default preview; readback verified; no save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageWatchForceTableWebAccess(
            string[] devicePath,
            string[] itemPath,
            [Description("action: the operation to perform - read | assign | unassign.")] string action="read",
            string softwarePath="",
            [Description("tableKind: watch | force.")] string tableKind="watch",
            string tablePath="",
            [Description("access: None | Read | Write.")] string access="Read",
            bool confirmChange=false,
            bool dryRun=true)
            => HardwareToolContract.Invoke("ManageWatchForceTableWebAccess", dryRun || action == "read", action != "read", () =>
            {
                string devicePathJson = HardwareToolContract.Path(devicePath, "devicePath", rootAllowed: false, optional: false);
                string itemPathJson = HardwareToolContract.Path(itemPath, "itemPath", rootAllowed: true, optional: false);
                HardwareToolContract.Check(() =>
                {
                    HardwareServicesLogic.RequireOneOf(action, new[] { "read", "assign", "unassign" }, "action");
                    HardwareServicesLogic.RequireOneOf(tableKind, new[] { "watch", "force" }, "tableKind");
                    if (action != "read") HardwareToolContract.Confirm(dryRun, confirmChange);
                });
                return _hardware.ManageWatchForceTableWebAccess(devicePathJson,itemPathJson,action,softwarePath,tableKind,tablePath,access,confirmChange,dryRun);
            });







        [McpServerTool(Name="ManagePlcProtection"), Description("[L2][Hardware][WRITE] Access level and confidential-configuration-data password of ONE CPU (PlcAccessLevelProvider / PlcMasterSecretConfigurator on the CPU device item; itemPath [] resolves the station's CPU). read reports accessLevel (FullAccess / ReadAccess / HMIAccess / NoAccess / FullAccessIncludingFailsafe), masterSecret (None = 'Protect confidential PLC configuration data' unchecked, WithoutPassword = checked without password, WithPassword, WithPasswordAllDataProtection) and the access-control mode. setAccessLevel accessLevel; setAccessPassword / resetAccessPassword accessLevel [+ password] (TIA only accepts passwords for levels less strict than the selected one); protectMasterSecret password; changeMasterSecret password newPassword; unprotectMasterSecret [password]; resetMasterSecret (certificates encrypted with it are lost); protectAllConfiguration [password]; unprotectAllConfiguration. Why: TIA V21 refuses the hardware download of an S7-1500 FW >= 2.9 CPU whose level is above FullAccess without a FullAccess password or whose confidential data has no password ('Hardware compilation completed with errors' - see CompileDevice). Passwords go in as SecureString and are never echoed; state read back (meta.before / after). Default preview; real change needs dryRun=false AND confirmChange=true; no compile / save / download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManagePlcProtection(
            [Description("devicePath: array naming the station, e.g. [\"PLC_1\"] (or [group, ..., station]).")] string[] devicePath,
            [Description("itemPath: array of device-item names down to the CPU; [] (default) resolves the station's CPU.")] string[] itemPath = null!,
            [Description("action: read | setAccessLevel | setAccessPassword | resetAccessPassword | protectMasterSecret | changeMasterSecret | unprotectMasterSecret | resetMasterSecret | protectAllConfiguration | unprotectAllConfiguration.")] string action="read",
            [Description("accessLevel: for setAccessLevel / setAccessPassword / resetAccessPassword - FullAccess | ReadAccess | HMIAccess | NoAccess | FullAccessIncludingFailsafe.")] string accessLevel="",
            [Description("password: the password to set (setAccessPassword, protectMasterSecret) or the current one (changeMasterSecret); never logged.")] string password="",
            [Description("newPassword: the new password for changeMasterSecret.")] string newPassword="",
            [Description("confirmChange: must be true together with dryRun=false for every action except read.")] bool confirmChange=false,
            [Description("dryRun: true (default) previews; false applies the change.")] bool dryRun=true)
            => HardwareToolContract.Invoke("ManagePlcProtection", dryRun || action == "read", action != "read", () =>
            {
                string devicePathJson = HardwareToolContract.Path(devicePath, "devicePath", rootAllowed: false, optional: false);
                string itemPathJson = HardwareToolContract.Path(itemPath, "itemPath", rootAllowed: true, optional: true);
                HardwareToolContract.Check(() =>
                {
                    HardwareServicesLogic.RequireOneOf(action, PlcProtectionLogic.Actions, "action");
                    if (action != "read") HardwareToolContract.Confirm(dryRun, confirmChange);
                });
                return _hardware.ManagePlcProtection(devicePathJson,itemPathJson,action,accessLevel,password,newPassword,confirmChange,dryRun);
            });

        [McpServerTool(Name="CompileDevice"), Description("[L2][Hardware][EXECUTE] Hardware compile of one device (or device item) through ICompilable - what the TIA UI's 'Compile > Hardware (rebuild all)' does and what DownloadPlc runs first; CompilePlcSoftware / CompilePlcDiagnostics only compile the program. Returns the compiler state, error / warning counts and the flattened diagnostics (errors[] / warnings[] / nodes) - e.g. the security errors of an S7-1500 FW >= 2.9 CPU that ManagePlcProtection fixes. No save / download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult CompileDevice(
            [Description("devicePath: array naming the station to compile, e.g. [\"PLC_1\"].")] string[] devicePath,
            [Description("itemPath: array of device-item names when one item (e.g. the CPU) is to be compiled; [] = the whole station.")] string[] itemPath = null!)
            => HardwareToolContract.Invoke("CompileDevice", false, true, () =>
            {
                string devicePathJson = HardwareToolContract.Path(devicePath, "devicePath", rootAllowed: false, optional: false);
                string itemPathJson = HardwareToolContract.Path(itemPath, "itemPath", rootAllowed: true, optional: true);
                return _hardware.CompileDevice(devicePathJson,itemPathJson);
            });

        [McpServerTool(Name = "GetPlcPutGetAccess"), Description(
            "[L2][Category:Hardware][PreCondition:ConnectPortal+OpenProject]" +
            " Read whether a CPU permits remote PUT/GET access — the precondition for GetPlcLiveValuesS7 on DB areas." +
            " If enabled=false, S7 absolute reads of DBs will fail; enable with SetPlcPutGetAccess (then hardware DownloadPlc).")]
        public CallToolResult GetPlcPutGetAccess(
            [Description("devicePath: device name from GetProjectTree, e.g. 'PLC_1'.")] string devicePath)
            => HardwareToolContract.Invoke("GetPlcPutGetAccess", true, false, () =>
            {
                return GetPutGetAccessCore(devicePath);
            });

        [McpServerTool(Name = "SetPlcPutGetAccess"), Description(
            "[L2][Category:Hardware][WRITE][PreCondition:ConnectPortal+OpenProject]" +
            " Enable or disable remote PUT/GET access on a CPU (the precondition for S7 DB reads)." +
            " This is a hardware-configuration change — you must run DownloadPlc afterwards for it to take effect on the live CPU." +
            " Returns before/after readback evidence. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult SetPlcPutGetAccess(
            [Description("devicePath: device name from GetProjectTree, e.g. 'PLC_1'.")] string devicePath,
            [Description("enable: true to permit remote PUT/GET access, false to forbid it.")] bool enable = true)
            => HardwareToolContract.Invoke("SetPlcPutGetAccess", false, true, () =>
            {
                return SetPutGetAccessCore(devicePath, enable);
            });



        // Unregistered adapters retain the existing domain response construction.
        private ResponseJsonReport GetPutGetAccessCore(
            [Description("devicePath: device name from GetProjectTree, e.g. 'PLC_1'.")] string devicePath)
        {
            try
            {
                var data = _hardware.GetPutGetAccess(devicePath);
                bool found = data["found"]?.GetValue<bool>() ?? false;
                bool enabled = data["enabled"]?.GetValue<bool>() ?? false;
                return new ResponseJsonReport
                {
                    Ok = found,
                    Message = found
                        ? $"PUT/GET access on {devicePath}: {(enabled ? "ENABLED" : "DISABLED")} (attribute '{data["attributeName"]}')."
                        : (data["message"]?.ToString() ?? "Not found."),
                    Data = data,
                    Meta = ResponseMeta.Basic(DateTime.Now, found)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"GetPlcPutGetAccess failed for '{devicePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        private ResponseJsonReport SetPutGetAccessCore(
            [Description("devicePath: device name from GetProjectTree, e.g. 'PLC_1'.")] string devicePath,
            [Description("enable: true to permit remote PUT/GET access, false to forbid it.")] bool enable = true)
        {
            try
            {
                var data = _hardware.SetPutGetAccess(devicePath, enable);
                bool ok = data["ok"]?.GetValue<bool>() ?? false;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok
                        ? $"PUT/GET access on {devicePath} set to {enable}. Download hardware config to apply."
                        : (data["message"]?.ToString() ?? "SetPlcPutGetAccess failed."),
                    Data = data,
                    Meta = ResponseMeta.Basic(DateTime.Now, ok)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"SetPlcPutGetAccess failed for '{devicePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }
    }
}
