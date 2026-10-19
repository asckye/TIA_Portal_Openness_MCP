using System.Reflection;
using System.Text;
using System.Text.Json;
using TiaMcp.PlcWorker;
using TiaMcp.WorkerChannel;
using TiaOpenness.Shared;
using Xunit;

public sealed class PlcSharedLifecycleTests
{
    private sealed class Fixture(string release) : IDisposable
    {
        internal readonly WorkerSessionOutcomeState Outcome = new();
        internal readonly string Root = Path.Combine(AppContext.BaseDirectory, "plc-lifecycle", Guid.NewGuid().ToString("N"));
        internal FoundationProcessLease Lease = null!;
        internal WorkerSessionRecovery Recovery = null!;
        internal bool Attached, Detached;
        internal int Calls, Disposals, Invalidations, Refreshes;
        internal string? Project;
        internal Func<ChannelRequest, ChannelResponse>? Operation;
        internal string ProjectPath(string name = "P") => "C:/fixture/" + name + ".ap" + release;
        internal string Marker => Directory.GetFiles(Root, "*.lease").Single();
        // A reserved lease keeps its marker open with FileShare.None. Read the persisted
        // bytes through that owner handle; a released marker is read from disk as before.
        internal string MarkerText(string marker)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var reservation = typeof(FoundationProcessLease).GetField("lease", flags)!.GetValue(Lease);
            var stream = reservation == null ? null : (FileStream?)reservation.GetType().GetField("stream", flags)!.GetValue(reservation);
            if (stream == null) return File.ReadAllText(marker);
            Assert.Equal(Path.GetFullPath(marker), Path.GetFullPath(stream.Name));
            var bytes = new byte[RandomAccess.GetLength(stream.SafeFileHandle)];
            return Encoding.UTF8.GetString(bytes, 0, RandomAccess.Read(stream.SafeFileHandle, bytes, 0));
        }
        internal Fixture Initialize()
        {
            Recovery = new(Outcome, () => Detached, () => Refreshes++, () => Project = null,
                _ => { }, () => { Invalidations++; Attached = false; }, () => Disposals++);
            Lease = new(() => Root, request => Recovery.Dispatch(request, current => {
                if (current.Method != "adapter.Disconnect") Outcome.RequireUsable(false);
                Calls++;
                if (Operation != null) return Operation(current);
                if (current.Method == "adapter.Attach") Attached = true;
                if (current.Method == "adapter.Disconnect") { Detached = true; Attached = false; }
                return ChannelResponse.Success("{}");
            }), () => Attached, () => Detached, () => Outcome.RequiresReset, Recovery.DisposeSession, Recovery.Synchronize);
            return this;
        }
        internal ChannelResponse Call(string operation) => Lease.Dispatch(new ChannelRequest(1, "adapter." + operation,
            operation == "Attach" ? JsonSerializer.Serialize(new { processId = Environment.ProcessId }) : "{}", (_, _) => { }));
        internal void Attach() => Assert.Null(Call("Attach").Failure);
        public void Dispose()
        {
            Lease.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    public static IEnumerable<object[]> RestoredOperations()
    {
        foreach (string release in new[] { "20", "21" })
        foreach (string operation in new[] { "RetrieveProjectArchive", "SaveProjectCopy", "ManageMultiuserSession", "BuildProjectScaffold", "ConnectIsolatedPortal" })
            yield return new object[] { release, operation };
    }
    [Theory, MemberData(nameof(RestoredOperations))]
    public void Restored_lifecycle_keeps_one_process_reservation_until_acknowledged_disconnect(string release, string operation)
    {
        using var fixture = new Fixture(release).Initialize(); fixture.Attach();
        string marker = fixture.Marker;
        fixture.Operation = _ => { fixture.Project = fixture.ProjectPath(operation); return ChannelResponse.Success("{}"); };
        Assert.Null(fixture.Call("project-lifecycle." + operation).Failure);
        Assert.Equal(fixture.ProjectPath(operation), fixture.Project);
        Assert.Equal(marker, fixture.Marker); Assert.True(fixture.Lease.Reserved);
        Assert.Equal("IDLE\n", fixture.MarkerText(marker)); Assert.False(fixture.Outcome.RequiresReset);
        Assert.Equal(0, fixture.Disposals); Assert.Equal(0, fixture.Invalidations);
        fixture.Operation = null; Assert.Null(fixture.Call("Disconnect").Failure);
        Assert.Equal("RELEASED\n", File.ReadAllText(marker)); Assert.Null(fixture.Project);
        Assert.False(fixture.Lease.Reserved);
    }
    [Theory, MemberData(nameof(RestoredOperations))]
    public void Unknown_restored_operation_locks_reads_and_writes_without_a_native_retry(string release, string operation)
    {
        using var fixture = new Fixture(release).Initialize(); fixture.Attach(); fixture.Project = fixture.ProjectPath();
        fixture.Operation = _ => ChannelResponse.Error(new ChannelFailureException(operation + " lost acknowledgement", -32603, ChannelOutcome.Unknown));
        Assert.Equal(ChannelOutcome.Unknown, fixture.Call("project-lifecycle." + operation).Failure!.Outcome);
        Assert.True(fixture.Outcome.RequiresReset); Assert.NotNull(fixture.Recovery.Fault);
        int calls = fixture.Calls;
        foreach (string next in new[] { "ReadState", "SaveProject" })
        {
            var refused = fixture.Call(next).Failure!;
            Assert.Equal(ChannelOutcome.RejectedBeforeNative, refused.Outcome); Assert.Equal(SessionBehavior.Recovery, refused.Message);
        }
        Assert.Equal(calls, fixture.Calls); Assert.Equal(fixture.ProjectPath(), fixture.Project);
        Assert.Equal(0, fixture.Disposals); Assert.Equal(0, fixture.Invalidations);
        fixture.Lease.Dispose(); Assert.Equal("UNCERTAIN\n", File.ReadAllText(fixture.Marker));
        Assert.Equal(1, fixture.Disposals); Assert.Equal(1, fixture.Invalidations);
    }
    [Theory, InlineData("20", false), InlineData("21", false), InlineData("20", true), InlineData("21", true)]
    public void Save_as_refreshes_cached_identity_without_replacing_the_process_lease(string release, bool moves)
    {
        using var fixture = new Fixture(release).Initialize(); fixture.Attach(); fixture.Project = fixture.ProjectPath();
        long ticks = fixture.Lease.ProcessStartTicks; string marker = fixture.Marker;
        fixture.Operation = _ => { fixture.Project = fixture.ProjectPath(moves ? "Copy" : "P"); return ChannelResponse.Success("{}"); };
        Assert.Null(fixture.Call("project-lifecycle.ArchiveSaveProjectCopy").Failure);
        Assert.Equal(fixture.ProjectPath(moves ? "Copy" : "P"), fixture.Project);
        Assert.Equal(ticks, fixture.Lease.ProcessStartTicks); Assert.Equal(marker, fixture.Marker);
        Assert.Equal(2, fixture.Refreshes); Assert.Equal(0, fixture.Disposals);
    }
    [Theory, InlineData("20", false), InlineData("21", false), InlineData("20", true), InlineData("21", true)]
    public void Pre_native_refusal_keeps_binding_and_does_not_latch_unknown(string release, bool readOnly)
    {
        using var fixture = new Fixture(release).Initialize(); fixture.Attach(); fixture.Project = fixture.ProjectPath();
        fixture.Operation = _ => ChannelResponse.Error(new ChannelFailureException("Borrowed projects cannot be closed by this session.", -32603, ChannelOutcome.RejectedBeforeNative));
        Assert.Equal(ChannelOutcome.RejectedBeforeNative, fixture.Call(readOnly ? "ReadState" : "CloseProject").Failure!.Outcome);
        Assert.False(fixture.Outcome.RequiresReset); Assert.Null(fixture.Recovery.Fault);
        fixture.Outcome.RequireUsable(readOnly); Assert.Equal(fixture.ProjectPath(), fixture.Project);
        Assert.Equal("IDLE\n", fixture.MarkerText(fixture.Marker)); Assert.Equal(0, fixture.Disposals);
    }
    [Theory, InlineData("20", false), InlineData("21", false), InlineData("20", true), InlineData("21", true)]
    public void Recovery_disconnect_releases_the_original_reservation_once(string release, bool typed)
    {
        using var fixture = new Fixture(release).Initialize(); fixture.Attach();
        fixture.Operation = _ => {
            if (typed) { fixture.Outcome.MarkUncertain(); return ChannelResponse.Success("{}"); }
            return ChannelResponse.Error(new ChannelFailureException("unknown", -32603, ChannelOutcome.Unknown));
        };
        fixture.Call("SaveProject"); Assert.True(fixture.Outcome.RequiresReset);
        fixture.Operation = null; Assert.Null(fixture.Call("Disconnect").Failure);
        Assert.Equal("RELEASED\n", File.ReadAllText(fixture.Marker)); Assert.False(fixture.Lease.Reserved);
        Assert.True(fixture.Outcome.RequiresReset);
        int calls = fixture.Calls; Assert.Equal(ChannelOutcome.RejectedBeforeNative, fixture.Call("Attach").Failure!.Outcome);
        Assert.Equal(calls, fixture.Calls); fixture.Lease.Dispose(); fixture.Lease.Dispose(); Assert.Equal(1, fixture.Disposals);
    }
    [Theory, InlineData("20", false), InlineData("21", false), InlineData("20", true), InlineData("21", true)]
    public void Failed_or_unknown_disconnect_is_never_retried_on_disposal(string release, bool throws)
    {
        using var fixture = new Fixture(release).Initialize(); fixture.Attach();
        fixture.Operation = _ => throws ? throw new IOException("disconnect acknowledgement lost")
            : ChannelResponse.Error(new ChannelFailureException("disconnect acknowledgement lost", -32603, ChannelOutcome.Unknown));
        Assert.Equal(ChannelOutcome.Unknown, fixture.Call("Disconnect").Failure!.Outcome);
        int calls = fixture.Calls; fixture.Lease.Dispose(); fixture.Lease.Dispose();
        Assert.Equal(calls, fixture.Calls); Assert.Equal(1, fixture.Disposals);
        Assert.Equal("UNCERTAIN\n", File.ReadAllText(fixture.Marker));
    }
    [Theory, InlineData("20", false), InlineData("21", false), InlineData("20", true), InlineData("21", true)]
    public void Scaffold_and_local_session_changes_refresh_on_the_same_owner(string release, bool local)
    {
        using var fixture = new Fixture(release).Initialize(); fixture.Attach();
        int owner = Environment.CurrentManagedThreadId;
        foreach (string? project in new[] { fixture.ProjectPath(), null, fixture.ProjectPath(local ? "Other" : "New") })
        {
            fixture.Operation = _ => { Assert.Equal(owner, Environment.CurrentManagedThreadId); fixture.Project = project; return ChannelResponse.Success("{}"); };
            Assert.Null(fixture.Call("project-lifecycle.ExecuteProjectScaffoldPrimitive").Failure);
            Assert.Equal(project, fixture.Project); Assert.True(fixture.Lease.Reserved);
        }
        Assert.Equal(4, fixture.Refreshes); Assert.Equal(0, fixture.Disposals);
    }
    [Theory, InlineData("20"), InlineData("21")]
    public void Failed_cached_binding_refresh_latches_the_generation(string release)
    {
        using var fixture = new Fixture(release).Initialize(); fixture.Attach();
        fixture.Operation = _ => { fixture.Outcome.MarkUncertain(); return ChannelResponse.Success("{}"); };
        Assert.Equal(ChannelOutcome.Unknown, fixture.Call("SaveProject").Failure!.Outcome);
        Assert.True(fixture.Outcome.RequiresReset); Assert.NotNull(fixture.Recovery.Fault);
        Assert.Throws<InvalidOperationException>(() => fixture.Outcome.RequireUsable(false));
        Assert.Throws<InvalidOperationException>(() => fixture.Outcome.RequireUsable(true));
    }
    [Theory, InlineData("20"), InlineData("21")]
    public void Clean_exit_releases_after_disposal_and_a_new_generation_starts_unbound(string release)
    {
        using var first = new Fixture(release).Initialize(); first.Attach(); first.Project = first.ProjectPath();
        first.Lease.Dispose(); first.Lease.Dispose();
        Assert.Equal("RELEASED\n", File.ReadAllText(first.Marker)); Assert.Equal(1, first.Disposals);
        using var next = new Fixture(release).Initialize();
        Assert.False(next.Outcome.RequiresReset); Assert.Null(next.Project); Assert.False(next.Lease.Reserved);
        next.Attach(); Assert.True(next.Lease.Reserved); Assert.Null(next.Recovery.Fault);
    }
    [Theory, InlineData("20"), InlineData("21")]
    public void Another_owner_is_refused_before_any_lifecycle_operation(string release)
    {
        using var fixture = new Fixture(release).Initialize();
        using var reservation = TiaMcpServer.Siemens.PortalProcessLease.Acquire(fixture.Root, Environment.ProcessId,
            System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks);
        var refusal = fixture.Call("Attach").Failure!;
        Assert.Equal(SessionBehavior.LeaseReserved, refusal.Message);
        Assert.Equal(ChannelOutcome.RejectedBeforeNative, refusal.Outcome); Assert.Equal(0, fixture.Calls);
        Assert.False(fixture.Lease.Reserved); Assert.False(fixture.Outcome.RequiresReset);
        reservation.ReleaseCleanly();
    }
}
