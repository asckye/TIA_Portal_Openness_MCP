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
        #region devices


        [McpServerTool(Name = "GetDeviceItemNetworkInfo"), Description("[L2][Hardware]Get network-related attributes for a device item (best-effort heuristic filter)")]
        public static ResponseNetworkInfo GetDeviceItemNetworkInfo(
            [Description("deviceItemPath: path in the project structure to the device item")] string deviceItemPath)
        {
            try
            {
                var attrs = Portal.GetDeviceItemNetworkInfo(deviceItemPath);
                if (attrs != null)
                {
                    return new ResponseNetworkInfo
                    {
                        Message = $"Network info retrieved from '{deviceItemPath}'",
                        DeviceItemName = deviceItemPath.Split('/').LastOrDefault(),
                        Attributes = attrs,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"Device item not found at '{deviceItemPath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving network info from '{deviceItemPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }


        [McpServerTool(Name = "ConnectDeviceNodesToProfinetSubnet"), Description("[L1][Hardware] PREFERRED for PROFINET network setup. Finds the first IE/PROFINET node under two devices, creates/reuses a subnet on the first, connects the second, and returns readback evidence. Requires: Connect + OpenProject + both devices added. Typical: firstRootPath='PLC_1', secondRootPath='HMI_KTP700_1/HMI_KTP700_1.IE_CP_1'. Verified for S7-1200 + KTP700 Basic PN.")]
        public static ResponseMessage ConnectDeviceNodesToProfinetSubnet(
            [Description("firstRootPath: first device/device-item root, usually PLC root, e.g. 'PLC_1'")] string firstRootPath,
            [Description("secondRootPath: second device/device-item root, e.g. 'HMI_KTP700_1/HMI_KTP700_1.IE_CP_1'")] string secondRootPath,
            [Description("subnetName: subnet name to create when the first node is not already connected, e.g. 'PN_IE_1'")] string subnetName = "PN_IE_1")
        {
            try
            {
                var report = Portal.ProbeConnectDeviceNodesToSubnet(firstRootPath, secondRootPath, subnetName);
                var success =
                    report.IndexOf("ConnectToSubnet: OK", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (report.IndexOf("already connected to subnet", StringComparison.OrdinalIgnoreCase) >= 0 &&
                     report.IndexOf("connectedSubnet=<none>", StringComparison.OrdinalIgnoreCase) < 0);

                return new ResponseMessage
                {
                    Message = success
                        ? "Device nodes connected to PROFINET subnet"
                        : "Device node PROFINET subnet connection did not complete",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = success,
                        ["firstRootPath"] = firstRootPath,
                        ["secondRootPath"] = secondRootPath,
                        ["subnetName"] = subnetName,
                        ["report"] = report
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error connecting device nodes to PROFINET subnet: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "PlanHardwareNetworkConfiguration"), Description("[L2][Hardware][OFFLINE] Validate a hardware network operation plan without connecting to TIA Portal or modifying a project. Use this before EnsureSubnet/AttachDeviceNodeToSubnet/SetCpuCommonSettings; rejects guessed paths, unsafe subnet types, invalid IP/mask/gateway, and CPU settings without exactAttributes.")]
        public static ResponseJsonReport PlanHardwareNetworkConfiguration(
            [Description("planJson: JSON with operations[]. Supported operation types: EnsureSubnet, AttachDeviceNodeToSubnet, SetCpuCommonSettings. This is offline-only and performs validation only.")] string planJson)
        {
            try
            {
                var report = HardwareNetworkPlanValidator.Validate(planJson);
                return new ResponseJsonReport
                {
                    Ok = report["ok"]?.GetValue<bool>() == true,
                    Data = report,
                    Message = report["ok"]?.GetValue<bool>() == true
                        ? "Hardware network plan is valid"
                        : "Hardware network plan has validation errors",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = report["ok"]?.GetValue<bool>() == true,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error validating hardware network plan: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureSubnet"), Description("[L2][Hardware] Ensure an Industrial Ethernet/PROFINET subnet by anchoring on a real deviceItemPath from GetProjectTree/GetDeviceItemTree. Applies only through TIA Openness, then returns readback evidence (node path, subnet name, interface path). Does not guess paths.")]
        public static ResponseMessage EnsureSubnet(
            [Description("anchorDeviceItemPath: real device/device-item path resolved from GetProjectTree/GetDeviceItemTree; used to create/reuse the subnet from its first PROFINET node.")] string anchorDeviceItemPath,
            [Description("subnetType: IndustrialEthernet/PROFINET/PN/IE only.")] string subnetType,
            [Description("subnetName: exact subnet name to create or read back, e.g. PN_IE_1.")] string subnetName)
        {
            try
            {
                return Portal.EnsureSubnet(anchorDeviceItemPath, subnetType, subnetName);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring subnet '{subnetName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AttachDeviceNodeToSubnet"), Description("[L2][Hardware] Attach one real device network node to an existing PROFINET subnet and return readback evidence. deviceItemPath must come from GetProjectTree/GetDeviceItemTree, interfaceIndex selects a discovered Industrial Ethernet/PROFINET node, and online/force operations are never used.")]
        public static ResponseMessage AttachDeviceNodeToSubnet(
            [Description("deviceItemPath: real device/device-item path resolved from GetProjectTree/GetDeviceItemTree.")] string deviceItemPath,
            [Description("interfaceIndex: zero-based index among discovered Industrial Ethernet/PROFINET nodes under deviceItemPath.")] int interfaceIndex,
            [Description("subnetName: existing subnet name to attach to.")] string subnetName,
            [Description("anchorDeviceItemPath: optional real device-item path used to EnsureSubnet first when subnetName is not found.")] string anchorDeviceItemPath = "")
        {
            try
            {
                return Portal.AttachDeviceNodeToSubnet(deviceItemPath, interfaceIndex, subnetName, anchorDeviceItemPath);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error attaching device node to subnet '{subnetName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }


        [McpServerTool(Name = "ProbeHardwareHmiConnectionOwnerCandidates"), Description("[L2][Hardware]Enumerate candidate owner objects for hardware HMI connection creation without calling GetService on each high-level object.")]
        public static ResponseStringList ProbeHardwareHmiConnectionOwnerCandidates(
            [Description("plcRootPath: PLC device/device-item root, e.g. 'PLC_1'")] string plcRootPath,
            [Description("hmiRootPath: HMI device/device-item root, e.g. 'HMI_KTP700_1/HMI_KTP700_1.IE_CP_1'")] string hmiRootPath,
            [Description("deepScan: when true include ancestor/project/device-level candidates; when false only direct node/interface/device-item candidates")] bool deepScan = true)
        {
            try
            {
                var items = Portal.ProbeHardwareHmiConnectionOwnerCandidates(plcRootPath, hmiRootPath, deepScan);
                return new ResponseStringList
                {
                    Message = "Hardware HMI connection owner candidates enumerated",
                    Items = items,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error probing hardware HMI connection owner candidates: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ProbeHardwareHmiConnectionWhitelistedServices"), Description("[L2][Hardware]Read-only scan of whitelisted services on safe hardware HMI connection owner candidates. Does not create connections and skips high-level project/composition objects to avoid hangs.")]
        public static ResponseStringList ProbeHardwareHmiConnectionWhitelistedServices(
            [Description("plcRootPath: PLC device/device-item root, e.g. 'PLC_1'")] string plcRootPath,
            [Description("hmiRootPath: HMI device/device-item root, e.g. 'HMI_KTP700_1/HMI_KTP700_1.IE_CP_1'")] string hmiRootPath,
            [Description("deepScan: when true include safe ancestors; when false only direct node/interface/device-item candidates")] bool deepScan = true)
        {
            try
            {
                var items = Portal.ProbeHardwareHmiConnectionWhitelistedServices(plcRootPath, hmiRootPath, deepScan);
                return new ResponseStringList
                {
                    Message = "Hardware HMI connection whitelisted services scanned",
                    Items = items,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error scanning hardware HMI connection whitelisted services: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }


        #endregion
    }
}
