using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="ReadMotionAxisConfiguration"), Description("[L2][PLC-TechnologyObjects][READ] Exact Motion technology object (axis/cam/kinematics/output cam/measuring input/ident) read: scalar object values plus every native Motion/Ident service it provides (hardware interfaces, master-value couplings, interpreter mappings, cam/interpreter capabilities) and, under typed, the same data as typed rows (AxisHardwareConnectionProvider actor / sensors / torque, encoder, measuring input, output cam, master values, V21 TOMapping / DBMemberMapping / Ident). Absent services are reported by state. Optional paginated parameters. No drive or motion command.")]
        public static ResponseMessage ReadMotionAxisConfiguration(
            string softwarePath,
            string objectPath,
            [Description("includeParameters: true also returns parameters.")] bool includeParameters=false,
            int offset=0,
            int limit=100)
            => Portal.ReadMotionAxisConfiguration(softwarePath,objectPath,includeParameters,offset,limit);
        [McpServerTool(Name="ManageMotionAxis"), Description("[L2][PLC-TechnologyObjects][WRITE] Native Motion TO operations beyond cam/bit-address tools: add/removeMasterValue (aspect synchronous*/conveyor*/superimposingSetPoint, name=exact master TO path), create/update/deleteMapping (aspect toMapping/dbMemberMapping, name=alias, propertiesJson), connect/disconnect (aspect actor/sensor/torque/encoder/measuringInput/outputCam; targetJson selects exactly one native overload: devicePath+itemPath[+secondItemPath|channelIndex|channelType+channelIoType+channelNumber for Connect(Channel)], dbMemberPath, plcTagPath, address, or encoder bit addresses; typed AxisEncoderHardwareConnectionInterface / TorqueHardwareConnectionInterface / MeasuringInput / OutputCam calls with typed readback rows), connectIdent (deviceItem target). Exact names; Offline PLC for real writes; deletes need confirmDelete=true. Default preview; native readback; no save/compile/download, never commands a drive.")]
        public static ResponseMessage ManageMotionAxis(
            string softwarePath,
            string objectPath,
            string action,
            [Description("aspect: which aspect of the axis to manage (see the tool description).")] string aspect="",
            string name="",
            [Description("targetJson: JSON object naming the target (see the tool description).")] string targetJson="{}",
            string propertiesJson="{}",
            int sensorIndex=0,
            bool confirmDelete=false,
            bool dryRun=true)
            => Portal.ManageMotionAxis(softwarePath,objectPath,action,aspect,name,targetJson,propertiesJson,sensorIndex,confirmDelete,dryRun);
        [McpServerTool(Name="ManagePlcSupervision"), Description("[L2][PLC-Software][WRITE] ProDiag native object access on an exact PLC or block: read provider metadata (attributes, advertised compositions), readComposition/createEntry/deleteEntry through the official IEngineeringObject composition API (typeName must be advertised by GetCreationInfos), setAttributes on the provider, exportSettings/importSettings (.dat) via SupervisionSettingsProvider. Openness exposes no typed supervision list; XLSX bulk exchange is ExchangePlcSupervisions. Offline PLC for real writes; deletes need confirmDelete=true. Default preview; no save/compile/download.")]
        public static ResponseMessage ManagePlcSupervision(
            string softwarePath,
            string action,
            string blockPath="",
            [Description("providerKind: supervision | settings.")] string providerKind="supervision",
            [Description("compositionName: exact name of the composition (collection) on the object.")] string compositionName="",
            [Description("entryName: exact name of the entry inside the composition.")] string entryName="",
            [Description("typeName: exact type name of the entry to create.")] string typeName="",
            string filePath="",
            string attributesJson="{}",
            int offset=0,
            int limit=100,
            bool confirmDelete=false,
            bool dryRun=true)
            => Portal.ManagePlcSupervision(softwarePath,action,blockPath,providerKind,compositionName,entryName,typeName,filePath,attributesJson,offset,limit,confirmDelete,dryRun);
        [McpServerTool(Name="ReadClassicHmiScripts"), Description("[L2][HMI-Classic][READ] Classic (non-Unified) WinCC VB scripts under an exact script folder path: folder tree, script names and every readable attribute advertised by the object. Script source is not a typed Openness property; export it with ManageClassicHmiScript. Live pagination; Unified targets are refused.")]
        public static ResponseMessage ReadClassicHmiScripts(string softwarePath,string folderPath="",int offset=0,int limit=100)
            => Portal.ReadClassicHmiScripts(softwarePath,folderPath,offset,limit);
        [McpServerTool(Name="ManageClassicHmiScript"), Description("[L2][HMI-Classic][WRITE] Exact classic VB script or folder: read, export (new XML file, hashed), import (scriptPath=target folder; Override needs confirmDelete=true), delete (confirmDelete=true), createFolder/deleteFolder (empty only), setAttributes (advertised writable attributes, readback verified). No Create-from-code exists natively; new scripts come from XML import. Default preview; no save/compile/download.")]
        public static ResponseMessage ManageClassicHmiScript(
            string softwarePath,
            [Description("scriptPath: 'Folder/Script' path of the script.")] string scriptPath,
            string action,
            string filePath="",
            string importOptions="None",
            string attributesJson="{}",
            bool confirmDelete=false,
            bool dryRun=true)
            => Portal.ManageClassicHmiScript(softwarePath,scriptPath,action,filePath,importOptions,attributesJson,confirmDelete,dryRun);
        [McpServerTool(Name="ManageClassicHmiCycle"), Description("[L2][HMI-Classic][WRITE] Classic HMI cycles: read (all or exact cycleName with advertised time/unit attributes), export (new XML file), import (Override needs confirmDelete=true), delete (confirmDelete=true; system cycles refused), setAttributes (readback verified). CycleComposition has no native Create. Default preview; no save/compile/download.")]
        public static ResponseMessage ManageClassicHmiCycle(
            string softwarePath,
            string action,
            [Description("cycleName: exact cycle name.")] string cycleName="",
            string filePath="",
            string importOptions="None",
            string attributesJson="{}",
            int offset=0,
            int limit=100,
            bool confirmDelete=false,
            bool dryRun=true)
            => Portal.ManageClassicHmiCycle(softwarePath,action,cycleName,filePath,importOptions,attributesJson,offset,limit,confirmDelete,dryRun);
        [McpServerTool(Name="ManageClassicHmiTextGraphicList"), Description("[L2][HMI-Classic][WRITE] Classic HMI text/graphic lists (listKind text/graphic): read (all or exact listName with advertised compositions), readEntries/createEntry/deleteEntry through an advertised compositionName (typeName from GetCreationInfos), export, import (Override needs confirmDelete=true), delete (confirmDelete=true), setAttributes. No native list Create; new lists arrive by XML import. Default preview; no save/compile/download.")]
        public static ResponseMessage ManageClassicHmiTextGraphicList(
            string softwarePath,
            [Description("listKind: text | graphic.")] string listKind,
            string action,
            [Description("listName: exact list name.")] string listName="",
            [Description("compositionName: exact name of the composition (collection) on the object.")] string compositionName="",
            [Description("entryName: exact name of the entry inside the composition.")] string entryName="",
            [Description("typeName: exact type name of the entry to create.")] string typeName="",
            string filePath="",
            string importOptions="None",
            string attributesJson="{}",
            int offset=0,
            int limit=100,
            bool confirmDelete=false,
            bool dryRun=true)
            => Portal.ManageClassicHmiTextGraphicList(softwarePath,listKind,action,listName,compositionName,entryName,typeName,filePath,importOptions,attributesJson,offset,limit,confirmDelete,dryRun);
        [McpServerTool(Name="ReadClassicHmiGlobalization"), Description("[L2][HMI-Classic][READ] Classic HMI multilingual graphics via the native GraphicsProvider service: names, scalar properties and advertised attributes with live pagination. NotSupported on V20 (service type absent). Image bytes are not read.")]
        public static ResponseMessage ReadClassicHmiGlobalization(string softwarePath,int offset=0,int limit=100)
            => Portal.ReadClassicHmiGlobalization(softwarePath,offset,limit);
        [McpServerTool(Name="ReadClassicHmiFaceplates"), Description("[L2][HMI-Classic][READ] Classic HMI faceplate (or vbScript/cScript/all) library types with versions from the project library or an exact open global library, under an exact type folder path. Scalar properties only; live pagination; no instantiation.")]
        public static ResponseMessage ReadClassicHmiFaceplates(
            [Description("kind: faceplate | vbScript | cScript | all.")] string kind="faceplate",
            string libraryName="",
            string folderPath="",
            int offset=0,
            int limit=100)
            => Portal.ReadClassicHmiFaceplates(kind,libraryName,folderPath,offset,limit);
    }
}
