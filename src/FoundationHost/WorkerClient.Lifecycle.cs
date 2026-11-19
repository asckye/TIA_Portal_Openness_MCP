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
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.WorkerChannel;
using TiaMcp.PlcWorker;
using TiaMcpServer.Siemens;

namespace TiaMcp.FoundationHost;
internal sealed partial class WorkerClient
{
    private void FinishDeferredDisposal()
    {
        if (disposeRequested) Dispose();
    }
    private bool ResetDisconnectedWorker(string reason, bool clearTarget = true)
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
            bindingEpochOffset += (channel?.BindingEpoch ?? 0) + 1;
            WorkerShutdown.Unregister(this); Holders.TryRemove(this, out _); process = null; channel = null;
            approvalIdentity = new JsonObject(); attachedProcessId = null; attachAttempted = false;
            disconnectAcknowledgement = null; disconnectCompleted = false; disconnecting = false; faultEvidence = null;
            ClearHostBinding(); HostSessionKey = new object();
            portalLostReason = null; ownsPortal = false; rebindPending = false;
            if (clearTarget) { rebindTarget = null; recoveryWarningPending = false; }
            generation++;
            return true;
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
                start.Environment[TiaMcp.Adapters.Contracts.ProjectFilePreset.WorkerVariable] = PresetProjectFile;
                start.Environment["TIA_MCP_HOST_PROCESS_ID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (NativeCrossReferenceSessionFile != null) start.Environment[TiaMcp.Adapters.Contracts.NativeCrossReferenceConsent.SessionFileVariable] =
                    NativeCrossReferenceSessionFile;
                if (NativeCrossReferenceSessionSecret != null) start.Environment[TiaMcp.Adapters.Contracts.NativeCrossReferenceConsent.SessionSecretVariable] =
                    NativeCrossReferenceSessionSecret;
                start.Environment["TIA_MCP_DIAGNOSTICS_DIRECTORY"] = TiaOpenness.Shared.DataLocations.Current.DiagnosticsDirectory;
                process = Process.Start(start) ?? throw new IOException("Worker failed to start."); WorkerJob.Bind(process); WorkerShutdown.Register(this);
                Holders.TryAdd(this, 0);
                attachAttempted = false;
                LateResults.DescribeRunning = DescribeRunning;
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) ReceiveDiagnostic(e.Data); };
                process.BeginErrorReadLine();
                channel=new ChannelClient(process.StandardOutput.BaseStream,process.StandardInput.BaseStream,
                    new ChannelIdentity(releaseKey,workerHash,adapterHash,process.Id,nonce)) {
                    UnsettledRequests = UnsettledRequestIds };
            }
        }
    }

    private async Task<JsonNode?> Disconnect(CancellationToken token, bool forTakeover = false)
    {
        token.ThrowIfCancellationRequested();
        if (CallRunning || UnansweredRequest) throw Busy();
        if (disconnectAcknowledgement != null) return disconnectAcknowledgement.DeepClone();
        ExitedBinding();
        ForgetSecondaryIfHolderGone();
        if (secondaryProjectFile != null)
            throw SecondaryHolderRefusal("CloseProject secondaryProjectFile must explicitly close it before DisconnectPortal.");
        disconnecting = true;
        bool acquired = !ReferenceEquals(Held.Value, this);
        bool idle = !acquired || serial.Wait(0);
        if (!idle) { disconnecting = false; throw Busy(); }
        try
        {
            if (process != null && (process.HasExited || channel?.CanDisconnect != true))
            {
                if (forTakeover) throw RecoveryRefusal("tia-held-by-session", "The holder could not acknowledge Disconnect.");
                HostSessionCall.Ended("disconnect"); return TerminateForDisconnect();
            }
            if (process == null) disconnectAcknowledgement = DisconnectContract.Idle();
            else
            {
                try
                {
                    unansweredHostProbe = false;
                    var dialog = BeginDialogCall(FoundationWorkerRequest.CorrelationId, "Disconnect", attachedProcessId);
                    var wire = await FoundationWorkerRequest
                        .Dispatch(channel!, "adapter.Disconnect", "{}", BindingChange.MayAdvance, false, TimeSpan.FromMinutes(2), false, SetRunning, dialog,
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
                catch (Exception) when (!forTakeover) /* swallow(teardown): an unacknowledged detach retains the unknown outcome */
                { HostSessionCall.Ended("disconnect"); return TerminateForDisconnect(); }
            }
            disconnectCompleted = true;
            if (!forTakeover) HostSessionCall.Ended("disconnect");
            RecordDisconnect();
            lock (stateSync) { approvalIdentity = new JsonObject(); attachedProcessId = null; ClearHostBinding(); }
            return disconnectAcknowledgement.DeepClone();
        }
        finally
        {
            if (forTakeover && !disconnectCompleted) disconnecting = false;
            lock (stateSync) { activeCall = null; dialogReported = null; }
            if (idle && acquired) { ClearRunning(); serial.Release(); FinishDeferredDisposal(); }
        }
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
            lock (stateSync) { approvalIdentity = new JsonObject(); attachedProcessId = null; ClearHostBinding(); }
        }
        RecordDisconnect();
        return disconnectAcknowledgement.DeepClone();
    }

    private void RecordDisconnect()
    {
        lock (stateSync) { rebindTarget = null; rebindPending = false; sessionDisconnected = true; }
        TiaMcpServer.ModelContextProtocol.InvocationJournal.Write(FoundationWorkerRequest.CorrelationId,
            "DisconnectPortal", "RECOVERY_DISCONNECT", details: new JsonObject { ["unsettled"] = Unsettled.ToJson(),
                ["workerAcknowledged"] = disconnectAcknowledgement?["WorkerAcknowledged"]?.DeepClone() });
    }

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
                    WorkerShutdown.Unregister(this); Holders.TryRemove(this, out _); channel = null; process = null;
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
}
