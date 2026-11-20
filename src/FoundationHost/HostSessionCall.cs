#if TIA_FOUNDATION_TEST_HOST
extern alias toolpipeline;
using LateCalls = toolpipeline::TiaMcp.FoundationHost.LateCalls;
#endif
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaOpenness.Shared;

namespace TiaMcp.FoundationHost;

internal sealed class HostSessionCall : IDisposable
{
    internal static async ValueTask<CallToolResult> Measure(TimeProvider clock, Func<ValueTask<CallToolResult>> invoke)
    {
        long started = clock.GetTimestamp();
        using var timing = new ToolCallTiming(() => clock.GetElapsedTime(started));
        return Stamp(await invoke().ConfigureAwait(false));
    }

    internal static CallToolResult Stamp(CallToolResult result)
    {
        if (result.StructuredContent is not JsonObject body || ToolCallTiming.Meta(body) is not { } meta
            || ToolCallTiming.Active?.TryWrite(meta) != true) return result;
        if (result.Content.FirstOrDefault() is TextContentBlock first)
        {
            var blocks = result.Content.ToArray();
            blocks[0] = new TextContentBlock { Text = body.ToJsonString(), Meta = first.Meta, Annotations = first.Annotations };
            result.Content = blocks;
        }
        return result;
    }

    private static readonly AsyncLocal<HostSessionCall?> Current = new();
    internal static string? SessionKey => Current.Value?.sessionKey;
    private readonly HostSessionCall? previous = Current.Value;
    private readonly Action<bool, string, string, string> changed;
    private readonly Action<string, string> ended;
    private readonly string tool, actor, id, sessionKey;
    private HostSessionCall(Action<bool, string, string, string> changed, Action<string, string> ended, string tool, string actor, string id, string sessionKey)
    {
        this.changed = changed; this.ended = ended; this.tool = tool; this.actor = actor; this.id = id;
        // Nested tool calls retain the initiating host session's takeover key.
        this.sessionKey = previous?.sessionKey ?? sessionKey; Current.Value = this;
    }
    internal static IDisposable Enter(Action<bool, string, string, string> changed, Action<string, string> ended, string tool, string actor, string id,
        string sessionKey) => new HostSessionCall(changed, ended, tool, actor, id, sessionKey);
    internal static void LaneChanged(bool held)
    {
        var call = Current.Value;
        call?.changed(held, call.tool, call.actor, call.id);
    }
    internal static void RequestIdChanged(string id)
    { var call = Current.Value; call?.changed(true, call.tool, call.actor, id); }
    internal static void Detach(System.Text.Json.Nodes.JsonObject running) => LateCalls.Detach(running);
    internal static void Ended(string reason)
    { var call = Current.Value; call?.ended(call.actor, reason); }
    public void Dispose() => Current.Value = previous;
}
