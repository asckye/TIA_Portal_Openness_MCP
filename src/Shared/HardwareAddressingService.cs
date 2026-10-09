using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.V4;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class HardwareAddressWorkerBridge
    {
        internal static Func<bool> HasProject = () => false;
        internal static Func<string> ProjectIdentity = () => "";
        internal static Func<string, JsonObject, JsonNode?> Call = (_, __) => throw new InvalidOperationException("Hardware worker module is unavailable.");
    }

    // Managed host/worker bridge only. Every Openness operation belongs to the
    // registered hardware-addressing module on the worker's owner thread.
    internal sealed class HardwareAddressingService : IHardwareAddressingService
    {
        private readonly Func<string, JsonObject, JsonNode?> call;
        private readonly Func<bool> hasProject;
        private readonly Func<string> projectIdentity;
        internal HardwareAddressingService(Func<string, JsonObject, JsonNode?> call, Func<bool> hasProject, Func<string> projectIdentity)
        { this.call = call; this.hasProject = hasProject; this.projectIdentity = projectIdentity; }
        public bool HasProject => hasProject();

        private JsonNode? Invoke(string operation, JsonObject arguments, bool write = false)
        {
            if (write)
            {
                arguments["dryRun"] = false;
                arguments["confirm"] = true;
                arguments["expectedProjectFile"] = projectIdentity();
            }
            return call("hardware-addressing." + operation, arguments);
        }

        public IReadOnlyList<IoAddressInfo>? GetDeviceItemAddresses(string path)
        {
            var reply = Invoke("ReadHardwareIoAddresses", new JsonObject { ["deviceItemPath"] = path });
            return reply == null ? null : JsonSerializer.Deserialize<IoAddressInfo[]>(reply.ToJsonString());
        }
        public List<string> DescribeChildItemsWithAddresses(string path)
            => JsonSerializer.Deserialize<List<string>>(Invoke("DescribeHardwareIoChildren", new JsonObject { ["deviceItemPath"] = path })!.ToJsonString())!;
        public (bool ok, string message, IoAddressInfo? before, IoAddressInfo? after) SetDeviceItemStartAddress(string path, string ioType, int startAddress)
        {
            var reply = JsonSerializer.Deserialize<IoAddressWriteReply>(Invoke("SetHardwareIoAddress", new JsonObject {
                ["deviceItemPath"] = path, ["ioType"] = ioType, ["startAddress"] = startAddress }, true)!.ToJsonString())!;
            return (reply.Ok, reply.Message, reply.Before, reply.After);
        }

        private static string[] Path(string json) => V4Json.Deserialize<string[]>(json);
        internal static Dictionary<string, HardwareScalar> Scalars(string json)
        {
            var result = new Dictionary<string, HardwareScalar>(StringComparer.Ordinal);
            foreach (var property in V4Json.ParseInput(json).EnumerateObject())
            {
                var value = property.Value;
                string kind = value.ValueKind == JsonValueKind.String ? "string" : value.ValueKind == JsonValueKind.Number ? "number"
                    : value.ValueKind is JsonValueKind.True or JsonValueKind.False ? "boolean" : value.ValueKind == JsonValueKind.Null ? "null"
                    : throw new ArgumentException("Only scalar values are supported.");
                result.Add(property.Name, new HardwareScalar { Kind = kind, Text = kind == "string" ? value.GetString()! : value.GetRawText() });
            }
            return result;
        }

        private static ResponseMessage Step(JsonNode? reply)
        {
            if (reply is not JsonObject result || result["Meta"] is not JsonObject meta || result["Message"] is not JsonValue message)
                throw new InvalidOperationException("Missing hardware step reply.");
            return new ResponseMessage { Message = message.GetValue<string>(), Meta = (JsonObject)meta.DeepClone() };
        }

        public ResponseMessage ReadDeviceAddressing(string devicePathJson, string itemPathJson, int offset, int limit)
            => Step(Invoke("ReadHardwareAddressing", new JsonObject {
                ["devicePath"] = JsonSerializer.SerializeToNode(Path(devicePathJson)), ["itemPath"] = JsonSerializer.SerializeToNode(Path(itemPathJson)),
                ["offset"] = offset, ["limit"] = limit }));

        public ResponseMessage UpdateDeviceAddress(string devicePathJson, string itemPathJson, string ioType, int startAddress,
            string propertiesJson, string attributesJson, string softwarePath, string processImageObName, bool dryRun)
        {
            var update = new HardwareAddressUpdate { DevicePath = Path(devicePathJson), ItemPath = Path(itemPathJson), IoType = ioType,
                StartAddress = startAddress, Properties = Scalars(propertiesJson), Attributes = Scalars(attributesJson),
                SoftwarePath = softwarePath, ProcessImageObName = processImageObName, DryRun = dryRun };
            return Step(Invoke("UpdateHardwareAddress", new JsonObject { ["update"] = JsonSerializer.SerializeToNode(update), ["dryRun"] = dryRun }, !dryRun));
        }

        public JsonObject GetDeviceIpAddress(string path)
        {
            var reply = JsonSerializer.Deserialize<HardwareIpReply>(Invoke("ReadHardwareIpAddress", new JsonObject { ["devicePath"] = path })!.ToJsonString())!;
            if (reply.Message != null)
            {
                var absent = new JsonObject { ["found"] = false };
                if (reply.Device != null) absent["device"] = reply.Device;
                absent["message"] = reply.Message;
                return absent;
            }
            var nodes = new JsonArray();
            foreach (var node in reply.Nodes)
                nodes.Add(new JsonObject { ["nodeName"] = node.NodeName, ["address"] = node.Address, ["nodeType"] = node.NodeType,
                    ["connectedSubnet"] = node.ConnectedSubnet, ["interfacePath"] = node.InterfacePath, ["isIndustrialEthernet"] = node.IsIndustrialEthernet });
            return new JsonObject { ["found"] = reply.Found, ["device"] = reply.Device, ["ipAddress"] = reply.IpAddress,
                ["nodeCount"] = nodes.Count, ["nodes"] = nodes };
        }
    }
}
