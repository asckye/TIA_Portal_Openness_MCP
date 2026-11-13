using System;
using System.IO;
using System.Collections.Concurrent;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.Generation;

namespace TiaMcp.FoundationHost;

public static class WriteDigest
{
    private static readonly AsyncLocal<PreparedCall?> Current = new();
    public static IDisposable Enter()
    {
        var previous = Current.Value;
        Current.Value ??= new PreparedCall();
        return new Scope(() => Current.Value = previous);
    }
    public static string Capture(string tool, JsonObject arguments, string? projectFile)
    {
        string key = Key(tool, arguments);
        var call = Current.Value;
        string digest = call == null ? Create(tool, arguments, projectFile)
            : call.Digests.GetOrAdd(key, _ => Create(tool, arguments, projectFile));
        if (call != null) call.Latest[tool] = digest;
        return digest;
    }
    public static string? Prepared(string tool, JsonObject arguments)
        => Current.Value?.Digests.TryGetValue(Key(tool, arguments), out var digest) == true ? digest : null;
    public static string? ForOutcome(string tool, JsonObject arguments, string? projectFile)
    {
        try { return Prepared(tool, arguments) ?? Create(tool, arguments, projectFile); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An invalid identity removes the replay guard, while the unknown outcome is still retained.
            return null;
        }
    }
    public static string? Latest(string tool)
        => Current.Value?.Latest.TryGetValue(tool, out var digest) == true ? digest : null;
    private static string Key(string tool, JsonObject arguments)
        => tool + "\n" + CanonicalJson.Hash(JsonSerializer.SerializeToElement(arguments));
    private sealed class PreparedCall
    {
        internal readonly ConcurrentDictionary<string, string> Digests = new(StringComparer.Ordinal);
        internal readonly ConcurrentDictionary<string, string> Latest = new(StringComparer.Ordinal);
    }
    private sealed class Scope(Action restore) : IDisposable { public void Dispose() => restore(); }
    public static string Create(string tool, JsonObject arguments, string? projectFile)
    {
        var canonical = TiaOpenness.Shared.ApprovalOutputTargets.IdentityArguments(tool, arguments);
        canonical.Remove("dryRun");
        canonical.Remove("confirm");
        return CanonicalJson.Hash(JsonSerializer.SerializeToElement(new JsonObject {
            ["tool"] = tool, ["arguments"] = canonical,
            ["projectFile"] = string.IsNullOrEmpty(projectFile) ? null : Path.GetFullPath(projectFile).ToUpperInvariant() }));
    }
}
