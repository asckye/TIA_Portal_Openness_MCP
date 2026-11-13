extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using Xunit;
using UnsettledCalls = enginehost::TiaMcp.FoundationHost.UnsettledCalls;

namespace TiaMcp.FoundationHost.Tests;

public sealed class NoLockTests
{
    public static IEnumerable<object[]> Releases => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });

    private sealed class Worker(string release) : IFoundationSessionWorker
    {
        internal bool UnknownNextWrite;
        internal string? UnknownNextOperation;
        internal SemaphoreSlim? Serial;
        internal Func<string, JsonObject, Task<JsonNode?>>? Dispatch;
        public bool Faulted { get; set; }
        internal int Writes, Reads;
        internal string Project = "C:/fixture.ap" + release;
        public UnsettledCalls Unsettled { get; } = new();
        public string ApprovalIdentity => new JsonObject { ["processId"] = 123, ["projectPath"] = Project }.ToJsonString();
        public bool Bundled => false;
        public bool SharedSession => false;
        public TiaMcp.Logic.ModelContextProtocol.ImportStagingSession? StagingOwner => null;
        public IDisposable? EnterRequest(RequestContext<CallToolRequestParams> request) => null;
        public async Task<IDisposable?> AcquireLane(CancellationToken token)
        {
            if (Serial == null) return null;
            await Serial.WaitAsync(token);
            return new Lane(Serial);
        }
        private sealed class Lane(SemaphoreSlim serial) : IDisposable { public void Dispose() => serial.Release(); }
        public void ActivateLane(IDisposable? lane) { }
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
        {
            if (Dispatch != null) return Dispatch(operation, args);
            if (operation == UnknownNextOperation)
            {
                UnknownNextOperation = null;
                var failure = new WorkerOperationException("Synthetic unknown result.", -32603, "unknown");
                failure.Data["foundationPriorMutation"] = true;
                throw failure;
            }
            if (operation is "plc-paging.ReadBlockPage" or "ReadBlocks")
            {
                Reads++;
                return Task.FromResult<JsonNode?>(new JsonObject { ["Items"] = new JsonArray(), ["Offset"] = 0,
                    ["Total"] = 0, ["NextCursor"] = null });
            }
            if (operation == "ReadState")
                return Task.FromResult<JsonNode?>(new JsonObject { ["ReleaseKey"] = release, ["IsAttached"] = true,
                    ["ProcessId"] = 123, ["ProjectFile"] = Project, ["IsLocalSession"] = false });
            if (operation == "project-lifecycle.ReadProjectLifecycleBinding")
                return Task.FromResult<JsonNode?>(new JsonObject { ["identity"] = new JsonObject { ["projectPath"] = Project } });
            if (operation == "project-lifecycle.ArchiveSaveProjectCopy")
            {
                Writes++;
                Project = "C:/copy/fixture.ap" + release;
                return Task.FromResult<JsonNode?>(JsonValue.Create(true));
            }
            bool preview = (bool?)args["dryRun"] != false;
            if (!preview)
            {
                Writes++;
                if (UnknownNextWrite)
                {
                    UnknownNextWrite = false;
                    throw new WorkerOperationException("Synthetic write outcome is unknown.", -32603, "unknown");
                }
            }
            return Task.FromResult<JsonNode?>(new JsonObject { ["Executed"] = !preview, ["ProjectFile"] = Project });
        }
        public void Dispose() => Unsettled.Clear();
    }

    private static RequestContext<CallToolRequestParams> Request(string name, JsonObject args) => new(
        DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>()) {
        Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.ToJsonString()) } };

    private static FoundationV4Tool Tool(Worker worker, string release, string name)
        => new(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == name), worker), release, () => new(false, 1));
    private static JsonObject WriteArgs(Worker worker, bool preview = false) => new() {
        ["dryRun"] = preview, ["confirm"] = !preview, ["expectedProjectFile"] = worker.Project };
    private static JsonNode Body(CallToolResult result) => result.StructuredContent!;

    private static async Task<IDisposable> ReadUnknown(IFoundationWorker worker, string release, string id)
    {
        var services = FoundationHostPorts.CreateServices(worker, release, () => new JsonObject { ["ready"] = true }, null);
        var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline(release, services, Array.Empty<McpServerTool>());
        var lookup = pipeline.AllTools.Single(tool => tool.ProtocolTool.Name == "GetCallResult");
        var result = Body(await lookup.InvokeAsync(Request("GetCallResult", new() { ["requestId"] = id })));
        Assert.Equal("unknown", (string?)result["meta"]?["outcome"]);
        Assert.Equal(id, (string?)result["meta"]?["requestId"]);
        return pipeline;
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Unknown_write_leaves_the_next_read_and_write_running(string release)
    {
        using var worker = new Worker(release) { UnknownNextWrite = true };
        var save = Tool(worker, release, "SaveProject");
        var unknown = Body(await save.InvokeAsync(Request("SaveProject", WriteArgs(worker))));
        Assert.Equal("unknown", (string?)unknown["meta"]?["outcome"]);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)unknown["error"]?["code"]);
        var read = Body(await Tool(worker, release, "ListPlcBlocks").InvokeAsync(Request("ListPlcBlocks", new() { ["softwarePath"] = "PLC" })));
        Assert.True((bool?)read["ok"], read.ToJsonString());
        var refused = Body(await save.InvokeAsync(Request("SaveProject", WriteArgs(worker))));
        Assert.Equal("same-call-unsettled", (string?)refused["error"]?["details"]?["condition"]);
        Assert.Equal(1, worker.Writes);
        using var lookup = await ReadUnknown(worker, release, (string)unknown["meta"]!["requestId"]!);
        var following = Body(await save.InvokeAsync(Request("SaveProject", WriteArgs(worker))));
        Assert.True((bool?)following["ok"], following.ToJsonString());
        var warning = Assert.Single(following["meta"]!["warnings"]!.AsArray(), item => (string?)item?["code"] == "UNSETTLED_CALLS")!;
        Assert.Single(warning["details"]!["unsettled"]!.AsArray());
        Assert.Equal(2, worker.Writes);
        Assert.Equal(1, worker.Reads);
        Assert.Single(worker.Unsettled.Snapshot());
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Unknown_preview_does_not_guard_the_following_real_write(string release)
    {
        using var worker = new Worker(release) { UnknownNextOperation = "SaveProject" };
        var save = Tool(worker, release, "SaveProject");
        var preview = Body(await save.InvokeAsync(Request("SaveProject", WriteArgs(worker, preview: true))));
        Assert.Equal("unknown", (string?)preview["meta"]?["outcome"]);
        var following = Body(await save.InvokeAsync(Request("SaveProject", WriteArgs(worker))));
        Assert.True((bool?)following["ok"], following.ToJsonString());
        Assert.Equal(1, worker.Writes);
        Assert.Empty(worker.Unsettled.Snapshot());
        Assert.Empty(((IFoundationSessionWorker)worker).LateResults.Snapshot());
        Assert.Null(((IFoundationSessionWorker)worker).LateResults.FindUnread(
            UnsettledCalls.WriteDigest("SaveProject", WriteArgs(worker), worker.Project)));
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Unknown_read_does_not_enter_the_unread_write_store(string release)
    {
        using var worker = new Worker(release) { UnknownNextOperation = "plc-paging.ReadBlockPage" };
        var arguments = new JsonObject { ["softwarePath"] = "PLC" };
        var read = Body(await Tool(worker, release, "ListPlcBlocks").InvokeAsync(Request("ListPlcBlocks", arguments)));
        Assert.Equal("unknown", (string?)read["meta"]?["outcome"]);
        Assert.Null(((IFoundationSessionWorker)worker).LateResults.FindUnread(
            UnsettledCalls.WriteDigest("ListPlcBlocks", arguments, worker.Project)));
        Assert.Empty(((IFoundationSessionWorker)worker).LateResults.Snapshot());
        Assert.Empty(worker.Unsettled.Snapshot());
        var following = Body(await Tool(worker, release, "SaveProject").InvokeAsync(Request("SaveProject", WriteArgs(worker))));
        Assert.True((bool?)following["ok"], following.ToJsonString());
        Assert.Equal(1, worker.Writes);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task No_replay_after_unknown(string release)
    {
        using var worker = new Worker(release) { UnknownNextWrite = true };
        var save = Tool(worker, release, "SaveProject");
        await save.InvokeAsync(Request("SaveProject", WriteArgs(worker)));
        await Tool(worker, release, "ListPlcBlocks").InvokeAsync(Request("ListPlcBlocks", new() { ["softwarePath"] = "PLC" }));
        await save.InvokeAsync(Request("SaveProject", WriteArgs(worker, preview: true)));
        Assert.Equal(1, worker.Writes);
        Assert.Equal(1, worker.Reads);
    }

    [Theory]
    [InlineData("\"true\"")] [InlineData("\"false\"")] [InlineData("1")] [InlineData("null")] [InlineData("{}")] [InlineData("[]")]
    public void Non_boolean_dry_run_keeps_reviewed_writes_and_unknown_evidence(string dryRun)
    {
        using var scope = new SharedPipelineTests.Scope();
        var arguments = new JsonObject { ["dryRun"] = JsonNode.Parse(dryRun), ["expectedInventoryHash"] = new string('a', 64) };
        Assert.True(UnsettledCalls.IsReviewedApply(arguments));
        var result = FoundationV4Result.Failure("21", "SaveProject", "invalid-dry-run", true, true, null,
            new WorkerOperationException("Synthetic write outcome unknown.", -32603, "unknown"));
        enginehost::TiaMcpServer.ModelContextProtocol.McpServer.ObserveSessionOutcome("SaveProject", result, true, arguments.ToJsonString());
        Assert.Equal("invalid-dry-run", Assert.Single(scope.Worker.Unsettled.Snapshot()).RequestId);
        var next = enginehost::TiaMcpServer.ModelContextProtocol.McpServer.CallTool(SessionFixtureTool.Name,
            new TiaMcp.Logic.V4.Inputs.ToolArguments(JsonSerializer.SerializeToElement(new JsonObject())));
        Assert.True((bool?)next.StructuredContent?["ok"]);
        Assert.Equal(1, scope.Worker.EngineCalls);
    }

    [Theory]
    [InlineData("C:/fixture.ap21", true)]
    [InlineData("invalid\0path", false)]
    public void Unknown_without_a_prepare_scope_is_recorded_even_when_its_path_is_invalid(string outputPath, bool hasDigest)
    {
        using var scope = new SharedPipelineTests.Scope();
        var result = FoundationV4Result.Failure("21", "SaveExportContent", "unprepared-unknown", true, true, null,
            new WorkerOperationException("Synthetic unknown write.", -32603, "unknown"));
        var arguments = new JsonObject { ["outputPath"] = outputPath, ["content"] = "payload", ["dryRun"] = false };
        enginehost::TiaMcpServer.ModelContextProtocol.McpServer.ObserveSessionOutcome(
            "SaveExportContent", result, true, arguments.ToJsonString());
        var call = Assert.Single(scope.Worker.Unsettled.Snapshot());
        Assert.Equal("unprepared-unknown", call.RequestId);
        Assert.Equal(hasDigest, call.Digest != null);
        var read = enginehost::TiaMcpServer.ModelContextProtocol.McpServer.GetCallResult(call.RequestId);
        Assert.Equal("unknown", (string?)read.StructuredContent?["meta"]?["outcome"]);
        Assert.Single(scope.Worker.Unsettled.Snapshot());
    }

    [Fact]
    public void Engine_bridge_refuses_the_identical_reviewed_apply_before_dispatch()
    {
        using var scope = new SharedPipelineTests.Scope();
        scope.Worker.FoundationUnknown = true;
        scope.RegisterShared(scope.Foundation("ExportPlcBlocks"));
        using var request = enginehost::TiaMcpServer.ModelContextProtocol.McpServer.UseProgressRequest(Request("CallTool", new()), CancellationToken.None);
        var arguments = new JsonObject { ["softwarePath"] = "PLC", ["groupPath"] = "", ["exportPath"] = Path.GetTempPath(),
            ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = "C:/fixture.ap21", ["expectedInventoryHash"] = new string('a', 64) };
        var unknown = enginehost::TiaMcpServer.ModelContextProtocol.McpServer.CallTool("ExportPlcBlocks",
            new TiaMcp.Logic.V4.Inputs.ToolArguments(JsonSerializer.SerializeToElement(arguments)));
        Assert.Equal("unknown", (string?)unknown.StructuredContent?["meta"]?["outcome"]);
        Assert.Single(scope.Worker.Unsettled.Snapshot());
        var replay = enginehost::TiaMcpServer.ModelContextProtocol.McpServer.CallTool("ExportPlcBlocks",
            new TiaMcp.Logic.V4.Inputs.ToolArguments(JsonSerializer.SerializeToElement(arguments)));
        Assert.Equal("same-call-unsettled", (string?)replay.StructuredContent?["error"]?["details"]?["condition"]);
        Assert.Equal(1, scope.Worker.FoundationCalls);
        var next = enginehost::TiaMcpServer.ModelContextProtocol.McpServer.CallTool(SessionFixtureTool.Name,
            new TiaMcp.Logic.V4.Inputs.ToolArguments(JsonSerializer.SerializeToElement(new JsonObject())));
        Assert.True((bool?)next.StructuredContent?["ok"]);
        Assert.Equal(1, scope.Worker.EngineCalls);
    }

    [Fact]
    public async Task Queued_identical_apply_is_refused_after_the_first_call_becomes_unknown()
    {
        using var serial = new SemaphoreSlim(1);
        using var worker = new Worker("21") { Serial = serial };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        worker.Dispatch = (_, _) => { Interlocked.Increment(ref calls); entered.TrySetResult(); return reply.Task; };
        var tool = Tool(worker, "21", "ExportPlcBlocks");
        var args = new JsonObject { ["softwarePath"] = "PLC", ["groupPath"] = "", ["exportPath"] = Path.GetTempPath(),
            ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = worker.Project, ["expectedInventoryHash"] = new string('a', 64) };
        using var deadline = new CancellationTokenSource(TiaMcp.Engine.Tests.Shared.TestDeadlines.Hang);
        var first = tool.InvokeAsync(Request("ExportPlcBlocks", args), deadline.Token).AsTask();
        await entered.Task.WaitAsync(deadline.Token);
        var second = tool.InvokeAsync(Request("ExportPlcBlocks", args), deadline.Token).AsTask();
        try { Assert.False(second.IsCompleted); }
        finally { reply.TrySetException(new WorkerOperationException("Synthetic write outcome unknown.", -32603, "unknown")); }
        Assert.Equal("unknown", (string?)(await first).StructuredContent?["meta"]?["outcome"]);
        Assert.Equal("same-call-unsettled", (string?)(await second).StructuredContent?["error"]?["details"]?["condition"]);
        Assert.Equal(1, calls);
        Assert.Single(worker.Unsettled.Snapshot());
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Get_session_state_lists_the_unsettled_request(string release)
    {
        using var worker = new Worker(release) { UnknownNextWrite = true };
        var unknown = Body(await Tool(worker, release, "SaveProject").InvokeAsync(Request("SaveProject", WriteArgs(worker))));
        var state = Body(await Tool(worker, release, "GetSessionState").InvokeAsync(Request("GetSessionState", new())));
        var call = Assert.Single(state["data"]!["sessionState"]!["unsettled"]!.AsArray())!;
        Assert.Equal((string?)unknown["meta"]?["requestId"], (string?)call["requestId"]);
        Assert.Equal("SaveProject", (string?)call["tool"]);
        Assert.False((bool?)call["fromPreviousOwner"]);
        Assert.NotNull(call["utc"]);
        Assert.Null(state["data"]!["sessionState"]!["requiresSessionReset"]);
        Assert.Null(state["data"]!["sessionState"]!["priorUnknownRequestId"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Save_project_warns_and_runs_while_unsettled(string release)
    {
        using var worker = new Worker(release) { UnknownNextWrite = true };
        var save = Tool(worker, release, "SaveProject");
        var unknown = Body(await save.InvokeAsync(Request("SaveProject", WriteArgs(worker))));
        var refused = Body(await save.InvokeAsync(Request("SaveProject", WriteArgs(worker))));
        Assert.Equal("same-call-unsettled", (string?)refused["error"]?["details"]?["condition"]);
        Assert.Equal(1, worker.Writes);
        using var lookup = await ReadUnknown(worker, release, (string)unknown["meta"]!["requestId"]!);
        Assert.Single(worker.Unsettled.Snapshot());
        foreach (bool preview in new[] { true, false })
        {
            var result = Body(await save.InvokeAsync(Request("SaveProject", WriteArgs(worker, preview))));
            Assert.True((bool?)result["ok"], result.ToJsonString());
            var warning = Assert.Single(result["meta"]!["warnings"]!.AsArray(), w => (string?)w?["code"] == "UNSETTLED_CALLS")!;
            Assert.Single(warning["details"]!["unsettled"]!.AsArray());
        }
        Assert.Equal(2, worker.Writes);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Save_project_has_no_warning_when_settled(string release)
    {
        using var worker = new Worker(release);
        var result = Body(await Tool(worker, release, "SaveProject").InvokeAsync(Request("SaveProject", WriteArgs(worker))));
        Assert.True((bool?)result["ok"]);
        Assert.DoesNotContain(result["meta"]!["warnings"]!.AsArray(), w => (string?)w?["code"] == "UNSETTLED_CALLS");
        Assert.Empty(worker.Unsettled.Snapshot());
    }

    [Theory, MemberData(nameof(Releases))]
    public void Document_import_with_status_unknown_stays_unknown_after_synchronize_is_removed(string release)
    {
        var definition = FoundationTools.Definitions.Single(d => d.Name == "ImportPlcBlockDocuments");
        var raw = new JsonObject { ["Status"] = "unknown", ["Attempted"] = true, ["Executed"] = false, ["RequiresSessionReset"] = true };
        var body = Body(FoundationV4Result.Worker(release, definition, "document-import", new() { ["dryRun"] = false }, raw, raw));
        Assert.Equal("unknown", (string?)body["meta"]?["outcome"]);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Save_project_copy_warns_about_the_prior_project_before_clearing_it(string release)
    {
        using var worker = new Worker(release);
        worker.Unsettled.ProjectChanged(worker.Project);
        worker.Unsettled.Record("prior-write", "CreatePlcTag", "digest", DateTimeOffset.UtcNow);
        var inner = PortedToolContract.Create(worker, release).OfType<FoundationTool>()
            .SingleOrDefault(tool => tool.ProtocolTool.Name == "SaveProjectCopy")
            ?? new FoundationTool(worker, release,
                typeof(enginehost::TiaMcpServer.ModelContextProtocol.ProjectLifecycleProjectTools).GetMethod("SaveProjectCopyV4")!, "SaveProjectCopy");
        var copy = new FoundationV4Tool(inner, release, () => new(false, 1));
        var result = Body(await copy.InvokeAsync(Request("SaveProjectCopy", new() { ["newProjectPath"] = "C:/copy" })));
        bool supported = release != "14sp1";
        Assert.Equal(supported, (bool?)result["ok"]);
        Assert.Equal(supported ? 1 : 0, worker.Writes);
        var warning = Assert.Single(result["meta"]!["warnings"]!.AsArray(), item => (string?)item?["code"] == "UNSETTLED_CALLS")!;
        Assert.Equal("prior-write", (string?)Assert.Single(warning["details"]!["unsettled"]!.AsArray())!["requestId"]);
        Assert.Equal(supported ? 0 : 1, worker.Unsettled.Snapshot().Count);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Real_worker_fault_refuses_before_approval(string release)
    {
        using var worker = new Worker(release) { Faulted = true };
        var save = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "SaveProject"), worker),
            release, () => new(true, 1), (_, _, _) => throw new Exception("A faulted worker must refuse before approval."));
        var result = Body(await save.InvokeAsync(Request("SaveProject", WriteArgs(worker))));
        Assert.Equal("PRECONDITION_FAILED", (string?)result["error"]?["code"]);
        Assert.Equal("worker-faulted", (string?)result["error"]?["details"]?["condition"]);
        Assert.Equal(0, worker.Writes);
        Assert.Empty(worker.Unsettled.Snapshot());
    }

    [Theory, MemberData(nameof(Releases))]
    public void A_default_preview_does_not_record_an_unknown_result(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var worker = enginehost::TiaMcpServer.ModelContextProtocol.McpServer.Worker;
        var result = FoundationV4Result.Failure(release, "CompilePlcSoftware", "preview", true, true, null);
        enginehost::TiaMcpServer.ModelContextProtocol.McpServer.ObserveSessionOutcome("CompilePlcSoftware", result, true, "{}");
        Assert.Equal("unknown", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.Empty(worker.Unsettled.Snapshot());
    }

    [Theory, MemberData(nameof(PreviewToolCallTruthTests.ProjectCopyAndDownloadReleases), MemberType = typeof(PreviewToolCallTruthTests))]
    public void Save_project_copy_preview_warns_without_dispatch(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var store = enginehost::TiaMcpServer.ModelContextProtocol.McpServer.Worker.Unsettled;
        store.Record("prior-write", "CreatePlcTag", "digest", DateTimeOffset.UtcNow);
        var result = Body(enginehost::TiaMcpServer.ModelContextProtocol.McpServer.PreviewToolCall("SaveProjectCopy",
            new TiaMcp.Logic.V4.Inputs.ToolArguments(JsonSerializer.SerializeToElement(new { newProjectPath = "C:/Projects/Copy" }))));
        Assert.True((bool?)result["ok"]);
        var warning = Assert.Single(result["meta"]!["warnings"]!.AsArray(), item => (string?)item?["code"] == "UNSETTLED_CALLS")!;
        Assert.Equal("prior-write", (string?)Assert.Single(warning["details"]!["unsettled"]!.AsArray())!["requestId"]);
        Assert.Equal(0, fixture.Worker.NativeCalls);
        Assert.Single(store.Snapshot());
    }

    [Theory, MemberData(nameof(Releases))]
    public void Succeeded_with_a_legacy_flag_is_not_unsettled(string release)
    {
        var definition = FoundationTools.Definitions.Single(tool => tool.Name == "SaveProject");
        var raw = new JsonObject { ["Executed"] = true, ["ProjectFile"] = "C:/fixture.ap" + release, ["RequiresSessionReset"] = true };
        var result = FoundationV4Result.Worker(release, definition, "succeeded", new() { ["dryRun"] = false }, raw, raw);
        Assert.Equal("succeeded", (string?)result.StructuredContent?["meta"]?["outcome"]);
        RemainsSettled(release, "SaveProject", result, true);
    }

    private static void RemainsSettled(string release, string name, CallToolResult result, bool write)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        enginehost::TiaMcpServer.ModelContextProtocol.McpServer.ObserveSessionOutcome(name, result, write, "{\"dryRun\":false}");
        Assert.Empty(((enginehost::TiaMcp.FoundationHost.IEngineWorker)worker).Unsettled.Snapshot());
    }

    [Theory, MemberData(nameof(Releases))]
    public void Failed_hmi_read_is_not_unsettled(string release)
    {
        var result = FoundationV4Result.Failure(release, "ListHmiTags", "hmi-read", true, false, null,
            new WorkerOperationException("Connection unavailable.", -32603, "read-failed"));
        Assert.Equal("read-failed", (string?)result.StructuredContent?["meta"]?["outcome"]);
        RemainsSettled(release, "ListHmiTags", result, false);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Compile_with_errors_is_not_unsettled(string release)
    {
        var definition = FoundationTools.Definitions.Single(d => d.Name == "CompilePlcSoftware");
        var raw = new JsonObject { ["Executed"] = true, ["State"] = "Error", ["ErrorCount"] = 1,
            ["Errors"] = new JsonArray("Synthetic compiler error"), ["RequiresSessionReset"] = true };
        var result = FoundationV4Result.Worker(release, definition, "compile", new() { ["dryRun"] = false }, raw, raw);
        Assert.Equal("failed", (string?)result.StructuredContent?["meta"]?["outcome"]);
        RemainsSettled(release, definition.Name, result, true);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Definite_batch_failure_is_not_unsettled(string release)
    {
        var definition = FoundationTools.Definitions.Single(d => d.Name == "ExportPlcBlocks");
        var raw = new JsonObject { ["Executed"] = true, ["RequiresSessionReset"] = true,
            ["Items"] = new JsonArray(new JsonObject { ["Status"] = "exported", ["Attempted"] = true },
                new JsonObject { ["Status"] = "failed", ["Attempted"] = false, ["Failure"] = "target-recheck-failed" }) };
        var result = FoundationV4Result.Worker(release, definition, "batch", new() { ["dryRun"] = false }, raw, raw);
        Assert.Equal("partial", (string?)result.StructuredContent?["meta"]?["outcome"]);
        RemainsSettled(release, definition.Name, result, true);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Answered_unknown_keeps_the_worker_running(string release)
    {
        string exe = TiaMcp.Engine.Tests.Shared.TestPaths.WorkerFixture(
            "TiaMcp.PlcWorker.FaultFixture", "TiaMcp.PlcWorker.FaultFixture.exe", "net48");
        TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(Path.GetDirectoryName(exe)!, "TiaMcp.Adapter." + release + ".dll"),
            "offline fixture identity");
        using var worker = new WorkerClient(release, exe, Path.GetDirectoryName(exe)!, true) { Bundled = false };
        var error = await Assert.ThrowsAsync<WorkerOperationException>(() => worker.Call("FixtureAnsweredUnknown", new(), CancellationToken.None));
        Assert.Equal("unknown", error.Outcome);
        int pid = (int)worker.OperatorSnapshot()["pid"]!;
        var next = await worker.Call("ReadState", new(), CancellationToken.None);
        Assert.Equal(release, (string?)next?["ReleaseKey"]);
        Assert.Equal(pid, (int?)worker.OperatorSnapshot()["pid"]);
        Assert.False(worker.Faulted);
    }

    [Theory, InlineData("20"), InlineData("21")]
    public async Task Document_import_unknown_crosses_the_process_channel_without_promotion(string release)
    {
        string exe = TiaMcp.Engine.Tests.Shared.TestPaths.WorkerFixture(
            "TiaMcp.PlcWorker.FaultFixture", "TiaMcp.PlcWorker.FaultFixture.exe", "net48");
        TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(Path.GetDirectoryName(exe)!, "TiaMcp.Adapter." + release + ".dll"),
            "offline fixture identity");
        using var worker = new WorkerClient(release, exe, Path.GetDirectoryName(exe)!, true) { Bundled = false };
        var raw = await worker.Call("FixtureDocumentImportUnknown", new(), CancellationToken.None);
        var definition = FoundationTools.Definitions.Single(d => d.Name == "ImportPlcBlockDocuments");
        var result = FoundationV4Result.Worker(release, definition, "document-fixture", new() { ["dryRun"] = false }, raw, raw);
        Assert.Equal("unknown", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.False(worker.Faulted);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Restart_and_rebind_to_the_same_project_keeps_unsettled_and_save_warns(string release)
    {
        string exe = TiaMcp.Engine.Tests.Shared.TestPaths.WorkerFixture(
            "TiaMcp.PlcWorker.FaultFixture", "TiaMcp.PlcWorker.FaultFixture.exe", "net48");
        TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(Path.GetDirectoryName(exe)!, "TiaMcp.Adapter." + release + ".dll"),
            "offline fixture identity");
        using var worker = new WorkerClient(release, exe, Path.GetDirectoryName(exe)!, true) { Bundled = false };
        await worker.Call("Attach", new() { ["processId"] = Environment.ProcessId }, CancellationToken.None);
        var definition = FoundationTools.Definitions.Single(item => item.Name == "SaveProject");
        var unknownTool =
            new FoundationV4Tool(new FoundationTool(definition with { Operation = "FixtureAnsweredUnknown" }, worker), release, () => new(false, 1));
        var arguments = new JsonObject { ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = "C:/fixture.ap" + release };
        var unknown = Body(await unknownTool.InvokeAsync(Request("SaveProject", arguments)));
        Assert.Equal("unknown", (string?)unknown["meta"]?["outcome"]);
        string id = Assert.Single(worker.Unsettled.Snapshot()).RequestId;
        int writes = (int)(await worker.Call("FixtureWriteCount", new(), CancellationToken.None))!;
        Assert.Equal(1, writes);
        var refused = Body(await unknownTool.InvokeAsync(Request("SaveProject", arguments)));
        Assert.Equal("same-call-unsettled", (string?)refused["error"]?["details"]?["condition"]);
        Assert.Equal(writes, (int)(await worker.Call("FixtureWriteCount", new(), CancellationToken.None))!);
        using var lookup = await ReadUnknown(worker, release, id);
        Assert.True((bool?)((await worker.RestartHostWorker(true, CancellationToken.None))["success"]));
        var state = Body(await new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(item => item.Name == "GetSessionState"), worker),
            release, () => new(false, 1)).InvokeAsync(Request("GetSessionState", new())));
        Assert.Equal(id, (string?)Assert.Single(state["data"]!["sessionState"]!["unsettled"]!.AsArray())!["requestId"]);
        await worker.Call("Attach", new() { ["processId"] = Environment.ProcessId }, CancellationToken.None);
        var saved = Body(await new FoundationV4Tool(new FoundationTool(definition, worker), release, () => new(false, 1))
            .InvokeAsync(Request("SaveProject", arguments)));
        Assert.True((bool?)saved["ok"], saved.ToJsonString());
        var warning = Assert.Single(saved["meta"]!["warnings"]!.AsArray(), item => (string?)item?["code"] == "UNSETTLED_CALLS")!;
        Assert.Equal(id, (string?)Assert.Single(warning["details"]!["unsettled"]!.AsArray())!["requestId"]);
        Assert.Equal(id, Assert.Single(worker.Unsettled.Snapshot()).RequestId);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Answered_status_error_does_not_latch_a_worker_fault(string release)
    {
        string exe = TiaMcp.Engine.Tests.Shared.TestPaths.WorkerFixture(
            "TiaMcp.PlcWorker.FaultFixture", "TiaMcp.PlcWorker.FaultFixture.exe", "net48");
        TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(Path.GetDirectoryName(exe)!, "TiaMcp.Adapter." + release + ".dll"),
            "offline fixture identity");
        using var worker = new WorkerClient(release, exe, Path.GetDirectoryName(exe)!, true) { Bundled = false };
        await worker.Call("FixtureFailNextStatus", new(), CancellationToken.None);
        var error = await Assert.ThrowsAsync<WorkerOperationException>(() => worker.Observe("status", new(), CancellationToken.None));
        Assert.Equal("read-failed", error.Outcome);
        Assert.False(worker.Faulted);
        var next = await worker.Call("ReadState", new(), CancellationToken.None);
        Assert.Equal(release, (string?)next?["ReleaseKey"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Cold_status_start_failure_allows_start_after_the_prerequisite_is_fixed(string release)
    {
        string exe = TiaMcp.Engine.Tests.Shared.TestPaths.WorkerFixture(
            "TiaMcp.PlcWorker.FaultFixture", "TiaMcp.PlcWorker.FaultFixture.exe", "net48");
        TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(Path.GetDirectoryName(exe)!, "TiaMcp.Adapter." + release + ".dll"),
            "offline fixture identity");
        string root = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory), "bin-build", "refactor");
        string api = Path.Combine(root, "cold-status-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var worker = new WorkerClient(release, exe, api, true) { Bundled = false };
            await Assert.ThrowsAsync<FileNotFoundException>(() => worker.Observe("status", new(), CancellationToken.None));
            Assert.Null(worker.OperatorSnapshot()["pid"]);
            Assert.False(worker.Faulted);
            Assert.False(worker.WorkerFaulted);
            Directory.CreateDirectory(api);
            Assert.NotNull(await worker.Observe("status", new(), CancellationToken.None));
            Assert.NotNull(worker.OperatorSnapshot()["pid"]);
            var next = await worker.Call("ReadState", new(), CancellationToken.None);
            Assert.Equal(release, (string?)next?["ReleaseKey"]);
            Assert.False(worker.Faulted);
        }
        finally
        {
            Assert.StartsWith(root + Path.DirectorySeparatorChar, Path.GetFullPath(api), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(api)) Directory.Delete(api);
        }
    }

    [Fact]
    public void Project_change_and_session_end_clear_unsettled_and_digest_is_canonical()
    {
        var store = new UnsettledCalls();
        store.ProjectChanged("C:/P.ap21");
        string digest = UnsettledCalls.WriteDigest("SaveProject", new() { ["confirm"] = true, ["dryRun"] = false, ["x"] = 1 }, "C:/P.ap21");
        Assert.Equal(digest, UnsettledCalls.WriteDigest("SaveProject", new() { ["x"] = 1, ["dryRun"] = true }, "c:/p.ap21"));
        store.Record("request", "SaveProject", digest, DateTimeOffset.UtcNow);
        store.ProjectChanged(null); Assert.Single(store.Snapshot());
        store.ProjectChanged("c:/p.ap21"); Assert.Single(store.Snapshot());
        store.ProjectChanged("C:/Other.ap21"); Assert.Empty(store.Snapshot());
        store.Record("second", "SaveProject", digest, DateTimeOffset.UtcNow);
        store.Clear(); Assert.Empty(store.Snapshot());
        store.ProjectChanged(null);
        store.Record("unbound", "CreateProject", digest, DateTimeOffset.UtcNow);
        store.ProjectChanged("C:/New.ap21");
        Assert.Single(store.Snapshot());
    }
}
