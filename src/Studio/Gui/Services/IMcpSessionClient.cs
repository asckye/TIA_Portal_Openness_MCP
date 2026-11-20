using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TiaOpenness.Gui.Services;

public sealed record OperatorHost(int Pid, long StartTicks, string ReleaseKey, string PipeName, string RegistrationPath);
public sealed record OperatorSession(OperatorHost Host, string SessionKey, JsonObject State)
{
    public string DisplayName => SessionKey[..8] + " · " + ((string?)State["participants"]?["mcpClient"] ?? "MCP") + " (unverified)";
}

public interface IMcpSessionClient : IAsyncDisposable
{
    public string? SessionKey { get; }
    public JsonObject? State { get; }
    public JsonObject? ToolSchema(string tool);
    public event EventHandler<JsonObject>? StateChanged;
    public event EventHandler? Detached;
    public Task<IReadOnlyList<OperatorSession>> DiscoverAsync(string releaseKey, CancellationToken token = default);
    public Task AttachAsync(OperatorSession session, CancellationToken token = default);
    public Task CreateAsync(string releaseKey, CancellationToken token = default);
    public Task DetachAsync();
    public void CancelPending();
    public Task<JsonObject> CallAsync(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken token = default);
}
