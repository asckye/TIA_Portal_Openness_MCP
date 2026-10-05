using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens.Services;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class DriveContractsTests : IDisposable
    {
        private readonly DccService dcc = new DccService();
        private readonly StartdriveService drive = new StartdriveService();
        private readonly TeamcenterService teamcenter = new TeamcenterService();
        private readonly ToolCatalog catalog;
        private readonly Dictionary<string, McpServerTool> tools;
        private const string Paths = "\"devicePath\":[\"Drive\"],\"itemPath\":[\"Axis\"]";

        public DriveContractsTests()
        {
            catalog = new ToolCatalog(new[] { typeof(DccTools), typeof(StartdriveTools), typeof(TeamcenterTools) });
            McpServer.ConfigureToolBridge(catalog, () => false, new HashSet<string>());
            EngineServices.SetServiceProvider(new ServiceCollection().AddSingleton(new DccTools(dcc))
                .AddSingleton(new StartdriveTools(drive)).AddSingleton(new TeamcenterTools(teamcenter)).BuildServiceProvider());
            tools = catalog.Methods.ToDictionary(p => p.Key, p => (McpServerTool)new VersionPolicyTool(McpServer.CreateTool(p.Key, p.Value)));
        }
        public void Dispose() => ToolBridgeFixture.Configure();
        private int Calls => dcc.Calls + drive.Calls + teamcenter.Calls;
        private static ToolArguments Arguments(string json) => new ToolArguments(JsonSerializer.Deserialize<JsonElement>(json));
        private static RequestContext<CallToolRequestParams> Request(string name, string json)
            => new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ToolCatalogServerProxy>())
            { Params = new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } };
        private static JsonObject Body(CallToolResult result)
        {
            string text = ((TextContentBlock)Assert.Single(result.Content)).Text;
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), result.StructuredContent));
            V4Json.Deserialize<Envelope>(text);
            var body = result.StructuredContent!.AsObject();
            Assert.Equal(4, (int)body["schemaVersion"]!);
            Assert.Equal(!(bool)body["ok"]!, result.IsError);
            return body;
        }
        private static void Rejected(CallToolResult result, string code = "INVALID_ARGUMENT")
        {
            var body = Body(result);
            Assert.Equal(code, (string?)body["error"]!["code"]);
            Assert.Equal("rejected-before-operation", (string?)body["meta"]!["outcome"]);
            Assert.Equal("not-started", (string?)body["meta"]!["execution"]);
        }

        [Fact]
        public void EveryEntryHasOneV4NameAndConcreteParameterTypes()
        {
            string[] names = { "ListDccCharts", "GetDccObject", "ManageDccChart", "ManageDccBlock", "ManageDccPin",
                "ManageDccChartInterface", "ManageDccChartPartition", "ManageDcbLibraries", "ListDriveObjects", "GetDriveParameters",
                "ManageStartdriveParameter", "ManageDriveTelegrams", "ManageDriveFunctions", "ManageDriveSecurity", "ManageTechnologyExtensions",
                "ManageDriveHardwareModule", "ManageDriveSafetyAcceptanceTest", "GetOnlineDriveParameters", "ManageOnlineDriveFunctions",
                "ManageTeamcenterConnection", "ManageTeamcenterDataset", "ManageTeamcenterWorkflow" };
            Assert.Equal(names.OrderBy(n => n), catalog.Methods.Select(p => p.Key).OrderBy(n => n));
            var typed = new Dictionary<string, Type> { ["devicePath"] = typeof(string[]), ["itemPath"] = typeof(string[]),
                ["names"] = typeof(string[]), ["numbers"] = typeof(ParameterRef[]), ["properties"] = typeof(AttributeMap<Scalar>),
                ["customAttributes"] = typeof(AttributeMap<Scalar>), ["partner"] = typeof(DccPartnerSpec),
                ["itemDetails"] = typeof(TeamcenterItemSpec), ["revisionDetails"] = typeof(TeamcenterRevisionSpec), ["value"] = typeof(NativeValue) };
            foreach (var pair in catalog.Methods)
            {
                Assert.Equal(typeof(CallToolResult), pair.Value.ReturnType);
                Assert.DoesNotContain("\"default\":null", tools[pair.Key].ProtocolTool.InputSchema.GetRawText());
                foreach (var parameter in pair.Value.GetParameters())
                {
                    Assert.False(parameter.Name!.EndsWith("Json", StringComparison.Ordinal));
                    if (typed.TryGetValue(parameter.Name, out var type)) Assert.Equal(type, parameter.ParameterType);
                }
            }
            Assert.Equal(typeof(PropertyStep[]), catalog.Methods.Single(p => p.Key == "GetDccObject").Value.GetParameters().Single(p => p.Name == "objectPath").ParameterType);
        }

        [Theory]
        [InlineData("ListDriveObjects", "{\"devicePath\":\"[]\",\"itemPath\":[]}")]
        [InlineData("GetDriveParameters", "{" + Paths + ",\"names\":null}")]
        [InlineData("GetDriveParameters", "{" + Paths + ",\"numbers\":[1]}")]
        [InlineData("GetDriveParameters", "{" + Paths + ",\"numbers\":[{\"number\":1.5}]}")]
        [InlineData("GetDriveParameters", "{" + Paths + ",\"numbers\":[{\"number\":2147483648}]}")]
        [InlineData("GetDriveParameters", "{" + Paths + ",\"numbers\":[{\"number\":1,\"arrayIndex\":32768}]}")]
        [InlineData("GetOnlineDriveParameters", "{" + Paths + ",\"numbers\":[{\"Number\":1}]}")]
        [InlineData("GetDccObject", "{" + Paths + ",\"objectPath\":[{\"property\":\"Charts\",\"name\":\"A\",\"index\":0}]}")]
        [InlineData("ManageDccBlock", "{" + Paths + ",\"chartPath\":\"C\",\"properties\":{\"Name\":{}}}")]
        [InlineData("ManageDccPin", "{" + Paths + ",\"chartPath\":\"C\",\"blockName\":\"B\",\"pinName\":\"IN\",\"partner\":{}}")]
        [InlineData("ManageDccPin", "{" + Paths + ",\"chartPath\":\"C\",\"blockName\":\"B\",\"pinName\":\"IN\",\"partner\":{\"block\":\"B\",\"pin\":\"P\",\"chartInterface\":\"I\"}}")]
        [InlineData("ManageDccPin", "{" + Paths + ",\"chartPath\":\"C\",\"blockName\":\"B\",\"pinName\":\"IN\",\"partner\":{\"block\":\"B\",\"pin\":\"P\",\"Pin\":\"Q\"}}")]
        [InlineData("ManageTeamcenterWorkflow", "{\"action\":\"saveAsNewItem\",\"itemDetails\":{\"itemName\":\"A\"}}")]
        [InlineData("ManageTeamcenterWorkflow", "{\"action\":\"saveAsNewItem\",\"itemDetails\":\"{}\"}")]
        [InlineData("ManageTeamcenterWorkflow", "{\"action\":\"saveAsNewRevision\",\"revisionDetails\":{\"extra\":true}}")]
        [InlineData("ManageTeamcenterWorkflow", "{\"action\":\"saveAsNewRevision\",\"customAttributes\":{\"Cost\":[]}}")]
        [InlineData("ManageDriveFunctions", "{" + Paths + ",\"valueJson\":\"{}\"}")]
        public async Task TypedInputsAreRejectedThroughDirectCallToolAndBatch(string name, string json)
        {
            Rejected(await tools[name].InvokeAsync(Request(name, json)));
            Rejected(McpServer.CallTool(name, Arguments(json)));
            var calls = new[] { new ToolCall(name, Arguments(json)) };
            Rejected(name.StartsWith("Get", StringComparison.Ordinal) || name.StartsWith("List", StringComparison.Ordinal)
                ? McpServer.ReadToolBatch(calls) : McpServer.PreviewToolBatch(calls, "fixture"));
            Assert.Equal(0, Calls);
        }

        [Fact]
        public async Task DuplicateDomainFieldsAreRejectedByDirectAdmissionAndToolArguments()
        {
            const string name = "ManageTeamcenterWorkflow";
            const string json = "{\"action\":\"saveAsNewRevision\",\"revisionDetails\":{\"comment\":\"A\",\"comment\":\"B\"}}";
            Rejected(await tools[name].InvokeAsync(Request(name, json)));
            Assert.ThrowsAny<Exception>(() => Arguments(json));
            Assert.Equal(0, Calls);
        }

        [Theory]
        [InlineData("ManageStartdriveParameter", ",\"driveObjectNumber\":0,\"parameter\":\"p1000\",\"action\":\"write\",\"value\":[]")]
        [InlineData("ManageStartdriveParameter", ",\"driveObjectNumber\":0,\"parameter\":\"p1000\",\"action\":\"write\",\"value\":{\"bicoSource\":\"r1\",\"extra\":true}")]
        [InlineData("ManageDriveFunctions", ",\"action\":\"setMotorCode\",\"value\":{\"motorCode\":1,\"motorDataSet\":65536}")]
        [InlineData("ManageDriveFunctions", ",\"action\":\"readMotorConfiguration\",\"value\":{\"entries\":{\"p305\":2}}")]
        [InlineData("ManageDriveFunctions", ",\"action\":\"read\",\"value\":\"{}\"")]
        [InlineData("ManageDccBlock", ",\"chartPath\":\"C\",\"action\":\"update\",\"properties\":{\"name\":\"B\"}")]
        [InlineData("ManageDccPin", ",\"chartPath\":\"C\",\"blockName\":\"B\",\"pinName\":\"P\",\"partner\":{\"chartInterface\":\"I\"}")]
        [InlineData("GetDccObject", ",\"objectPath\":[{\"property\":\"Parent\"}]")]
        public async Task ActionPoliciesRejectBeforeTheService(string name, string suffix)
        {
            string json = "{" + Paths + suffix + "}";
            Rejected(await tools[name].InvokeAsync(Request(name, json)));
            Rejected(McpServer.CallTool(name, Arguments(json)));
            Assert.Equal(0, Calls);
        }

        [Fact]
        public void SelectorsPreserveOrderIndicesAndOmittedDefaults()
        {
            var result = McpServer.CallTool("GetDriveParameters", Arguments("{" + Paths + ",\"numbers\":[{\"number\":47},{\"number\":2051,\"arrayIndex\":0},{\"number\":47}]}"));
            Assert.True((bool)Body(result)["ok"]!);
            Assert.Equal("[]", drive.LastArguments[5]);
            Assert.Equal("[{\"number\":47},{\"number\":2051,\"arrayIndex\":0},{\"number\":47}]", drive.LastArguments[6]);
            Assert.Equal(1, Calls);
        }

        [Fact]
        public void AllGroupExamplesPassSharedAdmission()
        {
            foreach (var entry in TiaOpenness.Shared.ToolUsageCatalog.ProfileEntries(McpServer.ReleaseKey))
            {
                string name = (string)entry!["currentName"]!;
                if (!catalog.Methods.ToDictionary(p => p.Key, p => p.Value).TryGetValue(name, out var method)) continue;
                Assert.Equal(4, (int)entry["envelopeVersion"]!);
                Assert.Null(McpServer.ValidateV4Arguments(method, JsonSerializer.SerializeToElement(entry["arguments"]), tools[name].ProtocolTool.InputSchema));
            }
        }

        [Theory]
        [InlineData("{\"block\":\"B2\",\"pin\":\"OUT\"}")]
        [InlineData("{\"chartInterface\":\"IN\"}")]
        public void BothDccPartnerBranchesReachTheServiceUnchanged(string partner)
        {
            var result = McpServer.CallTool("ManageDccPin", Arguments("{" + Paths
                + ",\"chartPath\":\"C\",\"blockName\":\"B\",\"pinName\":\"P\",\"action\":\"connect\",\"partner\":" + partner + "}"));
            var body = Body(result);
            Assert.True((bool)body["ok"]!);
            Assert.Equal("read-only", (string?)body["meta"]!["execution"]);
            Assert.Equal(partner, dcc.LastArguments[7]);
        }

        [Fact]
        public void TeamcenterTypedDetailsAndDynamicAttributeCaseSurvive()
        {
            const string item = "{\"itemName\":\"Example\",\"teamcenterItemType\":\"T4TiaProject\",\"teamcenterProject\":[\"P\"]}";
            var result = McpServer.CallTool("ManageTeamcenterWorkflow", Arguments("{\"action\":\"saveAsNewItem\",\"itemDetails\":"
                + item + ",\"customAttributes\":{\"Cost\":3,\"Approved\":true}}"));
            Assert.True((bool)Body(result)["ok"]!);
            Assert.Equal(item, teamcenter.LastArguments[7]);
            Assert.Equal("{}", teamcenter.LastArguments[8]);
            Assert.Equal("{\"Cost\":3,\"Approved\":true}", teamcenter.LastArguments[9]);
        }

        [Theory]
        [InlineData(true, "read-only")]
        [InlineData(false, "completed")]
        public async Task SuccessfulWriteAndPreviewUseTheirActualExecutionMode(bool dryRun, string execution)
        {
            const string name = "ManageStartdriveParameter";
            string json = "{" + Paths + ",\"driveObjectNumber\":0,\"parameter\":\"p1120[0]\",\"action\":\"write\",\"value\":11,\"dryRun\":" + (dryRun ? "true" : "false") + "}";
            foreach (var result in new[] { await tools[name].InvokeAsync(Request(name, json)), McpServer.CallTool(name, Arguments(json)) })
            {
                var body = Body(result);
                Assert.True((bool)body["ok"]!);
                Assert.Equal(execution, (string?)body["meta"]!["execution"]);
                Assert.Equal("current", (string?)body["meta"]!["behaviorPolicy"]);
            }
            Assert.Equal("11", drive.LastArguments[5]);
        }

        [Theory]
        [InlineData("{\"expectedCount\":501,\"records\":[]}")]
        [InlineData("{\"chart\":{\"subchartCount\":1}}")]
        [InlineData("{\"notFound\":[\"p1\"]}")]
        public void BoundedOrMissingObservationsRemainPartial(string json)
        {
            var evidence = JsonNode.Parse(json)!.AsObject(); evidence["success"] = true;
            var body = Body(DriveToolContract.Map("ListDccCharts", new ResponseMessage { Meta = evidence }, false, false));
            Assert.True((bool)body["ok"]!);
            Assert.Equal("partial", (string?)body["meta"]!["completeness"]);
            Assert.Equal("INCOMPLETE_DATA", (string?)body["meta"]!["warnings"]![0]!["code"]);
        }

        [Fact]
        public async Task ReadBatchPreservesTheDirectAndBridgeEnvelope()
        {
            const string name = "ListDriveObjects";
            const string json = "{" + Paths + "}";
            drive.Response.Meta!["records"] = new JsonArray(new JsonObject { ["index"] = 0 });
            var direct = Body(await tools[name].InvokeAsync(Request(name, json)));
            var bridge = Body(McpServer.CallTool(name, Arguments(json)));
            var batch = Body(McpServer.ReadToolBatch(new[] { new ToolCall(name, Arguments(json)) }));
            var child = batch["data"]!["items"]![0]!["result"]!.AsObject();
            foreach (var body in new[] { direct, bridge, child })
            {
                Assert.True((bool)body["ok"]!);
                Assert.Equal(name, (string?)body["meta"]!["tool"]);
                body["meta"]!.AsObject().Remove("timestamp"); body["meta"]!.AsObject().Remove("requestId");
            }
            Assert.True(JsonNode.DeepEquals(direct, bridge));
            Assert.True(JsonNode.DeepEquals(direct, child));
        }

        [Theory]
        [InlineData("{}", false, "read-failed", "read-only", "unknown", "NATIVE_OPERATION_FAILED")]
        [InlineData("{}", true, "unknown", "unknown", "unknown", "OUTCOME_UNKNOWN")]
        [InlineData("{\"status\":\"InvalidState\",\"operationSuccess\":false}", true, "rejected-before-operation", "not-started", "none", "PROJECT_NOT_BOUND")]
        [InlineData("{\"status\":\"InvalidState\",\"apiCallSuccess\":false,\"mayHaveChanged\":false}", true, "rejected-before-operation", "not-started", "none", "PRECONDITION_FAILED")]
        [InlineData("{\"success\":true,\"result\":false,\"mayHaveChanged\":true}", true, "failed", "completed", "complete", "NATIVE_OPERATION_FAILED")]
        [InlineData("{\"success\":false,\"result\":false,\"mayHaveChanged\":true,\"appliedEntries\":[{\"name\":\"p305\",\"value\":2}]}", true, "partial", "partial", "partial", "PARTIAL_FAILURE")]
        [InlineData("{\"success\":false,\"result\":false,\"apiCallSuccess\":false,\"mayHaveChanged\":true}", true, "unknown", "unknown", "unknown", "OUTCOME_UNKNOWN")]
        [InlineData("{\"success\":true,\"mayHaveChanged\":true,\"requiresExplicitRebind\":true}", true, "unknown", "unknown", "unknown", "OUTCOME_UNKNOWN")]
        [InlineData("{\"success\":true,\"verifiedAbsent\":false,\"mayHaveChanged\":true}", true, "unknown", "unknown", "unknown", "OUTCOME_UNKNOWN")]
        [InlineData("{\"success\":false,\"mayHaveWrittenFiles\":true,\"dccException\":{\"type\":\"DccExportException\",\"family\":\"export\"}}", true, "unknown", "unknown", "unknown", "OUTCOME_UNKNOWN")]
        public void EnvelopesFollowEvidenceNeverFailureText(string json, bool writes, string outcome, string execution, string completeness, string code)
        {
            var response = new ResponseMessage { Message = "success", Meta = JsonNode.Parse(json)!.AsObject() };
            var body = Body(DriveToolContract.Map("ManageDriveFunctions", response, writes, true));
            Assert.Equal(outcome, (string?)body["meta"]!["outcome"]);
            Assert.Equal(execution, (string?)body["meta"]!["execution"]);
            Assert.Equal(completeness, (string?)body["meta"]!["completeness"]);
            Assert.Equal(code, (string?)body["error"]!["code"]);
            Assert.Equal(outcome == "unknown", (bool)body["meta"]!["requiresSessionReset"]!);
            Assert.Equal("current", (string?)body["meta"]!["behaviorPolicy"]);
            Assert.Equal("UNVERIFIED_BEHAVIOR", (string?)body["meta"]!["warnings"]![0]!["code"]);
            Assert.False((bool)body["ok"]!);
            Assert.Null(body["data"]!["summary"]);
        }

        [Fact]
        public void IncompleteReadsKeepRowsPagingAndTypedDccEvidenceWithoutStacks()
        {
            var response = new ResponseMessage { Meta = new JsonObject { ["success"] = true, ["dataComplete"] = false,
                ["records"] = new JsonArray(new JsonObject { ["name"] = "C" }), ["expectedCount"] = 3, ["nextOffset"] = 1,
                ["dccException"] = new JsonObject { ["type"] = "DccLicenseUnavailableException", ["family"] = "licence", ["licenceMissing"] = true, ["message"] = "private diagnostic" },
                ["error"] = "secret\n   at Native.Call()", ["password"] = "secret" } };
            var body = Body(DriveToolContract.Map("GetDccObject", response, false, false, 0, 1));
            Assert.True((bool)body["ok"]!);
            Assert.Equal("partial", (string?)body["meta"]!["completeness"]);
            Assert.Equal(1, (int)body["meta"]!["paging"]!["nextOffset"]!);
            Assert.Equal("C", (string?)body["data"]!["items"]![0]!["name"]);
            Assert.True((bool)body["data"]!["evidence"]!["dccException"]!["licenceMissing"]!);
            Assert.DoesNotContain("secret", body.ToJsonString());
            Assert.DoesNotContain("private diagnostic", body.ToJsonString());
        }
    }
}

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class DccService
    {
        internal int Calls;
        internal object?[] LastArguments = Array.Empty<object?>();
        internal ResponseMessage Response = new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        public ResponseMessage ReadDccCharts(string devicePathJson,
            string itemPathJson,
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            string chartPath="",
            int maxDepth=3,
            bool includeBlocks=true,
            bool includePins=false,
            bool includeLibraries=true)
        { Calls++; LastArguments = new object?[] { devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex, chartPath, maxDepth, includeBlocks, includePins, includeLibraries }; return Response; }
        public ResponseMessage ManageDccChart(string devicePathJson, string itemPathJson, ushort driveObjectNumber, string chartName, string action, string filePath = "", string importOptions = "", string propertiesJson = "{}", bool dryRun = true, int driveObjectIndex = -1, bool confirmDelete = false, int sequenceIndex = -1)
        { Calls++; LastArguments = new object?[] { devicePathJson, itemPathJson, driveObjectNumber, chartName, action, filePath, importOptions, propertiesJson, dryRun, driveObjectIndex, confirmDelete, sequenceIndex }; return Response; }
        public ResponseMessage ManageDccBlock(string devicePathJson,
            string itemPathJson,
            string chartPath,
            string blockName="",
            string action="read",
            string blockType="",
            string libraryName="",
            string propertiesJson="{}",
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            bool confirmDelete=false,
            int sequenceIndex=-1,
            bool dryRun=true)
        { Calls++; LastArguments = new object?[] { devicePathJson, itemPathJson, chartPath, blockName, action, blockType, libraryName, propertiesJson, driveObjectNumber, driveObjectIndex, confirmDelete, sequenceIndex, dryRun }; return Response; }
        public ResponseMessage ManageDccPin(string devicePathJson,
            string itemPathJson,
            string chartPath,
            string blockName,
            string pinName,
            string action="read",
            string propertiesJson="{}",
            string partnerJson="{}",
            bool setAsSignal=false,
            int parameterNumber=-1,
            int arrayIndex=-1,
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            bool dryRun=true)
        { Calls++; LastArguments = new object?[] { devicePathJson, itemPathJson, chartPath, blockName, pinName, action, propertiesJson, partnerJson, setAsSignal, parameterNumber, arrayIndex, driveObjectNumber, driveObjectIndex, dryRun }; return Response; }
        public ResponseMessage ManageDccChartInterface(string devicePathJson,
            string itemPathJson,
            string chartPath,
            string interfaceName="",
            string action="read",
            string sourceBlock="",
            string sourcePin="",
            string propertiesJson="{}",
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            bool confirmDelete=false,
            bool dryRun=true)
        { Calls++; LastArguments = new object?[] { devicePathJson, itemPathJson, chartPath, interfaceName, action, sourceBlock, sourcePin, propertiesJson, driveObjectNumber, driveObjectIndex, confirmDelete, dryRun }; return Response; }
        public ResponseMessage ManageDccChartPartition(string devicePathJson,
            string itemPathJson,
            string chartPath,
            string partitionName="",
            string action="read",
            string propertiesJson="{}",
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            bool confirmDelete=false,
            bool dryRun=true)
        { Calls++; LastArguments = new object?[] { devicePathJson, itemPathJson, chartPath, partitionName, action, propertiesJson, driveObjectNumber, driveObjectIndex, confirmDelete, dryRun }; return Response; }
        public ResponseMessage ManageDcbLibraries(string action="read",
            string devicePathJson="[]",
            string itemPathJson="[]",
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            string filePath="",
            bool dryRun=true)
        { Calls++; LastArguments = new object?[] { action, devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex, filePath, dryRun }; return Response; }
        public ResponseMessage ReadDccObject(string devicePathJson, string itemPathJson, ushort driveObjectNumber, string objectPathJson = "[]", int offset = 0, int limit = 100, int driveObjectIndex = -1)
        { Calls++; LastArguments = new object?[] { devicePathJson, itemPathJson, driveObjectNumber, objectPathJson, offset, limit, driveObjectIndex }; return Response; }
    }
    internal sealed class StartdriveService
    {
        internal int Calls;
        internal object?[] LastArguments = Array.Empty<object?>();
        internal ResponseMessage Response = new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        public ResponseMessage ReadDriveObjects(string devicePathJson,
            string itemPathJson,
            bool includeTelegrams=true,
            bool includeFunctions=true,
            bool includeTechnologyExtensions=true,
            bool includeDcc=true)
        { Calls++; LastArguments = new object?[] { devicePathJson, itemPathJson, includeTelegrams, includeFunctions, includeTechnologyExtensions, includeDcc }; return Response; }
        public ResponseMessage ReadDriveParameters(string devicePathJson,
            string itemPathJson,
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            string source="read",
            string namesJson="[]",
            string numbersJson="[]",
            bool includeBits=false,
            bool includeEnumValues=false,
            int offset=0,
            int limit=100,
            bool includeValue=true)
        { Calls++; LastArguments = new object?[] { devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex, source, namesJson, numbersJson, includeBits, includeEnumValues, offset, limit, includeValue }; return Response; }
        public ResponseMessage ManageStartdriveParameter(string devicePathJson,
            string itemPathJson,
            ushort driveObjectNumber,
            string parameter,
            string action="read",
            string valueJson="null",
            bool dryRun=true,
            int driveObjectIndex=-1)
        { Calls++; LastArguments = new object?[] { devicePathJson, itemPathJson, driveObjectNumber, parameter, action, valueJson, dryRun, driveObjectIndex }; return Response; }
        public ResponseMessage ManageDriveTelegrams(string devicePathJson,
            string itemPathJson,
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            string action="read",
            string telegramType="",
            int telegramNumber=-1,
            int inputSize=-1,
            int outputSize=-1,
            string direction="",
            int size=-1,
            bool keepOriginalAddress=true,
            string softwarePath="",
            string objectPath="",
            string interfaceKind="",
            int sensorIndex=0,
            string connectOption="",
            bool dryRun=true)
        { Calls++; LastArguments = new object?[] { devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex, action, telegramType, telegramNumber, inputSize, outputSize, direction, size, keepOriginalAddress, softwarePath, objectPath, interfaceKind, sensorIndex, connectOption, dryRun }; return Response; }
        public ResponseMessage ManageDriveFunctions(string devicePathJson,
            string itemPathJson,
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            string action="read",
            string valueJson="{}",
            bool dryRun=true)
        { Calls++; LastArguments = new object?[] { devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex, action, valueJson, dryRun }; return Response; }
        public ResponseMessage ManageDriveSecurity(string devicePathJson,
            string itemPathJson,
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            string action="read",
            string password="",
            bool dryRun=true)
        { Calls++; LastArguments = new object?[] { devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex, action, password, dryRun }; return Response; }
        public ResponseMessage ManageTechnologyExtensions(string action="read",
            string devicePathJson="[]",
            string itemPathJson="[]",
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            string identifier="",
            string filePath="",
            bool confirmUninstallInUse=false,
            bool includeParameters=false,
            bool dryRun=true)
        { Calls++; LastArguments = new object?[] { action, devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex, identifier, filePath, confirmUninstallInUse, includeParameters, dryRun }; return Response; }
        public ResponseMessage ManageDriveHardwareModule(string devicePathJson,
            string itemPathJson,
            string action="read",
            string typeIdentifier="",
            int positionNumber=-1,
            bool dryRun=true)
        { Calls++; LastArguments = new object?[] { devicePathJson, itemPathJson, action, typeIdentifier, positionNumber, dryRun }; return Response; }
        public ResponseMessage ManageDriveSafetyAcceptanceTest(string devicePathJson,
            string itemPathJson,
            string action="read",
            string identifier="",
            bool active=false,
            string filePath="",
            string fileOperation="",
            bool dryRun=true)
        { Calls++; LastArguments = new object?[] { devicePathJson, itemPathJson, action, identifier, active, filePath, fileOperation, dryRun }; return Response; }
        public ResponseMessage ReadOnlineDriveParameters(string devicePathJson,
            string itemPathJson,
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            string namesJson="[]",
            string numbersJson="[]",
            bool includeBits=false,
            bool includeEnumValues=false,
            int offset=0,
            int limit=100,
            bool includeValue=true)
        { Calls++; LastArguments = new object?[] { devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex, namesJson, numbersJson, includeBits, includeEnumValues, offset, limit, includeValue }; return Response; }
        public ResponseMessage ManageOnlineDriveFunctions(string devicePathJson,
            string itemPathJson,
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            string action="read",
            string resetMode="",
            string activationState="",
            bool confirmOnline=false)
        { Calls++; LastArguments = new object?[] { devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex, action, resetMode, activationState, confirmOnline }; return Response; }
    }
    internal sealed class TeamcenterService
    {
        internal int Calls;
        internal object?[] LastArguments = Array.Empty<object?>();
        internal ResponseMessage Response = new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        public ResponseMessage ManageTeamcenterConnection(string action="read",
            string userName="",
            string password="",
            string group="",
            string role="",
            string hostUrl="",
            string instance="",
            string loginUrl="",
            string applicationId="",
            bool dryRun=true)
        { Calls++; LastArguments = new object?[] { action, userName, password, group, role, hostUrl, instance, loginUrl, applicationId, dryRun }; return Response; }
        public ResponseMessage ManageTeamcenterDataset(string action,
            string itemId="",
            string revisionId="",
            string datasetType="",
            string datasetName="",
            string itemType="",
            string tiaObjectName="",
            string itemName="",
            string localCacheOption="",
            bool dryRun=true)
        { Calls++; LastArguments = new object?[] { action, itemId, revisionId, datasetType, datasetName, itemType, tiaObjectName, itemName, localCacheOption, dryRun }; return Response; }
        public ResponseMessage ManageTeamcenterWorkflow(string action,
            string target="project",
            string libraryName="",
            string itemType="",
            string itemId="",
            string revisionId="",
            string localCacheOption="",
            string itemDetailsJson="{}",
            string revisionDetailsJson="{}",
            string customAttributesJson="{}",
            bool confirmSave=false,
            bool dryRun=true)
        { Calls++; LastArguments = new object?[] { action, target, libraryName, itemType, itemId, revisionId, localCacheOption, itemDetailsJson, revisionDetailsJson, customAttributesJson, confirmSave, dryRun }; return Response; }
    }
}
