using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaOpenness.Gui.Services;

// The environment fix runs the selected release's bundled host as `doctor --fix --json`. The doctor's exit code reports
// readiness (2 = not ready, also after a successful join that waits for a new logon), so the fix result comes from the JSON
// object. FoundationHost.Tests compiles this file to check these arguments with HostCliOptions and to read the host's object.
public enum DoctorFixState { Ready, NeedsLogon, Failed }

public sealed record DoctorRun(int ExitCode, string Output, string Errors);

public sealed record DoctorFixReport(bool Ready, bool GroupOk, string? Outcome, string? Code, string? Message, IReadOnlyList<string> Lines)
{
    public DoctorFixState State => Outcome switch
    {
        "Ready" => DoctorFixState.Ready,
        "PendingLogon" or "AddedPendingLogon" => DoctorFixState.NeedsLogon,
        // The engine helper returns only a bool; the recheck after the fix reads the actual membership.
        "Unknown" when GroupOk => DoctorFixState.Ready,
        _ => DoctorFixState.Failed,
    };
}

public static class DoctorFixCommand
{
    public static IReadOnlyList<string> Arguments(string releaseKey) =>
        ["doctor", "--fix", "--json", "--release-key", releaseKey, "--logging", "0"];

    // Null unless the output is the doctor's JSON object (for example a crash or a rejected command line); the exit code decides then.
    public static DoctorFixReport? Parse(string output)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(output); }
        catch (JsonException) /* swallow(parse-fallback): output without the doctor's JSON object falls back to the exit code */ { return null; }
        if (root is not JsonObject report || Flag(report["ready"]) is not bool ready
            || report["group"] is not JsonObject group || Flag(group["ok"]) is not bool groupOk) return null;
        var lines = (report["report"] as JsonArray ?? new JsonArray()).Select(Text).OfType<string>().ToArray();
        return new DoctorFixReport(ready, groupOk, Text(group["outcome"]), Text(group["code"]), Text(group["message"]), lines);
    }

    private static bool? Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
