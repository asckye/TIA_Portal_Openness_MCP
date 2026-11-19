using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;

namespace TiaMcp.FoundationHost;

public sealed record UnsettledCall(string RequestId, string Tool, string? Digest, DateTimeOffset Utc, bool FromPreviousOwner = false,
    SettlementTarget? Target = null)
{
    internal (int? Id, string? StartUtc) Process { get; init; }
}

// Both host routes share this store. Unknown results record evidence without refusing another call.
public sealed class UnsettledCalls
{
    private static readonly ConditionalWeakTable<object, UnsettledCalls> Stores = new();
    private readonly object gate = new();
    private readonly List<UnsettledCall> calls = new();
    private readonly HashSet<string> ids = new(StringComparer.Ordinal);
    private readonly HashSet<string> read = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CallSettlement> settled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UnsettledCall> guardedWrites = new(StringComparer.Ordinal);
    private string? projectFile;
    private string? lastRecoverySettlement;
    private (int? Id, string? StartUtc) process;
    public static UnsettledCalls For(object worker) => Stores.GetValue(worker, _ => new UnsettledCalls());

    public void Record(string requestId, string tool, string? digest, DateTimeOffset utc, JsonObject? arguments = null)
    {
        lock (gate)
        {
            if (settled.ContainsKey(requestId)) return;
            if (ids.Add(requestId)) calls.Add(new UnsettledCall(requestId, tool, digest, utc.ToUniversalTime(),
                Target: arguments == null ? null : UnknownSettlement.Capture(tool, arguments)) { Process = process });
        }
        LateCalls.ObserveUnsettled(requestId);
    }

    public IReadOnlyList<UnsettledCall> Snapshot()
    {
        lock (gate) return calls.ToArray();
    }

    internal IReadOnlyList<UnsettledCall> RecoverySettlements(int limit)
    {
        lock (gate)
        {
            var eligible = calls.Where(call => call.Target != null).ToList();
            int start = eligible.FindIndex(call => call.RequestId == lastRecoverySettlement) + 1;
            var selected = eligible.Skip(start).Concat(eligible.Take(start)).Take(limit).ToArray();
            if (selected.Length > 0) lastRecoverySettlement = selected.Last().RequestId;
            return selected;
        }
    }

    public void BindProcess(int? pid, string? startUtc) { lock (gate) process = (pid, startUtc); }

    public IReadOnlyList<CallSettlement> SettleExitedProcess(int pid, string? startUtc)
    {
        lock (gate)
        {
            var exited = calls.Where(call => call.Process == (pid, startUtc)).ToArray();
            var results = new List<CallSettlement>(exited.Length);
            foreach (var call in exited)
            {
                read.Remove(call.RequestId);
                results.Add(SettleCall(call, CallSettlement.ProcessExited));
            }
            return results;
        }
    }

    public JsonArray ImportPreviousOwner(IEnumerable<string> requestIds)
    {
        lock (gate)
        {
            var local = calls.Where(call => !call.FromPreviousOwner).Select(call => call.RequestId).ToHashSet(StringComparer.Ordinal);
            var rows = new JsonArray();
            foreach (string id in requestIds.Distinct(StringComparer.Ordinal))
            {
                if (settled.ContainsKey(id)) continue;
                if (!ids.Contains(id)) AddPreviousOwner(id, process);
                rows.Add(new JsonObject { ["requestId"] = id, ["origin"] = local.Contains(id) ? "this-session" : "previous-owner" });
            }
            return rows;
        }
    }

    public void ImportPreviousOwner(IEnumerable<UnsettledCall> previous)
    {
        lock (gate)
            foreach (var call in previous) AddPreviousOwner(call.RequestId, call.Process);
    }

    private void AddPreviousOwner(string requestId, (int? Id, string? StartUtc) owner)
    {
        if (!settled.ContainsKey(requestId) && ids.Add(requestId))
            calls.Add(new UnsettledCall(requestId, "previous-owner", null, DateTimeOffset.UtcNow, true) { Process = owner });
    }

    public void Clear()
    {
        lock (gate)
        {
            calls.Clear();
            ids.Clear();
            read.Clear();
            settled.Clear();
            guardedWrites.Clear();
            projectFile = null;
            process = default;
            lastRecoverySettlement = null;
        }
    }

    public void ProjectChanged(string? file)
    {
        if (string.IsNullOrEmpty(file)) return;
        file = Path.GetFullPath(file);
        lock (gate)
        {
            if (projectFile != null && !string.Equals(projectFile, file, StringComparison.OrdinalIgnoreCase))
            { calls.Clear(); ids.Clear(); read.Clear(); settled.Clear(); guardedWrites.Clear(); lastRecoverySettlement = null; }
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
        ["fromPreviousOwner"] = call.FromPreviousOwner, ["origin"] = call.FromPreviousOwner ? "previous-owner" : "this-session" }).ToArray());

    public JsonArray SettledJson()
    { lock (gate) return new JsonArray(settled.Values.Select(value => (JsonNode)value.ToJson()).ToArray()); }
    public CallSettlement? Settlement(string requestId)
    { lock (gate) return settled.GetValueOrDefault(requestId); }
    public CallSettlement? SettleByReadBack(string requestId, string outcome)
    {
        lock (gate)
        {
            var call = calls.FirstOrDefault(item => item.RequestId == requestId);
            return call == null ? settled.GetValueOrDefault(requestId) : SettleCall(call, outcome);
        }
    }

    private CallSettlement SettleCall(UnsettledCall call, string outcome)
    {
        var result = new CallSettlement(call.RequestId, call.Tool, outcome, DateTimeOffset.UtcNow);
        calls.Remove(call);
        settled[call.RequestId] = result;
        if (outcome is "applied" or CallSettlement.ProcessExited) guardedWrites[call.RequestId] = call;
        else { ids.Remove(call.RequestId); read.Remove(call.RequestId); }
        return result;
    }

    public static bool HasUnknownOutcome(JsonNode body) => (string?)body["meta"]?["outcome"] == "unknown"
        || (string?)body["error"]?["code"] == "LIMIT_EXCEEDED" && (bool?)body["data"]?["nativeOutcomeUnknown"] == true;

    public static string WriteDigest(string tool, JsonObject arguments, string? projectFile)
        => global::TiaMcp.FoundationHost.WriteDigest.Create(tool, arguments, projectFile);

    public bool IsUnread(string requestId) { lock (gate) return ids.Contains(requestId) && !read.Contains(requestId); }
    public bool HasUnread(IEnumerable<string> ids)
    { lock (gate) return ids.Any(id => this.ids.Contains(id) && !read.Contains(id)); }
    public void MarkRead(string requestId) { lock (gate) { read.Add(requestId); guardedWrites.Remove(requestId); } }
    public void Settle(string requestId)
    {
        lock (gate)
        {
            calls.RemoveAll(call => call.RequestId == requestId);
            ids.Remove(requestId);
            read.Remove(requestId);
            guardedWrites.Remove(requestId);
        }
    }
    public UnsettledCall? FindWrite(string digest)
    { lock (gate) return calls.Concat(guardedWrites.Values).FirstOrDefault(call => call.Digest == digest && !read.Contains(call.RequestId)); }

    public JsonArray SaveWarnings()
    {
        lock (gate)
        {
            var warnings = ToJson();
            foreach (var id in guardedWrites.Keys)
                if (settled[id].Outcome == CallSettlement.ProcessExited) warnings.Add(settled[id].ToJson());
            return warnings;
        }
    }

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
        var unsettled = beforeSave is { Count: > 0 } ? beforeSave : SaveWarnings();
        if (unsettled.Count == 0 || body["meta"]?["warnings"]?.AsArray().Any(w => (string?)w?["code"] == "UNSETTLED_CALLS") == true) return body;
        return EnvelopeMutation.AddWarning(body, new Warning(WarningCode.UnsettledCalls,
            "Earlier calls have an unknown outcome. Inspect their targets before saving or repeating them; never replay them automatically.",
            new Dictionary<string, JsonElement> { ["unsettled"] = JsonSerializer.SerializeToElement(unsettled) }));
    }
}
