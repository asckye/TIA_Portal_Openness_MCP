using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.WorkerChannel;
using TiaMcp.PlcWorker;
using TiaMcpServer.Siemens;

namespace TiaMcp.LegacyHost;

internal interface IFoundationWorker : IDisposable
{
    Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken cancellationToken);
}

internal sealed class WorkerClient(string releaseKey, string workerExe, string apiDirectory, bool nativeEnabled) : IFoundationWorker
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private static readonly AsyncLocal<WorkerClient?> Held = new();
    internal async Task<IDisposable?> AcquireLane(CancellationToken token)
    {
        if (ReferenceEquals(Held.Value, this)) return null;
        await serial.WaitAsync(token);
        return new Lane(this);
    }
    internal static void ActivateLane(IDisposable? lane) { if (lane is Lane held) held.Activate(); }
    private sealed class Lane(WorkerClient owner) : IDisposable
    {
        private WorkerClient? previous;
        private bool disposed;
        internal void Activate() { previous = Held.Value; Held.Value = owner; }
        public void Dispose() { if (!disposed) { disposed = true; Held.Value = previous; owner.serial.Release(); } }
    }
    private Process? process;
    private ChannelClient? channel;
    private bool attachAttempted;
    private int? attachedProcessId;
    private JsonObject? disconnectAcknowledgement;
    private readonly WorkerOutcomeState outcome=new();
    private readonly Queue<string> diagnostics = new();
    private JsonObject approvalIdentity = new();
    internal string ApprovalIdentity => approvalIdentity.ToJsonString();
    internal bool Poisoned => outcome.Poisoned;
    // Only the bundled worker gets the pre-dispatch readiness gate; an explicit --worker-exe (fixture) reports its own failures.
    internal bool Bundled { get; init; } = true;

    public async Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
    {
        bool acquired = !ReferenceEquals(Held.Value, this);
        if (acquired) await serial.WaitAsync(token);
        bool sent = false;
        bool usableAtEntry = !outcome.Poisoned;
        try
        {
            outcome.RequireUsable();
            token.ThrowIfCancellationRequested();
            if (disconnectAcknowledgement != null)
            {
                if (operation=="Disconnect" && arguments.Count==0) return disconnectAcknowledgement.DeepClone();
                throw new InvalidOperationException("Disconnect ended this session. A subsequent explicit Attach requires a new host session; automatic restart is refused.");
            }
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
            if (!nativeEnabled) throw new InvalidOperationException("Native calls are disabled by --offline. Start a normal configured session to use Openness.");
            if (process == null)
            {
                if (!File.Exists(workerExe) || !Directory.Exists(apiDirectory)) throw new FileNotFoundException("Select the compiled worker and authorized PublicAPI directory explicitly.");
                var adapterFile=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(workerExe))!,"TiaMcp.Adapter."+releaseKey+".dll");
                var workerHash=ArgumentRules.HashFile(workerExe,FileShare.Read);
                var adapterHash=ArgumentRules.HashFile(adapterFile,FileShare.Read);
                var nonce=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                var start = new ProcessStartInfo(Path.GetFullPath(workerExe)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = new System.Text.UTF8Encoding(false), StandardErrorEncoding = new System.Text.UTF8Encoding(false) };
                start.ArgumentList.Add("--native-session"); start.ArgumentList.Add(releaseKey); start.ArgumentList.Add(Path.GetFullPath(apiDirectory));
                start.ArgumentList.Add(nonce);
                process = Process.Start(start) ?? throw new IOException("Worker failed to start.");
                attachAttempted = false;
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (diagnostics) { diagnostics.Enqueue(e.Data); while (diagnostics.Count > 8) diagnostics.Dequeue(); } };
                process.BeginErrorReadLine();
                channel=new ChannelClient(process.StandardOutput.BaseStream,process.StandardInput.BaseStream,
                    new ChannelIdentity(releaseKey,workerHash,adapterHash,process.Id,nonce));
            }
            await channel!.ConnectAsync(TimeSpan.FromMinutes(2),token);
            token.ThrowIfCancellationRequested();
            var change = BindingChangeFor(operation, arguments);
            string response;
            try
            {
                bool attach = operation == "Attach" || operation == WorkerOperations.SessionCandidate
                    && (string?)arguments["candidate"]?["Check"]?["Request"]?["Action"] == "attach";
                bool firstAttach = attach && !attachAttempted;
                if (attach) attachAttempted = true;
                response=await channel.CallAsync("adapter."+operation,arguments.ToJsonString(),change,
                    WorkerOperations.IsReadOnly(operation) || arguments["dryRun"]?.GetValue<bool>()==true || WorkerOperations.IsDevicePreview(operation, (string?)arguments["mode"]) || WorkerOperations.IsImportPreview(operation, (string?)arguments["mode"]) || WorkerOperations.IsExportPreview(operation, (string?)arguments["mode"]) || WorkerOperations.IsSessionPreview(operation, (string?)arguments["mode"]) || WorkerOperations.IsSaveClosePreview(operation, (string?)arguments["mode"]) || WorkerOperations.IsSourcePreview(operation, (string?)arguments["mode"]) || WorkerOperations.IsCompilePreview(operation, (string?)arguments["mode"]),TimeSpan.FromMinutes(2),token,
                    firstAttach: firstAttach);
                sent=true;
            }
            catch(ChannelFailure failure)
            {
                sent=true;
                throw new WorkerOperationException(failure.Message,failure.Code,failure.Outcome==ChannelOutcome.RejectedBeforeNative ? "rejected-before-operation" : failure.Outcome==ChannelOutcome.ReadFailed ? "read-failed" : "unknown",failure.EvidenceJson);
            }
            var result=JsonNode.Parse(response);
            outcome.AcceptResult(operation,arguments,result);
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
            if(operation=="Attach") attachedProcessId=arguments["processId"]!.GetValue<int>();
            if(operation=="Disconnect")
                disconnectAcknowledgement=(JsonObject)DisconnectContract.Validate(result,true,attachedProcessId,true).DeepClone();
            return result;
        }
        catch(Exception ex)
        {
            // ChannelClient marks OutcomeUnknown only after it has dispatched a request.
            // A first-attach timeout therefore remains an unknown native outcome instead
            // of being mistaken for a failure before the worker request was sent.
            sent |= usableAtEntry && channel?.OutcomeUnknown == true;
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
            if(outcome.Poisoned) channel?.Invalidate(ex);
            throw;
        }
        finally { if (acquired) serial.Release(); }
    }

    public void Dispose()
    {
        // Close our input only. Never kill a TIA process or replay a timed-out call.
        if (process != null) { try { if(channel!=null) channel.Dispose(); else process.StandardInput.Close(); } catch (IOException) /* swallow(teardown): a broken worker input pipe must not prevent releasing local process and semaphore resources */ { } process.Dispose(); }
        serial.Dispose();
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
            outcome.Failed(true, failure); channel?.Invalidate(failure);
        }
        finally { if (acquired) serial.Release(); }
    }
}
