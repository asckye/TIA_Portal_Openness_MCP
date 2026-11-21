using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;
using Xunit;

namespace PlcExchangeTests
{
    public sealed class HmiExchangeContractsTests
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static readonly Assembly Engine = typeof(McpServer).Assembly;
        private static Type ToolType(string name) => Engine.GetType("TiaMcpServer.ModelContextProtocol." + name, true)!;
#if TIA_PLC_EXCHANGE_TESTS
        public HmiExchangeContractsTests() => PlcExchangeTarget.ConfigureHost();
#endif
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
        private static object? Boundary(string name, params object?[] args)
        {
            if (name == "ValidateV4Arguments")
            {
                string tool = ((MethodInfo)args[0]!).GetCustomAttribute<McpServerToolAttribute>()!.Name!;
                var input = JsonNode.Parse(((JsonElement)args[1]!).GetRawText())!.AsObject();
                var rawSchema = (JsonElement)args[2]!;
                var required = rawSchema.TryGetProperty("required", out var fields)
                    ? fields.EnumerateArray().Select(field => field.GetString()!).ToArray() : Array.Empty<string>();
                input = CanonicalToolParameters.WithDefaults(tool, input, required, Path.Combine(Path.GetTempPath(), "stack-l-example-exports"));
                var schema = CanonicalToolParameters.Schema(tool, rawSchema);
                var error = CanonicalToolParameters.ValidateAliases(tool, input, required, requireOutput: true)
                    ?? new InputSchema(schema).Validate(JsonSerializer.SerializeToElement(input), "arguments");
                if (error != null) return error;
                args[1] = JsonSerializer.SerializeToElement(CanonicalToolParameters.LegacyArguments(tool, input));
            }
#if TIA_PLC_EXCHANGE_TESTS
            return PlcExchangeTarget.Boundary(name, args);
#else
            return typeof(McpServer).GetMethods(All)
                .Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(null, args);
#endif
        }
        private static CallToolResult Map(string tool, object response, bool writes, bool current)
            => (CallToolResult)ToolType("HmiExchangeContract").GetMethod("Map", All)!.Invoke(null, new[] { tool, response, writes, current })!;
        private static string[] ParseNames(string json)
        {
#if TIA_PLC_EXCHANGE_TESTS
            return PlcExchangeTarget.ParseNames(json);
#else
            var reference = Engine.GetReferencedAssemblies().Single(a => a.Name!.StartsWith("TiaMcp.Adapter.", StringComparison.Ordinal));
            var adapter = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Engine.Location)!, reference.Name + ".dll"));
            var decoder = adapter.GetType("TiaMcp.Adapters.Native.Hmi.UnifiedRequestValues", true)!.GetField("Decoder", All)!;
            var decode = Engine.GetType("TiaMcpServer.Siemens.Services.UnifiedInputCodec", true)!.GetMethod("Decode", All)!;
            Func<string, object> read = text => { using var document = JsonDocument.Parse(text); return decode.Invoke(null, new object[] { document.RootElement })!; };
            var input = Expression.Parameter(typeof(string));
            decoder.SetValue(null, Expression.Lambda(decoder.FieldType,
                Expression.Convert(Expression.Invoke(Expression.Constant(read), input), decoder.FieldType.GetGenericArguments()[1]), input).Compile());
            return (string[])adapter.GetType("TiaMcp.Adapters.Native.Hmi.UnifiedExchangeLogic", true)!.GetMethod("ParseExpectedNames", All)!.Invoke(null, new object[] { json, 500 })!;
#endif
        }
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
        public class InspectionSessionProxy : DispatchProxy
        {
            protected override object? Invoke(MethodInfo? method, object?[]? args)
            {
                if (method!.Name == "ResolveHmiSoftwareOrThrow")
                    return new { ScreenGroups = new[] { new { Name = "Line", Screens = new[] { new { Name = "Overview" } }, Groups = Array.Empty<object>() } }
                    };
                if (method.Name == "RunHmiStepTool")
                {
                    var action = (Delegate)args![1]!;
                    var meta = Activator.CreateInstance(action.Method.GetParameters()[0].ParameterType, nonPublic: true);
                    return action.DynamicInvoke(meta);
                }
                throw new InvalidOperationException("Unexpected inspection method " + method.Name);
            }
        }

        [Fact]
        public void Nonempty_screen_group_refuses_deletion_before_writing()
        {
            var type = PlcExchangeTarget.Adapter.GetType("TiaMcp.Adapters.Native.Hmi.UnifiedHmiInspectionAdapter", true)!;
            var session = DispatchProxy.Create(type.GetConstructors().Single().GetParameters()[0].ParameterType, typeof(InspectionSessionProxy));
            var adapter = Activator.CreateInstance(type, session);
            var failure = Assert.Throws<TargetInvocationException>(() =>
                type.GetMethod("DeleteEmptyUnifiedHmiScreenGroup")!.Invoke(adapter, new object[] { "Panel", "/Line", true }));
            var refusal = Assert.IsType<AdapterPreconditionException>(failure.GetBaseException());
            Assert.Equal("target-not-empty", refusal.Condition);
        }

        public sealed class ScriptFolder
        {
            public void Delete() => throw new InvalidOperationException("The preview attempted a deletion.");
        }

        [Fact]
        public void Classic_script_folder_preview_ignores_confirmation_and_apply_names_it()
        {
            var adapter = PlcExchangeTarget.Adapter.GetType("TiaMcp.Adapters.Native.Hmi.MotionProDiagClassicHmiAdapter", true)!;
            var method = adapter.GetMethod("ClassicDelete", All)!;
            var meta = Activator.CreateInstance(method.GetParameters()[4].ParameterType, nonPublic: true);
            var args = new object?[] { new ScriptFolder(), Array.Empty<object>(), "Scripts", false, meta, false };
            Assert.Contains("preview", (string?)method.Invoke(null, args));
            args[5] = true;
            var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, args));
            var argument = Assert.IsType<ArgumentException>(error.InnerException);
            Assert.Equal("confirmDelete", argument.ParamName);
            var reply = new ResponseMessage { Meta = new JsonObject {
                ["operationSuccess"] = false, ["mayHaveChanged"] = false, ["mayHaveWrittenFiles"] = false,
                ["status"] = StepFailureStatus.Of(argument), ["v4FailureCode"] = MutationEvidence.StepFailureCode(argument),
                ["nativeMessage"] = argument.Message, ["parameter"] = StepFailureStatus.Parameter(argument) } };
            var body = Body(ClassicMotionToolContract.Map("ManageClassicHmiScript", reply, false, true));
            Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
            Assert.Equal("/confirmDelete", (string?)body["error"]?["details"]?["parameter"]);
        }

        [Fact]
        public void Connection_preview_rejects_tag_table_XML_before_worker_dispatch()
        {
            string file = Path.GetTempFileName();
            try
            {
                File.WriteAllText(file, "<Document><Hmi.Tag.TagTable ID='0'><AttributeList><Name>Tags</Name></AttributeList></Hmi.Tag.TagTable></Document>");
                var service = new HmiExchangeService((_, _) => throw new InvalidOperationException("Unexpected worker call"), () => "panel.ap21");
                var result = Body(new HmiExchangeTools(service).ImportHmiConnectionV4("Panel", file));
                Assert.Equal("INVALID_ARGUMENT", (string?)result["error"]?["code"]);
                Assert.Contains("Hmi.Communication.Connection", (string?)result["error"]?["message"]);
                Assert.Null(result["data"]?["planHash"]);
            }
            finally { File.Delete(file); }
        }

        [Fact]
        public void Directory_import_names_the_destination_when_every_item_loses_the_same_folder()
        {
            const string folder = "/Line/Area";
            var assembly = PlcExchangeTarget.Adapter;
            var policy = assembly.GetType("TiaMcp.Adapters.Native.Hmi.HmiExchangePolicy", true)!;
            var root = new { Folders = Array.Empty<object>() };
            var failure = Assert.Throws<TargetInvocationException>(() =>
                policy.GetMethod("Folder", All)!.Invoke(null, new object[] { root, folder, "Folders" })).InnerException;
            var adapter = assembly.GetType("TiaMcp.Adapters.Native.Hmi.HmiExchangeAdapter", true)!;
            var item = adapter.GetMethod("ImportItem", All)!.Invoke(null, new object?[] { "Main.xml", false, failure, null });
            var result = Body(Map("ImportHmiScreensFromDirectory", new ResponseMessage { Meta = new JsonObject {
                ["success"] = false, ["items"] = new JsonArray(JsonSerializer.SerializeToNode(item)) } }, true, true));
            Assert.Equal("NOT_FOUND", (string?)result["error"]?["code"]);
            Assert.Equal(folder, (string?)result["error"]?["details"]?["target"]);
            Assert.Contains(folder, (string?)result["error"]?["message"]);
        }

        [Fact]
        public void Connection_preview_accepts_an_exported_classic_connection()
        {
            string file = Path.GetTempFileName();
            try
            {
                File.WriteAllText(file, "<Document><Engineering version='V21'/><Hmi.Communication.Connection ID='0'>"
                    + "<AttributeList><Name>PLC connection</Name></AttributeList></Hmi.Communication.Connection></Document>");
                var service = new HmiExchangeService((_, _) => throw new InvalidOperationException("Unexpected worker call"), () => "");
                var body = Body(new HmiExchangeTools(service).ImportHmiConnectionV4("Panel", file));
                Assert.True((bool?)body["ok"]);
            }
            finally { File.Delete(file); }
        }

        [Theory]
        [InlineData("ExportHmiScreen", "Overview")]
        [InlineData("ExportHmiConnection", "S7 connection")]
        public void Exchange_refusal_names_the_missing_target(string tool, string name)
        {
            var reply = HmiExchangeContract.Run(tool, true, true, () => throw new PortalException(PortalErrorCode.NotFound, "Missing: " + name));
            Assert.Contains(name, (string?)Body(reply)["error"]?["message"]);
        }

        [Fact]
        public void ClassicXmlExportsExposeTheOfficialDefaultAndExplicitOption()
        {
            foreach (string tool in new[] { "ExportHmiScreen", "ExportHmiTagTable", "ExportHmiConnection", "ExportHmiProgram" })
            {
                var parameter = Method(tool).GetParameters().Single(p => p.Name == "exportOptions");
                Assert.Equal(typeof(string), parameter.ParameterType);
                Assert.Equal("WithDefaults", parameter.DefaultValue);
            }
        }

        [Fact]
        public void HmiImportsRequireExplicitOverwrite()
        {
            foreach (string tool in new[] { "ImportHmiScreen", "ImportHmiTagTable", "ImportHmiConnection",
                "ImportHmiScreensFromDirectory", "ImportHmiTagTablesFromDirectory", "ExchangeUnifiedTags",
                "ExchangeUnifiedScriptModules", "ImportUnifiedOpcUaAlarms" })
            {
                var parameter = Method(tool).GetParameters().Single(p => p.Name == "overwrite");
                Assert.Equal(typeof(bool), parameter.ParameterType);
                Assert.Equal(false, parameter.DefaultValue);
            }
        }

        [Fact]
        public void UnverifiableClassicScreenSizeKeepsItsMessageAndConditionForSingleAndDirectoryImports()
        {
            string file = Path.GetTempFileName();
            try
            {
                File.WriteAllText(file, "<Document><Hmi.Screen.Screen><AttributeList><Width>800</Width><Height>480</Height></AttributeList>"
                    + "</Hmi.Screen.Screen></Document>");
                var type = PlcExchangeTarget.Adapter.GetType("TiaMcp.Adapters.Native.Hmi.HmiExchangeAdapter", true)!;
                var adapter = Activator.CreateInstance(type, new object?[] { null });
                var guard = type.GetMethod("GuardClassicScreenSize", All)!;
                var panel = new { ScreenFolder = new { Screens = Array.Empty<object>(), Folders = Array.Empty<object>() } };
                var thrown = Assert.Throws<TargetInvocationException>(() => guard.Invoke(adapter, new object[] { panel, file }));
                var failure = Assert.IsType<PlcSoftwareException>(thrown.InnerException);
                Assert.Equal("PreconditionFailed", failure.Code);
                Assert.Equal(false, failure.Data["mayHaveChanged"]);
                var refusal = Assert.IsType<AdapterPreconditionException>(failure.GetBaseException());
                var reply = new HmiReply { Failure = new PlcSoftwareFailure { Code = failure.Code, Message = failure.Message,
                    Evidence = new() { ["v4FailureCode"] = MutationEvidence.FailureCode(refusal), ["condition"] = refusal.Condition } } };
                var service = new HmiExchangeService((_, _) => JsonSerializer.SerializeToNode(reply), () => "panel.ap17");
                var tools = new HmiExchangeTools(service);
                var single = Body(HmiExchangeContract.Run("ImportHmiScreen", true, true, () => tools.ImportHmiScreen("HMI", "/", file)));
                var row = type.GetMethod("ImportItem", All)!.Invoke(null, new object?[] { file, false, failure, null });
                var batch = Body(Map("ImportHmiScreensFromDirectory", new ResponseMessage { Meta = new JsonObject {
                    ["success"] = false, ["items"] = new JsonArray(JsonSerializer.SerializeToNode(row)) } }, true, true));
                foreach (var result in new[] { single, batch["data"]!["items"]![0]!["result"]! })
                {
                    Assert.Equal("PRECONDITION_FAILED", (string?)result["error"]?["code"]);
                    Assert.Equal("classic-screen-size-unverifiable", (string?)result["error"]?["details"]?["condition"]);
                    Assert.Contains("Screen size 800x480", (string?)result["error"]?["message"]);
                    Assert.Equal("not-started", (string?)result["meta"]?["execution"]);
                    Assert.False((bool?)result["data"]?["evidence"]?["mayHaveChanged"]);
                }
                var accepted = type.GetMethod("ImportItem", All)!.Invoke(null, new object?[] { "Earlier.xml", true, null, null });
                var mixed = Body(Map("ImportHmiScreensFromDirectory", new ResponseMessage { Meta = new JsonObject {
                    ["success"] = false, ["items"] = JsonSerializer.SerializeToNode(new[] { accepted, row }) } }, true, true));
                Assert.Equal("PARTIAL_FAILURE", (string?)mixed["error"]?["code"]);
                Assert.Equal("succeeded", (string?)mixed["data"]?["items"]?[0]?["result"]?["meta"]?["outcome"]);
                Assert.Equal("PRECONDITION_FAILED", (string?)mixed["data"]?["items"]?[1]?["result"]?["error"]?["code"]);
            }
            finally { File.Delete(file); }
        }

        [Fact]
        public void SingleScreenImportWithNoProjectStillReturnsProjectNotBound()
        {
            var reply = new HmiReply { Failure = new PlcSoftwareFailure { Code = "InvalidState", Message = "No project is open." } };
            var service = new HmiExchangeService((_, _) => JsonSerializer.SerializeToNode(reply), () => "");
            var tools = new HmiExchangeTools(service);
            var result = Body(HmiExchangeContract.Run("ImportHmiScreen", true, true, () => tools.ImportHmiScreen("HMI", "/", "Screen.xml")));
            Assert.Equal("PROJECT_NOT_BOUND", (string?)result["error"]?["code"]);
        }

        [Fact]
        public void ScreenImportPreviewExplainsWhenThePanelSizeIsChecked()
        {
            string file = Path.GetTempFileName();
            try
            {
                File.WriteAllText(file, "<Document/>");
                var tools = new HmiExchangeTools(new HmiExchangeService((_, _) => throw new InvalidOperationException(), () => ""));
                var preview = Body(tools.ImportHmiScreenV4("HMI", "/", file));
                Assert.True((bool?)preview["data"]?["dryRun"]);
                Assert.Contains("screen size is checked when the import is applied", (string?)preview["data"]?["scope"]);
            }
            finally { File.Delete(file); }
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
#if TIA_PLC_EXCHANGE_TESTS
            var raw = PlcExchangeTarget.RegisteredTool("ExchangeUnifiedTags");
#else
            var raw = (McpServerTool)ToolType("ToolCatalog").GetMethod("CreateTool", All)!.Invoke(null, new object?[] { method, null })!;
#endif
            var direct = (McpServerTool)Boundary("WithSchemaHints", raw, "ExchangeUnifiedTags", method)!;
            var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
                Params = new CallToolRequestParams { Name = "ExchangeUnifiedTags", Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) }
            };
            var directResult = await direct.InvokeAsync(request);
            foreach (var result in new[] { directResult, (CallToolResult)Boundary("DispatchNestedTool", "ExchangeUnifiedTags", Args(json))! })
            {
                var body = Body(result);
                Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]!["code"]);
                string pointer = value is "[null]" or "[42]" ? "/expectedTagNames/0" : "/expectedTagNames";
                Assert.Equal(pointer, (string?)body["error"]!["details"]!["parameter"]);
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
#if TIA_PLC_EXCHANGE_TESTS
            Assert.Null(McpServer.ToolInvoker.Bind("ExchangeUnifiedTags", Args(json), out var bound));
            Assert.NotNull(bound);
            Assert.False(Args(json).Json.TryGetProperty("expectedTagNames", out _));
#else
            Assert.Null(Boundary("BindV4Call", bind));
            Assert.Null(((object?[])bind[3]!)[5]);
#endif
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
            Assert.Equal(outcome == "unknown", (string?)body["meta"]!["outcome"] == "unknown");
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

        [Theory]
        [InlineData("{\"exportFile\":42}")]
        [InlineData("{\"exportFile\":\"relative.xml\"}")]
        [InlineData("{\"exportPath\":\"C:/a.xml\",\"exportFile\":\"C:/b.xml\"}")]
        public void InvalidGroupExamplesRemainRejected(string invalid)
        {
            const string name = "ExportHmiScreen";
            var type = ToolType("ToolUsageTools");
            var method = type.GetMethod("GetToolUsage")!;
            var arguments = method.GetParameters().Select(p => p.Name == "toolName" ? name : p.DefaultValue).ToArray();
            var body = Body((CallToolResult)method.Invoke(Activator.CreateInstance(type), arguments)!);
            var example = body["data"]!["example"]!["request"]!["params"]!["arguments"]!.DeepClone().AsObject();
            foreach (var field in JsonNode.Parse(invalid)!.AsObject()) example[field.Key] = field.Value?.DeepClone();
            var schema = (JsonElement)Boundary("ToolInputSchema", name, Method(name))!;
            Assert.NotNull(Boundary("ValidateV4Arguments", Method(name), JsonSerializer.SerializeToElement(example), schema));
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
#if TIA_PLC_EXCHANGE_TESTS
            PlcExchangeTarget.DeleteTag(meta, () => target, _ => target = null);
#else
            Engine.GetType("TiaMcpServer.Siemens.HmiTagDeletion", true)!.GetMethod("Execute", All)!.Invoke(null,
                new object[] { meta, false, (Func<object?>)(() => target), (Action<object>)(_ => target = null) });
#endif
            meta["success"] = true;
            var result = Body(Map("DeleteHmiTag", new ResponseMessage { Meta = meta }, true, false));
            Assert.Equal("succeeded", (string?)result["meta"]!["outcome"]);
            Assert.True((bool)result["data"]!["evidence"]!["verifiedAbsent"]!);
            meta = new JsonObject { ["status"] = "HmiReadSessionBlocked", ["success"] = false, ["lastFailure"] = new JsonObject { ["error"] = "failure\n   secret stack" } };
            result = Body(Map("DeleteHmiTag", new ResponseMessage { Meta = meta }, true, false));
            Assert.Equal("OUTCOME_UNKNOWN", (string?)result["error"]!["code"]);
            Assert.Equal("unknown", (string?)result["meta"]!["outcome"]);
            Assert.DoesNotContain("secret stack", result.ToJsonString());
        }
    }
}
