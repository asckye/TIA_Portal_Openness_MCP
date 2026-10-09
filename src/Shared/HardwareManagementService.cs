using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class HardwareManagementService
    {
        private readonly Func<string, JsonObject, JsonNode?> call;
        private readonly Func<bool> hasProject;
        private readonly Func<string> projectIdentity;
        internal HardwareManagementService(Func<string, JsonObject, JsonNode?> call, Func<bool> hasProject, Func<string> projectIdentity)
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
        public ResponseMessage ManageHardwareObject(string devicePathJson, string action, string itemPathJson = "[]",
            string destinationDevicePathJson = "[]", string destinationItemPathJson = "[]", int position = -1, bool dryRun = true)
            => Step(Invoke("hardware-devices.HardwareManageHardwareObject", new JsonObject { ["devicePathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(devicePathJson)), ["action"] = JsonSerializer.SerializeToNode(action), ["itemPathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(itemPathJson)), ["destinationDevicePathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(destinationDevicePathJson)), ["destinationItemPathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(destinationItemPathJson)), ["position"] = JsonSerializer.SerializeToNode(position), ["dryRun"] = JsonSerializer.SerializeToNode(dryRun) }, !dryRun && action != "read"));
    }
}
