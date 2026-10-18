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

namespace TiaMcp.Engine.Tests
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

        public static IEnumerable<object[]> UnionMembers()
        {
            yield return new object[] { "udt", "{\"members\":[]}", typeof(UdtSpec) };
            yield return new object[] { "tagtable", "{\"tableName\":\"T\",\"tags\":[]}", typeof(PlcTagTableSpec) };
            yield return new object[] { "globaldb", "{\"dbName\":\"D\",\"dbNumber\":1,\"staticMembers\":[]}", typeof(GlobalDbSpec) };
            const string overlapping = "{\"blockName\":\"B\",\"blockNumber\":1,\"inputs\":[],\"outputs\":[],\"structuredText\":{\"operations\":[]}}";
            yield return new object[] { "fc", overlapping, typeof(FcBlockSpec) };
            yield return new object[] { "fb", overlapping, typeof(FbBlockSpec) };
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
    }
}
