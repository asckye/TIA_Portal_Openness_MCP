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
        [McpServerTool(Name="ManageHardwareObject"), Description("[L2][Hardware][WRITE] Native deleteDevice/deleteItem/moveItem/copyItem. devicePath=[group,...,station] or [unique exact station name]; itemPath lists exact child names, preserving slashes in names. Move/copy require destinationDevicePath,destinationItemPath,position and pass native CanPlug check. dryRun=true default. Deletes may remove contained software. No save/download/online control.")]
        public CallToolResult ManageHardwareObjectV4(
            [Description("devicePath: JSON array naming the station - [group, ..., station] or the unique station name, e.g. [\"PLC_2\"].")] string[] devicePath,
            [Description("action: deleteDevice | deleteItem | moveItem | copyItem.")] string action,
            [Description("itemPath: JSON array of exact device-item names (deleteItem / moveItem / copyItem); [] for deleteDevice.")] string[]? itemPath = null,
            [Description("destinationDevicePath: for moveItem / copyItem - JSON array naming the destination station.")] string[]? destinationDevicePath = null,
            [Description("destinationItemPath: for moveItem / copyItem - JSON array of device-item names of the destination container.")] string[]? destinationItemPath = null,
            [Description("position: for moveItem / copyItem - target slot / position number (-1 = let TIA pick).")] int position=-1,
            [Description("dryRun: true (default) previews (CanPlug check only); false executes.")] bool dryRun=true)
            => HardwareContract.Run("ManageHardwareObject", () =>
            {
                var devicePathJson = HardwareContract.Input(PathValidator.Device(), devicePath, "devicePath");
                var itemPathJson = HardwareContract.Input(PathValidator.Item(), itemPath ?? System.Array.Empty<string>(), "itemPath");
                var destinationDevicePathJson = HardwareContract.Input(PathValidator.Item(), destinationDevicePath ?? System.Array.Empty<string>(), "destinationDevicePath");
                var destinationItemPathJson = HardwareContract.Input(PathValidator.Item(), destinationItemPath ?? System.Array.Empty<string>(), "destinationItemPath");
                HardwareContract.Management(action, itemPath ?? System.Array.Empty<string>(), destinationDevicePath ?? System.Array.Empty<string>(), position);
                HardwareContract.RequireProject(_service.HasProject);
                return ManageHardwareObject(devicePathJson, action, itemPathJson, destinationDevicePathJson, destinationItemPathJson, position, dryRun);
            }, write: !dryRun, current: false);

        public ResponseMessage ManageHardwareObject(
            [Description("devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name, e.g. [\"PLC_2\"].")] string devicePathJson,
            [Description("action: deleteDevice | deleteItem | moveItem | copyItem.")] string action,
            [Description("itemPathJson: JSON array of exact device-item names (deleteItem / moveItem / copyItem); [] for deleteDevice.")] string itemPathJson="[]",
            [Description("destinationDevicePathJson: for moveItem / copyItem - JSON array naming the destination station.")] string destinationDevicePathJson="[]",
            [Description("destinationItemPathJson: for moveItem / copyItem - JSON array of device-item names of the destination container.")] string destinationItemPathJson="[]",
            [Description("position: for moveItem / copyItem - target slot / position number (-1 = let TIA pick).")] int position=-1,
            [Description("dryRun: true (default) previews (CanPlug check only); false executes.")] bool dryRun=true)
            => _service.ManageHardwareObject(devicePathJson,action,itemPathJson,destinationDevicePathJson,destinationItemPathJson,position,dryRun);
    }

}
