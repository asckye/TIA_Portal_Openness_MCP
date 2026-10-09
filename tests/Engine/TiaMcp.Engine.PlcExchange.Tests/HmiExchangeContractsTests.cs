using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;
using Xunit;

namespace PlcExchangeTests
{
    public sealed class HmiExchangeContractsTests
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static readonly Assembly Engine = typeof(McpServer).Assembly;
        private static Type ToolType(string name) => Engine.GetType("TiaMcpServer.ModelContextProtocol." + name, true)!;
        static HmiExchangeContractsTests()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) => {
                var name = new AssemblyName(args.Name);
                if (!name.Name!.StartsWith("Siemens.Engineering", StringComparison.Ordinal)) return null;
                var root = Environment.GetEnvironmentVariable("TIA_MCP_TEST_PUBLIC_API_ROOT");
                if (string.IsNullOrEmpty(root)) return null;
                string release = name.Version!.Major.ToString();
                var directory = Path.Combine(root, "TIA_V" + release + "_PublicAPI", "V" + release);
                if (release == "21") directory = Path.Combine(directory, "net48");
                var path = Path.Combine(directory, name.Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
        }
        private static Dictionary<string, MethodInfo> Methods() => new[] { "HmiExchangeTools", "HmiTagDeletionTools", "UnifiedExchangeTools" }
            .SelectMany(n => ToolType(n).GetMethods(All)).Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
            .ToDictionary(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name!, m => m);
        private static MethodInfo Method(string name) => Methods()[name];
        private static object? Boundary(string name, params object?[] args) => typeof(McpServer).GetMethods(All)
            .Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(null, args);
        private static CallToolResult Map(string tool, object response, bool writes, bool current)
            => (CallToolResult)ToolType("HmiExchangeContract").GetMethod("Map", All)!.Invoke(null, new[] { tool, response, writes, current })!;
        private static string[] ParseNames(string json)
            => (string[])Engine.GetType("TiaMcpServer.Siemens.UnifiedExchangeLogic", true)!.GetMethod("ParseExpectedNames", All)!.Invoke(null, new object[] { json, 500 })!;
        public class ServerProxy : DispatchProxy
        {
            protected override object? Invoke(MethodInfo? method, object?[]? args) => null;
        }
        private static readonly string[] Names = {
            "ListHmiScreens", "ListHmiTagTables", "ListHmiTags", "ListHmiConnections",
            "ExportHmiScreen", "ExportHmiTagTable", "ExportHmiConnection", "ExportHmiProgram",
            "ImportHmiScreen", "ImportHmiTagTable", "ImportHmiConnection", "ImportHmiScreensFromDirectory", "ImportHmiTagTablesFromDirectory",
            "DeleteHmiTag", "ExchangeUnifiedTags", "ExchangeUnifiedScriptModules", "ImportUnifiedOpcUaAlarms"
        };
        private static ToolArguments Args(string json) => new ToolArguments(JsonSerializer.Deserialize<JsonElement>(json));
        private static JsonObject Body(CallToolResult result)
        {
            string text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
            var envelope = V4Json.Deserialize<Envelope>(text);
            Assert.Equal(!envelope.Ok, result.IsError);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), result.StructuredContent));
            return result.StructuredContent!.AsObject();
        }
        [Fact]
        public void AllSeventeenEntriesHaveOneReviewedNameAndEnvelope()
        {
            Assert.Equal(Names.OrderBy(n => n), Methods().Keys.OrderBy(n => n));
            Assert.All(Methods(), method => Assert.Equal(typeof(CallToolResult), method.Value.ReturnType));
            Assert.All(Methods().SelectMany(m => m.Value.GetParameters()), p => Assert.False(p.Name!.EndsWith("Json", StringComparison.Ordinal)));
            var parameter = Method("ExchangeUnifiedTags").GetParameters().Single(p => p.Name == "expectedTagNames");
            Assert.Equal(typeof(string[]), parameter.ParameterType);
            Assert.True(parameter.HasDefaultValue);
            Assert.Null(parameter.DefaultValue);
            var schema = (JsonElement)Boundary("ToolInputSchema", "ExchangeUnifiedTags", Method("ExchangeUnifiedTags"))!;
            var names = schema.GetProperty("properties").GetProperty("expectedTagNames");
            Assert.Equal("array", names.GetProperty("type").GetString());
            Assert.Equal("string", names.GetProperty("items").GetProperty("type").GetString());
            Assert.False(names.TryGetProperty("default", out _));
        }

        [Theory]
        [InlineData("\"[]\"")]
        [InlineData("null")]
        [InlineData("{}")]
        [InlineData("[null]")]
        [InlineData("[42]")]
        public async Task DirectAndNestedDispatchRejectErasedOrInvalidArraysBeforeService(string value)
        {
            string json = "{\"softwarePath\":\"HMI\",\"action\":\"import\",\"directory\":\"C:/fixture\",\"expectedTagNames\":" + value + "}";
            var method = Method("ExchangeUnifiedTags");
            var raw = (McpServerTool)ToolType("ToolCatalog").GetMethod("CreateTool", All)!.Invoke(null, new object?[] { method, null })!;
            var direct = (McpServerTool)Boundary("WithSchemaHints", raw, "ExchangeUnifiedTags", method)!;
            var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
                Params = new CallToolRequestParams { Name = "ExchangeUnifiedTags", Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) }
            };
            foreach (var result in new[] { await direct.InvokeAsync(request), (CallToolResult)Boundary("DispatchNestedTool", "ExchangeUnifiedTags", Args(json))! })
            {
                var body = Body(result);
                Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]!["code"]);
                Assert.Equal("arguments", (string?)body["error"]!["details"]!["parameter"]);
                Assert.Equal("rejected-before-operation", (string?)body["meta"]!["outcome"]);
                Assert.Equal("not-started", (string?)body["meta"]!["execution"]);
            }
        }

        [Theory]
        [InlineData("expectedTagNamesJson", "\"[]\"")]
        [InlineData("ExpectedTagNames", "[]")]
        public void OldAndWrongCaseParametersAreRejected(string parameter, string value)
        {
            var result = (CallToolResult)Boundary("DispatchNestedTool", "ExchangeUnifiedTags", Args("{\"softwarePath\":\"HMI\",\"action\":\"import\",\"directory\":\"C:/fixture\",\"" + parameter + "\":" + value + "}"))!;
            Assert.Equal("INVALID_ARGUMENT", (string?)Body(result)["error"]!["code"]);
        }

        [Fact]
        public void OmittedNamesAndExactAcceptedNamesRetainLegacyParserMeaning()
        {
            const string json = "{\"softwarePath\":\"HMI\",\"action\":\"import\",\"directory\":\"C:/fixture\"}";
            object?[] bind = { "ExchangeUnifiedTags", Args(json), null, null };
            Assert.Null(Boundary("BindV4Call", bind));
            Assert.Null(((object?[])bind[3]!)[5]);
            Assert.Empty(ParseNames(V4Json.Serialize(Array.Empty<string>())));
            string[] names = { "Tag/one", " Tag,two " };
            Assert.Equal(names, ParseNames(V4Json.Serialize(names)));
            foreach (var invalid in new[] { new[] { " " }, new[] { "Tag", "tag" }, Enumerable.Range(0, 501).Select(i => "T" + i).ToArray() })
                Assert.IsType<ArgumentException>(Assert.Throws<TargetInvocationException>(() => ParseNames(V4Json.Serialize(invalid))).InnerException);
            Assert.Equal(500, ParseNames(V4Json.Serialize(Enumerable.Range(0, 500).Select(i => "T" + i).ToArray())).Length);
        }

        [Theory]
        [InlineData("{}", true, "unknown", "unknown", "unknown")]
        [InlineData("{}", false, "read-failed", "read-only", "unknown")]
        [InlineData("{\"success\":true}", false, "succeeded", "read-only", "complete")]
        [InlineData("{\"success\":true}", true, "succeeded", "completed", "complete")]
        [InlineData("{\"success\":true,\"fullContentVerified\":false}", true, "succeeded", "completed", "partial")]
        [InlineData("{\"success\":false,\"tool\":\"ExchangeUnifiedTags\",\"status\":\"InvalidState\"}", true, "rejected-before-operation", "not-started", "none")]
        [InlineData("{\"success\":false,\"mayHaveChanged\":true,\"nativeSuccess\":false}", true, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":false,\"mayHaveChanged\":true,\"nativeSuccess\":true,\"missingNames\":[\"T\"]}", true, "failed", "completed", "complete")]
        [InlineData("{\"success\":true,\"writeOutcomeUnknown\":true}", true, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":false,\"tool\":\"ExchangeUnifiedTags\",\"readStarted\":true}", false, "read-failed", "read-only", "unknown")]
        [InlineData("{\"success\":false,\"mayHaveWrittenFiles\":true}", true, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":false,\"mayHaveWrittenFiles\":true,\"apiCallSuccess\":true}", true, "partial", "partial", "partial")]
        [InlineData("{\"success\":false,\"partialArtifactExists\":true}", true, "partial", "partial", "partial")]
        public void ExplicitEvidenceControlsOutcome(string json, bool writes, string outcome, string execution, string completeness)
        {
            var body = Body(Map("ExchangeUnifiedTags", new ResponseMessage { Message = "Success is only text", Meta = JsonNode.Parse(json)!.AsObject() }, writes, true));
            Assert.Equal(outcome, (string?)body["meta"]!["outcome"]);
            Assert.Equal(execution, (string?)body["meta"]!["execution"]);
            Assert.Equal(completeness, (string?)body["meta"]!["completeness"]);
            Assert.Equal(outcome == "unknown", (bool)body["meta"]!["requiresSessionReset"]!);
            Assert.Equal("current", (string?)body["meta"]!["behaviorPolicy"]);
            Assert.Contains(body["meta"]!["warnings"]!.AsArray(), w => (string?)w!["code"] == "UNVERIFIED_BEHAVIOR");
            Assert.Equal("Success is only text", (string?)body["data"]!["summary"]);
        }

        [Theory]
        [InlineData("{\"operationSuccess\":false,\"capability\":\"unsupported\"}", "partial")]
        [InlineData("{\"operationSuccess\":false,\"mayHaveChanged\":true}", "unknown")]
        public void BatchPreservesOrderAndEarlierSuccessWithoutHidingPartialOrUnknown(string failed, string outcome)
        {
            var meta = JsonNode.Parse("{\"success\":false,\"items\":[{\"target\":\"B\",\"evidence\":{\"success\":true,\"sha256\":\"hash\"}},{\"target\":\"A\",\"evidence\":" + failed + "}]}")!.AsObject();
            var body = Body(Map("ExportHmiProgram", new ResponseBatchExport { Exported = new[] { "B" }, Failed = new[] { "A" }, Meta = meta }, true, true));
            Assert.Equal(outcome, (string?)body["meta"]!["outcome"]);
            Assert.Equal(new[] { "B", "A" }, body["data"]!["items"]!.AsArray().Select(i => (string)i!["target"]!).ToArray());
            Assert.Equal("hash", (string?)body["data"]!["items"]![0]!["result"]!["data"]!["evidence"]!["sha256"]);
            Assert.Single(body["data"]!["exported"]!.AsArray());
            Assert.Single(body["data"]!["failed"]!.AsArray());
        }

        [Fact]
        public void EveryGroupExampleMatchesTheActualRegisteredSchema()
        {
            var type = ToolType("ToolUsageTools");
            var method = type.GetMethod("GetToolUsage")!;
            foreach (string name in Names)
            {
                var arguments = method.GetParameters().Select(p => p.Name == "toolName" ? name : p.DefaultValue).ToArray();
                var body = Body((CallToolResult)method.Invoke(Activator.CreateInstance(type), arguments)!);
                Assert.True((bool)body["ok"]!, name);
                var schema = (JsonElement)Boundary("ToolInputSchema", name, Method(name))!;
                var example = body["data"]!["example"]!["request"]!["params"]!["arguments"]!;
                Assert.Null(Boundary("ValidateV4Arguments", Method(name), JsonSerializer.SerializeToElement(example), schema));
            }
        }

        [Fact]
        public void ParentUnknownEvidenceOutranksKnownPartialItems()
        {
            var meta = JsonNode.Parse("{\"writeOutcomeUnknown\":true,\"items\":[{\"target\":\"B\",\"evidence\":{\"success\":true}},{\"target\":\"A\",\"evidence\":{\"success\":false,\"capability\":\"unsupported\"}}]}")!.AsObject();
            var result = Body(Map("ExportHmiProgram", new ResponseMessage { Meta = meta }, true, true));
            Assert.Equal("unknown", (string?)result["meta"]!["outcome"]);
            Assert.Equal("OUTCOME_UNKNOWN", (string?)result["error"]!["code"]);
        }

        [Fact]
        public void ScriptImportRetainsLimitedContentVerification()
        {
            var result = Body(Map("ExchangeUnifiedScriptModules", new ResponseMessage { Meta = new JsonObject {
                ["success"] = true, ["nativeSuccess"] = true, ["modulesAfter"] = new JsonArray("ModuleA") } }, true, true));
            Assert.Equal("succeeded", (string?)result["meta"]!["outcome"]);
            Assert.Equal("partial", (string?)result["meta"]!["completeness"]);
            Assert.Equal("ModuleA", (string?)result["data"]!["evidence"]!["modulesAfter"]![0]);
        }

        [Fact]
        public void DeletionAndSessionFailureKeepVerificationAndResetEvidence()
        {
            var meta = new JsonObject();
            object? target = new object();
            Engine.GetType("TiaMcpServer.Siemens.HmiTagDeletion", true)!.GetMethod("Execute", All)!.Invoke(null,
                new object[] { meta, false, (Func<object?>)(() => target), (Action<object>)(_ => target = null) });
            meta["success"] = true;
            var result = Body(Map("DeleteHmiTag", new ResponseMessage { Meta = meta }, true, false));
            Assert.Equal("succeeded", (string?)result["meta"]!["outcome"]);
            Assert.True((bool)result["data"]!["evidence"]!["verifiedAbsent"]!);
            meta = new JsonObject { ["status"] = "HmiReadSessionBlocked", ["success"] = false, ["lastFailure"] = new JsonObject { ["error"] = "failure\n   secret stack" } };
            result = Body(Map("DeleteHmiTag", new ResponseMessage { Meta = meta }, true, false));
            Assert.Equal("SESSION_RESET_REQUIRED", (string?)result["error"]!["code"]);
            Assert.True((bool)result["meta"]!["requiresSessionReset"]!);
            Assert.DoesNotContain("secret stack", result.ToJsonString());
        }
    }
}
