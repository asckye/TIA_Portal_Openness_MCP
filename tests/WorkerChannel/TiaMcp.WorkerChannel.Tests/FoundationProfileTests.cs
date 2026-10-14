using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.WorkerChannel;
using Xunit;
using static TiaMcp.WorkerChannel.Tests.ProtocolTests;

namespace TiaMcp.WorkerChannel.Tests;

public sealed class FoundationProfileTests
{
    [Theory]
    [InlineData("request-1:part_2.3")] [InlineData("boundary")]
    public async Task Correlation_and_tool_name_are_bounded_and_accepted(string id)
    {
        if (id == "boundary") id = new string('a', 128);
        using var input = new FeedStream(); using var output = new CaptureStream();
        using var client = new ChannelClient(input, output, Identity);
        input.Feed(Hello); await client.ConnectAsync(Budget);
        using var replies = new MemoryStream();
        var server = new ChannelServer(Stream.Null, replies, Identity, () => new(0, false), request => {
            Assert.Equal(id, request.CorrelationId); Assert.Equal("GetSessionState", request.ToolName);
            Assert.Equal("{}", request.ArgumentsJson); return ChannelResponse.Success("null");
        });
        server.WriteHello();
        output.OnWrite = frame => {
            server.Handle(Encoding.UTF8.GetBytes(frame.TrimEnd('\n')));
            input.Feed(Encoding.UTF8.GetString(replies.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Last());
        };
        Assert.Equal("null", await client.CallToolAsync("adapter.ReadState", "{}", BindingChange.None, true, Budget, firstAttach: false,
            correlationId: id, toolName: "GetSessionState"));
        Assert.False(client.Poisoned); Assert.False(server.Poisoned); Assert.Single(output.Writes);
    }

    [Theory]
    [InlineData("")] [InlineData("oversized")] [InlineData("bad id")] [InlineData("bad\nId")]
    [InlineData("汉")] [InlineData("bad/id")]
    public async Task Invalid_correlation_is_rejected_without_consuming_a_client_id(string id)
    {
        if (id == "oversized") id = new string('a', 129);
        using var wire = new Wire(); await wire.Connect();
        await Assert.ThrowsAsync<ArgumentException>(() => wire.Client.CallAsync("adapter.ReadState", "{}", BindingChange.None,
            true, Budget, correlationId: id));
        Assert.False(wire.Client.Poisoned); Assert.Equal(0, wire.Client.LastRequestId); Assert.Empty(wire.Output.Writes);
        int calls = 0; using var replies = new MemoryStream();
        var server = new ChannelServer(Stream.Null, replies, Identity, () => new(0, false), _ => { calls++; return ChannelResponse.Success("null"); });
        server.WriteHello();
        var request = JsonNode.Parse(Request())!; request["requestId"] = id;
        var fault = Assert.Throws<ChannelFaultException>(() => server.Handle(Encoding.UTF8.GetBytes(request.ToJsonString())));
        Assert.False(fault.OutcomeUnknown); Assert.Equal(0, calls); Assert.True(server.Poisoned);
    }

    [Fact]
    public void Oversized_reply_uses_the_same_refusal_and_keeps_the_server_usable()
    {
        using var output = new MemoryStream();
        const string refusal = "{\"error\":{\"code\":\"LIMIT_EXCEEDED\"}}";
        var server = new ChannelServer(Stream.Null, output, Identity, () => new(0, false), _ =>
            ChannelResponse.Success("\"" + new string('x', ChannelLimits.ResponseBytes) + "\"", refusal));
        server.WriteHello(); server.Handle(Encoding.UTF8.GetBytes(Request())); server.Handle(Encoding.UTF8.GetBytes(Request(2)));
        var frames = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, frames.Length);
        Assert.All(frames.Skip(1), frame => Assert.Equal(refusal, JsonNode.Parse(frame)!["result"]!.ToJsonString()));
        Assert.False(server.Poisoned);
    }

    [Fact]
    public void Spill_is_accepted_consumed_and_cleaned_before_the_next_request()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "foundation-spill-" + Guid.NewGuid().ToString("N"));
        try
        {
            string full = "{\"text\":\"" + new string('x', ChannelLimits.ResponseBytes) + "\"}";
            using var output = new MemoryStream();
            var server = new ChannelServer(Stream.Null, output, Identity, () => new(0, false), _ =>
                ChannelResponse.WithSpill(full, () => WorkerReplySpill.Write(directory, full)));
            server.WriteHello(); server.Handle(Encoding.UTF8.GetBytes(Request()));
            string reply = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Last();
            string descriptor = JsonNode.Parse(reply)!["result"]!.ToJsonString();
            Assert.Single(Directory.GetFiles(directory));
            Assert.Equal(full, WorkerReplySpill.Read(directory, descriptor, out bool spilled)); Assert.True(spilled);
            Assert.Empty(Directory.GetFiles(directory));
            Assert.Throws<FileNotFoundException>(() => WorkerReplySpill.Read(directory, descriptor, out _));
            server.Handle(Encoding.UTF8.GetBytes(Request(2))); Assert.False(server.Poisoned);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
