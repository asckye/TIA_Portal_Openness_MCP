using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="ExchangePlcSupervisions"), Description("[L2][PLC][WRITE] ProDiag XLSX export/import/importSettings, explicit native options and diagnostic state. Import requires offline PLC; output is new and hashed. Default preview; no save/compile/download.")]
        public static ResponseMessage ExchangePlcSupervisions(string softwarePath,string action,string filePath,string importOptions="None",bool dryRun=true)
            => Portal.ExchangePlcSupervisions(softwarePath,action,filePath,importOptions,dryRun);
        [McpServerTool(Name="ExchangeCfcCharts"), Description("[L2][PLC][WRITE] CFC ChartProviderS7 native ZIP exchange; explicit modelVersion/filter. deleteAtTarget=false; default preview. Native chart completeness not asserted. Import requires offline PLC; no save/compile/download.")]
        public static ResponseMessage ExchangeCfcCharts(string softwarePath,string action,string filePath,string modelVersion,long filter,bool unattended=true,bool deleteAtTarget=false,bool dryRun=true)
            => Portal.ExchangeCfcCharts(softwarePath,action,filePath,modelVersion,filter,unattended,deleteAtTarget,dryRun);
        [McpServerTool(Name="ReadTestSuiteCases"), Description("[L2][Project][READ] Siemens Test Suite styleGuide/application/system case or rule-set scalar read. Exact name or live offset pagination. Does not execute tests.")]
        public static ResponseMessage ReadTestSuiteCases(string category,string name="",int offset=0,int limit=100)
            => Portal.ReadTestSuiteCases(category,name,offset,limit);
        [McpServerTool(Name="ExchangeTestSuiteCase"), Description("[L2][Project][WRITE] Native Test Suite definition import/export/delete. Import has explicit native loadOptions and may affect multiple definitions. Default preview; no test execution. New output files, hashed.")]
        public static ResponseMessage ExchangeTestSuiteCase(string category,string action,string name,string filePath="",string importOptions="None",string loadOptions="",bool dryRun=true)
            => Portal.ExchangeTestSuiteCase(category,action,name,filePath,importOptions,loadOptions,dryRun);
        [McpServerTool(Name="RunTestSuiteCase"), Description("[L2][Project][EXECUTE] Execute one exact Siemens Test Suite rule set/case. Default preview; application/system require confirmExternalExecution=true because configured simulation or servers may be affected. Returns actual testPassed and native diagnostics.")]
        public static ResponseMessage RunTestSuiteCase(string category,string name,bool confirmExternalExecution=false,bool dryRun=true)
            => Portal.RunTestSuiteCase(category,name,confirmExternalExecution,dryRun);
        [McpServerTool(Name="ExchangeMotionCamData"), Description("[L2][PLC][WRITE] Native cam text/binary/point-list export and text/binary import. Explicit native format/separator, new output file, default preview. Import requires Offline. No drive/motion command.")]
        public static ResponseMessage ExchangeMotionCamData(string softwarePath,string objectPath,string action,string filePath,string format="",string separator="",int pointCount=0,bool dryRun=true)
            => Portal.ExchangeMotionCamData(softwarePath,objectPath,action,filePath,format,separator,pointCount,dryRun);
        [McpServerTool(Name="ConfigureMotionHardwareConnection"), Description("[L2][PLC][WRITE] Offline axis actor/sensor/torque hardware mapping read/connect/disconnect. Addresses are BIT addresses. Explicit sensor index; default preview. Native readback, no live drive or motion command.")]
        public static ResponseMessage ConfigureMotionHardwareConnection(string softwarePath,string objectPath,string interfaceKind,string action,int inputBitAddress=0,int outputBitAddress=0,string connectOption="Default",int sensorIndex=0,bool dryRun=true)
            => Portal.ConfigureMotionHardwareConnection(softwarePath,objectPath,interfaceKind,action,inputBitAddress,outputBitAddress,connectOption,sensorIndex,dryRun);
        [McpServerTool(Name="ManageUnifiedEvent"), Description("[L2][HMI-Unified][WRITE] Exact screen/control event or property event read/create/update/delete. Object JSON path. Updates preserve omitted script fields. Mutations need preview token. No Script SyntaxCheck, script execution, save/compile/download.")]
        public static ResponseMessage ManageUnifiedEvent(string softwarePath,string objectPathJson,string eventType,string action="read",string propertyName="",string scriptPropertiesJson="{}",string expectedToken="",bool dryRun=true)
            => Portal.ManageUnifiedEvent(softwarePath,objectPathJson,eventType,action,propertyName,scriptPropertiesJson,expectedToken,dryRun);
        [McpServerTool(Name="ManageStartdriveParameter"), Description("[L2][Hardware][WRITE] Exact offline DriveObjectContainer/drive number/parameter read or write. Native indexed parameter name, value conversion and readback. Default preview. Never uses OnlineDriveObjectContainer or commands a drive.")]
        public static ResponseMessage ManageStartdriveParameter(string devicePathJson,string itemPathJson,ushort driveObjectNumber,string parameter,string action="read",string valueJson="null",bool dryRun=true)
            => Portal.ManageStartdriveParameter(devicePathJson,itemPathJson,driveObjectNumber,parameter,action,valueJson,dryRun);
        [McpServerTool(Name="ReadSiVArcRules"), Description("[L2][HMI][READ] Exact SiVArc rule category and property-only JSON path; live scalar pagination with schema, complex values excluded. No generation.")]
        public static ResponseMessage ReadSiVArcRules(string category,string objectPathJson="[]",int offset=0,int limit=100)
            => Portal.ReadSiVArcRules(category,objectPathJson,offset,limit);
        [McpServerTool(Name="ManageSiVArcRule"), Description("[L2][HMI][WRITE] Native SiVArc rule/folder/table composition create/update/delete with exact collection path and name. Nonempty container deletion refused. Default preview; no generation/save/compile/download.")]
        public static ResponseMessage ManageSiVArcRule(string category,string collectionPathJson,string name,string action,string propertiesJson="{}",bool dryRun=true)
            => Portal.ManageSiVArcRule(category,collectionPathJson,name,action,propertiesJson,dryRun);
        [McpServerTool(Name="GenerateSiVArc"), Description("[L2][HMI][WRITE] Native SiVArc generation for one exact HMI device and explicit PLC paths. Requires native GenerationOptions. Default preview; can create/replace HMI objects according to rules. No automatic save/compile/download.")]
        public static ResponseMessage GenerateSiVArc(string hmiDeviceName,string plcSoftwarePathsJson,string generationOptions,bool dryRun=true)
            => Portal.GenerateSiVArc(hmiDeviceName,plcSoftwarePathsJson,generationOptions,dryRun);
        [McpServerTool(Name="ManageLibraryMasterCopy"), Description("[L2][Library][WRITE] Exact master-copy read/copy/compare/delete. copy destinationPath is a folder; compare uses exact master-copy path. No overwrites or automatic save. Default preview for mutations.")]
        public static ResponseMessage ManageLibraryMasterCopy(string sourcePath,string action,string libraryName="",string destinationLibraryName="",string destinationPath="",bool dryRun=true)
            => Portal.ManageLibraryMasterCopy(sourcePath,action,libraryName,destinationLibraryName,destinationPath,dryRun);
        [McpServerTool(Name="ImportLibraryTypeDocuments"), Description("[L2][Library][WRITE] Native type CreateFromDocuments in exact library folder. Explicit import options; default preview. Dependency changes governed by native import; no automatic save.")]
        public static ResponseMessage ImportLibraryTypeDocuments(string filePath,string folderPath="",string libraryName="",string importOptions="None",bool dryRun=true)
            => Portal.ImportLibraryTypeDocuments(filePath,folderPath,libraryName,importOptions,dryRun);
        [McpServerTool(Name="ManageDccChart"), Description("[L2][Hardware][WRITE] Exact offline DCC chart read/create/update/delete/import/export/optimizeSequence. Default preview. Delete includes chart contents; import uses explicit native options. No online drive commands. Native results and file hashes, not semantic completeness.")]
        public static ResponseMessage ManageDccChart(string devicePathJson,string itemPathJson,ushort driveObjectNumber,string chartName,string action,string filePath="",string importOptions="",string propertiesJson="{}",bool dryRun=true)
            => Portal.ManageDccChart(devicePathJson,itemPathJson,driveObjectNumber,chartName,action,filePath,importOptions,propertiesJson,dryRun);
        [McpServerTool(Name="ReadDccObject"), Description("[L2][Hardware][READ] Exact offline DCC chart/block/pin/library property path with bounded scalar pagination. Complex properties excluded explicitly; no complete internal binding guarantee.")]
        public static ResponseMessage ReadDccObject(string devicePathJson,string itemPathJson,ushort driveObjectNumber,string objectPathJson="[]",int offset=0,int limit=100)
            => Portal.ReadDccObject(devicePathJson,itemPathJson,driveObjectNumber,objectPathJson,offset,limit);
    }
}
