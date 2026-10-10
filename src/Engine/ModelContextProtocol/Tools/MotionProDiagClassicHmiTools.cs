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
    internal sealed class MotionProDiagClassicHmiTools
    {
        private readonly MotionProDiagClassicHmiService _motionProDiagClassicHmi;

        public MotionProDiagClassicHmiTools(MotionProDiagClassicHmiService service) => _motionProDiagClassicHmi = service;

        [McpServerTool(Name="ManagePlcSupervision"), Description("[L2][PLC-Software][WRITE] ProDiag native object access on an exact PLC or block: read provider metadata (attributes, advertised compositions), readComposition/createEntry/deleteEntry through the official IEngineeringObject composition API (typeName must be advertised by GetCreationInfos), setAttributes on the provider, exportSettings/importSettings (.dat) via SupervisionSettingsProvider. Openness exposes no typed supervision list; XLSX bulk exchange is ExchangePlcSupervisions. Offline PLC for real writes; deletes need confirmDelete=true. Default preview; no save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManagePlcSupervision(
            string softwarePath,
            [Description("read | readComposition | createEntry | deleteEntry | setAttributes | exportSettings | importSettings. ")] string action,
            string blockPath="",
            [Description("providerKind: supervision | settings.")] string providerKind="supervision",
            [Description("compositionName: exact name of the composition (collection) on the object.")] string compositionName="",
            [Description("entryName: exact name of the entry inside the composition.")] string entryName="",
            [Description("typeName: exact type name of the entry to create.")] string typeName="",
            string filePath="",
            AttributeMap<Scalar> attributes = null!,
            int offset=0,
            int limit=100,
            bool confirmDelete=false,
            bool dryRun=true)
            => ClassicMotionToolContract.Invoke("ManagePlcSupervision", dryRun || action == "read" || action == "readComposition", action != "read" && action != "readComposition",
                () => _motionProDiagClassicHmi.ManagePlcSupervision(softwarePath,action,blockPath,providerKind,compositionName,entryName,typeName,filePath,ClassicMotionToolContract.Json(attributes),offset,limit,confirmDelete,dryRun));
        [McpServerTool(Name="ListClassicHmiScripts"), Description("[L2][HMI-Classic][READ] Classic (non-Unified) WinCC VB scripts under an exact script folder path: folder tree, script names and every readable attribute advertised by the object. Script source is not a typed Openness property; export it with ManageClassicHmiScript. Live pagination; Unified targets are refused.")]
        public CallToolResult ListClassicHmiScripts(string softwarePath,string folderPath="",int offset=0,int limit=100)
            => ClassicMotionToolContract.Invoke("ListClassicHmiScripts", true, false,
                () => _motionProDiagClassicHmi.ReadClassicHmiScripts(softwarePath,folderPath,offset,limit));
        [McpServerTool(Name="ManageClassicHmiScript"), Description("[L2][HMI-Classic][WRITE] Exact classic VB script or folder: read, export (new XML file, hashed), import (scriptPath=target folder; Override needs confirmDelete=true), delete (confirmDelete=true), createFolder/deleteFolder (empty only), setAttributes (advertised writable attributes, readback verified). No Create-from-code exists natively; new scripts come from XML import. Default preview; no save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageClassicHmiScript(
            string softwarePath,
            [Description("scriptPath: 'Folder/Script' path of the script.")] string scriptPath,
            [Description("read | export | import | delete | createFolder | deleteFolder | setAttributes. ")] string action,
            string filePath="",
            string importOptions="None",
            AttributeMap<Scalar> attributes = null!,
            bool confirmDelete=false,
            bool dryRun=true)
            => ClassicMotionToolContract.Invoke("ManageClassicHmiScript", dryRun || action == "read", action != "read",
                () => _motionProDiagClassicHmi.ManageClassicHmiScript(softwarePath,scriptPath,action,filePath,importOptions,ClassicMotionToolContract.Json(attributes),confirmDelete,dryRun));
        [McpServerTool(Name="ManageClassicHmiCycle"), Description("[L2][HMI-Classic][WRITE] Classic HMI cycles: read (all or exact cycleName with advertised time/unit attributes), export (new XML file), import (Override needs confirmDelete=true), delete (confirmDelete=true; system cycles refused), setAttributes (readback verified). CycleComposition has no native Create. Default preview; no save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageClassicHmiCycle(
            string softwarePath,
            [Description("read | export | import | delete | setAttributes. ")] string action,
            [Description("cycleName: exact cycle name.")] string cycleName="",
            string filePath="",
            string importOptions="None",
            AttributeMap<Scalar> attributes = null!,
            int offset=0,
            int limit=100,
            bool confirmDelete=false,
            bool dryRun=true)
            => ClassicMotionToolContract.Invoke("ManageClassicHmiCycle", dryRun || action == "read", action != "read",
                () => _motionProDiagClassicHmi.ManageClassicHmiCycle(softwarePath,action,cycleName,filePath,importOptions,ClassicMotionToolContract.Json(attributes),offset,limit,confirmDelete,dryRun));
        [McpServerTool(Name="ManageClassicHmiTextGraphicList"), Description("[L2][HMI-Classic][WRITE] Classic HMI text/graphic lists (listKind text/graphic): read (all or exact listName with advertised compositions), readEntries/createEntry/deleteEntry through an advertised compositionName (typeName from GetCreationInfos), export, import (Override needs confirmDelete=true), delete (confirmDelete=true), setAttributes. No native list Create; new lists arrive by XML import. Default preview; no save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageClassicHmiTextGraphicList(
            string softwarePath,
            [Description("listKind: text | graphic.")] string listKind,
            [Description("read | readEntries | createEntry | deleteEntry | export | import | delete | setAttributes. ")] string action,
            [Description("listName: exact list name.")] string listName="",
            [Description("compositionName: exact name of the composition (collection) on the object.")] string compositionName="",
            [Description("entryName: exact name of the entry inside the composition.")] string entryName="",
            [Description("typeName: exact type name of the entry to create.")] string typeName="",
            string filePath="",
            string importOptions="None",
            AttributeMap<Scalar> attributes = null!,
            int offset=0,
            int limit=100,
            bool confirmDelete=false,
            bool dryRun=true)
            => ClassicMotionToolContract.Invoke("ManageClassicHmiTextGraphicList", dryRun || action == "read" || action == "readEntries", action != "read" && action != "readEntries",
                () => _motionProDiagClassicHmi.ManageClassicHmiTextGraphicList(softwarePath,listKind,action,listName,compositionName,entryName,typeName,filePath,importOptions,ClassicMotionToolContract.Json(attributes),offset,limit,confirmDelete,dryRun));
        [McpServerTool(Name="GetClassicHmiGlobalization"), Description("[L2][HMI-Classic][READ] Classic HMI multilingual graphics via the native GraphicsProvider service: names, scalar properties and advertised attributes with live pagination. NotSupported on V20 (service type absent). Image bytes are not read.")]
        public CallToolResult GetClassicHmiGlobalization(string softwarePath,int offset=0,int limit=100)
            => ClassicMotionToolContract.Invoke("GetClassicHmiGlobalization", true, false,
                () => _motionProDiagClassicHmi.ReadClassicHmiGlobalization(softwarePath,offset,limit));
        [McpServerTool(Name="ListClassicHmiFaceplates"), Description("[L2][HMI-Classic][READ] Classic HMI faceplate (or vbScript/cScript/all) library types with versions from the project library or an exact open global library, under an exact type folder path. Scalar properties only; live pagination; no instantiation.")]
        public CallToolResult ListClassicHmiFaceplates(
            [Description("kind: faceplate | vbScript | cScript | all.")] string kind="faceplate",
            string libraryName="",
            string folderPath="",
            int offset=0,
            int limit=100)
            => ClassicMotionToolContract.Invoke("ListClassicHmiFaceplates", true, false,
                () => _motionProDiagClassicHmi.ReadClassicHmiFaceplates(kind,libraryName,folderPath,offset,limit));
        [McpServerTool(Name="ExportPlcProDiagInfo"), Description("[L2][PLC-Software][READ] Native CodeBlock.ExportProDIAGInfo: writes the alarm messages of one exact ProDiag FB (blockPath under BlockGroup or a unit's BlockGroup) as CSV files into an existing directoryPath on the TIA Portal machine and hashes the new files. Refused before the call unless the block is a ProDiag FB and consistent (compile first). Default dryRun=true; no project change. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ExportPlcProDiagInfo(
            string softwarePath,
            string blockPath,
            [Description("directoryPath: folder on the TIA machine that receives the files.")] string directoryPath,
            string unitName="",
            [Description("unitKind: which unit collection unitName refers to - unit | safety.")] string unitKind="unit",
            bool dryRun=true)
            => ClassicMotionToolContract.Invoke("ExportPlcProDiagInfo", dryRun, true,
                () => _motionProDiagClassicHmi.ExportPlcProDiagInfo(softwarePath,blockPath,directoryPath,unitName,unitKind,dryRun));

    }
}
