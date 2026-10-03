using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class HardwareManagementTools
    {
        private readonly HardwareManagementService _service;

        public HardwareManagementTools(HardwareManagementService service) => _service = service;
        [McpServerTool(Name="ManageHardwareObject"), Description("[L2][Hardware][WRITE] Native deleteDevice/deleteItem/moveItem/copyItem. devicePathJson=[group,...,station] or [unique exact station name]; itemPathJson lists exact child names, preserving slashes in names. Move/copy require destinationDevicePathJson,destinationItemPathJson,position and pass native CanPlug check. dryRun=true default. Deletes may remove contained software. No save/download/online control.")]
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
