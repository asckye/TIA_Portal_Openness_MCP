using TiaMcp.WorkerChannel;
using Xunit;

namespace TiaMcp.WorkerChannel.Tests;

public sealed class CallRunningTests
{
    public static IEnumerable<object[]> Releases() => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });

    [Theory, MemberData(nameof(Releases))]
    public async Task Timed_out_read_keeps_the_channel_and_its_reply(string release)
    {
        using var wire = new ProtocolTests.Wire(release);
        await wire.Connect();
        var running = await Assert.ThrowsAsync<ChannelCallRunningException>(() => wire.Client.CallToolAsync(
            "adapter.ReadBlocks", "{}", BindingChange.None, true, TimeSpan.FromMilliseconds(20), false, null, null,
            drainOnTimeout: true));
        Assert.True(wire.Client.CallRunning);
        Assert.False(wire.Client.Poisoned);
        long id = wire.Client.LastRequestId;
        var refusal = await Assert.ThrowsAsync<ChannelFailureException>(() => wire.Call());
        Assert.Equal(ChannelOutcome.RejectedBeforeNative, refusal.Outcome);
        Assert.Contains("worker-call-running", refusal.EvidenceJson);
        Assert.Equal(id, wire.Client.LastRequestId);
        wire.Input.Feed(ProtocolTests.Reply());
        Assert.Equal("null", await running.Reply.WaitAsync(ProtocolTests.Budget));
        Assert.False(wire.Client.CallRunning);
        wire.Output.OnWrite = _ => wire.Input.Feed(ProtocolTests.Reply(2));
        await wire.Call();
        Assert.False(wire.Client.Poisoned);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Timed_out_write_keeps_the_channel(string release)
    {
        using var wire = new ProtocolTests.Wire(release);
        await wire.Connect();
        var running = await Assert.ThrowsAsync<ChannelCallRunningException>(() => wire.Client.CallToolAsync(
            "adapter.SaveProject", "{}", BindingChange.None, false, TimeSpan.FromMilliseconds(20), false, null, null,
            drainOnTimeout: true));
        Assert.False(wire.Client.Poisoned);
        Assert.False(wire.Client.Unanswered);
        Assert.True(wire.Client.CallRunning);
        Assert.Single(wire.Output.Writes);
        wire.Input.Feed(ProtocolTests.Reply(payload: "\"result\":{\"saved\":true}"));
        Assert.Equal("{\"saved\":true}", await running.Reply.WaitAsync(ProtocolTests.Budget));
        wire.Output.OnWrite = _ => wire.Input.Feed(ProtocolTests.Reply(2));
        await wire.Call();
        Assert.False(wire.Client.Poisoned);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Invalid_progress_preserves_a_running_call_and_its_real_reply(bool overLimit)
    {
        using var wire = new ProtocolTests.Wire();
        await wire.Connect();
        var running = await Assert.ThrowsAsync<ChannelCallRunningException>(() => wire.Client.CallToolAsync(
            "adapter.SaveProject", "{}", BindingChange.None, false, TimeSpan.FromMilliseconds(20), false, null, null, true));
        int count = overLimit ? 1025 : 1;
        wire.Input.Feed(string.Join("\n", Enumerable.Range(1, count)
            .Select(sequence => ProtocolTests.Progress(sequence: overLimit ? sequence : 2))) + "\n" + ProtocolTests.Reply());
        Assert.Equal("null", await running.Reply.WaitAsync(ProtocolTests.Budget));
        Assert.False(wire.Client.Poisoned);
        Assert.False(wire.Client.CallRunning);
        Assert.Single(wire.Output.Writes);
    }

    [Fact]
    public async Task Status_read_after_a_call_still_poisons()
    {
        using var wire = new ProtocolTests.Wire();
        await wire.Connect();
        await Assert.ThrowsAsync<ChannelFaultException>(() => wire.Call(timeout: TimeSpan.FromMilliseconds(20)));
        Assert.True(wire.Client.Poisoned);
        Assert.True(wire.Client.Unanswered);
    }

    [Fact]
    public async Task Late_failure_reaches_the_waiter()
    {
        using var wire = new ProtocolTests.Wire();
        await wire.Connect();
        var running = await Assert.ThrowsAsync<ChannelCallRunningException>(() => wire.Client.CallToolAsync(
            "adapter.SaveProject", "{}", BindingChange.None, false, TimeSpan.FromMilliseconds(20), false, null, null, true));
        wire.Input.Feed(ProtocolTests.Reply(payload:
            "\"error\":{\"code\":-32603,\"message\":\"unknown\",\"data\":{\"outcome\":\"Unknown\",\"evidence\":null}}"));
        var failure = await Assert.ThrowsAsync<ChannelFailureException>(() => running.Reply.WaitAsync(ProtocolTests.Budget));
        Assert.Equal(ChannelOutcome.Unknown, failure.Outcome);
        Assert.False(wire.Client.Poisoned);
    }

    [Fact]
    public async Task Worker_exit_during_a_running_call_faults_the_waiter()
    {
        using var wire = new ProtocolTests.Wire();
        await wire.Connect();
        var running = await Assert.ThrowsAsync<ChannelCallRunningException>(() => wire.Client.CallToolAsync(
            "adapter.SaveProject", "{}", BindingChange.None, false, TimeSpan.FromMilliseconds(20), false, null, null, true));
        wire.Input.End();
        await Assert.ThrowsAsync<ChannelFaultException>(() => running.Reply.WaitAsync(ProtocolTests.Budget));
        Assert.True(wire.Client.Poisoned);
        Assert.True(wire.Client.Unanswered);
        Assert.False(wire.Client.CallRunning);
        Assert.Single(wire.Output.Writes);
    }

    [Fact]
    public async Task Bad_frame_during_a_running_call_clears_running_state()
    {
        using var wire = new ProtocolTests.Wire();
        await wire.Connect();
        var running = await Assert.ThrowsAsync<ChannelCallRunningException>(() => wire.Client.CallToolAsync(
            "adapter.SaveProject", "{}", BindingChange.None, false, TimeSpan.FromMilliseconds(20), false, null, null, true));
        wire.Input.Feed("{invalid}\n");
        await Assert.ThrowsAsync<ChannelFaultException>(() => running.Reply.WaitAsync(ProtocolTests.Budget));
        Assert.False(wire.Client.CallRunning);
        Assert.False(wire.Client.InFlight);
        Assert.True(wire.Client.Poisoned);
        Assert.Single(wire.Output.Writes);
    }
}
