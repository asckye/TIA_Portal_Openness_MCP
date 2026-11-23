extern alias foundationhost;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Engine.Tests.Shared;
using TiaMcp.FoundationHost;
using TiaOpenness.Shared;
using TiaMcp.WorkerChannel;
using Xunit;
using WorkerClient = foundationhost::TiaMcp.FoundationHost.WorkerClient;
using HostSession = foundationhost::TiaMcp.FoundationHost.HostSession;
using FoundationTool = foundationhost::TiaMcp.FoundationHost.FoundationTool;
using FoundationTools = foundationhost::TiaMcp.FoundationHost.FoundationTools;
using FoundationV4Tool = foundationhost::TiaMcp.FoundationHost.FoundationV4Tool;

[Collection(WorkerDataDirectoryCollection.Name)]
public sealed class NoLockTakeoverTests
{
    public static IEnumerable<object[]> Releases() => NoLockRecoveryTests.Releases();

    [Fact]
    public async Task Audit_failure_refuses_takeover_with_the_holder_still_connected()
    {
        using var scope = new Scope("21");
        await scope.Bind();
        string path = Path.Combine(scope.Leases, "unavailable-audit");
        File.WriteAllText(path, "fixture");
        using var audit = AuditInvocation.UseLog(new AuditLog(path));
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "ConnectPortal"), scope.Newcomer), "21");
        var response = await scope.NewSession.Invoke(tool, Request("ConnectPortal", new() {
            ["processId"] = Environment.ProcessId, ["takeOver"] = true }), true, CancellationToken.None);
        var body = response.StructuredContent!;
        Assert.Equal("RESOURCE_UNAVAILABLE", (string?)body["error"]?["code"]);
        Assert.Contains("audit start", (string?)body["error"]?["message"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.False(scope.Child.HasExited);
        Assert.DoesNotContain("adapter.Disconnect", scope.Trace);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Approval_wait_counts_as_a_call_in_flight(string release)
    {
        using var scope = new Scope(release);
        await scope.Bind();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "SaveProject"), scope.Holder), release,
            () => new(true, 120), async (pending, _, _) => {
                entered.TrySetResult(); await decision.Task;
                return new ApprovalOutcome(pending, false, "denied");
            });
        var request = Request("SaveProject", new() { ["dryRun"] = false, ["confirm"] = true,
            ["expectedProjectFile"] = "C:/fixture.ap" + release });
        var waiting = scope.HolderSession.Invoke(tool, request, true, CancellationToken.None).AsTask();
        try
        {
            await entered.Task.WaitAsync(TestDeadlines.Hang);
            var refusal = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Connect(scope.Newcomer, true));
            Assert.Equal("tia-held-by-session", refusal.Condition);
            Assert.Contains("SaveProject", refusal.Message);
            Assert.Contains("approval wait", refusal.Message);
            Assert.Contains(scope.HolderSession.SessionKey, refusal.Message);
            Assert.False(scope.Child.HasExited);
        }
        finally { decision.TrySetResult(); await waiting.WaitAsync(TestDeadlines.Hang); }
        await scope.Connect(scope.Newcomer, true);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Workbench_attachment_and_operator_ownership_protect_the_holder(bool operatorOwned)
    {
        using var scope = new Scope("19", operatorOwned);
        await scope.Bind();
        using var workbench = operatorOwned ? null : scope.HolderSession.AttachOperator();
        scope.Clock.Advance(TimeSpan.FromMinutes(10));
        foreach (bool takeOver in new[] { false, true })
        {
            var refusal = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Connect(scope.Newcomer, takeOver));
            Assert.Equal("tia-held-by-session", refusal.Condition);
            Assert.Contains("Workbench", refusal.Message);
            Assert.False(scope.Child.HasExited);
            Assert.DoesNotContain(scope.Trace, method => method == "adapter.Disconnect");
        }
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Worker_started_portal_does_not_give_way(string release)
    {
        using var scope = new Scope(release);
        await scope.Holder.Call("project-lifecycle.ConnectIsolatedPortal", new(), CancellationToken.None);
        scope.Clock.Advance(TimeSpan.FromMinutes(10));
        foreach (bool takeOver in new[] { false, true })
        {
            var refusal = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Connect(scope.Newcomer, takeOver, 123));
            Assert.Equal("tia-held-by-session", refusal.Condition);
            Assert.Contains("started its TIA", refusal.Message);
            Assert.False(scope.Child.HasExited);
            Assert.DoesNotContain(scope.Trace, method => method == "adapter.Disconnect");
        }
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task An_unanswered_initial_attach_names_its_holder_and_cannot_be_taken_over(string release)
    {
        using var scope = new Scope(release);
        File.WriteAllText(Path.Combine(scope.Leases, "block-attach"), "");
        File.WriteAllText(Path.Combine(scope.Leases, "report-dialog"), "");
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "ConnectPortal"), scope.Holder), release);
        var response = await scope.HolderSession.Invoke(tool, Request("ConnectPortal", new() {
            ["processId"] = Environment.ProcessId }), true, CancellationToken.None).AsTask().WaitAsync(TestDeadlines.Hang);
        Assert.Equal("in-progress", (string?)response.StructuredContent?["meta"]?["outcome"]);
        var refusal = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Connect(scope.Newcomer, true));
        Assert.Equal("tia-held-by-session", refusal.Condition);
        Assert.Contains(scope.HolderSession.SessionKey, refusal.Message);
        Assert.Contains("ConnectPortal", refusal.Message);
        Assert.Contains("Fixture handover dialog", refusal.Message);
        Assert.True(scope.Holder.CallRunning);
        Assert.False(scope.Child.HasExited);
        Assert.DoesNotContain(scope.Trace, method => method == "adapter.Disconnect");
        await scope.Complete(scope.HolderSession);
        string id = (string)response.StructuredContent!["data"]!["activeRequestId"]!;
        Assert.Equal("succeeded", (string?)scope.Holder.LateResults.Read(release, id, scope.Holder.Unsettled)!.StructuredContent?["meta"]?["outcome"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Holder_disconnect_blocked_by_tia_is_not_terminated(string release)
    {
        using var scope = new Scope(release);
        await scope.Bind();
        File.WriteAllText(Path.Combine(scope.Leases, "block-disconnect"), "");
        File.WriteAllText(Path.Combine(scope.Leases, "report-dialog"), "");
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "ConnectPortal"), scope.Newcomer), release);
        var response = await scope.NewSession.Invoke(tool, Request("ConnectPortal", new() {
            ["processId"] = Environment.ProcessId, ["takeOver"] = true }), true, CancellationToken.None).AsTask().WaitAsync(TestDeadlines.Hang);
        Assert.Equal("in-progress", (string?)response.StructuredContent?["meta"]?["outcome"]);
        Assert.True(scope.Holder.CallRunning);
        Assert.False(scope.Child.HasExited);
        Assert.False(scope.HolderSession.CanGiveWay(TimeSpan.Zero));
        Assert.Throws<IOException>(() => File.ReadAllText(Path.Combine(scope.Leases, Environment.ProcessId + "-1.lease")));
        var busy = await Assert.ThrowsAsync<AdapterPreconditionException>(() =>
            scope.Newcomer.Call("ListProjects", new(), CancellationToken.None).WaitAsync(TestDeadlines.Hang));
        Assert.Equal("worker-call-running", busy.Condition);
        string id = (string)response.StructuredContent!["data"]!["activeRequestId"]!;
        var dialog = new JsonObject { ["event"] = "Confirmation", ["utc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["caption"] = "Later handover dialog", ["text"] = "Still waiting", ["answer"] = "unanswered", ["choices"] = new JsonArray("Yes") };
        typeof(WorkerClient).GetMethod("ReceiveDiagnostic", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scope.Holder, new object[] { "TIA_MCP_DIALOG " + dialog.ToJsonString() });
        Assert.Equal("Later handover dialog", (string?)scope.Newcomer.LateResults.Read(release, id, scope.Newcomer.Unsettled)!
            .StructuredContent?["data"]?["running"]?["dialog"]?["caption"]);
        var stateTool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "GetSessionState"), scope.Holder), release);
        var state = await scope.HolderSession.Invoke(stateTool, Request("GetSessionState", new()), true, CancellationToken.None);
        Assert.True((bool?)state.StructuredContent?["ok"]);
        Assert.NotNull(state.StructuredContent?["data"]?["sessionState"]?["running"]);
        await scope.Complete(scope.NewSession);
        var final = scope.Newcomer.LateResults.Read(release, id, scope.Newcomer.Unsettled)!;
        Assert.Equal("succeeded", (string?)final.StructuredContent?["meta"]?["outcome"]);
        Assert.False(scope.Child.HasExited);
        Assert.False(scope.Holder.CallRunning);
        Assert.Single(scope.Trace, method => method == "adapter.Disconnect");
        Assert.DoesNotContain(scope.Trace, method => method.Contains("Save", StringComparison.Ordinal) || method.Contains("Close", StringComparison.Ordinal));
        var taken = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Holder.Call("ListProjects", new(), CancellationToken.None));
        Assert.Equal("taken-over", taken.Condition);
        Assert.Contains(scope.NewSession.SessionKey, taken.Message);
    }

    [Fact]
    public void Idle_threshold_and_handover_reservation_use_the_injected_clock()
    {
        using var scope = new Scope("19");
        scope.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));
        Assert.False(scope.HolderSession.CanGiveWay(TimeSpan.FromMinutes(10)));
        scope.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(scope.HolderSession.TryBeginGiveWay(TimeSpan.FromMinutes(10)));
        Assert.False(scope.HolderSession.CanGiveWay(TimeSpan.Zero));
        Assert.False(scope.HolderSession.TryBeginHostShutdown());
        scope.HolderSession.FinishGiveWay();
        Assert.True(scope.HolderSession.CanGiveWay(TimeSpan.FromMinutes(10)));
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Faulted_abandoned_holder_gives_way_through_the_guarded_stop(string release)
    {
        using var scope = new Scope(release);
        await scope.Bind();
        using var child = Process.GetProcessById(scope.Child.Id);
        string trace = Path.Combine(scope.Leases, "trace-" + child.Id + ".jsonl");
        AssertOwned(child);
        scope.Holder.Unsettled.Record("faulted-owner-write", "SaveProject", "fixture", DateTimeOffset.UtcNow);
        Fault(scope.Holder);
        var refused = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Connect(scope.Newcomer));
        Assert.Equal("tia-held-by-session", refused.Condition);
        Assert.Contains("takeOver=true", refused.Message);
        Assert.False(child.HasExited);
        scope.Clock.Advance(TimeSpan.FromMinutes(10));
        await scope.Connect(scope.Newcomer);
        Assert.True(child.HasExited);
        Assert.Equal("faulted-owner-write", Assert.Single(scope.Newcomer.Unsettled.Snapshot()).RequestId);
        Assert.Single(scope.Audit.Read(), row => row.Outcome == "taken-over-worker-stopped");
        string[] methods = File.ReadAllLines(trace).Select(line => (string)JsonNode.Parse(line)!["method"]!).ToArray();
        Assert.DoesNotContain(methods, method => method.Contains("Save", StringComparison.Ordinal) || method.Contains("Close", StringComparison.Ordinal));
        var taken = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Holder.Call("ListProjects", new(), CancellationToken.None));
        Assert.Equal("taken-over", taken.Condition);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Explicit_takeover_of_a_faulted_idle_holder_is_audited(string release)
    {
        using var scope = new Scope(release);
        await scope.Bind();
        using var child = Process.GetProcessById(scope.Child.Id);
        AssertOwned(child);
        Fault(scope.Holder);
        await scope.Connect(scope.Newcomer, true);
        Assert.True(child.HasExited);
        Assert.Single(scope.Audit.Read(), row => row.Outcome == "taken-over-worker-stopped");
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Faulted_holder_with_an_unanswered_request_keeps_H1_priority(string release)
    {
        using var scope = new Scope(release);
        await scope.Bind();
        var channel = Fault(scope.Holder, unanswered: true);
        scope.Clock.Advance(TimeSpan.FromMinutes(10));
        foreach (bool takeOver in new[] { false, true })
        {
            var refused = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Connect(scope.Newcomer, takeOver));
            Assert.Equal("tia-held-by-session", refused.Condition);
            Assert.Contains("This clears when the active call returns", refused.Message);
            Assert.DoesNotContain("takes an eligible", refused.Message);
            Assert.False(scope.Child.HasExited);
        }
        typeof(ChannelClient).GetField("unknown", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(channel, false);
        Assert.DoesNotContain(scope.Trace, method => method.Contains("Save", StringComparison.Ordinal) || method.Contains("Close", StringComparison.Ordinal));
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Faulted_worker_started_portal_keeps_H2_priority(string release)
    {
        using var scope = new Scope(release);
        await scope.Holder.Call("project-lifecycle.ConnectIsolatedPortal", new(), CancellationToken.None);
        Fault(scope.Holder);
        scope.Clock.Advance(TimeSpan.FromMinutes(10));
        foreach (bool takeOver in new[] { false, true })
        {
            var refused = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Connect(scope.Newcomer, takeOver, 123));
            Assert.Equal("tia-held-by-session", refused.Condition);
            Assert.Contains("This worker started its TIA instance", refused.Message);
            Assert.False(scope.Child.HasExited);
        }
        Assert.DoesNotContain(scope.Trace, method => method.Contains("Save", StringComparison.Ordinal) || method.Contains("Close", StringComparison.Ordinal));
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task A_previously_disconnected_caller_finishes_a_detached_handover(string release)
    {
        using var scope = new Scope(release);
        await scope.Bind();
        await scope.Connect(scope.Newcomer, pid: 456);
        var process = (Process)typeof(WorkerClient).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope.Newcomer)!;
        using var previousChild = Process.GetProcessById(process.Id);
        AssertOwned(previousChild);
        await scope.Newcomer.Call("Disconnect", new(), CancellationToken.None);
        typeof(WorkerClient).GetField("takenOverBy", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(scope.Newcomer, "prior-owner");
        File.WriteAllText(Path.Combine(scope.Leases, "block-disconnect"), "");
        File.WriteAllText(Path.Combine(scope.Leases, "report-dialog"), "");
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "ConnectPortal"), scope.Newcomer), release);
        var response = await scope.NewSession.Invoke(tool, Request("ConnectPortal", new() {
            ["processId"] = Environment.ProcessId, ["takeOver"] = true }), true, CancellationToken.None).AsTask().WaitAsync(TestDeadlines.Hang);
        Assert.Equal("in-progress", (string?)response.StructuredContent?["meta"]?["outcome"]);
        string id = (string)response.StructuredContent!["data"]!["activeRequestId"]!;
        var busy = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Newcomer.Call("Disconnect", new(), CancellationToken.None));
        Assert.Equal("worker-call-running", busy.Condition);
        await scope.Complete(scope.NewSession);
        Assert.Equal("succeeded", (string?)scope.Newcomer.LateResults.Read(release, id, scope.Newcomer.Unsettled)!
            .StructuredContent?["meta"]?["outcome"]);
        Assert.False(scope.Newcomer.CallRunning);
        Assert.NotNull(await scope.Newcomer.Call("ListProjects", new(), CancellationToken.None));
        Assert.True(previousChild.HasExited);
        Assert.False(scope.Child.HasExited);
        Assert.DoesNotContain(scope.Trace, method => method.Contains("Save", StringComparison.Ordinal) || method.Contains("Close", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Approval_refusal_only_advises_waiting_even_when_take_over_is_true()
    {
        using var scope = new Scope("19");
        await scope.Bind();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "SaveProject"), scope.Holder), "19",
            () => new(true, 120), async (pending, _, _) => { entered.TrySetResult(); await decision.Task;
                return new ApprovalOutcome(pending, false, "denied"); });
        var waiting = scope.HolderSession.Invoke(tool, Request("SaveProject", new() { ["dryRun"] = false, ["confirm"] = true,
            ["expectedProjectFile"] = "C:/fixture.ap19" }), true, CancellationToken.None).AsTask();
        try
        {
            await entered.Task.WaitAsync(TestDeadlines.Hang);
            var refused = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Connect(scope.Newcomer, true));
            Assert.Contains("approval wait", refused.Message);
            Assert.Contains("This clears when the active call returns", refused.Message);
            Assert.DoesNotContain("takeOver=true", refused.Message);
        }
        finally { decision.TrySetResult(); await waiting.WaitAsync(TestDeadlines.Hang); }
    }

    [Fact]
    public async Task Faulted_handover_retains_host_ids_beyond_the_transport_snapshot_limit()
    {
        using var scope = new Scope("19");
        await scope.Bind();
        var ids = Enumerable.Range(0, ChannelLimits.UnsettledRequestCount + 1).Select(index => "unknown-" + index).ToArray();
        foreach (string id in ids) scope.Holder.Unsettled.Record(id, "SaveProject", id, DateTimeOffset.UtcNow);
        Fault(scope.Holder);
        await scope.Connect(scope.Newcomer, true);
        Assert.Equal(ids.Order(StringComparer.Ordinal), scope.Newcomer.Unsettled.Snapshot().Select(call => call.RequestId).Order(StringComparer.Ordinal));
        Assert.All(scope.Newcomer.Unsettled.Snapshot(), call => Assert.True(call.FromPreviousOwner));
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task A_faulted_taken_over_session_can_connect_again_after_local_disconnect(string release)
    {
        using var scope = new Scope(release);
        await scope.Bind();
        Fault(scope.Holder);
        await scope.Connect(scope.Newcomer, true);
        var disconnected = await scope.Holder.Call("Disconnect", new(), CancellationToken.None);
        Assert.Equal("disconnected", (string?)disconnected?["SessionState"]);
        Assert.False((bool?)disconnected?["WorkerAcknowledged"]);
        var taken = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Holder.Call("ListProjects", new(), CancellationToken.None));
        Assert.Equal("taken-over", taken.Condition);
        await scope.Connect(scope.Holder, true);
        Assert.NotNull(await scope.Holder.Call("ListProjects", new(), CancellationToken.None));
        Assert.False(scope.Holder.CallRunning);
        var transferred = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Newcomer.Call("ListProjects", new(), CancellationToken.None));
        Assert.Equal("taken-over", transferred.Condition);
    }

    private static ChannelClient Fault(WorkerClient worker, bool unanswered = false)
    {
        var channel = (ChannelClient)typeof(WorkerClient).GetField("channel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker)!;
        channel.Invalidate(unanswered ? new TimeoutException("Fixture unanswered native call.") : new IOException("Fixture channel fault."));
        if (unanswered) typeof(ChannelClient).GetField("unknown", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(channel, true);
        return channel;
    }

    private static void AssertOwned(Process child)
        => Assert.StartsWith(Path.GetFullPath(TestPaths.RepositoryRoot(AppContext.BaseDirectory)) + Path.DirectorySeparatorChar,
            Path.GetFullPath(child.MainModule!.FileName), StringComparison.OrdinalIgnoreCase);

    private static RequestContext<CallToolRequestParams> Request(string name, JsonObject arguments)
        => new(DispatchProxy.Create<IMcpServer, WorkerRobustnessTests.ServerProxy>()) {
            Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } };

    private sealed class Scope : IDisposable
    {
        private readonly Dictionary<string, string?> environment = new();
        private readonly string root = Path
            .Combine(TestPaths.RepositoryRoot(AppContext.BaseDirectory), "bin-build", "takeover-" + Guid.NewGuid().ToString("N"));
        private readonly IDisposable audit;
        private readonly string release;
        internal AuditLog Audit { get; }
        internal WorkerClient Holder { get; }
        internal WorkerClient Newcomer { get; }
        internal HostSession HolderSession { get; }
        internal HostSession NewSession { get; }
        internal FixtureSessionClock Clock { get; } = new();
        internal string Leases => Path.Combine(root, "leases");
        internal Process Child => (Process)typeof(WorkerClient).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Holder)!;
        internal string[] Trace => File.ReadAllLines(Path.Combine(Leases, "trace-" + Child.Id + ".jsonl"))
            .Select(line => (string)JsonNode.Parse(line)!["method"]!).ToArray();
        internal Scope(string release, bool operatorOwned = false)
        {
            this.release = release;
            Directory.CreateDirectory(Leases);
            Set("TIA_MCP_FIXTURE_LEASE_ROOT", Leases);
            Set("TIA_MCP_FIXTURE_SLOW_GATE", root);
            string image = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
            TestPaths.WriteFixtureFile(Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter." + release + ".dll"), "offline session fixture");
            // These fixture sessions invoke Foundation tools directly instead of entering EngineHostPipeline.
            Holder = new(release, image, AppContext.BaseDirectory, true) { Bundled = false, SharedSession = false, SpillsDirectoryForTest = root };
            Newcomer = new(release, image, AppContext.BaseDirectory, true) { Bundled = false, SharedSession = false, SpillsDirectoryForTest = root };
            HolderSession = new(release, "fixture", operatorOwned, [], [], Holder.OperatorSnapshot, () => { },
                lateResults: Holder.LateResults, unsettled: Holder.Unsettled, clock: Clock);
            NewSession = new(release, "fixture", false, [], [], Newcomer.OperatorSnapshot, () => { },
                lateResults: Newcomer.LateResults, unsettled: Newcomer.Unsettled, clock: Clock);
            Holder.OwnerSession = HolderSession; Newcomer.OwnerSession = NewSession;
            Audit = new AuditLog(Path.Combine(root, "audit"));
            audit = AuditInvocation.UseLog(Audit);
        }
        private void Set(string name, string value)
        { environment[name] = Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name, value); }
        internal Task<JsonNode?> Connect(WorkerClient worker, bool takeOver = false, int? pid = null)
            => worker.Call("Attach", new() { ["processId"] = pid ?? Environment.ProcessId, ["takeOver"] = takeOver }, CancellationToken.None);
        internal async Task Bind()
        {
            await Connect(Holder);
            await Holder
                .Call("BindProject", new() { ["projectName"] = "fixture", ["expectedProjectFile"] = "C:/fixture.ap" + release }, CancellationToken.None);
        }
        internal void Release() => File.WriteAllText(Path.Combine(root, "release"), "");
        internal async Task Complete(HostSession session)
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Observe()
            {
                if ((bool?)session.Snapshot()["callInProgress"] == false) completed.TrySetResult();
            }
            session.StateChanged += Observe;
            try
            {
                Release(); Observe();
                await completed.Task.WaitAsync(TestDeadlines.Hang);
            }
            finally { session.StateChanged -= Observe; }
        }
        public void Dispose()
        {
            Release();
            HolderSession.Dispose(); NewSession.Dispose();
            foreach (var worker in new[] { Holder, Newcomer })
            {
                var child = (Process?)typeof(WorkerClient).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker);
                if (child is { HasExited: false })
                {
                    Assert.StartsWith(Path.GetFullPath(TestPaths.RepositoryRoot(AppContext.BaseDirectory)) + Path.DirectorySeparatorChar,
                        Path.GetFullPath(child.MainModule!.FileName), StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(Path.GetFullPath(child.StartInfo.FileName), Path.GetFullPath(child.MainModule!.FileName), ignoreCase: true);
                }
                worker.Dispose();
            }
            audit.Dispose();
            foreach (var variable in environment) Environment.SetEnvironmentVariable(variable.Key, variable.Value);
        }
    }
}
