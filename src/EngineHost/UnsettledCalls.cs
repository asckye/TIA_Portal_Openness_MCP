using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;

namespace TiaMcp.FoundationHost;

public sealed record UnsettledCall(string RequestId, string Tool, string? Digest, DateTimeOffset Utc, bool FromPreviousOwner = false);

// Both host routes share this store. Unknown results record evidence without refusing another call.
public sealed class UnsettledCalls
{
    private static readonly ConditionalWeakTable<object, UnsettledCalls> Stores = new();
    private readonly object gate = new();
    private readonly List<UnsettledCall> calls = new();
    private readonly HashSet<string> ids = new(StringComparer.Ordinal);
    private readonly HashSet<string> read = new(StringComparer.Ordinal);
    private string? projectFile;
    public static UnsettledCalls For(object worker) => Stores.GetValue(worker, _ => new UnsettledCalls());

    public void Record(string requestId, string tool, string? digest, DateTimeOffset utc)
    {
        lock (gate)
            if (ids.Add(requestId)) calls.Add(new UnsettledCall(requestId, tool, digest, utc.ToUniversalTime()));
        LateCalls.ObserveUnsettled(requestId);
    }

    public IReadOnlyList<UnsettledCall> Snapshot()
    {
        lock (gate) return calls.ToArray();
    }

    public void ImportPreviousOwner(IEnumerable<string> requestIds)
    {
        lock (gate)
            foreach (string requestId in requestIds)
                if (ids.Add(requestId)) calls.Add(new UnsettledCall(requestId, "previous-owner", null, DateTimeOffset.UtcNow, true));
    }

    public void Clear()
    {
        lock (gate)
        {
            calls.Clear();
            ids.Clear();
            read.Clear();
            projectFile = null;
        }
    }

    public void ProjectChanged(string? file)
    {
        if (string.IsNullOrEmpty(file)) return;
        file = Path.GetFullPath(file);
        lock (gate)
        {
            if (projectFile != null && !string.Equals(projectFile, file, StringComparison.OrdinalIgnoreCase))
            { calls.Clear(); ids.Clear(); read.Clear(); }
            projectFile = file;
        }
    }
    public void ObserveProjectAfterCall(string? file)
    {
        try { ProjectChanged(file); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Keep the prior project's evidence when the returned binding cannot identify a project.
            return;
        }
    }
    public JsonArray ToJson() => new(Snapshot().Select(call => (JsonNode)new JsonObject {
        ["requestId"] = call.RequestId, ["tool"] = call.Tool, ["utc"] = call.Utc.ToString("O"),
        ["fromPreviousOwner"] = call.FromPreviousOwner }).ToArray());

    public static bool HasUnknownOutcome(JsonNode body) => (string?)body["meta"]?["outcome"] == "unknown"
        || (string?)body["error"]?["code"] == "LIMIT_EXCEEDED" && (bool?)body["data"]?["nativeOutcomeUnknown"] == true;

    public static string WriteDigest(string tool, JsonObject arguments, string? projectFile)
        => global::TiaMcp.FoundationHost.WriteDigest.Create(tool, arguments, projectFile);

    public bool IsUnread(string requestId) { lock (gate) return ids.Contains(requestId) && !read.Contains(requestId); }
    public bool HasUnread(IEnumerable<string> ids)
    { lock (gate) return ids.Any(id => this.ids.Contains(id) && !read.Contains(id)); }
    public void MarkRead(string requestId) { lock (gate) read.Add(requestId); }
    public void Settle(string requestId)
    {
        lock (gate)
        {
            calls.RemoveAll(call => call.RequestId == requestId);
            ids.Remove(requestId);
            read.Remove(requestId);
        }
    }
    public UnsettledCall? FindWrite(string digest)
    { lock (gate) return calls.FirstOrDefault(call => call.Digest == digest && !read.Contains(call.RequestId)); }

    public static bool IsReviewedApply(JsonObject arguments) => !HostBehavior.IsPreview(arguments, true)
        && !string.IsNullOrWhiteSpace((string?)arguments["expectedPlanHash"] ?? (string?)arguments["expectedInventoryHash"]);

    public UnsettledCall? FindReviewedApply(string tool, JsonObject arguments, string? projectFile)
    {
        if (!IsReviewedApply(arguments)) return null;
        string digest = WriteDigest(tool, arguments, projectFile);
        return FindWrite(digest);
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
