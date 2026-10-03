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
        [McpServerTool(Name="ManageLibraryTypeVersion"), Description("[L2][Library][WRITE] Exact library type/version read/edit/release/setDefault/deleteVersion/updateInstances/discard/findInstances. Empty libraryName selects project library; otherwise unique already-open global library. typePath relative to TypeFolder. Release requires newVersion and official dependenciesMode. findInstances is read-only and requires exact targetSoftwarePath; discard removes the selected editable version. updateInstances requires exact targetSoftwarePath; native type update chooses its applicable versions, not necessarily version argument. dryRun=true default. No export, save, close or compile. Semantic validity/dependency impact determined by native TIA.")]
        public static ResponseMessage ManageLibraryTypeVersion(
            string typePath,
            string version,
            [Description("action: the operation to perform - read | edit | release | setDefault | deleteVersion | updateInstances | discard | findInstances.")] string action,
            string libraryName="",
            [Description("newVersion: version string of the new type version, e.g. 'V1.0.1'.")] string newVersion="",
            [Description("dependenciesMode: how dependent types are handled by the action ('' = default).")] string dependenciesMode="",
            [Description("author: author text of the version.")] string author="",
            [Description("comment: comment text.")] string comment="",
            string targetSoftwarePath="",
            bool dryRun=true)
            => Portal.ManageLibraryTypeVersion(typePath,version,action,libraryName,newVersion,dependenciesMode,author,comment,targetSoftwarePath,dryRun);
        [McpServerTool(Name="CreateLibraryMasterCopy"), Description("[L2][Library][WRITE] Create a native master copy from an exact block/type/device/screen in an existing library folder. sourcePath is relative object path; for device use JSON array of exact group/station names. Empty libraryName=project library; otherwise already-open global library. dryRun=true default. Native IMasterCopySource required; no automatic save or close.")]
        public static ResponseMessage CreateLibraryMasterCopy(
            [Description("sourceKind: block | type | device | screen.")] string sourceKind,
            [Description("sourcePath: 'Group/Name' of the source object.")] string sourcePath,
            string softwarePath="",
            string folderPath="",
            string libraryName="",
            bool dryRun=true)
            => Portal.CreateLibraryMasterCopy(sourceKind,sourcePath,softwarePath,folderPath,libraryName,dryRun);
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

        [McpServerTool(Name="ManageTechnologyObject"), Description("[L2][PLC-Software][WRITE] Native technology object read/create/delete/setParameter. Exact objectPath relative to TechnologicalObjectGroup, including user folders. create requires official typeIdentifier and version (official 'Overview of technology objects and versions': S7-1500 TO_PositioningAxis / TO_SpeedAxis / ... >= V5.0 with FW >= 2.8, PID_Compact >= V2.3; version as 'major.minor'). WARNING (real project, crash 9 in the handoff): Create(\"MCP_Axis\", \"TO_PositioningAxis\", 6.0) on a CPU 1515F-2 PN V2.9 in TIA V21 threw NonRecoverableException and TIA Portal exited, while TO_PositioningAxis / TO_SpeedAxis 5.0 and PID_Compact 2.3 were created normally on the same CPU - a version the CPU does not offer is not refused cleanly, so use the lowest version of the official table (S7-1500 motion 5.0, PID_Compact 2.3) and save the project before a create. setParameter takes exact parameter name and scalar valueJson. dryRun=true default; writes require Offline. No save/compile/download; dependencies not analyzed.")]
        public static ResponseMessage ManageTechnologyObject(
            string softwarePath,
            string objectPath,
            [Description("action: the operation to perform - read | create | delete | setParameter.")] string action,
            [Description("typeIdentifier: catalog type identifier of the form 'OrderNumber:6ES7 ...' or 'OrderNumber:.../V2.9' (SearchHardwareCatalog / ManageHardwareUtilities normalizeTypeIdentifier).")] string typeIdentifier="",
            string version="",
            [Description("parameter: exact parameter name.")] string parameter="",
            [Description("valueJson: the value to write, as JSON (number, string, boolean or object as the parameter expects).")] string valueJson="null",
            bool dryRun=true)
            => Portal.ManageTechnologyObject(softwarePath,objectPath,action,typeIdentifier,version,parameter,valueJson,dryRun);
    }
}
