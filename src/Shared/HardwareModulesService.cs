using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class HardwareModulesService
    {
        private readonly Func<string, JsonObject, JsonNode?> call;
        private readonly Func<bool> hasProject;
        private readonly Func<string> projectIdentity;
        internal HardwareModulesService(Func<string, JsonObject, JsonNode?> call, Func<bool> hasProject, Func<string> projectIdentity)
        { this.call = call; this.hasProject = hasProject; this.projectIdentity = projectIdentity; }
        internal bool HasProject => hasProject();
        private JsonNode? Invoke(string operation, JsonObject arguments, bool write)
        {
            if (write) { arguments["dryRun"] = false; arguments["confirm"] = true; arguments["expectedProjectFile"] = projectIdentity(); }
            return call(operation, arguments);
        }
        private static ResponseMessage Step(JsonNode? reply)
        {
            if (reply is not JsonObject result || result["Meta"] is not JsonObject meta || result["Message"] is not JsonValue message)
                throw new InvalidOperationException("Missing hardware network step reply.");
            return new ResponseMessage { Message = message.GetValue<string>(), Meta = (JsonObject)meta.DeepClone() };
        }

        public (IReadOnlyList<HardwarePlugLocation> free, IReadOnlyList<HardwarePluggedItem> occupied)? GetDevicePlugLocations(string deviceItemPath, bool plugOnDevice = false)
        {
            var reply = Invoke("hardware-devices.HardwareReadPlugLocations", new JsonObject { ["deviceItemPath"] = deviceItemPath, ["plugOnDevice"] = plugOnDevice }, false) as JsonObject;
            return reply == null ? null : (JsonSerializer.Deserialize<HardwarePlugLocation[]>(reply["free"]!.ToJsonString())!, JsonSerializer.Deserialize<HardwarePluggedItem[]>(reply["occupied"]!.ToJsonString())!);
        }
        public HardwarePlugResult PlugSubmodule(string deviceItemPath, string orderNumber, string version, int positionNumber, string? name, bool dryRun, bool plugOnDevice = false)
            => JsonSerializer.Deserialize<HardwarePlugResult>(Invoke("hardware-devices.HardwarePlugModule", new JsonObject { ["deviceItemPath"] = deviceItemPath, ["orderNumber"] = orderNumber, ["version"] = version, ["positionNumber"] = positionNumber, ["name"] = name, ["dryRun"] = dryRun, ["plugOnDevice"] = plugOnDevice }, !dryRun)!.ToJsonString())!;
    }
}
