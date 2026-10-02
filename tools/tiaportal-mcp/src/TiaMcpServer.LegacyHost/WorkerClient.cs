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
    private readonly WorkerOutcomeState outcome=new();
    private readonly Queue<string> diagnostics = new();

    public async Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
    {
        // No worker process and no Siemens assemblies are touched in discovery mode.
        if (!nativeEnabled) throw new InvalidOperationException("Native calls are disabled. This source-preview host is not accepted for production; explicit --native-session is required for a separately authorized validation session.");
        await serial.WaitAsync(token);
        bool sent = false;
        try
        {
            outcome.RequireUsable();
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
            WorkerProtocol.ValidateExchangeResult(operation,arguments,result);
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
