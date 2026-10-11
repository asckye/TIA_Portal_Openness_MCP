using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TiaOpenness.Gui.Services;

/// <summary>The offline demo has no discovery, transport or host launch capability.</summary>
internal sealed class OfflineSessionClient : IMcpSessionClient
{
    public string? SessionKey => null;
    public JsonObject? State => null;
    public JsonObject? ToolSchema(string tool) => null;
    public event EventHandler<JsonObject>? StateChanged { add { } remove { } }
    public event EventHandler? Detached { add { } remove { } }
    public Task<IReadOnlyList<OperatorSession>> DiscoverAsync(string releaseKey, CancellationToken token = default)
        => Task.FromResult<IReadOnlyList<OperatorSession>>([]);
    private static InvalidOperationException Refused() => new("Offline demo: session operations are unavailable.");
    public Task AttachAsync(OperatorSession session, CancellationToken token = default) => throw Refused();
    public Task CreateAsync(string releaseKey, CancellationToken token = default) => throw Refused();
    public Task<JsonObject> CallAsync(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken token = default) => throw Refused();
    public Task DetachAsync() => Task.CompletedTask;
    public void CancelPending() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
