using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.WorkerChannel;
using Xunit;

namespace TiaMcp.WorkerChannel.Tests;

public sealed class LeaseMetadataTests
{
    [Fact]
    public void Unsettled_id_count_boundary_is_accepted()
    {
        using var output = new MemoryStream();
        int calls = 0;
        var server = new ChannelServer(Stream.Null, output, ProtocolTests.Identity, () => new ChannelBinding(0, false), request => {
            calls++;
            Assert.Equal(4096, request.UnsettledRequests!.Count);
            return ChannelResponse.Success("null");
        });
        server.WriteHello();
        var frame = JsonNode.Parse(ProtocolTests.Request())!;
        frame["unsettledRequests"] = new JsonArray(Enumerable.Range(0, 4096).Select(value => (JsonNode)JsonValue.Create("id-" + value)!).ToArray());
        server.Handle(Encoding.UTF8.GetBytes(frame.ToJsonString()));
        Assert.Equal(1, calls);
        Assert.False(server.Poisoned);
    }

    [Fact]
    public void Excess_unsettled_ids_are_rejected_before_dispatch()
    {
        string[] ids = Enumerable.Range(0, 4097).Select(value => "id-" + value).ToArray();
        Assert.Throws<ChannelLimitException>(() => ChannelLimits.CheckRequest("adapter.ReadState", "{}", "read", "GetSessionState", ids));
        using var output = new MemoryStream();
        int calls = 0;
        var server = new ChannelServer(Stream.Null, output, ProtocolTests.Identity, () => new ChannelBinding(0, false), _ => {
            calls++;
            return ChannelResponse.Success("null");
        });
        server.WriteHello();
        var frame = JsonNode.Parse(ProtocolTests.Request())!;
        frame["unsettledRequests"] = new JsonArray(ids.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());
        Assert.Throws<ChannelFaultException>(() => server.Handle(Encoding.UTF8.GetBytes(frame.ToJsonString())));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Request_preflight_counts_the_unsettled_metadata_before_dispatch()
    {
        string arguments = new JsonObject { ["value"] = new string('x', ChannelLimits.RequestBytes - 256) }.ToJsonString();
        ChannelLimits.CheckRequest("adapter.SaveProject", arguments, "write-1", "SaveProject", Array.Empty<string>());
        Assert.Throws<ChannelLimitException>(() => ChannelLimits.CheckRequest("adapter.SaveProject", arguments, "write-1", "SaveProject",
            new[] { new string('a', 128), new string('b', 128) }));
    }

    [Fact]
    public async Task Metadata_that_exceeds_the_frame_limit_is_not_sent_and_keeps_the_channel_usable()
    {
        using var wire = new ProtocolTests.Wire();
        await wire.Connect();
        wire.Client.UnsettledRequests = () => new[] { new string('a', 128), new string('b', 128) };
        string arguments = new JsonObject { ["value"] = new string('x', ChannelLimits.RequestBytes - 256) }.ToJsonString();
        await Assert.ThrowsAsync<ChannelLimitException>(() => wire.Call(arguments));
        Assert.Empty(wire.Output.Writes);
        Assert.False(wire.Client.Poisoned);
        Assert.Equal(0, wire.Client.LastRequestId);
        wire.Output.OnWrite = _ => wire.Input.Feed(ProtocolTests.Reply());
        Assert.Equal("null", await wire.Call());
    }

    [Fact]
    public async Task Unsettled_ids_travel_as_transport_metadata_with_the_current_snapshot()
    {
        using var wire = new ProtocolTests.Wire();
        await wire.Connect();
        string[] ids = ["prior-write"];
        wire.Client.UnsettledRequests = () => ids;
        wire.Output.OnWrite = text => {
            var frame = JsonNode.Parse(text)!;
            Assert.Equal(ids, frame["unsettledRequests"]!.AsArray().Select(value => (string)value!));
            Assert.Empty(frame["params"]!.AsObject());
            wire.Input.Feed(ProtocolTests.Reply((long)frame["id"]!));
        };
        await wire.Call();
        ids = [];
        await wire.Call();
        Assert.Equal(2, wire.Output.Writes.Count);
        Assert.False(wire.Client.Poisoned);
    }

    [Fact]
    public void Server_passes_unsettled_ids_to_the_owner_thread_dispatcher()
    {
        using var output = new MemoryStream();
        int owner = Environment.CurrentManagedThreadId;
        var server = new ChannelServer(Stream.Null, output, ProtocolTests.Identity, () => new ChannelBinding(0, false), request => {
            Assert.Equal(owner, Environment.CurrentManagedThreadId);
            Assert.Equal("write-2", request.CorrelationId);
            Assert.Equal(new[] { "prior-1", "prior-2" }, request.UnsettledRequests);
            Assert.Equal("{}", request.ArgumentsJson);
            return ChannelResponse.Success("null");
        });
        server.WriteHello();
        var frame = JsonNode.Parse(ProtocolTests.Request())!;
        frame["requestId"] = "write-2";
        frame["unsettledRequests"] = new JsonArray("prior-1", "prior-2");
        server.Handle(Encoding.UTF8.GetBytes(frame.ToJsonString()));
        Assert.False(server.Poisoned);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[1]")]
    [InlineData("[\"bad,id\"]")]
    public void Invalid_unsettled_metadata_is_refused_before_dispatch(string metadata)
    {
        using var output = new MemoryStream();
        int calls = 0;
        var server = new ChannelServer(Stream.Null, output, ProtocolTests.Identity, () => new ChannelBinding(0, false), _ => {
            calls++;
            return ChannelResponse.Success("null");
        });
        server.WriteHello();
        var frame = JsonNode.Parse(ProtocolTests.Request())!;
        frame["unsettledRequests"] = JsonNode.Parse(metadata);
        Assert.Throws<ChannelFaultException>(() => server.Handle(Encoding.UTF8.GetBytes(frame.ToJsonString())));
        Assert.Equal(0, calls);
    }
}
