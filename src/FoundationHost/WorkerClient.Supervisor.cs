#if TIA_FOUNDATION_TEST_HOST
extern alias enginehost;
using McpServer = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;
using ImportStagingTools = enginehost::TiaMcpServer.ModelContextProtocol.ImportStagingTools;
using WorkerSupervisorReplies = enginehost::TiaMcp.FoundationHost.WorkerSupervisorReplies;
using WorkerSupervisorState = enginehost::TiaMcp.FoundationHost.WorkerSupervisorState;
using ReleaseCatalogData = enginehost::TiaMcp.FoundationHost.ReleaseCatalogData;
using UnsettledCalls = enginehost::TiaMcp.FoundationHost.UnsettledCalls;
using LateCalls = enginehost::TiaMcp.FoundationHost.LateCalls;
using UnknownSettlement = enginehost::TiaMcp.FoundationHost.UnknownSettlement;
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
internal sealed partial class WorkerClient
{
    public async Task PrepareReadiness(CancellationToken token)
    {
        // The engine read its worker status for V20/V21 and stayed usable when TIA was missing. A bundled PlcWorker cannot start
        // without the release's Openness assemblies; then the host probe alone answers readiness (P8-04j2).
        if (SharedSession && ReleaseCatalogData.Releases[releaseKey].MergeWorkerReadiness && sharedStatus == null
            && !Disconnected && !RecoveryNeeded && CanStartForStatus())
            await Observe("status", new JsonObject(), token);
    }
    // notReady answers when neither the worker nor an SDK fixture reports a ready environment (V20/V21: the engine's probe, P8-04j2).
    internal JsonObject Readiness(Func<JsonObject> production, Func<JsonObject>? notReady = null)
    {
        if (ReleaseCatalogData.Releases[releaseKey].MergeWorkerReadiness
            && ((bool?)SharedStatus?["readiness"]?["ready"] == true || WorkerSdkFixture.Ready(workerExe, releaseKey)))
        {
            var merged = production(); merged["ready"] = true; merged["cause"] = null; return merged;
        }
        return WorkerSdkFixture.Readiness(workerExe, releaseKey, apiDirectory, notReady ?? production);
    }
    private JsonObject SupervisorEnvironment() => SupervisorReadiness?.Invoke()
        ?? Readiness(() => FoundationPassiveDiagnostics.Readiness(releaseKey, apiDirectory, null));
    public ModelContextProtocol.Protocol.CallToolResult ParkResult(ModelContextProtocol.Protocol.CallToolResult result, string name)
    {
        result = DecorateRecovery(result);
        result = DecorateTakenOverDisconnect(result, name);
        return System.Text.Encoding.UTF8
            .GetByteCount(JsonSerializer.Serialize(result, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions)) >= ChannelLimits.ResponseBytes / 2
            ? ResponseGuardTool.Shrink(result, name, "worker", force: true) : result;
    }
    private void SetRunning(JsonObject running)
    {
        lock (stateSync)
        {
            var current = (JsonObject)(runningCall ?? running).DeepClone();
            current["dialog"] = activeCall?["dialog"]?.DeepClone() ?? current["dialog"]?.DeepClone();
            running = DescribeRunning((JsonObject)current.DeepClone());
            runningCall = current;
            lastRunningCall = current;
            busyWaiters.Cancel();
        }
        HostSessionCall.Detach(running);
        WorkerClient? caller;
        JsonObject latest;
        lock (stateSync)
        {
            latest = DescribeRunning((JsonObject)(runningCall ?? running).DeepClone());
            LateResults.UpdateRunning(latest);
            caller = handoverCaller;
            caller?.LateResults.UpdateRunning(latest);
        }
        caller?.RetainHandoverRunning(latest);
    }
    private JsonObject DescribeRunning(JsonObject running)
    {
        int pid = Volatile.Read(ref dialogProcessId);
        if (pid > 0)
        {
            var windows = WindowProbe(pid);
            running["window"] = new JsonObject { ["mainWindowDisabled"] = windows.MainWindowDisabled,
                ["titles"] = new JsonArray(windows.Titles.Select(title => (JsonNode?)JsonValue.Create(
                    TiaMcp.Logic.V4.HostBehavior.SafeDiagnostic(title))).ToArray()) };
        }
        return running;
    }
    private Task<JsonObject> BeginDialogCall(string requestId, string operation, int? portalProcessId)
    {
        lock (stateSync)
        {
            activeCall = new JsonObject { ["requestId"] = requestId, ["tool"] = FoundationWorkerRequest.ToolName ?? operation,
                ["sinceUtc"] = DateTimeOffset.UtcNow.ToString("O") };
            dialogReported = new(TaskCreationOptions.RunContinuationsAsynchronously);
            dialogProcessId = portalProcessId ?? 0;
            return dialogReported.Task;
        }
    }
    private void ReceiveDiagnostic(string line)
    {
        var dialog = WorkerDialog.Parse(line);
        lock (stateSync)
        {
            if (dialog != null && activeCall != null && !unansweredHostProbe && channel?.InFlight == true)
            {
                var current = (JsonObject)activeCall.DeepClone();
                current["dialog"] = dialog.DeepClone();
                activeCall = current;
                if (runningCall != null)
                {
                    var running = (JsonObject)runningCall.DeepClone();
                    running["dialog"] = dialog.DeepClone();
                    runningCall = running;
                    LateResults.UpdateRunning(running);
                    handoverCaller?.LateResults.UpdateRunning(running);
                }
                if ((string?)dialog["answer"] == "unanswered") dialogReported?.TrySetResult(dialog);
            }
        }
        lock (diagnostics)
        {
            diagnostics.Enqueue(dialog?.ToJsonString() ?? TiaMcp.Logic.V4.HostBehavior.SafeDiagnostic(line) ?? "");
            while (diagnostics.Count > 8) diagnostics.Dequeue();
        }
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
            ["workerRecovery"] = workerRecovery?.DeepClone(),
            ["running"] = LateResults.Running ?? Running,
            ["binding"] = new JsonObject { ["processId"] = SharedSession ? JsonValue.Create(attachedProcessId) : approvalIdentity["ProcessId"]?.DeepClone(),
                ["projectFile"] = SharedSession ? (sharedStatus?["binding"]?["identity"]?["projectPath"] ?? sharedStatus?["binding"]?["projectPath"])?
                    .DeepClone()
                    : (approvalIdentity["ProjectFile"] ?? approvalIdentity["ProjectPath"])?.DeepClone(),
                ["bindingEpoch"] = SharedSession ? JsonValue.Create(bindingEpochOffset + (channel?.BindingEpoch ?? 0))
                    : approvalIdentity["BindingEpoch"]?.DeepClone() } };
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
    private void UpdateApprovalIdentity(JsonObject values, int? processId = null)
    {
        lock (stateSync)
        {
            var identity = (JsonObject)approvalIdentity.DeepClone();
            foreach (var key in new[] { "ProjectFile", "ProjectPath", "ProcessId", "ProcessStartUtc", "BindingEpoch", "SessionId", "Identity" })
                if (values.TryGetPropertyValue(key, out var value)) identity[key] = value?.DeepClone();
            identity["BindingEpoch"] = bindingEpochOffset + channel!.BindingEpoch;
            if (processId.HasValue) identity["ProcessId"] = processId.Value;
            approvalIdentity = identity;
        }
    }

    private void ClearHostBinding()
    {
        lock (stateSync)
        {
            hostProcessStartUtc = null; hostState = null; sharedStatus = null;
            if (!SharedSession) HostSessionKey = new object();
        }
    }
    internal JsonObject HostSnapshot()
    {
        lock (stateSync)
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
    }
    // The V20/V21 engine started for a status read without TIA; the PlcWorker needs this release's Openness assemblies to start.
    // Without them a status read reports the stopped worker and the not-ready environment instead of a fault (P8-04j2).
    private bool CanStartForStatus() => !ReleaseCatalogData.Releases[releaseKey].MergeWorkerReadiness || !Bundled
        || !string.IsNullOrWhiteSpace(apiDirectory) && Directory.Exists(apiDirectory)
            && FoundationPassiveDiagnostics.Readiness(releaseKey, apiDirectory, null)["environment"]?["tiaInstallPath"] != null;
    internal async Task RefreshHostState(bool startForStatus, CancellationToken token)
    {
        // ReadState is the worker's cached lifecycle state, not a native lookup.
        if (CallRunning || RecoveryNeeded) return;
        // Only an explicit supervisor read may start a cold shared worker.
        if (SharedSession)
        {
            if ((process == null && (!startForStatus || !CanStartForStatus())) || Faulted || disconnecting || !serial.Wait(0)) return;
            HostSessionCall.LaneChanged(true);
            using var lane = new Lane(this, false); lane.Activate();
            if (CallRunning) return;
            var observed = await Observe("status", new JsonObject(), token);
            if (observed is JsonObject observedStatus)
            {
                var status = (JsonObject)observedStatus.DeepClone();
                StampSharedBinding(status);
                lock (stateSync) sharedStatus = status;
            }
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
}
