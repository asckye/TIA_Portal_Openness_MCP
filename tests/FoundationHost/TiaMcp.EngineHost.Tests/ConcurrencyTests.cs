using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;
using Xunit;

public sealed partial class PipelineTests
{
    [Theory]
    [InlineData("GetToolUsage", false)]
    [InlineData("GetExportContent", false)]
    [InlineData("UnknownFutureTool", true)]
    [InlineData("GetEnvironmentDiagnostics", true)]
    [InlineData("PreviewToolCall", true)]
    public void DispatchClassificationRemainsExplicit(string name, bool native)
        => Assert.Equal(native, ToolTaxonomy.UsesOpennessLane(name));

    [Fact]
    public async Task LocalLaneRemainsBoundedAtEight()
    {
        using var fixture = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, maximum = 0;
        var tool = McpServer.WrapWithSerializedCalls(new List<McpServerTool> { new ConcurrentTool("BuildStructuredText", async token => {
            int current = Interlocked.Increment(ref active);
            InterlockedExtensions.Maximum(ref maximum, current);
            if (current == 8) entered.TrySetResult();
            try { await release.Task.WaitAsync(token); } finally { Interlocked.Decrement(ref active); }
        }) })[0];
        var calls = Enumerable.Range(0, 12).Select(_ => tool.InvokeAsync(Request("BuildStructuredText")).AsTask()).ToArray();
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(8, active); }
        finally { release.TrySetResult(); }
        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(8, maximum);
    }

    [Fact]
    public async Task WorkerLaneSerializesCallsAndCancelsQueuedRequests()
    {
        using var fixture = new Fixture();
        var lanes = typeof(McpServer).Assembly.GetType("TiaMcpServer.Dispatch.ToolDispatchLanes", true)!;
        var acquire = lanes.GetMethod("AcquireForSession", BindingFlags.Static | BindingFlags.NonPublic)!;
        Task<IDisposable?> Acquire(CancellationToken token = default) => (Task<IDisposable?>)acquire.Invoke(null,
            new object[] { "GetSessionState", fixture.Worker.SessionKey, token })!;
        using (await Acquire())
        {
            using var cancel = new CancellationTokenSource();
            var pending = Acquire(cancel.Token);
            Assert.False(pending.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        using (await Acquire()) { }
    }

    [Fact]
    public async Task IndependentSessionsDoNotShareWorkerLane()
    {
        using var first = new Fixture();
        var lanes = typeof(McpServer).Assembly.GetType("TiaMcpServer.Dispatch.ToolDispatchLanes", true)!;
        var acquire = lanes.GetMethod("AcquireForSession", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var held = await (Task<IDisposable?>)acquire.Invoke(null, new object[] { "GetSessionState", first.Worker.SessionKey, CancellationToken.None })!;
        using var second = new Fixture();
        using var independent = await ((Task<IDisposable?>)acquire.Invoke(null, new object[] { "GetSessionState", second.Worker.SessionKey, CancellationToken.None })!)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ApprovalWaitDoesNotOccupyWorkerLane()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var grant = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Wait = async (pending, _, token) => { entered.TrySetResult(); await grant.Task.WaitAsync(token); return new ApprovalOutcome(pending, false, null); };
        var write = Task.Run(() => fixture.Call("SaveProject", "{}"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True((bool?)(await Task.Run(() => fixture.Call("GetSessionState", "{}")).WaitAsync(TimeSpan.FromSeconds(5))).StructuredContent?["ok"]);
            Assert.DoesNotContain("SaveProject", fixture.Worker.Calls);
        }
        finally { grant.TrySetResult(); }
        await write.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("SaveProject", fixture.Worker.Calls);
    }

    [Fact]
    public async Task ConcurrentAuditWritersPreserveRotatedChainAndRefuseInterruptedTail()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "audit-concurrency-" + Guid.NewGuid().ToString("N"));
        var logs = new[] { new AuditLog(root, 1024), new AuditLog(root, 1024) };
        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() => {
            for (int j = 0; j < 20; j++) logs[i % 2].Append("start", "fixture", "engine", "21", "SaveProject");
        })));
        var verified = logs[0].Verify(); Assert.True(verified.Passed); Assert.Equal(80, verified.Count);
        var tail = Directory.GetFiles(root, "audit-*.jsonl").Order().Last();
        var bytes = File.ReadAllBytes(tail); bytes[^1] = (byte)' '; File.WriteAllBytes(tail, bytes);
        Assert.ThrowsAny<Exception>(() => logs[0].Append("start", "fixture", "engine", "21", "SaveProject"));
    }

    [Theory]
    [InlineData("open"), InlineData("closed"), InlineData("expired"), InlineData("restarted"), InlineData("unknown"), InlineData("active")]
    public void HttpStagingOwnersRetainCleanupAdmissionAfterSessionEnd(string state)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "staging-lifecycle-" + Guid.NewGuid().ToString("N"));
        using var owner = new TiaMcp.Logic.ModelContextProtocol.ImportStagingSession("session-a");
        using var peer = new TiaMcp.Logic.ModelContextProtocol.ImportStagingSession("session-b");
        using var first = new TiaMcp.Logic.ModelContextProtocol.ImportStagingStore(root, "21", owner);
        using var current = new TiaMcp.Logic.ModelContextProtocol.ImportStagingStore(root, "21", peer);
        var batch = first.Stage(new[] { new TiaMcp.Logic.ModelContextProtocol.StagedTextFile {
            FileName = "F.scl", Kind = "scl", Content = "FUNCTION F : Void\nBEGIN\nEND_FUNCTION" } }, false);
        string id = (string)batch["batchId"]!, folder = (string)batch["directory"]!;
        Assert.Equal("session-a", (string?)batch["mcpSessionId"]);
        IDisposable? active = state == "active" ? owner.EnterRequest() : null;
        try
        {
            if (state is "closed" or "expired" or "active") owner.Dispose();
            if (state is "unknown" or "restarted")
                foreach (string name in new[] { ".staging-batch.json", ".staging-batch.previous.json" })
                {
                    string path = Path.Combine(folder, name); var manifest = JsonNode.Parse(File.ReadAllText(path))!;
                    manifest["hostInstanceId"] = Guid.NewGuid().ToString("N");
                    if (state == "restarted") manifest["hostStartedUtc"] = DateTimeOffset.UtcNow.AddYears(-1).ToString("O");
                    File.WriteAllText(path, manifest.ToJsonString());
                }
            bool ended = state is "closed" or "expired" or "restarted";
            Assert.Equal(ended ? "ended" : state == "unknown" ? "unknown" : "live-other",
                (string?)current.List()["batches"]![0]!["ownerState"]);
            foreach (bool preview in new[] { true, false })
                if (ended) current.Cleanup(id, preview);
                else Assert.Throws<ArgumentException>(() => current.Cleanup(id, preview));
            if (state == "active")
            {
                active!.Dispose(); active = null; current.Cleanup(id, false); Assert.False(Directory.Exists(folder));
            }
        }
        finally { active?.Dispose(); }
    }

    private static RequestContext<CallToolRequestParams> Request(string name) => new(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
        Params = new() { Name = name, Arguments = new Dictionary<string, JsonElement>() }
    };
    private sealed class ConcurrentTool(string name, Func<CancellationToken, Task> action) : McpServerTool
    {
        public override Tool ProtocolTool => new() { Name = name, InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) };
        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken token = default)
        {
            await action(token);
            return McpServer.V4Result(name, new JsonObject(), null, false, false);
        }
    }
    private static class InterlockedExtensions
    {
        internal static void Maximum(ref int target, int value)
        {
            int previous;
            do { previous = Volatile.Read(ref target); if (previous >= value) return; }
            while (Interlocked.CompareExchange(ref target, value, previous) != previous);
        }
    }
}
