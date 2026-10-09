extern alias enginehost;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace PlcExchangeTests
{
    public sealed class PlcExchangeContractsTests : IDisposable
    {
        private static readonly Assembly Host = typeof(enginehost::TiaMcp.FoundationHost.EngineHostPipeline).Assembly;
        private static Type HostType(string name) => Host.GetType(name, true)!;
        public PlcExchangeContractsTests()
        {
            var configuration = HostType("TiaMcp.FoundationHost.EngineHostConfiguration");
            configuration.GetProperty("Worker", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, new HostWorker());
            configuration.GetField("ReleaseKey", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null,
                Engine.GetName().Name!.EndsWith("V20", StringComparison.Ordinal) ? "20" : "21");
        }
        public void Dispose()
        {
            var context = HostType("TiaMcp.FoundationHost.EngineHostConfiguration").GetProperty("Current", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            ((IDisposable)context.GetType().GetProperty("Exports", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!).Dispose();
        }
        private sealed class HostWorker : enginehost::TiaMcp.FoundationHost.IEngineWorker
        {
            public bool Faulted => false;
            public JsonNode? Binding => null;
            public object SessionKey { get; } = new();
            public JsonObject Snapshot() => new();
            public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
            public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new NotSupportedException();
            public Task<IDisposable> Acquire(CancellationToken token) => throw new NotSupportedException();
            public Task<enginehost::TiaMcp.FoundationHost.EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token) => throw new NotSupportedException();
            public void Dispose() { }
        }
        private static string Park(string tool, string target, string content)
        {
            var result = ((string id, ExportSlice head))HostType("TiaMcp.FoundationHost.SessionExportStore")
                .GetMethod("PutAndSlice", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { tool, target, content, 4 })!;
            return result.id;
        }
        public static int Main() => 2;
        static PlcExchangeContractsTests()
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
        private static readonly Assembly Engine = typeof(McpServer).Assembly;
        private static Type Type(string name) => Engine.GetType("TiaMcpServer.ModelContextProtocol." + name, true)!;
        private static readonly string[] Owners = { "DocumentsTools", "NativeExchangeTools", "PlcExternalSourcesTools" };
        private static Dictionary<string, MethodInfo> Entries() => Owners.SelectMany(n => Type(n).GetMethods())
            .Concat(HostType("TiaMcpServer.ModelContextProtocol.ExportTools").GetMethods())
            .Concat(HostType("TiaMcpServer.ModelContextProtocol.PlcOfflineTools").GetMethods()
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == "WritePlcSclSourceFile"))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
            .ToDictionary(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name!, m => m);
        // These admission cases must return before using either injected native
        // dependency. Avoid resolving Siemens constructor signatures in CI.
        private static object Instance(Type type) => System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
        private static JsonObject Body(CallToolResult result)
        {
            Assert.Single(result.Content);
            var body = JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)!.AsObject();
            Assert.True(JsonNode.DeepEquals(body, result.StructuredContent));
            Assert.Equal(!(bool)body["ok"]!, result.IsError);
            return body;
        }
        private static JsonObject Invoke(string name, params object?[] args)
        {
            var method = Entries()[name];
            var parameters = method.GetParameters();
            var values = parameters.Select((p, i) => i < args.Length ? args[i] : p.DefaultValue).ToArray();
            return Body((CallToolResult)method.Invoke(Instance(method.DeclaringType!), values)!);
        }
        private static object SharedMapContract() => typeof(V4Json).Assembly.GetType("TiaMcp.Logic.V4.Inputs.TypedToolInput", true)!
            .GetMethod("For", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { typeof(AttributeMap<Scalar>) })!;

        [Fact]
        public void RenamedCrossReferencesRemainBatchOrchestration()
        {
            var method = Entries()["GetPlcCrossReferences"];
            Assert.Equal("GetCrossReferencesV4", method.Name);
            var classify = typeof(McpServer).GetMethod("IsBatchOrchestration", BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(MethodInfo) }, null)!;
            Assert.True((bool)classify.Invoke(null, new object[] { method })!);
            Assert.False((bool)classify.Invoke(null, new object[] { Entries()["ListExportHandles"] })!);
            var target = new ToolTarget("GetPlcCrossReferences", new InputSchema(JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\"}")),
                new InputBudget(), read: true, preview: true, orchestration: true);
            var call = new ToolCall("GetPlcCrossReferences", new ToolArguments(JsonSerializer.Deserialize<JsonElement>("{}")));
            foreach (var mode in new[] { ToolCallMode.ReadBatch, ToolCallMode.PreviewBatch })
                Assert.Equal(ErrorCode.InvalidArgument, ToolCallValidator.Create(mode, new[] { target }).Validate(new[] { call }, "operations").Error!.Code);
        }

        [Fact]
        public void ExportToolsRemainProjectExemptAndAreNotReparked()
        {
            var guard = typeof(McpServer).GetMethod("IsExportTool", BindingFlags.Static | BindingFlags.NonPublic)!;
            foreach (var name in new[] { "GetExportContent", "ListExportHandles", "SaveExportContent", "DeleteExportHandle", "ClearExportHandles" })
                foreach (var spelling in new[] { name, name.ToLowerInvariant() })
                {
                    Assert.False(PreflightLogic.NeedsProject("FILE", spelling));
                    Assert.True((bool)guard.Invoke(null, new object[] { spelling })!);
                }
            foreach (var name in new[] { "GetExport", "ListExports", "SaveExport", "DeleteExport", "ClearExports", "GetBlocks" })
            {
                Assert.True(PreflightLogic.NeedsProject("FILE", name));
                Assert.False((bool)guard.Invoke(null, new object[] { name })!);
            }
        }

        [Fact]
        public async Task ParentSupervisorAdmitsRenamedExportReadsDuringRebind()
        {
            var supervisorType = Engine.GetType("TiaMcpServer.Isolation.OpennessWorkerSupervisor", true)!;
            var constructor = supervisorType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var call = supervisorType.GetMethod("CallAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var name in new[] { "GetExportContent", "ListExportHandles", "SaveExportContent", "DeleteExportHandle", "ClearExportHandles", "GetExport", "ListExports" })
                foreach (bool bridge in new[] { false, true })
                {
                    int starts = 0;
                    Func<ProcessStartInfo> start = () => { starts++; throw new InvalidOperationException("Offline start sentinel; no process is launched."); };
                    using var supervisor = (IDisposable)constructor.Invoke(new object[] { start, 21, "fixture", Array.Empty<string>(), TimeSpan.FromSeconds(1) });
                    supervisorType.GetField("bindingRequired", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(supervisor, true);
                    var parameters = new JsonObject { ["name"] = name.ToLowerInvariant(), ["arguments"] = new JsonObject() };
                    if (bridge) parameters = new JsonObject { ["name"] = "CallTool", ["arguments"] = parameters };
                    var task = (Task<JsonObject>)call.Invoke(supervisor, new object?[] { parameters, null, CancellationToken.None })!;
                    var error = await Assert.ThrowsAnyAsync<Exception>(() => task);
                    Assert.Equal("WorkerCallException", error.GetType().Name);
                    bool diagnostic = name == "GetExportContent" || name == "ListExportHandles";
                    Assert.Equal(diagnostic ? 1 : 0, starts);
                    Assert.StartsWith(diagnostic ? "Worker could not start/validate" : "Recovery requires an explicit", error.Message);
                }
        }

        [Fact]
        public void SharedMapFamilyAndOptionalParameterNeedNoRequestContext()
        {
            var parameters = Entries()["ManagePlcTagDefinition"].GetParameters();
            Assert.DoesNotContain(parameters, p => p.Name == "context");
            var parameter = parameters.Single(p => p.Name == "properties");
            Assert.Equal(typeof(AttributeMap<Scalar>), parameter.ParameterType);
            Assert.True(parameter.HasDefaultValue);
            Assert.Null(parameter.DefaultValue);
            var contract = SharedMapContract();
            var schema = (JsonElement)contract.GetType().GetProperty("Schema", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(contract)!;
            Assert.Equal("object", schema.GetProperty("type").GetString());
            Assert.True(schema.TryGetProperty("additionalProperties", out var values));
            Assert.NotEqual(JsonValueKind.False, values.ValueKind);
            Assert.False(schema.TryGetProperty("default", out _));
        }

        [Theory]
        [InlineData("null", false)]
        [InlineData("\"{}\"", false)]
        [InlineData("{\"Comment\":{\"en-US\":\"Motor\"}}", false)]
        [InlineData("{}", true)]
        [InlineData("{\"Comment\":\"Motor\"}", true)]
        [InlineData("{\"ExternalWritable\":true}", true)]
        public void SharedMapFamilyValidatesShapeBeforeDomainAttributeRules(string json, bool valid)
        {
            var contract = SharedMapContract();
            var error = (Error?)contract.GetType().GetMethod("Validate", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(contract, new object[] { JsonSerializer.Deserialize<JsonElement>(json), "properties" });
            if (valid) Assert.Null(error);
            else Assert.Equal(ErrorCode.InvalidArgument, error!.Code);
        }

        private static JsonObject Map(string json, bool write = true)
        {
            var response = new ResponseMessage { Message = "Retained domain summary.", Meta = JsonNode.Parse(json)!.AsObject() };
            return Body((CallToolResult)Type("PlcExchangeContract").GetMethod("Map", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object?[] { "ImportPlcBlocksDocuments", response, write, true, null })!);
        }
        private static void Verdict(JsonObject body, string outcome, string execution)
        {
            Assert.Equal(4, (int)body["schemaVersion"]!);
            Assert.Equal(outcome, (string?)body["meta"]!["outcome"]);
            Assert.Equal(execution, (string?)body["meta"]!["execution"]);
            Assert.Equal(outcome == "succeeded", (bool)body["ok"]!);
        }

        [Theory]
        [InlineData("ExportAsDocuments", "ExportPlcBlockDocuments")]
        [InlineData("ExportBlocksAsDocuments", "ExportPlcBlocksDocuments")]
        [InlineData("ImportFromDocuments", "ImportPlcBlockDocuments")]
        [InlineData("ImportBlocksFromDocuments", "ImportPlcBlocksDocuments")]
        [InlineData("GetExport", "GetExportContent")]
        [InlineData("ListExports", "ListExportHandles")]
        [InlineData("SaveExport", "SaveExportContent")]
        [InlineData("DeleteExport", "DeleteExportHandle")]
        [InlineData("ClearExports", "ClearExportHandles")]
        [InlineData("GetCrossReferences", "GetPlcCrossReferences")]
        [InlineData("GetPlcExternalSources", "ListPlcExternalSources")]
        [InlineData("ReadPlcSystemGroups", "ListPlcSystemGroups")]
        public void RegistersOneV4NameWithoutAlias(string oldName, string name)
        {
            var entries = Entries();
            Assert.Equal(25, entries.Count);
            Assert.Contains(name, entries.Keys);
            Assert.DoesNotContain(oldName, entries.Keys);
            Assert.All(entries.Values, method => Assert.True(method.ReturnType == typeof(CallToolResult)
                || method.ReturnType == typeof(System.Threading.Tasks.Task<CallToolResult>)));
        }

        [Theory]
        [InlineData("[]", "INVALID_ARGUMENT")]
        [InlineData("[\"\"]", "INVALID_ARGUMENT")]
        [InlineData("[\"A\",\"a\"]", "INVALID_ARGUMENT")]
        [InlineData("[null]", "INVALID_ARGUMENT")]
        public void ExactPathRejectionsOccurWithoutASession(string json, string code)
        {
            var paths = JsonSerializer.Deserialize<string[]>(json)!;
            var body = Invoke("GeneratePlcSourceFromBlocks", "PLC", paths, "C:\\unused.scl");
            Verdict(body, "rejected-before-operation", "not-started");
            Assert.Equal(code, (string?)body["error"]!["code"]);
        }

        [Fact]
        public void ExactPathBudgetsAndCaseInsensitiveUniquenessSurvive()
        {
            var contract = (InputContract<string[]>)Type("PlcExchangeContract").GetField("Paths", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Assert.Null(contract.Read("[\"Group/A\",\"Group/B\"]", "blockPaths").Error);
            Assert.NotNull(contract.Read("\"[\\\"A\\\"]\"", "blockPaths").Error);
            Assert.Equal(ErrorCode.LimitExceeded, contract.Validate(new[] { new string('x', 65537) }, "blockPaths").Error!.Code);
            Assert.NotNull(contract.Validate(Enumerable.Range(0, 501).Select(i => "B" + i).ToArray(), "blockPaths").Error);
        }

        [Theory]
        [InlineData("{\"ExternalWritable\":true}", true)]
        [InlineData("{\"Comment\":\"Motor\"}", true)]
        [InlineData("{\"Comment\":{\"en-US\":\"Motor\"}}", false)]
        [InlineData("{\"ExternalWritable\":\"true\"}", false)]
        [InlineData("{\"externalWritable\":true}", false)]
        [InlineData("{\"Name\":\"renamed\"}", false)]
        [InlineData("{\"ExternalWritable\":true,\"ExternalWritable\":false}", false)]
        [InlineData("null", false)]
        [InlineData("\"{}\"", false)]
        public void ScalarTagMapIsClosedAndDoesNotDecodeStrings(string json, bool valid)
        {
            var contract = (InputContract<AttributeMap<Scalar>>)Type("PlcExchangeContract").GetMethod("TagProperties", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { "tag" })!;
            Assert.Equal(valid, contract.Read(json, "properties").IsValid);
        }

        [Theory]
        [InlineData("{\"success\":true,\"exportedBlocks\":1,\"skippedBlocks\":1}", "partial", "partial")]
        [InlineData("{\"success\":false,\"mayHaveChanged\":true,\"attemptedFiles\":2,\"succeededFiles\":1}", "unknown", "unknown")]
        [InlineData("{\"success\":true,\"outcomeUnknown\":true}", "unknown", "unknown")]
        [InlineData("{\"success\":true,\"operationSuccess\":false,\"mayHaveChanged\":true}", "unknown", "unknown")]
        [InlineData("{\"success\":false,\"status\":\"InvalidState\"}", "rejected-before-operation", "not-started")]
        [InlineData("{\"success\":true,\"contentVerified\":null}", "succeeded", "completed")]
        [InlineData("{\"success\":true,\"outcome\":\"partial\"}", "partial", "partial")]
        [InlineData("{}", "unknown", "unknown")]
        public void IndependentVerdictsNeverHidePartialOrUnknown(string meta, string outcome, string execution)
        {
            var body = Map(meta);
            Verdict(body, outcome, execution);
            Assert.Equal("current", (string?)body["meta"]!["behaviorPolicy"]);
            Assert.Equal(outcome == "unknown", (bool)body["meta"]!["requiresSessionReset"]!);
        }

        [Theory]
        [InlineData(false, "rejected-before-operation", "not-started")]
        [InlineData(true, "read-failed", "read-only")]
        public void CrossReferenceRefusalIsNotAnEmptySuccess(bool queried, string outcome, string execution)
        {
            var body = Map("{\"success\":false,\"queried\":" + (queried ? "true" : "false") + "}", false);
            Verdict(body, outcome, execution);
            Assert.Equal(queried ? "NATIVE_OPERATION_FAILED" : "PRECONDITION_FAILED", (string?)body["error"]!["code"]);
        }

        [Fact]
        public void ObservationNativeResultAndContentVerificationRemainDistinct()
        {
            var body = Map("{\"success\":true,\"contentVerified\":null,\"nativeResult\":null,\"observation\":{\"source\":\"inventory\",\"names\":[\"FB_A\"]}}");
            Assert.Equal("inventory", (string?)body["data"]!["observation"]!["source"]);
            Assert.Null(body["data"]!["nativeResult"]);
            Assert.Equal("unknown", (string?)body["meta"]!["completeness"]);
            Assert.Contains(body["meta"]!["warnings"]!.AsArray(), w => (string?)w!["code"] == "INCOMPLETE_DATA");
        }

        [Fact]
        public void ReadAdmissionAndTruncatedSnapshotsAreNotCompleteReads()
        {
            Verdict(Map("{\"success\":false,\"status\":\"InvalidState\"}", false), "rejected-before-operation", "not-started");
            var body = Map("{\"success\":true,\"systemBlockGroups\":[{\"groupCount\":1,\"groupsTruncated\":true}]}", false);
            Verdict(body, "succeeded", "read-only");
            Assert.Equal("partial", (string?)body["meta"]!["completeness"]);
        }

        [Fact]
        public void ScopedFailureEvidenceSurvivesWithoutReplayingTheAction()
        {
            var adapter = Type("PlcExchangeContract");
            int calls = 0;
            Func<ResponseMessage> action = () => {
                calls++;
                adapter.GetMethod("StartWrite", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { "generation" });
                throw new IOException("untrusted native detail");
            };
            var result = (CallToolResult)adapter.GetMethod("Run", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { "GenerateBlocksFromExternalSource", action, true, true })!;
            var body = Body(result);
            Verdict(body, "unknown", "unknown");
            Assert.Equal(1, calls);
            Assert.Equal(1, (int)body["error"]!["details"]!["evidence"]!["executionEvidence"]!["issued"]!);
            Assert.DoesNotContain("untrusted native detail", body.ToJsonString());
        }

        [Fact]
        public void ReportingFailureAfterConfirmedImportsKeepsKnownPartialResults()
        {
            var adapter = Type("PlcExchangeContract");
            Func<ResponseMessage> action = () => {
                adapter.GetMethod("StartWrite", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { "import" });
                adapter.GetMethod("ConfirmWrite", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
                return new ResponseMessage { Meta = JsonNode.Parse("{\"success\":false,\"mayHaveChanged\":true,\"attemptedFiles\":1,\"succeededFiles\":1,\"responseReportingFailed\":true}")!.AsObject() };
            };
            var result = (CallToolResult)adapter.GetMethod("Run", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { "ImportPlcBlocksDocuments", action, true, true })!;
            Verdict(Body(result), "partial", "partial");
        }

        [Fact]
        public void ExportLifecyclePreservesTextAndFileBytesAndRejectsOverwrite()
        {
            ExportStore.Clear(0);
            var directory = Path.Combine(Path.GetTempPath(), "P6-11-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var entry = ExportStore.Get(Park("Fixture", "target", "Hello 中文"))!;
                var page = Invoke("GetExportContent", entry.Id, 0, 3);
                Assert.Equal("Hel", (string?)page["data"]!["text"]);
                Assert.Equal(3, (int)page["meta"]!["paging"]!["nextOffset"]!);
                Assert.Equal(64, ((string)page["data"]!["export"]!["sha256"]!).Length);
                Assert.Equal("text/plain", (string?)page["data"]!["export"]!["mediaType"]);
                Assert.Equal(12, (long)page["data"]!["export"]!["byteLength"]!);
                Assert.Equal(entry.CreatedUtc.AddHours(24), DateTime.Parse((string)page["data"]!["export"]!["expiresUtc"]!).ToUniversalTime());
                var path = Path.Combine(directory, "result.txt");
                Verdict(Invoke("SaveExportContent", entry.Id, path), "succeeded", "completed");
                Assert.Equal("Hello 中文", File.ReadAllText(path));
                Assert.Equal(new byte[] { 239, 187, 191 }, File.ReadAllBytes(path).Take(3));
                Assert.Equal("ALREADY_EXISTS", (string?)Invoke("SaveExportContent", entry.Id, path)["error"]!["code"]);
                Verdict(Invoke("DeleteExportHandle", entry.Id), "succeeded", "completed");
                Assert.Equal("NOT_FOUND", (string?)Invoke("GetExportContent", entry.Id)["error"]!["code"]);
            }
            finally { ExportStore.Clear(0); Directory.Delete(directory, true); }
        }

        [Fact]
        public void ExportHandleLimitRetainsTheMatchingCountAndPartialCompleteness()
        {
            ExportStore.Clear(0);
            try
            {
                Park("Fixture", "first", "one");
                Park("Fixture", "second", "two");
                Park("Other", "third", "three");
                var body = Invoke("ListExportHandles", "Fixture", 1);
                Verdict(body, "succeeded", "read-only");
                Assert.Equal("partial", (string?)body["meta"]!["completeness"]);
                Assert.Equal(2, (int)body["data"]!["matchingCount"]!);
                Assert.Single(body["data"]!["items"]!.AsArray());
                Assert.Equal("complete", (string?)Invoke("ListExportHandles", "Fixture", 2)["meta"]!["completeness"]);
            }
            finally { ExportStore.Clear(0); }
        }

        [Fact]
        public void EmptySclIsRejectedWithTheInputErrorBeforeCreatingAFile()
        {
            var body = Invoke("WritePlcSclSourceFile", "");
            Verdict(body, "rejected-before-operation", "not-started");
            Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]!["code"]);
        }

        [Theory]
        [InlineData("GetExportContent", -1)]
        [InlineData("ListExportHandles", 0)]
        [InlineData("ClearExportHandles", -1)]
        public void LifecycleRejectsInvalidPagingAndAges(string tool, int value)
        {
            var body = tool == "ClearExportHandles" ? Invoke(tool, value) : Invoke(tool, "missing", value);
            Verdict(body, "rejected-before-operation", "not-started");
            Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]!["code"]);
        }
    }
}
