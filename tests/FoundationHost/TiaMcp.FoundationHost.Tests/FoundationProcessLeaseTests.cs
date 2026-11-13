extern alias foundationhost;
using System.Diagnostics;
using System.Text.Json;
using TiaMcp.FoundationHost;
using TiaMcp.PlcWorker;
using TiaMcp.WorkerChannel;
using TiaMcpServer.Siemens;
using TiaOpenness.Shared;
using Xunit;
using TiaMcp.Engine.Tests.Shared;

public sealed class FoundationProcessLeaseTests
{
    private static ChannelRequest Request(string method, string args = "{}") => new(1, "adapter." + method, args, (_, _) => { }, "1");
    private static ChannelRequest Attach(int pid) => Request("Attach", JsonSerializer.Serialize(new { processId = pid }));
    private static ChannelRequest FormattedAttach(int pid) => Request("Attach",
        JsonSerializer.Serialize(new { processId = pid }, new JsonSerializerOptions { WriteIndented = true }));
    private static ChannelFailureException Rejection(string message) => new(message, -32603, ChannelOutcome.RejectedBeforeNative,
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

    private sealed class InterruptedLeaseStream : FileStream
    {
        private readonly Microsoft.Win32.SafeHandles.SafeFileHandle rawHandle;
        private bool interrupted;
        internal bool Observe { get; set; }
        internal string? InterruptAfter { get; set; }
        internal List<long> ObservedLengths { get; } = new();

        internal InterruptedLeaseStream(string path) : base(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            => rawHandle = SafeFileHandle;

        private void Checkpoint(string operation)
        {
            if (!Observe) return;
            ObservedLengths.Add(RandomAccess.GetLength(rawHandle));
            if (InterruptAfter != operation) return;
            interrupted = true;
            // Closing the captured OS handle discards the managed write buffer, as a killed worker does.
            rawHandle.Dispose();
            throw new IOException("Simulated worker termination after " + operation);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            base.Write(buffer, offset, count);
            Checkpoint("write");
        }
        public override void SetLength(long value)
        {
            base.SetLength(value);
            Checkpoint("length");
        }
        internal string PersistedRecord()
        {
            var bytes = new byte[RandomAccess.GetLength(rawHandle)];
            int count = RandomAccess.Read(rawHandle, bytes, 0);
            return System.Text.Encoding.UTF8.GetString(bytes, 0, count);
        }
        protected override void Dispose(bool disposing)
        {
            if (!interrupted) base.Dispose(disposing);
        }
    }

    private static PortalProcessLease LeaseForStream(FileStream stream)
        => (PortalProcessLease)Activator.CreateInstance(typeof(PortalProcessLease),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            new object[] { stream, "interrupted-write-fixture" }, null)!;

    [Theory, InlineData("write", "BUSY"), InlineData("length", "UNCERTAIN")]
    public void Interrupted_uncertain_update_keeps_the_request_evidence_for_the_next_owner(string checkpoint, string expectedState)
    {
        using var session = new Session();
        Directory.CreateDirectory(session.Root);
        File.WriteAllText(session.FilePath, "BUSY x\n");
        using (var stream = new InterruptedLeaseStream(session.FilePath))
        using (var lease = LeaseForStream(stream))
        {
            lease.BeginRequest("x");
            stream.Observe = true;
            stream.InterruptAfter = checkpoint;
            Assert.Throws<IOException>(() => lease.CompleteRequest(uncertain: true));
            Assert.NotEmpty(stream.ObservedLengths);
            Assert.All(stream.ObservedLengths, length => Assert.True(length > 0));
        }
        Assert.NotEmpty(File.ReadAllText(session.FilePath));
        using var next = PortalProcessLease.Acquire(session.Root, session.Pid, session.Ticks);
        Assert.Equal(expectedState, next.PreviousOwnerState);
        Assert.Equal(new[] { "x" }, next.PreviousOwnerRequests);
    }

    [Fact]
    public void Completed_uncertain_records_are_nonempty_at_every_observed_write_step()
    {
        using var session = new Session();
        Directory.CreateDirectory(session.Root);
        File.WriteAllText(session.FilePath, "BUSY seed\n");
        using var stream = new InterruptedLeaseStream(session.FilePath);
        using var lease = LeaseForStream(stream);
        lease.BeginRequest("seed");
        stream.Observe = true;
        for (int index = 0; index < 32; index++)
        {
            string id = "write-" + index;
            lease.BeginRequest(id);
            lease.CompleteRequest(uncertain: true);
            string record = stream.PersistedRecord();
            Assert.StartsWith("UNCERTAIN ", record);
            Assert.EndsWith("\n", record);
            Assert.Contains(id, record.TrimEnd('\n').Split(' ', 2)[1].Split(','));
        }
        Assert.NotEmpty(stream.ObservedLengths);
        Assert.All(stream.ObservedLengths, length => Assert.True(length > 0));
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Empty_or_between_write_truncated_files_admit_and_publish_the_owners_evidence(bool truncateBetweenWrites)
    {
        using var session = new Session();
        Directory.CreateDirectory(session.Root);
        File.WriteAllText(session.FilePath, truncateBetweenWrites ? "UNCERTAIN prior\n" : "");
        using (var lease = PortalProcessLease.Acquire(session.Root, session.Pid, session.Ticks))
        {
            if (truncateBetweenWrites)
            {
                var stream = (FileStream)typeof(PortalProcessLease).GetField("stream",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(lease)!;
                stream.SetLength(0);
            }
            lease.BeginRequest("next");
            lease.CompleteRequest(uncertain: true);
        }
        Assert.StartsWith("UNCERTAIN ", File.ReadAllText(session.FilePath));
        var response = session.Boundary.Dispatch(Attach(session.Pid));
        Assert.Null(response.Failure);
        Assert.Equal(1, session.NativeCalls);
        var raw = System.Text.Json.Nodes.JsonNode.Parse(response.ResultJson)!;
        Assert.Equal("UNCERTAIN", (string?)raw["PreviousOwnerState"]);
        string[] expected = truncateBetweenWrites ? new[] { "next", "prior" } : new[] { "next" };
        Assert.Equal(expected, raw["PreviousOwnerRequests"]!.AsArray().Select(value => (string)value!));
        var body = FoundationV4Result.Worker("19", FoundationTools.Definitions.Single(row => row.Name == "ConnectPortal"),
            "recovered-record", new(), raw, null).StructuredContent!;
        Assert.Contains(body["meta"]!["warnings"]!.AsArray(), warning => (string?)warning?["code"] == "PREVIOUS_OWNER_UNCLEAN");
    }

    [Theory]
    [InlineData("BUSY a,b,c\nb\n", new[] { "a", "b", "c" })]
    [InlineData("BUSY a,bad/id,c\n", new[] { "a", "c" })]
    [InlineData("BUSY good,bad ,tail\t\n", new[] { "good" })]
    public void Damaged_lease_records_keep_valid_ids_and_log_once(string record, string[] expected)
    {
        using var session = new Session();
        Directory.CreateDirectory(session.Root);
        File.WriteAllText(session.FilePath, record);
        var previousError = Console.Error;
        using var errors = new StringWriter();
        try
        {
            Console.SetError(errors);
            using var lease = PortalProcessLease.Acquire(session.Root, session.Pid, session.Ticks);
            Assert.Equal("BUSY", lease.PreviousOwnerState);
            Assert.Equal(expected, lease.PreviousOwnerRequests);
            lease.ReleaseCleanly();
            Assert.Equal("RELEASED " + string.Join(",", expected) + "\n", File.ReadAllText(session.FilePath));
            Assert.Single(errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
        finally { Console.SetError(previousError); }
    }

    [Fact]
    public void Oversized_lease_ids_and_excess_count_are_discarded_before_the_next_request()
    {
        using var session = new Session();
        Directory.CreateDirectory(session.Root);
        string[] valid = Enumerable.Range(0, 4097).Select(value => "id-" + value).ToArray();
        File.WriteAllText(session.FilePath, "UNCERTAIN " + new string('x', 129) + "," + string.Join(",", valid) + "\n");
        using var lease = PortalProcessLease.Acquire(session.Root, session.Pid, session.Ticks);
        Assert.Equal(valid.Take(4096), lease.PreviousOwnerRequests);
        Assert.DoesNotContain(new string('x', 129), lease.PreviousOwnerRequests);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Compact_and_formatted_attach_reserve_the_same_identity_as_the_engine(bool formatted)
    {
        using var session = new Session();
        Assert.Null(session.Boundary.Dispatch(formatted ? FormattedAttach(session.Pid) : Attach(session.Pid)).Failure);
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
            : ChannelResponse.Error(new ChannelFailureException("Unknown fixture outcome.", -32603, ChannelOutcome.Unknown)); };
        session.Boundary.Dispatch(Request("FixtureUnknown")); session.Boundary.Dispose();
        Assert.Equal("UNCERTAIN 1\n", File.ReadAllText(session.FilePath));
        using var next = PortalProcessLease.Acquire(session.Root, session.Pid, session.Ticks);
        Assert.Equal("UNCERTAIN", next.PreviousOwnerState);
        Assert.Equal(new[] { "1" }, next.PreviousOwnerRequests);
    }

    [Fact]
    public void Acknowledged_disconnect_after_unknown_releases_the_reservation()
    {
        using var session = new Session(); session.Boundary.Dispatch(Attach(session.Pid));
        session.Reset = true; session.Boundary.Dispatch(Request("FixtureUnknown"));
        session.Boundary.Dispatch(Request("Disconnect")); session.Boundary.Dispose();
        Assert.Equal("RELEASED 1\n", File.ReadAllText(session.FilePath));
    }

    [Fact]
    public void Failed_exit_detach_leaves_uncertain_and_is_not_retried()
    {
        using var session = new Session(); session.Boundary.Dispatch(Attach(session.Pid)); session.ThrowOnExit = true;
        Assert.Null(Record.Exception(session.Boundary.Dispose)); session.Boundary.Dispose();
        Assert.Equal(1, session.Disposals);
        Assert.Equal("UNCERTAIN\n", File.ReadAllText(session.FilePath));

        string repository = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        string root = Path.Combine(repository, "bin-build", "fix-r1-round2-lease-" + Guid.NewGuid().ToString("N"));
        var outcome = new WorkerSessionOutcomeState();
        int detaches = 0;
        var recovery = new WorkerSessionRecovery(outcome, () => false, () => { }, () => { }, () => { },
            () => { detaches++; throw new IOException("Fixture detach failed."); });
        using var boundary = new FoundationProcessLease(() => root, _ => ChannelResponse.Success("{}"),
            () => true, () => false, () => outcome.RequiresReset, recovery.DisposeSession);
        try
        {
            Assert.Null(boundary.Dispatch(Attach(Environment.ProcessId)).Failure);
            Assert.False(outcome.RequiresReset);
            Assert.Null(Record.Exception(boundary.Dispose));
            Assert.True(outcome.RequiresReset);
            boundary.Dispose();
            Assert.True(outcome.RequiresReset);
            Assert.Equal(1, detaches);
            Assert.Equal("UNCERTAIN\n", File.ReadAllText(Directory.GetFiles(root, "*.lease").Single()));
            var evidence = Assert.Single(recovery.ShutdownEvidence)!;
            Assert.Equal("dispose", (string?)evidence["phase"]);
            Assert.Equal(nameof(IOException), (string?)evidence["exceptionType"]);
            Assert.Equal("Fixture detach failed.", (string?)evidence["message"]);
            Assert.True((bool?)evidence["requiresSessionReset"]);
        }
        finally
        {
            boundary.Dispose();
            Assert.StartsWith(Path.Combine(repository, "bin-build") + Path.DirectorySeparatorChar, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Rejected_or_unbound_attach_returns_its_reservation(bool unbound)
    {
        using var session = new Session();
        session.Reply = _ => unbound ? ChannelResponse.Success("{}")
            : ChannelResponse.Error(Rejection("Attach rejected before native."));
        session.Boundary.Dispatch(Attach(session.Pid));
        Assert.Equal("RELEASED\n", File.ReadAllText(session.FilePath));
    }

    [Fact]
    public void Unknown_attach_without_cached_adoption_keeps_uncertain()
    {
        using var session = new Session();
        session.Reply = _ => ChannelResponse.Error(new ChannelFailureException("Attach outcome unknown.", -32603, ChannelOutcome.Unknown));
        session.Boundary.Dispatch(Attach(session.Pid)); session.Boundary.Dispose();
        Assert.Equal("UNCERTAIN 1\n", File.ReadAllText(session.FilePath));
    }

    [Fact]
    public void Cached_state_read_does_not_reserve_a_process()
    {
        using var session = new Session(); session.Boundary.Dispatch(Request("ReadState"));
        Assert.False(Directory.Exists(session.Root));
    }

    [Fact]
    public async Task Process_gone_is_refused_without_native_dispatch()
    {
        using var session = new Session(); using var target = Start("target");
        Assert.Equal("READY", await Line(target)); Assert.Equal(Path.GetFullPath(Path.ChangeExtension(typeof(FoundationProcessLeaseTests).Assembly.Location, ".exe")), Path.GetFullPath(target.MainModule!.FileName), ignoreCase: true); target.Kill(); Assert.True(target.WaitForExit(TestDeadlines.HangMilliseconds));
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
        Assert.Equal("ATTACHED", await Command(loser, "attach-formatted"));
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
        host.StandardInput.WriteLine("exit"); Assert.True(host.WaitForExit(TestDeadlines.HangMilliseconds)); Assert.True(worker.WaitForExit(TestDeadlines.HangMilliseconds));
        Assert.Equal("IDLE\n", File.ReadAllText(session.FilePath));
        var response = session.Boundary.Dispatch(Attach(session.Pid));
        Assert.Null(response.Failure);
        Assert.Equal("IDLE", JsonDocument.Parse(response.ResultJson).RootElement.GetProperty("PreviousOwnerState").GetString());
        Assert.Equal(1, session.NativeCalls);
    }

    [Theory, InlineData(SessionBehavior.LeaseReserved)]
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
    public async Task Job_kill_during_native_fixture_keeps_busy_and_admits_the_next_owner()
    {
        using var session = new Session(); using var host = Start("job", session.Root, session.Pid.ToString());
        int pid = int.Parse((await Line(host))!); using var worker = Process.GetProcessById(pid);
        Assert.Equal("BUSY", await Command(host, "busy"));
        host.StandardInput.WriteLine("exit"); Assert.True(host.WaitForExit(TestDeadlines.HangMilliseconds)); Assert.True(worker.WaitForExit(TestDeadlines.HangMilliseconds));
        Assert.Equal("BUSY 1\n", File.ReadAllText(session.FilePath));
        var response = session.Boundary.Dispatch(Attach(session.Pid));
        Assert.Null(response.Failure);
        Assert.Equal("BUSY", JsonDocument.Parse(response.ResultJson).RootElement.GetProperty("PreviousOwnerState").GetString());
        Assert.Equal(1, session.NativeCalls);
    }

    [Theory, InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Host_process_exit_runs_graceful_stop_before_its_job_closes(string release)
    {
        using var session = new Session(); using var host = Start("shutdown", session.Root, session.Pid.ToString(), release);
        using var worker = Process.GetProcessById(int.Parse((await Line(host))!));
        host.StandardInput.WriteLine("exit");
        Assert.True(host.WaitForExit(TestDeadlines.HangMilliseconds)); Assert.True(host.ExitCode == 0, FixtureExit.Describe(host.ExitCode, host.StandardError.ReadToEnd())); Assert.Equal(0, host.ExitCode); Assert.True(worker.WaitForExit(TestDeadlines.HangMilliseconds));
        Assert.Equal("RELEASED\n", File.ReadAllText(Path.Combine(session.Root, session.Pid + "-1.lease")));
    }

    [Theory, InlineData("ACTIVE"), InlineData("BUSY"), InlineData("UNCERTAIN")]
    public void Busy_or_uncertain_lease_of_a_dead_owner_admits_with_a_warning(string state)
    {
        using var session = new Session(); Directory.CreateDirectory(session.Root);
        File.WriteAllText(session.FilePath, state + "\n");
        var response = session.Boundary.Dispatch(Attach(session.Pid));
        Assert.Null(response.Failure);
        Assert.Equal(1, session.NativeCalls);
        var raw = System.Text.Json.Nodes.JsonNode.Parse(response.ResultJson)!;
        Assert.Equal(state, (string?)raw["PreviousOwnerState"]);
        var definition = FoundationTools.Definitions.Single(row => row.Name == "ConnectPortal");
        var body = FoundationV4Result.Worker("19", definition, "connect", new(), raw, null).StructuredContent!;
        Assert.Equal(state is "BUSY" or "UNCERTAIN" or "ACTIVE", body["meta"]!["warnings"]!.AsArray()
            .Any(w => (string?)w?["code"] == "PREVIOUS_OWNER_UNCLEAN"));
    }

    [Theory, InlineData("IDLE"), InlineData("RELEASED")]
    public void Takeover_data_reaches_the_connect_result_without_a_new_warning(string state)
    {
        using var session = new Session(); Directory.CreateDirectory(session.Root); File.WriteAllText(session.FilePath, state + "\n");
        using var lease = PortalProcessLease.Acquire(session.Root, session.Pid, session.Ticks);
        var raw = System.Text.Json.Nodes.JsonNode.Parse("{\"Stage\":\"attached\",\"OwnsPortal\":false,\"AttemptedPids\":[123],\"Strategy\":\"explicit\",\"LaunchMode\":\"attach\"}")!.AsObject();
        raw["PreviousOwnerState"] = lease.PreviousOwnerState;
        raw["PreviousOwnerRequests"] = JsonSerializer.SerializeToNode(lease.PreviousOwnerRequests);
        var result = V17ProjectEnvelope.Wrap("Connect", "Connection", raw, false);
        Assert.Equal(state, (string?)result["previousOwner"]?["state"]);
        Assert.Empty(result["previousOwner"]!["requests"]!.AsArray());
        Assert.Null(result["previousOwnerEndedIdle"]);
        var definition = FoundationTools.Definitions.Single(row => row.Name == "ConnectPortal");
        var body = FoundationV4Result.Worker("19", definition, "connect", new(), raw, result).StructuredContent!;
        Assert.DoesNotContain(body["meta"]!["warnings"]!.AsArray(), w => (string?)w?["code"] == "PREVIOUS_OWNER_UNCLEAN");
        lease.ReleaseCleanly();
    }

    [Fact]
    public void Durable_request_state_overhead_is_measured()
    {
        using var session = new Session(); using var lease = PortalProcessLease.Acquire(session.Root, session.Pid, session.Ticks);
        const int count = 100;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < count; i++) { lease.BeginRequest(); lease.CompleteRequest(false); }
        double milliseconds = watch.Elapsed.TotalMilliseconds / count;
        TestDeadlines.Report("lease.durable-request", milliseconds, "ms/request");
        lease.ReleaseCleanly();
    }

    [Fact]
    public void Lease_content_names_the_request_and_preserves_other_unsettled_ids()
    {
        using var session = new Session();
        session.Boundary.Dispatch(Attach(session.Pid));
        session.Reply = request => {
            var reservation = typeof(FoundationProcessLease).GetField("lease",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(session.Boundary)!;
            var stream = (FileStream)reservation.GetType().GetField("stream",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(reservation)!;
            var bytes = new byte[RandomAccess.GetLength(stream.SafeFileHandle)];
            int count = RandomAccess.Read(stream.SafeFileHandle, bytes, 0);
            Assert.Equal("BUSY prior-1,write-7\n", System.Text.Encoding.UTF8.GetString(bytes, 0, count));
            return ChannelResponse.Error(new ChannelFailureException("Unknown fixture write.", -32603, ChannelOutcome.Unknown));
        };
        var request = new ChannelRequest(7, "adapter.FixtureWrite", "{}", (_, _) => { }, "write-7", "FixtureWrite", new[] { "prior-1" });
        Assert.Equal(ChannelOutcome.Unknown, session.Boundary.Dispatch(request).Failure!.Outcome);
        session.Boundary.Dispose();
        Assert.Equal("UNCERTAIN prior-1,write-7\n", File.ReadAllText(session.FilePath));
        using var next = PortalProcessLease.Acquire(session.Root, session.Pid, session.Ticks);
        Assert.Equal(new[] { "prior-1", "write-7" }, next.PreviousOwnerRequests);
    }

    [Fact]
    public void Acknowledged_disconnect_keeps_unsettled_ids_in_the_record()
    {
        using var session = new Session();
        session.Boundary.Dispatch(Attach(session.Pid));
        var request = new ChannelRequest(2, "adapter.Disconnect", "{}", (_, _) => { }, "disconnect", "DisconnectPortal",
            new[] { "unknown-1", "unknown-2" });
        Assert.Null(session.Boundary.Dispatch(request).Failure);
        Assert.Equal("RELEASED unknown-1,unknown-2\n", File.ReadAllText(session.FilePath));
        using var next = PortalProcessLease.Acquire(session.Root, session.Pid, session.Ticks);
        Assert.Equal("RELEASED", next.PreviousOwnerState);
        Assert.Equal(new[] { "unknown-1", "unknown-2" }, next.PreviousOwnerRequests);
    }

    private static Process Start(params string[] args)
    {
        var start = new ProcessStartInfo(Path.ChangeExtension(typeof(FoundationProcessLeaseTests).Assembly.Location, ".exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--process-lease-fixture"); foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }
    private static async Task<string?> Line(Process process)
    {
        string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(TestDeadlines.Hang);
        if (line == null && process.HasExited)
            throw new InvalidOperationException(FixtureExit.Describe(process.ExitCode, await process.StandardError.ReadToEndAsync()));
        return line;
    }
    private static async Task<string?> Command(Process process, string command) { process.StandardInput.WriteLine(command); return await Line(process); }
    private static async Task Stop(Process process)
    { process.StandardInput.Close(); await process.WaitForExitAsync().WaitAsync(TestDeadlines.Hang); Assert.True(process.ExitCode == 0, FixtureExit.Describe(process.ExitCode, await process.StandardError.ReadToEndAsync())); Assert.Equal(0, process.ExitCode); }

    internal static int RunFixture(string[] args)
    {
        if (args[1] == "target") { Console.WriteLine("READY"); Console.ReadLine(); return 0; }
        if (args[1] == "shutdown")
        {
            Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT", args[2]);
            string exe = Environment.ProcessPath!, release = args[4];
            TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter." + release + ".dll"), "offline session fixture");
            var worker = new WorkerClient(release, exe, AppContext.BaseDirectory, true);
            worker.Call("Attach", new System.Text.Json.Nodes.JsonObject { ["processId"] = int.Parse(args[3]) }, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine(((Process)typeof(WorkerClient).GetField("process", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(worker)!).Id);
            Console.ReadLine(); Environment.Exit(0); return 0;
        }
        if (args[1] == "job")
        {
            using var child = Start(args[2], args[3], "job-owned"); WorkerJob.Bind(child);
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
            var request = command == "busy" ? Request("FixtureBusy") : command == "disconnect" ? Request("Disconnect") : command == "attach-formatted" ? FormattedAttach(int.Parse(args[2])) : Attach(int.Parse(args[2]));
            var response = boundary.Dispatch(request);
            Console.WriteLine(response.Failure?.Message ?? (detached ? "DISCONNECTED" : "ATTACHED"));
        }
        // This fixture models abrupt job death, not graceful stdin EOF. The
        // parent can close the pipe before its last job handle closes. Keep the
        // idle lease intact until the kernel kills this child. The separate EOF
        // test still exercises normal boundary disposal.
        if (args.Length > 3 && args[3] == "job-owned") Thread.Sleep(Timeout.Infinite);
        return 0;
    }
}
