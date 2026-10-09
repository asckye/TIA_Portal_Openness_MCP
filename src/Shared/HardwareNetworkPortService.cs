using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class HardwareNetworkPortService
    {
        private readonly Func<string, JsonObject, JsonNode?> call;
        private readonly Func<bool> hasProject;
        private readonly Func<string> projectIdentity;
        internal HardwareNetworkPortService(Func<string, JsonObject, JsonNode?> call, Func<bool> hasProject, Func<string> projectIdentity)
        { this.call = call; this.hasProject = hasProject; this.projectIdentity = projectIdentity; }
        internal bool HasProject => hasProject();
        private JsonNode? Invoke(string operation, JsonObject arguments, bool write)
        {
            if (!HasProject)
            {
                string name = operation.Substring(operation.LastIndexOf('.') + 1);
                string tool = name == "HardwareReadIoSystems" ? "ListIoSystems" : name == "HardwareReadNetworkDomains" ? "ListNetworkDomains"
                    : name == "HardwareReadTransferAreas" ? "ListTransferAreas" : name == "HardwareReadDeviceItemChannels" ? "ListDeviceItemChannels"
                    : name == "HardwareUpdateDeviceItemChannel" ? "SetDeviceItemChannel" : name.Substring("Hardware".Length);
                if (name == "ReadHardwareOwnerCandidates" || name == "ReadHardwareWhitelistedServices")
                    return JsonSerializer.SerializeToNode(new HardwareProbeReply { Lines = new[] { "Project is null" }, FailureCode = "PROJECT_NOT_BOUND" });
                if (name == "HardwareGetDeviceItemNetworkInfo") return null;
                if (name == "HardwareProbeConnectDeviceNodesToSubnet") return JsonValue.Create("Project is null");
                if (name == "HardwareEnsureSubnet" || name == "HardwareAttachDeviceNodeToSubnet")
                {
                    var meta = ResponseMeta.Basic(false); meta["mayHaveChanged"] = false;
                    if (name == "HardwareEnsureSubnet")
                    { meta["anchorDeviceItemPath"] = arguments["anchorDeviceItemPath"]!.DeepClone(); meta["subnetType"] = arguments["subnetType"]!.DeepClone(); meta["subnetName"] = arguments["subnetName"]!.DeepClone(); }
                    else
                    { meta["deviceItemPath"] = arguments["deviceItemPath"]!.DeepClone(); meta["interfaceIndex"] = arguments["interfaceIndex"]!.DeepClone(); meta["subnetName"] = arguments["subnetName"]!.DeepClone(); meta["anchorDeviceItemPath"] = arguments["anchorDeviceItemPath"]!.DeepClone(); }
                    meta["failureCode"] = "PROJECT_NOT_BOUND";
                    return new JsonObject { ["Message"] = "Project is null", ["Meta"] = meta };
                }
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
        private static HardwareProbeEvidence Probe(JsonNode? reply)
        {
            var data = JsonSerializer.Deserialize<HardwareProbeReply>(reply!.ToJsonString())
                ?? throw new InvalidOperationException("Missing hardware network probe reply.");
            var evidence = new HardwareProbeEvidence { FailureCode = data.FailureCode }; evidence.AddRange(data.Lines); return evidence;
        }
        public ResponseMessage ReadIoSystems(string subnetName = "", string devicePathJson = "[]", string itemPathJson = "[]", int offset = 0, int limit = 100)
            => Step(Invoke("hardware-network.HardwareReadIoSystems", new JsonObject { ["subnetName"] = JsonSerializer.SerializeToNode(subnetName), ["devicePathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(devicePathJson)), ["itemPathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(itemPathJson)), ["offset"] = JsonSerializer.SerializeToNode(offset), ["limit"] = JsonSerializer.SerializeToNode(limit) }, false));

        public ResponseMessage ManageIoSystem(string devicePathJson, string itemPathJson, string action, string name = "", string subnetName = "", string ioSystemName = "",
            string propertiesJson = "{}", string attributesJson = "{}", bool confirmDelete = false, bool dryRun = true)
            => Step(Invoke("hardware-network.HardwareManageIoSystem", new JsonObject { ["devicePathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(devicePathJson)), ["itemPathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(itemPathJson)), ["action"] = JsonSerializer.SerializeToNode(action), ["name"] = JsonSerializer.SerializeToNode(name), ["subnetName"] = JsonSerializer.SerializeToNode(subnetName), ["ioSystemName"] = JsonSerializer.SerializeToNode(ioSystemName), ["propertiesJson"] = JsonSerializer.SerializeToNode(HardwareAddressingService.Scalars(propertiesJson)), ["attributesJson"] = JsonSerializer.SerializeToNode(HardwareAddressingService.Scalars(attributesJson)), ["confirmDelete"] = JsonSerializer.SerializeToNode(confirmDelete), ["dryRun"] = JsonSerializer.SerializeToNode(dryRun) }, !dryRun && action != "read"));

        public ResponseMessage ReadNetworkDomains(string subnetName, int offset = 0, int limit = 100)
            => Step(Invoke("hardware-network.HardwareReadNetworkDomains", new JsonObject { ["subnetName"] = JsonSerializer.SerializeToNode(subnetName), ["offset"] = JsonSerializer.SerializeToNode(offset), ["limit"] = JsonSerializer.SerializeToNode(limit) }, false));

        public ResponseMessage ManageNetworkDomain(string subnetName, string kind, string action, string name, string propertiesJson = "{}", string attributesJson = "{}",
            string participantDevicePathJson = "[]", string participantItemPathJson = "[]", bool confirmDelete = false, bool dryRun = true)
            => Step(Invoke("hardware-network.HardwareManageNetworkDomain", new JsonObject { ["subnetName"] = JsonSerializer.SerializeToNode(subnetName), ["kind"] = JsonSerializer.SerializeToNode(kind), ["action"] = JsonSerializer.SerializeToNode(action), ["name"] = JsonSerializer.SerializeToNode(name), ["propertiesJson"] = JsonSerializer.SerializeToNode(HardwareAddressingService.Scalars(propertiesJson)), ["attributesJson"] = JsonSerializer.SerializeToNode(HardwareAddressingService.Scalars(attributesJson)), ["participantDevicePathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(participantDevicePathJson)), ["participantItemPathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(participantItemPathJson)), ["confirmDelete"] = JsonSerializer.SerializeToNode(confirmDelete), ["dryRun"] = JsonSerializer.SerializeToNode(dryRun) }, !dryRun && action != "read"));

        public ResponseMessage ReadTransferAreas(string devicePathJson, string itemPathJson, int positionNumber = -1, int extendedPositionNumber = -1, int offset = 0, int limit = 100)
            => Step(Invoke("hardware-network.HardwareReadTransferAreas", new JsonObject { ["devicePathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(devicePathJson)), ["itemPathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(itemPathJson)), ["positionNumber"] = JsonSerializer.SerializeToNode(positionNumber), ["extendedPositionNumber"] = JsonSerializer.SerializeToNode(extendedPositionNumber), ["offset"] = JsonSerializer.SerializeToNode(offset), ["limit"] = JsonSerializer.SerializeToNode(limit) }, false));

        public ResponseMessage ManageTransferArea(string devicePathJson, string itemPathJson, string action, string kind = "standard", string name = "", string type = "",
            int positionNumber = -1, int extendedPositionNumber = -1, string partnerDevicePathJson = "[]", string partnerItemPathJson = "[]", string senderName = "", int length = -1,
            string propertiesJson = "{}", string attributesJson = "{}", int ruleIndex = -1, string targetDevicePathJson = "[]", string targetItemPathJson = "[]", bool confirmDelete = false, bool dryRun = true)
            => Step(Invoke("hardware-network.HardwareManageTransferArea", new JsonObject { ["devicePathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(devicePathJson)), ["itemPathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(itemPathJson)), ["action"] = JsonSerializer.SerializeToNode(action), ["kind"] = JsonSerializer.SerializeToNode(kind), ["name"] = JsonSerializer.SerializeToNode(name), ["type"] = JsonSerializer.SerializeToNode(type), ["positionNumber"] = JsonSerializer.SerializeToNode(positionNumber), ["extendedPositionNumber"] = JsonSerializer.SerializeToNode(extendedPositionNumber), ["partnerDevicePathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(partnerDevicePathJson)), ["partnerItemPathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(partnerItemPathJson)), ["senderName"] = JsonSerializer.SerializeToNode(senderName), ["length"] = JsonSerializer.SerializeToNode(length), ["propertiesJson"] = JsonSerializer.SerializeToNode(HardwareAddressingService.Scalars(propertiesJson)), ["attributesJson"] = JsonSerializer.SerializeToNode(HardwareAddressingService.Scalars(attributesJson)), ["ruleIndex"] = JsonSerializer.SerializeToNode(ruleIndex), ["targetDevicePathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(targetDevicePathJson)), ["targetItemPathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(targetItemPathJson)), ["confirmDelete"] = JsonSerializer.SerializeToNode(confirmDelete), ["dryRun"] = JsonSerializer.SerializeToNode(dryRun) }, !dryRun && action != "read"));

        public ResponseMessage ReadDeviceItemChannels(string devicePathJson, string itemPathJson, string channelType = "", string channelIoType = "", int channelNumber = -1, string attributeNamesJson = "[]", int offset = 0, int limit = 100, bool includeLinkedTags = false)
            => Step(Invoke("hardware-network.HardwareReadDeviceItemChannels", new JsonObject { ["devicePathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(devicePathJson)), ["itemPathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(itemPathJson)), ["channelType"] = JsonSerializer.SerializeToNode(channelType), ["channelIoType"] = JsonSerializer.SerializeToNode(channelIoType), ["channelNumber"] = JsonSerializer.SerializeToNode(channelNumber), ["attributeNamesJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(attributeNamesJson)), ["offset"] = JsonSerializer.SerializeToNode(offset), ["limit"] = JsonSerializer.SerializeToNode(limit), ["includeLinkedTags"] = JsonSerializer.SerializeToNode(includeLinkedTags) }, false));

        public ResponseMessage UpdateDeviceItemChannel(string devicePathJson, string itemPathJson, string channelType, string channelIoType, int channelNumber, string attributesJson, bool dryRun = true)
            => Step(Invoke("hardware-network.HardwareUpdateDeviceItemChannel", new JsonObject { ["devicePathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(devicePathJson)), ["itemPathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(itemPathJson)), ["channelType"] = JsonSerializer.SerializeToNode(channelType), ["channelIoType"] = JsonSerializer.SerializeToNode(channelIoType), ["channelNumber"] = JsonSerializer.SerializeToNode(channelNumber), ["attributesJson"] = JsonSerializer.SerializeToNode(HardwareAddressingService.Scalars(attributesJson)), ["dryRun"] = JsonSerializer.SerializeToNode(dryRun) }, !dryRun));

        public ResponseMessage ManageDeviceUserGroup(string groupPath = "", string action = "read", string newName = "", bool dryRun = true)
            => Step(Invoke("hardware-devices.HardwareManageDeviceUserGroup", new JsonObject { ["groupPath"] = JsonSerializer.SerializeToNode(groupPath), ["action"] = JsonSerializer.SerializeToNode(action), ["newName"] = JsonSerializer.SerializeToNode(newName), ["dryRun"] = JsonSerializer.SerializeToNode(dryRun) }, !dryRun && action != "read"));

        public ResponseMessage ManagePortInterconnection(string devicePathJson, string itemPathJson, string action = "read", string partnerDevicePathJson = "[]", string partnerItemPathJson = "[]", bool dryRun = true)
            => Step(Invoke("hardware-network.HardwareManagePortInterconnection", new JsonObject { ["devicePathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(devicePathJson)), ["itemPathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(itemPathJson)), ["action"] = JsonSerializer.SerializeToNode(action), ["partnerDevicePathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(partnerDevicePathJson)), ["partnerItemPathJson"] = JsonSerializer.SerializeToNode(V4Json.Deserialize<string[]>(partnerItemPathJson)), ["dryRun"] = JsonSerializer.SerializeToNode(dryRun) }, !dryRun && action != "read"));

        public JsonObject GetProjectTopology()
            => !HasProject ? new JsonObject { ["failureCode"] = "PROJECT_NOT_BOUND", ["message"] = "No project open." }
                : (JsonObject)Invoke("hardware-network.HardwareGetProjectTopology", new JsonObject {  }, false)!;

        public ResponseMessage EnsureSubnet(string anchorDeviceItemPath, string subnetType, string subnetName)
            => Step(Invoke("hardware-network.HardwareEnsureSubnet", new JsonObject { ["anchorDeviceItemPath"] = JsonSerializer.SerializeToNode(anchorDeviceItemPath), ["subnetType"] = JsonSerializer.SerializeToNode(subnetType), ["subnetName"] = JsonSerializer.SerializeToNode(subnetName) }, true));

        public ResponseMessage AttachDeviceNodeToSubnet(string deviceItemPath, int interfaceIndex, string subnetName, string anchorDeviceItemPath = "")
            => Step(Invoke("hardware-network.HardwareAttachDeviceNodeToSubnet", new JsonObject { ["deviceItemPath"] = JsonSerializer.SerializeToNode(deviceItemPath), ["interfaceIndex"] = JsonSerializer.SerializeToNode(interfaceIndex), ["subnetName"] = JsonSerializer.SerializeToNode(subnetName), ["anchorDeviceItemPath"] = JsonSerializer.SerializeToNode(anchorDeviceItemPath) }, true));

        public List<string> ProbeHardwareHmiConnectionOwnerCandidates(string plcRootPath, string hmiRootPath, bool deepScan = true)
            => Probe(Invoke("hardware-network.ReadHardwareOwnerCandidates", new JsonObject { ["plcRootPath"] = JsonSerializer.SerializeToNode(plcRootPath), ["hmiRootPath"] = JsonSerializer.SerializeToNode(hmiRootPath), ["deepScan"] = JsonSerializer.SerializeToNode(deepScan) }, false));

        public List<string> ProbeHardwareHmiConnectionWhitelistedServices(string plcRootPath, string hmiRootPath, bool deepScan = true)
            => Probe(Invoke("hardware-network.ReadHardwareWhitelistedServices", new JsonObject { ["plcRootPath"] = JsonSerializer.SerializeToNode(plcRootPath), ["hmiRootPath"] = JsonSerializer.SerializeToNode(hmiRootPath), ["deepScan"] = JsonSerializer.SerializeToNode(deepScan) }, false));

        public List<ModelContextProtocol.NetworkAttribute>? GetDeviceItemNetworkInfo(string deviceItemPath)
            => !HasProject ? null : JsonSerializer.Deserialize<List<ModelContextProtocol.NetworkAttribute>? >(Invoke("hardware-network.HardwareGetDeviceItemNetworkInfo", new JsonObject { ["deviceItemPath"] = JsonSerializer.SerializeToNode(deviceItemPath) }, false)!.ToJsonString());

        public string ProbeConnectDeviceNodesToSubnet(string plcRootPath, string hmiRootPath, string subnetName)
            => JsonSerializer.Deserialize<string>(Invoke("hardware-network.HardwareProbeConnectDeviceNodesToSubnet", new JsonObject { ["plcRootPath"] = JsonSerializer.SerializeToNode(plcRootPath), ["hmiRootPath"] = JsonSerializer.SerializeToNode(hmiRootPath), ["subnetName"] = JsonSerializer.SerializeToNode(subnetName) }, true)!.ToJsonString())!;
    }
    internal static class HardwarePortPreconditions
    {
        internal static JsonObject NoProject(string tool)
        {
            var meta = ResponseMeta.Step(tool); meta["error"] = "Project is null"; meta["status"] = "InvalidState"; meta["operationSuccess"] = false;
            return new JsonObject { ["Message"] = "Project is null", ["Meta"] = meta };
        }
    }

}
