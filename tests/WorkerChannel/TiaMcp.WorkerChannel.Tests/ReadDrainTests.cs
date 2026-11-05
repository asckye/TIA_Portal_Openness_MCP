using TiaMcp.WorkerChannel;
using Xunit;

namespace TiaMcp.WorkerChannel.Tests;

public sealed class ReadDrainTests
{
    [Fact]
    public async Task Timed_out_read_drains_and_the_channel_stays_usable()
    {
        using var wire = new ProtocolTests.Wire();
        await wire.Connect();
        await Assert.ThrowsAsync<ChannelReadTimeoutException>(() => wire.Client.CallToolAsync(
            "adapter.ReadBlocks", "{}", BindingChange.None, true, TimeSpan.FromMilliseconds(20), false, null, null,
            drainOnTimeout: true));
        Assert.True(wire.Client.Draining);
        Assert.False(wire.Client.Poisoned);
        long id = wire.Client.LastRequestId;
        var refusal = await Assert.ThrowsAsync<ChannelFailureException>(() => wire.Call());
        Assert.Equal(ChannelOutcome.RejectedBeforeNative, refusal.Outcome);
        Assert.Contains("worker-read-running", refusal.EvidenceJson);
        Assert.Equal(id, wire.Client.LastRequestId);
        wire.Input.Feed(ProtocolTests.Reply());
        Assert.True(await wire.Client.WaitForDrain(ProtocolTests.Budget));
        wire.Output.OnWrite = _ => wire.Input.Feed(ProtocolTests.Reply(2));
        await wire.Call();
        Assert.False(wire.Client.Poisoned);
    }

    [Fact]
    public async Task Timed_out_write_still_poisons()
    {
        using var wire = new ProtocolTests.Wire();
        await wire.Connect();
        await Assert.ThrowsAsync<ChannelFaultException>(() => wire.Client.CallToolAsync(
            "adapter.SaveProject", "{}", BindingChange.None, false, TimeSpan.FromMilliseconds(20), false, null, null,
            drainOnTimeout: true));
        Assert.True(wire.Client.Poisoned);
        Assert.True(wire.Client.Unanswered);
        Assert.False(wire.Client.Draining);
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
}
