using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.LegacyHost;

internal interface IFoundationWorker : IDisposable
{
    Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken cancellationToken);
}

internal sealed class WorkerClient(string releaseKey, string workerExe, string apiDirectory, bool nativeEnabled) : IFoundationWorker
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private Process? process;
    private long sequence;
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
            if (!nativeEnabled) throw new InvalidOperationException("Native calls are disabled. Explicit --native-session is required for a separately authorized validation session.");
            if (process == null)
            {
                if (!File.Exists(workerExe) || !Directory.Exists(apiDirectory)) throw new FileNotFoundException("Select the compiled worker and authorized PublicAPI directory explicitly.");
                var start = new ProcessStartInfo(Path.GetFullPath(workerExe)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("--native-session"); start.ArgumentList.Add(releaseKey); start.ArgumentList.Add(Path.GetFullPath(apiDirectory));
                process = Process.Start(start) ?? throw new IOException("Worker failed to start.");
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (diagnostics) { diagnostics.Enqueue(e.Data); while (diagnostics.Count > 8) diagnostics.Dequeue(); } };
                process.BeginErrorReadLine();
            }
            var id = ++sequence;
            token.ThrowIfCancellationRequested();
            var line = new JsonObject { ["id"] = id, ["operation"] = operation, ["arguments"] = arguments.DeepClone() }.ToJsonString();
            if (line.Length > 1024 * 1024) throw new ArgumentException("Worker request exceeds one MiB.");
            sent = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await process.StandardInput.WriteLineAsync(line.AsMemory(),timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            var response = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (response == null) throw new IOException("Worker exited; native outcome is unknown.");
            var result=WorkerProtocol.Decode(response,id);
            outcome.AcceptResult(operation,arguments,result);
            if(operation=="Attach") attachedProcessId=arguments["processId"]!.GetValue<int>();
            if(operation=="Disconnect")
                disconnectAcknowledgement=(JsonObject)DisconnectContract.Validate(result,true,attachedProcessId,true).DeepClone();
            return result;
        }
        catch(Exception ex) { outcome.Failed(sent,ex); throw; }
        finally { serial.Release(); }
    }

    public void Dispose()
    {
        // Close our input only. Never kill a TIA process or replay a timed-out call.
        if (process != null) { try { process.StandardInput.Close(); } catch (IOException) { } process.Dispose(); }
        serial.Dispose();
    }
}
