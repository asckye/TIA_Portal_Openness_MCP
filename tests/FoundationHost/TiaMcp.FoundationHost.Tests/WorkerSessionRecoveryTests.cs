using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using TiaMcp.PlcWorker;
using TiaMcp.WorkerChannel;
using TiaOpenness.Shared;
using Xunit;

public sealed class WorkerSessionRecoveryTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly WorkerSessionOutcomeState Outcome = new();
        internal readonly WorkerSessionRecovery Recovery;
        internal readonly FoundationProcessLease Lease;
        internal readonly string Root = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
            "bin-build", "refactor", "session-recovery-" + Guid.NewGuid().ToString("N"));
        internal int Calls, Invalidations, Disposals, Refreshes, Clears;
        internal bool Attached, Detached;
        internal string? BindingFault;
        internal Func<ChannelRequest, ChannelResponse>? Reply;
        internal string Marker => Directory.GetFiles(Root, "*.lease").Single();
        internal Fixture()
        {
            Recovery = new(Outcome, () => Detached, () => Refreshes++, () => { Clears++; BindingFault = null; },
                reason => BindingFault = reason, () => { Invalidations++; Attached = false; }, () => Disposals++);
            Lease = new(() => Root, request => Recovery.Dispatch(request, native => {
                if (native.Method != "adapter.Disconnect") Outcome.RequireUsable(false);
                Calls++;
                if (Reply != null) return Reply(native);
                if (native.Method == "adapter.Disconnect") { Detached = true; Attached = false; }
                else Attached = true;
                return ChannelResponse.Success("{}");
            }), () => Attached, () => Detached, () => Outcome.RequiresReset, Recovery.DisposeSession, Recovery.Synchronize);
        }
        internal ChannelResponse Call(string method) => Lease.Dispatch(new ChannelRequest(1, "adapter." + method,
            method == "Attach" ? JsonSerializer.Serialize(new { processId = Environment.ProcessId }) : "{}", (_, _) => { }));
        public void Dispose()
        {
            Lease.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    [Theory]
    [InlineData("20", false), InlineData("21", false), InlineData("20", true), InlineData("21", true)]
    public void Unknown_native_outcome_locks_reads_and_writes_and_preserves_typed_evidence(string release, bool typed)
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Call("Attach").Failure);
        fixture.Reply = _ => {
            if (typed) { fixture.Outcome.MarkUncertain(); return ChannelResponse.Success("{\"before\":1,\"after\":null}"); }
            return ChannelResponse.Error(new ChannelFailureException("Unknown native fixture.", -32603, ChannelOutcome.Unknown));
        };
        var response = fixture.Call("hardware-addressing.ReadHardwareIoAddresses");
        if (typed) Assert.Equal("{\"before\":1,\"after\":null}", response.ResultJson);
        else Assert.Equal(ChannelOutcome.Unknown, response.Failure!.Outcome);
        Assert.True(fixture.Outcome.RequiresReset);
        int calls = fixture.Calls;
        foreach (string method in new[] { "ReadState", "SaveProject" })
        {
            var refusal = fixture.Call(method).Failure!;
            Assert.Equal(SessionBehavior.Recovery, refusal.Message);
            Assert.Equal(ChannelOutcome.RejectedBeforeNative, refusal.Outcome);
        }
        Assert.Equal(calls, fixture.Calls);
        Assert.Throws<InvalidOperationException>(() => fixture.Outcome.RequireUsable(true));
        Assert.Throws<InvalidOperationException>(() => fixture.Outcome.RequireUsable(false));
        fixture.Lease.Dispose();
        Assert.Equal(1, fixture.Invalidations);
        Assert.Equal(1, fixture.Disposals);
        Assert.Equal("UNCERTAIN\n", File.ReadAllText(fixture.Marker));
        Assert.Contains(release, new[] { "20", "21" });
    }

    [Theory]
    [InlineData("20"), InlineData("21")]
    public void Process_loss_during_a_read_becomes_unknown_and_disconnect_recovery_releases_the_lease(string release)
    {
        using var fixture = new Fixture();
        fixture.Call("Attach");
        fixture.Reply = _ => {
            fixture.Recovery.ObserveFailure(new COMException("TIA process lost."));
            return ChannelResponse.Error(new ChannelFailureException("TIA process lost.", -32603, ChannelOutcome.ReadFailed));
        };
        Assert.Equal(ChannelOutcome.Unknown, fixture.Call("ReadState").Failure!.Outcome);
        Assert.Equal("TIA process lost.", fixture.BindingFault);
        fixture.Reply = null;
        Assert.Null(fixture.Call("Disconnect").Failure);
        Assert.Equal("RELEASED\n", File.ReadAllText(fixture.Marker));
        Assert.Equal(1, fixture.Clears);
        Assert.Null(fixture.BindingFault);
        Assert.True(fixture.Outcome.RequiresReset);
        Assert.Equal(ChannelOutcome.RejectedBeforeNative, fixture.Call("Attach").Failure!.Outcome);
        Assert.Contains(release, new[] { "20", "21" });
    }

    [Theory]
    [InlineData("20"), InlineData("21")]
    public void Clean_worker_restart_and_rebind_start_with_a_new_unlatched_outcome(string release)
    {
        using var first = new Fixture();
        first.Call("Attach");
        first.Lease.Dispose();
        Assert.Equal("RELEASED\n", File.ReadAllText(first.Marker));
        Assert.Equal(0, first.Invalidations);
        using var second = new Fixture();
        Assert.False(second.Outcome.RequiresReset);
        Assert.Null(second.Call("Attach").Failure);
        Assert.Null(second.Call("BindProject").Failure);
        Assert.Equal(2, second.Refreshes);
        Assert.Null(second.Recovery.Fault);
        Assert.Contains(release, new[] { "20", "21" });
    }
}
