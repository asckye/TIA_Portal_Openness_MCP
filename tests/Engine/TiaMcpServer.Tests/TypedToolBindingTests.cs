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
        private const string InputDescription = "The typed input supplied to the fixture.";
        // The fixture uses names whose generated envelopeVersion marker is 4.
        // It traverses the production catalog, profile/schema and SDK binder.
        internal sealed class Required<T>
        {
            [McpServerTool(Name = "FindTools"), Description("[READ] Typed admission fixture.")]
            public static CallToolResult Read([Description(InputDescription)] T input) { calls++; return McpServer.V4Result("FindTools", new JsonObject()); }
        }
        internal sealed class Optional<T>
        {
            [McpServerTool(Name = "ListToolCategories"), Description("[READ] Optional typed admission fixture.")]
            public static CallToolResult Read([Description(InputDescription)] T input = default!) { calls++; return McpServer.V4Result("ListToolCategories", new JsonObject { ["omitted"] = input == null }); }
        }

        internal sealed class ArtifactUnion
        {
            [McpServerTool(Name = "FindTools"), Description("[READ] Outer-kind union fixture.")]
            public static CallToolResult Read(string kind, [Description(InputDescription)] PlcArtifactSpec spec)
            {
                calls++;
                try
                {
                    var resolved = spec.Resolve(kind);
                    return McpServer.V4Result("FindTools", new JsonObject { ["member"] = resolved.GetType().Name,
                        ["spec"] = JsonNode.Parse(V4Json.Serialize(resolved)) });
                }
                catch (ArgumentException) { return McpServer.V4Reject("FindTools", McpServer.InvalidInput("kind")); }
            }
        }

        internal sealed class SdkDefaults
        {
            [McpServerTool(Name = "FindTools"), Description("[READ] SDK default fixture.")]
            public static CallToolResult Read(string[] paths = null!, string label = null!, int? count = null, int limit = 7)
            {
                calls++;
                return McpServer.V4Result("FindTools", JsonSerializer.SerializeToNode(new { paths, label, count, limit })!.AsObject());
            }

        }

        public void Dispose() => ToolBridgeFixture.Configure();
        private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
        private static RequestContext<CallToolRequestParams> Request(string name, string text) =>
            new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ToolCatalogServerProxy>())
            { Params = new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text) } };
        private static JsonObject Body(CallToolResult result) => McpServer.ResultBody(result)!.AsObject();
        private static List<McpServerTool> Register(Type type)
            => RegisterTools(typeof(Required<>).MakeGenericType(type), typeof(Optional<>).MakeGenericType(type));
        private static List<McpServerTool> RegisterTools(params Type[] types)
        {
            calls = 0;
            var catalog = new ToolCatalog(types);
            McpServer.ConfigureToolBridge(catalog, () => false, new HashSet<string>());
            return catalog.Methods.Select(p => (McpServerTool)new VersionPolicyTool(McpServer.CreateTool(p.Key, p.Value))).ToList();
        }
        public static IEnumerable<object[]> Families()
        {
            yield return new object[] { typeof(AttributeMap<Scalar>), "{\"ExactName\":1}" };
            yield return new object[] { typeof(UdtSpec), "{\"name\":\"U\",\"members\":[{\"name\":\"Value\",\"datatype\":\"Bool\"}]}" };
            yield return new object[] { typeof(PlcArtifactSpec), "{\"members\":[]}" };
            foreach (var row in V4HmiContractTests.Roots()) yield return row;
            yield return new object[] { typeof(MonitoringOptions), "{}" };
            yield return new object[] { typeof(Artifact[]), "[{\"id\":\"A\"}]" };
            yield return new object[] { typeof(LintRules), "{}" };
            yield return new object[] { typeof(CompositeAttributeMap), "{\"Width\":10,\"Font\":{\"Size\":12,\"Bold\":true}}" };
            yield return new object[] { typeof(WriteValue[]), "[{\"name\":\"DB1.Run\",\"value\":true}]" };
        }

        [Theory, MemberData(nameof(Families))]
        public async Task RealToolListUsesFamilySchemasForRequiredAndOptionalParameters(Type type, string valid)
        {
            var tools = Register(type);
            var expected = McpServer.InlineSchema(JsonNode.Parse(TypedToolInput.For(type)!.Schema.GetRawText())!.AsObject());
            var definitions = expected["$defs"]?.DeepClone();
            expected.Remove("$defs");
            expected["description"] = InputDescription;
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
            var omitted = Body(await optional.InvokeAsync(Request("ListToolCategories", "{}")));
            Assert.Null(omitted["error"]);
            Assert.True((bool)omitted["data"]!["omitted"]!);
            Assert.Equal(3, calls);
        }

        [Fact]
        public async Task CompositeAttributeMapLeafLimitIsCheckedBeforeBinding()
        {
            var tool = Register(typeof(CompositeAttributeMap)).Single(t => t.ProtocolTool.Name == "FindTools");
            string parts = string.Join(",", Enumerable.Range(0, 26).Select(i => "\"P" + i + "\":{\"A\":1,\"B\":2}"));
            var result = Body(await tool.InvokeAsync(Request("FindTools", "{\"input\":{" + parts + "}}")));
            Assert.Equal("LIMIT_EXCEEDED", (string?)result["error"]!["code"]);
            Assert.Equal("input", (string?)result["error"]!["details"]!["parameter"]);
            Assert.Equal(0, calls);
        }

        [Theory]
        [InlineData("[{\"name\":\"A\",\"value\":1,\"extra\":1}]")]
        [InlineData("[{\"name\":\"A\",\"value\":1},{\"name\":\"A\",\"value\":2}]")]
        [InlineData("[{\"name\":\"A\",\"value\":{\"nested\":1}}]")]
        [InlineData("[]")]
        public async Task WriteValueRowsAreClosedBeforeBinding(string writes)
        {
            var tool = Register(typeof(WriteValue[])).Single(t => t.ProtocolTool.Name == "FindTools");
            var result = Body(await tool.InvokeAsync(Request("FindTools", "{\"input\":" + writes + "}")));
            Assert.Equal("INVALID_ARGUMENT", (string?)result["error"]!["code"]);
            Assert.Equal(0, calls);
        }

        public static IEnumerable<object[]> UnionMembers()
        {
            yield return new object[] { "udt", "{\"members\":[]}", typeof(UdtSpec) };
            yield return new object[] { "tagtable", "{\"tableName\":\"T\",\"tags\":[]}", typeof(PlcTagTableSpec) };
            yield return new object[] { "globaldb", "{\"dbName\":\"D\",\"dbNumber\":1,\"staticMembers\":[]}", typeof(GlobalDbSpec) };
            const string overlapping = "{\"blockName\":\"B\",\"blockNumber\":1,\"inputs\":[],\"outputs\":[],\"structuredText\":{\"operations\":[]}}";
            yield return new object[] { "fc", overlapping, typeof(FcBlockSpec) };
            yield return new object[] { "fb", overlapping, typeof(FbBlockSpec) };
        }

        [Theory, MemberData(nameof(UnionMembers))]
        public async Task OuterKindResolvesTheBoundUnionOnDirectBridgeAndBatch(string kind, string spec, Type member)
        {
            var tool = RegisterTools(typeof(ArtifactUnion)).Single();
            var schema = tool.ProtocolTool.InputSchema;
            Assert.DoesNotContain("\"$ref\"", schema.GetRawText());
            var union = schema.GetProperty("properties").GetProperty("spec");
            Assert.Equal(InputDescription, union.GetProperty("description").GetString());
            Assert.Equal(5, union.GetProperty("anyOf").GetArrayLength());
            Assert.Equal(new[] { typeof(UdtSpec), typeof(PlcTagTableSpec), typeof(GlobalDbSpec), typeof(FcBlockSpec), typeof(FbBlockSpec) }
                .Select(t => TypedToolInput.For(t)!.Schema.GetRawText()), union.GetProperty("anyOf").EnumerateArray().Select(s => s.GetRawText()));
            string json = "{\"kind\":\"" + kind + "\",\"spec\":" + spec + "}";
            Assert.Null(McpServer.BindV4Call("FindTools", new ToolArguments(Json(json)), out _, out var bound));
            var value = Assert.IsAssignableFrom<PlcArtifactSpec>(bound![1]);
            Assert.False(value is UdtSpec || value is PlcTagTableSpec || value is GlobalDbSpec || value is FcBlockSpec || value is FbBlockSpec);
            Assert.IsType(member, value.Resolve(kind));
            Assert.Equal(spec, V4Json.Serialize<PlcArtifactSpec>(value));
            var direct = Body(await tool.InvokeAsync(Request("FindTools", json)));
            var bridged = Body(McpServer.CallTool("FindTools", new ToolArguments(Json(json))));
            var batch = Body(McpServer.ReadToolBatch(new[] { new ToolCall("FindTools", new ToolArguments(Json(json))) }));
            foreach (var result in new[] { direct, bridged, batch["data"]!["items"]![0]!["result"]! })
            {
                Assert.True((bool)result["ok"]!);
                Assert.Equal(member.Name, (string?)result["data"]!["member"]);
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(spec), result["data"]!["spec"]));
            }
            Assert.Equal(3, calls);
        }

        [Theory]
        [InlineData("udt", "{\"tableName\":\"T\",\"tags\":[]}")]
        [InlineData("unsupported", "{\"members\":[]}")]
        public async Task KindMemberMismatchIsCheckedOnlyByTheTool(string kind, string spec)
        {
            var tool = RegisterTools(typeof(ArtifactUnion)).Single();
            string json = "{\"kind\":\"" + kind + "\",\"spec\":" + spec + "}";
            Assert.Null(McpServer.BindV4Call("FindTools", new ToolArguments(Json(json)), out _, out _));
            var direct = Body(await tool.InvokeAsync(Request("FindTools", json)));
            var bridge = Body(McpServer.CallTool("FindTools", new ToolArguments(Json(json))));
            var batch = Body(McpServer.ReadToolBatch(new[] { new ToolCall("FindTools", new ToolArguments(Json(json))) }));
            foreach (var result in new[] { direct, bridge, batch["data"]!["items"]![0]!["result"]! })
                Assert.Equal("kind", (string?)result["error"]!["details"]!["parameter"]);
            Assert.Equal(3, calls);
        }

        public static IEnumerable<object[]> InvalidUnionValues()
        {
            foreach (string spec in new[] { "null", "{}", "[]", "\"{\\\"members\\\":[]}\"", "{\"Members\":[]}",
                "{\"members\":[],\"unknown\":true}", "{\"members\":null}",
                "{\"members\":[{\"name\":\"x\",\"datatype\":\"Bool\",\"startValue\":\"1\"}]}", "{\"members\":[],\"name\":\"secret\\u0000\"}" })
                yield return new object[] { spec, "INVALID_ARGUMENT", 0, 0 };
            yield return new object[] { "{\"members\":[],\"name\":\"" + new string('x', 4097) + "\"}", "LIMIT_EXCEEDED", 4096, 4097 };
            yield return new object[] { "{\"members\":[" + string.Join(",", Enumerable.Repeat("{\"name\":\"x\",\"datatype\":\"Bool\"}", 1001)) + "]}", "LIMIT_EXCEEDED", 1000, 1001 };
            yield return new object[] { new string('[', 17) + "0" + new string(']', 17), "LIMIT_EXCEEDED", 16, 17 };
        }

        [Theory, MemberData(nameof(InvalidUnionValues))]
        public async Task UnionRejectionsKeepFamilyDetailsBeforeDispatch(string spec, string code, int limit, int actual)
        {
            var tool = RegisterTools(typeof(ArtifactUnion)).Single();
            string json = "{\"kind\":\"udt\",\"spec\":" + spec + "}";
            var direct = Body(await tool.InvokeAsync(Request("FindTools", json)));
            var bridge = Body(McpServer.CallTool("FindTools", new ToolArguments(Json(json))));
            var batch = Body(McpServer.ReadToolBatch(new[] { new ToolCall("FindTools", new ToolArguments(Json(json))) }));
            var results = new List<JsonObject> { direct, bridge, batch };
            foreach (string name in new[] { "CallTool", "RunReadOnlyToolBatch", "PreviewToolBatch" })
            {
                var method = typeof(McpServer).GetMethods().Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == name);
                var wrapper = McpServer.CreateTool(name, method);
                string call = "{\"name\":\"FindTools\",\"arguments\":" + json + "}";
                results.Add(Body(await wrapper.InvokeAsync(Request(name, name == "CallTool" ? call : "{\"operations\":[" + call + "]}"))));
            }
            foreach (var result in results)
            {
                Assert.Equal(code, (string?)result["error"]!["code"]);
                Assert.Equal("spec", (string?)result["error"]!["details"]!["parameter"]);
                Assert.True(JsonNode.DeepEquals(direct["error"], result["error"]));
                Assert.Equal("rejected-before-operation", (string?)result["meta"]!["outcome"]);
                Assert.Equal("not-started", (string?)result["meta"]!["execution"]);
                if (limit > 0)
                {
                    Assert.Equal(limit, (int?)result["error"]!["details"]!["limit"]);
                    Assert.Equal(actual, (int?)result["error"]!["details"]!["actual"]);
                }
            }
            Assert.Equal(0, calls);
        }

        [Fact]
        public async Task SdkInferredV4ParametersOmitNullDefaultsButKeepDeclaredBindingDefaults()
        {
            var tools = RegisterTools(typeof(SdkDefaults));
            var method = typeof(SdkDefaults).GetMethod("Read")!;
            var tool = tools.Single(t => t.ProtocolTool.Name == "FindTools");
            foreach (var schema in new[] { tool.ProtocolTool.InputSchema, ToolCatalog.CreateTool(method).ProtocolTool.InputSchema,
                McpServer.ToolInputSchema("FindTools", method) })
            {
                foreach (string name in new[] { "paths", "label", "count" })
                    Assert.False(schema.GetProperty("properties").GetProperty(name).TryGetProperty("default", out _));
                Assert.Equal(7, schema.GetProperty("properties").GetProperty("limit").GetProperty("default").GetInt32());
            }
            Assert.Null(McpServer.BindV4Call("FindTools", McpServer.EmptyArguments(), out _, out var bound));
            Assert.Equal(new object?[] { null, null, null, 7 }, bound);
            var direct = Body(await tool.InvokeAsync(Request("FindTools", "{}")));
            var bridge = Body(McpServer.CallTool("FindTools", McpServer.EmptyArguments()));
            var batch = Body(McpServer.ReadToolBatch(new[] { new ToolCall("FindTools", McpServer.EmptyArguments()) }));
            foreach (var result in new[] { direct, bridge, batch["data"]!["items"]![0]!["result"]! })
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"paths\":null,\"label\":null,\"count\":null,\"limit\":7}"), result["data"]));
            Assert.Equal(3, calls);
            foreach (string json in new[] { "{\"paths\":null}", "{\"label\":null}", "{\"limit\":null}" })
            {
                Assert.Equal("INVALID_ARGUMENT", (string?)Body(await tool.InvokeAsync(Request("FindTools", json)))["error"]!["code"]);
                Assert.Equal("INVALID_ARGUMENT", (string?)Body(McpServer.CallTool("FindTools", new ToolArguments(Json(json))))["error"]!["code"]);
                Assert.Equal("INVALID_ARGUMENT", (string?)Body(McpServer.ReadToolBatch(new[] { new ToolCall("FindTools", new ToolArguments(Json(json))) }))["error"]!["code"]);
            }
            Assert.Equal(3, calls);
            Assert.True((bool)Body(await tool.InvokeAsync(Request("FindTools", "{\"count\":null}")))["ok"]!);
        }

        [Fact]
        public void V4NullDefaultCleanupVisitsSchemasAndPreservesExampleData()
        {
            const string json = "{\"properties\":{\"input\":{\"default\":null,\"anyOf\":[{\"type\":\"array\",\"items\":{\"type\":\"string\",\"default\":null}}],\"examples\":[{\"default\":null}]}},\"$defs\":{\"child\":{\"type\":\"string\",\"default\":null}}}";
            var schema = JsonNode.Parse(json)!.AsObject();
            McpServer.RemoveV4NullDefaults(schema);
            Assert.False(schema["properties"]!["input"]!.AsObject().ContainsKey("default"));
            Assert.False(schema["properties"]!["input"]!["anyOf"]![0]!["items"]!.AsObject().ContainsKey("default"));
            Assert.False(schema["$defs"]!["child"]!.AsObject().ContainsKey("default"));
            Assert.True(schema["properties"]!["input"]!["examples"]![0]!.AsObject().ContainsKey("default"));
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

        [Fact]
        public async Task UnionDuplicateFieldsAreRejectedAtEveryWireBoundary()
        {
            var tool = RegisterTools(typeof(ArtifactUnion)).Single();
            const string arguments = "{\"kind\":\"udt\",\"spec\":{\"members\":[],\"members\":[]}}";
            Assert.Throws<InputRejection>(() => new ToolArguments(Json(arguments)));
            var direct = Body(await tool.InvokeAsync(Request("FindTools", arguments)));
            Assert.Equal("INVALID_ARGUMENT", (string?)direct["error"]!["code"]);
            Assert.Equal("spec", (string?)direct["error"]!["details"]!["parameter"]);
            foreach (string name in new[] { "CallTool", "RunReadOnlyToolBatch", "PreviewToolBatch" })
            {
                var method = typeof(McpServer).GetMethods().Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == name);
                var wrapper = McpServer.CreateTool(name, method);
                string call = "{\"name\":\"FindTools\",\"arguments\":" + arguments + "}";
                var result = Body(await wrapper.InvokeAsync(Request(name, name == "CallTool" ? call : "{\"operations\":[" + call + "]}")));
                Assert.True(JsonNode.DeepEquals(direct["error"], result["error"]));
                Assert.Equal("not-started", (string?)result["meta"]!["execution"]);
            }
            Assert.Equal(0, calls);
        }
    }
}
