extern alias foundationhost;
using System.Diagnostics;
using System.Text.Json;
using TiaMcp.FoundationHost;
using TiaMcp.PlcWorker;
using TiaMcp.WorkerChannel;
using TiaMcpServer.Siemens;
using TiaOpenness.Shared;
using Xunit;

public sealed class FoundationProcessLeaseTests
{
    private static ChannelRequest Request(string method, string args = "{}") => new(1, "adapter." + method, args, (_, _) => { });
    private static ChannelRequest Attach(int pid) => Request("Attach", JsonSerializer.Serialize(new { processId = pid }));
    private static ChannelRequest Candidate(int pid, string mode = "apply") => Request(WorkerOperations.SessionCandidate,
        JsonSerializer.Serialize(new { mode, candidate = new { Action = "execute", Check = new { Request = new { Action = "attach", ProcessId = pid } } } }));
    private static ChannelFailure Rejection(string message) => new(message, -32603, ChannelOutcome.RejectedBeforeNative,
        "{\"exceptionType\":\"InvalidOperationException\"}");

    private sealed class Session : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "tia-foundation-lease-" + Guid.NewGuid().ToString("N"));
        internal int Pid { get; } = Environment.ProcessId;
        internal long Ticks { get; } = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        internal bool Attached, Detached, Reset, ThrowOnExit;
        internal int NativeCalls, Disposals;
        internal Func<ChannelRequest, ChannelResponse>? Reply;
        internal FoundationProcessLease Boundary { get; }
        internal string FilePath => Path.Combine(Root, Pid + "-" + Ticks + ".lease");
        internal Session()
        {
            Boundary = new(() => Root, request => {
                NativeCalls++;
                if (Reply != null) return Reply(request);
                if (request.Method == "adapter.Disconnect") { Attached = false; Detached = true; }
                else Attached = true;
                return ChannelResponse.Success("{}");
            }, () => Attached, () => Detached, () => Reset, () => {
                Disposals++;
                if (ThrowOnExit) throw new IOException("Fixture detach failed.");
                Attached = false;
            });
        }
        public void Dispose()
        {
            try { Boundary.Dispose(); }
            finally { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        }
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Legacy_and_candidate_attach_reserve_the_same_identity_as_the_engine(bool candidate)
    {
        using var session = new Session();
        Assert.Null(session.Boundary.Dispatch(candidate ? Candidate(session.Pid) : Attach(session.Pid)).Failure);
        var refused = Assert.Throws<InvalidOperationException>(() => PortalProcessLease.Acquire(session.Root, session.Pid, session.Ticks));
        Assert.Equal(SessionBehavior.LeaseReserved, refused.Message);
        Assert.Equal(1, session.NativeCalls);
        Assert.Null(session.Boundary.Dispatch(Request("Disconnect")).Failure);
        Assert.Equal("RELEASED\n", File.ReadAllText(session.FilePath));
        using var engine = PortalProcessLease.Acquire(session.Root, session.Pid, session.Ticks);
        engine.ReleaseCleanly();
    }

    [Fact]
    public void Engine_reservation_refuses_foundation_before_dispatch()
    {
        using var session = new Session();
        using var engine = PortalProcessLease.Acquire(session.Root, session.Pid, session.Ticks);
        var refusal = session.Boundary.Dispatch(Attach(session.Pid)).Failure!;
        Assert.Equal(SessionBehavior.LeaseReserved, refusal.Message);
        Assert.Equal(ChannelOutcome.RejectedBeforeNative, refusal.Outcome);
        Assert.Equal(0, session.NativeCalls);
        engine.ReleaseCleanly();
    }

    [Fact]
    public void Clean_exit_releases_after_native_disposal()
    {
        using var session = new Session();
        session.Boundary.Dispatch(Attach(session.Pid));
        session.Boundary.Dispose(); session.Boundary.Dispose();
        Assert.Equal(1, session.Disposals);
        Assert.Equal("RELEASED\n", File.ReadAllText(session.FilePath));
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Unknown_channel_or_typed_reply_leaves_uncertain_on_exit(bool typed)
    {
        using var session = new Session(); session.Boundary.Dispatch(Attach(session.Pid));
        session.Reply = _ => { session.Reset = typed; return typed ? ChannelResponse.Success("{}")
            : ChannelResponse.Error(new ChannelFailure("Unknown fixture outcome.", -32603, ChannelOutcome.Unknown)); };
        session.Boundary.Dispatch(Request("FixtureUnknown")); session.Boundary.Dispose();
        Assert.Equal("UNCERTAIN\n", File.ReadAllText(session.FilePath));
        Assert.Equal(SessionBehavior.LeaseNotReleased,
            Assert.Throws<InvalidOperationException>(() => PortalProcessLease.Acquire(session.Root, session.Pid, session.Ticks)).Message);
    }

    [Fact]
    public void Acknowledged_disconnect_after_unknown_releases_the_reservation()
    {
        using var session = new Session(); session.Boundary.Dispatch(Attach(session.Pid));
        session.Reset = true; session.Boundary.Dispatch(Request("ReadState"));
        session.Boundary.Dispatch(Request("Disconnect")); session.Boundary.Dispose();
        Assert.Equal("RELEASED\n", File.ReadAllText(session.FilePath));
    }

    [Fact]
    public void Failed_exit_detach_leaves_uncertain_and_is_not_retried()
    {
        using var session = new Session(); session.Boundary.Dispatch(Attach(session.Pid)); session.ThrowOnExit = true;
        Assert.Throws<IOException>(session.Boundary.Dispose); session.Boundary.Dispose();
        Assert.Equal(1, session.Disposals);
        Assert.Equal("UNCERTAIN\n", File.ReadAllText(session.FilePath));
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Rejected_legacy_or_nonissued_candidate_attach_returns_its_reservation(bool candidate)
    {
        using var session = new Session();
        session.Reply = _ => candidate ? ChannelResponse.Success("{\"Attempt\":{\"Issued\":false}}")
            : ChannelResponse.Error(Rejection("Attach rejected before native."));
        session.Boundary.Dispatch(candidate ? Candidate(session.Pid) : Attach(session.Pid));
        Assert.Equal("RELEASED\n", File.ReadAllText(session.FilePath));
    }

    [Fact]
    public void Unknown_attach_without_cached_adoption_keeps_uncertain()
    {
        using var session = new Session();
        session.Reply = _ => ChannelResponse.Error(new ChannelFailure("Attach outcome unknown.", -32603, ChannelOutcome.Unknown));
        session.Boundary.Dispatch(Attach(session.Pid)); session.Boundary.Dispose();
        Assert.Equal("UNCERTAIN\n", File.ReadAllText(session.FilePath));
    }

    [Fact]
    public void Candidate_preview_does_not_reserve_a_process()
    {
        using var session = new Session(); session.Boundary.Dispatch(Candidate(session.Pid, "preview"));
        Assert.False(Directory.Exists(session.Root));
    }

    [Fact]
    public async Task Process_gone_is_refused_without_native_dispatch()
    {
        using var session = new Session(); using var target = Start("target");
        Assert.Equal("READY", await Line(target)); target.Kill(); Assert.True(target.WaitForExit(5000));
        var failure = session.Boundary.Dispatch(Attach(target.Id)).Failure!;
        Assert.Equal(ChannelOutcome.RejectedBeforeNative, failure.Outcome);
        Assert.Equal(0, session.NativeCalls);
        Assert.False(Directory.Exists(session.Root));
    }

    [Fact]
    public void A_new_process_start_identity_ignores_the_old_active_marker()
    {
        using var session = new Session();
        using (PortalProcessLease.Acquire(session.Root, session.Pid, session.Ticks - 1)) { }
        Assert.Null(session.Boundary.Dispatch(Attach(session.Pid)).Failure);
        Assert.Equal("BUSY\n", File.ReadAllText(Path.Combine(session.Root, session.Pid + "-" + (session.Ticks - 1) + ".lease")));
    }

    [Fact]
    public async Task Two_workers_racing_to_attach_have_one_winner_and_clean_release_allows_retry()
    {
        using var session = new Session(); using var a = Start(session.Root, session.Pid.ToString()); using var b = Start(session.Root, session.Pid.ToString());
        Assert.Equal("READY", await Line(a)); Assert.Equal("READY", await Line(b));
        a.StandardInput.WriteLine("attach"); b.StandardInput.WriteLine("attach");
        var replies = await Task.WhenAll(Line(a), Line(b));
        Assert.Equal(new[] { "ATTACHED", SessionBehavior.LeaseReserved }.Order(), replies.Order());
        var winner = replies[0] == "ATTACHED" ? a : b; var loser = winner == a ? b : a;
        Assert.Equal("DISCONNECTED", await Command(winner, "disconnect"));
        Assert.Equal("ATTACHED", await Command(loser, "candidate"));
        Assert.Equal("DISCONNECTED", await Command(loser, "disconnect"));
        await Stop(a); await Stop(b);
        Assert.Equal("RELEASED\n", File.ReadAllText(session.FilePath));
    }

    [Fact]
    public async Task Worker_stdin_eof_releases_an_attached_session_before_the_next_worker()
    {
        using var session = new Session();
        foreach (var attempt in new[] { 1, 2 })
        {
            using var worker = Start(session.Root, session.Pid.ToString());
            Assert.Equal("READY", await Line(worker));
            Assert.Equal("ATTACHED", await Command(worker, "attach"));
            await Stop(worker);
            Assert.Equal("RELEASED\n", File.ReadAllText(session.FilePath));
        }
    }

    [Fact]
    public async Task Job_object_killed_idle_worker_allows_takeover_with_client_evidence()
    {
        using var session = new Session(); using var host = Start("job", session.Root, session.Pid.ToString());
        int pid = int.Parse((await Line(host))!); using var worker = Process.GetProcessById(pid);
        host.StandardInput.WriteLine("exit"); Assert.True(host.WaitForExit(5000)); Assert.True(worker.WaitForExit(5000));
        Assert.Equal("IDLE\n", File.ReadAllText(session.FilePath));
        var response = session.Boundary.Dispatch(Attach(session.Pid));
        Assert.Null(response.Failure);
        Assert.True(JsonDocument.Parse(response.ResultJson).RootElement.GetProperty("PreviousOwnerEndedIdle").GetBoolean());
        Assert.Equal(1, session.NativeCalls);
    }

    [Theory, InlineData(SessionBehavior.LeaseReserved), InlineData(SessionBehavior.LeaseNotReleased)]
    public void Both_host_kinds_preserve_only_the_exact_lease_refusal_text(string message)
    {
        var error = WorkerOperationException.FromChannelFailure(Rejection(message));
        Assert.Equal(message, FoundationV4Result.WorkerRejection(error).Message);
        var body = FoundationV4Result.Failure("19", "ConnectPortal", "fixture", true, true, null, error).StructuredContent!;
        Assert.Equal(message, (string?)body["error"]?["message"]);
        Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.False((bool?)body["meta"]?["requiresSessionReset"]);
        Assert.Equal("The request precondition failed before operation.",
            FoundationV4Result.WorkerRejection(WorkerOperationException.FromChannelFailure(Rejection(message + " private suffix"))).Message);
    }

    [Fact]
    public async Task Job_kill_during_native_fixture_keeps_busy_and_refuses_takeover()
    {
        using var session = new Session(); using var host = Start("job", session.Root, session.Pid.ToString());
        int pid = int.Parse((await Line(host))!); using var worker = Process.GetProcessById(pid);
        Assert.Equal("BUSY", await Command(host, "busy"));
        host.StandardInput.WriteLine("exit"); Assert.True(host.WaitForExit(5000)); Assert.True(worker.WaitForExit(5000));
        Assert.Equal("BUSY\n", File.ReadAllText(session.FilePath));
        Assert.Equal(SessionBehavior.LeaseNotReleased, session.Boundary.Dispatch(Attach(session.Pid)).Failure!.Message);
        Assert.Equal(0, session.NativeCalls);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Host_process_exit_runs_graceful_stop_before_its_job_closes(bool engine)
    {
        using var session = new Session(); using var host = Start("shutdown", session.Root, session.Pid.ToString(), engine ? "21" : "19");
        using var worker = Process.GetProcessById(int.Parse((await Line(host))!));
        host.StandardInput.WriteLine("exit");
        Assert.True(host.WaitForExit(10000)); Assert.Equal(0, host.ExitCode); Assert.True(worker.WaitForExit(10000));
        Assert.Equal("RELEASED\n", File.ReadAllText(Path.Combine(session.Root, session.Pid + "-1.lease")));
    }

    [Theory, InlineData("ACTIVE"), InlineData("BUSY"), InlineData("UNCERTAIN")]
    public void Unsafe_and_legacy_markers_refuse_before_dispatch(string state)
    {
        using var session = new Session(); Directory.CreateDirectory(session.Root);
        File.WriteAllText(session.FilePath, state + "\n");
        Assert.Equal(SessionBehavior.LeaseNotReleased, session.Boundary.Dispatch(Attach(session.Pid)).Failure!.Message);
        Assert.Equal(0, session.NativeCalls);
    }

    [Theory, InlineData("IDLE", true), InlineData("RELEASED", false)]
    public void Takeover_data_reaches_the_connect_result_without_a_new_warning(string state, bool idle)
    {
        using var session = new Session(); Directory.CreateDirectory(session.Root); File.WriteAllText(session.FilePath, state + "\n");
        using var lease = PortalProcessLease.Acquire(session.Root, session.Pid, session.Ticks);
        var raw = System.Text.Json.Nodes.JsonNode.Parse("{\"Stage\":\"attached\",\"OwnsPortal\":false,\"AttemptedPids\":[123],\"Strategy\":\"explicit\",\"LaunchMode\":\"attach\"}")!.AsObject();
        if (lease.PreviousOwnerEndedIdle) raw["PreviousOwnerEndedIdle"] = true;
        var result = V17ProjectEnvelope.Wrap("Connect", "Connection", raw, false);
        Assert.Equal(idle, (bool?)result["previousOwnerEndedIdle"] == true);
        lease.ReleaseCleanly();
    }

    [Fact]
    public void Durable_request_state_overhead_is_measured()
    {
        using var session = new Session(); using var lease = PortalProcessLease.Acquire(session.Root, session.Pid, session.Ticks);
        const int count = 1000;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < count; i++) { lease.BeginRequest(); lease.CompleteRequest(false); }
        double milliseconds = watch.Elapsed.TotalMilliseconds / count;
        Console.WriteLine("LEASE_OVERHEAD requests=" + count + " millisecondsPerRequest=" + milliseconds.ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
        lease.ReleaseCleanly();
    }

    private static Process Start(params string[] args)
    {
        var start = new ProcessStartInfo(Path.ChangeExtension(typeof(FoundationProcessLeaseTests).Assembly.Location, ".exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--process-lease-fixture"); foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }
    private static async Task<string?> Line(Process process) => await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
    private static async Task<string?> Command(Process process, string command) { process.StandardInput.WriteLine(command); return await Line(process); }
    private static async Task Stop(Process process)
    { process.StandardInput.Close(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, process.ExitCode); }

    internal static int RunFixture(string[] args)
    {
        if (args[1] == "target") { Console.WriteLine("READY"); Console.ReadLine(); return 0; }
        if (args[1] == "shutdown")
        {
            Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT", args[2]);
            string exe = Environment.ProcessPath!, release = args[4];
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter." + release + ".dll"), "offline session fixture");
            if (release == "21")
            {
                var worker = new foundationhost::TiaMcp.FoundationHost.EngineWorkerClient(new foundationhost::TiaMcp.FoundationHost.HostOptions {
                    ReleaseKey = release, WorkerExe = exe, ApiDirectory = AppContext.BaseDirectory, BundleRoot = AppContext.BaseDirectory,
                    EngineWorkerExe = exe, NativeEnabled = true }, foundationhost::TiaMcp.FoundationHost.EngineWorkerClient.Hash(exe));
                worker.Call("Attach", new System.Text.Json.Nodes.JsonObject { ["processId"] = int.Parse(args[3]) }, CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine(((Process)typeof(foundationhost::TiaMcp.FoundationHost.EngineWorkerClient).GetField("process", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(worker)!).Id);
            }
            else
            {
                var worker = new WorkerClient(release, exe, AppContext.BaseDirectory, true);
                worker.Call("Attach", new System.Text.Json.Nodes.JsonObject { ["processId"] = int.Parse(args[3]) }, CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine(((Process)typeof(WorkerClient).GetField("process", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(worker)!).Id);
            }
            Console.ReadLine(); Environment.Exit(0); return 0;
        }
        if (args[1] == "job")
        {
            using var child = Start(args[2], args[3]); WorkerJob.Bind(child);
            if (child.StandardOutput.ReadLine() != "READY") return 1;
            child.StandardInput.WriteLine("attach");
            if (child.StandardOutput.ReadLine() != "ATTACHED") return 1;
            Console.WriteLine(child.Id);
            if (Console.ReadLine() == "busy")
            {
                child.StandardInput.WriteLine("busy");
                Console.WriteLine(child.StandardOutput.ReadLine()); Console.ReadLine();
            }
            Environment.Exit(0); return 0;
        }
        bool attached = false, detached = false;
        using var boundary = new FoundationProcessLease(() => args[1], request => {
            if (request.Method == "adapter.FixtureBusy") { Console.WriteLine("BUSY"); Thread.Sleep(Timeout.Infinite); }
            detached = request.Method == "adapter.Disconnect"; attached = !detached;
            return ChannelResponse.Success("{}");
        }, () => attached, () => detached, () => false, () => { attached = false; });
        Console.WriteLine("READY");
        string? command;
        while ((command = Console.ReadLine()) != null)
        {
            var request = command == "busy" ? Request("FixtureBusy") : command == "disconnect" ? Request("Disconnect") : command == "candidate" ? Candidate(int.Parse(args[2])) : Attach(int.Parse(args[2]));
            var response = boundary.Dispatch(request);
            Console.WriteLine(response.Failure?.Message ?? (detached ? "DISCONNECTED" : "ATTACHED"));
        }
        return 0;
    }
}
