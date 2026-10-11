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
    string? SessionKey { get; }
    JsonObject? State { get; }
    JsonObject? ToolSchema(string tool);
    event EventHandler<JsonObject>? StateChanged;
    event EventHandler? Detached;
    Task<IReadOnlyList<OperatorSession>> DiscoverAsync(string releaseKey, CancellationToken token = default);
    Task AttachAsync(OperatorSession session, CancellationToken token = default);
    Task CreateAsync(string releaseKey, CancellationToken token = default);
    Task DetachAsync();
    void CancelPending();
    Task<JsonObject> CallAsync(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken token = default);
}
