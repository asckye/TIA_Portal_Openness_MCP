using TiaMcp.Logic.V4.Hmi;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens;

using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class HardwareAmlTools
    {
        private readonly HardwareAmlService _service;

        public HardwareAmlTools(HardwareAmlService service) => _service = service;

        [McpServerTool(Name = "ExportDeviceAml"), Description("[L2][Hardware] Read-only: export a device's hardware configuration to an AutomationML (CAx) .aml file. The file contains the configured IP address, subnet/mask, PROFINET device name and topology — info GetDeviceItemNetworkInfo omits. devicePath is the station/device path from GetProjectTree (e.g. 'S7-1200 station_3'). exportPath may be an existing folder (file named <device>.aml) or a full .aml path in an existing directory; existing target files are refused. Does NOT modify the project or go online. Behavior policy is current; existing native selection/retry behavior remains pending V4 acceptance.")]
        public CallToolResult ExportDeviceAmlV4(
            [Description("devicePath: station/device path from GetProjectTree, e.g. 'S7-1200 station_3'")] string devicePath,
            [Description("exportPath: target folder or full .aml file path")] string exportPath)
            => HardwareContract.Run("ExportDeviceAml", () =>
            {
                HardwareContract.RequireProject(_service.HasProject);
                return ExportDeviceAml(devicePath, exportPath);
            }, write: true, current: true);

        public ResponseExportDeviceAml ExportDeviceAml(
            [Description("devicePath: station/device path from GetProjectTree, e.g. 'S7-1200 station_3'")] string devicePath,
            [Description("exportPath: target folder or full .aml file path")] string exportPath)
        {
            try
            {
                var result = _service.ExportDeviceConfigurationAml(devicePath, exportPath);
                var evidence = ResponseMeta.Basic(DateTime.Now, result.Success);
                NativeResultState.Record(evidence, result.State, false, targetPath: result.FilePath, messages: new System.Text.Json.Nodes.JsonArray(result.Messages?.Select(m => (JsonNode)m).ToArray() ?? Array.Empty<JsonNode>()));
                return new ResponseExportDeviceAml
                {
                    Message = $"Device '{result.DeviceName}' exported to AML at '{result.FilePath}' (state={result.State}, errors={result.ErrorCount}, warnings={result.WarningCount})",
                    DeviceName = result.DeviceName,
                    FilePath = result.FilePath,
                    Success = result.Success,
                    State = result.State,
                    ErrorCount = result.ErrorCount,
                    WarningCount = result.WarningCount,
                    Messages = result.Messages,
                    Meta = evidence
                };
            }
            catch (PortalException pex)
            {
                CallerInputFiles.RecordExportFailure(pex);
                throw new McpException($"CAx/AML export failed for '{devicePath}': {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                if (ex is TiaMcp.Adapters.Contracts.AdapterPreconditionException) throw;
                CallerInputFiles.RecordExportFailure(ex);
                throw new McpException($"Unexpected error exporting AML from '{devicePath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }
        [McpServerTool(Name="ImportDeviceAml"), Description("[L2][Hardware][FILE] Import an AutomationML/CAx (.aml) file into the open project via CaxProvider.Import(file, logFile, option). filePath must exist (absolute); logFilePath is a NEW absolute file; importOption RetainTiaDevice|OverwriteTiaDevice|MoveToParkingLot. May add or replace devices; real run needs confirmImport=true and exclusive access. Returns native bool result, device counts and the log file sha256. Default preview; no save/compile/download. Behavior policy is current; existing native selection/retry/overwrite behavior remains pending V4 acceptance.")]
        public CallToolResult ImportDeviceAmlV4(
            [Description("filePath: existing absolute AutomationML (.aml) file.")] string filePath,
            [Description("logFilePath: full path of the log file to write on the TIA machine ('' = no log).")] string logFilePath,
            [Description("importOption: import option - MoveToParkingLot | OverwriteTiaDevice | RetainTiaDevice.")] string importOption="RetainTiaDevice",
            [Description("confirmImport: must be true together with dryRun=false to import (imports replace project data).")] bool confirmImport=false,
            bool dryRun=true)
            => HardwareContract.Run("ImportDeviceAml", () =>
            {
                HardwareContract.RequireProject(_service.HasProject);
                return ImportDeviceAml(filePath, logFilePath, importOption, confirmImport, dryRun);
            }, write: !dryRun, current: true);

        public ResponseMessage ImportDeviceAml(
            string filePath,
            [Description("logFilePath: full path of the log file to write on the TIA machine ('' = no log).")] string logFilePath,
            [Description("importOption: import option - MoveToParkingLot | OverwriteTiaDevice | RetainTiaDevice.")] string importOption="RetainTiaDevice",
            [Description("confirmImport: must be true together with dryRun=false to import (imports replace project data).")] bool confirmImport=false,
            bool dryRun=true)
            => _service.ImportDeviceAml(filePath,logFilePath,importOption,confirmImport,dryRun);
        [McpServerTool(Name = "BuildDeviceAmlDocument"), Description("[L2][Hardware][OFFLINE] Build an AutomationML (CAEX 2.15, *.aml) hardware description for ImportDeviceAml from a JSON spec — the generation half of a TIA Selection Tool / Aml.Engine workflow without extra dependencies. spec: {\"projectName\":\"P\",\"devices\":[{\"name\":\"PLC_1\",\"typeIdentifier\":\"System:Device.S71500\",\"deviceItems\":[{\"name\":\"Rack_0\",\"role\":\"Rack\",\"typeIdentifier\":\"OrderNumber:6ES7 590-1AE80-0AA0\",\"positionNumber\":0,\"deviceItems\":[{\"name\":\"PLC_1\",\"typeIdentifier\":\"OrderNumber:6ES7 516-3AN02-0AB0/V2.9\",\"positionNumber\":1,\"deviceItems\":[{\"name\":\"PROFINET interface_1\",\"role\":\"CommunicationInterface\",\"builtIn\":true,\"nodes\":[{\"name\":\"E1\",\"networkAddress\":\"192.168.0.1\",\"subnetMask\":\"255.255.255.0\",\"subnetName\":\"PN/IE_1\"}]}]},{\"name\":\"DI 32x24VDC HF_1\",\"typeIdentifier\":\"OrderNumber:6ES7 521-1BL00-0AB0/V2.0\",\"positionNumber\":2}]}]}],\"subnets\":[{\"name\":\"PN/IE_1\",\"type\":\"Ethernet\"}]}. Roles: Rack | DeviceItem | CommunicationInterface | CommunicationPort; TypeIdentifier uses the TIA catalog form OrderNumber:<MLFB>[/Vx.y] or System:Device.<family>. outputPath must be a NEW absolute .aml file. STRONGLY RECOMMENDED: referenceAmlPath = a file that ExportDeviceAml wrote on the same TIA version — its header, role/interface class libraries are copied verbatim and only the instance hierarchy is generated; without it a built-in skeleton is used and Meta.importVerified=false is returned. Verify with ImportDeviceAml (dry run first). Nothing is saved, compiled or downloaded.")]
        public CallToolResult BuildDeviceAmlDocumentV4(
            [Description("spec: JSON object describing the document to build (see the tool description).")] DeviceAmlSpec spec,
            string outputPath,
            [Description("referenceAmlPath: full path of a reference AML file whose structure is reused.")] string referenceAmlPath = "")
            => HardwareContract.Run("BuildDeviceAmlDocument", () =>
            {
                var specJson = spec.ToBuilderInput().ToJsonString();
                return BuildDeviceAmlDocument(specJson, outputPath, referenceAmlPath);
            }, write: true, current: false);

        public ResponseMessage BuildDeviceAmlDocument(
            [Description("specJson: JSON object describing the document to build (see the tool description).")] string specJson,
            string outputPath,
            [Description("referenceAmlPath: full path of a reference AML file whose structure is reused.")] string referenceAmlPath = "")
            => OfflineToolExecution.RunOfflineAnalysisTool("BuildDeviceAmlDocument", meta =>
            {
                meta["mayHaveWrittenFiles"] = false;
                var output = NativeFileOutput.Plan(outputPath);
                if (!output.Extension.Equals(".aml", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("outputPath must end with .aml.");
                var spec = HardwareAmlLogic.ParseSpec(specJson);
                var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";
                var result = HardwareAmlLogic.Build(spec, string.IsNullOrWhiteSpace(referenceAmlPath) ? null : referenceAmlPath, output.Name, version);
                meta["mayHaveWrittenFiles"] = true;
                File.WriteAllText(output.FullName, HardwareAmlLogic.Serialize(result.Document), new UTF8Encoding(false));
                foreach (var kv in HardwareAmlLogic.Summary(result)) meta[kv.Key] = kv.Value?.DeepClone();
                meta["output"] = NativeFileOutput.Verify(output);
                meta["mayHaveWrittenFiles"] = true; meta["apiCallSuccess"] = true; meta["dataComplete"] = true;
                meta["nextStep"] = "ImportDeviceAml with this file (dryRun first); on import errors export a reference AML with ExportDeviceAml and pass it as referenceAmlPath.";
                return "AML written: " + result.Devices + " device(s), " + result.DeviceItems + " device item(s), " + result.Nodes + " node(s), " + result.Subnets + " subnet(s) -> " + output.FullName + " (libraries: " + result.LibraryOrigin + ").";
            });
    }
}
