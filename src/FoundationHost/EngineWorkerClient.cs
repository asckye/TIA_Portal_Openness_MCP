using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.WorkerChannel;
using TiaMcp.PlcWorker;

namespace TiaMcp.LegacyHost;

internal sealed class EngineWorkerClient(HostOptions options, string workerHash) : IEngineWorker, IFoundationSessionWorker
{
    private static readonly AsyncLocal<EngineWorkerClient?> Held = new();
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly TimeSpan timeout = TimeSpan.FromSeconds(options.EngineTimeoutSeconds);
    private Process? process;
    private ChannelClient? channel;
    private bool faulted;
    private bool attachAttempted;
    private int? attachedProcessId;
    private JsonObject? disconnectAcknowledgement;
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
    internal Action<string>? Progress { get; set; }
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
        var readiness = LegacyHostPassiveDiagnostics.Readiness(options.ReleaseKey, options.ApiDirectory, options.ApiDirectorySource);
        lock (stateSync)
            if ((bool?)status["readiness"]?["ready"] == true) { readiness["ready"] = true; readiness["cause"] = null; }
        return readiness;
    }

    public async Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
    {
        bool sent = false;
        using var lane = await AcquireLane(token).ConfigureAwait(false);
        try
        {
            if (disconnectAcknowledgement != null)
            {
                if (operation == "Disconnect" && arguments.Count == 0) return disconnectAcknowledgement.DeepClone();
                throw new InvalidOperationException("Disconnect ended this session. A subsequent explicit Attach requires a new host session; automatic restart is refused.");
            }
            if (Poisoned) throw new InvalidOperationException(TiaOpenness.Shared.SessionBehavior.Recovery);
            if (!options.NativeEnabled) throw new InvalidOperationException("Native calls are disabled by --offline. Start a normal configured session to use Openness.");
            await Start(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ChannelLimits.CheckRequest("adapter." + operation, arguments.ToJsonString());
            bool attach = operation == "Attach" || operation == WorkerOperations.SessionCandidate
                && (string?)arguments["candidate"]?["Check"]?["Request"]?["Action"] == "attach";
            bool firstAttach = attach && !attachAttempted;
            if (attach) attachAttempted = true;
            sent = true;
            string wire = await channel!.CallAsync("adapter." + operation, arguments.ToJsonString(),
                WorkerClient.BindingChangeFor(operation, arguments), WorkerOperations.IsReadOnly(operation)
                    || (bool?)arguments["dryRun"] == true || (string?)arguments["mode"] == "preview", timeout, CancellationToken.None,
                firstAttach: firstAttach, correlationId: TiaOpenness.Shared.AuditInvocation.CurrentRequestId
                    ?? TiaMcpServer.ModelContextProtocol.InvocationJournal.CorrelationId).ConfigureAwait(false);
            var result = JsonNode.Parse(wire);
            foundationOutcome.AcceptResult(operation, arguments, result);
            if (operation == "Attach") attachedProcessId = (int?)arguments["processId"];
            if (operation == "Disconnect") disconnectAcknowledgement = DisconnectContract.Validate(result, true, attachedProcessId, true);
            if (foundationOutcome.Poisoned) MarkUncertain();
            await ReadStatus(CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch (ChannelFailure failure)
        {
            var error = new WorkerOperationException(failure.Message, failure.Code,
                failure.Outcome == ChannelOutcome.RejectedBeforeNative ? "rejected-before-operation" : failure.Outcome == ChannelOutcome.ReadFailed ? "read-failed" : "unknown", failure.EvidenceJson);
            error.Data["foundationRequestSent"] = sent;
            if (failure.Outcome == ChannelOutcome.Unknown) { lock (stateSync) faulted = true; error.Data["foundationSessionPoisoned"] = true; }
            throw error;
        }
        catch (Exception error)
        {
            error.Data["foundationRequestSent"] = sent;
            if (sent) { lock (stateSync) faulted = true; error.Data["foundationSessionPoisoned"] = true; }
            throw;
        }
    }
    public JsonObject Snapshot()
    {
        lock (stateSync)
            return new JsonObject { ["enabled"] = true, ["state"] = Faulted ? "Faulted" : Volatile.Read(ref active) > 0 ? "Busy" : process == null ? "NotStarted" : "Ready",
                ["generation"] = generation, ["active"] = Volatile.Read(ref active), ["queued"] = Volatile.Read(ref queued),
                ["timeoutSeconds"] = options.EngineTimeoutSeconds, ["sessionLocked"] = SessionLocked?.Invoke() == true,
                ["readiness"] = status["readiness"]?.DeepClone(), ["binding"] = binding?.DeepClone(),
                ["previousGenerations"] = previousGenerations.DeepClone() };
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
            process = Process.Start(start) ?? throw new IOException("Engine worker did not start.");
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (diagnostics) { diagnostics.Enqueue(e.Data); while (diagnostics.Count > 8) diagnostics.Dequeue(); } };
            process.BeginErrorReadLine();
            channel = new ChannelClient(process.StandardOutput.BaseStream, process.StandardInput.BaseStream,
                new ChannelIdentity(options.ReleaseKey, workerHash, adapterHash, process.Id, nonce), ChannelProfile.Engine, p => Progress?.Invoke(p));
        }
        await channel!.ConnectAsync(timeout, token).ConfigureAwait(false);
    }

    // Caller owns Acquire across identity verification, input validation and dispatch.
    // Only the pre-dispatch token is observed. An issued native call cannot be cancelled.
    public async Task<EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
    {
        var payload = new JsonObject { ["requestId"] = id, ["name"] = name, ["arguments"] = arguments.DeepClone(), ["preview"] = preview };
        string json = payload.ToJsonString();
        ChannelLimits.CheckRequest("engine.invoke", json);
        token.ThrowIfCancellationRequested();
        try
        {
            await Start(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            string wire = await channel!.CallAsync("engine.invoke", json, preview ? BindingChange.None : BindingChange.MayAdvance,
                preview || name == "GetSessionState", timeout, CancellationToken.None).ConfigureAwait(false);
            var reply = JsonNode.Parse(wire)!.AsObject();
            lock (stateSync) { status = (JsonObject)reply.DeepClone(); binding = reply["binding"]?.DeepClone(); }
            string? nativeFault = (string?)reply["nativeFault"];
            if (nativeFault != null) lock (stateSync) faulted = true;
            return new EngineReply(JsonSerializer.Deserialize<CallToolResult>(reply["result"]!.ToJsonString(), global::ModelContextProtocol.McpJsonUtilities.DefaultOptions)!,
                (bool)reply["nativeCallIssued"]!, reply["binding"]?.DeepClone(), reply["session"]?.DeepClone(), nativeFault);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { lock (stateSync) faulted = true; throw; }
    }

    public async Task<JsonObject> Status(CancellationToken token)
    {
        // Local tools use the last worker observation while its native lane is busy.
        if (Faulted || !serial.Wait(0)) { lock (stateSync) return (JsonObject)status.DeepClone(); }
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
            lock (stateSync) { status = observed; binding = observed["binding"]?.DeepClone(); if (observed["nativeFault"] != null) faulted = true; return (JsonObject)status.DeepClone(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { lock (stateSync) faulted = true; throw; }
    }
    public async Task<JsonObject> Restart(bool confirmed, CancellationToken token)
    {
        if (!confirmed) return new JsonObject { ["success"] = true, ["dryRun"] = true, ["worker"] = Snapshot() };
        if (Volatile.Read(ref queued) > 0 || !serial.Wait(0))
            return new JsonObject { ["success"] = false, ["worker"] = Snapshot() };
        try
        {
            var previous = Snapshot();
            // Retain the generation's lock evidence without nesting its entire history.
            previous.Remove("previousGenerations");
            lock (stateSync)
            {
                previousGenerations.Add(previous);
                while (previousGenerations.Count > 16) previousGenerations.RemoveAt(0);
                Stop(); faulted = false; attachAttempted = false; attachedProcessId = null; disconnectAcknowledgement = null; foundationOutcome = new WorkerOutcomeState(); binding = null; sessionKey = new object(); generation++;
                FoundationCandidateSession.Reset(this);
                status = new JsonObject { ["readiness"] = new JsonObject { ["ready"] = false } };
            }
            await ReadStatus(token).ConfigureAwait(false);
            return new JsonObject { ["success"] = true, ["restarted"] = true, ["requiresExplicitRebind"] = true, ["worker"] = Snapshot() };
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
