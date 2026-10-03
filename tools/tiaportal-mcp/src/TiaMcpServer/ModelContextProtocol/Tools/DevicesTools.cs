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

        public DevicesTools(DevicesService service) => _service = service;

        [McpServerTool(Name = "GetProjectTree"), Description("[L1][Project] Get the full project device/software tree as ASCII art. Requires: Connect + OpenProject. ALWAYS call this first after opening a project to discover the exact softwarePath (e.g. 'PLC_1') and device paths needed by all other PLC/HMI/hardware tools. Returns device names, software nodes, and HMI nodes.")]
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
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
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

        [McpServerTool(Name = "GetDeviceInfo"), Description("[L2][Hardware]Get info from a device from the current project/session")]
        public ResponseDeviceInfo GetDeviceInfo(
            [Description("devicePath: defines the path in the project structure to the device")] string devicePath)
        {
            try
            {
                var device = _service.GetDevice(devicePath);

                if (device != null)
                {
                    var attributes = Helper.GetAttributeList(device);

                    return new ResponseDeviceInfo
                    {
                        Message = $"Device info retrieved from '{devicePath}'",
                        Name = device.Name,
                        Attributes = attributes,
                        Description = device.ToString(),
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Device not found at '{devicePath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving device info from '{devicePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetDeviceItemInfo"), Description("[L2][Hardware]Get info from a device item from the current project/session")]
        public ResponseDeviceItemInfo GetDeviceItemInfo(
            [Description("deviceItemPath: defines the path in the project structure to the device item")] string deviceItemPath)
        {
            try
            {
                var deviceItem = _service.GetDeviceItem(deviceItemPath);

                if (deviceItem != null)
                {
                    var attributes = Helper.GetAttributeList(deviceItem);

                    return new ResponseDeviceItemInfo
                    {
                        Message = $"Device item info retrieved from '{deviceItemPath}'",
                        Name = deviceItem.Name,
                        Attributes = attributes,
                        Description = deviceItem.ToString(),
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Device item not found at '{deviceItemPath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving device item info from '{deviceItemPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetDeviceItemTree"), Description("[L2][Hardware]Get a subtree view for a device item (hardware components + sub device items)")]
        public ResponseTree GetDeviceItemTree(
            [Description("deviceItemPath: path in the project structure to the device item")] string deviceItemPath,
            [Description("maxDepth: recursion depth, default 4")] int maxDepth = 4)
        {
            try
            {
                var tree = _service.GetDeviceItemTree(deviceItemPath, maxDepth);
                if (!string.IsNullOrEmpty(tree))
                {
                    return new ResponseTree
                    {
                        Message = $"Device item tree retrieved from '{deviceItemPath}'",
                        Tree = "```\n" + tree + "\n```",
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"Device item not found at '{deviceItemPath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving device item tree from '{deviceItemPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SetDeviceItemAttribute"), Description("[L2][Hardware]Set one exact DeviceItem attribute by name with type coercion and detailed error return. Use GetDeviceItemInfo/GetDeviceItemNetworkInfo first.")]
        public ResponseMessage SetDeviceItemAttribute(
            [Description("deviceItemPath: path in the project structure to the device item")] string deviceItemPath,
            [Description("attributeName: exact attribute name from GetDeviceItemInfo/GetDeviceItemNetworkInfo")] string attributeName,
            [Description("value: new value as string; converted based on current attribute type")] string value)
        {
            try
            {
                return _service.SetDeviceItemAttribute(deviceItemPath, attributeName, value);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error setting device item attribute '{attributeName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ValidateAutomationContext"), Description("[L1][Diagnostics]Preflight current project for automation: devices, software, expected PLC/HMI paths, and project tree.")]
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

        [McpServerTool(Name = "SetCpuCommonSettings"), Description("[L2][Hardware] Set CPU common settings using exactAttributes JSON only after reading exact attribute names from GetDeviceItemInfo/GetDeviceItemNetworkInfo. The tool writes only those exact attributes, rejects missing/non-writable attributes, and returns readback evidence.")]
        public ResponseMessage SetCpuCommonSettings(
            [Description("cpuPath: real CPU device-item path resolved from GetProjectTree/GetDeviceItemTree.")] string cpuPath,
            [Description("settingsJson: JSON object { \"exactAttributes\": { \"ExactAttributeNameFromReadback\": \"value\" } }. No aliases or guessed attribute names are accepted.")] string settingsJson)
        {
            try
            {
                return _service.SetCpuCommonSettings(cpuPath, settingsJson);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error setting CPU common settings for '{cpuPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        // NOTE: Deprecated demo tools removed (GenerateStartStopBlocks / EnsureUnifiedMainScreen).

        [McpServerTool(Name = "GetDevices"), Description("[L1][Hardware] List all hardware devices (PLCs, HMI panels, drives) in the project with their attributes. Requires: Connect + OpenProject. Prefer GetProjectTree for a visual overview; use this when you need the exact device Name and attribute values for subsequent operations like AddDevice or SetDeviceItemAttribute.")]
        public ResponseDevices GetDevices(
            [Description("includePlcSoftware: include a structured PLC software name inventory for desktop browsing.")] bool includePlcSoftware = false)
        {
            try
            {
                var list = _service.GetDevices();
                var responseList = new List<ResponseDeviceInfo>();

                if (list != null)
                {
                    foreach (var device in list)
                    {
                        if (device != null)
                        {
                            var attributes = Helper.GetAttributeList(device);
                            responseList.Add(new ResponseDeviceInfo
                            {
                                Name = device.Name,
                                Attributes = attributes,
                                Description = device.ToString()
                            });
                        }
                    }

                    return new ResponseDevices
                    {
                        Message = "Devices retrieved",
                        Items = responseList,
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["plcSoftwareNames"] = includePlcSoftware ? new JsonArray(_service.GetPlcSoftwareNamesForDesktop().Select(n => (JsonNode)n).ToArray()) : null,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Failed retrieving devices", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving devices: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AddDevice"), Description("[L2][Hardware] Add one hardware device using an exact MLFB/order number and version from the TIA hardware catalog. Requires: Connect + OpenProject. Use SearchHardwareCatalog first to find the exact MLFB/version. For unknown or approximate device names, use AddDeviceWithFallback or AddHardwareCatalogDeviceWithProbe instead.")]
        public ResponseMessage AddDevice(
            [Description("orderNumber: MLFB/order number from the TIA hardware catalog, e.g. '6ES7211-1BE40-0XB0' or '6ES7 211-1BE40-0XB0'")] string orderNumber,
            [Description("version: catalog device version, e.g. 'V4.7', 'V3.1', or empty to let TIA try defaults")] string version,
            [Description("deviceName: name in project tree")] string deviceName)
        {
            try
            {
                var dev = _service.AddDevice(orderNumber, version, deviceName);
                return new ResponseMessage
                {
                    Message = $"Device '{deviceName}' added",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["name"] = dev.Name
                    }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed to add device '{deviceName}' ({orderNumber} {version}) [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error adding device: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AddDeviceWithFallback"), Description("[L1][Hardware] PREFERRED for natural-language device insertion. Adds a Siemens hardware device by probing the installed TIA catalog with fallback versions. Requires: Connect + OpenProject. Example: 'add CPU 1211C AC/DC/RLY' → family='S7-1200'. Families: S7-1200, S7-1500, WinCCUnifiedPC. For third-party/GSD devices use AddGsdDeviceWithProbe.")]
        public ResponseDeviceProbe AddDeviceWithFallback(
            [Description("preferredMlfb: preferred MLFB/order number, e.g. '6ES7211-1BE40-0XB0'; empty uses family fallback list")] string preferredMlfb,
            [Description("preferredVersion: preferred catalog version, e.g. 'V4.7'; empty probes known/default versions")] string preferredVersion,
            [Description("deviceName: name in project tree")] string deviceName,
            [Description("family: fallback hint, e.g. 'S7-1200', 'S7-1500', or 'WinCCUnifiedPC'")] string family = "S7-1500")
        {
            try
            {
                var res = _service.AddDeviceWithFallback(preferredMlfb, preferredVersion, deviceName, family);
                if (res.Device == null)
                {
                    return new ResponseDeviceProbe
                    {
                        Message = $"Failed to add device '{deviceName}' with fallback probes",
                        Ok = false,
                        DeviceName = deviceName,
                        Family = family,
                        Attempts = res.Attempts,
                        Error = res.Error,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false }
                    };
                }

                return new ResponseDeviceProbe
                {
                    Message = $"Device '{deviceName}' added (fallback)",
                    Ok = true,
                    DeviceName = deviceName,
                    Family = family,
                    MlfbUsed = res.MlfbUsed,
                    VersionUsed = res.VersionUsed,
                    Attempts = res.Attempts,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true, ["name"] = res.Device.Name }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error adding device with fallback: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SearchInstalledGsdDevices"), Description("[L2][Hardware]Search installed TIA V21 hardware catalog and local GSDML files for third-party PROFINET/GSD devices such as AFM60A, ATV320, or DL100. Use this before inserting a non-Siemens device.")]
        public ResponseGsdDeviceSearch SearchInstalledGsdDevices(
            [Description("keyword: device/vendor/order/DAP keyword, e.g. 'AFM60A', 'ATV320', 'DL100', 'SICK AFM60A'")] string keyword,
            [Description("limit: maximum candidates to return")] int limit = 50)
        {
            try
            {
                var items = _service.SearchInstalledGsdDevices(keyword, limit);
                return new ResponseGsdDeviceSearch
                {
                    Message = $"Found {items.Count} installed GSD/catalog candidate(s) for '{keyword}'",
                    Keyword = keyword,
                    Count = items.Count,
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
                throw new McpException($"Unexpected error searching installed GSD devices: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SearchHardwareCatalog"), Description("[L1][Hardware]Search the installed TIA Portal hardware catalog for Siemens or third-party catalog entries by keyword, MLFB/order number, device family, or description. Use this before adding hardware when the exact TypeIdentifier is unknown, especially HMI panels such as KTP700 Basic.")]
        public ResponseHardwareCatalogSearch SearchHardwareCatalog(
            [Description("keyword: MLFB/order number/device/family text, e.g. 'KTP700', '6AV2123', '1211C DC/DC/DC', '6ES7211-1AE40'")] string keyword,
            [Description("limit: maximum candidates to return")] int limit = 50)
        {
            try
            {
                var items = _service.SearchHardwareCatalog(keyword, limit);
                var success = items.Count > 0;
                return new ResponseHardwareCatalogSearch
                {
                    Message = $"Found {items.Count} hardware catalog candidate(s) for '{keyword}'",
                    Keyword = keyword,
                    Count = items.Count,
                    Items = items,
                    Error = success ? null : $"No matching hardware catalog candidates for '{keyword}'",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = success
                    }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed searching hardware catalog for '{keyword}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error searching hardware catalog: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AddGsdDeviceWithProbe"), Description("[L2][Hardware]Add a third-party GSD/GSDML hardware device by first searching the installed TIA hardware catalog, ranking candidates, and inserting with the exact catalog TypeIdentifier. Does not fall back to unrelated Siemens devices.")]
        public ResponseGsdDeviceProbe AddGsdDeviceWithProbe(
            [Description("keyword: device/vendor/order/DAP keyword, e.g. 'AFM60A', 'ATV320', 'DL100'")] string keyword,
            [Description("deviceName: name in project tree, e.g. 'ENC_AFM60A_1'")] string deviceName,
            [Description("preferredDap: optional DAP/version hint, e.g. 'DAP V2.0', 'DAP 1', or empty")] string preferredDap = "")
        {
            try
            {
                var res = _service.AddGsdDeviceWithProbe(keyword, deviceName, preferredDap);
                if (res.Device == null)
                {
                    return new ResponseGsdDeviceProbe
                    {
                        Message = $"Failed to add GSD device '{deviceName}' for '{keyword}'",
                        Ok = false,
                        Keyword = keyword,
                        DeviceName = deviceName,
                        PreferredDap = preferredDap,
                        Candidates = res.Candidates,
                        Attempts = res.Attempts,
                        Error = res.Error,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false }
                    };
                }

                return new ResponseGsdDeviceProbe
                {
                    Message = $"GSD device '{deviceName}' added",
                    Ok = true,
                    Keyword = keyword,
                    DeviceName = deviceName,
                    PreferredDap = preferredDap,
                    CandidateUsed = res.Candidate,
                    Candidates = res.Candidates,
                    Attempts = res.Attempts,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["name"] = res.Device.Name
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error adding GSD device with probe: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AddHardwareCatalogDeviceWithProbe"), Description("[L2][Hardware]Add a hardware device by searching the installed TIA hardware catalog, ranking insertable TypeIdentifier candidates, and inserting the best match. Use for Siemens devices/HMI panels when exact TypeIdentifier is unknown, e.g. KTP700 Basic PN.")]
        public ResponseHardwareCatalogDeviceProbe AddHardwareCatalogDeviceWithProbe(
            [Description("keyword: MLFB/order number/device/family text, e.g. 'KTP700 Basic PN', '6AV2123-2GB03', 'S7-1211C DC/DC/DC'")] string keyword,
            [Description("deviceName: name in project tree, e.g. 'HMI_KTP700_1'")] string deviceName,
            [Description("preferredText: optional ranking hint, e.g. 'PN 17.0.0.0 landscape', '6AV2 123-2GB03-0AX0'")] string preferredText = "")
        {
            try
            {
                var res = _service.AddHardwareCatalogDeviceWithProbe(keyword, deviceName, preferredText);
                if (res.Device == null)
                {
                    return new ResponseHardwareCatalogDeviceProbe
                    {
                        Message = $"Failed to add hardware catalog device '{deviceName}' for '{keyword}'",
                        Ok = false,
                        Keyword = keyword,
                        DeviceName = deviceName,
                        PreferredText = preferredText,
                        Candidates = res.Candidates,
                        Attempts = res.Attempts,
                        Error = res.Error,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false }
                    };
                }

                return new ResponseHardwareCatalogDeviceProbe
                {
                    Message = $"Hardware catalog device '{deviceName}' added",
                    Ok = true,
                    Keyword = keyword,
                    DeviceName = deviceName,
                    PreferredText = preferredText,
                    CandidateUsed = res.Candidate,
                    Candidates = res.Candidates,
                    Attempts = res.Attempts,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true, ["name"] = res.Device.Name }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error adding hardware catalog device with probe: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }
    }
}
