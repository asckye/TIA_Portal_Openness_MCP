using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaOpenness.Shared;

namespace TiaMcp.FoundationHost;

internal interface IHostSession : IDisposable
{
    public string SessionKey { get; }
    public string Instructions { get; }
    public bool OperatorOwned { get; }
    public CancellationToken Closed { get; }
    public IReadOnlyList<McpServerTool> Tools { get; }
    public IReadOnlyList<McpServerTool> OperatorTools { get; }
    public JsonObject Snapshot();
    public event Action? StateChanged;
    public void BindAi(IMcpServer server);
    public IDisposable AttachOperator();
}

internal sealed class HostSession : IHostSession
{
    private readonly object sync = new();
    private readonly Func<JsonObject> workerState;
    private readonly Action dispose;
    private readonly string release;
    private readonly DateTimeOffset created = DateTimeOffset.UtcNow;
    private readonly CancellationTokenSource closed = new();
    private readonly Queue<JsonObject> calls = new();
    private JsonObject? lane;
    private string? endedBy, endReason, clientName;
    private string? mcpSessionId;
    private long revision;
    private int operators;
    private bool disposed;
    private JsonObject? finalWorkerState;
    private JsonObject observedWorkerState;
    private SessionNotifications? aiNotifications;
    private Action<string>? registerAi;
    private readonly CancellationToken closedToken;
    public string SessionKey { get; } = Guid.NewGuid().ToString("N");
    public string Instructions { get; }
    public bool OperatorOwned { get; }
    public CancellationToken Closed => closedToken;
    public IReadOnlyList<McpServerTool> Tools { get; }
    public IReadOnlyList<McpServerTool> OperatorTools { get; }
    public event Action? StateChanged;

    internal HostSession(string release, string instructions, bool operatorOwned, IReadOnlyList<McpServerTool> tools,
        IReadOnlyList<McpServerTool> operatorTools, Func<JsonObject> workerState, Action dispose)
    {
        this.release = release; Instructions = instructions; OperatorOwned = operatorOwned;
        closedToken = closed.Token;
        this.workerState = workerState; this.dispose = dispose;
        observedWorkerState = workerState();
        Tools = tools.Select(t => (McpServerTool)new ObservedTool(t, this)).ToArray();
        OperatorTools = operatorTools.Select(t => (McpServerTool)new OperatorTool(t, this)).ToArray();
    }
    internal static HostSession Legacy(HostOptions options, string instructions, bool operatorOwned = false)
    {
        var worker = new WorkerClient(options.ReleaseKey, options.WorkerExe, options.ApiDirectory, options.NativeEnabled, options.EngineTimeoutSeconds, options.WorkerTimeoutConfig) { Bundled = options.BundledWorker };
        try
        {
            var tools = LegacyHostToolRegistry.Create(worker, options.ReleaseKey, options.NativeEnabled, options.ApiDirectory, options.ApiDirectorySource);
            var pipeline = (EngineHostPipeline)worker.HostPipeline!;
            TiaMcp.Logic.ModelContextProtocol.ImportStagingSession? staging = null;
            var session = new HostSession(options.ReleaseKey, instructions, operatorOwned, tools, pipeline.OperatorTools,
                () => { var state = worker.OperatorSnapshot(); state["sessionLocked"] = worker.Poisoned || pipeline.OperatorSessionLocked; return state; },
                () => { staging?.Dispose(); worker.Dispose(); });
            if (operatorOwned || options.Transport == "stdio")
            {
                staging = FoundationTool.RegisterStagingSession(worker, session.SessionKey);
                session.mcpSessionId = session.SessionKey;
            }
            else session.registerAi = id => staging = FoundationTool.RegisterStagingSession(worker, id);
            return session;
        }
        catch { worker.Dispose(); throw; }
    }
    public void BindAi(IMcpServer server)
    {
        if (OperatorOwned) throw new InvalidOperationException("AI clients cannot join an operator-owned session.");
        lock (sync) clientName = server.ClientInfo?.Name is { } name ? name.Substring(0, Math.Min(128, name.Length)) : null;
        lock (sync)
        {
            if (mcpSessionId == null)
            {
                mcpSessionId = server.SessionId ?? SessionKey;
                registerAi?.Invoke(mcpSessionId); registerAi = null;
            }
            aiNotifications ??= new SessionNotifications(this, server, OperatorEndpoint.Report);
        }
    }
    public IDisposable AttachOperator()
    {
        lock (sync) { if (disposed) throw new InvalidOperationException("The host session has ended."); operators++; }
        Changed();
        return new Attachment(this);
    }
    private sealed class Attachment(HostSession owner) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (owner.sync) owner.operators--;
            owner.Changed();
        }
    }
    public JsonObject Snapshot()
    {
        lock (sync)
        {
            var worker = finalWorkerState ?? workerState();
            bool disconnected = disposed || (bool?)worker["disconnected"] == true;
            bool locked = (bool?)worker["sessionLocked"] == true;
            return new JsonObject {
                ["sessionKey"] = SessionKey, ["releaseKey"] = release, ["createdUtc"] = created.ToString("O"), ["revision"] = revision,
                ["owner"] = OperatorOwned ? "workbench" : "mcp", ["endedBy"] = endedBy, ["endReason"] = endReason,
                ["participants"] = new JsonObject { ["mcpClient"] = clientName, ["mcpClientVerified"] = false, ["workbench"] = operators },
                ["worker"] = new JsonObject { ["pid"] = worker["pid"]?.DeepClone(), ["generation"] = worker["generation"]?.DeepClone(),
                    ["state"] = disconnected ? "disconnected" : locked ? "poisoned" : lane != null ? "busy" : "idle" },
                ["lane"] = lane?.DeepClone(), ["binding"] = worker["binding"]?.DeepClone(), ["sessionLocked"] = locked,
                ["approvalEnabled"] = ApprovalSettings.Load(ApprovalSettings.SettingsPath).Enabled,
                ["recentCalls"] = new JsonArray(calls.Select(c => (JsonNode)c.DeepClone()).ToArray()) };
        }
    }
    internal void LaneChanged(bool held, string name, string actor, string id)
    {
        lock (sync)
        {
            lane = held ? new JsonObject { ["tool"] = name, ["actor"] = actor, ["requestId"] = id, ["sinceUtc"] = DateTimeOffset.UtcNow.ToString("O") } : null;
            if (!held) ObserveWorker(actor);
        }
        Changed();
    }
    private void ObserveWorker(string actor)
    {
        var after = finalWorkerState ?? workerState();
        string? reason = (bool?)after["disconnected"] == true && (bool?)observedWorkerState["disconnected"] != true ? "disconnect"
            : !JsonNode.DeepEquals(observedWorkerState["generation"], after["generation"]) ? "worker-restart"
            : (bool?)after["sessionLocked"] == true && (bool?)observedWorkerState["sessionLocked"] != true ? "session-locked" : null;
        if (reason != null && (endReason != "disconnect" || reason == "worker-restart")) { endedBy = actor; endReason = reason; }
        observedWorkerState = (JsonObject)after.DeepClone();
    }
    private void Ended(string actor, string reason)
    {
        lock (sync)
        {
            endedBy = actor; endReason = reason;
            observedWorkerState = workerState();
        }
        Changed();
    }
    private void Changed()
    {
        lock (sync) revision++;
        foreach (Action handler in StateChanged?.GetInvocationList() ?? [])
        {
            try { handler(); }
            catch (Exception error) { OperatorEndpoint.Report(error); }
        }
    }
    internal async ValueTask<CallToolResult> Invoke(McpServerTool inner, RequestContext<CallToolRequestParams> request, bool workbench, CancellationToken token)
    {
        lock (sync) if (disposed) throw new InvalidOperationException("The host session has ended; initialize a new session.");
        if (!workbench) BindAi(request.Server);
        string id = Guid.NewGuid().ToString("N"), actor = workbench ? ActorScope.Workbench : ActorScope.Mcp;
        using var attribution = workbench ? ActorScope.EnterWorkbench(mcpSessionId ?? SessionKey, id) : ActorScope.EnterCall(mcpSessionId ?? SessionKey, request.Server);
        using var observation = HostSessionCall.Enter(LaneChanged, Ended, inner.ProtocolTool.Name, actor, id);
        var elapsed = Stopwatch.StartNew();
        CallToolResult? result = null;
        try { return result = await inner.InvokeAsync(request, token).ConfigureAwait(false); }
        finally
        {
            lock (sync)
            {
                calls.Enqueue(new JsonObject { ["requestId"] = (string?)result?.StructuredContent?["meta"]?["requestId"] ?? id,
                    ["tool"] = inner.ProtocolTool.Name, ["actor"] = actor, ["outcome"] = (string?)result?.StructuredContent?["meta"]?["outcome"] ?? "unknown", ["ms"] = elapsed.ElapsedMilliseconds });
                while (calls.Count > 20) calls.Dequeue();
            }
            Changed();
        }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            finalWorkerState = workerState(); disposed = true;
            if (endReason != "disconnect") { endedBy = OperatorOwned ? ActorScope.Workbench : ActorScope.Mcp; endReason = "session-ended"; }
        }
        Changed(); closed.Cancel();
        if (aiNotifications != null) aiNotifications.DisposeAsync().AsTask().GetAwaiter().GetResult();
        dispose();
        closed.Dispose();
    }
    private sealed class ObservedTool(McpServerTool inner, HostSession session) : McpServerTool
    {
        public override Tool ProtocolTool => inner.ProtocolTool;
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
            => session.Invoke(inner, request, false, cancellationToken);
    }
}

internal sealed class OperatorTool(McpServerTool inner, HostSession session) : McpServerTool
{
    public override Tool ProtocolTool => inner.ProtocolTool;
    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        => session.Invoke(inner, request, true, cancellationToken);
}

