#if TIA_FOUNDATION_TEST_HOST
extern alias enginehost;
using McpServer = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;
using ImportStagingTools = enginehost::TiaMcpServer.ModelContextProtocol.ImportStagingTools;
using WorkerSupervisorReplies = enginehost::TiaMcp.FoundationHost.WorkerSupervisorReplies;
using WorkerSupervisorState = enginehost::TiaMcp.FoundationHost.WorkerSupervisorState;
using ReleaseCatalogData = enginehost::TiaMcp.FoundationHost.ReleaseCatalogData;
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
    public bool Poisoned { get; }
    public bool Bundled { get; }
    public bool SharedSession { get; }
    public TiaMcp.Logic.ModelContextProtocol.ImportStagingSession? StagingOwner { get; }
    public IDisposable? EnterRequest(ModelContextProtocol.Server.RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams> request);
    public Task<IDisposable?> AcquireLane(CancellationToken token);
    public void ActivateLane(IDisposable? lane);
    public void MarkUncertain();
}

internal static class FoundationWorkerRequest
{
    private static readonly AsyncLocal<(string Id, string Name)?> Current = new();
    internal static string CorrelationId => Current.Value?.Id ?? TiaOpenness.Shared.AuditInvocation.CurrentRequestId
        ?? TiaMcpServer.ModelContextProtocol.InvocationJournal.CurrentCallId ?? TiaMcpServer.ModelContextProtocol.InvocationJournal.CorrelationId;
    internal static IDisposable Use(string id, string name)
    {
        var previous = Current.Value; Current.Value = (id, name);
        return new Scope(() => Current.Value = previous);
    }
    private sealed class Scope(Action restore) : IDisposable { public void Dispose() => restore(); }
    internal static async Task<string> Dispatch(ChannelClient channel, string method, string args, BindingChange change,
        bool readOnly, TimeSpan timeout, bool firstAttach, CancellationToken token)
    {
        string id = CorrelationId;
        string? name = Current.Value?.Name ?? TiaMcpServer.ModelContextProtocol.InvocationJournal.CurrentTool;
        string phase = "THREW";
        if (name != null) TiaMcpServer.ModelContextProtocol.InvocationJournal.Write(id, "worker:" + name, "BEFORE");
        try
        {
            string reply = await channel.CallToolAsync(method, args, change, readOnly, timeout, firstAttach, id, name, token);
            phase = "RETURNED";
            return reply;
        }
        finally { if (name != null) TiaMcpServer.ModelContextProtocol.InvocationJournal.Write(id, "worker:" + name, phase); }
    }
}

internal sealed class WorkerClient(string releaseKey, string workerExe, string apiDirectory, bool nativeEnabled, int defaultTimeoutSeconds = 120, string? timeoutConfig = null) : IFoundationSessionWorker, IFoundationWorkerResult, IFoundationWorkerSupervisor, IFoundationWorkerReadiness
{
    public async Task PrepareReadiness(CancellationToken token)
    {
        if (SharedSession && ReleaseCatalogData.Releases[releaseKey].MergeWorkerReadiness && sharedStatus == null && !WorkerFaulted)
            await Observe("status", new JsonObject(), token);
    }
    internal static TiaMcp.Adapters.Contracts.AdapterPreconditionException SessionEnded(string release, string message)
    {
        var error = new TiaMcp.Adapters.Contracts.AdapterPreconditionException(message, isArgument: false);
        error.Data["foundationSuppressAdmissionDiagnostic"] = !ReleaseCatalogData.Releases[release].RevealSessionEndedDiagnostic;
        error.Data["foundationSessionEnded"] = true;
        error.Data["foundationRequestSent"] = false;
        return error;
    }
    internal bool SharedSession { get; init; }
    internal Func<bool>? SessionLocked { get; set; }
    internal Func<TiaMcp.Logic.ModelContextProtocol.ImportStagingSession>? StagingProvider { get; set; }
    internal bool WorkerFaulted => outcome.Poisoned || ReleaseCatalogData.Releases[releaseKey].ExtendedWorkerStatus
        && (faultEvidence != null || channel?.Poisoned == true || process?.HasExited == true);
    internal Func<JsonObject>? SupervisorReadiness { get; set; }
    internal bool WithUi { get; init; }
    internal string? TiaPortalLocation { get; init; }
    internal JsonObject Readiness(Func<JsonObject> production)
    {
        if (ReleaseCatalogData.Releases[releaseKey].MergeWorkerReadiness
            && ((bool?)sharedStatus?["readiness"]?["ready"] == true || WorkerSdkFixture.Ready(workerExe, releaseKey)))
        {
            var merged = production(); merged["ready"] = true; merged["cause"] = null; return merged;
        }
        return WorkerSdkFixture.Readiness(workerExe, releaseKey, apiDirectory, production);
    }
    private JsonObject SupervisorEnvironment() => SupervisorReadiness?.Invoke()
        ?? Readiness(() => FoundationPassiveDiagnostics.Readiness(releaseKey, apiDirectory, null));
    public ModelContextProtocol.Protocol.CallToolResult ParkResult(ModelContextProtocol.Protocol.CallToolResult result, string name)
        => System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions)) >= ChannelLimits.ResponseBytes / 2
            ? ResponseGuardTool.Shrink(result, name, "worker", force: true) : result;
    private readonly WorkerTimeoutPolicy timeouts = new(defaultTimeoutSeconds, timeoutConfig,
        Environment.GetEnvironmentVariable("TIA_MCP_WORKER_TIMEOUTS"));
    // Same test seam as EngineWorkerClient's spills directory: the process data location is cached at its first use, so
    // parallel test processes could share it. Production always uses the data location.
    internal string? SpillsDirectoryForTest { get; init; }
    private string SpillsDirectory => SpillsDirectoryForTest ?? TiaOpenness.Shared.DataLocations.Current.WorkerSpillsDirectory;
    private readonly SemaphoreSlim serial = new(1, 1);
    private static readonly AsyncLocal<WorkerClient?> Held = new();
    internal async Task<IDisposable?> AcquireLane(CancellationToken token)
    {
        if (ReferenceEquals(Held.Value, this)) return null;
        Interlocked.Increment(ref queued);
        try { await serial.WaitAsync(token); }
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
        public void Dispose() { if (!disposed) { disposed = true; Held.Value = previous; if (counted) Interlocked.Decrement(ref owner.active); HostSessionCall.LaneChanged(false); owner.serial.Release(); } }
    }
    private readonly object stateSync = new();
    private Process? process;
    private ChannelClient? channel;
    private bool attachAttempted;
    private int? attachedProcessId;
    private JsonObject? disconnectAcknowledgement;
    private volatile bool disconnecting;
    private string? priorUnknownRequestId, activeRequestId;
    private WorkerOutcomeState outcome=new();
    private readonly Queue<string> diagnostics = new();
    private JsonObject approvalIdentity = new();
    internal string ApprovalIdentity => SharedSession ? sharedStatus?["binding"]?.ToJsonString() ?? "{}" : approvalIdentity.ToJsonString();
    internal bool Poisoned => WorkerFaulted || SessionLocked?.Invoke() == true;
    internal IDisposable? HostPipeline { get; set; }
    internal object HostSessionKey { get; private set; } = new();
    private string? hostProcessStartUtc;
    private JsonObject? hostState;
    private JsonObject? sharedStatus;
    private long generation = 1;
    private int queued, active;
    private readonly JsonArray previousGenerations = new();
    private JsonObject? faultEvidence;
    internal JsonObject SupervisorSnapshot(JsonObject ready)
    {
        lock (stateSync)
        {
            bool faulted = ReleaseCatalogData.Releases[releaseKey].ExtendedWorkerStatus
                ? channel?.Poisoned == true || process?.HasExited == true || faultEvidence != null
                    || ReleaseCatalogData.Releases[releaseKey].StatusOutcomeIsFault && outcome.Poisoned
                : outcome.Poisoned;
            if (faulted && faultEvidence == null) faultEvidence = WorkerSupervisorReplies.Fault(channel?.LastFault ?? new IOException("Worker exited."),
                "idle", false, channel?.BindingEpoch, channel?.LastRequestId, process?.Id);
            var identity = SharedSession ? sharedStatus?["binding"] : HostSnapshot()["binding"];
            var environment = ready["environment"];
            var legacy = HostSnapshot();
            JsonNode? observedReadiness = ready;
            if (ReleaseCatalogData.Releases[releaseKey].ExtendedWorkerStatus)
                observedReadiness = sharedStatus?["readiness"]?.DeepClone() ?? new JsonObject { ["ready"] = ready["ready"]?.DeepClone(), ["cause"] = ready["cause"]?.DeepClone(),
                    ["groupOk"] = environment?["opennessGroupOk"]?.DeepClone(), ["recommendedFix"] = ready["recommendedFix"]?.DeepClone(),
                    ["recommendedFixZh"] = ready["recommendedFixZh"]?.DeepClone() };
            return WorkerSupervisorReplies.Snapshot(releaseKey, new WorkerSupervisorState(legacy, faulted, process != null,
                generation, Volatile.Read(ref active), Volatile.Read(ref queued), timeouts.For("status").TotalSeconds,
                JsonSerializer.SerializeToNode(timeouts.Budgets.ToDictionary(p => p.Key, p => p.Value.TotalSeconds)), Poisoned,
                faultEvidence, observedReadiness, SharedSession ? identity : attachedProcessId.HasValue ? identity : null, previousGenerations));
        }
    }
    JsonObject IFoundationWorkerSupervisor.OperatorSnapshot() => OperatorSnapshot();
    internal JsonObject OperatorSnapshot()
    {
        lock (stateSync) return new JsonObject { ["pid"] = process?.Id, ["generation"] = generation,
            ["disconnected"] = disconnecting, ["sessionLocked"] = Poisoned,
            ["binding"] = new JsonObject { ["processId"] = SharedSession ? JsonValue.Create(attachedProcessId) : approvalIdentity["ProcessId"]?.DeepClone(),
                ["projectFile"] = SharedSession ? (sharedStatus?["binding"]?["identity"]?["projectPath"] ?? sharedStatus?["binding"]?["projectPath"])?.DeepClone()
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
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        { /* swallow(env-probe): unavailable OS identity must keep the batch binding incomplete. */ return null; }
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
        string? start = !Poisoned && !disconnecting && pid.HasValue && hostProcessStartUtc == ProcessStartUtc(pid.Value) ? hostProcessStartUtc : null;
        return new JsonObject { ["enabled"] = true, ["state"] = process == null ? "Stopped" : Poisoned ? "Faulted" : "Running",
            ["sessionLocked"] = Poisoned, ["session"] = new JsonObject { ["isConnected"] = (bool?)hostState?["IsAttached"] ?? attachedProcessId.HasValue, ["project"] = project },
            ["binding"] = new JsonObject { ["identity"] = new JsonObject {
                ["tiaMajorVersion"] = TiaMcp.Versioning.TiaVersionCatalog.Get(releaseKey).MajorVersion, ["processId"] = pid,
                ["processStartUtc"] = start, ["projectPath"] = file, ["projectName"] = project,
                ["generation"] = approvalIdentity["BindingEpoch"]?.DeepClone() } } };
    }
    internal async Task RefreshHostState(bool startForStatus, CancellationToken token)
    {
        // ReadState is the worker's cached lifecycle state, not a native lookup.
        // Only an explicit supervisor read may start a cold shared worker.
        if (SharedSession)
        {
            if ((process == null && !startForStatus) || Poisoned || disconnecting || !serial.Wait(0)) return;
            HostSessionCall.LaneChanged(true);
            using var lane = new Lane(this, false); lane.Activate();
            await Observe("status", new JsonObject(), token);
        }
        else if (process != null && !Poisoned && !disconnecting) await Call("ReadState", new JsonObject(), token);
    }
    internal async Task<JsonObject> RestartHostWorker(bool confirmed, CancellationToken token)
    {
        bool extended = ReleaseCatalogData.Releases[releaseKey].ExtendedWorkerStatus;
        JsonObject Snapshot() => extended ? SupervisorSnapshot(SupervisorEnvironment()) : HostSnapshot();
        if (!confirmed) return WorkerSupervisorReplies.Restart(releaseKey, false, true, false, Snapshot());
        if (Volatile.Read(ref queued) > 0 || !serial.Wait(0)) return WorkerSupervisorReplies.Restart(releaseKey, true, false, false, Snapshot());
        HostSessionCall.LaneChanged(true);
        using var lane = new Lane(this, false); lane.Activate();
        var previous = Snapshot(); previous.Remove("previousGenerations");
        var detached = await Disconnect(token);
        bool restartTia = (bool?)detached?["RequiresTiaRestart"] == true;
        lock (stateSync)
        {
            if (WorkerShutdown.Stop(process, channel) is { } refusal)
            { RetainStopRefusal(refusal); return WorkerSupervisorReplies.Restart(releaseKey, true, false, restartTia, Snapshot()); }
            previousGenerations.Add(previous);
            while (previousGenerations.Count > 16) previousGenerations.RemoveAt(0);
            WorkerShutdown.Unregister(this); process = null; channel = null;
            approvalIdentity = new JsonObject(); attachedProcessId = null; attachAttempted = false;
            disconnectAcknowledgement = null; disconnecting = false; priorUnknownRequestId = null; faultEvidence = null;
            outcome = new WorkerOutcomeState(); ClearHostBinding(); HostSessionKey = new object(); FoundationCandidateSession.Reset(this);
            generation++;
        }
        if (extended) await Observe("status", new JsonObject(), token);
        return WorkerSupervisorReplies.Restart(releaseKey, true, true, restartTia, Snapshot());
    }
    // Only the bundled worker gets the pre-dispatch readiness gate; an explicit --worker-exe (fixture) reports its own failures.
    internal bool Bundled { get; init; } = true;
    string IFoundationSessionWorker.ApprovalIdentity => ApprovalIdentity;
    bool IFoundationSessionWorker.Poisoned => Poisoned;
    bool IFoundationSessionWorker.Bundled => Bundled;
    Task<IDisposable?> IFoundationSessionWorker.AcquireLane(CancellationToken token) => AcquireLane(token);
    void IFoundationSessionWorker.ActivateLane(IDisposable? lane) => ActivateLane(lane);
    void IFoundationSessionWorker.MarkUncertain() { if (SharedSession) McpServer.MarkSharedSessionUncertain(); }
    bool IFoundationSessionWorker.SharedSession => SharedSession;
    TiaMcp.Logic.ModelContextProtocol.ImportStagingSession? IFoundationSessionWorker.StagingOwner => StagingProvider?.Invoke();
    IDisposable? IFoundationSessionWorker.EnterRequest(ModelContextProtocol.Server.RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams> request)
        => SharedSession ? ImportStagingTools.UseSession(request) : null;

    internal Task<JsonNode?> Observe(string operation, JsonObject arguments, CancellationToken token)
        => CallCore("HostObserve", new JsonObject { ["operation"] = operation, ["arguments"] = arguments.DeepClone() }, managed: true, token);
    public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        => CallCore(operation, arguments, managed: false, token);
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
            try { await serial.WaitAsync(token); }
            finally { Interlocked.Decrement(ref queued); }
            Interlocked.Increment(ref active);
        }
        string? startBeforeAttach = null;
        Exception? channelFailureForStatus = null;
        int? hostAttachPid = null;
        bool sent = false;
        bool statusProbe = managed && (string?)arguments["operation"] == "status";
        bool usableAtEntry = !outcome.Poisoned;
        try
        {
            ChannelLimits.CheckRequest("adapter." + operation, arguments.ToJsonString(), FoundationWorkerRequest.CorrelationId);
            if (ReleaseCatalogData.Releases[releaseKey].ExtendedWorkerStatus && faultEvidence != null
                && !(WorkerOperations.IsIndependentRuntime(operation) && process is { HasExited: false } && channel?.CanCallIndependentRuntime == true))
                throw new InvalidOperationException(TiaOpenness.Shared.SessionBehavior.Recovery);
            if (!WorkerOperations.IsIndependentRuntime(operation)) { outcome.RequireUsable();
                if (SessionLocked?.Invoke() == true) throw new InvalidOperationException(TiaOpenness.Shared.SessionBehavior.Recovery); }
            token.ThrowIfCancellationRequested();
            if (disconnectAcknowledgement != null)
            {
                if (operation=="Disconnect" && arguments.Count==0) return disconnectAcknowledgement.DeepClone();
                throw SessionEnded(releaseKey, "Disconnect ended this session. A subsequent explicit Attach requires a new host session; automatic restart is refused.");
            }
            if (disconnecting) throw SessionEnded(releaseKey, "Disconnect is ending this session; a new host session is required.");
            if (operation=="Disconnect")
            {
                if(arguments.Count!=0) throw new ArgumentException("Disconnect takes no arguments.");
                if(process==null)
                {
                    disconnectAcknowledgement=DisconnectContract.Validate(DisconnectContract.Idle(),false,null,true);
                    return disconnectAcknowledgement.DeepClone();
                }
            }
            // Idle Disconnect above must not launch a worker, even in native-disabled discovery.
            if (!nativeEnabled && !managed) throw new InvalidOperationException("Native calls are disabled by --offline. Start a normal configured session to use Openness.");
            if (!managed && Bundled && WorkerOperations.RequiresPortal(operation) && !attachedProcessId.HasValue)
                throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException(TiaOpenness.Shared.SessionBehavior.PortalRequired, "session", false);
            hostAttachPid = operation == "Attach" ? (int?)arguments["processId"]
                : operation == WorkerOperations.SessionCandidate && !WorkerOperations.IsSessionPreview(operation, (string?)arguments["mode"])
                    && (string?)arguments["candidate"]?["Check"]?["Request"]?["Action"] == "attach"
                    ? (int?)arguments["candidate"]?["Check"]?["Request"]?["ProcessId"] : null;
            if (hostAttachPid.HasValue) startBeforeAttach = ProcessStartUtc(hostAttachPid.Value);
            EnsureProcess();
            if (!WorkerOperations.IsIndependentRuntime(operation) || !channel!.CanCallIndependentRuntime)
                await channel!.ConnectAsync(timeouts.For("status"),token);
            token.ThrowIfCancellationRequested();
            if (disconnecting) throw SessionEnded(releaseKey, "Disconnect ended this session; a new host session is required.");
            activeRequestId = FoundationWorkerRequest.CorrelationId;
            HostSessionCall.RequestIdChanged(activeRequestId);
            var change = BindingChangeFor(operation, arguments);
            string response;
            try
            {
                bool attach = operation == "Attach" || operation == "project-lifecycle.ConnectIsolatedPortal" || operation == WorkerOperations.SessionCandidate
                    && (string?)arguments["mode"] == "apply" && (string?)arguments["candidate"]?["Check"]?["Request"]?["Action"] == "attach";
                bool firstAttach = attach && !attachAttempted;
                token.ThrowIfCancellationRequested();
                if (attach) attachAttempted = true;
                if (operation == "HostObserve") response = await channel.CallAsync("adapter.HostObserve", arguments.ToJsonString(), BindingChange.None, true, timeouts.For("status"), CancellationToken.None);
                else
                response=await FoundationWorkerRequest.Dispatch(channel, "adapter."+operation,arguments.ToJsonString(),change,
                    WorkerOperations.IsReadOnly(operation) || arguments["dryRun"]?.GetValue<bool>()==true || WorkerOperations.IsDevicePreview(operation, (string?)arguments["mode"]) || WorkerOperations.IsImportPreview(operation, (string?)arguments["mode"]) || WorkerOperations.IsExportPreview(operation, (string?)arguments["mode"]) || WorkerOperations.IsSessionPreview(operation, (string?)arguments["mode"]) || WorkerOperations.IsSaveClosePreview(operation, (string?)arguments["mode"]) || WorkerOperations.IsSourcePreview(operation, (string?)arguments["mode"]) || WorkerOperations.IsCompilePreview(operation, (string?)arguments["mode"]),timeouts.ForPlc(operation, arguments, firstAttach), firstAttach, CancellationToken.None);
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
            outcome.AcceptResult(operation,arguments,result);
            if (outcome.Poisoned) priorUnknownRequestId ??= activeRequestId;
            if (operation == "Disconnect") approvalIdentity = new JsonObject();
            else if (result is JsonObject values)
            {
                var identity = (JsonObject)approvalIdentity.DeepClone();
                foreach (var key in new[] { "ProjectFile", "ProjectPath", "ProcessId", "ProcessStartUtc", "BindingEpoch", "SessionId", "Identity" })
                    if (values[key] != null) identity[key] = values[key]!.DeepClone();
                identity["BindingEpoch"] = channel.BindingEpoch;
                if (operation == "Attach") identity["ProcessId"] = arguments["processId"]!.GetValue<int>();
                approvalIdentity = identity;
            }
            if(operation=="Attach")
            {
                attachedProcessId=arguments["processId"]!.GetValue<int>();
                ClearHostBinding();
                if (!Poisoned && startBeforeAttach != null && startBeforeAttach == ProcessStartUtc(attachedProcessId.Value)) hostProcessStartUtc = startBeforeAttach;
            }
            if (operation == "ReadState" && result is JsonObject cachedState) hostState = (JsonObject)cachedState.DeepClone();
            else if (operation == WorkerOperations.SessionCandidate && !outcome.Poisoned
                && result?["Attempt"]?["Issued"]?.GetValue<bool>() == true && result?["Attempt"]?["Fault"] == null
                && (string?)arguments["candidate"]?["Check"]?["Request"]?["Action"] == "attach")
            {
                attachedProcessId = (int?)arguments["candidate"]?["Check"]?["Request"]?["ProcessId"];
                ClearHostBinding();
                if (hostAttachPid.HasValue && startBeforeAttach != null && startBeforeAttach == ProcessStartUtc(hostAttachPid.Value)) hostProcessStartUtc = startBeforeAttach;
            }
            if(operation=="Disconnect")
                disconnectAcknowledgement=(JsonObject)DisconnectContract.Validate(result,true,attachedProcessId,true).DeepClone();
            if (SharedSession && !outcome.Poisoned && !WorkerOperations.IsIndependentRuntime(operation))
            {
                var statusWire = await channel.CallAsync("adapter.HostObserve", "{\"operation\":\"status\",\"arguments\":{}}", BindingChange.None, true, timeouts.For("status"), CancellationToken.None);
                sharedStatus = JsonNode.Parse(statusWire)!.AsObject();
            }
            if (token.IsCancellationRequested) TiaMcpServer.ModelContextProtocol.InvocationJournal.Write(activeRequestId!, operation, "RETURNED_AFTER_CANCELLATION");
            return result;
        }
        catch(Exception ex) when (ex.Data["foundationSessionEnded"] is not true)
        {
            // ChannelClient marks OutcomeUnknown only after it has dispatched a request.
            // A first-attach timeout therefore remains an unknown native outcome instead
            // of being mistaken for a failure before the worker request was sent.
            sent |= usableAtEntry && channel?.OutcomeUnknown == true;
            if (sent && channel?.OutcomeUnknown == true) ex.Data["workerOutcomeUnknown"] = true;
            // Same mark as the engine client: a request over the worker frame was never sent.
            if (!sent && ex is ChannelLimitException) ex.Data["workerLimitBytes"] = ChannelLimits.RequestBytes;
            if (TiaMcp.Adapters.Contracts.Candidates.SessionPrimitives.IsTimeout(ex) && operation == "project-lifecycle.ConnectIsolatedPortal")
                ex.Data["sessionReason"] = TiaMcp.Adapters.Contracts.Candidates.SessionPrimitives.ConfirmationReason;
            if (TiaMcp.Adapters.Contracts.Candidates.SessionPrimitives.IsTimeout(ex) && (operation == "Attach" || operation == WorkerOperations.SessionCandidate && (string?)arguments["candidate"]?["Check"]?["Request"]?["Action"] == "attach"))
            {
                int? selectedPid = operation == "Attach" ? (int?)arguments["processId"] : (int?)arguments["candidate"]?["Check"]?["Request"]?["ProcessId"];
                bool matching = false;
                try
                {
                    using var selected = Process.GetProcessById(selectedPid ?? 0);
                    var expected = (string?)arguments["candidate"]?["Check"]?["Request"]?["ProcessStartUtc"];
                    matching = !selected.HasExited && (expected == null || new DateTimeOffset(selected.StartTime.ToUniversalTime()) == DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
                }
                catch (Exception) /* swallow(env-probe): timeout guidance does not claim a matching process after exit or access denial */ { }
                string? reason = TiaMcp.Adapters.Contracts.Candidates.SessionPrimitives.TimeoutReason(true, matching);
                if (reason != null) ex.Data["sessionReason"] = reason;
            }
            if(usableAtEntry && channel?.Poisoned==true) outcome.Failed(true,new IOException("Worker channel is poisoned."));
            ex.Data["foundationRequestSent"] = sent;
            outcome.Failed(sent,ex);
            ex.Data["foundationSessionPoisoned"] = outcome.Poisoned;
            if(outcome.Poisoned || statusProbe && !(ex is OperationCanceledException && token.IsCancellationRequested))
            {
                if (!managed && outcome.Poisoned) priorUnknownRequestId ??= activeRequestId;
                faultEvidence ??= WorkerSupervisorReplies.Fault(channelFailureForStatus ?? ex, statusProbe ? "status" : operation, !managed && sent,
                    channel?.BindingEpoch, channel?.LastRequestId, process?.Id);
            }
            throw;
        }
        finally { activeRequestId = null; if (acquired) { Interlocked.Decrement(ref active); serial.Release(); } }
    }

    // Synchronous on purpose: with CallCore's exception filter in the same async frame, a refusal
    // thrown inside this lock reached Monitor.Exit(null) in Release builds, so the caller saw an
    // ArgumentNullException and stateSync stayed held. A separate frame keeps the lock balanced.
    private void EnsureProcess()
    {
        lock (stateSync)
        {
            if (disconnecting) throw SessionEnded(releaseKey, "Disconnect ended this session; a new host session is required.");
            if (process == null)
            {
                if (!File.Exists(workerExe) || !Directory.Exists(apiDirectory)) throw new FileNotFoundException("Select the compiled worker and authorized PublicAPI directory explicitly.");
                var adapterFile=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(workerExe))!,"TiaMcp.Adapter."+releaseKey+".dll");
                var workerHash=ArgumentRules.HashFile(workerExe,FileShare.Read);
                var adapterHash=ArgumentRules.HashFile(adapterFile,FileShare.Read);
                var nonce=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                var start = new ProcessStartInfo(Path.GetFullPath(workerExe)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = new System.Text.UTF8Encoding(false), StandardErrorEncoding = new System.Text.UTF8Encoding(false) };
                foreach (var argument in new WorkerLaunchOptions(releaseKey, apiDirectory, nonce, WithUi, TiaPortalLocation).Arguments())
                    start.ArgumentList.Add(argument);
                start.Environment["TIA_MCP_WORKER_SPILLS_DIRECTORY"] = SpillsDirectory;
                start.Environment["TIA_MCP_DIAGNOSTICS_DIRECTORY"] = TiaOpenness.Shared.DataLocations.Current.DiagnosticsDirectory;
                process = Process.Start(start) ?? throw new IOException("Worker failed to start."); WorkerJob.Bind(process); WorkerShutdown.Register(this);
                attachAttempted = false;
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (diagnostics) { diagnostics.Enqueue(e.Data); while (diagnostics.Count > 8) diagnostics.Dequeue(); } };
                process.BeginErrorReadLine();
                channel=new ChannelClient(process.StandardOutput.BaseStream,process.StandardInput.BaseStream,
                    new ChannelIdentity(releaseKey,workerHash,adapterHash,process.Id,nonce));
            }
        }
    }

    private async Task<JsonNode?> Disconnect(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (disconnectAcknowledgement != null) return disconnectAcknowledgement.DeepClone();
        bool unknown = ReleaseCatalogData.Releases[releaseKey].ExtendedWorkerStatus ? Poisoned : outcome.Poisoned;
        disconnecting = true;
        bool acquired = !ReferenceEquals(Held.Value, this);
        bool idle = !acquired || serial.Wait(0);
        try
        {
            if (process != null && (process.HasExited || channel?.CanDisconnect != true))
            { HostSessionCall.Ended("disconnect"); return TerminateForDisconnect(); }
            if (process == null) disconnectAcknowledgement = DisconnectContract.Idle();
            else
            {
                try
                {
                    var wire = await FoundationWorkerRequest.Dispatch(channel!, "adapter.Disconnect", "{}", BindingChange.MayAdvance, false, TimeSpan.FromMinutes(2), false, CancellationToken.None);
                    wire = WorkerReplySpill.Read(SpillsDirectory, wire, out _);
                    disconnectAcknowledgement = (JsonObject)DisconnectContract.Validate(JsonNode.Parse(wire), true, attachedProcessId, attachedProcessId.HasValue).DeepClone();
                }
                catch (ChannelFailureException refused) when (DisconnectContract.Refused(refused))
                { disconnecting = false; throw DisconnectContract.Refusal(refused); }
                catch (Exception) /* swallow(teardown): an unacknowledged detach retains the unknown outcome and requires TIA restart */ { HostSessionCall.Ended("disconnect"); return TerminateForDisconnect(); }
            }
            HostSessionCall.Ended("disconnect");
            approvalIdentity = new JsonObject(); attachedProcessId = null; ClearHostBinding();
            if (unknown) DisconnectContract.Recovery(disconnectAcknowledgement, false, priorUnknownRequestId);
            return disconnectAcknowledgement.DeepClone();
        }
        finally { if (idle && acquired) serial.Release(); }
    }

    private JsonNode TerminateForDisconnect()
    {
        lock (stateSync)
        {
            priorUnknownRequestId ??= activeRequestId;
            channel?.Dispose();
            if (process != null)
            {
                try { WorkerShutdown.KillOwned(process); }
                catch (WorkerTerminationException refusal) { RetainStopRefusal(refusal); throw; }
                if (ReleaseCatalogData.Releases[releaseKey].ExtendedWorkerStatus) { process.Dispose(); process = null; channel = null; }
            }
            disconnectAcknowledgement = DisconnectContract.Recovery(DisconnectContract.Idle(), true, priorUnknownRequestId);
            approvalIdentity = new JsonObject(); attachedProcessId = null; ClearHostBinding();
        }
        return disconnectAcknowledgement.DeepClone();
    }

    public void Dispose()
    {
        HostPipeline?.Dispose(); HostPipeline = null;
        disconnecting = true;
        lock (stateSync)
        {
            if (WorkerShutdown.Stop(process, channel) is { } refusal) { RetainStopRefusal(refusal); return; }
            WorkerShutdown.Unregister(this); channel = null; process = null;
        }
        // An interrupted async caller still releases its lane in finally.
    }

    private void RetainStopRefusal(WorkerTerminationException refusal)
    {
        outcome.Failed(true, refusal);
        faultEvidence ??= WorkerSupervisorReplies.Fault(refusal, "stop", false, channel?.BindingEpoch, channel?.LastRequestId, process?.Id);
    }

    internal static BindingChange BindingChangeFor(string operation, JsonObject arguments)
    {
        if (operation == WorkerOperations.SaveCloseCandidate)
            return WorkerOperations.IsSaveClosePreview(operation, (string?)arguments["mode"])
                || (string?)arguments["candidate"]?["Check"]?["Request"]?["Action"] == "save" ? BindingChange.None : BindingChange.MayAdvance;
        if (operation == WorkerOperations.SessionCandidate)
            return WorkerOperations.IsSessionPreview(operation, (string?)arguments["mode"]) ? BindingChange.None : BindingChange.MayAdvance;
        var dryRun = arguments["dryRun"]?.GetValue<bool>() ?? true;
        return operation is "Attach" or "BindProject" || (!dryRun && operation is "OpenProject" or "CreateProject" or "CloseProject")
            ? BindingChange.Advance : operation == "Disconnect" ? BindingChange.MayAdvance : BindingChange.None;
    }

    internal void InvalidateCandidateSession()
    {
        bool acquired = !ReferenceEquals(Held.Value, this);
        if (acquired) serial.Wait();
        try
        {
            var failure = new IOException("The candidate outcome is unknown; inspect TIA before a new session.");
            outcome.Failed(true, failure);
            priorUnknownRequestId ??= TiaOpenness.Shared.AuditInvocation.CurrentRequestId ?? TiaMcpServer.ModelContextProtocol.InvocationJournal.CorrelationId;
        }
        finally { if (acquired) serial.Release(); }
    }
}
