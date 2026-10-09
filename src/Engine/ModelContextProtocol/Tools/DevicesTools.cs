using TiaMcp.Logic.V4.Hmi;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4;
using CpuSettings = TiaMcp.Logic.V4.Domain.CpuSettings;
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


using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class DevicesTools
    {
        private readonly DevicesService _service;
        private readonly HardwareDevicesTools port;
        public DevicesTools(DevicesService service, HardwareDevicesTools port) { _service = service; this.port = port; }
        [McpServerTool(Name = "GetProjectTree"), Description("[L1][Project] Get the full project device/software tree as ASCII art. Requires: ConnectPortal + OpenProject. ALWAYS call this first after opening a project to discover the exact softwarePath (e.g. 'PLC_1') and device paths needed by all other PLC/HMI/hardware tools. Returns device names, software nodes, and HMI nodes.")]
        public CallToolResult GetProjectTreeV4()
            => HardwareContract.Run("GetProjectTree", () =>
            {
                HardwareContract.RequireProject(_service.HasProject);
                return GetProjectTree();
            }, write: false, current: false);

        public ResponseProjectTree GetProjectTree()
        {
            try
            {
                var tree = _service.GetProjectTree();

                if (!string.IsNullOrEmpty(tree))
                {
                    return new ResponseProjectTree
                    {
                        Message = "Project tree retrieved",
                        Tree = "```\n" + tree + "\n```",
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }
                else
                {
                    throw new McpException("Failed retrieving project tree", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving project tree: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ValidateAutomationContext"), Description("[L1][Diagnostics]Preflight current project for automation: devices, software, expected PLC/HMI paths, and project tree.")]
        public CallToolResult ValidateAutomationContextV4(
            [Description("expectedPlcSoftwarePath: expected PLC software path, empty to skip")] string expectedPlcSoftwarePath = "PLC_1",
            [Description("expectedHmiSoftwarePath: expected HMI software path, empty to skip")] string expectedHmiSoftwarePath = "HMI_RT_1")
            => HardwareContract.Run("ValidateAutomationContext", () =>
            {
                return ValidateAutomationContext(expectedPlcSoftwarePath, expectedHmiSoftwarePath);
            }, write: false, current: false);

        public ResponseMessage ValidateAutomationContext(
            [Description("expectedPlcSoftwarePath: expected PLC software path, empty to skip")] string expectedPlcSoftwarePath = "PLC_1",
            [Description("expectedHmiSoftwarePath: expected HMI software path, empty to skip")] string expectedHmiSoftwarePath = "HMI_RT_1")
        {
            try
            {
                return _service.ValidateAutomationContext(expectedPlcSoftwarePath, expectedHmiSoftwarePath);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error validating automation context: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }
        public ResponseDeviceProbe AddDeviceWithFallback(
            string preferredMlfb,
            string preferredVersion,
            string deviceName,
            string family = "S7-1500") => port.AddDeviceWithFallback(preferredMlfb, preferredVersion, deviceName, family);
        public ResponseMessage AddDevice(
            string orderNumber,
            string version,
            string deviceName) => port.AddDevice(orderNumber, version, deviceName);
        public ResponseDeviceInfo GetDeviceInfo(
            string devicePath) => port.GetDeviceInfo(devicePath);
        public ResponseGsdDeviceSearch SearchInstalledGsdDevices(
            string keyword,
            int limit = 50) => port.SearchInstalledGsdDevices(keyword, limit);
        public ResponseHardwareCatalogSearch SearchHardwareCatalog(
            string keyword,
            int limit = 50) => port.SearchHardwareCatalog(keyword, limit);
        public ResponseHardwareCatalogDeviceProbe AddHardwareCatalogDeviceWithProbe(
            string keyword,
            string deviceName,
            string preferredText = "") => port.AddHardwareCatalogDeviceWithProbe(keyword, deviceName, preferredText);
        public ResponseGsdDeviceProbe AddGsdDeviceWithProbe(
            string keyword,
            string deviceName,
            string preferredDap = "") => port.AddGsdDeviceWithProbe(keyword, deviceName, preferredDap);
        public ResponseDeviceItemInfo GetDeviceItemInfo(
            string deviceItemPath) => port.GetDeviceItemInfo(deviceItemPath);
        public ResponseDevices GetDevices(
            bool includePlcSoftware = false) => port.GetDevices(includePlcSoftware);
        public ResponseTree GetDeviceItemTree(
            string deviceItemPath,
            int maxDepth = 4) => port.GetDeviceItemTree(deviceItemPath, maxDepth);
    }
}
