using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.WorkerChannel;

namespace TiaMcp.LegacyHost;

internal sealed record EngineReply(CallToolResult Result, bool NativeCallIssued, JsonNode? Binding, JsonNode? Session, string? NativeFault);

internal interface IEngineWorker : IDisposable
{
    bool Faulted { get; }
    JsonNode? Binding { get; }
    Task<IDisposable> Acquire(CancellationToken token);
    Task<EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token);
}

internal sealed class EngineWorkerClient(HostOptions options, string workerHash) : IEngineWorker
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly TimeSpan timeout = TimeSpan.FromSeconds(options.EngineTimeoutSeconds);
    private Process? process;
    private ChannelClient? channel;
    private bool faulted;
    private JsonNode? binding;
    private readonly Queue<string> diagnostics = new();
    internal Action<string>? Progress { get; set; }
    public bool Faulted => faulted || channel?.Poisoned == true;
    public JsonNode? Binding => binding?.DeepClone();
    internal static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }
    public async Task<IDisposable> Acquire(CancellationToken token)
    {
        await serial.WaitAsync(token).ConfigureAwait(false);
        return new Lane(serial);
    }
    private sealed class Lane(SemaphoreSlim serial) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) serial.Release(); }
    }

    private async Task Start(CancellationToken token)
    {
        if (Faulted) throw new ChannelFault("Engine worker generation is faulted; explicitly restart and rebind. Never replay the request.", false);
        if (process == null)
        {
            string worker = options.EngineWorkerExe!;
            if (Hash(worker) != workerHash) throw new InvalidDataException("Engine worker changed after catalog verification.");
            string adapterHash = Hash(Path.Combine(Path.GetDirectoryName(worker)!, "TiaMcp.Adapter.21.dll"));
            string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var start = new ProcessStartInfo(worker) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add("--engine-worker"); start.ArgumentList.Add("--bundle-root"); start.ArgumentList.Add(options.BundleRoot);
            start.ArgumentList.Add("--tia-major-version"); start.ArgumentList.Add("21");
            if (options.TiaPortalLocation != null) { start.ArgumentList.Add("--tia-portal-location"); start.ArgumentList.Add(options.TiaPortalLocation); }
            if (options.WithUi) start.ArgumentList.Add("--with-ui");
            start.Environment["TIA_MCP_ENGINE_NONCE"] = nonce;
            process = Process.Start(start) ?? throw new IOException("Engine worker did not start.");
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (diagnostics) { diagnostics.Enqueue(e.Data); while (diagnostics.Count > 8) diagnostics.Dequeue(); } };
            process.BeginErrorReadLine();
            channel = new ChannelClient(process.StandardOutput.BaseStream, process.StandardInput.BaseStream,
                new ChannelIdentity("21", workerHash, adapterHash, process.Id, nonce), ChannelProfile.Engine, p => Progress?.Invoke(p));
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
            binding = reply["binding"]?.DeepClone();
            string? nativeFault = (string?)reply["nativeFault"];
            if (nativeFault != null) faulted = true;
            return new EngineReply(JsonSerializer.Deserialize<CallToolResult>(reply["result"]!.ToJsonString(), global::ModelContextProtocol.McpJsonUtilities.DefaultOptions)!,
                (bool)reply["nativeCallIssued"]!, binding?.DeepClone(), reply["session"]?.DeepClone(), nativeFault);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { faulted = true; throw; }
    }

    internal async Task<JsonObject> Status(CancellationToken token)
    {
        using var lane = await Acquire(token).ConfigureAwait(false);
        await Start(token).ConfigureAwait(false);
        return JsonNode.Parse(await channel!.CallAsync("engine.status", "{}", BindingChange.None, true, timeout, CancellationToken.None).ConfigureAwait(false))!.AsObject();
    }
    internal async Task Restart(CancellationToken token)
    {
        using var lane = await Acquire(token).ConfigureAwait(false);
        Stop(); faulted = false; binding = null;
    }
    private void Stop()
    {
        channel?.Dispose(); channel = null;
        if (process != null) { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) /* swallow(teardown): an already exited process still needs its local resources released */ { } process.Dispose(); process = null; }
    }
    public void Dispose() => Stop();
}
