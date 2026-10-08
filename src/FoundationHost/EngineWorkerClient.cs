using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.WorkerChannel;
using TiaMcp.PlcWorker;

namespace TiaMcp.FoundationHost;

internal sealed class EngineWorkerClient(HostOptions options, string workerHash) : IEngineWorker, IEngineWorkerProgress, IFoundationSessionWorker, IFoundationWorkerResult
{
    private static readonly AsyncLocal<EngineWorkerClient?> Held = new();
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly WorkerTimeoutPolicy timeouts = new(options.EngineTimeoutSeconds, options.WorkerTimeoutConfig,
        Environment.GetEnvironmentVariable("TIA_MCP_WORKER_TIMEOUTS"));
    private TimeSpan timeout => timeouts.For("status");
    private Process? process;
    private ChannelClient? channel;
    private bool faulted;
    private bool attachAttempted;
    private JsonObject? faultEvidence;
    private readonly AsyncLocal<Action<string>?> progressScope = new();
    private Action<string>? activeProgress;
    private int? attachedProcessId;
    private JsonObject? disconnectAcknowledgement;
    private volatile bool disconnecting;
    private string? priorUnknownRequestId, activeRequestId;
    private WorkerOutcomeState foundationOutcome = new();
    private JsonNode? binding;
    private JsonObject status = new() { ["readiness"] = new JsonObject { ["ready"] = false } };
    private readonly object stateSync = new();
    private object sessionKey = new();
    private long generation = 1;
    private int queued, active;
    private readonly JsonArray previousGenerations = new();
    internal Func<bool>? SessionLocked { get; set; }
    private readonly Queue<string> diagnostics = new();
    public IDisposable UseProgress(Action<string>? report)
    {
        var previous = progressScope.Value; progressScope.Value = report;
        return new ProgressScope(() => progressScope.Value = previous);
    }
    private sealed class ProgressScope(Action restore) : IDisposable { public void Dispose() => restore(); }
    public CallToolResult ParkResult(CallToolResult result, string name)
        => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions)) >= ChannelLimits.ResponseBytes / 2
            ? TiaMcpServer.ModelContextProtocol.ResponseGuardTool.Shrink(result, name, "worker", force: true) : result;
    public bool Faulted { get { lock (stateSync) return faulted || channel?.Poisoned == true || process?.HasExited == true; } }
    public JsonNode? Binding { get { lock (stateSync) return binding?.DeepClone(); } }
    public object SessionKey { get { lock (stateSync) return sessionKey; } }
    public string ApprovalIdentity => Binding?.ToJsonString() ?? "{}";
    public bool Poisoned => foundationOutcome.Poisoned || Faulted || SessionLocked?.Invoke() == true;
    public bool Bundled => true;
    public bool SharedSession => true;
    public TiaMcp.Logic.ModelContextProtocol.ImportStagingSession StagingOwner => TiaMcpServer.ModelContextProtocol.McpServer.SharedStagingOwner;
    public IDisposable? EnterRequest(ModelContextProtocol.Server.RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams> request)
        => TiaMcpServer.ModelContextProtocol.ImportStagingTools.UseSession(request);
    public void MarkUncertain() => TiaMcpServer.ModelContextProtocol.McpServer.MarkSharedSessionUncertain();
    public async Task<IDisposable?> AcquireLane(CancellationToken token)
        => ReferenceEquals(Held.Value, this) ? null : await Acquire(token).ConfigureAwait(false);
    public void ActivateLane(IDisposable? lane) { if (lane is Lane held) held.Activate(); }
    internal JsonObject FoundationReadiness()
    {
        var readiness = FoundationPassiveDiagnostics.Readiness(options.ReleaseKey, options.ApiDirectory, options.ApiDirectorySource);
        lock (stateSync)
            if ((bool?)status["readiness"]?["ready"] == true) { readiness["ready"] = true; readiness["cause"] = null; }
        return readiness;
    }

    public async Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
    {
        if (operation == "Disconnect")
        {
            if (arguments.Count != 0) throw new ArgumentException("Disconnect takes no arguments.");
            return await Disconnect(token).ConfigureAwait(false);
        }
        bool sent = false;
        string correlation = TiaOpenness.Shared.AuditInvocation.CurrentRequestId ?? TiaMcpServer.ModelContextProtocol.InvocationJournal.CorrelationId;
        try
        {
            using var lane = await AcquireLane(token).ConfigureAwait(false);
            ChannelLimits.CheckRequest("adapter." + operation, arguments.ToJsonString(), correlation);
            if (disconnectAcknowledgement != null)
            {
                if (operation == "Disconnect" && arguments.Count == 0) return disconnectAcknowledgement.DeepClone();
                throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Disconnect ended this session. A subsequent explicit Attach requires a new host session; automatic restart is refused.", isArgument: false);
            }
            if (disconnecting) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Disconnect is ending this session; a new host session is required.", isArgument: false);
            if (Poisoned) throw new InvalidOperationException(TiaOpenness.Shared.SessionBehavior.Recovery);
            if (!options.NativeEnabled) throw new InvalidOperationException("Native calls are disabled by --offline. Start a normal configured session to use Openness.");
            await Start(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            bool attach = operation == "Attach" || operation == WorkerOperations.SessionCandidate
                && (string?)arguments["mode"] == "apply" && (string?)arguments["candidate"]?["Check"]?["Request"]?["Action"] == "attach";
            bool firstAttach = attach && !attachAttempted;
            if (attach) attachAttempted = true;
            if (disconnecting) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Disconnect is ending this session; a new host session is required.", isArgument: false);
            activeRequestId = correlation;
            sent = true;
            string wire = await channel!.CallAsync("adapter." + operation, arguments.ToJsonString(),
                WorkerClient.BindingChangeFor(operation, arguments), WorkerOperations.IsReadOnly(operation)
                    || (bool?)arguments["dryRun"] == true || (string?)arguments["mode"] == "preview", timeouts.For(operation, firstAttach), CancellationToken.None,
                firstAttach: firstAttach, correlationId: correlation).ConfigureAwait(false);
            wire = WorkerReplySpill.Read(TiaOpenness.Shared.DataLocations.Current.WorkerSpillsDirectory, wire, out _);
            var result = JsonNode.Parse(wire);
            foundationOutcome.AcceptResult(operation, arguments, result);
            if (foundationOutcome.Poisoned) priorUnknownRequestId ??= correlation;
            if (operation == "Attach") attachedProcessId = (int?)arguments["processId"];
            if (operation == "Disconnect") disconnectAcknowledgement = DisconnectContract.Validate(result, true, attachedProcessId, true);
            if (foundationOutcome.Poisoned) MarkUncertain();
            if (!foundationOutcome.Poisoned) await ReadStatus(CancellationToken.None).ConfigureAwait(false);
            if (token.IsCancellationRequested) TiaMcpServer.ModelContextProtocol.InvocationJournal.Write(correlation, operation, "RETURNED_AFTER_CANCELLATION");
            return result;
        }
        catch (ChannelLimitException error) when (!sent)
        { error.Data["foundationRequestSent"] = false; error.Data["workerLimitBytes"] = ChannelLimits.RequestBytes; throw; }
        catch (ChannelFailure failure)
        {
            string outcome = failure.Outcome == ChannelOutcome.RejectedBeforeNative ? "rejected-before-operation" : failure.Outcome == ChannelOutcome.ReadFailed ? "read-failed" : "unknown";
            var reported = new WorkerOperationException(failure.Message, failure.Code, outcome, failure.EvidenceJson);
            string? diagnostic = TiaMcp.Logic.V4.HostBehavior.AdmissionDiagnostic(reported, reported.Code, reported.Outcome, reported.ExceptionType);
            string? evidence = failure.EvidenceJson;
            // PortalProcessLease predates typed adapter refusals. Only its two authored
            // messages are admission diagnostics; arbitrary internal exceptions stay private.
            if (failure.Outcome == ChannelOutcome.RejectedBeforeNative && reported.ExceptionType == nameof(InvalidOperationException)
                && failure.Message is TiaOpenness.Shared.SessionBehavior.LeaseNotReleased or TiaOpenness.Shared.SessionBehavior.LeaseReserved)
            {
                diagnostic = TiaMcp.Logic.V4.HostBehavior.SafeDiagnostic(failure.Message);
                var authored = JsonNode.Parse(evidence!)!.AsObject();
                authored["exceptionType"] = nameof(TiaMcp.Adapters.Contracts.AdapterPreconditionException);
                authored["isArgument"] = false;
                evidence = authored.ToJsonString();
            }
            var error = new WorkerOperationException(diagnostic ?? failure.Message, failure.Code, outcome, evidence);
            error.Data["foundationRequestSent"] = sent;
            if (failure.Outcome == ChannelOutcome.Unknown) { RecordFault(failure, operation, true); error.Data["foundationSessionPoisoned"] = true; error.Data["workerOutcomeUnknown"] = true; }
            throw error;
        }
        catch (Exception error)
        {
            error.Data["foundationRequestSent"] = sent;
            if (sent) { RecordFault(error, operation, true); error.Data["foundationSessionPoisoned"] = true; error.Data["workerOutcomeUnknown"] = true; }
            throw;
        }
        finally { activeRequestId = null; }
    }

    private async Task<JsonNode?> Disconnect(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (disconnectAcknowledgement != null) return disconnectAcknowledgement.DeepClone();
        disconnecting = true;
        bool acquired = !ReferenceEquals(Held.Value, this);
        bool idle = !acquired || serial.Wait(0);
        try
        {
            if (!idle && process != null && channel?.CanDisconnect != true) return TerminateForDisconnect();
            return await DisconnectIdle().ConfigureAwait(false);
        }
        finally { if (idle && acquired) serial.Release(); }
    }

    private async Task<JsonObject> DisconnectIdle()
    {
        bool unknown = Poisoned;
        if (process != null && (process.HasExited || channel?.CanDisconnect != true)) return TerminateForDisconnect();
        if (process == null) disconnectAcknowledgement = DisconnectContract.Idle();
        else
        {
            try
            {
                string correlation = TiaOpenness.Shared.AuditInvocation.CurrentRequestId ?? TiaMcpServer.ModelContextProtocol.InvocationJournal.CorrelationId;
                var wire = await channel!.CallAsync("adapter.Disconnect", "{}", BindingChange.MayAdvance, false,
                    timeouts.For("Disconnect"), CancellationToken.None, correlationId: correlation).ConfigureAwait(false);
                wire = WorkerReplySpill.Read(TiaOpenness.Shared.DataLocations.Current.WorkerSpillsDirectory, wire, out _);
                disconnectAcknowledgement = (JsonObject)DisconnectContract.Validate(JsonNode.Parse(wire), true, attachedProcessId, attachedProcessId.HasValue).DeepClone();
            }
            catch (Exception) /* swallow(teardown): failed acknowledgement cannot release a lease or claim clean recovery */ { return TerminateForDisconnect(); }
        }
        lock (stateSync) { binding = null; status["binding"] = null; status["session"] = null; attachedProcessId = null; }
        if (unknown) DisconnectContract.Recovery(disconnectAcknowledgement, false, priorUnknownRequestId);
        return (JsonObject)disconnectAcknowledgement.DeepClone();
    }

    private JsonObject TerminateForDisconnect()
    {
        priorUnknownRequestId ??= activeRequestId;
        disconnectAcknowledgement = DisconnectContract.Recovery(DisconnectContract.Idle(), true, priorUnknownRequestId);
        Stop();
        lock (stateSync) { binding = null; status["binding"] = null; status["session"] = null; attachedProcessId = null; }
        return (JsonObject)disconnectAcknowledgement.DeepClone();
    }
    public JsonObject Snapshot()
    {
        lock (stateSync)
        {
            if (Faulted && faultEvidence == null) RecordFault(channel?.LastFault ?? new IOException("Worker exited."), "idle", false);
            return new JsonObject { ["enabled"] = true, ["state"] = Faulted ? "Faulted" : Volatile.Read(ref active) > 0 ? "Busy" : process == null ? "NotStarted" : "Ready",
                ["generation"] = generation, ["active"] = Volatile.Read(ref active), ["queued"] = Volatile.Read(ref queued),
                ["timeoutSeconds"] = timeouts.For("status").TotalSeconds, ["timeoutPolicy"] = JsonSerializer.SerializeToNode(timeouts.Budgets.ToDictionary(p => p.Key, p => p.Value.TotalSeconds)),
                ["sessionLocked"] = Faulted || foundationOutcome.Poisoned || SessionLocked?.Invoke() == true, ["fault"] = faultEvidence?.DeepClone(),
                ["readiness"] = status["readiness"]?.DeepClone(), ["binding"] = binding?.DeepClone(),
                ["previousGenerations"] = previousGenerations.DeepClone() };
        }
    }
    private void RecordFault(Exception error, string operation, bool dispatched)
    {
        lock (stateSync)
        {
            faulted = true;
            if (dispatched) priorUnknownRequestId ??= activeRequestId ?? TiaOpenness.Shared.AuditInvocation.CurrentRequestId ?? TiaMcpServer.ModelContextProtocol.InvocationJournal.CorrelationId;
            faultEvidence ??= new JsonObject { ["cause"] = error.GetType().Name, ["message"] = error.Message,
                ["operation"] = operation, ["dispatched"] = dispatched, ["outcome"] = dispatched ? "unknown" : "not-started",
                ["observedUtc"] = DateTimeOffset.UtcNow.ToString("O"), ["bindingEpoch"] = channel?.BindingEpoch,
                ["channelRequestId"] = channel?.LastRequestId, ["processId"] = process?.Id };
        }
    }
    internal static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }
    public async Task<IDisposable> Acquire(CancellationToken token)
    {
        Interlocked.Increment(ref queued);
        try { await serial.WaitAsync(token).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref queued); }
        Interlocked.Increment(ref active);
        return new Lane(this);
    }
    private sealed class Lane(EngineWorkerClient owner) : IDisposable
    {
        private int disposed;
        private EngineWorkerClient? previous;
        internal void Activate() { previous = Held.Value; Held.Value = owner; }
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) { Held.Value = previous; Interlocked.Decrement(ref owner.active); owner.serial.Release(); } }
    }

    private async Task Start(CancellationToken token)
    {
        ChannelClient current;
        lock (stateSync)
        {
            if (disconnecting) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Disconnect ended this session; a new host session is required.", isArgument: false);
            if (Faulted) throw new ChannelFault("Engine worker generation is faulted; explicitly restart and rebind. Never replay the request.", false);
            if (process == null)
            {
                string worker = options.EngineWorkerExe!;
                if (Hash(worker) != workerHash) throw new InvalidDataException("Engine worker changed after catalog verification.");
                string adapterHash = Hash(Path.Combine(Path.GetDirectoryName(worker)!, "TiaMcp.Adapter." + options.ReleaseKey + ".dll"));
                string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                var start = new ProcessStartInfo(worker) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
                start.ArgumentList.Add("--engine-worker"); start.ArgumentList.Add("--bundle-root"); start.ArgumentList.Add(options.BundleRoot);
                start.ArgumentList.Add("--tia-major-version"); start.ArgumentList.Add(options.ReleaseKey);
                if (options.TiaPortalLocation != null) { start.ArgumentList.Add("--tia-portal-location"); start.ArgumentList.Add(options.TiaPortalLocation); }
                if (options.WithUi) start.ArgumentList.Add("--with-ui");
                start.Environment["TIA_MCP_ENGINE_NONCE"] = nonce;
                start.Environment["TIA_MCP_WORKER_SPILLS_DIRECTORY"] = TiaOpenness.Shared.DataLocations.Current.WorkerSpillsDirectory;
                process = Process.Start(start) ?? throw new IOException("Engine worker did not start."); WorkerJob.Bind(process);
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (diagnostics) { diagnostics.Enqueue(e.Data); while (diagnostics.Count > 8) diagnostics.Dequeue(); } };
                process.BeginErrorReadLine();
                channel = new ChannelClient(process.StandardOutput.BaseStream, process.StandardInput.BaseStream,
                    new ChannelIdentity(options.ReleaseKey, workerHash, adapterHash, process.Id, nonce), ChannelProfile.Engine, p => activeProgress?.Invoke(p));
            }
            current = channel!;
        }
        await current.ConnectAsync(timeout, token).ConfigureAwait(false);
    }

    // Caller owns Acquire across identity verification, input validation and dispatch.
    // Only the pre-dispatch token is observed. An issued native call cannot be cancelled.
    public async Task<EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
    {
        bool sent = false;
        var report = preview ? null : progressScope.Value;
        var payload = new JsonObject { ["requestId"] = id, ["name"] = name, ["arguments"] = arguments.DeepClone(), ["preview"] = preview, ["progress"] = report != null };
        string json = payload.ToJsonString();
        try
        {
            ChannelLimits.CheckRequest("engine.invoke", json);
            token.ThrowIfCancellationRequested();
            await Start(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (disconnecting) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Disconnect is ending this session; a new host session is required.", isArgument: false);
            activeProgress = report;
            activeRequestId = id;
            sent = true;
            string wire = await channel!.CallAsync("engine.invoke", json, preview ? BindingChange.None : BindingChange.MayAdvance,
                preview || name == "GetSessionState", timeouts.For(name), CancellationToken.None).ConfigureAwait(false);
            wire = WorkerReplySpill.Read(TiaOpenness.Shared.DataLocations.Current.WorkerSpillsDirectory, wire, out bool spilled);
            var reply = JsonNode.Parse(wire)!.AsObject();
            lock (stateSync) { status = (JsonObject)reply.DeepClone(); status.Remove("result"); binding = reply["binding"]?.DeepClone(); }
            string? nativeFault = (string?)reply["nativeFault"];
            if (nativeFault != null) RecordFault(new IOException(nativeFault), name, true);
            var result = JsonSerializer.Deserialize<CallToolResult>(reply["result"]!.ToJsonString(), global::ModelContextProtocol.McpJsonUtilities.DefaultOptions)!;
            if ((bool)reply["nativeCallIssued"]! && (string?)result.StructuredContent?["meta"]?["outcome"] == "unknown") priorUnknownRequestId ??= id;
            _ = TiaMcpServer.ModelContextProtocol.McpServer.ToolResult(result);
            if (nativeFault != null && (string?)result.StructuredContent?["meta"]?["outcome"] != "unknown")
                result = TiaMcpServer.ModelContextProtocol.McpServer.V4Result(name,
                    new JsonObject { ["workerResult"] = result.StructuredContent?.DeepClone(), ["evidence"] = faultEvidence?.DeepClone() },
                    new TiaMcp.Logic.V4.Error("The worker lost its native session; inspect the retained evidence before restarting.",
                        new TiaMcp.Logic.V4.OutcomeUnknownDetails("worker-generation", new Dictionary<string, JsonElement>())),
                    TiaMcp.Logic.V4.Outcome.Unknown, TiaMcp.Logic.V4.Execution.Unknown, TiaMcp.Logic.V4.Completeness.Unknown, current: true);
            if (spilled) result = TiaMcpServer.ModelContextProtocol.ResponseGuardTool.Shrink(result, name, "worker", force: true);
            if (token.IsCancellationRequested) TiaMcpServer.ModelContextProtocol.InvocationJournal.Write(id, name, "RETURNED_AFTER_CANCELLATION");
            return new EngineReply(result,
                (bool)reply["nativeCallIssued"]!, reply["binding"]?.DeepClone(), reply["session"]?.DeepClone(), nativeFault);
        }
        catch (ChannelLimitException error) when (!sent)
        { error.Data["workerRequestSent"] = false; error.Data["workerLimitBytes"] = ChannelLimits.RequestBytes; throw; }
        catch (OperationCanceledException error) when (!sent && token.IsCancellationRequested)
        { error.Data["workerRequestSent"] = false; throw; }
        catch (ChannelFailure error) when (error.Outcome != ChannelOutcome.Unknown)
        { error.Data["workerRequestSent"] = false; throw; }
        catch (Exception error)
        { RecordFault(error, name, sent); error.Data["workerRequestSent"] = sent; error.Data["workerOutcomeUnknown"] = sent; throw; }
        finally { activeProgress = null; activeRequestId = null; }
    }

    public async Task<JsonObject> Status(CancellationToken token)
    {
        // Local tools use the last worker observation while its native lane is busy.
        if (disconnectAcknowledgement != null || Faulted || !serial.Wait(0)) { lock (stateSync) return (JsonObject)status.DeepClone(); }
        try { return await ReadStatus(token).ConfigureAwait(false); }
        finally { serial.Release(); }
    }
    private async Task<JsonObject> ReadStatus(CancellationToken token)
    {
        try
        {
            await Start(token).ConfigureAwait(false);
            var observed = JsonNode.Parse(await channel!.CallAsync("engine.status", "{}", BindingChange.None, true, timeout, CancellationToken.None).ConfigureAwait(false))!.AsObject();
            if ((string?)observed["releaseKey"] != options.ReleaseKey || !JsonNode.DeepEquals(observed["behaviorCapabilities"],
                TiaMcp.Logic.V4.BehaviorCapabilities.Table(typeof(EngineWorkerClient).Assembly, options.ReleaseKey)))
                throw new InvalidDataException("Engine worker release or behavior capabilities mismatch.");
            lock (stateSync) { status = observed; binding = observed["binding"]?.DeepClone(); if (observed["nativeFault"] != null) RecordFault(new IOException((string?)observed["nativeFault"]), "status", false); return (JsonObject)status.DeepClone(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { RecordFault(error, "status", false); throw; }
    }
    public async Task<JsonObject> Restart(bool confirmed, CancellationToken token)
    {
        if (!confirmed) return new JsonObject { ["success"] = true, ["dryRun"] = true, ["worker"] = Snapshot() };
        if (Volatile.Read(ref queued) > 0 || !serial.Wait(0))
            return new JsonObject { ["success"] = false, ["requiresTiaRestart"] = false, ["worker"] = Snapshot() };
        try
        {
            var previous = Snapshot();
            bool requiresTiaRestart = disconnectAcknowledgement?["RequiresTiaRestart"]?.GetValue<bool>() == true;
            if (disconnectAcknowledgement == null && process != null)
                requiresTiaRestart = (bool?) (await DisconnectIdle().ConfigureAwait(false))["RequiresTiaRestart"] == true;
            // Retain the generation's lock evidence without nesting its entire history.
            previous.Remove("previousGenerations");
            lock (stateSync)
            {
                previousGenerations.Add(previous);
                while (previousGenerations.Count > 16) previousGenerations.RemoveAt(0);
                Stop(); faulted = false; faultEvidence = null; attachAttempted = false; attachedProcessId = null; disconnectAcknowledgement = null; disconnecting = false; priorUnknownRequestId = null; foundationOutcome = new WorkerOutcomeState(); binding = null; sessionKey = new object(); generation++;
                FoundationCandidateSession.Reset(this);
                status = new JsonObject { ["readiness"] = new JsonObject { ["ready"] = false } };
            }
            await ReadStatus(token).ConfigureAwait(false);
            return new JsonObject { ["success"] = true, ["restarted"] = true, ["requiresExplicitRebind"] = true, ["requiresTiaRestart"] = requiresTiaRestart,
                ["recoveryMessage"] = requiresTiaRestart ? TiaOpenness.Shared.SessionBehavior.TiaRestartRequired : null, ["worker"] = Snapshot() };
        }
        finally { serial.Release(); }
    }
    private void Stop()
    {
        lock (stateSync)
        {
            channel?.Dispose(); channel = null;
            if (process != null) { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) /* swallow(teardown): an already exited process still needs its local resources released */ { } process.Dispose(); process = null; }
        }
    }
    public void Dispose() => Stop();
}
