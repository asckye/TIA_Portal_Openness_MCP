using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4;
using CpuSettings = TiaMcp.Logic.V4.Domain.CpuSettings;
using ModelContextProtocol.Protocol;
using System.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class HardwareManagementTools
    {
        private readonly HardwareManagementService _service;

        public HardwareManagementTools(HardwareManagementService service) => _service = service;
        [McpServerTool(Name="ManageHardwareObject"),
                Description("[L2][Hardware][WRITE] Native deleteDevice/deleteItem/moveItem/copyItem, changeType (V18+), " +
                "createSubnet/deleteSubnet (all releases). " +
                "Device actions require devicePath=[group,...,station] or [unique exact station name]; itemPath lists exact child names. Subnet actions " +
                "require empty devicePath/itemPath and an exact subnetName. changeType/createSubnet require an exact typeIdentifier. Move/copy pass " +
                "native CanPlug checks. dryRun=true validates and reads impact without writing; execution requires the preview's expectedPlanHash and " +
                "expectedProjectFile. Deletes/type changes may affect software and network configuration. No save/download/online control.")]
        public CallToolResult ManageHardwareObjectV4(
            [Description("action: deleteDevice | deleteItem | moveItem | copyItem | changeType (V18+) | createSubnet | deleteSubnet.")] string action,
            [Description("devicePath: JSON array naming the station - [group, ..., station] or the unique station name, e.g. " +
                "[\"PLC_2\"]. Required for device/item actions; [] (default) for subnet actions.")] string[]? devicePath = null,
            [Description("itemPath: exact device-item names for deleteItem/moveItem/copyItem/changeType; [] for deleteDevice and subnet actions.")]
                string[]? itemPath = null,
            [Description("destinationDevicePath: for moveItem / copyItem - JSON array naming the destination station.")] string[]? destinationDevicePath = null,
            [Description("destinationItemPath: for moveItem / copyItem - JSON array of device-item names of the destination container.")] string[]
                ? destinationItemPath = null,
            [Description("position: for moveItem / copyItem - target slot / position number (-1 = let TIA pick).")] int position=-1,
            [Description("dryRun: true (default) validates and previews native impact; false executes the bound plan.")] bool dryRun=true,
            [Description("typeIdentifier: exact catalog TypeIdentifier for changeType; exact subnet type (e.g. System:Subnet.Ethernet) for createSubnet.")]
                string typeIdentifier = "",
            [Description("subnetName: exact project subnet name for createSubnet/deleteSubnet; no anchor node is required.")] string subnetName = "",
            [Description("expectedPlanHash: copy data.planHash from the matching preview; required when dryRun=false.")] string expectedPlanHash = "",
            [Description("expectedProjectFile: absolute project file from the preview's targetIdentity; required when dryRun=false.")]
                string expectedProjectFile = "")
        {
            if (TiaMcp.Adapters.Contracts.HardwareCapabilities.Unsupported(HardwareContract.ReleaseKey, "ManageHardwareObject", action) is string reason)
                return HardwareToolContract.Unsupported("ManageHardwareObject", TiaOpenness.Shared.ReleaseCapabilityQuery.Error("ManageHardwareObject",
                    HardwareContract.ReleaseKey, new JsonObject { ["action"] = action }, reason));
            return McpServer.RunNativeWritePreview("ManageHardwareObject", dryRun,
                new { action, devicePath = devicePath ?? Array.Empty<string>(), itemPath = itemPath ?? Array.Empty<string>(),
                    destinationDevicePath = destinationDevicePath ?? Array.Empty<string>(),
                    destinationItemPath = destinationItemPath ?? Array.Empty<string>(), position, typeIdentifier, subnetName },
                () => Invoke(true), () => Invoke(false), expectedPlanHash, expectedProjectFile);

            CallToolResult Invoke(bool preview) => HardwareContract.Run("ManageHardwareObject", () =>
            {
                var devicePathJson = HardwareContract.Input(PathValidator.Item(), devicePath ?? Array.Empty<string>(), "devicePath");
                var itemPathJson = HardwareContract.Input(PathValidator.Item(), itemPath ?? System.Array.Empty<string>(), "itemPath");
                var destinationDevicePathJson = HardwareContract
                    .Input(PathValidator.Item(), destinationDevicePath ?? System.Array.Empty<string>(), "destinationDevicePath");
                var destinationItemPathJson = HardwareContract
                    .Input(PathValidator.Item(), destinationItemPath ?? System.Array.Empty<string>(), "destinationItemPath");
                HardwareContract.Management(action, devicePath ?? Array.Empty<string>(), itemPath ?? Array.Empty<string>(),
                    destinationDevicePath ?? Array.Empty<string>(), position, typeIdentifier, subnetName);
                HardwareContract.RequireProject(_service.HasProject);
                return ManageHardwareObject(devicePathJson, action, itemPathJson, destinationDevicePathJson, destinationItemPathJson, position, preview,
                    typeIdentifier, subnetName);
            }, write: !preview, current: false);
        }

        public ResponseMessage ManageHardwareObject(
            [Description("devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name, e.g. " +
                "[\"PLC_2\"].")] string devicePathJson,
            [Description("action: deleteDevice | deleteItem | moveItem | copyItem | changeType | createSubnet | deleteSubnet.")] string action,
            [Description("itemPathJson: JSON array of exact device-item names (deleteItem / moveItem / copyItem); [] for deleteDevice.")] string itemPathJson=
                "[]",
            [Description("destinationDevicePathJson: for moveItem / copyItem - JSON array naming the destination station.")] string destinationDevicePathJson=
                "[]",
            [Description("destinationItemPathJson: for moveItem / copyItem - JSON array of device-item names of the destination " +
                "container.")] string destinationItemPathJson=
                "[]",
            [Description("position: for moveItem / copyItem - target slot / position number (-1 = let TIA pick).")] int position=-1,
            [Description("dryRun: true (default) validates and previews native impact; false executes.")] bool dryRun=true,
            string typeIdentifier = "", string subnetName = "")
            => _service.ManageHardwareObject(devicePathJson,action,itemPathJson,destinationDevicePathJson,destinationItemPathJson,position,dryRun,
                typeIdentifier,subnetName);
    }

}
