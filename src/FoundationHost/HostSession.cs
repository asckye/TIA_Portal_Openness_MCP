#if TIA_FOUNDATION_TEST_HOST
extern alias enginehost;
using LateCalls = enginehost::TiaMcp.FoundationHost.LateCalls;
using UnsettledCalls = enginehost::TiaMcp.FoundationHost.UnsettledCalls;
#endif
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
    public void SetNativeCrossReferences(bool enabled, int workbenchPid) => throw new NotSupportedException("Human session settings unavailable.");
    public bool TryBeginHostShutdown() => false;
    public void CancelHostShutdown() { }
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
    private bool resourcesDisposed;
    private bool hostStopping;
    private int activeCalls;
    private JsonObject? finalWorkerState;
    private JsonObject observedWorkerState;
    private SessionNotifications? aiNotifications;
    private Action<string>? registerAi;
    private readonly CancellationToken closedToken;
    public string SessionKey { get; } = Guid.NewGuid().ToString("N");
    private FileStream? nativeConsent;
    private readonly string? nativeConsentDirectory;
    internal string NativeCrossReferenceSessionSecret { get; } = TiaMcp.Adapters.Contracts.NativeCrossReferenceConsent.CreateSecret();
    internal string NativeCrossReferenceSessionFile => Path
        .Combine(nativeConsentDirectory ?? ApprovalSettings.SettingsPath + ".sessions", "native-cross-references-" + SessionKey + ".settings");
    public string Instructions { get; }
    public bool OperatorOwned { get; }
    public CancellationToken Closed => closedToken;
    public IReadOnlyList<McpServerTool> Tools { get; }
    public IReadOnlyList<McpServerTool> OperatorTools { get; }
    public event Action? StateChanged;
    internal LateCalls LateResults { get; }
    private readonly UnsettledCalls? unsettled;

    internal HostSession(string release, string instructions, bool operatorOwned, IReadOnlyList<McpServerTool> tools,
        IReadOnlyList<McpServerTool> operatorTools, Func<JsonObject> workerState, Action dispose, string? nativeConsentDirectory = null,
        LateCalls? lateResults = null, UnsettledCalls? unsettled = null)
    {
        this.nativeConsentDirectory = nativeConsentDirectory;
        this.release = release; Instructions = instructions; OperatorOwned = operatorOwned;
        LateResults = lateResults ?? new LateCalls();
        this.unsettled = unsettled;
        closedToken = closed.Token;
        this.workerState = workerState; this.dispose = dispose;
        observedWorkerState = workerState();
        Tools = tools.Select(t => (McpServerTool)new ObservedTool(t, this)).ToArray();
        OperatorTools = operatorTools.Select(t => (McpServerTool)new OperatorTool(t, this)).ToArray();
    }
    internal static HostSession Legacy(HostOptions options, string instructions, bool operatorOwned = false)
        => SharedReleaseHost.CreateHostSession(options, operatorOwned).GetAwaiter().GetResult();
    private IDisposable? stagingRegistration;
    internal void RegisterSession(string id, Func<string, IDisposable> register)
    { mcpSessionId = id; stagingRegistration = register(id); }
    internal void RegisterSessionOnBind(Func<string, IDisposable> register)
        => registerAi = id => stagingRegistration = register(id);
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
    // Called only after the operator pipe has verified the Workbench image and peer PID.
    public void SetNativeCrossReferences(bool enabled, int workbenchPid)
    {
        using var workbench = Process.GetProcessById(workbenchPid);
        lock (sync)
        {
            if (disposed) throw new InvalidOperationException("The host session has ended.");
            Directory.CreateDirectory(Path.GetDirectoryName(NativeCrossReferenceSessionFile)!);
            nativeConsent ??= new FileStream(NativeCrossReferenceSessionFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            var bytes = System.Text.Encoding.UTF8.GetBytes(TiaMcp.Adapters.Contracts.NativeCrossReferenceConsent.Record(
                NativeCrossReferenceSessionSecret, enabled, workbenchPid, workbench.StartTime.ToUniversalTime().Ticks));
            nativeConsent.Position = 0; nativeConsent.SetLength(0); nativeConsent.Write(bytes, 0, bytes.Length); nativeConsent.Flush(true);
        }
        Changed();
    }
    public JsonObject Snapshot()
    {
        lock (sync)
        {
            var worker = finalWorkerState ?? workerState();
            bool disconnected = disposed || (bool?)worker["disconnected"] == true;
            var sessionState = worker["sessionState"]?.DeepClone();
            if (disconnected && sessionState is JsonObject ended)
            {
                ended["connected"] = false; ended["disconnected"] = true;
                if (ended["switches"]?["nativeCrossReferences"] is JsonObject policy) policy["allowed"] = false;
            }
            return new JsonObject {
                ["sessionKey"] = SessionKey, ["releaseKey"] = release, ["createdUtc"] = created.ToString("O"), ["revision"] = revision,
                ["owner"] = OperatorOwned ? "workbench" : "mcp", ["endedBy"] = endedBy, ["endReason"] = endReason,
                ["participants"] = new JsonObject { ["mcpClient"] = clientName, ["mcpClientVerified"] = false, ["workbench"] = operators },
                ["worker"] = new JsonObject { ["pid"] = worker["pid"]?.DeepClone(), ["generation"] = worker["generation"]?.DeepClone(),
                    ["state"] = hostStopping || (bool?)worker["stopping"] == true ? "stopping" : disconnected ? "disconnected"
                        : activeCalls > 0 || lane != null ? "busy" : "idle" },
                ["callInProgress"] = activeCalls > 0 || lane != null,
                ["lane"] = lane?.DeepClone(), ["running"] = LateResults.Running,
                ["binding"] = worker["binding"]?.DeepClone(),
                ["sessionState"] = sessionState,
                ["nativeCrossReferencesAllowed"] = !disposed && ((bool?)sessionState?["switches"]?["nativeCrossReferences"]?["humanSessionAllowed"]
                    ?? TiaMcp.Adapters.Contracts.NativeCrossReferenceConsent.Read(NativeCrossReferenceSessionFile, NativeCrossReferenceSessionSecret)),
                ["approvalEnabled"] = (bool?)sessionState?["switches"]?["approval"]?["enabled"] ?? ApprovalSettings.Load(ApprovalSettings.SettingsPath).Enabled,
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
            : !JsonNode.DeepEquals(observedWorkerState["generation"], after["generation"]) ? "worker-restart" : null;
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
    public bool TryBeginHostShutdown()
    {
        lock (sync)
        {
            if (activeCalls > 0 || lane != null) return false;
            hostStopping = true;
            return true;
        }
    }
    public void CancelHostShutdown() { lock (sync) hostStopping = false; }

    internal async ValueTask<CallToolResult> Invoke(McpServerTool inner, RequestContext<CallToolRequestParams> request, bool workbench, CancellationToken token)
    {
        lock (sync)
        {
            if (disposed || hostStopping) throw new InvalidOperationException("The host session has ended; initialize a new session.");
            activeCalls++;
        }
        var call = LateResults.CreateInvocation(inner.ProtocolTool.Name, Guid.NewGuid().ToString("N"), token);
        var core = InvokeOwned(inner, request, workbench, call);
        _ = core.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (await Task.WhenAny(core, call.Detachment).ConfigureAwait(false) == core && !call.Detached)
            return await core.ConfigureAwait(false);
        var running = await call.Detachment.ConfigureAwait(false);
        return FoundationV4Result.InProgress(release, inner.ProtocolTool.Name, running);
    }
    private async Task<CallToolResult> InvokeOwned(McpServerTool inner, RequestContext<CallToolRequestParams> request,
        bool workbench, LateCalls.Invocation call)
    {
        using (call)
        using (call.Enter())
        {
            CallToolResult? result = null;
            try
            {
                result = await InvokeCore(inner, request, workbench, call.Token).ConfigureAwait(false);
                call.Complete(result, unsettled);
                return result;
            }
            catch
            {
                if (call.Detached) call.Complete(FoundationV4Result.LateFailure(release, inner.ProtocolTool.Name,
                    (string?)LateResults.Running?["requestId"] ?? Guid.NewGuid().ToString("N")), unsettled);
                throw;
            }
            finally
            {
                lock (sync) activeCalls--;
                Changed();
                FinishDisposal();
            }
        }
    }
    private async ValueTask<CallToolResult> InvokeCore(McpServerTool inner, RequestContext<CallToolRequestParams> request,
        bool workbench, CancellationToken token)
    {
        if (!workbench) BindAi(request.Server);
        string id = Guid.NewGuid().ToString("N"), actor = workbench ? ActorScope.Workbench : ActorScope.Mcp;
        using var crossReferenceSession = TiaMcp.Adapters.Contracts.NativeCrossReferenceConsent
            .EnterSession(NativeCrossReferenceSessionFile, NativeCrossReferenceSessionSecret);
        using var attribution = workbench ? ActorScope.EnterWorkbench(mcpSessionId ?? SessionKey, id) : ActorScope
            .EnterCall(mcpSessionId ?? SessionKey, request.Server);
        using var observation = HostSessionCall.Enter(LaneChanged, Ended, inner.ProtocolTool.Name, actor, id);
        var elapsed = Stopwatch.StartNew();
        CallToolResult? result = null;
        try { return result = await inner.InvokeAsync(request, token).ConfigureAwait(false); }
        finally
        {
            lock (sync)
            {
                calls.Enqueue(new JsonObject { ["requestId"] = (string?)result?.StructuredContent?["meta"]?["requestId"] ?? id,
                    ["tool"] = inner.ProtocolTool.Name, ["actor"] = actor, ["outcome"] = (string?)result?.StructuredContent?["meta"]?["outcome"] ?? "unknown",
                        ["ms"] = elapsed.ElapsedMilliseconds });
                while (calls.Count > 20) calls.Dequeue();
            }
        }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            if (endReason != "disconnect") { endedBy = OperatorOwned ? ActorScope.Workbench : ActorScope.Mcp; endReason = "session-ended"; }
        }
        Changed(); closed.Cancel();
        FinishDisposal();
    }
    private void FinishDisposal()
    {
        lock (sync)
        {
            if (!disposed || resourcesDisposed || activeCalls > 0 || lane != null) return;
            resourcesDisposed = true;
            finalWorkerState = workerState();
        }
        if (aiNotifications != null) aiNotifications.DisposeAsync().AsTask().GetAwaiter().GetResult();
        stagingRegistration?.Dispose(); dispose();
        LateResults.Clear();
        nativeConsent?.Dispose(); nativeConsent = null;
        try { File.Delete(NativeCrossReferenceSessionFile); }
        catch (IOException error) { OperatorEndpoint.Report(error); }
        catch (UnauthorizedAccessException error) { OperatorEndpoint.Report(error); }
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

