using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;

namespace TiaMcp.FoundationHost;

// The response may end while its invocation still owns the lane and its complete processing pipeline.
public sealed class LateCalls
{
    private static readonly ConditionalWeakTable<object, LateCalls> Stores = new();
    private static readonly AsyncLocal<Invocation?> Current = new();
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> completed = new();
    private Entry? running;
    private readonly Dictionary<string, string> unreadWrites = new(StringComparer.Ordinal);
    public static LateCalls For(object owner) => Stores.GetValue(owner, _ => new LateCalls());
    public bool ContinuesCurrentCall => Current.Value is { Detached: true } call && ReferenceEquals(call.owner, this);
    public static Func<bool> CaptureProgress() { var call = Current.Value; return () => call?.Detached != true; }
    public static void Detach(JsonObject running) => Current.Value?.Detach(running);
    public static void IdentifyWrite(string digest) { if (Current.Value is { } call) call.Digest ??= digest; }
    public static void ObserveUnsettled(string id)
    { if (Current.Value is { } call) { lock (call.owner.gate) call.UnsettledIds.Add(id); } }
    public Invocation CreateInvocation(string tool, string requestId, CancellationToken token) => new(this, tool, requestId, token);

    public JsonObject? Running
    {
        get { lock (gate) return running?.Running.DeepClone().AsObject(); }
    }
    public JsonArray Snapshot()
    {
        lock (gate) return new JsonArray(completed.Select(entry => (JsonNode)new JsonObject {
            ["requestId"] = (string?)entry.Running["requestId"], ["tool"] = (string?)entry.Running["tool"],
            ["outcome"] = (string?)entry.Result!.StructuredContent?["meta"]?["outcome"], ["read"] = entry.Read }).ToArray());
    }
    public string? FindUnread(string digest)
    {
        lock (gate) return unreadWrites.TryGetValue(digest, out var id) ? id : null;
    }
    public void RecordUnknown(string id, string tool, string digest, CallToolResult result)
    {
        if (Current.Value is { } call && ReferenceEquals(call.owner, this)) return;
        lock (gate)
        {
            if (entries.ContainsKey(id)) return;
            var entry = new Entry(new JsonObject { ["requestId"] = id, ["tool"] = tool,
                ["sinceUtc"] = DateTimeOffset.UtcNow.ToString("O") }, digest);
            entry.Result = result;
            entry.Summary = OutcomeSummary(result, entry.Running);
            Index(entry);
            entry.Completed = completed.AddLast(entry);
            unreadWrites[digest] = id;
            Trim();
        }
    }
    public CallToolResult? Read(string release, string id, UnsettledCalls unsettled)
    {
        lock (gate)
        {
            entries.TryGetValue(id, out var entry);
            if (entry == null)
            {
                var unknown = unsettled.Snapshot().FirstOrDefault(call => call.RequestId == id && unsettled.IsUnread(id));
                if (unknown == null) return null;
                var meta = new Meta(unknown.Utc, release, unknown.Tool, id, Outcome.Unknown, Execution.Unknown, false,
                    BehaviorPolicy.Current, Completeness.Unknown, null, new[] { new Warning(WarningCode.UnverifiedBehavior,
                        "Native behavior has not been verified on a real machine.", new Dictionary<string, System.Text.Json.JsonElement>()) });
                var body = JsonNode.Parse(V4Json.Serialize(Envelope.Create(new JsonObject(),
                    new Error("The call has an unknown outcome. Inspect its target before repeating it.",
                        new OutcomeUnknownDetails("tool-call", new Dictionary<string, System.Text.Json.JsonElement>())), meta)))!.AsObject();
                var result = new CallToolResult { StructuredContent = body };
                unsettled.MarkRead(id);
                return SummaryResult(OutcomeSummary(result, new JsonObject { ["sinceUtc"] = unknown.Utc.ToString("O") }));
            }
            if (entry.Summary == null) return InProgress(release, (string)entry.Running["tool"]!, entry.Running);
            entry.Read = true;
            if (entry.Digest != null && unreadWrites.TryGetValue(entry.Digest, out var guardedId) && entry.Ids.Contains(guardedId))
                unreadWrites.Remove(entry.Digest);
            foreach (string alias in entry.Ids)
                if (UnsettledCalls.HasUnknownOutcome(entry.Summary) || unsettled.IsUnread(alias)) unsettled.MarkRead(alias);
            var selected = entry.Result == null ? SummaryResult(entry.Summary) : Copy(entry.Result);
            if (entry.Result == null) Remove(entry);
            return selected;
        }
    }
    public void Clear() { lock (gate) { entries.Clear(); completed.Clear(); running = null; unreadWrites.Clear(); } }
    private void Index(Entry entry)
    {
        foreach (string id in entry.Ids) entries[id] = entry;
    }
    private void Remove(Entry entry)
    {
        foreach (string id in entry.Ids)
            if (entries.TryGetValue(id, out var indexed) && ReferenceEquals(indexed, entry)) entries.Remove(id);
    }
    private void Trim()
    {
        while (completed.Count > 16)
        {
            var entry = completed.First!.Value;
            completed.RemoveFirst();
            entry.Completed = null;
            entry.Result = null;
            bool guarded = entry.Digest != null && unreadWrites.TryGetValue(entry.Digest, out var id) && entry.Ids.Contains(id);
            if (entry.Read || !guarded && entry.Unsettled?.HasUnread(entry.Ids) != true) Remove(entry);
        }
    }
    private static JsonObject OutcomeSummary(CallToolResult result, JsonObject running)
    {
        var body = TiaMcpServer.ModelContextProtocol.McpServer.ResultBody(result)!;
        var meta = body["meta"]?.DeepClone();
        if (meta is JsonObject metadata)
        {
            metadata["paging"] = null;
            if (metadata["warnings"] is JsonArray warnings)
                foreach (var warning in warnings.OfType<JsonObject>()) warning["details"] = new JsonObject();
        }
        var error = body["error"]?.DeepClone();
        if (error?["details"] is JsonObject details && details.ContainsKey("evidence")) details["evidence"] = new JsonObject();
        return new JsonObject { ["schemaVersion"] = body["schemaVersion"]?.DeepClone() ?? JsonValue.Create(4),
            ["ok"] = body["ok"]?.DeepClone() ?? JsonValue.Create((string?)body["meta"]?["outcome"] == "succeeded"),
            ["meta"] = meta, ["error"] = error,
            ["data"] = new JsonObject { ["tool"] = body["meta"]?["tool"]?.DeepClone() ?? running["tool"]?.DeepClone(),
                ["outcome"] = body["meta"]?["outcome"]?.DeepClone(),
                ["utc"] = body["meta"]?["timestamp"]?.DeepClone() ?? running["sinceUtc"]?.DeepClone(),
                ["nativeOutcomeUnknown"] = body["data"]?["nativeOutcomeUnknown"]?.DeepClone(),
                ["fullResultEvicted"] = true, ["note"] = "The full result payload is unavailable. This stored outcome summary acknowledges the call. "
                    + "Inspect its target before repeating a write." } };
    }
    private static CallToolResult SummaryResult(JsonObject summary)
        => new() { IsError = (bool?)summary["ok"] != true, StructuredContent = summary.DeepClone(),
            Content = new[] { new TextContentBlock { Text = summary.ToJsonString() } } };
    private static CallToolResult Copy(CallToolResult result)
    {
        return new CallToolResult { IsError = result.IsError, StructuredContent = result.StructuredContent?.DeepClone(),
            Content = result.Content };
    }
    public static CallToolResult InProgress(string release, string tool, JsonObject running)
    {
        string id = (string)running["requestId"]!;
        var meta = new Meta(DateTimeOffset.UtcNow, release, tool, id, Outcome.InProgress, Execution.Unknown, false,
            BehaviorPolicy.Current, Completeness.Unknown, null, new[] { new Warning(WarningCode.UnverifiedBehavior,
                "Native behavior has not been verified on a real machine.", new Dictionary<string, System.Text.Json.JsonElement>()) });
        var envelope = Envelope.Create(new JsonObject { ["activeRequestId"] = id, ["running"] = running.DeepClone() },
            new Error("The call is still running. Do not repeat this call. Read GetCallResult(requestId). "
                + (string?)running["message"], new OperationInProgressDetails(id)), meta);
        var body = JsonNode.Parse(V4Json.Serialize(envelope));
        return new CallToolResult { IsError = true, StructuredContent = body,
            Content = new[] { new TextContentBlock { Text = body!.ToJsonString() } } };
    }
    public static Error BusyError(JsonObject running) => new(
        $"Worker call {running["tool"]} (request {running["requestId"]}, started {running["sinceUtc"]}) is still running. "
            + "Read GetCallResult(requestId) before repeating it.",
        new PreconditionFailedDetails("worker-call-running", (string?)running["requestId"]));
    private sealed class Entry(JsonObject running, string? digest)
    {
        internal readonly JsonObject Running = (JsonObject)running.DeepClone();
        internal readonly HashSet<string> Ids = new(StringComparer.Ordinal) { (string)running["requestId"]! };
        internal readonly string? Digest = digest;
        internal CallToolResult? Result;
        internal JsonObject? Summary;
        internal bool Read;
        internal LinkedListNode<Entry>? Completed;
        internal UnsettledCalls? Unsettled;
    }
    public sealed class Invocation : IDisposable
    {
        internal readonly LateCalls owner;
        private readonly string tool, id;
        private readonly CancellationTokenSource cancellation = new();
        private readonly CancellationTokenRegistration registration;
        private readonly TaskCompletionSource<JsonObject> detached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Entry? entry;
        private int isDetached;
        internal string? Digest;
        internal readonly HashSet<string> UnsettledIds = new(StringComparer.Ordinal);
        public bool Detached => Volatile.Read(ref isDetached) != 0;
        public Task<JsonObject> Detachment => detached.Task;
        public CancellationToken Token => cancellation.Token;
        internal Invocation(LateCalls owner, string tool, string id, CancellationToken token)
        {
            this.owner = owner; this.tool = tool; this.id = id;
            registration = token.Register(() => cancellation.Cancel());
        }
        public IDisposable Enter()
        {
            var previous = Current.Value;
            Current.Value = this;
            return new Scope(() => Current.Value = previous);
        }
        internal void Detach(JsonObject running)
        {
            if (Interlocked.Exchange(ref isDetached, 1) != 0) return;
            registration.Dispose();
            lock (owner.gate)
            {
                entry = new Entry(running, Digest);
                entry.Ids.Add(id);
                owner.Index(entry);
                owner.running = entry;
                if (Digest != null) owner.unreadWrites[Digest] = (string)running["requestId"]!;
            }
            detached.TrySetResult((JsonObject)running.DeepClone());
        }
        public void Complete(CallToolResult result, UnsettledCalls? unsettled = null)
        {
            lock (owner.gate)
            {
                if (tool == "GetCallResult" && entry == null) return;
                string resultId = (string?)result.StructuredContent?["meta"]?["requestId"] ?? id;
                if (entry == null)
                {
                    if (result.StructuredContent == null || !UnsettledCalls.HasUnknownOutcome(result.StructuredContent) && UnsettledIds.Count == 0) return;
                    owner.entries.TryGetValue(resultId, out entry);
                    if (entry == null)
                    {
                        entry = new Entry(new JsonObject { ["requestId"] = resultId, ["tool"] = tool,
                            ["sinceUtc"] = DateTimeOffset.UtcNow.ToString("O") }, Digest);
                        owner.Index(entry);
                    }
                }
                entry.Ids.Add(resultId);
                entry.Ids.UnionWith(UnsettledIds);
                owner.Index(entry);
                if (ReferenceEquals(owner.running, entry)) owner.running = null;
                if (entry.Completed != null) owner.completed.Remove(entry.Completed);
                entry.Completed = owner.completed.AddLast(entry);
                entry.Unsettled = unsettled;
                entry.Result = Copy(result);
                entry.Summary = OutcomeSummary(result, entry.Running);
                if (entry.Digest != null && !entry.Read) owner.unreadWrites[entry.Digest] = (string)entry.Running["requestId"]!;
                if (result.StructuredContent != null)
                {
                    if (UnsettledCalls.HasUnknownOutcome(result.StructuredContent))
                    {
                        if (entry.Digest != null) unsettled?.Record(resultId, tool, entry.Digest, DateTimeOffset.UtcNow);
                    }
                    else foreach (string alias in entry.Ids.Where(alias => !UnsettledIds.Contains(alias))) unsettled?.Settle(alias);
                }
                owner.Trim();
            }
        }
        public void Dispose() { registration.Dispose(); cancellation.Dispose(); }
    }
    private sealed class Scope(Action restore) : IDisposable { public void Dispose() => restore(); }
}
