using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.WorkerChannel;
using Xunit;
using static TiaMcp.WorkerChannel.Tests.ProtocolTests;

namespace TiaMcp.WorkerChannel.Tests;

public sealed class EngineProfileTests
{
    [Fact]
    public void Long_class_budget_is_300_seconds()
    {
        var policy = new WorkerTimeoutPolicy();
        foreach (string family in new[] { "compile", "transfer", "importExport", "archive", "libraryUpdate" })
            Assert.Equal(TimeSpan.FromSeconds(300), policy.Budgets[family]);
    }

    [Fact]
    public async Task Answered_unknown_permits_independent_project_and_simulator_calls()
    {
        using var input = new FeedStream(); using var output = new CaptureStream();
        using var client = new ChannelClient(input, output, Identity, ChannelProfile.Engine);
        input.Feed(Hello); await client.ConnectAsync(Budget);
        var project = client.CallAsync("adapter.ProjectMutation", "{}", BindingChange.None, false, Budget);
        input.Feed(Reply(payload: "\"error\":{\"code\":-32603,\"message\":\"unknown\",\"data\":{\"outcome\":\"Unknown\",\"evidence\":null}}"));
        var failure = await Assert.ThrowsAsync<ChannelFailureException>(() => project);
        Assert.Equal(ChannelOutcome.Unknown, failure.Outcome); Assert.Equal(-32603, failure.Code);
        Assert.True(client.OutcomeUnknown); Assert.Single(output.Writes);
        Assert.False(client.Poisoned); Assert.True(client.CanCallIndependentRuntime);
        var simulation = client.CallAsync("adapter.runtime-channels.ExecutePlcSimAdvanced", "{}", BindingChange.None, true, Budget);
        input.Feed(Reply(2, payload: "\"result\":{\"simulator\":true}"));
        Assert.Equal("{\"simulator\":true}", await simulation);
        var next = client.CallAsync("engine.invoke", "{}", BindingChange.None, false, Budget);
        input.Feed(Reply(3, payload: "\"result\":{}"));
        Assert.Equal("{}", await next);
        Assert.True(client.OutcomeUnknown); Assert.False(client.Poisoned); Assert.Equal(3, output.Writes.Count);
    }
    [Fact]
    public void Timeout_defaults_and_override_precedence_are_shared_by_all_worker_operations()
    {
        var policy = new WorkerTimeoutPolicy(7, "{\"default\":8,\"compile\":9,\"firstAttach\":10}", "{\"compile\":11}");
        Assert.Equal(TimeSpan.FromSeconds(120), WorkerTimeoutPolicy.Defaults["default"]);
        Assert.Equal(TimeSpan.FromSeconds(300), WorkerTimeoutPolicy.Defaults["firstAttach"]);
        foreach (string family in new[] { "compile", "transfer", "importExport", "archive", "libraryUpdate" })
            Assert.Equal(TimeSpan.FromSeconds(300), WorkerTimeoutPolicy.Defaults[family]);
        Assert.Equal(TimeSpan.FromSeconds(8), policy.ForPlc("ReadState", new()));
        Assert.Equal(TimeSpan.FromSeconds(11), policy.ForPlc("CompileSoftware", new() { ["dryRun"] = false }));
        Assert.Equal(policy.For("CompilePlc"), policy.ForPlc("CompileSoftware", new() { ["dryRun"] = false }));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.ForPlc("Attach", new(), true));
        Assert.Equal(TimeSpan.FromSeconds(7), new WorkerTimeoutPolicy(7).ForPlc("ReadState", new()));
    }

    [Fact]
    public void Foundation_and_engine_share_one_owner_epoch_and_correlation()
    {
        using var output = new MemoryStream();
        int owner = Environment.CurrentManagedThreadId; long epoch = 0; var methods = new List<string>();
        var server = new ChannelServer(Stream.Null, output, Identity, () => new(epoch, epoch != 0), request => {
            Assert.Equal(owner, Environment.CurrentManagedThreadId);
            Assert.Equal("shared-correlation", request.CorrelationId);
            methods.Add(request.Method);
            if (request.Method == "adapter.Attach") epoch++;
            return ChannelResponse.Success("null");
        }, ChannelProfile.Engine);
        server.WriteHello();
        server.Handle(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"adapter.Attach\",\"params\":{\"processId\":1234},\"bindingEpoch\":0,\"requestId\":\"shared-correlation\"}"));
        server.Handle(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"engine.invoke\",\"params\":{},\"bindingEpoch\":1,\"requestId\":\"shared-correlation\"}"));
        Assert.Equal(new[] { "adapter.Attach", "engine.invoke" }, methods); Assert.Equal(1, epoch); Assert.False(server.Poisoned);
    }

    private static byte[] Frame(string method, bool preview = false, long id = 1, long epoch = 0) => Encoding.UTF8.GetBytes(
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"" + method + "\",\"params\":{\"preview\":" + (preview ? "true" : "false") + "},\"bindingEpoch\":" + epoch + "}");

    [Theory]
    [InlineData("adapter.Read", true)] [InlineData("adapter.", false)] [InlineData("invoke", false)] [InlineData("engine.", false)]
    [InlineData("engine.status", true)] [InlineData("engine.invoke", true)]
    public void Namespace(string method, bool valid)
    {
        using var output = new MemoryStream();
        var server = new ChannelServer(Stream.Null, output, Identity, () => new(0, false), _ => ChannelResponse.Success("null"), ChannelProfile.Engine);
        server.WriteHello();
        if (valid) server.Handle(Frame(method)); else Assert.Throws<ChannelFaultException>(() => server.Handle(Frame(method)));
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
        if (valid) server.Handle(Frame(method, preview)); else Assert.Throws<ChannelFaultException>(() => server.Handle(Frame(method, preview)));
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
    public void ChannelUnknownPermitsLaterCallsAndDisconnect()
    {
        using var output = new MemoryStream(); int calls = 0;
        var server = new ChannelServer(Stream.Null, output, Identity, () => new(0, false), request => {
            calls++; return request.Method == "engine.invoke" ? ChannelResponse.Error(new("lost", -32603, ChannelOutcome.Unknown)) : ChannelResponse
                .Success("{}");
        }, ChannelProfile.Engine);
        server.WriteHello(); server.Handle(Frame("engine.invoke")); Assert.False(server.Poisoned);
        Assert.Equal(1, calls);
        var failure = JsonNode.Parse(Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Last())!;
        Assert.Equal("Unknown", (string?)failure["error"]?["data"]?["outcome"]);
        Assert.Equal(-32603, (int?)failure["error"]?["code"]);
        server.Handle(Frame("adapter.Disconnect", id: 2)); Assert.Equal(2, calls);
        server.Handle(Frame("engine.status", id: 3));
        Assert.Equal(3, calls); Assert.False(server.Poisoned);
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
    [Fact]
    public async Task Dropped_progress_logs_once_and_stops_forwarding_until_the_reply()
    {
        Guid previous = System.Diagnostics.Trace.CorrelationManager.ActivityId;
        Guid activity = Guid.NewGuid();
        System.Diagnostics.Trace.CorrelationManager.ActivityId = activity;
        using var listener = new ProgressListener(activity);
        System.Diagnostics.Trace.Listeners.Add(listener);
        try
        {
            using var input = new FeedStream();
            using var output = new CaptureStream();
            var forwarded = new List<string>();
            using var client = new ChannelClient(input, output, Identity, ChannelProfile.Engine, forwarded.Add);
            input.Feed(Hello);
            await client.ConnectAsync(Budget);
            var call = client.CallAsync("engine.invoke", "{}", BindingChange.None, false, Budget);
            string ProgressFrame(int sequence) => "{\"jsonrpc\":\"2.0\",\"method\":\"progress\",\"params\":{\"requestId\":1,\"sequence\":"
                + sequence + ",\"percent\":50,\"payload\":{\"progress\":" + sequence + "}}}";
            input.Feed(ProgressFrame(1) + "\n" + ProgressFrame(3) + "\n" + ProgressFrame(2) + "\n" + ProgressFrame(4) + "\n" + Reply());
            Assert.Equal("null", await call);
            Assert.Single(forwarded);
            Assert.Equal("{\"progress\":1}", forwarded[0]);
            Assert.Single(listener.Warnings);
            Assert.Contains("Worker progress dropped for request 1", listener.Warnings[0]);
            Assert.False(client.Poisoned);
        }
        finally
        {
            System.Diagnostics.Trace.Listeners.Remove(listener);
            System.Diagnostics.Trace.CorrelationManager.ActivityId = previous;
        }
    }

    private sealed class ProgressListener(Guid activity) : System.Diagnostics.TraceListener
    {
        internal readonly List<string> Warnings = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (System.Diagnostics.Trace.CorrelationManager.ActivityId == activity && message?.Contains("Worker progress dropped") == true)
                Warnings.Add(message);
        }
    }

}
