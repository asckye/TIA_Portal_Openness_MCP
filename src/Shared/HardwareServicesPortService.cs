using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class HardwareServicesPortService
    {
        private readonly Func<string, JsonObject, JsonNode?> call;
        private readonly Func<bool> hasProject;
        private readonly Func<string> projectIdentity;
        internal HardwareServicesPortService(Func<string, JsonObject, JsonNode?> call, Func<bool> hasProject, Func<string> projectIdentity)
        { this.call = call; this.hasProject = hasProject; this.projectIdentity = projectIdentity; }
        internal bool HasProject => hasProject();
        private JsonNode? Invoke(string operation, JsonObject arguments, bool write)
        {
            if (!HasProject)
            {
                string name = operation.Substring(operation.LastIndexOf('.') + 1);
                string tool = name == "HardwareReadCommunicationConnections" ? "ListCommunicationConnections" : name == "HardwareReadHardwareFeatures" ? "GetHardwareFeatures" : name.Substring("Hardware".Length);
                return HardwarePortPreconditions.NoProject(tool);
            }
            if (write) { arguments["dryRun"] = false; arguments["confirm"] = true; arguments["expectedProjectFile"] = projectIdentity(); }
            return call(operation, arguments);
        }
        private static ResponseMessage Step(JsonNode? reply)
        {
            if (reply is not JsonObject result || result["Meta"] is not JsonObject meta || result["Message"] is not JsonValue message)
                throw new InvalidOperationException("Missing hardware network step reply.");
            return new ResponseMessage { Message = message.GetValue<string>(), Meta = (JsonObject)meta.DeepClone() };
        }
        public ResponseMessage ReadCommunicationConnections(string devicePathJson, string itemPathJson = "[]", int offset = 0, int limit = 100)
            => Step(Invoke("hardware-network.HardwareReadCommunicationConnections", new JsonObject { ["devicePathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(devicePathJson)), ["itemPathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(itemPathJson)), ["offset"] = JsonSerializer.SerializeToNode(offset), ["limit"] = JsonSerializer.SerializeToNode(limit) }, false));

        public ResponseMessage ManageCommunicationConnection(string devicePathJson, string itemPathJson, string action, string connectionType = "", string connectionName = "",
            string localInterfaceItemPathJson = "[]", string localNodeName = "", string partnerDevicePathJson = "[]", string partnerItemPathJson = "[]",
            string partnerInterfaceItemPathJson = "[]", string partnerNodeName = "", bool confirmDelete = false, bool dryRun = true)
            => Step(Invoke("hardware-network.HardwareManageCommunicationConnection", new JsonObject { ["devicePathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(devicePathJson)), ["itemPathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(itemPathJson)), ["action"] = JsonSerializer.SerializeToNode(action), ["connectionType"] = JsonSerializer.SerializeToNode(connectionType), ["connectionName"] = JsonSerializer.SerializeToNode(connectionName), ["localInterfaceItemPathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(localInterfaceItemPathJson)), ["localNodeName"] = JsonSerializer.SerializeToNode(localNodeName), ["partnerDevicePathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(partnerDevicePathJson)), ["partnerItemPathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(partnerItemPathJson)), ["partnerInterfaceItemPathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(partnerInterfaceItemPathJson)), ["partnerNodeName"] = JsonSerializer.SerializeToNode(partnerNodeName), ["confirmDelete"] = JsonSerializer.SerializeToNode(confirmDelete), ["dryRun"] = JsonSerializer.SerializeToNode(dryRun) }, !dryRun && action != "read"));

        public ResponseMessage ExchangeSystemDiagnosticsSettings(string action, string filePath, string devicePathJson = "[]", string itemPathJson = "[]", bool confirmImport = false, bool dryRun = true)
            => Step(Invoke("hardware-devices.HardwareExchangeSystemDiagnosticsSettings", new JsonObject { ["action"] = JsonSerializer.SerializeToNode(action), ["filePath"] = JsonSerializer.SerializeToNode(filePath), ["devicePathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(devicePathJson)), ["itemPathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(itemPathJson)), ["confirmImport"] = JsonSerializer.SerializeToNode(confirmImport), ["dryRun"] = JsonSerializer.SerializeToNode(dryRun) }, !dryRun && action != "read"));

        public ResponseMessage ReadHardwareFeatures(string devicePathJson, string itemPathJson = "[]", int offset = 0, int limit = 100)
            => Step(Invoke("hardware-devices.HardwareReadHardwareFeatures", new JsonObject { ["devicePathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(devicePathJson)), ["itemPathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(itemPathJson)), ["offset"] = JsonSerializer.SerializeToNode(offset), ["limit"] = JsonSerializer.SerializeToNode(limit) }, false));

        public ResponseMessage ManageDeviceServiceObjects(string devicePathJson, string itemPathJson, string family, string action = "read", string name = "", string propertiesJson = "{}", string filePath = "", bool confirmChange = false, bool dryRun = true)
            => Step(Invoke("hardware-devices.HardwareManageDeviceServiceObjects", new JsonObject { ["devicePathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(devicePathJson)), ["itemPathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(itemPathJson)), ["family"] = JsonSerializer.SerializeToNode(family), ["action"] = JsonSerializer.SerializeToNode(action), ["name"] = JsonSerializer.SerializeToNode(name), ["propertiesJson"] = JsonSerializer.SerializeToNode(HardwareAddressingService.Scalars(propertiesJson)), ["filePath"] = JsonSerializer.SerializeToNode(filePath), ["confirmChange"] = JsonSerializer.SerializeToNode(confirmChange), ["dryRun"] = JsonSerializer.SerializeToNode(dryRun) }, !dryRun && action != "read"));

        public ResponseMessage ManageHardwareUtilities(string action = "list", string typeIdentifier = "", string devicePathJson = "[]", string itemPathJson = "[]", string filePath = "", string password = "", bool dryRun = true)
            => Step(Invoke("hardware-devices.HardwareManageHardwareUtilities", new JsonObject { ["action"] = JsonSerializer.SerializeToNode(action), ["typeIdentifier"] = JsonSerializer.SerializeToNode(typeIdentifier), ["devicePathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(devicePathJson)), ["itemPathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(itemPathJson)), ["filePath"] = JsonSerializer.SerializeToNode(filePath), ["password"] = JsonSerializer.SerializeToNode(password), ["dryRun"] = JsonSerializer.SerializeToNode(dryRun) }, !dryRun && action != "read"));
    }
}
