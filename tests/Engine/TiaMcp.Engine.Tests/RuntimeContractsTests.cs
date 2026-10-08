using TiaMcpServer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Reflection;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Runtime;
using Xunit;

namespace TiaMcpServer.ModelContextProtocol
{
    public class ResponseOnlineState : ResponseMessage
    {
        // OnlineState values: Offline, Connecting, Online, Incompatible, NotReachable, Protected, Disconnecting
        public string? State { get; set; }
        public bool? IsOnline { get; set; }
        public bool? IsReachable { get; set; }
    }
    public class ResponseDownload : ResponseMessage
    {
        public bool? Ok { get; set; }
        public string? State { get; set; }          // Success | Information | Warning | Error
        public int? ErrorCount { get; set; }
        public int? WarningCount { get; set; }
        public string[]? Errors { get; set; }
        public string[]? Warnings { get; set; }
    }
    public class ResponseCompare : ResponseMessage
    {
        public bool? IsOnline { get; set; }
        public CompareEntry[]? Entries { get; set; }
        public Dictionary<string, int>? Summary { get; set; }
        public bool? Truncated { get; set; }
    }
    public class ResponseCheckDownload : ResponseMessage
    {
        public bool? Ready { get; set; }
        public bool? HasDownloadProvider { get; set; }
        public bool? HasConfiguration { get; set; }
        public bool? IsConsistent { get; set; }
        public string[]? Issues { get; set; }
    }
    public class CompareEntry
    {
        public string? Path { get; set; }
        public string? LeftName { get; set; }
        public string? RightName { get; set; }
        public string? Status { get; set; }
        public string? Details { get; set; }
    }
}
namespace TiaMcpServer.Siemens
{
    internal partial interface IEngineeringSession
    {
        JsonObject GetPutGetAccess(string path) => throw new InvalidOperationException("Runtime contract tests do not enter native sessions.");
        ResponseJsonReport TraceTagCause(string softwarePath, string tag, string blockScope) => throw new InvalidOperationException("Runtime contract tests do not enter native sessions.");
        ResponseJsonReport TraceTagCauseLive(string softwarePath, string tag, string ip, int rack, int slot, string blockScope, string expectModuleContains) => throw new InvalidOperationException("Runtime contract tests do not enter native sessions.");
    }
    internal sealed partial class Portal
    {
        public JsonObject GetPutGetAccess(string path) => throw Unexpected();
        public ResponseJsonReport TraceTagCause(string softwarePath, string tag, string blockScope) => throw Unexpected();
        public ResponseJsonReport TraceTagCauseLive(string softwarePath, string tag, string ip, int rack, int slot, string blockScope, string expectModuleContains) => throw Unexpected();
    }
}
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class OnlineDownloadService
    {
        internal int Calls;
        public ResponseOnlineState GetOnlineState(string softwarePath) { Calls++; return new ResponseOnlineState { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseOnlineState GoOnline(string softwarePath, string ipAddress = "", string password = "", string userName = "", string userType = "", string rhTarget = "", string pgPcInterface = "", bool trustDeviceCertificate = true) { Calls++; return new ResponseOnlineState { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage GoOffline(string softwarePath) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public JsonObject GoOfflineAll() { Calls++; return new JsonObject { ["plcs"] = new JsonArray(), ["allOffline"] = true }; }
        public ResponseCompare CompareSoftwareToOnline(string softwarePath, int maxDepth = 4, int maxEntries = 200) { Calls++; return new ResponseCompare { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseCheckDownload CheckDownloadReadiness(string softwarePath) { Calls++; return new ResponseCheckDownload { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseDownload DownloadToPlc(string softwarePath, bool consistentBlocksOnly = true, bool keepActualValues = true, bool startAfterDownload = true, bool stopBeforeDownload = true, string password = "", string pgPcInterface = "", string targetIpAddress = "", string userManagementMode = "keep", string promptAnswersJson = "{}", string moduleAccessPassword = "", string blockBindingPassword = "", string masterSecretPassword = "", string rhTarget = "", bool trustDeviceCertificate = true) { Calls++; return new ResponseDownload { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ReadTransferRoutes(string softwarePath, int maxItems = 500) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ScanAccessibleDevices(string pgPcInterface = "", string softwarePath = "", int offset = 0, int limit = 100) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage UploadStationFromPlc(string targetIpAddress, string pgPcInterface = "", string password = "", string promptAnswersJson = "{}", bool confirmUpload = false, bool dryRun = true) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage UploadDeviceParameters(string devicePathJson, string itemPathJson, string targetIpAddress, string pgPcInterface = "", string password = "", string promptAnswersJson = "{}", bool confirmUpload = false, bool dryRun = true) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage DownloadPlcToFolder(string softwarePath, string destinationDirectory, string targetForSoftware = "CPU", bool overwriteOnMemoryCard = false, bool keepActualValues = true, string userManagementMode = "keep", string promptAnswersJson = "{}", bool confirmDownload = false, bool dryRun = true) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
    }
}
namespace TiaMcp.Engine.Tests
{
    public sealed class RuntimeContractsTests : IDisposable
    {
        private readonly ToolCatalog catalog = new ToolCatalog(new[] { typeof(RuntimeTools), typeof(RuntimeChannelTools), typeof(PlcSimAdvancedTools), typeof(RuntimeSettingsTools), typeof(OnlineDownloadTools) });
        private readonly TiaMcpServer.Siemens.Services.OnlineDownloadService service = new TiaMcpServer.Siemens.Services.OnlineDownloadService();
        public RuntimeContractsTests()
        {
            McpServer.ConfigureToolBridge(catalog, () => false, new HashSet<string>());
            EngineServices.SetServiceProvider(new ServiceCollection().AddSingleton(new RuntimeTools(null!))
                .AddSingleton(new RuntimeChannelTools()).AddSingleton(new PlcSimAdvancedTools())
                .AddSingleton(new RuntimeSettingsTools(null!)).AddSingleton(new OnlineDownloadTools(service)).BuildServiceProvider());
        }
        public void Dispose() => ToolBridgeFixture.Configure();
        private static JsonObject Body(CallToolResult result)
        {
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), result.StructuredContent));
            var envelope = V4Json.Deserialize<Envelope>(text);
            Assert.Equal(!envelope.Ok, result.IsError);
            return result.StructuredContent!.AsObject();
        }
        [Fact]
        public void EveryEntryHasOneReviewedNameAndConcreteParameters()
        {
            string[] names = {
                "GetOnlineState",
                "ConnectOnlinePlc",
                "DisconnectOnlinePlc",
                "DisconnectOnlinePlcs",
                "CompareSoftwareToOnline",
                "CheckDownloadReadiness",
                "DownloadPlc",
                "ListTransferRoutes",
                "ScanAccessibleDevices",
                "UploadStationFromPlc",
                "UploadDeviceParameters",
                "DownloadPlcToFolder",
                "ListPlcSimAdvancedInstances",
                "ManagePlcSimAdvancedInstance",
                "GetPlcSimAdvancedTags",
                "WritePlcSimAdvancedTags",
                "RunPlcSimAdvancedTestScenario",
                "GetPlcWebVars",
                "WritePlcWebVars",
                "GetPlcWebDiagnostics",
                "SetPlcWebOperatingMode",
                "GetUnifiedRuntimeTags",
                "WriteUnifiedRuntimeTags",
                "GetUnifiedRuntimeAlarms",
                "InvokeUnifiedOpenPipe",
                "GetUnifiedRuntimeSettings",
                "SetUnifiedRuntimeSettings",
                "ProbeS7CpuIdentity",
                "GetPlcLiveValuesS7",
                "SamplePlcLiveValuesS7",
                "TraceTagCause",
                "TraceTagCauseLive",
                "GetPlcLiveValuesOpcUa",
                "GetPlcRunStateS7"
            };
            Assert.Equal(names.OrderBy(n => n), catalog.Methods.Select(p => p.Key).OrderBy(n => n));
            foreach (var method in catalog.Methods.Select(p => p.Value))
            {
                Assert.Equal(typeof(CallToolResult), method.ReturnType);
                Assert.DoesNotContain(method.GetParameters(), p => p.Name!.EndsWith("Json", StringComparison.Ordinal) || p.ParameterType == typeof(JsonElement));
            }
        }

        [Theory]
        [InlineData("{\"success\":true}", true, "succeeded", "read-only", "complete")]
        [InlineData("{\"success\":true,\"dataComplete\":false}", true, "succeeded", "read-only", "partial")]
        [InlineData("{}", true, "read-failed", "read-only", "none")]
        [InlineData("{}", false, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":true,\"outcomeUnknown\":true}", false, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":false,\"mayHaveChanged\":false}", false, "rejected-before-operation", "not-started", "none")]
        [InlineData("{\"success\":false,\"writeOutcomeKnown\":true}", false, "failed", "completed", "complete")]
        public void EnvelopeUsesEvidenceInsteadOfMessageText(string json, bool readOnly, string outcome, string execution, string completeness)
        {
            var body = Body(RuntimeToolContract.Map("WritePlcWebVars", new ResponseMessage { Message = "Success", Meta = JsonNode.Parse(json)!.AsObject() }, readOnly, true));
            Assert.Equal(outcome, (string?)body["meta"]!["outcome"]);
            Assert.Equal(execution, (string?)body["meta"]!["execution"]);
            Assert.Equal(completeness, (string?)body["meta"]!["completeness"]);
            Assert.Equal(outcome == "unknown", (bool)body["meta"]!["requiresSessionReset"]!);
            Assert.Equal("current", (string?)body["meta"]!["behaviorPolicy"]);
        }

        [Fact]
        public void UnknownWriteTakesPrecedenceAndRetainsEarlierSuccess()
        {
            var data = JsonNode.Parse("{\"items\":[{\"name\":\"A\",\"writeAttempted\":true,\"verified\":true},{\"name\":\"B\",\"writeAttempted\":true,\"verified\":false}]}")!.AsObject();
            var body = Body(RuntimeToolContract.Map("WritePlcWebVars", new ResponseJsonReport { Ok = false, Data = data }, false, true));
            Assert.Equal("unknown", (string?)body["meta"]!["outcome"]);
            Assert.Equal(2, body["data"]!["items"]!.AsArray().Count);
            data["items"]![1]!["writeOutcomeKnown"] = true;
            body = Body(RuntimeToolContract.Map("WritePlcWebVars", new ResponseJsonReport { Ok = false, Data = data }, false, true));
            Assert.Equal("partial", (string?)body["meta"]!["outcome"]);
        }

        [Fact]
        public void DirectTypedRejectionsDoNotEnterChannelsOrServices()
        {
            var tools = new RuntimeChannelTools();
            Assert.Equal("rejected-before-operation", (string?)Body(tools.ReadPlcWebVarsV4("unused", "user", "", null!))["meta"]!["outcome"]);
            Assert.Equal("rejected-before-operation", (string?)Body(new OnlineDownloadTools(service).UploadDeviceParametersV4(new[] { " " }, Array.Empty<string>(), "unused"))["meta"]!["outcome"]);
            Assert.Equal(0, service.Calls);
        }

        [Theory]
        [InlineData("GetPlcWebVars", "{\"host\":\"unused\",\"username\":\"u\",\"password\":\"\",\"vars\":\"[]\"}")]
        [InlineData("GetUnifiedRuntimeTags", "{\"tags\":[null]}")]
        [InlineData("GetUnifiedRuntimeAlarms", "{\"systemNames\":null}")]
        [InlineData("GetUnifiedRuntimeSettings", "{\"softwarePath\":\"HMI\",\"expectedProject\":\"P\",\"fields\":null}")]
        [InlineData("UploadDeviceParameters", "{\"devicePath\":\"[]\",\"itemPath\":[],\"targetIpAddress\":\"unused\"}")]
        [InlineData("DownloadPlc", "{\"softwarePath\":\"PLC\",\"promptAnswers\":{\"OverwriteHmiData\":true}}")]
        [InlineData("RunPlcSimAdvancedTestScenario", "{\"scenario\":{\"instance\":\"PLC\",\"steps\":[{\"waitMs\":1.5}]}}")]
        [InlineData("InvokeUnifiedOpenPipe", "{\"request\":{\"Message\":\"ReadTag\",\"Params\":{}}}")]
        [InlineData("WritePlcWebVars", "{\"host\":\"unused\",\"username\":\"u\",\"password\":\"\",\"writes\":\"[]\"}")]
        [InlineData("WritePlcSimAdvancedTags", "{\"instanceName\":\"unused\",\"values\":[{\"Name\":\"A\",\"value\":true}]}")]
        public async Task DirectBridgeAndBatchRejectTypedShapesBeforeDispatch(string name, string json)
        {
            var result = Body(McpServer.CallTool(name, new ToolArguments(JsonSerializer.Deserialize<JsonElement>(json))));
            Assert.Equal("INVALID_ARGUMENT", (string?)result["error"]!["code"]);
            Assert.Equal("not-started", (string?)result["meta"]!["execution"]);
            var method = catalog.Methods.Single(p => p.Key == name).Value;
            var tool = McpServer.WithSchemaHints(ToolCatalog.CreateTool(method), name, method);
            var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ToolCatalogServerProxy>())
            { Params = new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } };
            Assert.Equal("INVALID_ARGUMENT", (string?)Body(await tool.InvokeAsync(request))["error"]!["code"]);
            var batch = Body(McpServer.ReadToolBatch(new[] { new ToolCall(name, new ToolArguments(JsonSerializer.Deserialize<JsonElement>(json))) }));
            Assert.Equal("INVALID_ARGUMENT", (string?)batch["error"]!["code"]);
            Assert.Equal(0, service.Calls);
        }

        [Fact]
        public void GeneratedExamplesMeetTheAdvertisedSchema()
        {
            foreach (var entry in catalog.Methods)
            {
                var body = Body(new ToolUsageTools().GetToolUsage(toolName: entry.Key));
                var arguments = JsonSerializer.SerializeToElement(body["data"]!["example"]!["request"]!["params"]!["arguments"]);
                var error = McpServer.ValidateV4Arguments(entry.Value, arguments, McpServer.ToolInputSchema(entry.Key, entry.Value));
                Assert.True(error == null, entry.Key + " " + arguments.GetRawText() + ": " + (error == null ? "" : V4Json.Serialize(error)));
            }
        }

        [Theory]
        [InlineData("WritePlcWebVars", "{\"host\":\"unused\",\"username\":\"u\",\"password\":\"\",\"writes\":[{\"name\":\"A\",\"value\":true,\"extra\":1}]}")]
        [InlineData("WriteUnifiedRuntimeTags", "{\"writes\":[{\"name\":\"A\",\"value\":true,\"extra\":1}]}")]
        [InlineData("WritePlcSimAdvancedTags", "{\"instanceName\":\"unused\",\"values\":[{\"name\":\"A\",\"value\":true,\"extra\":1}]}")]
        public async Task SharedWriteValueSchemaRejectsUnknownFieldsWithoutDispatch(string name, string json)
        {
            // Keep this regression at admission: a permissive schema must never
            // cause this offline test to open a runtime channel.
            var method = catalog.Methods.Single(p => p.Key == name).Value;
            var arguments = JsonSerializer.Deserialize<JsonElement>(json);
            Assert.NotNull(McpServer.ValidateV4Arguments(method, arguments, McpServer.ToolInputSchema(name, method)));
            await DirectBridgeAndBatchRejectTypedShapesBeforeDispatch(name, json);
            var batch = Body(McpServer.PreviewToolBatch(new[] { new ToolCall(name, new ToolArguments(arguments)) }, "unused"));
            Assert.Equal("INVALID_ARGUMENT", (string?)batch["error"]!["code"]);
            Assert.Equal("not-started", (string?)batch["meta"]!["execution"]);
        }

        [Fact]
        public void AdapterNeverReplaysAnAttemptedWrite()
        {
            var writes = new S7WebWriteResult();
            Assert.True(S7WebApiChannel.CanReplay(writes));
            writes.Items.Add(new S7WebWriteItem { WriteAttempted = true });
            Assert.False(S7WebApiChannel.CanReplay(writes));
            Assert.False(S7WebApiChannel.CanReplay(new S7WebModeChangeResult { RequestAttempted = true }));
        }

        public enum FakePrimitive { Bool }
        public sealed class FakeValue
        {
            public FakePrimitive Type;
            public bool Bool;
        }
        public sealed class FailingInstance
        {
            public int Writes;
            public int Refreshes;
            public FakeValue Read(string name) => new FakeValue { Type = FakePrimitive.Bool };
            public void Write(string name, FakeValue value)
            {
                Writes++;
                throw new InvalidOperationException("Tag not found after an unacknowledged write.");
            }
            public void UpdateTagList() => Refreshes++;
        }

        [Fact]
        public void PlcSimUnknownTagWriteIsIssuedOnceWithoutRefresh()
        {
            var instance = new FailingInstance();
            var api = new PlcSimApi { DataValue = typeof(FakeValue), PrimitiveDataType = typeof(FakePrimitive) };
            Assert.Throws<InvalidOperationException>(() => PlcSimAdvancedChannel.WriteWithRefresh(api, "fake", instance, "A", JsonValue.Create(true)));
            Assert.Equal(1, instance.Writes);
            Assert.Equal(0, instance.Refreshes);
        }

        [Fact]
        public void TypedInputsPreserveOrderAndOptionalDefaultsAtTheLegacyBoundary()
        {
            Assert.Equal(new[] { "M0.0", "DB1.DBX0.0" }, RuntimeTools.ParseItemSpecs(RuntimeToolContract.Names(new[] { " M0.0 ", "DB1.DBX0.0" }, "items", false)));
            Assert.Equal("[]", RuntimeToolContract.Names(null!, "systemNames", true));
            Assert.Equal("[\"StartScreen\",\"ScreenResolution\"]", RuntimeToolContract.Names(null!, "fields", true, new[] { "StartScreen", "ScreenResolution" }));
            Assert.Equal("[]", RuntimeToolContract.Names(Array.Empty<string>(), "fields", true, new[] { "StartScreen" }));
            Assert.Equal("{}", RuntimeToolContract.Answers(null!));
            var input = V4Json.Deserialize<WriteValue[]>("[{\"name\":\" A \",\"value\":true},{\"name\":\"B\",\"value\":12}]");
            var writes = RuntimeChannelsLogic.ParseWriteList(RuntimeToolContract.Writes(input, "writes", true, false), "writes");
            Assert.Equal(new[] { "A", "B" }, writes.Select(w => w.Name));
            Assert.Equal(true, writes[0].ClrValue);
        }

        [Fact]
        public void TypedChannelPoliciesRejectDuplicatesAndBudgetOverflowWithoutIo()
        {
            var tools = new RuntimeChannelTools();
            var duplicate = V4Json.Deserialize<WriteValue[]>("[{\"name\":\"A\",\"value\":true},{\"name\":\" A \",\"value\":false}]");
            Assert.Equal("INVALID_ARGUMENT", (string?)Body(tools.WriteUnifiedRuntimeTagsV4(duplicate))["error"]!["code"]);
            Assert.Equal("LIMIT_EXCEEDED", (string?)Body(tools.ReadUnifiedRuntimeTagsV4(Enumerable.Repeat("A", 501).ToArray()))["error"]!["code"]);
            Assert.Equal("CONFIRMATION_REQUIRED", (string?)Body(tools.WriteUnifiedRuntimeTagsV4(duplicate, dryRun: false))["error"]!["code"]);
            Assert.Equal("CONFIRMATION_REQUIRED", (string?)Body(tools.WritePlcWebVarsV4("unused", "u", "", duplicate, dryRun: false))["error"]!["code"]);
            Assert.Equal("CONFIRMATION_REQUIRED", (string?)Body(new PlcSimAdvancedTools().WritePlcSimAdvancedTagsV4("unused", duplicate, dryRun: false))["error"]!["code"]);
        }

        [Theory]
        [InlineData(true, false, "255", true)]
        [InlineData(false, true, "255", true)]
        [InlineData(false, false, "255", false)]
        [InlineData(true, false, "256", false)]
        [InlineData(false, true, "256", false)]
        public void WriteAdmissionAppliesConfirmationAndTheTargetType(bool dryRun, bool confirmWrite, string value, bool accepted)
        {
            var writes = V4Json.Deserialize<WriteValue[]>("[{\"name\":\" A \",\"value\":" + value + "}]");
            bool admitted = false;
            var body = Body(RuntimeToolContract.Run("WritePlcSimAdvancedTags", dryRun, true, () =>
            {
                RuntimeToolContract.Writes(writes, "values", dryRun, confirmWrite, name =>
                {
                    Assert.Equal("A", name);
                    return NativeValueValidator.Simulation("UInt8");
                });
                admitted = true;
                return new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
            }));
            Assert.Equal(accepted, admitted);
            if (!accepted)
            {
                Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]!["code"]);
                Assert.Equal("not-started", (string?)body["meta"]!["execution"]);
            }
        }

        [Fact]
        public void MappingPreservesDomainSummaryAndMarksSettingsMismatchAsPartial()
        {
            var compare = new ResponseCompare { Entries = Array.Empty<CompareEntry>(), Summary = new Dictionary<string, int> { ["Equal"] = 2 }, Message = "Compared" };
            Assert.Equal(2, (int)Body(RuntimeToolContract.Map("CompareSoftwareToOnline", compare, true, true))["data"]!["summary"]!["Equal"]!);
            var settings = new ResponseMessage { Meta = JsonNode.Parse("{\"success\":true,\"status\":\"ReadbackMismatch\",\"mayHaveChanged\":true,\"proposed\":{\"A\":1,\"B\":2},\"readback\":{\"A\":{\"value\":1},\"B\":{\"value\":3}}}")!.AsObject() };
            Assert.Equal("partial", (string?)Body(RuntimeToolContract.Map("SetUnifiedRuntimeSettings", settings, false, true))["meta"]!["outcome"]);
            var unknown = Body(RuntimeToolContract.Run("DownloadPlc", false, true, () => throw new global::ModelContextProtocol.McpException("native call failed", new ArgumentException("native failure"), global::ModelContextProtocol.McpErrorCode.InternalError)));
            Assert.Equal("unknown", (string?)unknown["meta"]!["outcome"]);
        }
    }
}
