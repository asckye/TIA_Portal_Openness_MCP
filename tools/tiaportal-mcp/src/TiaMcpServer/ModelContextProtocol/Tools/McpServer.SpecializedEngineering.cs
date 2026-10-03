using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="ExchangeMotionCamData"), Description("[L2][PLC-TechnologyObjects][WRITE] Native cam text/binary/point-list export and text/binary import. Explicit native format/separator, new output file, default preview. Import requires Offline. No drive/motion command.")]
        public static ResponseMessage ExchangeMotionCamData(
            string softwarePath,
            string objectPath,
            [Description("export | import | exportBinary | importBinary | exportPoints. Text, native binary or point-list exchange.")] string action,
            string filePath,
            [Description("MCD | Scout | PointList. Native CamDataFormat for text export.")] string format="",
            [Description("Comma | Tab. Native CamDataFormatSeparator name for text import/export and point lists; do not pass a literal separator character.")] string separator="",
            [Description("pointCount: number of points to export.")] int pointCount=0,
            bool dryRun=true)
            => Portal.ExchangeMotionCamData(softwarePath,objectPath,action,filePath,format,separator,pointCount,dryRun);
        [McpServerTool(Name="ConfigureMotionHardwareConnection"), Description("[L2][PLC-TechnologyObjects][WRITE] Offline axis actor/sensor/torque hardware mapping read/connect/disconnect. Addresses are BIT addresses. Explicit sensor index; default preview. Native readback, no live drive or motion command.")]
        public static ResponseMessage ConfigureMotionHardwareConnection(
            string softwarePath,
            string objectPath,
            [Description("interfaceKind: actor | sensor | torque.")] string interfaceKind,
            [Description("action: the operation to perform - read | connect | disconnect.")] string action,
            [Description("Nonnegative integer input BIT address; e.g. byte 10 bit 0 is 80.")] int inputBitAddress=0,
            [Description("Nonnegative integer output BIT address; e.g. byte 10 bit 0 is 80.")] int outputBitAddress=0,
            [Description("connectOption: connect option name (Default or AllowAllModules).")] string connectOption="Default",
            int sensorIndex=0,
            bool dryRun=true)
            => Portal.ConfigureMotionHardwareConnection(softwarePath,objectPath,interfaceKind,action,inputBitAddress,outputBitAddress,connectOption,sensorIndex,dryRun);
        [McpServerTool(Name="ManageUnifiedEvent"), Description("[L2][HMI-Unified][WRITE] Exact screen/control event or property event read/create/update/delete. Object JSON path. Updates preserve omitted script fields. Mutations need preview token. No Script SyntaxCheck, script execution, save/compile/download.")]
        public static ResponseMessage ManageUnifiedEvent(
            string softwarePath,
            string objectPathJson,
            string eventType,
            [Description("action: the operation to perform - read | create | update | delete.")] string action="read",
            string propertyName="",
            [Description("scriptPropertiesJson: JSON object of script properties to set.")] string scriptPropertiesJson="{}",
            string expectedToken="",
            bool dryRun=true)
            => Portal.ManageUnifiedEvent(softwarePath,objectPathJson,eventType,action,propertyName,scriptPropertiesJson,expectedToken,dryRun);
    }
}
