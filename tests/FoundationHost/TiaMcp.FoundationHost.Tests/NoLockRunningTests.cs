extern alias foundationhost;
extern alias enginehost;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Engine.Tests.Shared;
using TiaMcp.Logic.V4;
using Xunit;
using Foundation = foundationhost::TiaMcp.FoundationHost;
using Engine = enginehost::TiaMcp.FoundationHost;
using Host = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;

[Collection(WorkerDataDirectoryCollection.Name)]
public sealed class NoLockRunningTests
{
    public static IEnumerable<object[]> Releases() => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });

    [Theory, MemberData(nameof(Releases))]
    public async Task Write_over_budget_returns_in_progress_and_the_worker_stays(string release)
    {
        using var scope = new Scope(release);
        var tool = scope.Tool("SaveProject");
        using var session = scope.Session(tool);
        int pid = scope.Pid;
        var result = await scope.Call(session, tool.ProtocolTool.Name, scope.WriteArguments);
        Assert.Equal("in-progress", Outcome(result));
        Assert.Equal("OPERATION_IN_PROGRESS", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.False((bool?)result.StructuredContent?["ok"]);
        Assert.False(scope.Worker.Faulted);
        Assert.Equal(pid, scope.Pid);
        Assert.False(scope.Child.HasExited);
        Assert.Equal("Busy", (string?)scope.Worker.SupervisorSnapshot(new JsonObject())["state"]);
        string id = (string)result.StructuredContent!["data"]!["activeRequestId"]!;
        var busy = await Assert.ThrowsAsync<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() =>
            scope.Worker.Call("ReadBlocks", new(), CancellationToken.None));
        Assert.Equal("worker-call-running", busy.Condition);
        Assert.Contains(id, busy.Message);
        Assert.Contains("SaveProject", busy.Message);
        Assert.NotNull(session.Snapshot()["running"]?["sinceUtc"]);
        Assert.Equal("in-progress", Outcome(scope.Read(id)));
        scope.Release();
        await scope.WaitCompleted(session);
        var final = scope.Read(id);
        Assert.Equal("succeeded", Outcome(final));
        Assert.Equal(id, (string?)final.StructuredContent?["meta"]?["requestId"]);
        Assert.Null(scope.Worker.Running);
        Assert.NotEqual("Busy", (string?)scope.Worker.SupervisorSnapshot(new JsonObject())["state"]);
        await scope.Worker.Call("ReadState", new(), CancellationToken.None);
        Assert.Equal(pid, scope.Pid);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Identical_write_waits_for_its_result_to_be_read(string release)
    {
        using var scope = new Scope(release);
        var tool = scope.Tool("SaveProject");
        using var session = scope.Session(tool);
        var first = await scope.Call(session, "SaveProject", scope.WriteArguments);
        string id = (string)first.StructuredContent!["meta"]!["requestId"]!;
        var same = await scope.Call(session, "SaveProject", scope.WriteArguments);
        Assert.Equal("same-call-unsettled", (string?)same.StructuredContent?["error"]?["details"]?["condition"]);
        Assert.Equal("not-started", (string?)same.StructuredContent?["meta"]?["execution"]);
        Assert.Equal(id, (string?)same.StructuredContent?["error"]?["details"]?["target"]);
        scope.Release();
        await scope.WaitCompleted(session);
        var unread = await scope.Call(session, "SaveProject", scope.WriteArguments);
        Assert.Equal("same-call-unsettled", (string?)unread.StructuredContent?["error"]?["details"]?["condition"]);
        Assert.Equal("succeeded", Outcome(scope.Read(id)));
        Assert.Equal("succeeded", Outcome(await scope.Call(session, "SaveProject", scope.WriteArguments)));
    }

    [Theory, MemberData(nameof(Releases))]
    public void GetCallResult_reads_evicted_and_unretained_outcome_summaries(string release)
    {
        using var scope = new Scope(release);
        for (int index = 0; index < 17; index++)
            scope.Worker.LateResults.RecordUnknown("completed-" + index, "SaveProject", "digest-" + index,
                Foundation.FoundationV4Result.LateFailure(release, "SaveProject", "completed-" + index));
        Assert.Equal("completed-0", scope.Worker.LateResults.FindUnread("digest-0"));
        var evicted = scope.Read("completed-0");
        Assert.Equal("unknown", Outcome(evicted));
        Assert.True((bool?)evicted.StructuredContent?["data"]?["fullResultEvicted"]);
        Assert.Null(scope.Worker.LateResults.FindUnread("digest-0"));
        Assert.Equal(16, scope.Worker.LateResults.Snapshot().Count);
        scope.Worker.Unsettled.Record("unretained", "SaveProject", "unretained-digest", DateTimeOffset.UtcNow);
        var unretained = scope.Read("unretained");
        Assert.Equal("unknown", Outcome(unretained));
        Assert.True((bool?)unretained.StructuredContent?["data"]?["fullResultEvicted"]);
        Assert.NotNull(V4Json.Deserialize<Envelope>(unretained.StructuredContent!.ToJsonString()));
        Assert.Null(scope.Worker.Unsettled.FindWrite("unretained-digest"));
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Unknown_write_is_guarded_until_its_result_is_read(string release)
    {
        using var scope = new Scope(release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        var tool = scope.Tool("SaveProject", "FixtureAnsweredUnknown", gated: false);
        using var session = scope.Session(tool);
        var first = await scope.Call(session, "SaveProject", scope.WriteArguments);
        Assert.Equal("unknown", Outcome(first));
        string id = (string)first.StructuredContent!["meta"]!["requestId"]!;
        var same = await scope.Call(session, "SaveProject", scope.WriteArguments);
        Assert.Equal("same-call-unsettled", (string?)same.StructuredContent?["error"]?["details"]?["condition"]);
        Assert.Equal("unknown", Outcome(scope.Read(id)));
        Assert.Single(scope.Worker.Unsettled.Snapshot());
        Assert.Equal("unknown", Outcome(await scope.Call(session, "SaveProject", scope.WriteArguments)));
        var preview = (JsonObject)scope.WriteArguments.DeepClone();
        preview["dryRun"] = true;
        Assert.NotEqual("same-call-unsettled", (string?)(await scope.Call(session, "SaveProject", preview))
            .StructuredContent?["error"]?["details"]?["condition"]);
        var different = (JsonObject)scope.WriteArguments.DeepClone();
        different["expectedProjectFile"] = "C:/other.ap" + release;
        Assert.NotEqual("same-call-unsettled", (string?)(await scope.Call(session, "SaveProject", different))
            .StructuredContent?["error"]?["details"]?["condition"]);
    }

    [Fact]
    public async Task Worker_exit_during_a_detached_disconnect_keeps_its_unknown_outcome()
    {
        using var scope = new Scope("21");
        var tool = new DisconnectAfterBudget(scope);
        using var session = scope.Session(tool);
        var response = await scope.Call(session, "DisconnectPortal", new());
        Assert.Equal("in-progress", Outcome(response));
        string id = (string)response.StructuredContent!["data"]!["activeRequestId"]!;
        Assert.StartsWith(Path.GetFullPath(TestPaths.RepositoryRoot(AppContext.BaseDirectory)) + Path.DirectorySeparatorChar,
            Path.GetFullPath(scope.Child.MainModule!.FileName), StringComparison.OrdinalIgnoreCase);
        scope.Child.Kill();
        Assert.True(scope.Child.WaitForExit(TestDeadlines.HangMilliseconds));
        await scope.WaitCompleted(session);
        var final = scope.Read(id);
        Assert.Equal("unknown", Outcome(final));
        Assert.Equal("OUTCOME_UNKNOWN", (string?)final.StructuredContent?["error"]?["code"]);
        Assert.Equal("unknown", (string?)final.StructuredContent?["meta"]?["execution"]);
    }

    [Fact]
    public async Task Disconnect_and_restart_refuse_while_running_and_do_not_kill()
    {
        using var scope = new Scope("21");
        var tool = scope.Tool("SaveProject");
        using var session = scope.Session(tool);
        await scope.Call(session, "SaveProject", scope.WriteArguments);
        int pid = scope.Pid;
        var disconnect = await Assert.ThrowsAsync<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() =>
            scope.Worker.Call("Disconnect", new(), CancellationToken.None));
        var restart = await Assert.ThrowsAsync<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() =>
            scope.Worker.RestartHostWorker(true, CancellationToken.None));
        Assert.Equal("worker-call-running", disconnect.Condition);
        Assert.Equal("worker-call-running", restart.Condition);
        Assert.False(scope.Child.HasExited);
        scope.Release();
        await scope.WaitCompleted(session);
        Assert.Equal(pid, scope.Pid);
        Assert.True((bool?)(await scope.Worker.Call("Disconnect", new(), CancellationToken.None))?["WorkerAcknowledged"]);
    }

    [Theory]
    [InlineData("19", "crash", false)] [InlineData("19", "malformed", false)]
    [InlineData("21", "crash", false)] [InlineData("21", "malformed", false)]
    [InlineData("19", "crash", true)] [InlineData("19", "malformed", true)]
    [InlineData("21", "crash", true)] [InlineData("21", "malformed", true)]
    public async Task Real_drained_fault_clears_busy_keeps_the_result_and_allows_restart(string release, string fault, bool read)
    {
        using var scope = new Scope(release);
        var tool = read ? scope.Tool("GetProjectInfo", "ReadBlocks") : scope.Tool("SaveProject");
        using var session = scope.Session(tool);
        var response = await scope.Call(session, tool.ProtocolTool.Name, read ? new JsonObject() : scope.WriteArguments);
        Assert.Equal("in-progress", Outcome(response));
        string id = (string)response.StructuredContent!["data"]!["activeRequestId"]!;
        Assert.True(scope.Worker.CallRunning);
        File.WriteAllText(Path.Combine(scope.DirectoryPath, "after-gate-fault"), fault);
        scope.Release();
        await scope.WaitCompleted(session);
        Assert.False(scope.Worker.CallRunning);
        Assert.Null(scope.Worker.Running);
        Assert.True(scope.Worker.Faulted);
        var final = scope.Read(id);
        Assert.Equal(read ? "read-failed" : "unknown", Outcome(final));
        var refused = await Assert.ThrowsAsync<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() =>
            scope.Worker.Call("ReadState", new(), CancellationToken.None));
        Assert.Equal("worker-faulted", refused.Condition);
        using (new ProductionWorkerBudgets(scope.Worker))
        {
            var restarted = await scope.Worker.RestartHostWorker(true, CancellationToken.None);
            Assert.True((bool?)restarted["success"]);
            await scope.Worker.Call("ReadState", new(), CancellationToken.None);
        }
        Assert.False(scope.Worker.Faulted);
        Assert.True(scope.Child.WaitForExit(TestDeadlines.HangMilliseconds));
        Assert.Equal(read ? "read-failed" : "unknown", Outcome(scope.Read(id)));
        using var replacement = Process.GetProcessById(scope.Pid);
        Assert.StartsWith(Path.GetFullPath(TestPaths.RepositoryRoot(AppContext.BaseDirectory)) + Path.DirectorySeparatorChar,
            Path.GetFullPath(replacement.MainModule!.FileName), StringComparison.OrdinalIgnoreCase);
        scope.Worker.Dispose();
        Assert.True(replacement.WaitForExit(TestDeadlines.HangMilliseconds));
    }

    [Theory, InlineData("over-limit"), InlineData("out-of-order")]
    public async Task Invalid_progress_keeps_the_native_call_alive_until_its_real_result(string fault)
    {
        using var scope = new Scope("21");
        File.WriteAllText(Path.Combine(scope.DirectoryPath, "progress-fault"), fault);
        var tool = scope.Tool("SaveProject");
        using var session = scope.Session(tool);
        int pid = scope.Pid;
        var response = await scope.Call(session, "SaveProject", scope.WriteArguments);
        Assert.Equal("in-progress", Outcome(response));
        string id = (string)response.StructuredContent!["data"]!["activeRequestId"]!;
        await scope.WaitProgressDropped();
        Assert.False(scope.Channel.Poisoned);
        Assert.True(scope.Worker.CallRunning);
        Assert.False(scope.Worker.Faulted);
        Assert.Equal(pid, scope.Pid);
        Assert.False(scope.Child.HasExited);
        var restart = await Assert.ThrowsAsync<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() =>
            scope.Worker.RestartHostWorker(true, CancellationToken.None));
        var disconnect = await Assert.ThrowsAsync<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() =>
            scope.Worker.Call("Disconnect", new(), CancellationToken.None));
        Assert.Equal("worker-call-running", restart.Condition);
        Assert.Equal("worker-call-running", disconnect.Condition);
        Assert.Equal("in-progress", Outcome(scope.Read(id)));
        scope.Release();
        await scope.WaitCompleted(session);
        Assert.Equal("succeeded", Outcome(scope.Read(id)));
        Assert.Equal(pid, scope.Pid);
        Assert.False(scope.Child.HasExited);
        Assert.False(scope.Channel.Poisoned);
        Assert.False(scope.Worker.CallRunning);
    }

    [Theory, InlineData("over-limit"), InlineData("out-of-order")]
    public async Task Dispose_after_invalid_progress_waits_for_the_native_reply(string fault)
    {
        using var scope = new Scope("21");
        File.WriteAllText(Path.Combine(scope.DirectoryPath, "progress-fault"), fault);
        var tool = scope.Tool("SaveProject");
        using var session = scope.Session(tool);
        await scope.Call(session, "SaveProject", scope.WriteArguments);
        await scope.WaitProgressDropped();
        var receipt = new DisposalReceipt();
        scope.Worker.HostPipeline = receipt;
        scope.Worker.Dispose();
        Assert.False(scope.Channel.Poisoned);
        Assert.True(scope.Worker.CallRunning);
        Assert.False(scope.Child.HasExited);
        Assert.False(receipt.Disposed);
        scope.Release();
        await scope.WaitCompleted(session);
        Assert.True(scope.Child.WaitForExit(TestDeadlines.HangMilliseconds));
        Assert.True(receipt.Disposed);
    }

    [Theory, InlineData("crash"), InlineData("malformed")]
    public async Task Dispose_waiting_for_a_real_drained_fault_releases_the_worker_and_pipeline(string fault)
    {
        using var scope = new Scope("21");
        var tool = scope.Tool("SaveProject");
        using var session = scope.Session(tool);
        await scope.Call(session, "SaveProject", scope.WriteArguments);
        var receipt = new DisposalReceipt();
        Assert.Null(scope.Worker.HostPipeline);
        scope.Worker.HostPipeline = receipt;
        scope.Worker.Dispose();
        Assert.False(receipt.Disposed);
        Assert.False(scope.Child.HasExited);
        File.WriteAllText(Path.Combine(scope.DirectoryPath, "after-gate-fault"), fault);
        scope.Release();
        await scope.WaitCompleted(session);
        Assert.False(scope.Worker.CallRunning);
        Assert.True(scope.Child.WaitForExit(TestDeadlines.HangMilliseconds));
        Assert.True(receipt.Disposed);
        Assert.Null(scope.Worker.HostPipeline);
    }

    [Theory, InlineData("c:/x/out.xml"), InlineData("C:\\x\\out.xml ")]
    public async Task Normalized_target_retries_are_refused_before_approval(string path)
    {
        using var scope = new Scope("19");
        var definition = Foundation.FoundationTools.Definitions.Single(tool => tool.Name == "ExportPlcBlock");
        var target = new Foundation.FoundationTool(definition, scope.Worker);
        var tool = new Foundation.FoundationV4Tool(target, "19", () => new(true, 1),
            approvalWait: (_, _, _) => throw new InvalidOperationException("A retry must not request approval."));
        using var session = scope.Session(tool);
        var args = new JsonObject { ["softwarePath"] = "PLC_1", ["blockPath"] = "B", ["exportPath"] = "C:\\x\\out.xml", ["dryRun"] = false };
        scope.Worker.Unsettled.Record("first-export", "ExportPlcBlock",
            Engine.WriteDigest.Create("ExportPlcBlock", args, target.BoundProjectFile), DateTimeOffset.UtcNow);
        args["exportPath"] = path;
        var result = await scope.Call(session, "ExportPlcBlock", args);
        Assert.Equal("same-call-unsettled", (string?)result.StructuredContent?["error"]?["details"]?["condition"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
    }

    private sealed class DisposalReceipt : IDisposable
    {
        internal bool Disposed;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task Ordinary_active_call_keeps_the_structured_restart_refusal()
    {
        using var scope = new Scope("19");
        using var production = new ProductionWorkerBudgets(scope.Worker);
        var tool = scope.Tool("SaveProject");
        using var session = scope.Session(tool);
        var pending = scope.Call(session, "SaveProject", scope.WriteArguments);
        try
        {
            await scope.WaitDispatched();
            Assert.False(scope.Worker.CallRunning);
            var refused = await scope.Worker.RestartHostWorker(true, CancellationToken.None);
            Assert.False((bool?)refused["success"]);
            Assert.Null(refused["restarted"]);
            var preview = await scope.Worker.RestartHostWorker(false, CancellationToken.None);
            Assert.True((bool?)preview["success"]);
            Assert.True((bool?)preview["dryRun"]);
            Assert.False(scope.Child.HasExited);
        }
        finally { scope.Release(); await pending; }
    }

    [Fact]
    public async Task Session_end_during_a_running_call_defers_disposal()
    {
        using var scope = new Scope("19");
        var tool = scope.Tool("SaveProject");
        using var session = scope.Session(tool);
        await scope.Call(session, "SaveProject", scope.WriteArguments);
        session.Dispose();
        Assert.False(scope.Disposed);
        Assert.False(scope.Child.HasExited);
        Assert.True((bool?)session.Snapshot()["callInProgress"]);
        scope.Release();
        await scope.WaitCompleted(session);
        Assert.True(scope.Disposed);
        Assert.True(scope.Child.WaitForExit(TestDeadlines.HangMilliseconds));
    }

    [Fact]
    public async Task Cancel_after_detach_does_not_touch_the_call()
    {
        using var scope = new Scope("21");
        var tool = scope.Tool("SaveProject");
        using var session = scope.Session(tool);
        using var client = new CancellationTokenSource();
        var result = await scope.Call(session, "SaveProject", scope.WriteArguments, token: client.Token);
        client.Cancel();
        Assert.False(scope.Child.HasExited);
        scope.Release();
        await scope.WaitCompleted(session);
        Assert.Equal("succeeded", Outcome(scope.Read((string)result.StructuredContent!["meta"]!["requestId"]!)));
    }

    [Fact]
    public async Task Queued_call_is_released_with_the_busy_answer()
    {
        using var scope = new Scope("19");
        var tool = scope.Tool("SaveProject");
        using var session = scope.Session(tool);
        ProductionWorkerBudgets.Live(scope.Worker)["default"] = TimeSpan.FromSeconds(0.2);
        var first = scope.Call(session, "SaveProject", scope.WriteArguments);
        await scope.WaitDispatched();
        var queued = scope.Worker.Call("ReadBlocks", new(), CancellationToken.None);
        Assert.False(queued.IsCompleted);
        Assert.Equal("in-progress", Outcome(await first));
        var refusal = await Assert.ThrowsAsync<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() => queued);
        Assert.Equal("worker-call-running", refusal.Condition);
        scope.Release();
        await scope.WaitCompleted(session);
    }

    [Fact]
    public async Task Detached_multi_step_tool_finishes_every_step()
    {
        using var scope = new Scope("21");
        var tool = new FollowingStep(scope.Tool("SaveProject"), scope.Worker);
        using var session = scope.Session(tool);
        var first = await scope.Call(session, "SaveProject", scope.WriteArguments);
        Assert.Equal("in-progress", Outcome(first));
        Assert.Equal(0, tool.CompletedSteps);
        scope.Release();
        await scope.WaitCompleted(session);
        Assert.Equal(2, tool.CompletedSteps);
        Assert.Equal("succeeded", Outcome(scope.Read((string)first.StructuredContent!["data"]!["activeRequestId"]!)));
    }

    [Fact]
    public async Task Late_post_processing_failure_retains_the_unknown_write()
    {
        using var scope = new Scope("21");
        var tool = new FollowingStep(scope.Tool("SaveProject"), scope.Worker, fail: true);
        using var session = scope.Session(tool);
        var first = await scope.Call(session, "SaveProject", scope.WriteArguments);
        Assert.Equal("in-progress", Outcome(first));
        scope.Release();
        await scope.WaitCompleted(session);
        var final = scope.Read((string)first.StructuredContent!["data"]!["activeRequestId"]!);
        Assert.Equal("unknown", Outcome(final));
        Assert.Equal("OUTCOME_UNKNOWN", (string?)final.StructuredContent?["error"]?["code"]);
        Assert.Single(scope.Worker.Unsettled.Snapshot());
        Assert.Equal(1, tool.CompletedSteps);
    }

    [Fact]
    public async Task A_completed_core_keeps_the_detached_response_and_both_result_ids()
    {
        using var scope = new Scope("19");
        var tool = new ImmediatelyCompleted(scope.Tool("SaveProject"));
        using var session = scope.Session(tool);
        var first = await scope.Call(session, "SaveProject", scope.WriteArguments);
        Assert.Equal("in-progress", Outcome(first));
        Assert.Equal("active-request", (string?)first.StructuredContent?["data"]?["activeRequestId"]);
        Assert.Equal("succeeded", Outcome(scope.Read("active-request")));
        Assert.Equal("final-request", (string?)scope.Read("final-request").StructuredContent?["meta"]?["requestId"]);
    }

    [Fact]
    public async Task Late_attach_updates_the_binding_and_the_stdio_tool_path_retains_its_result()
    {
        using var scope = new Scope("19");
        var tool = scope.Tool("ConnectPortal");
        using var session = scope.Session(tool, operatorOwned: false);
        var first = await scope.Call(session, "ConnectPortal", new() { ["processId"] = 123 }, ai: true);
        Assert.Equal("in-progress", Outcome(first));
        scope.Release();
        await scope.WaitCompleted(session);
        Assert.Equal(123, (int?)scope.Worker.OperatorSnapshot()["binding"]?["processId"]);
        Assert.Equal("succeeded", Outcome(scope.Read((string)first.StructuredContent!["data"]!["activeRequestId"]!)));
    }

    [Fact]
    public async Task Late_result_completes_the_audit_and_approval_once_with_the_final_outcome()
    {
        using var scope = new Scope("19");
        string root = TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        string settingsPath = Path.GetFullPath(TiaOpenness.Shared.ApprovalSettings.SettingsPath);
        Assert.True(settingsPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || settingsPath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            "Tests require an isolated TIA_MCP_DATA_DIRECTORY: " + settingsPath);
        var log = new TiaOpenness.Shared.AuditLog(Path.Combine(scope.DirectoryPath, "audit"));
        using var audit = TiaOpenness.Shared.AuditInvocation.UseLog(log);
        using var pipe = TiaOpenness.Shared.ApprovalPipe.CreateServer(TiaOpenness.Shared.ApprovalPipe.CurrentName,
            TiaOpenness.Shared.ApprovalPipe.CurrentSid, true);
        using var deadline = new CancellationTokenSource(TestDeadlines.Hang);
        async Task<TiaOpenness.Shared.PendingApproval> Receive()
        {
            await pipe.WaitForConnectionAsync(deadline.Token);
            return await TiaOpenness.Shared.ApprovalFrames.Read<TiaOpenness.Shared.PendingApproval>(pipe, deadline.Token);
        }
        var completion = Receive();
        using var traceText = new StringWriter();
        using var trace = new TextWriterTraceListener(traceText);
        Trace.Listeners.Add(trace);
        try
        {
            var tool = scope.Tool("SaveProject", approved: true);
            using var session = scope.Session(tool);
            var first = await scope.Call(session, "SaveProject", scope.WriteArguments);
            Assert.Equal("in-progress", Outcome(first));
            Assert.False(completion.IsCompleted);
            Assert.DoesNotContain(log.Read(), row => row.Event == "end");
            scope.Release();
            await scope.WaitCompleted(session);
            Assert.Equal("succeeded", Assert.Single(log.Read(), row => row.Event == "end").Outcome);
            Assert.Equal(1, scope.Approvals);
            Assert.True(log.Verify().Passed);
            var approved = await completion.WaitAsync(TestDeadlines.Hang);
            Assert.Equal("result", approved.Kind);
            Assert.Equal("succeeded", approved.Outcome);
            Assert.Equal((string?)first.StructuredContent?["meta"]?["requestId"], approved.RequestId);
        }
        catch (Exception error) { throw new InvalidOperationException(traceText.ToString(), error); }
        finally { Trace.Listeners.Remove(trace); }
    }

    [Theory, MemberData(nameof(Releases))]
    public void GetCallResult_is_registered_and_unknown_ids_are_not_found(string release)
    {
        using var scope = new Scope(release);
        var catalog = new Engine.FoundationCatalog(release, Array.Empty<McpServerTool>());
        Assert.Equal("host", catalog.All["GetCallResult"].Execution);
        Assert.Contains(catalog.Lite, tool => tool.Name == "GetCallResult");
        Assert.Equal("NOT_FOUND", (string?)scope.Read("missing-call").StructuredContent?["error"]?["code"]);
    }

    internal static string? Outcome(CallToolResult result) => (string?)result.StructuredContent?["meta"]?["outcome"];

    private sealed class DisconnectAfterBudget(Scope scope) : McpServerTool
    {
        public override Tool ProtocolTool => scope.Tool("DisconnectPortal").ProtocolTool;
        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            const string id = "disconnect-active";
            using var correlation = Foundation.FoundationWorkerRequest.Use(id, "DisconnectPortal");
            using (new ProductionWorkerBudgets(scope.Worker))
                await scope.Worker.Call("FaultFixtureArm", new() { ["method"] = "adapter.Disconnect" }, cancellationToken);
            var core = scope.Worker.Call("Disconnect", new(), cancellationToken);
            await scope.WaitDispatched();
            typeof(Foundation.WorkerClient).GetMethod("SetRunning", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(scope.Worker, new object[] { new JsonObject { ["requestId"] = id, ["tool"] = "DisconnectPortal",
                    ["sinceUtc"] = DateTimeOffset.UtcNow.ToString("O") } });
            try { await core; }
            catch (Exception error)
            {
                return Foundation.FoundationV4Result.Failure("21", "DisconnectPortal", id, true, true, null, error);
            }
            throw new InvalidOperationException("The synthetic worker must exit during the disconnect call.");
        }
    }

    private sealed class ImmediatelyCompleted(McpServerTool metadata) : McpServerTool
    {
        public override Tool ProtocolTool => metadata.ProtocolTool;
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            Engine.LateCalls.Detach(new JsonObject { ["requestId"] = "active-request", ["tool"] = "SaveProject",
                ["sinceUtc"] = DateTimeOffset.UtcNow.ToString("O") });
            var envelope = Envelope.Create(new JsonObject(), null,
                new Meta(DateTimeOffset.UtcNow, "19", "SaveProject", "final-request", TiaMcp.Logic.V4.Outcome.Succeeded,
                    Execution.Completed, false, BehaviorPolicy.NotApplicable, Completeness.Complete, null, Array.Empty<Warning>()));
            var body = JsonNode.Parse(V4Json.Serialize(envelope));
            return ValueTask.FromResult(new CallToolResult { IsError = false, StructuredContent = body,
                Content = new[] { new TextContentBlock { Text = body!.ToJsonString() } } });
        }
    }

    private sealed class FollowingStep(McpServerTool inner, Foundation.WorkerClient worker, bool fail = false) : McpServerTool
    {
        internal int CompletedSteps;
        public override Tool ProtocolTool => inner.ProtocolTool;
        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            var result = await inner.InvokeAsync(request, cancellationToken);
            CompletedSteps++;
            if (fail) throw new IOException("Synthetic post-processing failure.");
            await worker.Call("ReadState", new(), cancellationToken);
            CompletedSteps++;
            return result;
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly string release;
        private readonly string image;
        private readonly string? previousFault = Environment.GetEnvironmentVariable("TIA_MCP_PLC_WORKER_FAULT");
        private readonly string? previousGate = Environment.GetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE");
        private readonly string gate = Path.Combine(TestPaths.RepositoryRoot(AppContext.BaseDirectory), "bin-build", "running-" + Guid.NewGuid().ToString("N"));
        private readonly Engine.EngineHostPipeline pipeline;
        private readonly Foundation.FoundationHostPorts.Services services;
        internal readonly Foundation.WorkerClient Worker;
        internal readonly Process Child;
        internal bool Disposed;
        internal int Approvals;
        internal string DirectoryPath => gate;
        internal TiaMcp.WorkerChannel.ChannelClient Channel => (TiaMcp.WorkerChannel.ChannelClient)typeof(Foundation.WorkerClient)
            .GetField("channel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Worker)!;
        internal async Task WaitProgressDropped()
        {
            using var deadline = new CancellationTokenSource(TestDeadlines.Hang);
            bool Dropped()
            {
                var pending = typeof(TiaMcp.WorkerChannel.ChannelClient).GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(Channel);
                return pending?.GetType().GetField("ProgressDropped", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(pending) is true;
            }
            while (!Channel.Poisoned && !Dropped()) await Task.Delay(5, deadline.Token);
        }
        internal int Pid => (int)Worker.OperatorSnapshot()["pid"]!;
        internal JsonObject WriteArguments => new() { ["dryRun"] = false, ["confirm"] = true,
            ["expectedProjectFile"] = "C:/fixture.ap" + release };
        internal Scope(string release)
        {
            this.release = release;
            Directory.CreateDirectory(gate);
            Environment.SetEnvironmentVariable("TIA_MCP_PLC_WORKER_FAULT", "hang");
            Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE", gate);
            string exe = TestPaths.WorkerFixture("TiaMcp.PlcWorker.FaultFixture", "TiaMcp.PlcWorker.FaultFixture.exe", "net48");
            image = Path.GetFullPath(exe);
            TestPaths.WriteFixtureFile(Path.Combine(Path.GetDirectoryName(exe)!, "TiaMcp.Adapter." + release + ".dll"), "offline fixture identity");
            Worker = new Foundation.WorkerClient(release, exe, Path.GetDirectoryName(exe)!, true,
                timeoutConfig: "{\"default\":0.02,\"firstAttach\":0.02}") { Bundled = false,
                    SharedSession = Engine.ReleaseCatalogData.Releases[release].SharedSession };
            using (new ProductionWorkerBudgets(Worker))
            {
                Worker.Call("Attach", new() { ["processId"] = Environment.ProcessId }, CancellationToken.None).GetAwaiter().GetResult();
                Worker.Call("ReadState", new(), CancellationToken.None).GetAwaiter().GetResult();
            }
            Child = Process.GetProcessById(Pid);
            services = Foundation.FoundationHostPorts.CreateServices(Worker, release, () => new JsonObject { ["ready"] = true }, AppContext.BaseDirectory);
            pipeline = new Engine.EngineHostPipeline(release, services, Array.Empty<McpServerTool>());
        }
        internal McpServerTool Tool(string name, string? operation = null, bool gated = true, bool approved = false)
        {
            var definition = Foundation.FoundationTools.Definitions.Single(tool => tool.Name == name);
            if (operation != null) definition = definition with { Operation = operation };
            return new Foundation.FoundationV4Tool(new Foundation.FoundationTool(definition, Worker), release,
                () => new(approved, 1), approvalWait: approved ? (pending, _, _) => {
                    Approvals++;
                    ProductionWorkerBudgets.Live(Worker)["default"] = TimeSpan.FromMilliseconds(20);
                    return Task.FromResult(new TiaOpenness.Shared.ApprovalOutcome(pending, false, null));
                } : null, readinessForTest: () => {
                    if (gated)
                        using (new ProductionWorkerBudgets(Worker))
                            Worker.Call("FaultFixtureArm", new() { ["method"] = "adapter." + definition.Operation }, CancellationToken.None)
                                .GetAwaiter().GetResult();
                    if (approved) ProductionWorkerBudgets.Live(Worker)["default"] = TiaOpenness.Shared.AuditInvocation.IsReadOnlyPreview
                        ? TiaMcp.WorkerChannel.WorkerTimeoutPolicy.Defaults["default"] : TimeSpan.FromMilliseconds(20);
                    return new JsonObject { ["ready"] = true };
                });
        }
        internal Foundation.HostSession Session(McpServerTool tool, bool operatorOwned = true)
            => new(release, "fixture", operatorOwned, new[] { tool }, new[] { tool }, services.OperatorSnapshot,
                () => { Disposed = true; Worker.Dispose(); }, nativeConsentDirectory: gate,
                lateResults: Worker.LateResults, unsettled: Worker.Unsettled);
        internal async Task<CallToolResult> Call(Foundation.HostSession session, string name, JsonObject arguments,
            bool ai = false, CancellationToken token = default)
        {
            using var context = pipeline.EnterSession();
            var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, WorkerRobustnessTests.ServerProxy>()) {
                Params = new CallToolRequestParams { Name = name,
                    Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } };
            var tools = ai ? session.Tools : session.OperatorTools;
            return await tools.Single(tool => tool.ProtocolTool.Name == name).InvokeAsync(request, token).AsTask().WaitAsync(TestDeadlines.Hang);
        }
        internal CallToolResult Read(string id) { using var context = pipeline.EnterSession(); return Host.GetCallResult(id); }
        internal void Release()
        {
            foreach (var (family, budget) in TiaMcp.WorkerChannel.WorkerTimeoutPolicy.Defaults)
                ProductionWorkerBudgets.Live(Worker)[family] = budget;
            File.WriteAllText(Path.Combine(gate, "release"), "");
        }
        internal async Task WaitDispatched()
        {
            using var deadline = new CancellationTokenSource(TestDeadlines.Hang);
            while (!File.Exists(Path.Combine(gate, "dispatched"))) await Task.Delay(5, deadline.Token);
        }
        internal async Task WaitCompleted(Foundation.HostSession session)
        {
            using var deadline = new CancellationTokenSource(TestDeadlines.Hang);
            while ((bool?)session.Snapshot()["callInProgress"] == true) await Task.Delay(5, deadline.Token);
        }
        public void Dispose()
        {
            Release();
            if (!Child.HasExited)
                Assert.Equal(image,
                    Path.GetFullPath(Child.MainModule!.FileName), ignoreCase: true);
            Worker.Dispose();
            pipeline.Dispose();
            Assert.True(Child.WaitForExit(TestDeadlines.HangMilliseconds));
            Child.Dispose();
            Environment.SetEnvironmentVariable("TIA_MCP_PLC_WORKER_FAULT", previousFault);
            Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE", previousGate);
        }
    }
}
