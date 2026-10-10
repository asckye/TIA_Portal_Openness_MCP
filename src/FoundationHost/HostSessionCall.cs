namespace TiaMcp.FoundationHost;

internal sealed class HostSessionCall : IDisposable
{
    private static readonly AsyncLocal<HostSessionCall?> Current = new();
    private readonly HostSessionCall? previous = Current.Value;
    private readonly Action<bool, string, string, string> changed;
    private readonly Action<string, string> ended;
    private readonly string tool, actor, id;
    private HostSessionCall(Action<bool, string, string, string> changed, Action<string, string> ended, string tool, string actor, string id)
    { this.changed = changed; this.ended = ended; this.tool = tool; this.actor = actor; this.id = id; Current.Value = this; }
    internal static IDisposable Enter(Action<bool, string, string, string> changed, Action<string, string> ended, string tool, string actor, string id)
        => new HostSessionCall(changed, ended, tool, actor, id);
    internal static void LaneChanged(bool held)
    {
        var call = Current.Value;
        call?.changed(held, call.tool, call.actor, call.id);
    }
    internal static void RequestIdChanged(string id)
    { var call = Current.Value; call?.changed(true, call.tool, call.actor, id); }
    internal static void Ended(string reason)
    { var call = Current.Value; call?.ended(call.actor, reason); }
    public void Dispose() => Current.Value = previous;
}
