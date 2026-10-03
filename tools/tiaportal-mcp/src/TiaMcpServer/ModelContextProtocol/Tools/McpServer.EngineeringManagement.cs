using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="SetPlcUnitObjectAccess"), Description("[L2][PLC-Software][WRITE] Publish/unpublish a block or PLC type inside an exact software unit using official Access attribute. access=Published/Unpublished. objectPath includes nested groups relative to the unit's block/type root; OB publication is refused by native API. dryRun=true default; writes require Offline and readback. No save/compile/download.")]
        public static ResponseMessage SetPlcUnitObjectAccess(
            string softwarePath,
            string unitName,
            [Description("block | type. Object family inside the exact software unit.")] string objectKind,
            string objectPath,
            [Description("Published | Unpublished. Native software-unit object publication state.")] string access,
            bool dryRun=true)
            => Portal.SetPlcUnitObjectAccess(softwarePath,unitName,objectKind,objectPath,access,dryRun);
        [McpServerTool(Name="ManageHardwareObject"), Description("[L2][Hardware][WRITE] Native deleteDevice/deleteItem/moveItem/copyItem. devicePathJson=[group,...,station] or [unique exact station name]; itemPathJson lists exact child names, preserving slashes in names. Move/copy require destinationDevicePathJson,destinationItemPathJson,position and pass native CanPlug check. dryRun=true default. Deletes may remove contained software. No save/download/online control.")]
        public static ResponseMessage ManageHardwareObject(
            [Description("devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name, e.g. [\"PLC_2\"].")] string devicePathJson,
            [Description("action: deleteDevice | deleteItem | moveItem | copyItem.")] string action,
            [Description("itemPathJson: JSON array of exact device-item names (deleteItem / moveItem / copyItem); [] for deleteDevice.")] string itemPathJson="[]",
            [Description("destinationDevicePathJson: for moveItem / copyItem - JSON array naming the destination station.")] string destinationDevicePathJson="[]",
            [Description("destinationItemPathJson: for moveItem / copyItem - JSON array of device-item names of the destination container.")] string destinationItemPathJson="[]",
            [Description("position: for moveItem / copyItem - target slot / position number (-1 = let TIA pick).")] int position=-1,
            [Description("dryRun: true (default) previews (CanPlug check only); false executes.")] bool dryRun=true)
            => Portal.ManageHardwareObject(devicePathJson,action,itemPathJson,destinationDevicePathJson,destinationItemPathJson,position,dryRun);
    }
}
