using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Construction;
using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Hmi;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class TypedToolBindingTests : IDisposable
    {
        private static int calls;
        // The fixture uses names whose generated envelopeVersion marker is 4.
        // It traverses the production catalog, profile/schema and SDK binder.
        internal sealed class Required<T>
        {
            [McpServerTool(Name = "FindTools"), Description("[READ] Typed admission fixture.")]
            public static CallToolResult Read(T input) { calls++; return McpServer.V4Result("FindTools", new JsonObject()); }
        }
        internal sealed class Optional<T>
        {
            [McpServerTool(Name = "ListToolCategories"), Description("[READ] Optional typed admission fixture.")]
            public static CallToolResult Read(T input = default!) { calls++; return McpServer.V4Result("ListToolCategories", new JsonObject()); }
        }

        public void Dispose() => ToolBridgeFixture.Configure();
        private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
        private static RequestContext<CallToolRequestParams> Request(string name, string text) =>
            new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ToolCatalogServerProxy>())
            { Params = new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text) } };
        private static JsonObject Body(CallToolResult result) => McpServer.ResultBody(result)!.AsObject();
        private static List<McpServerTool> Register(Type type)
        {
            calls = 0;
            var catalog = new ToolCatalog(new[] { typeof(Required<>).MakeGenericType(type), typeof(Optional<>).MakeGenericType(type) });
            McpServer.ConfigureToolBridge(catalog, () => false, new HashSet<string>());
            return catalog.Methods.Select(p => (McpServerTool)new VersionPolicyTool(McpServer.CreateTool(p.Key, p.Value))).ToList();
        }
        public static IEnumerable<object[]> Families()
        {
            yield return new object[] { typeof(AttributeMap<Scalar>), "{\"ExactName\":1}" };
            yield return new object[] { typeof(UdtSpec), "{\"name\":\"U\",\"members\":[{\"name\":\"Value\",\"datatype\":\"Bool\"}]}" };
            foreach (var row in V4HmiContractTests.Roots()) yield return row;
            yield return new object[] { typeof(MonitoringOptions), "{}" };
            yield return new object[] { typeof(Artifact[]), "[{\"id\":\"A\"}]" };
            yield return new object[] { typeof(LintRules), "{}" };
        }

        [Theory, MemberData(nameof(Families))]
        public async Task RealToolListUsesFamilySchemasForRequiredAndOptionalParameters(Type type, string valid)
        {
            var tools = Register(type);
            var expected = McpServer.InlineSchema(JsonNode.Parse(TypedToolInput.For(type)!.Schema.GetRawText())!.AsObject());
            var definitions = expected["$defs"]?.DeepClone();
            expected.Remove("$defs");
            foreach (var tool in tools)
            {
                var schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
                Assert.True(JsonNode.DeepEquals(expected, schema["properties"]!["input"]), schema.ToJsonString());
                Assert.True(JsonNode.DeepEquals(definitions, schema["$defs"]));
                Assert.Equal(tool.ProtocolTool.Name == "FindTools", schema["required"]?.AsArray().Any(n => (string?)n == "input") == true);
                var result = await tool.InvokeAsync(Request(tool.ProtocolTool.Name, "{\"input\":" + valid + "}"));
                Assert.Null(Body(result)["error"]);
            }
            var optional = tools.Single(t => t.ProtocolTool.Name == "ListToolCategories");
            Assert.Null(Body(await optional.InvokeAsync(Request("ListToolCategories", "{}")))["error"]);
            Assert.Equal(3, calls);
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"Input\":{\"id\":\"A\"}}")]
        [InlineData("{\"input\":null}")]
        [InlineData("{\"input\":\"{\\\"id\\\":\\\"A\\\"}\"}")]
        [InlineData("{\"input\":{\"Id\":\"A\"}}")]
        [InlineData("{\"input\":{\"id\":\"A\",\"unknown\":1}}")]
        [InlineData("{\"input\":{\"id\":\"A\"},\"extra\":true}")]
        public async Task DirectBridgeAndBatchRejectBeforeTheBody(string json)
        {
            var tool = Register(typeof(Artifact)).Single(t => t.ProtocolTool.Name == "FindTools");
            var direct = Body(await tool.InvokeAsync(Request("FindTools", json)));
            var bridged = Body(McpServer.CallTool("FindTools", new ToolArguments(Json(json))));
            var batch = Body(McpServer.ReadToolBatch(new[] { new ToolCall("FindTools", new ToolArguments(Json(json))) }));
            foreach (var result in new[] { direct, bridged, batch })
            {
                Assert.Equal("INVALID_ARGUMENT", (string?)result["error"]!["code"]);
                Assert.Equal("rejected-before-operation", (string?)result["meta"]!["outcome"]);
                Assert.Equal("not-started", (string?)result["meta"]!["execution"]);
                Assert.True(JsonNode.DeepEquals(direct["error"], result["error"]));
            }
            Assert.Equal(0, calls);
        }

        [Fact]
        public async Task DuplicateFieldsAndFamilyBudgetsKeepV4Details()
        {
            var tool = Register(typeof(Artifact[])).Single(t => t.ProtocolTool.Name == "FindTools");
            var duplicate = Body(await tool.InvokeAsync(Request("FindTools", "{\"input\":[{\"id\":\"secret\",\"id\":\"other\"}]}")));
            Assert.Equal("INVALID_ARGUMENT", (string?)duplicate["error"]!["code"]);
            Assert.Equal("input", (string?)duplicate["error"]!["details"]!["parameter"]);
            string json = "{\"input\":[" + string.Join(",", Enumerable.Range(0, 257).Select(i => "{\"id\":\"a" + i + "\"}")) + "]}";
            var direct = Body(await tool.InvokeAsync(Request("FindTools", json)));
            var bridged = Body(McpServer.CallTool("FindTools", new ToolArguments(Json(json))));
            var batch = Body(McpServer.ReadToolBatch(new[] { new ToolCall("FindTools", new ToolArguments(Json(json))) }));
            foreach (var result in new[] { direct, bridged, batch })
            {
                Assert.Equal("LIMIT_EXCEEDED", (string?)result["error"]!["code"]);
                Assert.Equal("input", (string?)result["error"]!["details"]!["parameter"]);
                Assert.Equal(256, (int?)result["error"]!["details"]!["limit"]);
                Assert.Equal(257, (int?)result["error"]!["details"]!["actual"]);
            }
            Assert.Equal(0, calls);
        }

        [Fact]
        public void RecursiveTypeReferencesStayLocalToTheToolRoot()
        {
            var tools = Register(typeof(AttributeMap<NativeValue>));
            foreach (var tool in tools)
            {
                var schema = tool.ProtocolTool.InputSchema;
                Assert.True(schema.TryGetProperty("$defs", out _));
                Assert.Null(new InputSchema(schema).Validate(Json("{\"input\":{\"a\":[{\"b\":true}]}}"), "arguments"));
            }
        }

        [Fact]
        public async Task NestedDuplicateFieldsAreRejectedBySdkBridgeAndBatchBeforeBinding()
        {
            var directTool = Register(typeof(Artifact)).Single(t => t.ProtocolTool.Name == "FindTools");
            const string arguments = "{\"input\":{\"id\":\"secret\",\"id\":\"other\"}}";
            var direct = Body(await directTool.InvokeAsync(Request("FindTools", arguments)));
            foreach (string name in new[] { "CallTool", "RunReadOnlyToolBatch", "PreviewToolBatch" })
            {
                var method = typeof(McpServer).GetMethods().Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == name);
                var tool = McpServer.CreateTool(name, method);
                string call = "{\"name\":\"FindTools\",\"arguments\":" + arguments + "}";
                var result = Body(await tool.InvokeAsync(Request(name, name == "CallTool" ? call
                    : "{\"operations\":[" + call + "],\"expectedProject\":\"Fixture\"}")));
                Assert.True(JsonNode.DeepEquals(direct["error"], result["error"]));
                Assert.Equal("not-started", (string?)result["meta"]!["execution"]);
            }
            Assert.Equal(0, calls);
        }
    }
}
