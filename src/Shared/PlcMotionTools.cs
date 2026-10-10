using ModelContextProtocol.Protocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens.Services;
using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class PlcMotionTools
    {
        private readonly PlcOrganisationPortService _technology;
        public PlcMotionTools(PlcOrganisationPortService service) => _technology = service;
        [McpServerTool(Name="GetMotionAxisConfiguration"), Description("[L2][PLC-TechnologyObjects][READ] Exact Motion technology object (axis/cam/kinematics/output cam/measuring input/ident) read: scalar object values plus every native Motion/Ident service it provides (hardware interfaces, master-value couplings, interpreter mappings, cam/interpreter capabilities) and, under typed, the same data as typed rows (AxisHardwareConnectionProvider actor / sensors / torque, encoder, measuring input, output cam, master values, V21 TOMapping / DBMemberMapping / Ident). Absent services are reported by state. Optional paginated parameters. No drive or motion command.")]
        public CallToolResult GetMotionAxisConfiguration(
            string softwarePath,
            string objectPath,
            [Description("includeParameters: true also returns parameters.")] bool includeParameters=false,
            int offset=0,
            int limit=100)
            => ClassicMotionToolContract.Invoke("GetMotionAxisConfiguration", true, false,
                () => _technology.ReadMotionAxisConfiguration(softwarePath,objectPath,includeParameters,offset,limit));
        [McpServerTool(Name="ManageMotionAxis"), Description("[L2][PLC-TechnologyObjects][WRITE] Native Motion TO operations beyond cam/bit-address tools: add/removeMasterValue (aspect synchronous*/conveyor*/superimposingSetPoint, name=exact master TO path), create/update/deleteMapping (aspect toMapping/dbMemberMapping, name=alias, properties), connect/disconnect (aspect actor/sensor/torque/encoder/measuringInput/outputCam; target selects exactly one native overload: devicePath+itemPath[+secondItemPath|channelIndex|channelType+channelIoType+channelNumber for Connect(Channel)], dbMemberPath, plcTagPath, address, or encoder bit addresses; typed AxisEncoderHardwareConnectionInterface / TorqueHardwareConnectionInterface / MeasuringInput / OutputCam calls with typed readback rows), connectIdent (deviceItem target). Exact names; Offline PLC for real writes; deletes need confirmDelete=true. Default preview; native readback; no save/compile/download, never commands a drive. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageMotionAxis(
            string softwarePath,
            string objectPath,
            [Description("read | addMasterValue | removeMasterValue | createMapping | updateMapping | deleteMapping | connect | disconnect | connectIdent. ")] string action,
            [Description("aspect: which aspect of the axis to manage (see the tool description).")] string aspect="",
            string name="",
            [Description("target: Object naming the target (see the tool description).")] MotionTarget target = null!,
            AttributeMap<Scalar> properties = null!,
            int sensorIndex=0,
            bool confirmDelete=false,
            bool dryRun=true)
            => ClassicMotionToolContract.Invoke("ManageMotionAxis", dryRun || action == "read", action != "read",
                () => _technology.ManageMotionAxis(softwarePath,objectPath,action,aspect,name,ClassicMotionToolContract.Json(target),ClassicMotionToolContract.Json(properties),sensorIndex,confirmDelete,dryRun));
        [McpServerTool(Name="ExchangeMotionCamData"), Description("[L2][PLC-TechnologyObjects][WRITE] Native cam text/binary/point-list export and text/binary import. Explicit native format/separator, new output file, default preview. Import requires Offline. No drive/motion command. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ExchangeMotionCamData(
            string softwarePath,
            string objectPath,
            [Description("export | import | exportBinary | importBinary | exportPoints. Text, native binary or point-list exchange.")] string action,
            [Description("filePath: absolute cam data file. Text/point-list actions use the selected format and separator (e.g. Cam_1.txt); binary actions use the native binary format. New output or existing import file.")] string filePath,
            [Description("MCD | Scout | PointList. Native CamDataFormat for text export.")] string format="",
            [Description("Comma | Tab. Native CamDataFormatSeparator name for text import/export and point lists; do not pass a literal separator character.")] string separator="",
            [Description("pointCount: number of points to export.")] int pointCount=0,
            bool dryRun=true)
            => ClassicMotionToolContract.Invoke("ExchangeMotionCamData", dryRun, true,
                () => _technology.ExchangeMotionCamData(softwarePath,objectPath,action,filePath,format,separator,pointCount,dryRun));

        [McpServerTool(Name="ConfigureMotionHardwareConnection"), Description("[L2][PLC-TechnologyObjects][WRITE] Offline axis actor/sensor/torque hardware mapping read/connect/disconnect. Addresses are BIT addresses. Explicit sensor index; default preview. Native readback, no live drive or motion command. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ConfigureMotionHardwareConnection(
            string softwarePath,
            string objectPath,
            [Description("interfaceKind: actor | sensor | torque.")] string interfaceKind,
            [Description("action: the operation to perform - read | connect | disconnect.")] string action,
            [Description("Nonnegative integer input BIT address; e.g. byte 10 bit 0 is 80.")] int inputBitAddress=0,
            [Description("Nonnegative integer output BIT address; e.g. byte 10 bit 0 is 80.")] int outputBitAddress=0,
            [Description("connectOption: connect option name (Default or AllowAllModules).")] string connectOption="Default",
            int sensorIndex=0,
            bool dryRun=true)
            => ClassicMotionToolContract.Invoke("ConfigureMotionHardwareConnection", dryRun || action == "read", action != "read",
                () => _technology.ConfigureMotionHardwareConnection(softwarePath,objectPath,interfaceKind,action,inputBitAddress,outputBitAddress,connectOption,sensorIndex,dryRun));
    }
}
