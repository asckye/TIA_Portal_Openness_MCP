using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    // The managed service owns only wire conversion; Siemens objects stay in the adapter.
    internal sealed class HardwareAmlService
    {
        private readonly Func<string, JsonObject, JsonNode?> call;
        private readonly Func<bool> hasProject;
        private readonly Func<string> projectIdentity;
        internal HardwareAmlService(Func<string, JsonObject, JsonNode?> call, Func<bool> hasProject, Func<string> projectIdentity)
        { this.call = call; this.hasProject = hasProject; this.projectIdentity = projectIdentity; }
        internal bool HasProject => hasProject();

        public CaxExportResult ExportDeviceConfigurationAml(string devicePath, string exportPath)
        {
            var result = JsonSerializer.Deserialize<HardwareAmlExportReply>(call("hardware-aml.ExportHardwareAml",
                new JsonObject { ["devicePath"] = devicePath, ["exportPath"] = exportPath })!.ToJsonString())
                ?? throw new InvalidOperationException("Missing hardware AML export reply.");
            return new CaxExportResult { DeviceName = result.DeviceName, FilePath = result.FilePath, Success = result.Success,
                State = result.State, NativeBooleanResult = result.NativeBooleanResult,
                ErrorCount = result.ErrorCount, WarningCount = result.WarningCount, Messages = result.Messages };
        }

        public ResponseMessage ImportDeviceAml(string filePath, string logFilePath, string importOption = "RetainTiaDevice",
            bool confirmImport = false, bool dryRun = true)
        {
            var arguments = new JsonObject { ["filePath"] = filePath, ["logFilePath"] = logFilePath,
                ["importOption"] = importOption, ["confirmImport"] = confirmImport, ["dryRun"] = dryRun };
            if (!dryRun) { arguments["confirm"] = true; arguments["expectedProjectFile"] = projectIdentity(); }
            var reply = call("hardware-aml.ImportHardwareAml", arguments) as JsonObject;
            if (reply?["Meta"] is not JsonObject meta || reply["Message"] is not JsonValue message)
                throw new InvalidOperationException("Missing hardware AML import reply.");
            return new ResponseMessage { Message = message.GetValue<string>(), Meta = (JsonObject)meta.DeepClone() };
        }
    }
}
