using TiaMcpServer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4.Domain;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens.Services;
using Xunit;

namespace TiaMcpServer.ModelContextProtocol
{
    public class NetworkAttribute { }
    public class ResponseNetworkInfo : ResponseMessage
    {
        public string? DeviceItemName { get; set; }
        public IEnumerable<NetworkAttribute>? Attributes { get; set; }
    }
    public class ResponseJsonReport : ResponseMessage
    {
        public bool? Ok { get; set; }
        public JsonObject? Data { get; set; }
        public string[]? Errors { get; set; }
        public string[]? Warnings { get; set; }
        public string? OutputPath { get; set; }
        public string[]? OutputFiles { get; set; }
    }
}

namespace TiaMcpServer.ModelContextProtocol
{
    public class Attribute
    {
        public string? Name { get; set; }
        public object? Value { get; set; }
        public string? AccessMode { get; set; }
    }

    public class GsdDeviceCandidate
    {
        public string? Source { get; set; }
        public string? Keyword { get; set; }
        public string? Vendor { get; set; }
        public string? ProductFamily { get; set; }
        public string? MainFamily { get; set; }
        public string? DapId { get; set; }
        public string? DapName { get; set; }
        public string? ArticleNumber { get; set; }
        public string? CatalogPath { get; set; }
        public string? Description { get; set; }
        public string? TypeIdentifier { get; set; }
        public string? TypeIdentifierNormalized { get; set; }
        public string? TypeName { get; set; }
        public string? Version { get; set; }
        public string? GsdmlPath { get; set; }
        public int? Score { get; set; }
    }

    public class HardwareCatalogCandidate
    {
        public string? Source { get; set; }
        public string? Keyword { get; set; }
        public string? ArticleNumber { get; set; }
        public string? CatalogPath { get; set; }
        public string? Description { get; set; }
        public string? TypeIdentifier { get; set; }
        public string? TypeIdentifierNormalized { get; set; }
        public string? TypeName { get; set; }
        public string? Version { get; set; }
        public bool? Insertable { get; set; }
        public int? Score { get; set; }
    }

    public class CaxExportResult
    {
        public string? DeviceName { get; set; }
        public string? FilePath { get; set; }
        public bool Success { get; set; }
        public string? State { get; set; }
        public bool? NativeBooleanResult { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public List<string>? Messages { get; set; }
    }

    public class ResponseAttributes : ResponseMessage
    {
        public IEnumerable<Attribute>? Attributes { get; set; }
    }

    public class ResponseDeviceInfo : ResponseAttributes
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    public class ResponseDeviceItemInfo : ResponseAttributes
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    public class ResponseTree : ResponseMessage
    {
        public string? Tree { get; set; }
    }

    public class ResponseDevices : ResponseMessage
    {
        public IEnumerable<ResponseDeviceInfo>? Items { get; set; }
    }

    public class ResponseDeviceProbe : ResponseMessage
    {
        public bool? Ok { get; set; }
        public string? DeviceName { get; set; }
        public string? Family { get; set; }
        public string? MlfbUsed { get; set; }
        public string? VersionUsed { get; set; }
        public IEnumerable<string>? Attempts { get; set; }
        public string? Error { get; set; }
    }

    public class ResponseGsdDeviceSearch : ResponseMessage
    {
        public string? Keyword { get; set; }
        public int? Count { get; set; }
        public IEnumerable<GsdDeviceCandidate>? Items { get; set; }
    }

    public class ResponseHardwareCatalogSearch : ResponseMessage
    {
        public string? Keyword { get; set; }
        public int? Count { get; set; }
        public IEnumerable<HardwareCatalogCandidate>? Items { get; set; }
        public string? Error { get; set; }
    }

    public class ResponseGsdDeviceProbe : ResponseMessage
    {
        public bool? Ok { get; set; }
        public string? Keyword { get; set; }
        public string? DeviceName { get; set; }
        public string? PreferredDap { get; set; }
        public GsdDeviceCandidate? CandidateUsed { get; set; }
        public IEnumerable<GsdDeviceCandidate>? Candidates { get; set; }
        public IEnumerable<string>? Attempts { get; set; }
        public string? Error { get; set; }
    }

    public class ResponseHardwareCatalogDeviceProbe : ResponseMessage
    {
        public bool? Ok { get; set; }
        public string? Keyword { get; set; }
        public string? DeviceName { get; set; }
        public string? PreferredText { get; set; }
        public HardwareCatalogCandidate? CandidateUsed { get; set; }
        public IEnumerable<HardwareCatalogCandidate>? Candidates { get; set; }
        public IEnumerable<string>? Attempts { get; set; }
        public string? Error { get; set; }
    }

    public class ResponseExportDeviceAml : ResponseMessage
    {
        public string? DeviceName { get; set; }
        public string? FilePath { get; set; }
        public bool Success { get; set; }
        public string? State { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public IEnumerable<string>? Messages { get; set; }
    }
}

// Application service doubles only: invalid inputs must never cross this boundary.
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class HardwareNetworkService
    {
        internal int Calls;
        internal ResponseMessage Next() { Calls++; return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ReadIoSystems(string subnetName, string devicePathJson, string itemPathJson, int offset, int limit) => Next();
        public ResponseMessage ManageIoSystem(string devicePathJson, string itemPathJson, string action, string name, string subnetName, string ioSystemName, string propertiesJson, string attributesJson, bool confirmDelete, bool dryRun) => Next();
        public ResponseMessage ReadNetworkDomains(string subnetName, int offset, int limit) => Next();
        public ResponseMessage ManageNetworkDomain(string subnetName, string kind, string action, string name, string propertiesJson, string attributesJson, string participantDevicePathJson, string participantItemPathJson, bool confirmDelete, bool dryRun) => Next();
        public ResponseMessage ReadTransferAreas(string devicePathJson, string itemPathJson, int positionNumber, int extendedPositionNumber, int offset, int limit) => Next();
        public ResponseMessage ManageTransferArea(string devicePathJson, string itemPathJson, string action, string kind, string name, string type, int positionNumber, int extendedPositionNumber, string partnerDevicePathJson, string partnerItemPathJson, string senderName, int length, string propertiesJson, string attributesJson, int ruleIndex, string targetDevicePathJson, string targetItemPathJson, bool confirmDelete, bool dryRun) => Next();
        public ResponseMessage ReadDeviceItemChannels(string devicePathJson, string itemPathJson, string channelType, string channelIoType, int channelNumber, string attributeNamesJson, int offset, int limit, bool includeLinkedTags) => Next();
        public ResponseMessage UpdateDeviceItemChannel(string devicePathJson, string itemPathJson, string channelType, string channelIoType, int channelNumber, string attributesJson, bool dryRun) => Next();
        public ResponseMessage ManageDeviceUserGroup(string groupPath, string action, string newName, bool dryRun) => Next();
        public ResponseMessage ManageDeviceUsers(string devicePathJson, string itemPathJson, string family, string action, string userName, string password, string permissionsJson, bool active, string newName, bool confirmDelete, bool dryRun) => Next();
        public ResponseMessage ManagePortInterconnection(string devicePathJson, string itemPathJson, string action, string partnerDevicePathJson, string partnerItemPathJson, bool dryRun) => Next();
        public List<NetworkAttribute>? GetDeviceItemNetworkInfo(string path) { Calls++; return new List<NetworkAttribute>(); }
        public string ProbeConnectDeviceNodesToSubnet(string first, string second, string subnet) { Calls++; return "ConnectToSubnet: OK"; }
        public ResponseMessage EnsureSubnet(string anchor, string type, string subnet) => Next();
        public ResponseMessage AttachDeviceNodeToSubnet(string path, int index, string subnet, string anchor) => Next();
        public List<string> ProbeHardwareHmiConnectionOwnerCandidates(string plc, string hmi, bool deep) { Calls++; return new List<string>(); }
        public List<string> ProbeHardwareHmiConnectionWhitelistedServices(string plc, string hmi, bool deep) { Calls++; return new List<string>(); }
        public JsonObject GetProjectTopology() { Calls++; return new JsonObject { ["devices"] = new JsonArray(), ["deviceCount"] = 0 }; }
    }
    internal sealed class HardwareServicesService
    {
        internal int Calls;
        internal ResponseMessage Next() { Calls++; return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ReadCommunicationConnections(string devicePathJson, string itemPathJson, int offset, int limit) => Next();
        public ResponseMessage ManageCommunicationConnection(string devicePathJson, string itemPathJson, string action, string connectionType, string connectionName, string localInterfaceItemPathJson, string localNodeName, string partnerDevicePathJson, string partnerItemPathJson, string partnerInterfaceItemPathJson, string partnerNodeName, bool confirmDelete, bool dryRun) => Next();
        public ResponseMessage ManageWatchForceTableWebAccess(string devicePathJson, string itemPathJson, string action, string softwarePath, string tableKind, string tablePath, string access, bool confirmChange, bool dryRun) => Next();
        public ResponseMessage ExchangeSystemDiagnosticsSettings(string action, string filePath, string devicePathJson, string itemPathJson, bool confirmImport, bool dryRun) => Next();
        public ResponseMessage ReadHardwareFeatures(string devicePathJson, string itemPathJson, int offset, int limit) => Next();
        public ResponseMessage ManageDeviceServiceObjects(string devicePathJson, string itemPathJson, string family, string action, string name, string propertiesJson, string filePath, bool confirmChange, bool dryRun) => Next();
        public ResponseMessage ManagePlcProtection(string devicePathJson, string itemPathJson, string action, string accessLevel, string password, string newPassword, bool confirmChange, bool dryRun) => Next();
        public ResponseMessage CompileDevice(string devicePathJson, string itemPathJson) => Next();
        public ResponseMessage ManageHardwareUtilities(string action, string typeIdentifier, string devicePathJson, string itemPathJson, string filePath, string password, bool dryRun) => Next();
        public JsonObject GetPutGetAccess(string path) { Calls++; return new JsonObject { ["found"] = true, ["enabled"] = false }; }
        public JsonObject SetPutGetAccess(string path, bool enabled) { Calls++; return new JsonObject { ["ok"] = true, ["mayHaveChanged"] = true, ["writeOutcomeKnown"] = true }; }
    }
}

namespace TiaMcp.Engine.Tests
{
    public sealed class HardwareContractsTests : IDisposable
    {
        private static readonly string[] Names = {
            "ListIoSystems",
            "ManageIoSystem",
            "ListNetworkDomains",
            "ManageNetworkDomain",
            "ListTransferAreas",
            "ManageTransferArea",
            "ListDeviceItemChannels",
            "SetDeviceItemChannel",
            "ManageDeviceUserGroup",
            "ManageDeviceUsers",
            "ManagePortInterconnection",
            "GetDeviceItemNetworkInfo",
            "ConnectDeviceNodesToProfinetSubnet",
            "PlanHardwareNetworkConfiguration",
            "EnsureSubnet",
            "AttachDeviceNodeToSubnet",
            "ProbeHardwareHmiConnectionOwnerCandidates",
            "ProbeHardwareHmiConnectionWhitelistedServices",
            "GetProjectTopology",
            "ListCommunicationConnections",
            "ManageCommunicationConnection",
            "ManageWatchForceTableWebAccess",
            "ExchangeSystemDiagnosticsSettings",
            "GetHardwareFeatures",
            "ManageDeviceServiceObjects",
            "ManagePlcProtection",
            "CompileDevice",
            "GetPlcPutGetAccess",
            "SetPlcPutGetAccess",
            "ManageHardwareUtilities"
        };
        private readonly HardwareNetworkService network = new HardwareNetworkService();
        private readonly HardwareServicesService services = new HardwareServicesService();
        private readonly HardwareNetworkTools networkTools;
        private readonly HardwareSecurityTools securityTools;
        private readonly HardwareServicesTools serviceTools;
        private readonly ToolCatalog catalog;
        public HardwareContractsTests()
        {
            networkTools = new HardwareNetworkTools(new HardwareNetworkPortService((operation, _) => operation == "hardware-network.HardwareGetProjectTopology"
                ? network.GetProjectTopology() : JsonSerializer.SerializeToNode(network.Next()), () => true, () => "fixture"));
            securityTools = new HardwareSecurityTools(network);
            serviceTools = new HardwareServicesTools(services);
            var portTools = new HardwareServicesPortTools(new HardwareServicesPortService((_, __) => JsonSerializer.SerializeToNode(services.Next()), () => true, () => "fixture"));
            catalog = new ToolCatalog(new[] { typeof(HardwareNetworkTools), typeof(HardwareSecurityTools), typeof(HardwareServicesTools), typeof(HardwareServicesPortTools) });
            McpServer.ConfigureToolBridge(catalog, () => false, new HashSet<string>());
            EngineServices.SetServiceProvider(new ServiceCollection().AddSingleton(networkTools).AddSingleton(securityTools).AddSingleton(serviceTools).AddSingleton(portTools).BuildServiceProvider());
        }
        public void Dispose() => ToolBridgeFixture.Configure();
        private static JsonObject Body(CallToolResult result)
        {
            string text = ((TextContentBlock)Assert.Single(result.Content)).Text;
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), result.StructuredContent));
            var body = JsonNode.Parse(text)!.AsObject();
            Assert.Equal(4, (int)body["schemaVersion"]!);
            Assert.Equal(!(bool)body["ok"]!, result.IsError);
            Assert.False(string.IsNullOrWhiteSpace((string?)body["meta"]!["requestId"]));
            return body;
        }
        private static void Rejected(CallToolResult result)
        {
            var body = Body(result);
            Assert.Equal("rejected-before-operation", (string?)body["meta"]!["outcome"]);
            Assert.Equal("not-started", (string?)body["meta"]!["execution"]);
        }
        [Fact]
        public void RegistrationsContainOnlyTheReviewedNamesAndTypedParameters()
        {
            var expected = Names.AsEnumerable();
            Assert.Equal(expected.OrderBy(x => x), catalog.Methods.Select(p => p.Key).OrderBy(x => x));
            foreach (var entry in catalog.Methods)
            {
                Assert.Equal(typeof(CallToolResult), entry.Value.ReturnType);
                foreach (var p in entry.Value.GetParameters())
                {
                    Assert.False(p.Name!.EndsWith("Json", StringComparison.Ordinal));
                    if (p.Name == "properties" || p.Name == "attributes") Assert.Equal(typeof(AttributeMap<Scalar>), p.ParameterType);
                    if (p.Name == "plan") Assert.Equal(typeof(NetworkPlan), p.ParameterType);
                    if (new[] { "itemPath", "partnerItemPath", "targetItemPath", "participantItemPath", "localInterfaceItemPath", "partnerInterfaceItemPath", "permissions", "attributeNames" }.Contains(p.Name))
                        Assert.Equal(typeof(string[]), p.ParameterType);
                }
            }
        }
        [Fact]
        public void InvalidPathsListsAndMapsStopBeforeTheService()
        {
            Rejected(networkTools.ListTransferAreas(new[] { " " }, Array.Empty<string>()));
            Rejected(networkTools.ListTransferAreas(Enumerable.Repeat("x", 65).ToArray(), Array.Empty<string>()));
            Rejected(serviceTools.CompileDevice(null!));
            Rejected(networkTools.ListDeviceItemChannels(new[] { "PLC" }, Array.Empty<string>(), attributeNames: new[] { "Name", "Name" }));
            Rejected(securityTools.ManageDeviceUsers(new[] { "PLC" }, Array.Empty<string>(), "webserver", permissions: Enumerable.Range(0, 33).Select(i => "p" + i).ToArray()));
            var properties = V4Json.Deserialize<AttributeMap<Scalar>>("{\"Name\":\"" + new string('x', 32768) + "\"}");
            Rejected(networkTools.ManageIoSystem(new[] { "PLC" }, Array.Empty<string>(), "update", properties: properties));
            Rejected(networkTools.ListNetworkDomains("subnet", limit: 501));
            Assert.Equal(0, network.Calls + services.Calls);
        }
        [Theory]
        [InlineData("null")]
        [InlineData("\"{}\"")]
        [InlineData("{\"Name\":1,\"Name\":2}")]
        [InlineData("{\"Name\":{\"nested\":true}}")]
        [InlineData("{\"bad-key\":true}")]
        public void ScalarMapRejectsNullDoubleEncodingDuplicatesAndCompositeValues(string json)
            => Assert.False(HardwareToolContract.Attributes.Read(json, "properties").IsValid);
        [Theory]
        [InlineData("null")]
        [InlineData("\"{\\\"operations\\\":[]}\"")]
        [InlineData("{\"Operations\":[]}")]
        [InlineData("{\"operations\":[],\"extra\":true}")]
        [InlineData("{\"operations\":[],\"operations\":[]}")]
        [InlineData("{\"operations\":[{\"type\":\"Unknown\"}]}")]
        public void NetworkPlanUsesTheClosedDomainContract(string json)
            => Assert.False(DomainValidation.Contract<NetworkPlan>().Read(json, "plan").IsValid);
        [Fact]
        public void ValidSegmentsRemainExactAndPlanStaysOffline()
        {
            string[] path = { "Group/with,delimiters", "PLC\\exact" };
            Assert.Equal(V4Json.Serialize(path), HardwareToolContract.Path(path, "devicePath", false, false));
            var plan = DomainValidation.Contract<NetworkPlan>().Read("{\"operations\":[]}", "plan").Value!;
            Assert.True((bool)Body(networkTools.PlanHardwareNetworkConfiguration(plan))["ok"]!);
            Assert.Equal(0, network.Calls);
        }
        [Theory]
        [InlineData("{\"success\":true,\"dataComplete\":false}", true, "succeeded", "read-only", "partial")]
        [InlineData("{\"success\":false,\"status\":\"InvalidState\"}", false, "rejected-before-operation", "not-started", "none")]
        [InlineData("{\"success\":false}", true, "read-failed", "read-only", "unknown")]
        [InlineData("{\"success\":false,\"mayHaveChanged\":false}", false, "rejected-before-operation", "not-started", "none")]
        [InlineData("{\"success\":false,\"mayHaveChanged\":true,\"appliedProperties\":[\"Name\"]}", false, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":false,\"mayHaveChanged\":false,\"mayHaveWrittenFiles\":true,\"file\":{\"sha256\":\"evidence\"}}", false, "partial", "partial", "complete")]
        public void EnvelopeMappingPreservesEvidenceAndExecution(string json, bool readOnly, string outcome, string execution, string completeness)
        {
            var body = Body(HardwareToolContract.MapResult("ManageIoSystem", new ResponseMessage { Meta = JsonNode.Parse(json)!.AsObject() }, readOnly, !readOnly));
            Assert.Equal(outcome, (string?)body["meta"]!["outcome"]);
            Assert.Equal(execution, (string?)body["meta"]!["execution"]);
            Assert.Equal(completeness, (string?)body["meta"]!["completeness"]);
            Assert.Equal(outcome == "unknown", (bool)body["meta"]!["requiresSessionReset"]!);
            if (!readOnly) Assert.Equal("current", (string?)body["meta"]!["behaviorPolicy"]);
            if (outcome == "unknown") Assert.Single(body["data"]!["appliedProperties"]!.AsArray());
        }
        [Fact]
        public void CompilerSubtreeFailureAndUnconfirmedPutGetAreNeverSuccess()
        {
            var compile = new ResponseMessage { Meta = JsonNode.Parse("{\"success\":true,\"operationSuccess\":true,\"nativeCompleted\":true,\"mayHaveChanged\":true,\"effectiveState\":\"Error\",\"errors\":[\"child failure\"]}")!.AsObject() };
            Assert.Equal("failed", (string?)Body(HardwareToolContract.MapResult("CompileDevice", compile, false, true))["meta"]!["outcome"]);
            var putGet = new ResponseJsonReport { Ok = true, Data = JsonNode.Parse("{\"mayHaveChanged\":true,\"writeOutcomeKnown\":false}")!.AsObject() };
            Assert.Equal("unknown", (string?)Body(HardwareToolContract.MapResult("SetPlcPutGetAccess", putGet, false, true))["meta"]!["outcome"]);
            Assert.Equal("unknown", (string?)Body(networkTools.ConnectDeviceNodesToProfinetSubnet("PLC", "HMI"))["meta"]!["outcome"]);
        }
        [Fact]
        public void EmptyTopologyIsPartialObservationAndNativeExceptionTextIsNotPublished()
        {
            var topology = Body(networkTools.GetProjectTopology());
            Assert.True((bool)topology["ok"]!);
            Assert.Equal("partial", (string?)topology["meta"]!["completeness"]);
            var failed = HardwareToolContract.MapResult("ListIoSystems", new ResponseMessage { Meta = new JsonObject { ["success"] = false, ["error"] = "secret stack trace" } }, true, false);
            Assert.DoesNotContain("secret", Body(failed).ToJsonString());
        }
        [Fact]
        public void DirectAndBridgeSuccessfulReadsHaveTheSameEnvelopeShape()
        {
            var direct = Body(networkTools.ListNetworkDomains("subnet"));
            var bridged = Body(McpServer.CallTool("ListNetworkDomains", new ToolArguments(JsonSerializer.Deserialize<JsonElement>("{\"subnetName\":\"subnet\"}"))));
            foreach (var result in new[] { direct, bridged })
            {
                Assert.Equal("ListNetworkDomains", (string?)result["meta"]!["tool"]);
                result["meta"]!.AsObject().Remove("timestamp"); result["meta"]!.AsObject().Remove("requestId");
            }
            Assert.True(JsonNode.DeepEquals(direct, bridged));
        }
        [Fact]
        public void AdvertisedNetworkPlanSchemaIsClosedAndIncludesTheOperationUnion()
        {
            var entry = catalog.Methods.Single(p => p.Key == "PlanHardwareNetworkConfiguration");
            var schema = JsonNode.Parse(McpServer.ToolInputSchema(entry.Key, entry.Value).GetRawText())!;
            var plan = schema["properties"]!["plan"]!;
            Assert.Equal("object", (string?)plan["type"]);
            Assert.Equal(false, (bool?)plan["additionalProperties"]);
            Assert.NotNull(plan["properties"]?["operations"]);
        }

        [Theory]
        [InlineData("CompileDevice", "{\"devicePath\":[\"PLC\"],\"itemPath\":null}")]
        [InlineData("CompileDevice", "{\"devicePath\":\"[\\\"PLC\\\"]\"}")]
        [InlineData("ManageIoSystem", "{\"devicePath\":[\"PLC\"],\"itemPath\":[],\"action\":\"update\",\"properties\":null}")]
        [InlineData("ManageIoSystem", "{\"devicePath\":[\"PLC\"],\"itemPath\":[],\"action\":\"update\",\"properties\":{\"Name\":{}}}")]
        [InlineData("PlanHardwareNetworkConfiguration", "{\"plan\":{}}")]
        [InlineData("PlanHardwareNetworkConfiguration", "{\"plan\":{\"operations\":[],\"extra\":true}}")]
        [InlineData("ListNetworkDomains", "{\"subnetName\":\"one\",\"SUBNETNAME\":\"two\"}")]
        public void SharedAdmissionRejectsMalformedInputsBeforeTheService(string name, string json)
        {
            var result = McpServer.CallTool(name, new ToolArguments(JsonSerializer.Deserialize<JsonElement>(json)));
            Rejected(result);
            Assert.Equal("INVALID_ARGUMENT", (string?)Body(result)["error"]!["code"]);
            Assert.Equal(0, network.Calls + services.Calls);
        }

        [Fact]
        public void SharedAdmissionKeepsOmittedOptionalInputsDistinctFromExplicitNull()
        {
            var result = McpServer.CallTool("CompileDevice", new ToolArguments(JsonSerializer.Deserialize<JsonElement>("{\"devicePath\":[\"PLC\"]}")));
            Assert.True((bool)Body(result)["ok"]!);
            Assert.Equal(1, services.Calls);
            result = McpServer.CallTool("ManageIoSystem", new ToolArguments(JsonSerializer.Deserialize<JsonElement>("{\"devicePath\":[\"PLC\"],\"itemPath\":[],\"action\":\"update\"}")));
            Assert.True((bool)Body(result)["ok"]!);
            Assert.Equal(1, network.Calls);
        }

        [Fact]
        public void GeneratedHardwareExamplesFitTheSharedSchemasWithoutInvokingServices()
        {
            var methods = McpServer.AllToolMethods();
            var entries = TiaOpenness.Shared.ToolUsageCatalog.ProfileEntries(McpServer.ReleaseKey)
                .Where(row => methods.ContainsKey((string)row!["currentName"]!)).ToArray();
            Assert.Equal(methods.Count, entries.Length);
            foreach (var entry in entries)
            {
                var name = (string)entry!["currentName"]!;
                Assert.Equal(4, (int)entry["envelopeVersion"]!);
                var result = Body(new ToolUsageTools().GetToolUsage(toolName: name));
                Assert.True((bool)result["ok"]!, name);
                var data = result["data"]!;
                var arguments = JsonSerializer.SerializeToElement(data["example"]!["request"]!["params"]!["arguments"]);
                Assert.Null(McpServer.ValidateV4Arguments(methods[name], arguments,
                    JsonSerializer.SerializeToElement(data["inputSchema"])));
            }
            Assert.Equal(0, network.Calls + services.Calls);
        }
    }
}
