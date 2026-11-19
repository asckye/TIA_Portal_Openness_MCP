#if TIA_FOUNDATION_TEST_HOST
extern alias foundationhost;
extern alias toolpipeline;
using HostSession = foundationhost::TiaMcp.FoundationHost.HostSession;
using InvocationHost = toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer;
#else
using InvocationHost = TiaMcpServer.ModelContextProtocol.McpServer;
#endif
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaOpenness.Shared;

namespace TiaMcp.FoundationHost;

internal sealed partial class WorkerClient
{
    private static readonly ConcurrentDictionary<WorkerClient, byte> Holders = new();
    private string ReleaseKey => releaseKey;
    private HostSession? ownerSession;
    internal HostSession? OwnerSession
    {
        get { lock (stateSync) return ownerSession; }
        set
        {
            lock (stateSync)
            {
                LateResults.DescribeRunning = DescribeRunning;
                ownerSession = value;
            }
        }
    }
    private string? takenOverBy;
    private WorkerClient? handoverCaller;
    private bool handoverPending;

    private void RetainHandoverRunning(JsonObject running)
    {
        lock (stateSync)
        {
            runningCall = (JsonObject)running.DeepClone();
            lastRunningCall = runningCall;
            busyWaiters.Cancel();
        }
    }

    private void RequireCurrentOwner()
    {
        lock (stateSync)
            if (takenOverBy != null)
                throw RecoveryRefusal("taken-over", $"TIA was handed to session {takenOverBy}. Call ConnectPortal with the intended processId "
                    + "to connect again. An active idle holder requires takeOver=true.");
    }

    private AdapterPreconditionException HeldBySession(WorkerClient holder, string reason)
        => RecoveryRefusal("tia-held-by-session", (holder.OwnerSession?.HolderDiagnostic(holder.Running)
            ?? "Another MCP session holds this TIA process. ") + reason);

    private async Task YieldHolder(int pid, bool takeOver, CancellationToken token)
    {
        foreach (var holder in Holders.Keys)
        {
            if (ReferenceEquals(holder, this) || holder.ReleaseKey != releaseKey) continue;
            lock (holder.stateSync)
                if ((holder.attachedProcessId ?? holder.attachingProcessId) != pid || holder.process is not { HasExited: false }) continue;
            if (holder.OwnerSession == null) continue;
            if (!holder.serial.Wait(0))
                throw HeldBySession(holder, "This clears when the active call returns.");
            bool reserved = false;
            try
            {
                bool running, owned;
                lock (holder.stateSync)
                {
                    if ((holder.attachedProcessId ?? holder.attachingProcessId) != pid || holder.process is not { HasExited: false }) continue;
                    running = holder.CallRunning || holder.UnansweredRequest;
                    owned = holder.ownsPortal;
                }
                if (running) throw HeldBySession(holder, "This clears when the active call returns.");
                if (owned)
                    throw HeldBySession(holder,
                        "This worker started its TIA instance. End its connection in the Workbench after saving and closing its project.");
                if (!holder.OwnerSession.TryBeginGiveWay(takeOver ? TimeSpan.Zero : TimeSpan.FromMinutes(10)))
                    throw HeldBySession(holder, holder.OwnerSession.GiveWayRefusal(takeOver));
                reserved = true;
                Interlocked.Increment(ref holder.active);
                lock (stateSync) handoverPending = true;
                await holder.YieldToNewSession(OwnerSession?.SessionKey ?? "another-session", this, token);
            }
            finally
            {
                if (reserved)
                {
                    lock (stateSync) { handoverPending = false; ClearRunning(); }
                    Interlocked.Decrement(ref holder.active);
                    holder.ClearRunning();
                    holder.OwnerSession.FinishGiveWay();
                }
                holder.serial.Release();
                holder.FinishDeferredDisposal();
            }
        }
    }

    private async Task YieldToNewSession(string sessionKey, WorkerClient caller, CancellationToken token)
    {
        var previous = Held.Value;
        Held.Value = this;
        lock (stateSync) handoverCaller = caller;
        try
        {
            using var audit = AuditInvocation.Begin(true, InvocationHost.InvocationHostTag,
                releaseKey, "ConnectPortal", FoundationWorkerRequest.CorrelationId);
            audit?.Start();
            bool canAcknowledge;
            lock (stateSync) canAcknowledge = !Faulted && channel?.CanDisconnect == true;
            if (canAcknowledge)
            {
                var acknowledgement = await Disconnect(token, forTakeover: true);
                if ((bool?)acknowledgement?["WorkerAcknowledged"] != true)
                    throw RecoveryRefusal("tia-held-by-session", "The holder did not acknowledge Disconnect. Its TIA attachment could not be handed over.");
            }
            else if (!ResetDisconnectedWorker("takeover"))
                throw RecoveryRefusal("tia-held-by-session", "The holder's worker could not stop. Its TIA attachment could not be handed over.");
            caller.Unsettled.ImportPreviousOwner(Unsettled.Snapshot());
            lock (stateSync)
            {
                takenOverBy = sessionKey;
                rebindTarget = null;
                rebindPending = false;
                recoveryWarningPending = false;
            }
            audit?.End(canAcknowledge ? "taken-over" : "taken-over-worker-stopped");
        }
        finally { lock (stateSync) handoverCaller = null; Held.Value = previous; }
    }

    private JsonObject? SettleTakenOverDisconnect()
    {
        lock (stateSync)
        {
            if (takenOverBy == null) return null;
            if (CallRunning || UnansweredRequest || !ReferenceEquals(Held.Value, this) && serial.CurrentCount == 0) throw Busy();
            disconnectAcknowledgement = DisconnectContract.Idle();
            disconnectCompleted = true;
            disconnecting = true;
            return (JsonObject)disconnectAcknowledgement.DeepClone();
        }
    }

    private global::ModelContextProtocol.Protocol.CallToolResult DecorateTakenOverDisconnect(
        global::ModelContextProtocol.Protocol.CallToolResult result, string name)
    {
        lock (stateSync)
        {
            if (name != "DisconnectPortal" || takenOverBy == null || (bool?)result.StructuredContent?["ok"] != true) return result;
            var body = (JsonObject)result.StructuredContent!.DeepClone();
            body["meta"]!["warnings"]!.AsArray().Add(new JsonObject { ["code"] = "NATIVE_WARNING",
                ["message"] = $"This session had been taken over by session {takenOverBy}. Its local connection is disconnected.",
                ["details"] = new JsonObject { ["condition"] = "taken-over", ["sessionKey"] = takenOverBy } });
            return new global::ModelContextProtocol.Protocol.CallToolResult { IsError = result.IsError, StructuredContent = body,
                Content = new[] { new global::ModelContextProtocol.Protocol.TextContentBlock { Text = body.ToJsonString() } } };
        }
    }
}
