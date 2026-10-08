using System.Text;
using TiaMcp.WorkerChannel;
using Xunit;
using static TiaMcp.WorkerChannel.Tests.ProtocolTests;

namespace TiaMcp.WorkerChannel.Tests;

public sealed class EngineProfileTests
{
    private static byte[] Frame(string method, bool preview = false, long id = 1, long epoch = 0) => Encoding.UTF8.GetBytes(
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"" + method + "\",\"params\":{\"preview\":" + (preview ? "true" : "false") + "},\"bindingEpoch\":" + epoch + "}");

    [Theory]
    [InlineData("adapter.Read", false)] [InlineData("invoke", false)] [InlineData("engine.", false)]
    [InlineData("engine.status", true)] [InlineData("engine.invoke", true)]
    public void Namespace(string method, bool valid)
    {
        using var output = new MemoryStream();
        var server = new ChannelServer(Stream.Null, output, Identity, () => new(0, false), _ => ChannelResponse.Success("null"), ChannelProfile.Engine);
        server.WriteHello();
        if (valid) server.Handle(Frame(method)); else Assert.Throws<ChannelFault>(() => server.Handle(Frame(method)));
        Assert.Equal(!valid, server.Poisoned);
    }

    [Theory]
    [InlineData("engine.status", false, 1, false)] [InlineData("engine.invoke", true, 1, false)]
    [InlineData("engine.invoke", false, 1, true)] [InlineData("engine.invoke", false, 2, false)]
    [InlineData("engine.invoke", true, 0, true)]
    public void EpochRules(string method, bool preview, long next, bool valid)
    {
        using var output = new MemoryStream(); long epoch = 0;
        var server = new ChannelServer(Stream.Null, output, Identity, () => new(epoch, false), _ => { epoch = next; return ChannelResponse.Success("null"); }, ChannelProfile.Engine);
        server.WriteHello();
        if (valid) server.Handle(Frame(method, preview)); else Assert.Throws<ChannelFault>(() => server.Handle(Frame(method, preview)));
        Assert.Equal(!valid, server.Poisoned);
    }

    [Fact]
    public void UnknownToolOutcomeKeepsServerUsable()
    {
        using var output = new MemoryStream(); int calls = 0;
        var server = new ChannelServer(Stream.Null, output, Identity, () => new(0, false), _ => { calls++; return ChannelResponse.Success("{\"result\":{\"meta\":{\"outcome\":\"unknown\"}}}"); }, ChannelProfile.Engine);
        server.WriteHello(); server.Handle(Frame("engine.invoke")); server.Handle(Frame("engine.status", id: 2));
        Assert.False(server.Poisoned); Assert.Equal(2, calls);
    }

    [Fact]
    public void ChannelUnknownRemainsTerminal()
    {
        using var output = new MemoryStream();
        var server = new ChannelServer(Stream.Null, output, Identity, () => new(0, false), _ => ChannelResponse.Error(new("lost", -32603, ChannelOutcome.Unknown)), ChannelProfile.Engine);
        server.WriteHello(); Assert.Throws<ChannelFault>(() => server.Handle(Frame("engine.invoke"))); Assert.True(server.Poisoned);
    }

    [Fact]
    public void OversizedReplyUsesLimitEnvelopeAndAcceptsNextRequest()
    {
        using var output = new MemoryStream();
        var server = new ChannelServer(Stream.Null, output, Identity, () => new(0, false), _ => ChannelResponse.Success(
            "\"" + new string('x', ChannelLimits.ResponseBytes) + "\"", "{\"error\":{\"code\":\"LIMIT_EXCEEDED\"}}"), ChannelProfile.Engine);
        server.WriteHello(); server.Handle(Frame("engine.invoke")); server.Handle(Frame("engine.status", id: 2));
        Assert.False(server.Poisoned); Assert.Contains("LIMIT_EXCEEDED", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public void ProgressObjectStaysOnOwnerThread()
    {
        using var output = new MemoryStream(); int owner = Environment.CurrentManagedThreadId;
        var server = new ChannelServer(Stream.Null, output, Identity, () => new(0, false), r => {
            Assert.Equal(owner, Environment.CurrentManagedThreadId); r.ReportProgress(50, "{\"progress\":1,\"total\":2}"); return ChannelResponse.Success("null"); }, ChannelProfile.Engine);
        server.WriteHello(); server.Handle(Frame("engine.invoke"));
        Assert.Contains("\"payload\":{\"progress\":1", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task RequestLimitDoesNotConsumeIdOrPoisonClient()
    {
        using var input = new FeedStream(); using var output = new CaptureStream();
        using var client = new ChannelClient(input, output, Identity, ChannelProfile.Engine);
        input.Feed(Hello); await client.ConnectAsync(Budget);
        await Assert.ThrowsAsync<ChannelLimitException>(() => client.CallAsync("engine.invoke", "{\"large\":\"" + new string('x', ChannelLimits.RequestBytes) + "\"}", BindingChange.None, true, Budget));
        Assert.False(client.Poisoned); Assert.Equal(0, client.LastRequestId); Assert.Empty(output.Writes);
        var call = client.CallAsync("engine.status", "{}", BindingChange.None, true, Budget);
        input.Feed(Reply()); await call; Assert.Equal(1, client.LastRequestId);
    }

    [Fact]
    public async Task ClientForwardsProgressAndUnknownToolReply()
    {
        using var input = new FeedStream(); using var output = new CaptureStream(); string? payload = null;
        using var client = new ChannelClient(input, output, Identity, ChannelProfile.Engine, p => payload = p);
        input.Feed(Hello); await client.ConnectAsync(Budget);
        var call = client.CallAsync("engine.invoke", "{}", BindingChange.MayAdvance, false, Budget);
        input.Feed("{\"jsonrpc\":\"2.0\",\"method\":\"progress\",\"params\":{\"requestId\":1,\"sequence\":1,\"percent\":50,\"payload\":{\"progress\":1}}}");
        input.Feed(Reply(payload: "\"result\":{\"meta\":{\"outcome\":\"unknown\"}}"));
        Assert.Contains("unknown", await call); Assert.Equal("{\"progress\":1}", payload); Assert.False(client.Poisoned);
    }
}
