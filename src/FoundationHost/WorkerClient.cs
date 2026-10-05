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
    private Process? process;
    private ChannelClient? channel;
    private int? attachedProcessId;
    private JsonObject? disconnectAcknowledgement;
    private readonly WorkerOutcomeState outcome=new();
    private readonly Queue<string> diagnostics = new();

    public async Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
    {
        await serial.WaitAsync(token);
        bool sent = false;
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
                response=await channel.CallAsync("adapter."+operation,arguments.ToJsonString(),change,
                    WorkerOperations.IsReadOnly(operation) || arguments["dryRun"]?.GetValue<bool>()==true || WorkerOperations.IsDevicePreview(operation, (string?)arguments["mode"]) || WorkerOperations.IsImportPreview(operation, (string?)arguments["mode"]) || WorkerOperations.IsExportPreview(operation, (string?)arguments["mode"]) || WorkerOperations.IsSessionPreview(operation, (string?)arguments["mode"]),TimeSpan.FromMinutes(2),token);
                sent=true;
            }
            catch(ChannelFailure failure)
            {
                sent=true;
                var detail=failure.Message;
                if(failure.EvidenceJson!="null") detail+="; failure evidence: "+failure.EvidenceJson;
                throw new WorkerOperationException(detail,failure.Code,failure.Outcome==ChannelOutcome.RejectedBeforeNative ? "rejected-before-operation" : failure.Outcome==ChannelOutcome.ReadFailed ? "read-failed" : "unknown",failure.EvidenceJson);
            }
            var result=JsonNode.Parse(response);
            outcome.AcceptResult(operation,arguments,result);
            if(operation=="Attach") attachedProcessId=arguments["processId"]!.GetValue<int>();
            if(operation=="Disconnect")
                disconnectAcknowledgement=(JsonObject)DisconnectContract.Validate(result,true,attachedProcessId,true).DeepClone();
            return result;
        }
        catch(Exception ex)
        {
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
            if(channel?.Poisoned==true) outcome.Failed(true,new IOException("Worker channel is poisoned."));
            ex.Data["foundationRequestSent"] = sent;
            outcome.Failed(sent,ex);
            ex.Data["foundationSessionPoisoned"] = outcome.Poisoned;
            if(outcome.Poisoned) channel?.Invalidate(ex);
            throw;
        }
        finally { serial.Release(); }
    }

    public void Dispose()
    {
        // Close our input only. Never kill a TIA process or replay a timed-out call.
        if (process != null) { try { if(channel!=null) channel.Dispose(); else process.StandardInput.Close(); } catch (IOException) /* swallow(teardown): a broken worker input pipe must not prevent releasing local process and semaphore resources */ { } process.Dispose(); }
        serial.Dispose();
    }

    internal static BindingChange BindingChangeFor(string operation, JsonObject arguments)
    {
        if (operation == WorkerOperations.SessionCandidate)
            return WorkerOperations.IsSessionPreview(operation, (string?)arguments["mode"]) ? BindingChange.None : BindingChange.MayAdvance;
        var dryRun = arguments["dryRun"]?.GetValue<bool>() ?? true;
        return operation is "Attach" or "BindProject" || (!dryRun && operation is "OpenProject" or "CreateProject" or "CloseProject")
            ? BindingChange.Advance : operation == "Disconnect" ? BindingChange.MayAdvance : BindingChange.None;
    }

    internal void InvalidateCandidateSession()
    {
        serial.Wait();
        try
        {
            var failure = new IOException("The candidate outcome is unknown; inspect TIA before a new session.");
            outcome.Failed(true, failure); channel?.Invalidate(failure);
        }
        finally { serial.Release(); }
    }
}
