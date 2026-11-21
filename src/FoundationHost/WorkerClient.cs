#if TIA_FOUNDATION_TEST_HOST
extern alias toolpipeline;
using McpServer = toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer;
using ImportStagingTools = toolpipeline::TiaMcpServer.ModelContextProtocol.ImportStagingTools;
using WorkerSupervisorReplies = toolpipeline::TiaMcp.FoundationHost.WorkerSupervisorReplies;
using WorkerSupervisorState = toolpipeline::TiaMcp.FoundationHost.WorkerSupervisorState;
using ReleaseCatalogData = toolpipeline::TiaMcp.FoundationHost.ReleaseCatalogData;
using UnsettledCalls = toolpipeline::TiaMcp.FoundationHost.UnsettledCalls;
using LateCalls = toolpipeline::TiaMcp.FoundationHost.LateCalls;
using UnknownSettlement = toolpipeline::TiaMcp.FoundationHost.UnknownSettlement;
using ResponseGuardTool = toolpipeline::TiaMcpServer.ModelContextProtocol.ResponseGuardTool;
#else
using McpServer = TiaMcpServer.ModelContextProtocol.McpServer;
using ImportStagingTools = TiaMcpServer.ModelContextProtocol.ImportStagingTools;
using ResponseGuardTool = TiaMcpServer.ModelContextProtocol.ResponseGuardTool;
#endif
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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
internal interface IFoundationSessionWorker : IFoundationWorker
{
    public string ApprovalIdentity { get; }
    public bool Faulted => false;
    public bool HasPendingRebindTarget => false;
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
    private static readonly AsyncLocal<Scope?> Current = new();
    internal static string CorrelationId => Current.Value?.Id ?? TiaOpenness.Shared.AuditInvocation.CurrentRequestId
        ?? TiaMcpServer.ModelContextProtocol.InvocationJournal.CurrentCallId ?? TiaMcpServer.ModelContextProtocol.InvocationJournal.CorrelationId;
    internal static string? ToolName => Current.Value?.Name ?? TiaMcpServer.ModelContextProtocol.InvocationJournal.CurrentTool;
    internal static Scope Use(string id, string name) => Current.Value = new Scope(id, name, Current.Value);

    internal sealed class Scope(string id, string name, Scope? previous) : IDisposable
    {
        internal string Id => id;
        internal string Name => name;
        internal bool StatusUnavailable { get; set; }
        public void Dispose() => Current.Value = previous;
    }

    internal static async Task<bool> RefreshAfterReply(Func<Task> refresh, Action invalidate)
    {
        try { await refresh().ConfigureAwait(false); return true; }
        catch (Exception error) when (error is ChannelFailureException or IOException or TimeoutException or JsonException or InvalidOperationException
            or OperationCanceledException)
        {
            invalidate();
            if (Current.Value is { } scope) scope.StatusUnavailable = true;
            TiaMcpServer.ModelContextProtocol.InvocationJournal.Write(CorrelationId, "worker:post-call status", "THREW", error.GetType().Name);
            return false;
        }
    }
    internal static async Task<string> Dispatch(ChannelClient channel, string method, string args, BindingChange change,
        bool readOnly, TimeSpan timeout, bool firstAttach, Action<JsonObject> onRunning, Task<JsonObject>? dialogReported, CancellationToken token,
        Action<Task<string>>? onDrain = null)
    {
        string id = CorrelationId;
        string? name = Current.Value?.Name ?? TiaMcpServer.ModelContextProtocol.InvocationJournal.CurrentTool;
        string phase = "THREW";
        var since = DateTimeOffset.UtcNow;
        if (name != null) TiaMcpServer.ModelContextProtocol.InvocationJournal.Write(id, "worker:" + name, "BEFORE");
        try
        {
            string reply;
            try
            {
                var response = channel.CallToolAsync(method, args, change, readOnly, timeout, firstAttach, id, name, true, token);
                if (dialogReported != null && await Task.WhenAny(response, dialogReported) == dialogReported && !response.IsCompleted)
                {
                    onRunning(new JsonObject { ["requestId"] = id, ["tool"] = name ?? method, ["sinceUtc"] = since.ToString("O"),
                        ["dialog"] = (await dialogReported).DeepClone(), ["message"] = "TIA reported an unanswered dialog." });
                }
                reply = await response;
            }
            catch (ChannelCallRunningException running)
            {
                onDrain?.Invoke(running.Reply);
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

internal sealed partial class WorkerClient(string releaseKey, string workerExe, string apiDirectory, bool nativeEnabled, int defaultTimeoutSeconds = 120, string
        ? timeoutConfig = null)
    : IFoundationSessionWorker, IFoundationWorkerResult, IFoundationWorkerSupervisor
{
    internal bool SharedSession { get; init; } = true;
    internal Func<TiaMcp.Logic.ModelContextProtocol.ImportStagingSession>? StagingProvider { get; set; }
    internal bool WorkerFaulted => Faulted;
    internal Func<JsonObject>? SupervisorReadiness { get; set; }
    internal bool WithUi { get; init; }
    internal string? TiaPortalLocation { get; init; }
    private readonly WorkerTimeoutPolicy timeouts = new(defaultTimeoutSeconds, timeoutConfig,
        Environment.GetEnvironmentVariable("TIA_MCP_WORKER_TIMEOUTS"));
    // The process data location is cached at its first use, so
    // parallel test processes could share it. Production always uses the data location.
    internal string? SpillsDirectoryForTest { get; init; }
    internal bool Disconnected => disconnecting || sessionDisconnected;
    internal bool CallRunning
    {
        get
        {
            lock (stateSync) return handoverPending && runningCall != null || !settlementDrain.IsCompleted
                || channel?.Poisoned != true && process is { HasExited: false } && (runningCall != null || channel?.CallRunning == true);
        }
    }
    private bool UnansweredRequest => process is { HasExited: false }
        && !unansweredHostProbe
        && (channel?.Poisoned != true && (channel?.CallRunning == true || channel?.InFlight == true || channel?.Unanswered == true)
            || channel?.LastFault is TimeoutException && channel.OutcomeUnknown);
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
        var lane = new Lane(this);
        try
        {
            string? tool = FoundationWorkerRequest.ToolName;
            if (tool != "ConnectPortal") RequireCurrentOwner();
            RequireBindingProcess(tool);
            if (nativeEnabled && Faulted && (tool == null || !TiaMcp.Adapters.Contracts.PortedFamilies.Contains(tool)
                || TiaMcp.Adapters.Contracts.PortedFamilies.ForTool(tool).Name != "F04"))
                await RecoverLostWorker(false, token);
            return lane;
        }
        catch { lane.Dispose(); throw; }
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
    private Task settlementDrain = Task.CompletedTask;
    private JsonObject? runningCall, lastRunningCall, activeCall;
    private TaskCompletionSource<JsonObject>? dialogReported;
    private int dialogProcessId;
    internal Func<int, PortalWindows> WindowProbe { get; set; } = PortalWindowProbe.Read;
    private CancellationTokenSource busyWaiters = new();
    private bool disposeRequested;
    private volatile bool unansweredHostProbe;
    public LateCalls LateResults { get; } = new();
    internal JsonObject? Running
    {
        get
        {
            lock (stateSync)
            {
                if (handoverPending && LateResults.Running is { } handover) return handover;
                if (!settlementDrain.IsCompleted) return null;
                var current = runningCall ?? (channel?.CallRunning == true ? lastRunningCall : null);
                if (channel?.Poisoned == true || process?.HasExited == true || current == null) return null;
                return DescribeRunning((JsonObject)current.DeepClone());
            }
        }
    }
    private TiaMcp.Adapters.Contracts.AdapterPreconditionException Busy()
    {
        JsonObject? running;
        lock (stateSync)
        {
            if (!handoverPending && !settlementDrain.IsCompleted)
                return RecoveryRefusal("worker-call-running", "An optional settlement read is still draining on the worker channel. "
                    + "The earlier write has ended with an unknown outcome. Try the next native call after the read finishes.");
            running = Running ?? activeCall ?? lastRunningCall;
        }
        var error = new TiaMcp.Adapters.Contracts.AdapterPreconditionException(
            $"Worker call {running?["tool"]} (request {running?["requestId"]}, started {running?["sinceUtc"]}) is still running. "
                + "Read GetCallResult(requestId) before repeating it." + (running == null ? "" : LateCalls.RunningDiagnostic(running)),
            "worker-call-running", null);
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
    private Process? process;
    private ChannelClient? channel;
    private bool attachAttempted;
    private int? attachedProcessId;
    private int? attachingProcessId;
    private JsonObject? disconnectAcknowledgement;
    private bool disconnectCompleted;
    private volatile bool sessionDisconnected;
    private int unsettledLimitReported;
    private volatile bool disconnecting;
    private string? activeRequestId;
    private readonly Queue<string> diagnostics = new();
    private JsonObject approvalIdentity = new();
    internal string ApprovalIdentity
    {
        get { lock (stateSync) return SharedSession ? sharedStatus?["binding"]?.ToJsonString() ?? "{}" : approvalIdentity.ToJsonString(); }
    }
    internal bool Faulted => channel?.Poisoned == true || process?.HasExited == true || faultEvidence != null;
    public UnsettledCalls Unsettled { get; } = new();
    internal IDisposable? HostPipeline { get; set; }
    internal object HostSessionKey { get; private set; } = new();
    private string? hostProcessStartUtc;
    private JsonObject? hostState;
    private JsonObject? sharedStatus;
    private long generation = 1, bindingEpochOffset;
    private int queued, active;
    private readonly JsonArray previousGenerations = new();
    private JsonObject? faultEvidence;
    private bool stopping;
    private readonly object shutdownSync = new();
    internal JsonObject? SharedStatus
    {
        get { lock (stateSync) return sharedStatus == null ? null : (JsonObject)sharedStatus.DeepClone(); }
    }
    // Both ReadState paths publish the observation while holding stateSync.
    private void CacheRuntimeState(JsonObject state)
    {
        hostState = (JsonObject)state.DeepClone();
        if ((bool?)state["IsAttached"] == true || state["ProcessId"] != null) sessionDisconnected = false;
    }
    private void CacheSharedStatus(JsonObject status)
    {
        lock (stateSync)
        {
            StampSharedBinding(status);
            sharedStatus = status;
            if ((bool?)status["session"]?["isConnected"] == true) sessionDisconnected = false;
        }
    }
    internal bool Bundled { get; init; } = true;
    // An explicit path can name the real PLC worker or a standalone protocol simulator.
    internal bool IsReleasePlcWorker()
    {
        if (!File.Exists(workerExe)) return false;
        using var stream = File.OpenRead(workerExe);
        if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') return false;
        stream.Position = 0;
        using var image = new PEReader(stream);
        if (!image.HasMetadata) return false;
        var metadata = image.GetMetadataReader();
        return metadata.IsAssembly && metadata.StringComparer.Equals(metadata.GetAssemblyDefinition().Name, "TiaMcp.PlcWorker." + releaseKey);
    }

    internal string? PresetProjectFile { get; init; }
    string IFoundationSessionWorker.ApprovalIdentity => ApprovalIdentity;
    bool IFoundationSessionWorker.Faulted => Faulted;
    bool IFoundationSessionWorker.HasPendingRebindTarget { get { lock (stateSync) return rebindPending && rebindTarget != null; } }
    JsonObject? IFoundationSessionWorker.Running => Running;
    bool IFoundationSessionWorker.Bundled => Bundled;
    Task<IDisposable?> IFoundationSessionWorker.AcquireLane(CancellationToken token) => AcquireLane(token);
    void IFoundationSessionWorker.ActivateLane(IDisposable? lane) => ActivateLane(lane);
    bool IFoundationSessionWorker.SharedSession => SharedSession;
    TiaMcp.Logic.ModelContextProtocol.ImportStagingSession? IFoundationSessionWorker.StagingOwner => StagingProvider?.Invoke();
    IDisposable? IFoundationSessionWorker.EnterRequest(ModelContextProtocol.Server.RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams> request)
        => SharedSession && StagingProvider != null ? ImportStagingTools.UseSession(request) : null;
    private async Task<JsonNode?> CallCore(string operation, JsonObject arguments, bool managed, bool settling, CancellationToken token)
    {
        if (operation == "Disconnect")
        {
            if (arguments.Count != 0) throw new ArgumentException("Disconnect takes no arguments.");
            token.ThrowIfCancellationRequested();
            if (SettleTakenOverDisconnect() is { } settled) { HostSessionCall.Ended("disconnect"); return settled; }
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
        bool observedBeforeAttach = false;
        Exception? channelFailureForStatus = null;
        int? hostAttachPid = null;
        bool sent = false;
        bool? ownershipBeforeStart = null;
        long requestIdAtEntry = channel?.LastRequestId ?? 0;
        bool statusProbe = managed && (string?)arguments["operation"] == "status";
        try
        {
            ForgetSecondaryIfHolderGone();
            if (operation is "Attach" or "BindProject" && secondaryProjectFile != null)
            {
                arguments = (JsonObject)arguments.DeepClone();
                arguments["recordedSecondaryProjectFile"] = secondaryProjectFile;
                arguments["recordedSecondaryProcessId"] = secondaryProcessId;
                arguments["recordedSecondaryProcessStartTicks"] = DateTimeOffset.Parse(secondaryProcessStartUtc!,
                    System.Globalization.CultureInfo.InvariantCulture).UtcTicks;
            }
            ChannelLimits.CheckRequest("adapter." + operation, arguments.ToJsonString(), FoundationWorkerRequest.CorrelationId,
                FoundationWorkerRequest.ToolName, UnsettledRequestIds());
            if (CallRunning && (acquired || !settlementDrain.IsCompleted)) throw Busy();
            token.ThrowIfCancellationRequested();
            if (!nativeEnabled && !managed)
                throw new InvalidOperationException("Native calls are disabled by --offline. Start a normal configured session to use Openness.");
            if (!managed && operation != "Attach") RequireCurrentOwner();
            if (!managed) RequireBindingProcess(operation);
            if (!managed && operation == "Attach" && arguments["processId"] is JsonValue selected && selected.TryGetValue<int>(out int selectedPid))
            {
                // takeOver is host admission metadata; the worker also receives the retained secondary context.
                bool takeOver = (bool?)arguments["takeOver"] ?? false;
                arguments = (JsonObject)arguments.DeepClone();
                arguments.Remove("takeOver");
                lock (stateSync) dialogProcessId = selectedPid;
                if (!recovering && !attachedProcessId.HasValue) await YieldHolder(selectedPid, takeOver, token);
            }
            if (disconnectAcknowledgement != null && !managed && (operation == "Attach" || disconnectCompleted)
                && !ResetDisconnectedWorker("disconnect"))
                throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("The previous worker could not stop.", "worker-faulted", null);
            if (!settling && !managed && !WorkerOperations.IsIndependentRuntime(operation))
            {
                bool selectSession = operation is "Attach" or "ConnectPortal" or "project-lifecycle.ConnectIsolatedPortal"
                    || operation == "project-lifecycle.ExecuteProjectScaffoldPrimitive"
                        && (string?)arguments["request"]?["Kind"] is "connect" or "createProject";
                bool independentRead = !WorkerOperations.RequiresPortal(operation) && WorkerOperations.IsReadOnly(operation);
                if (!independentRead || Faulted) await RecoverLostWorker(!selectSession && !independentRead, token);
            }
            if (Faulted && (managed || WorkerOperations.IsIndependentRuntime(operation)
                && !(process is { HasExited: false } && channel?.CanCallIndependentRuntime == true)))
                throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException(
                    "The worker stopped. The next native call will attempt recovery.",
                    "worker-faulted", null);
            if (disconnectAcknowledgement != null || disconnecting)
                throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException(TiaOpenness.Shared.SessionBehavior.PortalRequired, "session", false);
            if (!managed && Bundled && WorkerOperations.RequiresPortal(operation) && !attachedProcessId.HasValue)
                throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException(TiaOpenness.Shared.SessionBehavior.PortalRequired, "session", false);
            hostAttachPid = operation == "Attach" ? (int?)arguments["processId"] : null;
            if (hostAttachPid.HasValue)
            {
                var observed = ReadPortalProcess(hostAttachPid.Value);
                observedBeforeAttach = !observed.Gone;
                startBeforeAttach = observed.StartUtc;
            }
            EnsureProcess();
            requestIdAtEntry = channel!.LastRequestId;
            if (!WorkerOperations.IsIndependentRuntime(operation) || !channel!.CanCallIndependentRuntime)
                await channel!.ConnectAsync(settling ? UnknownSettlement.ReadBudget : timeouts.For("status"),token);
            token.ThrowIfCancellationRequested();
            if (disconnecting) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException(
                TiaOpenness.Shared.SessionBehavior.PortalRequired, "session", false);
            if (operation == "CloseProject" && IsSecondaryProjectOperation(operation, arguments))
            {
                string file = (string)arguments["secondaryProjectFile"]!;
                if (secondaryProjectFile == null)
                    throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("This session has no recorded secondary project to close.",
                        "secondaryProjectFile", false);
                TiaMcp.Adapters.MutationIdentityPolicy.RequireSameProject(file, secondaryProjectFile);
                RequireSecondaryHolder();
            }
            else if (operation == "OpenProject" && IsSecondaryProjectOperation(operation, arguments)
                && (attachedProcessId == null || hostProcessStartUtc == null))
                throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Cannot identify the holding TIA PID and process start time.",
                    "openMode", false);
            else if (secondaryProjectFile != null && operation is "CloseProject" or "OpenProject")
                throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException(
                    "CloseProject secondaryProjectFile must explicitly close this session's recorded secondary project first.",
                    "secondaryProjectFile", false);
            activeRequestId = FoundationWorkerRequest.CorrelationId;
            if (operation == "Attach") lock (stateSync) attachingProcessId = hostAttachPid;
            var dialog = settling ? null : BeginDialogCall(activeRequestId, operation, hostAttachPid ?? attachedProcessId);
            if (!settling) HostSessionCall.RequestIdChanged(activeRequestId!);
            var change = BindingChangeFor(operation, arguments);
            long epochBefore = channel!.BindingEpoch;
            string response;
            try
            {
                bool attach = operation == "Attach" || operation == "project-lifecycle.ConnectIsolatedPortal";
                bool firstAttach = attach && !attachAttempted;
                token.ThrowIfCancellationRequested();
                if (attach) attachAttempted = true;
                if (operation == "project-lifecycle.ConnectIsolatedPortal"
                    || operation == "project-lifecycle.ExecuteProjectScaffoldPrimitive" && (string?)arguments["request"]?["Kind"] == "connect")
                {
                    lock (stateSync) { ownershipBeforeStart = ownsPortal; ownsPortal = true; }
                }
                unansweredHostProbe = operation == "ReadState" || operation == "HostObserve" && (string?)arguments["operation"] != "recovery-processes";
                if (unansweredHostProbe) response = await channel
                    .CallToolAsync("adapter." + operation, arguments.ToJsonString(), BindingChange.None, true, timeouts.For("status"), false,
                        activeRequestId, FoundationWorkerRequest.ToolName, false, CancellationToken.None);
                else
                response=await FoundationWorkerRequest.Dispatch(channel, "adapter."+operation,arguments.ToJsonString(),change,
                    managed || WorkerOperations.IsReadOnly(operation) || arguments["dryRun"] is JsonValue preview && preview.TryGetValue<bool>(out var dryRun)
                        && dryRun, settling ? UnknownSettlement.ReadBudget : timeouts.ForPlc(operation, arguments, firstAttach), firstAttach,
                    settling ? SettlementRunning : recovering ? RecoveryRunning : SetRunning, recovering ? null : dialog,
                    settling ? token : CancellationToken.None, settling ? RetainSettlementDrain : recovering ? RetainRecoveryDrain : null);
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
            if (operation == "Disconnect") { lock (stateSync) approvalIdentity = new JsonObject(); }
            else if (IsSecondaryProjectOperation(operation, arguments))
            {
                lock (stateSync)
                {
                    if (operation == "OpenProject" && (bool?)result?["Executed"] == true)
                    {
                        secondaryProjectFile = (string?)result?["ProjectFile"];
                        secondaryProcessId = attachedProcessId;
                        secondaryProcessStartUtc = hostProcessStartUtc;
                    }
                    else if (operation == "CloseProject" && ((bool?)result?["Executed"] == true
                        || (bool?)result?["SecondaryProjectGone"] == true))
                    {
                        ClearSecondaryRecord();
                    }
                }
            }
            else if (result is JsonObject values)
            {
                UpdateApprovalIdentity(values, operation == "Attach" ? arguments["processId"]!.GetValue<int>() : null);
            }
            if(operation=="Attach")
            {
                lock (stateSync)
                {
                    attachedProcessId = arguments["processId"]!.GetValue<int>(); takenOverBy = null; sessionDisconnected = false;
                    ClearHostBinding();
                    observedBindingPid = observedBeforeAttach ? attachedProcessId : null;
                    if (!Faulted && startBeforeAttach != null && startBeforeAttach == ProcessStartUtc(attachedProcessId.Value))
                        hostProcessStartUtc = startBeforeAttach;
                    Unsettled.BindProcess(attachedProcessId, hostProcessStartUtc);
                    if (result?["PreviousOwnerRequests"] is JsonArray previousRequests)
                        result["PreviousOwnerRequests"] = Unsettled.ImportPreviousOwner(previousRequests.Select(value => value!.GetValue<string>()));
                }
            }
            bool statusAvailable = true;
            if (change == BindingChange.MayAdvance && operation != "Disconnect" && channel.BindingEpoch > epochBefore)
            {
                unansweredHostProbe = true;
                statusAvailable = await FoundationWorkerRequest.RefreshAfterReply(async () => {
                    string stateWire = await channel.CallAsync("adapter.ReadState", "{}", BindingChange.None, true,
                        timeouts.For("status"), CancellationToken.None);
                    var state = JsonNode.Parse(stateWire)!.AsObject();
                    lock (stateSync)
                    {
                        attachedProcessId = (int?)state["ProcessId"];
                        ClearHostBinding();
                        UpdateApprovalIdentity(state);
                        CacheRuntimeState(state);
                        hostProcessStartUtc = !Faulted && attachedProcessId.HasValue ? ProcessStartUtc(attachedProcessId.Value) : null;
                        observedBindingPid = attachedProcessId is int pid && (hostProcessStartUtc != null || !ReadPortalProcess(pid).Gone) ? pid : null;
                        Unsettled.BindProcess(attachedProcessId, hostProcessStartUtc);
                        ownsPortal = (bool?)state["CanDetachOnUncertainShutdown"] == false || (bool?)state["OwnsPortal"] == true;
                    }
                }, () => {
                    lock (stateSync)
                    {
                        ClearHostBinding();
                        attachedProcessId = null;
                        rebindTarget = null;
                        approvalIdentity = new JsonObject();
                    }
                });
            }
            if (operation == "ReadState" && result is JsonObject cachedState)
                lock (stateSync) CacheRuntimeState(cachedState);
            RememberRebindTarget(operation, arguments, result, channel.BindingEpoch > epochBefore);
            if(operation=="Disconnect")
                disconnectAcknowledgement=(JsonObject)DisconnectContract.Validate(result,true,attachedProcessId,true).DeepClone();
            if (statusAvailable && !settling && SharedSession && !WorkerOperations.IsIndependentRuntime(operation))
            {
                unansweredHostProbe = true;
                statusAvailable = await FoundationWorkerRequest.RefreshAfterReply(async () => {
                    var statusWire = await channel
                        .CallAsync("adapter.HostObserve", "{\"operation\":\"status\",\"arguments\":{}}", BindingChange.None, true, timeouts.For("status"),
                            CancellationToken.None);
                    var status = JsonNode.Parse(statusWire)!.AsObject();
                    CacheSharedStatus(status);
                }, () => { lock (stateSync) sharedStatus = null; });
            }
            if (token.IsCancellationRequested) TiaMcpServer.ModelContextProtocol.InvocationJournal
                .Write(activeRequestId!, operation, "RETURNED_AFTER_CANCELLATION");
            unansweredHostProbe = !statusAvailable;
            return result;
        }
        catch(Exception ex)
        {
            // Only a request sent during this call can supply unknown transport evidence.
            // An answered unknown from an earlier call cannot taint a later admission refusal.
            sent |= ex.Data["workerRecoveryRejected"] is not true && channel?.Poisoned == true && channel.OutcomeUnknown
                && channel.LastRequestId > requestIdAtEntry;
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
            // A read-failed reply can follow portal creation, so only native admission rejection can restore ownership.
            if (ownershipBeforeStart.HasValue && ex is WorkerOperationException { Outcome: "rejected-before-operation" })
                lock (stateSync) ownsPortal = ownershipBeforeStart.Value;
            if (ex is WorkerOperationException workerFailure && workerFailure.EvidenceJson != null
                && JsonNode.Parse(workerFailure.EvidenceJson) is JsonObject evidence && (string?)evidence["portalLost"] is string lost)
            {
                lock (stateSync) portalLostReason = lost;
                if (TiaOpenness.Shared.ProcessLossPolicy.IsConfirmedLoss(lost)) ex.Data["workerPortalLost"] = lost;
            }
            if (sent && RecoveryNeeded && ex.Data["workerPortalLost"] == null)
                ex.Data["workerRecoveryMessage"] = "The worker connection failed during this call. Its outcome is unknown. Do not repeat this call. "
                    + "Read GetCallResult(requestId). The next native call restarts the worker. "
                    + (rebindTarget is { } target ? $"It attempts to rebind TIA PID {target.ProcessId}, project {target.ProjectFile}."
                        : "Call ConnectPortal to select the intended TIA process.");
            if(channel?.Poisoned == true || process?.HasExited == true)
            {
                faultEvidence ??= WorkerSupervisorReplies.Fault(channelFailureForStatus ?? ex, statusProbe ? "status" : operation, !managed && sent,
                    channel?.BindingEpoch, channel?.LastRequestId, process?.Id);
            }
            throw;
        }
        finally
        {
            lock (stateSync)
            {
                if (!settling && UnansweredRequest && activeCall != null) lastRunningCall = activeCall;
                activeRequestId = null;
                activeCall = null;
                dialogReported = null;
                if (operation == "Attach") attachingProcessId = null;
            }
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
}
