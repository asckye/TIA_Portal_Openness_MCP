using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.Generation;
using TiaMcp.Logic.V4;

namespace TiaMcp.FoundationHost;

public sealed record UnsettledCall(string RequestId, string Tool, string Digest, DateTimeOffset Utc, bool FromPreviousOwner = false);

// Both host routes share this store. Unknown results record evidence without refusing another call.
public sealed class UnsettledCalls
{
    private static readonly ConditionalWeakTable<object, UnsettledCalls> Stores = new();
    private readonly object gate = new();
    private readonly List<UnsettledCall> calls = new();
    private string? projectFile;
    public static UnsettledCalls For(object worker) => Stores.GetValue(worker, _ => new UnsettledCalls());

    public void Record(string requestId, string tool, string digest, DateTimeOffset utc)
    {
        lock (gate)
            if (!calls.Any(call => call.RequestId == requestId)) calls.Add(new UnsettledCall(requestId, tool, digest, utc.ToUniversalTime()));
    }

    public IReadOnlyList<UnsettledCall> Snapshot()
    {
        lock (gate) return calls.ToArray();
    }

    public void Clear()
    {
        lock (gate)
        {
            calls.Clear();
            projectFile = null;
        }
    }

    public void ProjectChanged(string? file)
    {
        if (string.IsNullOrEmpty(file)) return;
        file = Path.GetFullPath(file);
        lock (gate)
        {
            if (projectFile != null && !string.Equals(projectFile, file, StringComparison.OrdinalIgnoreCase)) calls.Clear();
            projectFile = file;
        }
    }
    public JsonArray ToJson() => new(Snapshot().Select(call => (JsonNode)new JsonObject {
        ["requestId"] = call.RequestId, ["tool"] = call.Tool, ["utc"] = call.Utc.ToString("O"),
        ["fromPreviousOwner"] = call.FromPreviousOwner }).ToArray());

    public static bool HasUnknownOutcome(JsonNode body) => (string?)body["meta"]?["outcome"] == "unknown"
        || (string?)body["error"]?["code"] == "LIMIT_EXCEEDED" && (bool?)body["data"]?["nativeOutcomeUnknown"] == true;

    public static string WriteDigest(string tool, JsonObject arguments, string? projectFile)
    {
        var canonical = (JsonObject)arguments.DeepClone();
        canonical.Remove("dryRun"); canonical.Remove("confirm");
        return CanonicalJson.Hash(JsonSerializer.SerializeToElement(new JsonObject {
            ["tool"] = tool, ["arguments"] = canonical,
            ["projectFile"] = string.IsNullOrEmpty(projectFile) ? null : Path.GetFullPath(projectFile).ToUpperInvariant() }));
    }

    public static bool IsReviewedApply(JsonObject arguments) => (bool?)arguments["dryRun"] == false
        && !string.IsNullOrWhiteSpace((string?)arguments["expectedPlanHash"] ?? (string?)arguments["expectedInventoryHash"]);

    public UnsettledCall? FindReviewedApply(string tool, JsonObject arguments, string? projectFile)
    {
        if (!IsReviewedApply(arguments)) return null;
        string digest = WriteDigest(tool, arguments, projectFile);
        lock (gate) return calls.FirstOrDefault(call => call.Digest == digest);
    }

    public JsonNode WarnOnSave(string tool, JsonNode body, JsonArray? beforeSave = null)
    {
        if (tool is not ("SaveProject" or "SaveProjectCopy")) return body;
        var unsettled = beforeSave is { Count: > 0 } ? beforeSave : ToJson();
        if (unsettled.Count == 0 || body["meta"]?["warnings"]?.AsArray().Any(w => (string?)w?["code"] == "UNSETTLED_CALLS") == true) return body;
        return EnvelopeMutation.AddWarning(body, new Warning(WarningCode.UnsettledCalls,
            "Earlier calls have an unknown outcome. Inspect their targets before saving or repeating them; never replay them automatically.",
            new Dictionary<string, JsonElement> { ["unsettled"] = JsonSerializer.SerializeToElement(unsettled) }));
    }
}
