using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    // Offline AML generation for the CAx import half (ImportDeviceAml). Pure file output; no TIA session.
    public static partial class McpServer
    {
        [McpServerTool(Name = "BuildDeviceAmlDocument"), Description("[L2][Hardware][OFFLINE] Build an AutomationML (CAEX 2.15, *.aml) hardware description for ImportDeviceAml from a JSON spec — the generation half of a TIA Selection Tool / Aml.Engine workflow without extra dependencies. specJson: {\"projectName\":\"P\",\"devices\":[{\"name\":\"PLC_1\",\"typeIdentifier\":\"System:Device.S71500\",\"deviceItems\":[{\"name\":\"Rack_0\",\"role\":\"Rack\",\"typeIdentifier\":\"OrderNumber:6ES7 590-1AE80-0AA0\",\"positionNumber\":0,\"deviceItems\":[{\"name\":\"PLC_1\",\"typeIdentifier\":\"OrderNumber:6ES7 516-3AN02-0AB0/V2.9\",\"positionNumber\":1,\"deviceItems\":[{\"name\":\"PROFINET interface_1\",\"role\":\"CommunicationInterface\",\"builtIn\":true,\"nodes\":[{\"name\":\"E1\",\"networkAddress\":\"192.168.0.1\",\"subnetMask\":\"255.255.255.0\",\"subnetName\":\"PN/IE_1\"}]}]},{\"name\":\"DI 32x24VDC HF_1\",\"typeIdentifier\":\"OrderNumber:6ES7 521-1BL00-0AB0/V2.0\",\"positionNumber\":2}]}]}],\"subnets\":[{\"name\":\"PN/IE_1\",\"type\":\"Ethernet\"}]}. Roles: Rack | DeviceItem | CommunicationInterface | CommunicationPort; TypeIdentifier uses the TIA catalog form OrderNumber:<MLFB>[/Vx.y] or System:Device.<family>. outputPath must be a NEW absolute .aml file. STRONGLY RECOMMENDED: referenceAmlPath = a file that ExportDeviceAml wrote on the same TIA version — its header, role/interface class libraries are copied verbatim and only the instance hierarchy is generated; without it a built-in skeleton is used and Meta.importVerified=false is returned. Verify with ImportDeviceAml (dry run first). Nothing is saved, compiled or downloaded.")]
        public static ResponseMessage BuildDeviceAmlDocument(
            [Description("specJson: JSON object describing the document to build (see the tool description).")] string specJson,
            string outputPath,
            [Description("referenceAmlPath: full path of a reference AML file whose structure is reused.")] string referenceAmlPath = "")
            => RunOfflineAnalysisTool("BuildDeviceAmlDocument", meta =>
            {
                var output = NativeFileOutput.Plan(outputPath);
                if (!output.Extension.Equals(".aml", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("outputPath must end with .aml.");
                var spec = HardwareAmlLogic.ParseSpec(specJson);
                var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";
                var result = HardwareAmlLogic.Build(spec, string.IsNullOrWhiteSpace(referenceAmlPath) ? null : referenceAmlPath, output.Name, version);
                File.WriteAllText(output.FullName, HardwareAmlLogic.Serialize(result.Document), new UTF8Encoding(false));
                foreach (var kv in HardwareAmlLogic.Summary(result)) meta[kv.Key] = kv.Value?.DeepClone();
                meta["output"] = NativeFileOutput.Verify(output);
                meta["mayHaveWrittenFiles"] = true; meta["apiCallSuccess"] = true; meta["dataComplete"] = true;
                meta["nextStep"] = "ImportDeviceAml with this file (dryRun first); on import errors export a reference AML with ExportDeviceAml and pass it as referenceAmlPath.";
                return "AML written: " + result.Devices + " device(s), " + result.DeviceItems + " device item(s), " + result.Nodes + " node(s), " + result.Subnets + " subnet(s) -> " + output.FullName + " (libraries: " + result.LibraryOrigin + ").";
            });
    }
}
