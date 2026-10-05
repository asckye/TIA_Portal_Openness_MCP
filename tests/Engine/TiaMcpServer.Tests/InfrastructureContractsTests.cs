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
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcpServer.Siemens
{
    internal sealed partial class Portal
    {
        internal void EnsureBoundProjectUnchanged(string operation) { }
        internal JsonObject GetPortalProcessHealth() => new JsonObject { ["boundProcessId"] = 1, ["processAlive"] = true };
        internal JsonObject GetBindingIdentity() => JsonNode.Parse("{\"identity\":{\"tiaMajorVersion\":21,\"processId\":1,\"processStartUtc\":\"2026-01-01T00:00:00Z\",\"projectPath\":\"fixture.ap21\",\"projectName\":\"Fixture\",\"generation\":1}}")!.AsObject();
    }
}

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer { }
    internal sealed class SessionTools
    {
        internal (bool IsConnected, string Project) GetState() => (true, "Fixture");
    }
}

namespace TiaMcpServer.Tests
{
    public sealed class InfrastructureContractsTests : IDisposable
    {
        public sealed class BatchProbes
        {
            internal static readonly List<string> Calls = new List<string>();
            [McpServerTool(Name = "ReadFixture"), ToolClassification("L0", "Meta", "READ", batchRead: true), System.ComponentModel.Description("Read a fixture; arbitrary translated text.")]
            public static CallToolResult Read(bool success = true) => success
                ? McpServer.V4Result("ReadFixture", new JsonObject { ["summary"] = "fixture" })
                : McpServer.V4Result("ReadFixture", new JsonObject { ["summary"] = "fixture" },
                    new Error("Fixture read failed.", new InternalErrorDetails(null)), Outcome.ReadFailed, Execution.ReadOnly, Completeness.None);
            [McpServerTool(Name = "WriteFixture"), ToolClassification("L2", "Meta", "WRITE", batchWrite: true), System.ComponentModel.Description("Write a fixture; arbitrary translated text.")]
            public static CallToolResult Write(string target, bool dryRun = true, string verdict = "success")
            {
                Calls.Add(target + ":" + dryRun);
                var data = new JsonObject { ["target"] = target };
                if (dryRun || verdict == "success") return McpServer.V4Result("WriteFixture", data);
                if (verdict == "okOnly") return new CallToolResult { Content = new[] { new TextContentBlock { Text = "{\"ok\":true}" } } };
                bool unknown = verdict == "unknown";
                return McpServer.V4Result("WriteFixture", data, McpServer.InvalidInput("target"),
                    unknown ? Outcome.Unknown : Outcome.Failed, unknown ? Execution.Unknown : Execution.Completed, Completeness.None);
            }
        }

        public InfrastructureContractsTests()
        {
            BatchProbes.Calls.Clear();
            var catalog = new ToolCatalog(new[] { typeof(McpServer), typeof(ToolUsageTools), typeof(BatchProbes), typeof(ToolBridgeProbes), typeof(DcbVersionProbe) });
            McpServer.ConfigureToolBridge(catalog, () => true, new HashSet<string> { "CallTool" });
            EngineServices.SetServiceProvider(new ServiceCollection().AddEngine(false, catalog)
                .AddSingleton<Siemens.Portal>().AddSingleton<SessionTools>().BuildServiceProvider());
        }
        public void Dispose() => ToolBridgeFixture.Configure();
        private static ToolArguments Args(string json = "{}") => new ToolArguments(JsonSerializer.Deserialize<JsonElement>(json));
        private static JsonObject Body(CallToolResult result) => McpServer.ResultBody(result)!.AsObject();
        private static string? Code(CallToolResult result) => (string?)Body(result)["error"]?["code"];
        private static ToolCall Call(string name, string args = "{}") => new ToolCall(name, Args(args));
        private static RequestContext<CallToolRequestParams> Request(string name, string json)
            => new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ToolCatalogServerProxy>())
            { Params = new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } };

        [Fact]
        public async Task BridgePreservesTheSdkV4Result()
        {
            var tool = ToolCatalog.CreateTool(typeof(BatchProbes).GetMethod("Read")!);
            var direct = await tool.InvokeAsync(Request("ReadFixture", "{\"success\":false}"));
            var bridged = McpServer.CallTool("ReadFixture", Args("{\"success\":false}"));
            foreach (var result in new[] { direct, bridged })
            {
                Assert.Equal(4, (int)Body(result)["schemaVersion"]!);
                Assert.True(JsonNode.DeepEquals(Body(result), result.StructuredContent));
                Assert.True(result.IsError);
            }
            Assert.Equal(Body(direct)["data"]!.ToJsonString(), Body(bridged)["data"]!.ToJsonString());
            Assert.Equal(Body(direct)["error"]!.ToJsonString(), Body(bridged)["error"]!.ToJsonString());
            Assert.Equal((string?)Body(direct)["meta"]!["outcome"], (string?)Body(bridged)["meta"]!["outcome"]);
            Assert.Equal(direct.IsError, bridged.IsError);
        }

        [Fact]
        public void DirectBridgeAndBatchRetainTheSameV4Shape()
        {
            var direct = Body(McpServer.ListToolCategoriesV4());
            var bridge = Body(McpServer.CallTool("ListToolCategories", Args()));
            var batch = Body(McpServer.ReadToolBatch(new[] { Call("ListToolCategories") }));
            var child = batch["data"]!["items"]![0]!["result"]!.AsObject();
            foreach (var body in new[] { bridge, child })
            {
                Assert.Equal(direct["data"]!.ToJsonString(), body["data"]!.ToJsonString());
                Assert.Equal("ListToolCategories", (string?)body["meta"]!["tool"]);
                Assert.Equal(4, (int)body["schemaVersion"]!);
                Assert.Null(body["Message"]);
            }
        }

        [Theory]
        [InlineData("NoSuchTool", "{}", "TOOL_NOT_FOUND")]
        [InlineData("CallTool", "{\"name\":\"ReadFixture\"}", "INVALID_ARGUMENT")]
        [InlineData("ReadFixture", "{\"Success\":true}", "INVALID_ARGUMENT")]
        [InlineData("ReadFixture", "{\"success\":\"true\"}", "INVALID_ARGUMENT")]
        [InlineData("ReadFixture", "{\"unknown\":1}", "INVALID_ARGUMENT")]
        public void BridgeRejectsBeforeDispatch(string name, string args, string code)
        {
            var result = McpServer.CallTool(name, Args(args));
            Assert.Equal(code, Code(result));
            Assert.True(result.IsError);
            Assert.Equal("not-started", (string?)Body(result)["meta"]!["execution"]);
        }

        [Theory]
        [InlineData("{\"name\":\"ReadFixture\",\"arguments\":\"{}\"}")]
        [InlineData("{\"name\":\"ReadFixture\",\"arguments\":null}")]
        [InlineData("{\"name\":\"ReadFixture\",\"arguments\":[]}")]
        [InlineData("{\"name\":\"ReadFixture\",\"argumentsJson\":{}}")]
        public async Task AdvertisedBridgeRejectsNonObjectsAndOldParameter(string json)
        {
            var method = McpServer.AllToolMethods()["CallTool"];
            var tool = McpServer.WithSchemaHints(ToolCatalog.CreateTool(method), "CallTool", method);
            var result = await tool.InvokeAsync(Request("CallTool", json));
            Assert.Equal("INVALID_ARGUMENT", Code(result));
        }

        [Fact]
        public void BatchLimitsAllowlistsAndNestingRemainDistinct()
        {
            Assert.Equal("INVALID_ARGUMENT", Code(McpServer.ReadToolBatch(Array.Empty<ToolCall>())));
            Assert.Equal("LIMIT_EXCEEDED", Code(McpServer.ReadToolBatch(Enumerable.Repeat(Call("ReadFixture"), 51).ToArray())));
            Assert.Equal("INVALID_ARGUMENT", Code(McpServer.ReadToolBatch(new[] { Call("WriteFixture", "{\"target\":\"A\"}") })));
            Assert.Equal("INVALID_ARGUMENT", Code(McpServer.PreviewToolBatch(new[] { Call("ReadFixture") }, "Fixture")));
            Assert.Equal("INVALID_ARGUMENT", Code(McpServer.ReadToolBatch(new[] { Call("CallTool", "{\"name\":\"ReadFixture\"}") })));
            Assert.Empty(BatchProbes.Calls);
        }

        [Theory]
        [InlineData("failure", "partial")]
        [InlineData("unknown", "unknown")]
        [InlineData("okOnly", "unknown")]
        public void ApplyPreservesEarlierResultsStopsAndConsumesToken(string verdict, string outcome)
        {
            var preview = Body(McpServer.PreviewToolBatch(new[] { Call("WriteFixture", "{\"target\":\"A\"}"),
                Call("WriteFixture", "{\"target\":\"B\",\"verdict\":\"" + verdict + "\"}"), Call("WriteFixture", "{\"target\":\"C\"}") }, "Fixture"));
            Assert.True((bool)preview["ok"]!);
            var token = (string)preview["data"]!["token"]!;
            var applied = Body(McpServer.ApplyToolBatch(token));
            Assert.Equal(outcome, (string?)applied["meta"]!["outcome"]);
            Assert.Equal("NOT_EXECUTED", (string?)applied["data"]!["items"]![2]!["result"]!["error"]!["code"]);
            Assert.DoesNotContain("C:False", BatchProbes.Calls);
            Assert.Equal("NOT_FOUND", Code(McpServer.ApplyToolBatch(token)));
            Assert.Equal(new[] { "A:True", "B:True", "C:True", "A:True", "B:True", "C:True", "A:False", "B:False" }, BatchProbes.Calls);
        }

        [Fact]
        public void LiteMapHasSixtyDistinctCurrentNamesAndObjectExamplesPerRelease()
        {
            foreach (string release in new[] { "20", "21" })
            {
                var rows = ToolUsageCatalog.ProfileEntries(release).Where(r => r!["profiles"]!.AsArray().Any(p => (string?)p == "lite")).ToArray();
                Assert.Equal(60, rows.Length);
                Assert.Equal(60, rows.Select(r => (string?)r!["currentName"]).Distinct().Count());
                Assert.All(rows, row => Assert.IsType<JsonObject>(row!["arguments"]));
                Assert.Contains(rows, r => (string?)r!["currentName"] == "PreviewToolCall");
                Assert.Contains(rows, r => (string?)r!["name"] == "GetSessionState" && (string?)r!["currentName"] == "GetSessionState");
            }
            foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19" })
            {
                var rows = ToolUsageCatalog.ProfileEntries(release);
                Assert.Contains(rows, r => (string?)r!["name"] == "RenderPlcBlock");
                Assert.Contains(rows, r => (string?)r!["name"] == "RenderPlcProgramAtlas");
                Assert.All(rows, r => Assert.DoesNotContain(r!["profiles"]!.AsArray(), p => (string?)p == "lite"));
            }
        }

        [Fact]
        public void AllExportedFixtureSchemasAreInlinedAndRecursiveBackEdgesRemainLocal()
        {
            foreach (var pair in McpServer.AllToolMethods())
                Assert.DoesNotContain("\"$ref\"", McpServer.ToolInputSchema(pair.Key, pair.Value).GetRawText());
            var schema = JsonNode.Parse("{\"type\":\"object\",\"properties\":{\"a\":{\"$ref\":\"#/$defs/a\"},\"b\":{\"$ref\":\"#/$defs/b\"}},\"$defs\":{\"a\":{\"type\":\"string\"},\"b\":{\"type\":\"object\",\"properties\":{\"child\":{\"$ref\":\"#/$defs/b\"}}}}}")!.AsObject();
            var inlined = McpServer.InlineSchema(schema);
            Assert.Equal("string", (string?)inlined["properties"]!["a"]!["type"]);
            Assert.Single(inlined["$defs"]!.AsObject());
            Assert.Null(new InputSchema(JsonSerializer.SerializeToElement(inlined)).Validate(JsonSerializer.Deserialize<JsonElement>("{\"a\":\"text\",\"b\":{\"child\":{}}}"), "arguments"));
        }

        [Fact]
        public void GuideAndEveryRecipeTopicUseTheSameExampleLibrary()
        {
            var usage = new ToolUsageTools();
            foreach (string topic in new[] { "workflow", "openness-workflow", "startdrive-bico", "hmi", "errors", "scl", "scl-sd", "lad", "fbd", "db", "udt", "s7res", "stl", "graph", "hmi-javascript", "hmi-vbscript" })
            {
                var selected = ToolUsageCatalog.GuideSelection(topic);
                var expected = usage.GetToolUsage(toolName: (string?)selected["toolName"] ?? "", query: (string?)selected["query"] ?? "",
                    language: (string?)selected["language"] ?? "", exampleId: (string?)selected["exampleId"] ?? "", operation: (string?)selected["operation"] ?? "");
                if (topic != "startdrive-bico") Assert.True((bool)Body(expected)["ok"]!);
                else Assert.Equal("TOOL_NOT_FOUND", Code(expected)); // The fixture catalog has no Startdrive boundary.
            }
            foreach (var recipe in ToolRecipes.All)
                foreach (string release in new[] { "20", "21" })
                {
                    var rows = ToolRecipes.ForRelease(release, ToolUsageCatalog.ProfileEntries(release).Select(r => (string)r!["currentName"]!), recipe.Topic);
                    foreach (var row in rows)
                    {
                        Assert.Equal(recipe.Purpose, (string?)row!["purpose"]);
                        Assert.Equal(recipe.Preconditions, (string?)row["preconditions"]);
                        Assert.Equal(recipe.Notes, (string?)row["notes"]);
                        Assert.Equal(recipe.Steps.Count, row["steps"]!.AsArray().Count);
                    }
                }
            Assert.Equal("NOT_FOUND", Code(usage.GetToolUsage(exampleId: "sequence/no-such-topic", exampleKind: "sequence")));
            Assert.DoesNotContain(McpServer.AllToolMethods().Keys, n => n == "GetRecipe" || n == "GetAuthoringGuide" || n == "PreflightToolCall" || n == "ReadToolBatch");
        }

        [Fact]
        public void CatalogPagingAndExampleFiltersUseTheFullCatalog()
        {
            var names = McpServer.AllToolMethods().Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var usage = new ToolUsageTools();
            var first = Body(usage.GetToolUsage(limit: 1));
            var second = Body(usage.GetToolUsage(offset: 1, limit: 1));
            Assert.True((bool)first["ok"]!);
            Assert.Equal(names[0], (string?)first["data"]!["tools"]![0]);
            Assert.Equal(names[1], (string?)second["data"]!["tools"]![0]);
            Assert.Equal(1, (int)first["meta"]!["paging"]!["nextOffset"]!);
            Assert.Null(first["data"]!["nextOffset"]);
            var found = Body(McpServer.FindToolsV4(limit: 1, offset: 1));
            Assert.Equal(names.Length, (int)found["meta"]!["paging"]!["total"]!);
            Assert.Equal(3, found["data"]!["items"]!.AsArray().Count);
            var filtered = Body(usage.GetToolUsage(toolName: "GetToolUsage", exampleKind: "sequence"));
            var examples = ToolUsageCatalog.Examples(McpServer.ReleaseKey, "full-engine", names, toolName: "GetToolUsage", exampleKind: "sequence");
            Assert.Equal(examples["examples"]!.ToJsonString(), filtered["data"]!["examples"]!.ToJsonString());
            Assert.Equal("INVALID_ARGUMENT", Code(usage.GetToolUsage(offset: -1)));
            Assert.Equal("INVALID_ARGUMENT", Code(McpServer.FindToolsV4(offset: int.MaxValue)));
        }

        [Fact]
        public void UsageServesEveryMigratedExampleAgainstItsActualSchema()
        {
            var usage = new ToolUsageTools();
            var entries = ToolUsageCatalog.ProfileEntries(McpServer.ReleaseKey)
                .Where(r => (int?)r!["envelopeVersion"] == 4).ToArray();
            Assert.NotEmpty(entries);
            var methods = McpServer.AllToolMethods();
            foreach (var entry in entries)
            {
                var name = (string)entry!["currentName"]!;
                Assert.True(McpServer.IsInfrastructureV4(name), name);
                // This offline assembly links only the shared tools. The full-engine
                // usage-contracts check covers every generated V4 entry without filtering.
                if (!methods.ContainsKey(name)) continue;
                var result = Body(usage.GetToolUsage(toolName: name));
                Assert.True((bool)result["ok"]!);
                var data = result["data"]!;
                var arguments = data["example"]!["request"]!["params"]!["arguments"]!;
                foreach (var value in entry["arguments"]!.AsObject())
                    Assert.True(JsonNode.DeepEquals(value.Value, arguments[value.Key]), name + "." + value.Key);
                foreach (var value in arguments.AsObject().Where(p => !entry["arguments"]!.AsObject().ContainsKey(p.Key)))
                    Assert.True(JsonNode.DeepEquals(data["inputSchema"]!["properties"]![value.Key]!["default"], value.Value), name + "." + value.Key);
                Assert.Equal("parameterized-call-example", (string?)data["example"]!["kind"]);
                Assert.Null(new InputSchema(JsonSerializer.SerializeToElement(data["inputSchema"]))
                    .Validate(JsonSerializer.SerializeToElement(arguments), "arguments"));
            }
        }

        [Fact]
        public void ExampleGateChecksInnerSchemasAndBatchAllowlistsWithoutDispatch()
        {
            JsonObject Parse(string value) => JsonNode.Parse(value)!.AsObject();
            Assert.Null(McpServer.ValidateInfrastructureExample("CallTool", Parse("{\"name\":\"ReadFixture\",\"arguments\":{\"success\":true}}")));
            Assert.NotNull(McpServer.ValidateInfrastructureExample("PreviewToolCall", Parse("{\"name\":\"ReadFixture\",\"arguments\":{\"success\":\"true\"}}")));
            Assert.NotNull(McpServer.ValidateInfrastructureExample("CallTool", Parse("{\"name\":\"ReadFixture\",\"argumentsJson\":{}}")));
            Assert.NotNull(McpServer.ValidateInfrastructureExample("PreviewToolBatch", Parse("{\"expectedProject\":\"Fixture\",\"operations\":[{\"name\":\"ReadFixture\",\"arguments\":{}}]}")));
            Assert.Null(McpServer.ValidateInfrastructureExample("PreviewToolBatch", Parse("{\"expectedProject\":\"Fixture\",\"operations\":[{\"name\":\"WriteFixture\",\"arguments\":{\"target\":\"A\"}}]}")));
            Assert.Empty(BatchProbes.Calls);
        }

        [Fact]
        public void ExportGuardPreservesEnvelopeIdentityAndReportsPagingAndContentHash()
        {
            string? previous = Environment.GetEnvironmentVariable("TIA_MCP_MAX_RESPONSE_CHARS");
            try
            {
                Environment.SetEnvironmentVariable("TIA_MCP_MAX_RESPONSE_CHARS", "1000");
                var source = McpServer.V4Result("GetToolUsage", new JsonObject { ["text"] = new string('x', 5000) });
                var body = Body(ResponseGuardTool.Shrink(source, "GetToolUsage", ""));
                Assert.Equal(Body(source)["meta"]!["requestId"]!.ToJsonString(), body["meta"]!["requestId"]!.ToJsonString());
                Assert.Equal(1000, (int)body["meta"]!["paging"]!["nextOffset"]!);
                Assert.Equal(64, ((string)body["data"]!["export"]!["sha256"]!).Length);
                var stored = ExportStore.Get((string)body["data"]!["export"]!["id"]!);
                Assert.Equal(((TextContentBlock)source.Content.Single()).Text, stored!.Content);
            }
            finally { Environment.SetEnvironmentVariable("TIA_MCP_MAX_RESPONSE_CHARS", previous); ExportStore.Clear(0); }
        }
    }
}
