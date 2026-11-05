extern alias enginehost;
extern alias foundationhost;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using TiaMcp.PlcWorker;
using TiaMcp.WorkerChannel;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Foundation = foundationhost::TiaMcp.FoundationHost;
using Xunit;

[Collection(BundleSelectionCollection.Name)]
public sealed class FixR1RegressionTests
{
    [Fact]
    public void Unclassified_contract_failures_still_use_the_worker_classification_and_retain_data()
    {
        var legacy = new CallToolResult { IsError = true, StructuredContent = new JsonObject { ["data"] = new JsonObject { ["retained"] = "fixture" } } };
        var mapped = FoundationV4Result.WorkerFailure(legacy, "21", "CreateDevice", "unclassified", true,
            new WorkerOperationException("Worker refused before operation", -32603, "rejected-before-operation"));
        Assert.Equal("rejected-before-operation", (string?)mapped.StructuredContent?["meta"]?["outcome"]);
        Assert.Equal("fixture", (string?)mapped.StructuredContent?["data"]?["retained"]);
        Assert.False(FoundationV4Result.Classified(new JsonArray()));
    }

    [Theory]
    [InlineData("rejected-before-operation", "partial", "PARTIAL_FAILURE")]
    [InlineData("unknown", "unknown", "OUTCOME_UNKNOWN")]
    public async Task Seed_keeps_confirmed_PLC_writes_and_the_later_HMI_failure_classification(string failure, string outcome, string code)
    {
        using var previewWorker = new WritePreviewCoverageTests.PlanWorker();
        previewWorker.Identity["identity"]!["projectPath"] = "C:/fixture.ap21";
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline("21", previewWorker, Array.Empty<McpServerTool>());
        using var previewSession = pipeline.EnterSession();
        string root = Path
            .Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory), "bin-build", "fix-r1-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "plc", "types"));
        Directory.CreateDirectory(Path.Combine(root, "hmi", "tags"));
        File
            .WriteAllText(Path.Combine(root, "plc", "types", "T.xml"),
                "<Document><Engineering version=\"V21\"/><SW.Types.PlcStruct " +
                "ID=\"0\"><AttributeList><Name>T</Name></AttributeList></SW.Types.PlcStruct></Document>");
        File.WriteAllText(Path.Combine(root, "hmi", "tags", "Tags.xml"), "<Document><Engineering version=\"V21\"/></Document>");
        using var worker = new SeedWorker(failure);
        try
        {
            var method = typeof(Host.PlcReadModifyTools).GetMethod("SeedProjectFromReferenceV4")!;
            var tool = new FoundationTool(worker, "21", method, "SeedProjectFromReference");
            var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>()) {
                Params = new() { Name = "SeedProjectFromReference", Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    JsonSerializer.Serialize(new { plcSoftwarePath = "PLC", hmiSoftwarePath = "HMI", referenceDir = root })) } };
            var preview = await tool.InvokeAsync(request);
            request.Params = new CallToolRequestParams { Name = "SeedProjectFromReference",
                Arguments = new Dictionary<string, JsonElement>(request.Params!.Arguments!) {
                    ["dryRun"] = JsonSerializer.SerializeToElement(false),
                    ["expectedPlanHash"] = JsonSerializer.SerializeToElement((string)preview.StructuredContent!["data"]!["planHash"]!),
                    ["expectedProjectFile"] = JsonSerializer.SerializeToElement(
                        (string)preview.StructuredContent!["data"]!["targetIdentity"]!["identity"]!["projectPath"]!)
                } };
            var result = await tool.InvokeAsync(request);
            Assert.Equal(outcome, (string?)result.StructuredContent?["meta"]?["outcome"]);
            Assert.Equal(code, (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal(outcome == "unknown", (bool?)result.StructuredContent?["meta"]?["requiresSessionReset"]);
            Assert.Contains("plc:type:T", result.StructuredContent!["data"]!["imported"]!.AsArray().Select(row => (string?)row));
            Assert.NotEmpty(result.StructuredContent["data"]!["failed"]!.AsArray());
            Assert.Equal(1, worker.PlcWrites);
        }
        finally
        {
            Assert
                .StartsWith(Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory), "bin-build") + Path
                    .DirectorySeparatorChar,
                Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(root, true);
        }
    }

    private sealed class SeedWorker(string failure) : IFoundationWorker
    {
        internal int PlcWrites;
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode
                ?>(new JsonObject { ["ProcessId"] = 42, ["IsAttached"] = true, ["ProjectFile"] = "C:/fixture.ap21" });
            if (operation.EndsWith("SeedReferenceHmi", StringComparison.Ordinal))
            {
                var error = new WorkerOperationException("HMI refused", -32603, failure);
                error.Data["foundationRequestSent"] = true;
                throw error;
            }
            Assert.EndsWith("ExecutePlcOrganisation", operation);
            PlcWrites++;
            return Task.FromResult<JsonNode?>(JsonSerializer.SerializeToNode(new PlcSoftwareReply { Found = true }));
        }
        public void Dispose() { }
    }

    [Fact]
    public void Native_diagnostics_and_both_evidence_paths_expose_only_a_redacted_first_line()
    {
        var inner = new IOException("Native failure at C:\\private\\project.ap21\n   at Secret.Inner() in C:\\private\\worker.cs:line 4");
        var nested = new TargetInvocationException("Outer wrapper", inner);
        var diagnostics = FoundationV4Result.NativeDiagnostics(new JsonObject { ["error"] = nested.ToString() })!;
        string wire = diagnostics.ToJsonString();
        Assert.DoesNotContain("Secret.Inner", wire); Assert.DoesNotContain("private", wire);
        Assert.DoesNotContain("\\n", wire); Assert.NotNull(diagnostics["exceptionType"]);
        var error = new WorkerOperationException(inner.Message, -32603, "read-failed", new JsonObject {
            ["exceptionType"] = nameof(IOException), ["nativeMessage"] = inner.ToString(),
            ["nested"] = new JsonObject { ["nativeMessage"] = inner.ToString(), ["stackTrace"] = "Secret.Inner" } }.ToJsonString());
        var body = FoundationV4Result.Failure("21", "ListPlcForceTables", "privacy", true, false, null, error).StructuredContent!;
        Assert.DoesNotContain("Secret.Inner", body.ToJsonString()); Assert.DoesNotContain("private", body.ToJsonString());
        Assert.Contains("<path>", (string)body["data"]!["evidence"]!["nativeMessage"]!);
        var data = new Host.ResponseMessage {
            Meta = new JsonObject { ["success"] = false, ["nativeMessage"] = inner.ToString(), ["stackTrace"] = "Secret.Inner" } };
        var plc = Host.PlcToolContract.Map("ListPlcForceTables", data, false, true).StructuredContent!;
        Assert.DoesNotContain("Secret.Inner", plc.ToJsonString()); Assert.DoesNotContain("private", plc.ToJsonString());
        Assert.Null(HostBehavior.FailureEvidence(nameof(InvalidOperationException), "Internal secret")["workerMessage"].GetString());
        Assert.Equal("IO failure", HostBehavior.FailureEvidence(nameof(IOException), "IO failure\nstack")["workerMessage"].GetString());
        const string code = "FUNCTION F : Void\nBEGIN\n    // input ---> output\n    #value := 1;\nEND_FUNCTION";
        var exported = new CallToolResult { StructuredContent = new JsonObject { ["data"] = new JsonObject { ["code"] = code, ["stackUsageBytes"] = 512 } } };
        Assert.Equal(code, (string?)FoundationV4Result.ReconcileWorker(exported, false, null).StructuredContent?["data"]?["code"]);
        Assert.Equal(512, (int?)FoundationV4Result.ReconcileWorker(exported, false, null).StructuredContent?["data"]?["stackUsageBytes"]);
    }

    [Fact]
    public async Task Portal_attachment_without_a_project_preserves_connection_process_and_channel_epoch()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
        TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter.21.dll"), "offline session fixture");
        using var worker = new WorkerBackendFixture(new Foundation.HostOptions { ReleaseKey = "21", BundleRoot = AppContext.BaseDirectory,
            WorkerExe = exe, ApiDirectory = AppContext.BaseDirectory, NativeEnabled = true, BundledWorker = false });
        await worker.Call("Attach", new JsonObject { ["processId"] = Environment.ProcessId }, CancellationToken.None);
        await worker.Call("FixtureNoProject", new JsonObject(), CancellationToken.None);
        var snapshot = await worker.Status(CancellationToken.None);
        Assert.True((bool?)snapshot["session"]?["isConnected"]);
        Assert.True((bool?)snapshot["sessionState"]?["connected"]);
        Assert.Null(snapshot["binding"]);
        var client = (Foundation.IFoundationWorkerSupervisor)worker.Client;
        var panel = Foundation.SharedSessionState.For((Foundation.IFoundationWorker)worker.Client).OperatorSnapshot("21");
        Assert.Equal(Environment.ProcessId, (int?)panel["binding"]?["processId"]);
        Assert.Equal((long?)client.OperatorSnapshot()["binding"]?["bindingEpoch"], (long?)panel["binding"]?["bindingEpoch"]);
        Assert.NotNull(panel["binding"]?["bindingEpoch"]);
        Assert.Null(panel["binding"]?["projectFile"]);
    }

    [Fact]
    public async Task Family_synchronize_lock_is_reflected_in_the_reply_and_next_call_reset_refusal()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
        TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter.21.dll"), "offline session fixture");
        using var worker = new WorkerClient("21", exe, AppContext.BaseDirectory, true) { SharedSession = true };
        await worker.Call("Attach", new JsonObject { ["processId"] = Environment.ProcessId }, CancellationToken.None);
        await worker.Call("FixtureLockOnNextFamily", new JsonObject(), CancellationToken.None);
        var reply = await worker
            .Call("plc-software.ExecutePlcOrganisation", new JsonObject {
                ["request"] = new JsonObject { ["Operation"] = "listGroups", ["SoftwarePath"] = "PLC" } }, CancellationToken.None);
        Assert.False((bool?)reply?["RequiresSessionReset"] == true);
        var original = FoundationV4Result.Failure("21", "ListPlcForceTables", "family", true, false, null,
            new WorkerOperationException("Native family read failed", -32603, "read-failed"));
        var result = FoundationV4Result.ReconcileWorker(original, SharedSessionState.For(worker).RequiresReset(reply), null);
        Assert.Equal("read-failed", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.True((bool?)result.StructuredContent?["meta"]?["requiresSessionReset"]);
        Assert.True((bool?)SharedSessionState.For(worker).Snapshot("21")["requiresSessionReset"]);
        var next = new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "ListPlcBlocks"), worker);
        var refusal = await next
            .InvokeV4Async(new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>()) {
            Params = new() { Name = "ListPlcBlocks", Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"softwarePath\":\"PLC\"}") } },
                "21", "next", CancellationToken.None);
        Assert.Equal("SESSION_RESET_REQUIRED", (string?)refusal.StructuredContent?["error"]?["code"]);
        Assert.True((bool?)refusal.StructuredContent?["meta"]?["requiresSessionReset"]);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Snapshot_during_slow_disconnect_or_disposal_returns_without_waiting_for_worker_exit(bool dispose)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
        TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter.21.dll"), "offline session fixture");
        string marker = Path.Combine(AppContext.BaseDirectory, "fix-r1-stopping-" + Guid.NewGuid().ToString("N"));
        using var worker = new WorkerClient("21", exe, AppContext.BaseDirectory, true);
        try
        {
            await worker.Call("Attach", new JsonObject { ["processId"] = Environment.ProcessId }, CancellationToken.None);
            await worker.Call("FixtureSlowShutdown", new JsonObject { ["marker"] = marker }, CancellationToken.None);
            var channel = (ChannelClient)typeof(WorkerClient).GetField("channel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker)!;
            channel.Dispose();
            Task stop = dispose ? Task.Run(worker.Dispose) : Task
                .Run(async () => { await worker.Call("Disconnect", new JsonObject(), CancellationToken.None); });
            Assert.True(SpinWait.SpinUntil(() => File.Exists(marker), TimeSpan.FromSeconds(5)));
            var clock = Stopwatch.StartNew();
            var snapshot = worker.OperatorSnapshot();
            while ((bool?)snapshot["stopping"] != true && !stop.IsCompleted)
            {
                await Task.Yield(); snapshot = worker.OperatorSnapshot();
            }
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), "Managed snapshot waited for worker shutdown: " + clock.Elapsed);
            Assert.True((bool?)snapshot["stopping"]);
            Assert.Equal("Stopping", (string?)worker.SupervisorSnapshot(new JsonObject())["state"]);
            await stop;
        }
        finally { if (File.Exists(marker)) File.Delete(marker); }
    }
}
