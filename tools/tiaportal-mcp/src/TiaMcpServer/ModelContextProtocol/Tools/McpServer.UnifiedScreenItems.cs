using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="DescribeUnifiedScreenItemType"), Description("[L2][HMI-Unified][READ] Typed catalog and schema of WinCC Unified screen item types from the loaded official API (no project needed): empty itemType lists every concrete UI.Shapes / UI.Widgets / UI.Controls / UI.Screens item type with its IHmi*Feature interfaces; a name (HmiCircle, Circle, Widgets.HmiSlider or full CLR name) returns its properties classified as scalar/color/multilingual/part/collection/reference with CLR type, enum values, writability, part sub-properties to depth, event types and Delete availability. Use before ManageUnifiedScreenItem so property names and enums are exact.")]
        public static ResponseMessage DescribeUnifiedScreenItemType(string itemType="",int depth=2)
            => Portal.DescribeUnifiedScreenItemType(itemType,depth);
        [McpServerTool(Name="ManageUnifiedScreenItem"), Description("[L2][HMI-Unified][WRITE] Any screen item type on one exact Unified screen (screenPath = unique screen name or /Group/Screen): list (name/type/geometry, paged), read (scalars, #AARRGGBB colors, parts and collections to depth, every MultilingualText language, features, event/dynamization counts), create (itemType from DescribeUnifiedScreenItemType via native Create<T>(name) or Create<T>(name, containedType) for faceplate/custom widget containers, with initial propertiesJson), update, delete (confirmDelete=true). propertiesJson nests parts as objects ({\"Font\":{\"Size\":14},\"BackColor\":\"#FF0000FF\"}) and multilingual texts per culture ({\"Text\":{\"en-US\":\"Start\"}}); every leaf is read back. Default preview; no save/compile/download. Events: ManageUnifiedEvent; dynamizations: ManageUnifiedDynamization.")]
        public static ResponseMessage ManageUnifiedScreenItem(string softwarePath,string screenPath,string action="read",string itemName="",string itemType="",string propertiesJson="{}",int depth=2,bool confirmDelete=false,int offset=0,int limit=100,bool dryRun=true,string containedType="")
            => Portal.ManageUnifiedScreenItem(softwarePath,screenPath,action,itemName,itemType,propertiesJson,depth,confirmDelete,offset,limit,dryRun,containedType);
    }
}
