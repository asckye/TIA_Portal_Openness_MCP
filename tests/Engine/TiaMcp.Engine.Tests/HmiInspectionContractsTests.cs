using TiaMcpServer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4.Domain;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens.Services;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class HmiInspectionContractsTests : IDisposable
    {
        private readonly UnifiedObjectServicesService objects = new UnifiedObjectServicesService();
        private readonly UnifiedEngineeringService engineering = new UnifiedEngineeringService();
        private readonly ReflectionService reflection = new ReflectionService();
        private readonly FakeHmiToolSession branchSession = new FakeHmiToolSession();
        private readonly ToolCatalog catalog;
        public HmiInspectionContractsTests()
        {
            var hmi = new global::Siemens.Engineering.HmiUnified.HmiSoftware();
            var screen = new GraphicSelectionTests.Screen();
            screen.ScreenItems.Add(new BranchProbe()); hmi.Screens.Add(screen);
            branchSession.FixtureRoot = hmi;
            catalog = new ToolCatalog(new[] { typeof(HmiInspectionTools), typeof(MigrationReadTools), typeof(GraphicSelectionTools),
                typeof(GlobalScriptEditTools), typeof(ReflectionTools), typeof(UnifiedEngineeringTools), typeof(UnifiedEventsTools), typeof(UnifiedObjectServicesTools) });
            McpServer.ConfigureToolBridge(catalog, () => false, new HashSet<string>());
            EngineServices.SetServiceProvider(new ServiceCollection()
                .AddSingleton<TiaMcpServer.Siemens.Portal>().AddSingleton<SessionTools>()
                .AddSingleton(new UnifiedObjectServicesTools(objects)).AddSingleton(new UnifiedEngineeringTools(engineering))
                .AddSingleton(new ReflectionTools(reflection)).AddSingleton(new UnifiedEventsTools(new UnifiedEventsService()))
                .AddSingleton(HmiToolFixture.HmiInspection).AddSingleton(new MigrationReadTools(new MigrationReadService(branchSession)))
                .AddSingleton(HmiToolFixture.GraphicSelection).AddSingleton(HmiToolFixture.GlobalScriptEdit).BuildServiceProvider());
        }
        public void Dispose() => ToolBridgeFixture.Configure();
        private static ToolArguments Args(string json) => new ToolArguments(JsonSerializer.Deserialize<JsonElement>(json));
        private static JsonObject Body(CallToolResult result)
        {
            string text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), result.StructuredContent));
            var envelope = V4Json.Deserialize<Envelope>(text);
            Assert.Equal(!envelope.Ok, result.IsError);
            return result.StructuredContent!.AsObject();
        }
        private MethodInfo Method(string name) => catalog.Methods.Single(p => p.Key == name).Value;
        private async Task<CallToolResult> Direct(string name, string json)
        {
            var tool = new VersionPolicyTool(McpServer.CreateTool(name, Method(name)));
            var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ToolCatalogServerProxy>())
            { Params = new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } };
            return await tool.InvokeAsync(request);
        }
        public sealed class BranchProbe
        {
            public string Name => "Probe";
            public string Label => "exact-value";
            public List<BranchValue> Values { get; } = new List<BranchValue> { new BranchValue() };
            public object GetAttribute(string name) => name == "Label" ? Label : throw new ArgumentException(name);
        }
        public sealed class BranchValue
        {
            public string Name => "Exact/Name";
            public string Code => "Exact,Code";
            public string Label => "selected-value";
        }
        private static string BranchArgs(string path) => "{\"softwarePath\":\"HMI\",\"expectedProject\":\"Project_A\",\"screenPath\":\"/Main\",\"itemName\":\"Probe\",\"branch\":" + path + "}";
        private static JsonObject BatchItem(CallToolResult result) => Body(result)["data"]!["items"]![0]!["result"]!.AsObject();

        [Theory]
        [InlineData("[]")]
        [InlineData("[{\"property\":\"Label\"}]")]
        [InlineData("[{\"attribute\":\"Label\"}]")]
        [InlineData("[{\"property\":\"Values\"},{\"index\":0},{\"property\":\"Label\"}]")]
        [InlineData("[{\"property\":\"Values\"},{\"name\":\"Exact/Name\"},{\"property\":\"Label\"}]")]
        [InlineData("[{\"property\":\"Values\"},{\"name\":\"Exact,Code\",\"key\":\"Code\"},{\"property\":\"Label\"}]")]
        public async Task BranchShapesReachTheOriginalParserThroughEveryPath(string path)
        {
            const string name = "GetUnifiedScreenBranch";
            string json = BranchArgs(path);
            var results = new[] { Body(await Direct(name, json)), Body(McpServer.CallTool(name, Args(json))),
                BatchItem(McpServer.ReadToolBatch(new[] { new ToolCall(name, Args(json)) })) };
            foreach (var body in results)
            {
                Assert.True((bool)body["ok"]!, body.ToJsonString());
                var data = body["data"]!;
                Assert.Equal("ReadUnifiedScreenBranch", (string?)data["scope"]!["tool"]);
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(path), JsonNode.Parse((string)data["scope"]!["branch"]!)));
                var selection = data["records"]!.AsArray().Single(n => (string?)n!["kind"] == "branchSelection");
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(path), selection!["branch"]));
            }
            Assert.Equal(3, branchSession.FixtureResolveCalls);
        }

        [Theory]
        [InlineData("null")]
        [InlineData("\"[]\"")]
        [InlineData("[\"Screens\"]")]
        [InlineData("[null]")]
        [InlineData("[{}]")]
        [InlineData("[{\"Property\":\"Label\"}]")]
        [InlineData("[{\"property\":\"Label\",\"name\":\"Probe\"}]")]
        [InlineData("[{\"attribute\":\"Label\",\"extra\":true}]")]
        [InlineData("[{\"index\":-1}]")]
        [InlineData("[{\"index\":0.5}]")]
        [InlineData("[{\"index\":2147483648}]")]
        [InlineData("[{\"key\":\"Name\"}]")]
        [InlineData("[{\"name\":\"Probe\",\"key\":null}]")]
        public async Task InvalidBranchesAreRejectedBeforeResolutionOnEveryPath(string path)
            => await AssertBranchesRejected(path, "INVALID_ARGUMENT");

        private async Task AssertBranchesRejected(string path, string code)
        {
            const string name = "GetUnifiedScreenBranch";
            string json = BranchArgs(path);
            foreach (var result in new[] { await Direct(name, json), McpServer.CallTool(name, Args(json)),
                McpServer.ReadToolBatch(new[] { new ToolCall(name, Args(json)) }) })
                Assert.Equal(code, (string?)Body(result)["error"]!["code"]);
            Assert.Equal(0, branchSession.FixtureResolveCalls);
        }

        [Fact]
        public async Task BranchLimitUsesTheSharedDomainContractAndAllCallPaths()
        {
            string Steps(int count) => "[" + string.Join(",", Enumerable.Repeat("{\"index\":0}", count)) + "]";
            var contract = DomainValidation.Contract<BranchStep[]>();
            Assert.Null(contract.Read(Steps(64), "branch").Error);
            Assert.NotNull(contract.Read(Steps(65), "branch").Error);
            await AssertBranchesRejected(Steps(65), "LIMIT_EXCEEDED");
            Assert.Equal(64, DomainSchemas.Get<BranchStep[]>()["maxItems"]!.GetValue<int>());
            Assert.Equal(4, DomainSchemas.Get<BranchStep[]>()["items"]!["oneOf"]!.AsArray().Count);
        }

        private static string LoggingArgs(string name, string path, string properties = "{}") => name == "ManageUnifiedLoggingTag"
            ? "{\"softwarePath\":\"HMI\",\"tagPath\":" + path + ",\"properties\":" + properties + "}"
            : "{\"softwarePath\":\"HMI\",\"durationPath\":" + path + ",\"kind\":\"log\",\"days\":1,\"hours\":2,\"minutes\":3,\"seconds\":4,\"hundredNanoseconds\":5}";

        [Theory]
        [InlineData("ManageUnifiedLoggingTag")]
        [InlineData("SetUnifiedLogDuration")]
        public async Task LoggingPathsPreserveObjectStepsAndRejectStringsThroughEveryPath(string name)
        {
            const string path = "[{\"property\":\"Tags\",\"name\":\"Exact/Name\"}]";
            string json = LoggingArgs(name, path, "{\"Enabled\":true,\"Interval\":\"00:00:01\"}");
            foreach (var body in new[] { Body(await Direct(name, json)), Body(McpServer.CallTool(name, Args(json))),
                BatchItem(McpServer.PreviewToolBatch(new[] { new ToolCall(name, Args(json)) }, "Fixture")) })
                Assert.True((bool)body["ok"]!);
            Assert.Equal(3, objects.Calls);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(path), JsonNode.Parse(objects.LastPath!)));
            if (name == "ManageUnifiedLoggingTag") Assert.Equal("00:00:01", (string?)JsonNode.Parse(objects.LastInput!)!["Interval"]);
            string overLimit = "[" + string.Join(",", Enumerable.Repeat("{\"property\":\"Tags\"}", 25)) + "]";
            foreach (var invalid in new[] { "[\"Tags\"]", "null", "\"[]\"", "[{\"property\":\"Tags\",\"unknown\":true}]", overLimit })
            {
                json = LoggingArgs(name, invalid);
                foreach (var result in new[] { await Direct(name, json), McpServer.CallTool(name, Args(json)),
                    McpServer.PreviewToolBatch(new[] { new ToolCall(name, Args(json)) }, "Project") })
                    Assert.Equal(invalid == overLimit ? "LIMIT_EXCEEDED" : "INVALID_ARGUMENT", (string?)Body(result)["error"]!["code"]);
            }
            Assert.Equal(3, objects.Calls);
        }

        [Fact]
        public async Task LoggingPropertiesRejectCompositeValuesThroughEveryPath()
        {
            const string name = "ManageUnifiedLoggingTag";
            string json = LoggingArgs(name, "[]", "{\"Enabled\":{}}");
            foreach (var result in new[] { await Direct(name, json), McpServer.CallTool(name, Args(json)),
                McpServer.PreviewToolBatch(new[] { new ToolCall(name, Args(json)) }, "Project") })
                Assert.Equal("INVALID_ARGUMENT", (string?)Body(result)["error"]!["code"]);
            Assert.Equal(0, objects.Calls);
        }
        [Theory]
        [InlineData("null")]
        [InlineData("\"[]\"")]
        [InlineData("[null]")]
        [InlineData("[{\"Property\":\"Screens\"}]")]
        [InlineData("[{\"property\":\"Screens\",\"extra\":1}]")]
        [InlineData("[{\"property\":\"Screens\",\"index\":0.5}]")]
        [InlineData("[{\"property\":\"Screens\",\"index\":2147483648}]")]
        [InlineData("[{\"property\":\"Screens\",\"name\":\"Main\",\"index\":0}]")]
        public async Task TypedPathRejectionsAreSharedByDirectBridgeAndBatch(string path)
        {
            const string name = "GetUnifiedObjectProperties";
            string json = "{\"softwarePath\":\"HMI\",\"objectPath\":" + path + "}";
            var results = new[] { await Direct(name, json), McpServer.CallTool(name, Args(json)),
                McpServer.ReadToolBatch(new[] { new ToolCall(name, Args(json)) }) };
            foreach (var result in results)
            {
                var body = Body(result);
                Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]!["code"]);
                Assert.Equal("rejected-before-operation", (string?)body["meta"]!["outcome"]);
                Assert.Equal("not-started", (string?)body["meta"]!["execution"]);
            }
            Assert.Equal(0, objects.Calls);
        }
        [Theory]
        [InlineData("null")]
        [InlineData("\"{}\"")]
        [InlineData("{\"Visible\":{}}")]
        public async Task ScalarMapsRejectBeforeDirectBridgeAndPreview(string properties)
        {
            const string name = "ManageUnifiedEngineeringObject";
            string json = "{\"softwarePath\":\"HMI\",\"category\":\"textLists\",\"name\":\"List\",\"action\":\"update\",\"properties\":" + properties + "}";
            foreach (var result in new[] { await Direct(name, json), McpServer.CallTool(name, Args(json)),
                McpServer.PreviewToolBatch(new[] { new ToolCall(name, Args(json)) }, "Project") })
                Assert.Equal("INVALID_ARGUMENT", (string?)Body(result)["error"]!["code"]);
            Assert.Equal(0, engineering.Calls);
        }
        [Fact]
        public async Task OmittedPathAndExplicitEmptyPathPreserveTheLegacyRoot()
        {
            foreach (var json in new[] { "{\"softwarePath\":\"HMI\"}", "{\"softwarePath\":\"HMI\",\"objectPath\":[]}" })
            {
                Assert.True((bool)Body(await Direct("GetUnifiedObjectProperties", json))["ok"]!);
                Assert.Equal("[]", objects.LastInput);
            }
            string exact = "[{\"property\":\"TagTables\",\"name\":\"exact/with,delimiters\"}]";
            Assert.True((bool)Body(McpServer.CallTool("GetUnifiedObjectProperties", Args("{\"softwarePath\":\"HMI\",\"objectPath\":" + exact + "}")))["ok"]!);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(exact), JsonNode.Parse(objects.LastInput!)));
        }
        [Fact]
        public async Task DuplicateFieldsAreRejectedAtTheWireAndTypedArgumentBoundary()
        {
            const string json = "{\"softwarePath\":\"HMI\",\"objectPath\":[{\"property\":\"Screens\",\"property\":\"Tags\"}]}";
            Assert.Equal("INVALID_ARGUMENT", (string?)Body(await Direct("GetUnifiedObjectProperties", json))["error"]!["code"]);
            Assert.ThrowsAny<Exception>(() => Args(json));
            Assert.Equal(0, objects.Calls);
        }
        [Fact]
        public async Task DirectBridgeAndBatchRetainTheTargetEnvelope()
        {
            const string json = "{\"softwarePath\":\"HMI\",\"objectPath\":[]}";
            var direct = Body(await Direct("GetUnifiedObjectProperties", json));
            var bridge = Body(McpServer.CallTool("GetUnifiedObjectProperties", Args(json)));
            var batch = Body(McpServer.ReadToolBatch(new[] { new ToolCall("GetUnifiedObjectProperties", Args(json)) }));
            foreach (var result in new[] { direct, bridge, batch["data"]!["items"]![0]!["result"]!.AsObject() })
            {
                Assert.Equal("GetUnifiedObjectProperties", (string?)result["meta"]!["tool"]);
                Assert.Null(result["data"]!["result"]);
                result["meta"]!.AsObject().Remove("timestamp"); result["meta"]!.AsObject().Remove("requestId");
            }
            Assert.True(JsonNode.DeepEquals(direct, bridge));
            Assert.Equal(3, objects.Calls);
        }
        [Theory]
        [InlineData("{\"success\":true}", false, "succeeded", "read-only", "complete")]
        [InlineData("{\"success\":true,\"dataComplete\":false}", false, "succeeded", "read-only", "partial")]
        [InlineData("{\"success\":true,\"operationSuccess\":null}", false, "read-failed", "read-only", "none")]
        [InlineData("{\"success\":true,\"apiCallSuccess\":null}", false, "read-failed", "read-only", "none")]
        [InlineData("{\"success\":false,\"mayHaveChanged\":false}", true, "rejected-before-operation", "not-started", "none")]
        [InlineData("{\"success\":false,\"mayHaveChanged\":true,\"appliedProperties\":[\"A\"]}", true, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":true,\"mayHaveChanged\":true,\"connectionUnavailable\":true}", true, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":false,\"status\":\"ReadbackMismatch\",\"apiCallSuccess\":true,\"importReturned\":true,\"mayHaveChanged\":true}", true, "partial", "partial", "complete")]
        [InlineData("{\"success\":false,\"v4Rejection\":\"ACCESS_DENIED\"}", true, "rejected-before-operation", "not-started", "none")]
        [InlineData("{}", true, "unknown", "unknown", "unknown")]
        public void EvidenceControlsOutcomeInsteadOfMessage(string json, bool writes, string outcome, string execution, string completeness)
        {
            var body = Body(HmiInspectionContract.Map("SetUnifiedGlobalScript", new ResponseMessage { Message = "Success", Meta = JsonNode.Parse(json)!.AsObject() }, writes, true));
            Assert.Equal(outcome, (string?)body["meta"]!["outcome"]);
            Assert.Equal(execution, (string?)body["meta"]!["execution"]);
            Assert.Equal(completeness, (string?)body["meta"]!["completeness"]);
            Assert.Equal(outcome == "unknown", (bool)body["meta"]!["requiresSessionReset"]!);
            Assert.Equal("current", (string?)body["meta"]!["behaviorPolicy"]);
            Assert.Contains(body["meta"]!["warnings"]!.AsArray(), w => (string?)w!["code"] == "UNVERIFIED_BEHAVIOR");
            if (json.Contains("appliedProperties")) Assert.Single(body["data"]!["appliedProperties"]!.AsArray());
        }
        [Fact]
        public void CursorIdentityAndObservationCompletenessStayIndependent()
        {
            var evidence = JsonNode.Parse("{\"success\":true,\"apiCallSuccess\":true,\"dataComplete\":false,\"collectionId\":\"snapshot\",\"pageIndex\":2,\"scope\":{\"project\":\"P\"},\"traversalComplete\":true,\"nextCursor\":null}")!.AsObject();
            var body = Body(HmiInspectionContract.Map("GetUnifiedGraphicSelection", new ResponseMessage { Meta = evidence }, false, false, "cursor", 20));
            Assert.True((bool)body["meta"]!["paging"]!["complete"]!);
            Assert.Equal("partial", (string?)body["meta"]!["completeness"]);
            Assert.Equal("snapshot", (string?)body["data"]!["page"]!["collectionId"]);
            Assert.Equal(2, (int)body["data"]!["page"]!["pageIndex"]!);
            Assert.Equal("P", (string?)body["data"]!["page"]!["scope"]!["project"]);
            Assert.Equal("cursor", (string?)body["meta"]!["paging"]!["cursor"]);
        }
        [Fact]
        public void GroupCatalogUsesConcreteTypesAndNoOldAliases()
        {
            Assert.Equal(42, catalog.Methods.Count);
            Assert.DoesNotContain("ReadUnifiedObjectProperties", catalog.Methods.Select(p => p.Key));
            Assert.DoesNotContain("UpdateUnifiedGlobalScript", catalog.Methods.Select(p => p.Key));
            Assert.DoesNotContain("ReadUnifiedScreenBranch", catalog.Methods.Select(p => p.Key));
            Assert.All(catalog.Methods, entry => Assert.Equal(typeof(CallToolResult), entry.Value.ReturnType));
            Assert.Equal(typeof(BranchStep[]), Method("GetUnifiedScreenBranch").GetParameters().Single(p => p.Name == "branch").ParameterType);
            Assert.Equal(typeof(PropertyStep[]), Method("ManageUnifiedLoggingTag").GetParameters().Single(p => p.Name == "tagPath").ParameterType);
            Assert.Equal(typeof(PropertyStep[]), Method("SetUnifiedLogDuration").GetParameters().Single(p => p.Name == "durationPath").ParameterType);
            Assert.Equal(typeof(PropertyStep[]), Method("GetUnifiedObjectProperties").GetParameters().Single(p => p.Name == "objectPath").ParameterType);
            Assert.Equal(typeof(GraphicSelectionPage[]), Method("CompareUnifiedGraphicSelections").GetParameters()[0].ParameterType);
            Assert.Equal(typeof(NativeValue[]), Method("InvokeObject").GetParameters().Single(p => p.Name == "args").ParameterType);
            Assert.Equal(typeof(AttributeMap<Scalar>), Method("ManageUnifiedEvent").GetParameters().Single(p => p.Name == "scriptProperties").ParameterType);
        }
        [Fact]
        public void GeneratedExamplesMatchRegisteredTypedInputs()
        {
            foreach (var entry in catalog.Methods.Where(p => p.Value.ReturnType == typeof(CallToolResult)))
            {
                var result = Body(new ToolUsageTools().GetToolUsage(toolName: entry.Key));
                Assert.True((bool)result["ok"]!, entry.Key);
                var data = result["data"]!;
                var args = JsonSerializer.SerializeToElement(data["example"]!["request"]!["params"]!["arguments"]);
                var error = McpServer.ValidateV4Arguments(entry.Value, args, JsonSerializer.SerializeToElement(data["inputSchema"]));
                Assert.True(error == null, entry.Key + ": " + (error == null ? "" : V4Json.Serialize(error)));
            }
            Assert.Equal(0, objects.Calls + engineering.Calls + reflection.Calls);
        }
    }
}

namespace TiaMcpServer.ModelContextProtocol
{
    // Response boundary stand-ins; native reflection is checked on the actual EXE.
    public class ResponseObjectDescribe : ResponseMessage
    {
        public string? ObjectKind { get; set; }
        public string? ObjectPath { get; set; }
        public string? TypeName { get; set; }
        public IEnumerable<object>? Members { get; set; }
    }
    public class ResponseObjectValue : ResponseMessage
    {
        public string? ObjectKind { get; set; }
        public string? ObjectPath { get; set; }
        public string? ValueType { get; set; }
        public object? Value { get; set; }
    }
    public class ResponseObjectChildren : ResponseMessage
    {
        public string? ObjectKind { get; set; }
        public string? ObjectPath { get; set; }
        public string? Collection { get; set; }
        public IEnumerable<string>? Items { get; set; }
    }
}

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class ReflectionService
    {
        internal int Calls;
        internal string? LastInput;
        public ResponseObjectDescribe DescribeObject(string objectKind, string objectPath, string softwarePath = "", int maxMembers = 200)
        { Calls++;  return new ResponseObjectDescribe { Meta = ResponseMeta.Unstamped(true) }; }
        public ResponseObjectDescribe DescribeObjectProperty(string objectKind, string objectPath, string propertyPath, string softwarePath = "", int maxMembers = 200)
        { Calls++;  return new ResponseObjectDescribe { Meta = ResponseMeta.Unstamped(true) }; }
        public ResponseObjectValue GetObjectProperty(string objectKind, string objectPath, string propertyPath, string softwarePath = "")
        { Calls++;  return new ResponseObjectValue { Meta = ResponseMeta.Unstamped(true) }; }
        public ResponseObjectChildren ListObjectChildren(string objectKind, string objectPath, string collectionProperty, string softwarePath = "", int limit = 200)
        { Calls++;  return new ResponseObjectChildren { Meta = ResponseMeta.Unstamped(true) }; }
        public ResponseObjectValue InvokeObject(string objectKind, string objectPath, string methodName, JsonArray? args = null, string softwarePath = "", bool allowWrite = false)
        { Calls++;  return new ResponseObjectValue { Meta = ResponseMeta.Unstamped(true) }; }
        public ResponseObjectDescribe DescribeService(string objectKind, string objectPath, string serviceTypeSuffix, string softwarePath = "", int maxMembers = 200)
        { Calls++;  return new ResponseObjectDescribe { Meta = ResponseMeta.Unstamped(true) }; }
        public ResponseObjectValue InvokeService(string objectKind, string objectPath, string serviceTypeSuffix, string methodName, JsonArray? args = null, string softwarePath = "", bool allowWrite = false)
        { Calls++;  return new ResponseObjectValue { Meta = ResponseMeta.Unstamped(true) }; }
    }
    internal sealed class UnifiedEngineeringService
    {
        internal int Calls;
        internal string? LastInput;
        public ResponseMessage ImportUnifiedEngineeringList(string softwarePath, string category, string filePath, string expectedNamesJson, bool dryRun = true)
        { Calls++; LastInput = expectedNamesJson; return new ResponseMessage { Meta = ResponseMeta.Unstamped(true) }; }
        public ResponseMessage ReadUnifiedEngineeringObjects(string softwarePath, string category, string name = "", int offset = 0, int limit = 100)
        { Calls++;  return new ResponseMessage { Meta = ResponseMeta.Unstamped(true) }; }
        public ResponseMessage ManageUnifiedEngineeringObject(string softwarePath, string category, string name, string action,
            string propertiesJson = "{}", bool dryRun = true)
        { Calls++; LastInput = propertiesJson; return new ResponseMessage { Meta = ResponseMeta.Unstamped(true) }; }
    }
    internal sealed class UnifiedEventsService
    {
        internal int Calls;
        internal string? LastInput;
        public ResponseMessage ManageUnifiedEvent(string softwarePath,string objectPathJson,string eventType,string action="read",string propertyName="",string scriptPropertiesJson="{}",string expectedToken="",bool dryRun=true)
        { Calls++; LastInput = scriptPropertiesJson; return new ResponseMessage { Meta = ResponseMeta.Unstamped(true) }; }
    }
    internal sealed class UnifiedObjectServicesService
    {
        internal int Calls;
        internal string? LastInput;
        internal string? LastPath;
        public ResponseMessage ReadUnifiedObjectProperties(string softwarePath, string objectPathJson = "[]", int offset = 0, int limit = 100)
        { Calls++; LastInput = objectPathJson; return new ResponseMessage { Meta = ResponseMeta.Unstamped(true) }; }
        public ResponseMessage UpdateUnifiedObjectProperties(string softwarePath, string objectPathJson, string propertiesJson, bool dryRun = true)
        { Calls++; LastInput = propertiesJson; return new ResponseMessage { Meta = ResponseMeta.Unstamped(true) }; }
        public ResponseMessage UpdateUnifiedMultilingualProperty(string softwarePath, string objectPathJson, string property, string culture, string rawText, bool dryRun = true)
        { Calls++; LastInput = objectPathJson; return new ResponseMessage { Meta = ResponseMeta.Unstamped(true) }; }
        public ResponseMessage ValidateUnifiedObject(string softwarePath, string objectPathJson)
        { Calls++; LastInput = objectPathJson; return new ResponseMessage { Meta = ResponseMeta.Unstamped(true) }; }
        public ResponseMessage GetUnifiedCrossReferences(string softwarePath, string objectPathJson, string filter = "AllObjects")
        { Calls++; LastInput = objectPathJson; return new ResponseMessage { Meta = ResponseMeta.Unstamped(true) }; }
        public ResponseMessage ExportUnifiedEngineeringList(string softwarePath, string category, string name, string destinationDirectory, bool dryRun = true)
        { Calls++;  return new ResponseMessage { Meta = ResponseMeta.Unstamped(true) }; }
        public ResponseMessage ManageUnifiedLoggingTag(string softwarePath, string tagPathJson, string action = "read", string name = "", string propertiesJson = "{}", bool dryRun = true)
        { Calls++; LastPath = tagPathJson; LastInput = propertiesJson; return new ResponseMessage { Meta = ResponseMeta.Unstamped(true) }; }
        public ResponseMessage SetUnifiedLogDuration(string softwarePath, string durationPathJson, string kind, uint days, uint hours, uint minutes, uint seconds, uint hundredNanoseconds, bool dryRun = true)
        { Calls++; LastPath = LastInput = durationPathJson; return new ResponseMessage { Meta = ResponseMeta.Unstamped(true) }; }
        public ResponseMessage ManageUnifiedOpcUaAlarmType(string softwarePath, string name, string nodeId, string connection, string action = "create", bool dryRun = true)
        { Calls++;  return new ResponseMessage { Meta = ResponseMeta.Unstamped(true) }; }
        public ResponseMessage ReadUnifiedPlantObject(string plantPath="", string objectPathJson="[]", int offset=0, int limit=100)
        { Calls++; LastInput = objectPathJson; return new ResponseMessage { Meta = ResponseMeta.Unstamped(true) }; }
        public ResponseMessage ManageUnifiedPlantNode(string plantPath, string action, string plantObjectType="", string propertiesJson="{}", bool dryRun=true)
        { Calls++; LastInput = propertiesJson; return new ResponseMessage { Meta = ResponseMeta.Unstamped(true) }; }
        public ResponseMessage UpdateUnifiedPlantObject(string plantPath, string objectPathJson, string propertiesJson, bool dryRun=true)
        { Calls++; LastInput = propertiesJson; return new ResponseMessage { Meta = ResponseMeta.Unstamped(true) }; }
    }
}
