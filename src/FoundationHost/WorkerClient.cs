#if TIA_FOUNDATION_TEST_HOST
extern alias enginehost;
using McpServer = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;
using ImportStagingTools = enginehost::TiaMcpServer.ModelContextProtocol.ImportStagingTools;
using WorkerSupervisorReplies = enginehost::TiaMcp.FoundationHost.WorkerSupervisorReplies;
using WorkerSupervisorState = enginehost::TiaMcp.FoundationHost.WorkerSupervisorState;
using ReleaseCatalogData = enginehost::TiaMcp.FoundationHost.ReleaseCatalogData;
using UnsettledCalls = enginehost::TiaMcp.FoundationHost.UnsettledCalls;
using LateCalls = enginehost::TiaMcp.FoundationHost.LateCalls;
using ResponseGuardTool = enginehost::TiaMcpServer.ModelContextProtocol.ResponseGuardTool;
#else
using McpServer = TiaMcpServer.ModelContextProtocol.McpServer;
using ImportStagingTools = TiaMcpServer.ModelContextProtocol.ImportStagingTools;
using ResponseGuardTool = TiaMcpServer.ModelContextProtocol.ResponseGuardTool;
#endif
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.WorkerChannel;
using TiaMcp.PlcWorker;
using TiaMcpServer.Siemens;

namespace TiaMcp.FoundationHost;

internal interface IFoundationWorker : IDisposable
{
    public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken cancellationToken);
}
internal interface IFoundationWorkerSupervisor
{
    public JsonObject OperatorSnapshot();
}
internal interface IFoundationWorkerResult
{
    public ModelContextProtocol.Protocol.CallToolResult ParkResult(ModelContextProtocol.Protocol.CallToolResult result, string name);
}
internal interface IFoundationWorkerReadiness
{
    public Task PrepareReadiness(CancellationToken token);
}

internal interface IFoundationSessionWorker : IFoundationWorker
{
    public string ApprovalIdentity { get; }
    public bool Faulted => false;
    public UnsettledCalls Unsettled => UnsettledCalls.For(this);
    public LateCalls LateResults => LateCalls.For(this);
    public JsonObject? Running => null;
    public bool Bundled { get; }
    public bool SharedSession { get; }
    public TiaMcp.Logic.ModelContextProtocol.ImportStagingSession? StagingOwner { get; }
    public IDisposable? EnterRequest(ModelContextProtocol.Server.RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams> request);
    public Task<IDisposable?> AcquireLane(CancellationToken token);
    public void ActivateLane(IDisposable? lane);
}

internal static class FoundationWorkerRequest
{
    private static readonly AsyncLocal<(string Id, string Name)?> Current = new();
    internal static string CorrelationId => Current.Value?.Id ?? TiaOpenness.Shared.AuditInvocation.CurrentRequestId
        ?? TiaMcpServer.ModelContextProtocol.InvocationJournal.CurrentCallId ?? TiaMcpServer.ModelContextProtocol.InvocationJournal.CorrelationId;
    internal static string? ToolName => Current.Value?.Name ?? TiaMcpServer.ModelContextProtocol.InvocationJournal.CurrentTool;
    internal static IDisposable Use(string id, string name)
    {
        var previous = Current.Value; Current.Value = (id, name);
        return new Scope(() => Current.Value = previous);
    }
    private sealed class Scope(Action restore) : IDisposable { public void Dispose() => restore(); }
    internal static async Task<string> Dispatch(ChannelClient channel, string method, string args, BindingChange change,
        bool readOnly, TimeSpan timeout, bool firstAttach, Action<JsonObject> onRunning, CancellationToken token)
    {
        string id = CorrelationId;
        string? name = Current.Value?.Name ?? TiaMcpServer.ModelContextProtocol.InvocationJournal.CurrentTool;
        string phase = "THREW";
        var since = DateTimeOffset.UtcNow;
        if (name != null) TiaMcpServer.ModelContextProtocol.InvocationJournal.Write(id, "worker:" + name, "BEFORE");
        try
        {
            string reply;
            try { reply = await channel.CallToolAsync(method, args, change, readOnly, timeout, firstAttach, id, name, true, token); }
            catch (ChannelCallRunningException running)
            {
                onRunning(new JsonObject { ["requestId"] = id, ["tool"] = name ?? method,
                    ["sinceUtc"] = since.ToString("O"), ["message"] = running.Message });
                reply = await running.Reply;
            }
            phase = "RETURNED";
            return reply;
        }
        finally { if (name != null) TiaMcpServer.ModelContextProtocol.InvocationJournal.Write(id, "worker:" + name, phase); }
    }
}

internal sealed class WorkerClient(string releaseKey, string workerExe, string apiDirectory, bool nativeEnabled, int defaultTimeoutSeconds = 120, string
        ? timeoutConfig = null)
    : IFoundationSessionWorker, IFoundationWorkerResult, IFoundationWorkerSupervisor, IFoundationWorkerReadiness
{
    internal string? NativeCrossReferenceSessionFile { get; set; }
    internal string? NativeCrossReferenceSessionSecret { get; set; }
    public async Task PrepareReadiness(CancellationToken token)
    {
        // The engine read its worker status for V20/V21 and stayed usable when TIA was missing. A bundled PlcWorker cannot start
        // without the release's Openness assemblies; then the host probe alone answers readiness (P8-04j2).
        if (SharedSession && ReleaseCatalogData.Releases[releaseKey].MergeWorkerReadiness && sharedStatus == null
            && !Disconnected && !WorkerFaulted && CanStartForStatus())
            await Observe("status", new JsonObject(), token);
    }
    internal bool SharedSession { get; init; }
    internal Func<TiaMcp.Logic.ModelContextProtocol.ImportStagingSession>? StagingProvider { get; set; }
    internal bool WorkerFaulted => Faulted;
    internal Func<JsonObject>? SupervisorReadiness { get; set; }
    internal bool WithUi { get; init; }
    internal string? TiaPortalLocation { get; init; }
    // notReady answers when neither the worker nor an SDK fixture reports a ready environment (V20/V21: the engine's probe, P8-04j2).
    internal JsonObject Readiness(Func<JsonObject> production, Func<JsonObject>? notReady = null)
    {
        if (ReleaseCatalogData.Releases[releaseKey].MergeWorkerReadiness
            && ((bool?)sharedStatus?["readiness"]?["ready"] == true || WorkerSdkFixture.Ready(workerExe, releaseKey)))
        {
            var merged = production(); merged["ready"] = true; merged["cause"] = null; return merged;
        }
        return WorkerSdkFixture.Readiness(workerExe, releaseKey, apiDirectory, notReady ?? production);
    }
    private JsonObject SupervisorEnvironment() => SupervisorReadiness?.Invoke()
        ?? Readiness(() => FoundationPassiveDiagnostics.Readiness(releaseKey, apiDirectory, null));
    public ModelContextProtocol.Protocol.CallToolResult ParkResult(ModelContextProtocol.Protocol.CallToolResult result, string name)
        => System.Text.Encoding.UTF8
            .GetByteCount(JsonSerializer.Serialize(result, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions)) >= ChannelLimits.ResponseBytes / 2
            ? ResponseGuardTool.Shrink(result, name, "worker", force: true) : result;
    private readonly WorkerTimeoutPolicy timeouts = new(defaultTimeoutSeconds, timeoutConfig,
        Environment.GetEnvironmentVariable("TIA_MCP_WORKER_TIMEOUTS"));
    // The process data location is cached at its first use, so
    // parallel test processes could share it. Production always uses the data location.
    internal string? SpillsDirectoryForTest { get; init; }
    internal bool Disconnected => disconnecting;
    internal bool CallRunning => channel?.Poisoned != true && process is { HasExited: false }
        && (Running != null || channel?.CallRunning == true);
    private bool UnansweredRequest => process is { HasExited: false }
        && channel?.Poisoned != true && !unansweredHostProbe
        && (channel?.CallRunning == true || channel?.InFlight == true || channel?.Unanswered == true);
    private string SpillsDirectory => SpillsDirectoryForTest ?? TiaOpenness.Shared.DataLocations.Current.WorkerSpillsDirectory;
    private readonly SemaphoreSlim serial = new(1, 1);
    private static readonly AsyncLocal<WorkerClient?> Held = new();
    internal async Task<IDisposable?> AcquireLane(CancellationToken token)
    {
        if (ReferenceEquals(Held.Value, this)) return null;
        Interlocked.Increment(ref queued);
        try { await WaitForLane(token); }
        finally { Interlocked.Decrement(ref queued); }
        Interlocked.Increment(ref active);
        HostSessionCall.LaneChanged(true);
        return new Lane(this);
    }
    internal static void ActivateLane(IDisposable? lane) { if (lane is Lane held) held.Activate(); }
    private sealed class Lane(WorkerClient owner, bool counted = true) : IDisposable
    {
        private WorkerClient? previous;
        private bool disposed;
        internal void Activate() { previous = Held.Value; Held.Value = owner; }
        public void Dispose() {
            if (!disposed) {
                disposed = true; Held.Value = previous; if (counted) Interlocked.Decrement(ref owner.active);
                owner.ClearRunning();
                HostSessionCall.LaneChanged(false);
                owner.serial.Release();
                owner.FinishDeferredDisposal();
            }
        }
    }
    private readonly object stateSync = new();
    private JsonObject? runningCall, lastRunningCall, activeCall;
    private CancellationTokenSource busyWaiters = new();
    private bool disposeRequested;
    private volatile bool unansweredHostProbe;
    public LateCalls LateResults { get; } = new();
    internal JsonObject? Running
    {
        get { lock (stateSync) return channel?.Poisoned == true || process?.HasExited == true ? null : runningCall?.DeepClone().AsObject(); }
    }
    private TiaMcp.Adapters.Contracts.AdapterPreconditionException Busy()
    {
        var running = Running ?? activeCall ?? lastRunningCall;
        var error = new TiaMcp.Adapters.Contracts.AdapterPreconditionException(
            $"Worker call {running?["tool"]} (request {running?["requestId"]}, started {running?["sinceUtc"]}) is still running. "
                + "Read GetCallResult(requestId) before repeating it.", "worker-call-running", null);
        error.Data["foundationRequestSent"] = false;
        return error;
    }
    private async Task WaitForLane(CancellationToken token)
    {
        CancellationToken busy;
        lock (stateSync)
        {
            if (CallRunning) throw Busy();
            busy = busyWaiters.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, busy);
        try { await serial.WaitAsync(linked.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && busy.IsCancellationRequested) { throw Busy(); }
        if (busy.IsCancellationRequested) { serial.Release(); throw Busy(); }
    }
    private void SetRunning(JsonObject running)
    {
        lock (stateSync)
        {
            runningCall ??= (JsonObject)running.DeepClone();
            lastRunningCall = runningCall;
            busyWaiters.Cancel();
        }
        HostSessionCall.Detach(running);
    }
    private void ClearRunning()
    {
        lock (stateSync)
        {
            if (runningCall == null) return;
            runningCall = null;
            busyWaiters.Dispose();
            busyWaiters = new CancellationTokenSource();
        }
    }
    private void FinishDeferredDisposal()
    {
        if (disposeRequested) Dispose();
    }
    private Process? process;
    private ChannelClient? channel;
    private bool attachAttempted;
    private int? attachedProcessId;
    private JsonObject? disconnectAcknowledgement;
    private bool disconnectCompleted;
    private int unsettledLimitReported;
    private volatile bool disconnecting;
    private string? activeRequestId;
    private readonly Queue<string> diagnostics = new();
    private JsonObject approvalIdentity = new();
    internal string ApprovalIdentity => SharedSession ? sharedStatus?["binding"]?.ToJsonString() ?? "{}" : approvalIdentity.ToJsonString();
    internal bool Faulted => channel?.Poisoned == true || process?.HasExited == true || faultEvidence != null;
    public UnsettledCalls Unsettled { get; } = new();
    internal IDisposable? HostPipeline { get; set; }
    internal object HostSessionKey { get; private set; } = new();
    private string? hostProcessStartUtc;
    private JsonObject? hostState;
    private JsonObject? sharedStatus;
    private long generation = 1;
    private int queued, active;
    private readonly JsonArray previousGenerations = new();
    private JsonObject? faultEvidence;
    private bool stopping;
    private readonly object shutdownSync = new();
    internal JsonObject SupervisorSnapshot(JsonObject ready)
    {
        lock (stateSync)
        {
            bool faulted = Faulted;
            if (faulted && faultEvidence == null && activeRequestId == null) faultEvidence = WorkerSupervisorReplies
                .Fault(channel?.LastFault ?? new IOException("Worker exited."),
                "idle", false, channel?.BindingEpoch, channel?.LastRequestId, process?.Id);
            JsonNode? identity = SharedSession ? sharedStatus?["binding"] is { } binding
                ? new JsonObject { ["identity"] = binding.DeepClone() } : null : HostSnapshot()["binding"];
            var environment = ready["environment"];
            var legacy = HostSnapshot();
            JsonNode? observedReadiness = ready;
            if (ReleaseCatalogData.Releases[releaseKey].ExtendedWorkerStatus)
                observedReadiness = sharedStatus?["readiness"]?.DeepClone() ?? new JsonObject {
                    ["ready"] = ready["ready"]?.DeepClone(), ["cause"] = ready["cause"]?.DeepClone(),
                    ["groupOk"] = environment?["opennessGroupOk"]?.DeepClone(), ["recommendedFix"] = ready["recommendedFix"]?.DeepClone(),
                    ["recommendedFixZh"] = ready["recommendedFixZh"]?.DeepClone() };
            var snapshot = WorkerSupervisorReplies.Snapshot(releaseKey, new WorkerSupervisorState(legacy, faulted, process != null,
                generation, Volatile.Read(ref active), Volatile.Read(ref queued), timeouts.For("status").TotalSeconds,
                JsonSerializer.SerializeToNode(timeouts.Budgets.ToDictionary(p => p.Key, p => p.Value.TotalSeconds)),
                faultEvidence, observedReadiness, SharedSession ? identity : attachedProcessId.HasValue ? identity : null, previousGenerations));
            if (stopping) snapshot["state"] = "Stopping";
            else if ((CallRunning || LateResults.Running != null) && !faulted) snapshot["state"] = "Busy";
            snapshot["running"] = LateResults.Running ?? Running;
            return snapshot;
        }
    }
    JsonObject IFoundationWorkerSupervisor.OperatorSnapshot() => OperatorSnapshot();
    internal JsonObject OperatorSnapshot()
    {
        lock (stateSync) return new JsonObject { ["pid"] = process?.Id, ["generation"] = generation,
            ["disconnected"] = disconnecting, ["stopping"] = stopping,
            ["connected"] = SharedSession ? sharedStatus?["session"]?["isConnected"]?.DeepClone()
                : JsonValue.Create((bool?)hostState?["IsAttached"] ?? attachedProcessId.HasValue),
            ["workerFault"] = faultEvidence?.DeepClone(),
            ["running"] = LateResults.Running ?? Running,
            ["binding"] = new JsonObject { ["processId"] = SharedSession ? JsonValue.Create(attachedProcessId) : approvalIdentity["ProcessId"]?.DeepClone(),
                ["projectFile"] = SharedSession ? (sharedStatus?["binding"]?["identity"]?["projectPath"] ?? sharedStatus?["binding"]?["projectPath"])?
                    .DeepClone()
                    : (approvalIdentity["ProjectFile"] ?? approvalIdentity["ProjectPath"])?.DeepClone(),
                ["bindingEpoch"] = SharedSession ? JsonValue.Create(channel?.BindingEpoch) : approvalIdentity["BindingEpoch"]?.DeepClone() } };
    }
    private static string? ProcessStartUtc(int processId)
    {
        try
        {
            using var observed = Process.GetProcessById(processId);
            var start = observed.StartTime.ToUniversalTime();
            return !observed.HasExited && observed.StartTime.ToUniversalTime() == start
                ? start.ToString("O", System.Globalization.CultureInfo.InvariantCulture) : null;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Security
                .SecurityException)
        { /* swallow(env-probe): unavailable OS identity must keep the batch binding incomplete. */ return null; }
    }
    private void UpdateApprovalIdentity(JsonObject values)
    {
        var identity = (JsonObject)approvalIdentity.DeepClone();
        foreach (var key in new[] { "ProjectFile", "ProjectPath", "ProcessId", "ProcessStartUtc", "BindingEpoch", "SessionId", "Identity" })
            if (values.TryGetPropertyValue(key, out var value)) identity[key] = value?.DeepClone();
        identity["BindingEpoch"] = channel!.BindingEpoch;
        approvalIdentity = identity;
    }

    private void ClearHostBinding()
    { hostProcessStartUtc = null; hostState = null; sharedStatus = null; if (!SharedSession) HostSessionKey = new object(); }
    internal JsonObject? SharedStatus => sharedStatus == null ? null : (JsonObject)sharedStatus.DeepClone();
    internal JsonObject HostSnapshot()
    {
        var identity = (JsonObject)(hostState ?? approvalIdentity).DeepClone();
        string? file = (string?)identity["ProjectFile"] ?? (string?)identity["ProjectPath"];
        string? project = file == null ? null : Path.GetFileNameWithoutExtension(file);
        int? pid = (int?)identity["ProcessId"] ?? attachedProcessId;
        string? start = !Faulted && !disconnecting && pid.HasValue && hostProcessStartUtc == ProcessStartUtc(pid.Value) ? hostProcessStartUtc : null;
        return new JsonObject { ["enabled"] = true, ["state"] = process == null ? "Stopped" : Faulted ? "Faulted" : "Running",
            ["session"] = new JsonObject {
                ["isConnected"] = (bool?)hostState?["IsAttached"] ?? attachedProcessId.HasValue, ["project"] = project },
            ["binding"] = new JsonObject { ["identity"] = new JsonObject {
                ["tiaMajorVersion"] = TiaMcp.Versioning.TiaVersionCatalog.Get(releaseKey).MajorVersion, ["processId"] = pid,
                ["processStartUtc"] = start, ["projectPath"] = file, ["projectName"] = project,
                ["generation"] = approvalIdentity["BindingEpoch"]?.DeepClone() } } };
    }
    // The V20/V21 engine started for a status read without TIA; the PlcWorker needs this release's Openness assemblies to start.
    // Without them a status read reports the stopped worker and the not-ready environment instead of a fault (P8-04j2).
    private bool CanStartForStatus() => !ReleaseCatalogData.Releases[releaseKey].MergeWorkerReadiness || !Bundled
        || !string.IsNullOrWhiteSpace(apiDirectory) && Directory.Exists(apiDirectory)
            && FoundationPassiveDiagnostics.Readiness(releaseKey, apiDirectory, null)["environment"]?["tiaInstallPath"] != null;
    internal async Task RefreshHostState(bool startForStatus, CancellationToken token)
    {
        // ReadState is the worker's cached lifecycle state, not a native lookup.
        if (CallRunning) return;
        // Only an explicit supervisor read may start a cold shared worker.
        if (SharedSession)
        {
            if ((process == null && (!startForStatus || !CanStartForStatus())) || Faulted || disconnecting || !serial.Wait(0)) return;
            HostSessionCall.LaneChanged(true);
            using var lane = new Lane(this, false); lane.Activate();
            if (CallRunning) return;
            var observed = await Observe("status", new JsonObject(), token);
            if (observed is JsonObject status) sharedStatus = (JsonObject)status.DeepClone();
        }
        else if (process != null && !Faulted && !disconnecting) await Call("ReadState", new JsonObject(), token);
    }
    internal async Task<JsonObject> RestartHostWorker(bool confirmed, CancellationToken token)
    {
        bool extended = ReleaseCatalogData.Releases[releaseKey].ExtendedWorkerStatus;
        JsonObject Snapshot() => extended ? SupervisorSnapshot(SupervisorEnvironment()) : HostSnapshot();
        if (CallRunning) throw Busy();
        if (!confirmed) return WorkerSupervisorReplies.Restart(false, true, Snapshot());
        if (UnansweredRequest) return WorkerSupervisorReplies.Restart(true, false, Snapshot());
        if (Volatile.Read(ref queued) > 0 || !serial.Wait(0)) return WorkerSupervisorReplies.Restart(true, false, Snapshot());
        HostSessionCall.LaneChanged(true);
        using var lane = new Lane(this, false); lane.Activate();
        await Disconnect(token);
        if (!ResetDisconnectedWorker("restart")) return WorkerSupervisorReplies.Restart(true, false, Snapshot());
        if (extended) await Observe("status", new JsonObject(), token);
        return WorkerSupervisorReplies.Restart(true, true, Snapshot());
    }
    private bool ResetDisconnectedWorker(string reason)
    {
        if (CallRunning || UnansweredRequest) throw Busy();
        lock (shutdownSync)
        lock (stateSync)
        {
            if (WorkerShutdown.Stop(process, channel) is { } refusal)
            { RetainStopRefusal(refusal); return false; }
            previousGenerations.Add(new JsonObject { ["generation"] = generation,
                ["endedUtc"] = DateTimeOffset.UtcNow.ToString("O"), ["reason"] = reason });
            while (previousGenerations.Count > 16) previousGenerations.RemoveAt(0);
            WorkerShutdown.Unregister(this); process = null; channel = null;
            approvalIdentity = new JsonObject(); attachedProcessId = null; attachAttempted = false;
            disconnectAcknowledgement = null; disconnectCompleted = false; disconnecting = false; faultEvidence = null;
            ClearHostBinding(); HostSessionKey = new object();
            generation++;
            return true;
        }
    }
    // Only the bundled worker gets the pre-dispatch readiness gate; an explicit --worker-exe (fixture) reports its own failures.
    internal bool Bundled { get; init; } = true;
    string IFoundationSessionWorker.ApprovalIdentity => ApprovalIdentity;
    bool IFoundationSessionWorker.Faulted => Faulted;
    JsonObject? IFoundationSessionWorker.Running => Running;
    bool IFoundationSessionWorker.Bundled => Bundled;
    Task<IDisposable?> IFoundationSessionWorker.AcquireLane(CancellationToken token) => AcquireLane(token);
    void IFoundationSessionWorker.ActivateLane(IDisposable? lane) => ActivateLane(lane);
    bool IFoundationSessionWorker.SharedSession => SharedSession;
    TiaMcp.Logic.ModelContextProtocol.ImportStagingSession? IFoundationSessionWorker.StagingOwner => StagingProvider?.Invoke();
    IDisposable? IFoundationSessionWorker.EnterRequest(ModelContextProtocol.Server.RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams> request)
        => SharedSession ? ImportStagingTools.UseSession(request) : null;

    internal Task<JsonNode?> Observe(string operation, JsonObject arguments, CancellationToken token)
        => CallCore("HostObserve", new JsonObject { ["operation"] = operation, ["arguments"] = arguments.DeepClone() }, managed: true, token);
    public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        => CallCore(operation, arguments, managed: false, token);
    private string[] UnsettledRequestIds()
    {
        var calls = Unsettled.Snapshot();
        if (calls.Count > ChannelLimits.UnsettledRequestCount && Interlocked.Exchange(ref unsettledLimitReported, 1) == 0)
        {
            Console.Error.WriteLine("Worker transport limited unsettled request IDs to " + ChannelLimits.UnsettledRequestCount
                + ". The session retains all request evidence.");
        }
        return calls.Take(ChannelLimits.UnsettledRequestCount).Select(call => call.RequestId).ToArray();
    }
    private async Task<JsonNode?> CallCore(string operation, JsonObject arguments, bool managed, CancellationToken token)
    {
        if (operation == "Disconnect")
        {
            if (arguments.Count != 0) throw new ArgumentException("Disconnect takes no arguments.");
            return await Disconnect(token);
        }
        bool acquired = !ReferenceEquals(Held.Value, this);
        if (acquired)
        {
            Interlocked.Increment(ref queued);
            try { await WaitForLane(token); }
            finally { Interlocked.Decrement(ref queued); }
            Interlocked.Increment(ref active);
        }
        string? startBeforeAttach = null;
        Exception? channelFailureForStatus = null;
        int? hostAttachPid = null;
        bool sent = false;
        long requestIdAtEntry = channel?.LastRequestId ?? 0;
        bool statusProbe = managed && (string?)arguments["operation"] == "status";
        try
        {
            ChannelLimits.CheckRequest("adapter." + operation, arguments.ToJsonString(), FoundationWorkerRequest.CorrelationId,
                FoundationWorkerRequest.ToolName, UnsettledRequestIds());
            if (CallRunning && acquired) throw Busy();
            token.ThrowIfCancellationRequested();
            if (disconnectAcknowledgement != null && !managed && (operation == "Attach" || disconnectCompleted)
                && !ResetDisconnectedWorker("disconnect"))
                throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("The previous worker could not stop.", "worker-faulted", null);
            if (Faulted && !(WorkerOperations.IsIndependentRuntime(operation) && process is { HasExited: false }
                && channel?.CanCallIndependentRuntime == true))
                throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException(
                    "The worker is faulted. Call RestartOpennessWorker before another native call.",
                    "worker-faulted", null);
            if (disconnectAcknowledgement != null || disconnecting)
                throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException(TiaOpenness.Shared.SessionBehavior.PortalRequired, "session", false);
            // Idle Disconnect above must not launch a worker, even in native-disabled discovery.
            if (!nativeEnabled && !managed)
                throw new InvalidOperationException("Native calls are disabled by --offline. Start a normal configured session to use Openness.");
            if (!managed && Bundled && WorkerOperations.RequiresPortal(operation) && !attachedProcessId.HasValue)
                throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException(TiaOpenness.Shared.SessionBehavior.PortalRequired, "session", false);
            hostAttachPid = operation == "Attach" ? (int?)arguments["processId"] : null;
            if (hostAttachPid.HasValue) startBeforeAttach = ProcessStartUtc(hostAttachPid.Value);
            EnsureProcess();
            requestIdAtEntry = channel!.LastRequestId;
            if (!WorkerOperations.IsIndependentRuntime(operation) || !channel!.CanCallIndependentRuntime)
                await channel!.ConnectAsync(timeouts.For("status"),token);
            token.ThrowIfCancellationRequested();
            if (disconnecting) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException(
                TiaOpenness.Shared.SessionBehavior.PortalRequired, "session", false);
            activeRequestId = FoundationWorkerRequest.CorrelationId;
            activeCall = new JsonObject { ["requestId"] = activeRequestId, ["tool"] = FoundationWorkerRequest.ToolName ?? operation,
                ["sinceUtc"] = DateTimeOffset.UtcNow.ToString("O") };
            HostSessionCall.RequestIdChanged(activeRequestId);
            var change = BindingChangeFor(operation, arguments);
            long epochBefore = channel!.BindingEpoch;
            string response;
            try
            {
                bool attach = operation == "Attach" || operation == "project-lifecycle.ConnectIsolatedPortal";
                bool firstAttach = attach && !attachAttempted;
                token.ThrowIfCancellationRequested();
                if (attach) attachAttempted = true;
                unansweredHostProbe = operation is "HostObserve" or "ReadState";
                if (unansweredHostProbe) response = await channel
                    .CallToolAsync("adapter." + operation, arguments.ToJsonString(), BindingChange.None, true, timeouts.For("status"), false,
                        activeRequestId, FoundationWorkerRequest.ToolName, false, CancellationToken.None);
                else
                response=await FoundationWorkerRequest.Dispatch(channel, "adapter."+operation,arguments.ToJsonString(),change,
                    WorkerOperations.IsReadOnly(operation) || arguments["dryRun"] is JsonValue preview && preview.TryGetValue<bool>(out var dryRun)
                        && dryRun, timeouts.ForPlc(operation, arguments, firstAttach), firstAttach, SetRunning, CancellationToken.None);
                sent=true;
            }
            catch(ChannelFailureException failure)
            {
                sent=true;
                channelFailureForStatus = failure;
                throw WorkerOperationException.FromChannelFailure(failure);
            }
            response = WorkerReplySpill.Read(SpillsDirectory, response, out _);
            var result=JsonNode.Parse(response);
            WorkerProtocol.ValidateExchangeResult(operation, arguments, result);
            if (operation == "Disconnect") approvalIdentity = new JsonObject();
            else if (result is JsonObject values)
            {
                UpdateApprovalIdentity(values);
                if (operation == "Attach") approvalIdentity["ProcessId"] = arguments["processId"]!.GetValue<int>();
            }
            if(operation=="Attach")
            {
                if (result?["PreviousOwnerRequests"] is JsonArray previousRequests)
                    Unsettled.ImportPreviousOwner(previousRequests.Select(value => value!.GetValue<string>()));
                attachedProcessId=arguments["processId"]!.GetValue<int>();
                ClearHostBinding();
                if (!Faulted && startBeforeAttach != null && startBeforeAttach == ProcessStartUtc(attachedProcessId.Value)) hostProcessStartUtc =
                    startBeforeAttach;
            }
            if (change == BindingChange.MayAdvance && operation != "Disconnect" && channel.BindingEpoch > epochBefore)
            {
                unansweredHostProbe = true;
                string stateWire = await channel.CallAsync("adapter.ReadState", "{}", BindingChange.None, true,
                    timeouts.For("status"), CancellationToken.None);
                var state = JsonNode.Parse(stateWire)!.AsObject();
                attachedProcessId = (int?)state["ProcessId"];
                ClearHostBinding();
                UpdateApprovalIdentity(state);
                hostState = (JsonObject)state.DeepClone();
                hostProcessStartUtc = !Faulted && attachedProcessId.HasValue ? ProcessStartUtc(attachedProcessId.Value) : null;
            }
            if (operation == "ReadState" && result is JsonObject cachedState) hostState = (JsonObject)cachedState.DeepClone();
            if(operation=="Disconnect")
                disconnectAcknowledgement=(JsonObject)DisconnectContract.Validate(result,true,attachedProcessId,true).DeepClone();
            if (SharedSession && !WorkerOperations.IsIndependentRuntime(operation))
            {
                unansweredHostProbe = true;
                var statusWire = await channel
                    .CallAsync("adapter.HostObserve", "{\"operation\":\"status\",\"arguments\":{}}", BindingChange.None, true, timeouts.For("status"),
                        CancellationToken.None);
                sharedStatus = JsonNode.Parse(statusWire)!.AsObject();
            }
            if (token.IsCancellationRequested) TiaMcpServer.ModelContextProtocol.InvocationJournal
                .Write(activeRequestId!, operation, "RETURNED_AFTER_CANCELLATION");
            return result;
        }
        catch(Exception ex)
        {
            // Only a request sent during this call can supply unknown transport evidence.
            // An answered unknown from an earlier call cannot taint a later admission refusal.
            sent |= channel?.Poisoned == true && channel.OutcomeUnknown && channel.LastRequestId > requestIdAtEntry;
            if (sent && channel?.Poisoned == true && channel.OutcomeUnknown) ex.Data["workerOutcomeUnknown"] = true;
            // Same mark as the engine client: a request over the worker frame was never sent.
            if (!sent && ex is ChannelLimitException) ex.Data["workerLimitBytes"] = ChannelLimits.RequestBytes;
            if (TiaMcp.Adapters.Contracts.Candidates.SessionPrimitives.IsTimeout(ex) && operation == "project-lifecycle.ConnectIsolatedPortal")
                ex.Data["sessionReason"] = TiaMcp.Adapters.Contracts.Candidates.SessionPrimitives.ConfirmationReason;
            if (TiaMcp.Adapters.Contracts.Candidates.SessionPrimitives.IsTimeout(ex) && operation == "Attach")
            {
                int? selectedPid = (int?)arguments["processId"];
                bool matching = false;
                try
                {
                    using var selected = Process.GetProcessById(selectedPid ?? 0);
                    matching = !selected.HasExited;
                }
                catch (Exception) /* swallow(env-probe): timeout guidance does not claim a matching process after exit or access denial */ { }
                string? reason = TiaMcp.Adapters.Contracts.Candidates.SessionPrimitives.TimeoutReason(true, matching);
                if (reason != null) ex.Data["sessionReason"] = reason;
            }
            ex.Data["foundationRequestSent"] = sent;
            if(channel?.Poisoned == true || process?.HasExited == true)
            {
                faultEvidence ??= WorkerSupervisorReplies.Fault(channelFailureForStatus ?? ex, statusProbe ? "status" : operation, !managed && sent,
                    channel?.BindingEpoch, channel?.LastRequestId, process?.Id);
            }
            throw;
        }
        finally
        {
            if (UnansweredRequest && activeCall != null) lastRunningCall = activeCall;
            activeRequestId = null;
            activeCall = null;
            if (acquired)
            {
                ClearRunning();
                Interlocked.Decrement(ref active);
                HostSessionCall.LaneChanged(false);
                serial.Release();
                FinishDeferredDisposal();
            }
        }
    }

    // Synchronous on purpose: with CallCore's exception filter in the same async frame, a refusal
    // thrown inside this lock reached Monitor.Exit(null) in Release builds, so the caller saw an
    // ArgumentNullException and stateSync stayed held. A separate frame keeps the lock balanced.
    private void EnsureProcess()
    {
        lock (stateSync)
        {
            if (disconnecting) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException(
                TiaOpenness.Shared.SessionBehavior.PortalRequired, "session", false);
            if (process == null)
            {
                if (!File.Exists(workerExe) || !Directory.Exists(apiDirectory))
                    throw new FileNotFoundException("Select the compiled worker and authorized PublicAPI directory explicitly.");
                var adapterFile=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(workerExe))!,"TiaMcp.Adapter."+releaseKey+".dll");
                var workerHash=ArgumentRules.HashFile(workerExe,FileShare.Read);
                var adapterHash=ArgumentRules.HashFile(adapterFile,FileShare.Read);
                var nonce=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                var start = new ProcessStartInfo(Path.GetFullPath(workerExe)) {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = new System.Text.UTF8Encoding(false),
                        StandardErrorEncoding = new System.Text.UTF8Encoding(false)
                };
                foreach (var argument in new WorkerLaunchOptions(releaseKey, apiDirectory, nonce, WithUi, TiaPortalLocation).Arguments())
                    start.ArgumentList.Add(argument);
                start.Environment["TIA_MCP_WORKER_SPILLS_DIRECTORY"] = SpillsDirectory;
                if (NativeCrossReferenceSessionFile != null) start.Environment[TiaMcp.Adapters.Contracts.NativeCrossReferenceConsent.SessionFileVariable] =
                    NativeCrossReferenceSessionFile;
                if (NativeCrossReferenceSessionSecret != null) start.Environment[TiaMcp.Adapters.Contracts.NativeCrossReferenceConsent.SessionSecretVariable] =
                    NativeCrossReferenceSessionSecret;
                start.Environment["TIA_MCP_DIAGNOSTICS_DIRECTORY"] = TiaOpenness.Shared.DataLocations.Current.DiagnosticsDirectory;
                process = Process.Start(start) ?? throw new IOException("Worker failed to start."); WorkerJob.Bind(process); WorkerShutdown.Register(this);
                attachAttempted = false;
                process.ErrorDataReceived += (_, e) => {
                    if (e.Data != null) lock (diagnostics) { diagnostics.Enqueue(e.Data); while (diagnostics.Count > 8) diagnostics.Dequeue(); } };
                process.BeginErrorReadLine();
                channel=new ChannelClient(process.StandardOutput.BaseStream,process.StandardInput.BaseStream,
                    new ChannelIdentity(releaseKey,workerHash,adapterHash,process.Id,nonce)) {
                    UnsettledRequests = UnsettledRequestIds };
            }
        }
    }

    private async Task<JsonNode?> Disconnect(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (CallRunning || UnansweredRequest) throw Busy();
        if (disconnectAcknowledgement != null) return disconnectAcknowledgement.DeepClone();
        disconnecting = true;
        bool acquired = !ReferenceEquals(Held.Value, this);
        bool idle = !acquired || serial.Wait(0);
        if (!idle) { disconnecting = false; throw Busy(); }
        try
        {
            if (process != null && (process.HasExited || channel?.CanDisconnect != true))
            { HostSessionCall.Ended("disconnect"); return TerminateForDisconnect(); }
            if (process == null) disconnectAcknowledgement = DisconnectContract.Idle();
            else
            {
                try
                {
                    unansweredHostProbe = false;
                    var wire = await FoundationWorkerRequest
                        .Dispatch(channel!, "adapter.Disconnect", "{}", BindingChange.MayAdvance, false, TimeSpan.FromMinutes(2), false, SetRunning,
                                CancellationToken
                            .None);
                    wire = WorkerReplySpill.Read(SpillsDirectory, wire, out _);
                    disconnectAcknowledgement = (JsonObject)DisconnectContract
                        .Validate(JsonNode.Parse(wire), true, attachedProcessId, attachedProcessId.HasValue).DeepClone();
                }
                catch (ChannelFailureException refused) when (DisconnectContract.Refused(refused))
                { disconnecting = false; throw DisconnectContract.Refusal(refused); }
                catch (Exception error) when (CallRunning)
                {
                    if (error is ChannelFailureException failure) throw WorkerOperationException.FromChannelFailure(failure);
                    throw;
                }
                catch (Exception) /* swallow(teardown): an unacknowledged detach retains the unknown outcome */
                { HostSessionCall.Ended("disconnect"); return TerminateForDisconnect(); }
            }
            disconnectCompleted = true;
            HostSessionCall.Ended("disconnect");
            RecordDisconnect();
            approvalIdentity = new JsonObject(); attachedProcessId = null; ClearHostBinding();
            return disconnectAcknowledgement.DeepClone();
        }
        finally { if (idle && acquired) { ClearRunning(); serial.Release(); FinishDeferredDisposal(); } }
    }

    private JsonNode TerminateForDisconnect()
    {
        lock (shutdownSync) return TerminateForDisconnectCore();
    }
    private JsonNode TerminateForDisconnectCore()
    {
        if (CallRunning || UnansweredRequest) throw Busy();
        Process? stoppingProcess;
        ChannelClient? stoppingChannel;
        lock (stateSync)
        {
            stopping = true;
            stoppingProcess = process; stoppingChannel = channel;
        }
        // Closing stdin can spend the full detach grace period. Managed snapshots
        // must remain available while the worker is on its owner-thread shutdown path.
        var refusal = WorkerShutdown.Stop(stoppingProcess, stoppingChannel, disposeProcess: false);
        lock (stateSync)
        {
            stopping = false;
            if (refusal != null) { RetainStopRefusal(refusal); throw refusal; }
            if (stoppingProcess != null && ReferenceEquals(process, stoppingProcess))
            {
                if (ReleaseCatalogData.Releases[releaseKey].ExtendedWorkerStatus) { process.Dispose(); process = null; channel = null; }
            }
            disconnectAcknowledgement = DisconnectContract.Idle(); disconnectCompleted = false;
            approvalIdentity = new JsonObject(); attachedProcessId = null; ClearHostBinding();
        }
        RecordDisconnect();
        return disconnectAcknowledgement.DeepClone();
    }

    private void RecordDisconnect()
        => TiaMcpServer.ModelContextProtocol.InvocationJournal.Write(FoundationWorkerRequest.CorrelationId,
            "DisconnectPortal", "RECOVERY_DISCONNECT", details: new JsonObject { ["unsettled"] = Unsettled.ToJson(),
                ["workerAcknowledged"] = disconnectAcknowledgement?["WorkerAcknowledged"]?.DeepClone() });

    public void Dispose()
    {
        bool acquired = false;
        lock (stateSync)
        {
            if (!WorkerShutdown.StoppingAll && (CallRunning || UnansweredRequest || !(acquired = serial.Wait(0))))
            { disposeRequested = true; return; }
        }
        try
        {
            disposeRequested = false;
            Unsettled.Clear();
            LateResults.Clear();
            var pipeline = HostPipeline;
            HostPipeline = null;
            pipeline?.Dispose();
            disconnecting = true;
            lock (shutdownSync)
            {
                Process? stoppingProcess;
                ChannelClient? stoppingChannel;
                lock (stateSync) { stopping = true; stoppingProcess = process; stoppingChannel = channel; }
                var refusal = WorkerShutdown.Stop(stoppingProcess, stoppingChannel, disposeProcess: false);
                lock (stateSync)
                {
                    stopping = false;
                    if (refusal != null) { RetainStopRefusal(refusal); return; }
                    WorkerShutdown.Unregister(this); channel = null; process = null;
                    stoppingProcess?.Dispose();
                }
            }
        }
        finally { if (acquired) serial.Release(); }
        // An interrupted async caller still releases its lane in finally.
    }

    private void RetainStopRefusal(WorkerTerminationException refusal)
    {
        faultEvidence ??= WorkerSupervisorReplies.Fault(refusal, "stop", false, channel?.BindingEpoch, channel?.LastRequestId, process?.Id);
    }

    internal static BindingChange BindingChangeFor(string operation, JsonObject arguments)
    {
        var dryRun = arguments["dryRun"]?.GetValue<bool>() ?? true;
        var request = arguments["request"] as JsonObject;
        bool rebinds = operation switch
        {
            "project-lifecycle.ArchiveSaveProjectCopy" => !dryRun,
            "project-lifecycle.ConnectIsolatedPortal" => true,
            "project-lifecycle.RetrieveProjectArchive" => (bool?)request?["DryRun"] == false,
            "project-lifecycle.ExecuteMultiuserSession" => (string?)request?["Action"] == "commit"
                && (bool?)request?["DryRun"] == false,
            "project-lifecycle.ExecuteProjectScaffoldPrimitive" => (string?)request?["Kind"] is "connect" or "createProject",
            _ => false
        };
        if (rebinds) return BindingChange.MayAdvance;
        return operation is "Attach" or "BindProject" || (!dryRun && operation is "OpenProject" or "CreateProject" or "CloseProject")
            ? BindingChange.Advance : operation == "Disconnect" ? BindingChange.MayAdvance : BindingChange.None;
    }
}
