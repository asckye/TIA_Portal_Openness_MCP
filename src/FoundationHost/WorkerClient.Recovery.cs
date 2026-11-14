using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Adapters.Contracts;
using TiaMcp.WorkerChannel;

namespace TiaMcp.FoundationHost;

internal sealed partial class WorkerClient
{
    private RebindTarget? rebindTarget;
    private string? portalLostReason;
    private bool ownsPortal, recovering, rebindPending, recoveryWarningPending;
    private JsonObject? workerRecovery;
    internal bool RecoveryNeeded { get { lock (stateSync) return Faulted || portalLostReason != null || rebindPending; } }
    internal Task RecoveryDrain { get; private set; } = Task.CompletedTask;

    private void RetainRecoveryDrain(Task<string> reply)
    {
        lock (stateSync) RecoveryDrain = reply;
        _ = reply.ContinueWith(completed => { _ = completed.Exception; FinishDeferredDisposal(); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void RecoveryRunning(JsonObject running)
        => throw RecoveryRefusal("tia-not-responding", $"Recovery before {running["tool"]} is still waiting for TIA PID "
            + $"{rebindTarget?.ProcessId}, project {rebindTarget?.ProjectFile}. "
            + "The requested operation has not started. The next native call can retry after recovery finishes.");

    private void StampSharedBinding(JsonObject status)
    {
        var binding = status["binding"] as JsonObject;
        if (binding == null) return;
        var identity = binding["identity"] as JsonObject ?? binding;
        identity["generation"] = bindingEpochOffset + channel!.BindingEpoch;
    }

    internal CallToolResult DecorateRecovery(CallToolResult result)
    {
        lock (stateSync)
        {
            if (!recoveryWarningPending || workerRecovery == null || result.StructuredContent is not JsonObject source
                || source["meta"]?["warnings"] is not JsonArray) return result;
            var body = (JsonObject)source.DeepClone();
            body["meta"]!["warnings"]!.AsArray().Add(new JsonObject { ["code"] = "WORKER_RESTARTED",
                ["message"] = "The worker restarted. Preview plans, approval identity and worker-held handles were lost. Preview again before applying.",
                ["details"] = workerRecovery.DeepClone() });
            recoveryWarningPending = false;
            return new CallToolResult { IsError = result.IsError, StructuredContent = body,
                Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } };
        }
    }

    private static AdapterPreconditionException RecoveryRefusal(string condition, string message)
    {
        var error = new AdapterPreconditionException(message, condition, null);
        error.Data["foundationRequestSent"] = false;
        error.Data["workerRecoveryRejected"] = true;
        return error;
    }

    private void RememberRebindTarget(string operation, JsonObject arguments, JsonNode? result)
    {
        lock (stateSync)
        {
            if (operation == "CloseProject" && (bool?)arguments["dryRun"] == false)
            { rebindTarget = null; return; }
            if (operation == "Attach") ownsPortal = (bool?)result?["OwnsPortal"] == true;
            if (operation == "ReadState" && result is JsonObject state)
                ownsPortal = (bool?)state["CanDetachOnUncertainShutdown"] == false || (bool?)state["OwnsPortal"] == true;
            if (operation == "project-lifecycle.ConnectIsolatedPortal") ownsPortal = true;
            if (ownsPortal && rebindTarget != null) rebindTarget = rebindTarget with { WorkerStartedPortal = true };
            if (BindingChangeFor(operation, arguments) == BindingChange.None) return;
            if (attachedProcessId is not int pid) return;
            string? project = (string?)(approvalIdentity["ProjectFile"] ?? approvalIdentity["ProjectPath"]);
            string? projectName = operation == "BindProject" ? (string?)arguments["projectName"]
                : project == rebindTarget?.ProjectFile ? rebindTarget?.ProjectName
                : project == null ? null : Path.GetFileNameWithoutExtension(project);
            rebindTarget = new(pid, hostProcessStartUtc, project, projectName, ownsPortal);
        }
    }

    internal async Task RecoverLostWorker(bool rebind, CancellationToken cancellationToken)
    {
        RebindTarget? target;
        string reason;
        long previousGeneration;
        bool lostOwnedPortal;
        lock (stateSync)
        {
            if (recovering || !Faulted && portalLostReason == null && !rebindPending) return;
            if (CallRunning || UnansweredRequest) throw Busy();
            if (process is { HasExited: false } && ownsPortal)
                throw RecoveryRefusal("worker-owns-portal", "This worker started the TIA instance it uses. Stopping it would close TIA. "
                    + "Save in TIA, then call RestartOpennessWorker.");
            target = rebind ? rebindTarget : null;
            lostOwnedPortal = rebind && process is { HasExited: true } && (ownsPortal || target?.WorkerStartedPortal == true);
            if (lostOwnedPortal) { rebindTarget = null; rebindPending = false; ownsPortal = false; }
            reason = portalLostReason ?? channel?.LastFault?.GetType().Name ?? (rebindPending ? "rebind-pending" : "worker-exited");
            previousGeneration = generation;
        }
        if (lostOwnedPortal)
            throw RecoveryRefusal("tia-process-gone", $"TIA PID {target?.ProcessId}, project {target?.ProjectFile}, was started by the worker that exited. "
                + "Its lifetime is no longer owned by this session. Select a TIA instance deliberately with ConnectPortal or ConnectIsolatedPortal.");
        if (!ResetDisconnectedWorker(reason, clearTarget: false))
            throw RecoveryRefusal("tia-not-responding", "The previous worker could not detach. Try the next call again.");
        var recovery = new JsonObject { ["previousGeneration"] = previousGeneration, ["generation"] = generation,
            ["reason"] = reason, ["lost"] = new JsonArray("preview-plans", "approval-identity", "batch-plans", "engineering-transactions",
                "hmi-read-tokens", "unified-read-cursors", "reflection-paths", "analysis-export-path", "project-ownership", "dialog-records"),
            ["rebound"] = false };
        lock (stateSync)
        {
            rebindTarget = target;
            rebindPending = target != null;
            workerRecovery = (JsonObject)recovery.DeepClone();
            recoveryWarningPending = true;
            recovering = target != null;
        }
        if (target == null) return;
        var previousLane = Held.Value;
        Held.Value = this;
        string phase = "processes";
        try
        {
            var facts = await CallCore("HostObserve", new JsonObject { ["operation"] = "recovery-processes", ["arguments"] = new JsonObject() },
                true, cancellationToken);
            var processes = facts!.AsArray().Select(row => new PortalProcessFact((int)row!["ProcessId"]!,
                (string?)row["ProcessStartUtc"], (string?)row["ProjectPath"], (int)row["AttachedSessions"]!)).ToArray();
            var resolution = RebindTargetResolver.Resolve(target, processes);
            string candidates = string.Join(", ", resolution.Candidates.Select(candidate => candidate.Id));
            if (resolution.Disposition == RebindDisposition.Gone)
                throw RecoveryRefusal("tia-process-gone", $"TIA PID {target.ProcessId}, project {target.ProjectFile}, is gone. The next call will try again.");
            if (resolution.Disposition is RebindDisposition.Ambiguous or RebindDisposition.Held)
                throw RecoveryRefusal("project-not-open", $"Cannot rebind project {target.ProjectFile}. "
                    + (resolution.Disposition == RebindDisposition.Ambiguous ? $"Several matching TIA processes are open ({candidates}). "
                        : $"TIA PID {candidates} has an attached Openness client. ") + "Select a process with ConnectPortal.");
            int pid = resolution.Process!.Id;
            phase = "attach";
            await CallCore("Attach", new JsonObject { ["processId"] = pid }, false, cancellationToken);
            if (target.ProjectFile != null)
            {
                phase = "project";
                await CallCore("BindProject", new JsonObject { ["projectName"] = target.ProjectName,
                    ["expectedProjectFile"] = target.ProjectFile }, false, cancellationToken);
            }
            recovery["rebound"] = true;
            lock (stateSync) { rebindPending = false; workerRecovery = (JsonObject)recovery.DeepClone(); }
        }
        catch (Exception error) when (error is not OperationCanceledException and not AdapterPreconditionException)
        {
            throw RecoveryRefusal(phase == "project" ? "project-not-open" : "tia-not-responding",
                $"Could not rebind TIA PID {target.ProcessId}, project {target.ProjectFile}. The next call will try again.");
        }
        finally
        {
            lock (stateSync) { recovering = false; if (rebindPending) rebindTarget = target; }
            Held.Value = previousLane;
        }
    }
}
