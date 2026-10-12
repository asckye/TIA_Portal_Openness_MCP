using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using TiaOpenness.Shared;

namespace TiaOpenness.Gui.Services;

public sealed class McpSessionClient : IMcpSessionClient
{
    internal static readonly TimeSpan OwnedHostExitTimeout = TimeSpan.FromSeconds(20);
    private readonly string root, dataRoot;
    private readonly Func<OperatorHost, CancellationToken, Task<Stream>> connect;
    private readonly object sync = new();
    private IMcpClient? client;
    private Stream? pipe;
    private IAsyncDisposable? notifications;
    private IOwnedOperatorHost? ownedHost;
    private Task? releasingHost;
    private readonly Func<ProcessStartInfo, IOwnedOperatorHost> startHost;
    private CancellationTokenSource? watching;
    private Task? watcher;
    private OperatorHost? attachedHost;
    private JsonObject? state;
    private readonly Dictionary<string, JsonObject> schemas = new(StringComparer.Ordinal);
    private long revision = -1;
    private CancellationTokenSource requests = new();
    private readonly SemaphoreSlim closing = new(1);
    private int disposed;
    public string? SessionKey { get; private set; }
    public JsonObject? State { get { lock (sync) return (JsonObject?)state?.DeepClone(); } }
    public JsonObject? ToolSchema(string tool)
    { lock (sync) return schemas.TryGetValue(tool, out var schema) ? (JsonObject)schema.DeepClone() : null; }
    public event EventHandler<JsonObject>? StateChanged;
    public event EventHandler? Detached;

    public McpSessionClient() : this(BundleLayout.RequireWorkbenchRoot(AppContext.BaseDirectory), DataLocations.Current.WorkbenchDataDirectory) { }
    internal McpSessionClient(string root, string dataRoot, Func<OperatorHost, CancellationToken, Task<Stream>>? connect = null,
        Func<ProcessStartInfo, IOwnedOperatorHost>? startHost = null)
    {
        this.root = Path.GetFullPath(root); this.dataRoot = Path.GetFullPath(dataRoot); this.connect = connect ?? ConnectVerified;
        this.startHost = startHost ?? (start => new OwnedOperatorHost(Process.Start(start) ?? throw new IOException("Unable to start the operator host.")));
    }

    private string Image(string release) => BundleLayout.WorkbenchEnginePath(root, release, AppContext.BaseDirectory);
    private static bool Alive(OperatorHost host)
    {
        try { using var process = Process.GetProcessById(host.Pid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == host.StartTicks; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { Trace.TraceInformation("Operator host no longer available: {0}", error.GetType().Name); return false; }
    }
    private async Task<Stream> ConnectVerified(OperatorHost host, CancellationToken token)
    {
        string sid = LocalPipeSecurity.CurrentSid;
        if (!Alive(host) || host.PipeName != OperatorPipe.Name(sid, dataRoot, host.Pid, host.StartTicks))
            throw new UnauthorizedAccessException("Operator host identity mismatch.");
        var stream = new NamedPipeClientStream(".", host.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await stream.ConnectAsync(1000, token).ConfigureAwait(false);
            // Registration files are hints. The kernel must attest PID, SID and the same-bundle image before the first byte is sent.
            if (!LocalPipeSecurity.ServerProcessMatches(stream, sid, Image(host.ReleaseKey), host.Pid) || !Alive(host))
                throw new UnauthorizedAccessException("Operator pipe server identity mismatch.");
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }
    private IEnumerable<OperatorHost> Hosts(string release)
    {
        string directory = Path.Combine(dataRoot, "run", "hosts");
        if (!Directory.Exists(directory)) yield break;
        foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
        {
            OperatorHost? host = null;
            try
            {
                if (new FileInfo(file).Length > OperatorProtocol.MaximumBytes) continue;
                var record = JsonNode.Parse(File.ReadAllText(file))!;
                if ((int?)record["version"] != 1 || (string?)record["releaseKey"] != release) continue;
                host = new((int)record["pid"]!, (long)record["startTicks"]!, release, (string)record["pipeName"]!, file);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
            { Trace.TraceInformation("Operator registration ignored: {0}", error.GetType().Name); }
            if (host != null && Alive(host)) yield return host;
        }
    }
    private async Task<(Stream Stream, OperatorHandshakeResult Result)> Handshake(OperatorHost host, string action, string? key, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var stream = await connect(host, deadline.Token).ConfigureAwait(false);
        try
        {
            await OperatorProtocol.Write(stream, new OperatorHandshake { Action = action, SessionKey = key,
                Workbench = new() { Pid = Environment.ProcessId, Version = ViewModels.MainViewModel.AppVersion } }, deadline.Token).ConfigureAwait(false);
            var result = await OperatorProtocol.Read<OperatorHandshakeResult>(stream, deadline.Token).ConfigureAwait(false);
            if (result.Protocol != "tiamcp.operator" || result.Version != 1 || !result.Accepted)
                throw new IOException(result.Error ?? "Invalid operator handshake response.");
            if (action != "list" && (!Guid.TryParseExact(result.SessionKey, "N", out _) || (action == "attach" && result.SessionKey != key)))
                throw new InvalidDataException("Operator session identity mismatch.");
            return (stream, result);
        }
        catch { stream.Dispose(); throw; }
    }
    public async Task<IReadOnlyList<OperatorSession>> DiscoverAsync(string releaseKey, CancellationToken token = default)
    {
        var sessions = new List<OperatorSession>();
        foreach (var host in Hosts(releaseKey))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var listed = await Handshake(host, "list", null, token).ConfigureAwait(false);
                using (listed.Stream)
                    foreach (var snapshot in listed.Result.Sessions)
                        if ((string?)snapshot["releaseKey"] == releaseKey && (string?)snapshot["owner"] == "mcp"
                            && (string?)snapshot["worker"]?["state"] != "disconnected" && Guid.TryParseExact((string?)snapshot["sessionKey"], "N", out _))
                            sessions.Add(new(host, (string)snapshot["sessionKey"]!, snapshot));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                or TimeoutException || error is OperationCanceledException && !token.IsCancellationRequested)
            { Trace.TraceWarning("Operator discovery refused: {0}", error.Message); }
        }
        return sessions;
    }
    public async Task AttachAsync(OperatorSession session, CancellationToken token = default)
    {
        await DetachAsync().ConfigureAwait(false);
        await Open(session.Host, "attach", session.SessionKey, token).ConfigureAwait(false);
    }
    private async Task Open(OperatorHost host, string action, string? key, CancellationToken token)
    {
        var attached = await Handshake(host, action, key, token).ConfigureAwait(false);
        pipe = attached.Stream; attachedHost = host; SessionKey = attached.Result.SessionKey; revision = -1;
        try
        {
            client = await McpClientFactory.CreateAsync(new StreamClientTransport(pipe, pipe), new McpClientOptions {
                ClientInfo = new() { Name = "TiaOpenness Workbench", Version = ViewModels.MainViewModel.AppVersion },
                InitializationTimeout = TimeSpan.FromSeconds(5) }, cancellationToken: token).ConfigureAwait(false);
            notifications = client.RegisterNotificationHandler(OperatorProtocol.SessionStateNotification, (notification, _) =>
            { if (notification.Params is JsonObject snapshot) Merge(snapshot); return ValueTask.CompletedTask; });
            var tools = await client.ListToolsAsync(cancellationToken: token).ConfigureAwait(false);
            lock (sync)
                foreach (var tool in tools) schemas[tool.Name] = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
            foreach (var snapshot in attached.Result.Sessions) Merge(snapshot);
            watching = new(); watcher = Watch(host, watching.Token);
        }
        catch { await DetachAsync().ConfigureAwait(false); throw; }
    }
    internal void Merge(JsonObject snapshot)
    {
        lock (sync)
        {
            if ((string?)snapshot["sessionKey"] != SessionKey || (string?)snapshot["releaseKey"] != attachedHost?.ReleaseKey
                || snapshot["revision"] is not JsonValue value || !value.TryGetValue<long>(out long next) || next <= revision) return;
            revision = next; state = (JsonObject)snapshot.DeepClone();
        }
        StateChanged?.Invoke(this, (JsonObject)snapshot.DeepClone());
    }
    private async Task Watch(OperatorHost host, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(500, token).ConfigureAwait(false);
                if (!Alive(host) || !File.Exists(host.RegistrationPath)) break;
                var record = JsonNode.Parse(await File.ReadAllTextAsync(host.RegistrationPath, token).ConfigureAwait(false));
                var snapshot = (record?["sessions"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(s => (string?)s["sessionKey"] == SessionKey);
                if (snapshot == null) break;
                Merge(snapshot);
                if ((string?)State?["worker"]?["state"] == "disconnected") break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) /* swallow(teardown): explicit panel detach stops registration watching */ { return; }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        { Trace.TraceWarning("Operator registration watch ended: {0}", error.Message); }
        // No automatic following of an AI client's new session key.
        if (!token.IsCancellationRequested)
        {
            try { await CloseConnection().ConfigureAwait(false); }
            finally { await ReleaseOwnedHost().ConfigureAwait(false); }
            Detached?.Invoke(this, EventArgs.Empty);
        }
    }
    public async Task CreateAsync(string releaseKey, CancellationToken token = default)
    {
        await DetachAsync().ConfigureAwait(false);
        var start = new ProcessStartInfo(Image(releaseKey)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
        foreach (string argument in new[] { "--bundle-root", root, "--release-key", releaseKey, "--transport", "operator", "--operator-owner", Environment.ProcessId.ToString() })
            start.ArgumentList.Add(argument);
        start.Environment["TIA_MCP_DATA_DIRECTORY"] = dataRoot;
        ownedHost = startHost(start);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (ownedHost.HasExited) throw new IOException("The operator host exited before registration.");
                var host = Hosts(releaseKey).SingleOrDefault(h => h.Pid == ownedHost.Id);
                if (host != null) { await Open(host, "create", null, deadline.Token).ConfigureAwait(false); return; }
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
        }
        catch { await DetachAsync().ConfigureAwait(false); throw; }
    }
    public async Task<JsonObject> CallAsync(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken token = default)
    {
        var active = client ?? throw new InvalidOperationException("Select and attach an MCP session first.");
        CancellationTokenSource call;
        lock (sync) call = CancellationTokenSource.CreateLinkedTokenSource(token, requests.Token);
        using var lifetime = call;
        var result = await active.CallToolAsync(tool, arguments, cancellationToken: call.Token).ConfigureAwait(false);
        var envelope = result.StructuredContent as JsonObject;
        if (envelope == null)
            envelope = result.Content.OfType<TextContentBlock>().Select(block => JsonNode.Parse(block.Text) as JsonObject).FirstOrDefault();
        if (envelope == null || (int?)envelope["schemaVersion"] != 4 || envelope["ok"] == null || envelope["meta"] is not JsonObject)
            throw new InvalidDataException("Expected a V4 tool envelope.");
        _ = TiaOpenness.Core.EngineToolResult.Read(envelope.ToJsonString());
        foreach (var text in result.Content.OfType<TextContentBlock>())
            if (!JsonNode.DeepEquals(envelope, JsonNode.Parse(text.Text))) throw new InvalidDataException("V4 text and structured content disagree.");
        if (result.IsError == (bool)envelope["ok"]!) throw new InvalidDataException("V4 error flag disagrees with the envelope.");
        return (JsonObject)envelope.DeepClone();
    }
    private async Task CloseConnection()
    {
        await closing.WaitAsync().ConfigureAwait(false);
        try
        {
            if (notifications != null) await notifications.DisposeAsync().ConfigureAwait(false); notifications = null;
            var endpoint = client; client = null;
            try { if (endpoint != null) await endpoint.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                pipe?.Dispose(); pipe = null;
                SessionKey = null; attachedHost = null;
                lock (sync) schemas.Clear();
            }
        }
        finally { closing.Release(); }
    }
    public async Task DetachAsync()
    {
        CancelPending();
        watching?.Cancel();
        if (watcher != null) await watcher.ConfigureAwait(false);
        watcher = null; watching?.Dispose(); watching = null;
        try { await CloseConnection().ConfigureAwait(false); }
        finally { await ReleaseOwnedHost().ConfigureAwait(false); }
        Detached?.Invoke(this, EventArgs.Empty);
    }
    private Task ReleaseOwnedHost()
    {
        lock (sync)
        {
            // Window exit must join an explicit detach that has already taken the process handle.
            if (releasingHost is { IsCompleted: false }) return releasingHost;
            var host = Interlocked.Exchange(ref ownedHost, null);
            return host == null ? Task.CompletedTask : releasingHost = WaitForOwnedHostExit(host);
        }
    }
    private async Task WaitForOwnedHostExit(IOwnedOperatorHost host)
    {
        try
        {
            // The stream is closed first. Allow the host's 10 s detach/release budget and a margin before any kill.
            using var deadline = new CancellationTokenSource(OwnedHostExitTimeout);
            try { await host.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                if (!host.HasExited)
                {
                    Trace.TraceWarning("Forced termination of owned operator host {0} after {1} seconds waiting for shutdown.", host.Id, OwnedHostExitTimeout.TotalSeconds);
                    host.Terminate();
                }
            }
        }
        finally { host.Dispose(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await DetachAsync().ConfigureAwait(false);
        requests.Dispose(); closing.Dispose();
    }
    public void CancelPending()
    {
        CancellationTokenSource previous;
        lock (sync) { previous = requests; requests = new(); }
        previous.Cancel(); previous.Dispose();
    }
}

internal interface IOwnedOperatorHost : IDisposable
{
    public int Id { get; }
    public bool HasExited { get; }
    public Task WaitForExitAsync(CancellationToken token);
    public void Terminate();
}
internal sealed class OwnedOperatorHost(Process process) : IOwnedOperatorHost
{
    public int Id => process.Id;
    public bool HasExited => process.HasExited;
    public Task WaitForExitAsync(CancellationToken token) => process.WaitForExitAsync(token);
    public void Terminate() { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    public void Dispose() => process.Dispose();
}
