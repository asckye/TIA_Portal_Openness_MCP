using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
    private const int RetainedResults = 16;
    // Match the worker response frame limit without coupling the tool pipeline to the worker transport.
    private const int ResponseBytes = 16 * 1024 * 1024;
    private readonly Queue<(string Id, byte[] Result)> ordinary = new();
    private readonly LinkedList<Entry> completed = new();
    private Entry? running;
    private readonly Dictionary<string, string> unreadWrites = new(StringComparer.Ordinal);
    public static LateCalls For(object owner) => Stores.GetValue(owner, _ => new LateCalls());
    public bool ContinuesCurrentCall => Current.Value is { Detached: true } call && ReferenceEquals(call.owner, this);
    public static Func<bool> CaptureProgress() { var call = Current.Value; return () => call?.Detached != true; }
    public static void Detach(JsonObject running) => Current.Value?.Detach(running);
    public static void IdentifyWrite(string digest) { if (Current.Value is { } call) call.Digest ??= digest; }
    public static void ObserveUnsettled(string id)
    {
        if (Current.Value is { } call)
            lock (call.owner.gate) (call.UnsettledIds ??= new(StringComparer.Ordinal)).Add(id);
    }
    public Invocation CreateInvocation(string tool, string requestId, CancellationToken token) => new(this, tool, requestId, token);
    public Func<JsonObject, JsonObject>? DescribeRunning { get; set; }

    public void UpdateRunning(JsonObject details)
    {
        lock (gate)
        {
            if (running == null || (string?)running.Running["requestId"] != (string?)details["requestId"]) return;
            foreach (var field in details) running.Running[field.Key] = field.Value?.DeepClone();
        }
    }

    public JsonObject? Running
    {
        get
        {
            JsonObject? snapshot;
            lock (gate) snapshot = running?.Running.DeepClone().AsObject();
            return snapshot == null ? null : DescribeRunning?.Invoke(snapshot) ?? snapshot;
        }
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
    public void ObserveSettlement(CallSettlement settlement)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(settlement.RequestId, out var entry)) return;
            if (entry.Result?.StructuredContent is { } body)
            {
                var decorated = UnknownSettlement.Decorate(body, settlement);
                entry.Result = new CallToolResult { IsError = entry.Result.IsError, StructuredContent = decorated,
                    Content = new[] { new TextContentBlock { Text = decorated.ToJsonString() } } };
            }
            if (entry.Summary != null) entry.Summary = UnknownSettlement.Decorate(entry.Summary, settlement).AsObject();
            bool ownsResult = (string?)(entry.Result?.StructuredContent ?? entry.Summary)?["meta"]?["requestId"] == settlement.RequestId;
            if (ownsResult && settlement.Outcome == "not-applied" && entry.Digest != null
                && unreadWrites.TryGetValue(entry.Digest, out var id) && entry.Ids.Contains(id)) unreadWrites.Remove(entry.Digest);
        }
    }
    public void RecordUnknown(string id, string tool, string? digest, CallToolResult result)
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
            if (digest != null && (string?)result.StructuredContent?["data"]?["settlement"]?["outcome"] != "not-applied") unreadWrites[digest] = id;
            Trim();
        }
    }
    internal long? RetainedElapsedMs(string id)
    {
        lock (gate)
            return entries.TryGetValue(id, out var entry) ? (long?)(entry.Result?.StructuredContent ?? entry.Summary)?["meta"]?["elapsedMs"] : null;
    }

    public CallToolResult? Read(string release, string id, UnsettledCalls unsettled)
    {
        var result = ReadStored(release, id, unsettled, out var bytes);
        if (bytes != null) result = JsonSerializer.Deserialize<CallToolResult>(bytes, ModelContextProtocol.McpJsonUtilities.DefaultOptions);
        TiaOpenness.Shared.ToolCallTiming.Active?.PreserveRetrievedResult(result?.StructuredContent);
        if (Current.Value is { } call && ReferenceEquals(call.owner, this))
        {
            var meta = (result?.StructuredContent as JsonObject)?["meta"] as JsonObject;
            call.RetrievedId = (string?)meta?["requestId"] ?? id;
        }
        return result;
    }
    private CallToolResult? ReadStored(string release, string id, UnsettledCalls unsettled, out byte[]? ordinaryResult)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        ordinaryResult = null;
        lock (gate)
        {
            entries.TryGetValue(id, out var entry);
            if (entry == null)
            {
                foreach (var stored in ordinary)
                    if (stored.Id == id) { ordinaryResult = stored.Result; return null; }
                if (!unsettled.IsUnread(id)) return null;
                var unknown = unsettled.Snapshot().FirstOrDefault(call => call.RequestId == id);
                var settlement = unsettled.Settlement(id);
                if (unknown == null && settlement?.Outcome != CallSettlement.ProcessExited) return null;
                var utc = unknown?.Utc ?? settlement!.Utc;
                var meta = new Meta(utc, release, unknown?.Tool ?? settlement!.Tool, id, Outcome.Unknown, Execution.Unknown,
                    BehaviorPolicy.Current, Completeness.Unknown, null, new[] { new Warning(WarningCode.UnverifiedBehavior,
                        "Native behavior has not been verified on a real machine.", new Dictionary<string, System.Text.Json.JsonElement>()) });
                var data = new JsonObject();
                if (settlement != null) data["settlement"] = settlement.ToJson();
                var body = JsonNode.Parse(V4Json.Serialize(Envelope.Create(data,
                    new Error("The call has an unknown outcome. Inspect its target before repeating it.",
                        new OutcomeUnknownDetails("tool-call", new Dictionary<string, System.Text.Json.JsonElement>())), meta)))!.AsObject();
                var result = new CallToolResult { StructuredContent = body };
                unsettled.MarkRead(id);
                var summary = OutcomeSummary(result, new JsonObject { ["sinceUtc"] = utc.ToString("O") });
                if (unknown?.FromPreviousOwner == true)
                {
                    // A recovered lease can contain only an ID. Identify the measured lookup duration in that case.
                    summary["data"]!["elapsedMsSource"] = unknown.OriginalElapsedMs.HasValue ? "original-call" : "result-lookup";
                    summary["meta"]!["elapsedMs"] = unknown.OriginalElapsedMs ?? (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }
                return SummaryResult(summary);
            }
            if (entry.Summary == null)
            {
                var snapshot = (JsonObject)entry.Running.DeepClone();
                return InProgress(release, (string)snapshot["tool"]!, DescribeRunning?.Invoke(snapshot) ?? snapshot);
            }
            // An unknown result can arrive after its process already exited.
            foreach (var alias in entry.Ids)
                if (unsettled.Settlement(alias) is { Outcome: CallSettlement.ProcessExited } exited) ObserveSettlement(exited);
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
    public void Clear() { lock (gate) { entries.Clear(); completed.Clear(); ordinary.Clear(); running = null; unreadWrites.Clear(); } }
    private static byte[]? SerializeWithinLimit(CallToolResult result)
    {
        if (result.Content.OfType<TextContentBlock>().Sum(block => (long)block.Text.Length) >= ResponseBytes) return null;
        var options = ModelContextProtocol.McpJsonUtilities.DefaultOptions;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = options.Encoder, Indented = options.WriteIndented,
            MaxDepth = options.MaxDepth }))
            JsonSerializer.Serialize(writer, result, options);
        return buffer.WrittenCount <= ResponseBytes ? buffer.WrittenSpan.ToArray() : null;
    }
    private static byte[] OrdinaryResult(string id, string tool, JsonObject? body, CallToolResult result)
    {
        if (SerializeWithinLimit(result) is { } bytes) return bytes;
        var meta = body?["meta"] as JsonObject;
        bool readOnly = (string?)meta?["execution"] == "read-only";
        var data = new JsonObject { ["tool"] = tool, ["requestId"] = id, ["outcome"] = meta?["outcome"]?.DeepClone(),
            ["errorCode"] = (body?["error"] as JsonObject)?["code"]?.DeepClone(), ["fullResultEvicted"] = true,
            ["note"] = readOnly ? "Result too large; repeat the read." : "Result too large. Inspect the target before repeating a write." };
        if (body?["schemaVersion"] is JsonValue version && version.TryGetValue<int>(out var number) && number == 4 && body["ok"] != null && meta != null)
        {
            var summary = SummaryEnvelope(body, data);
            if (SerializeWithinLimit(SummaryResult(summary)) is { } compact) return compact;
        }
        // Without a retainable envelope, report the lookup limit and keep the original status in the summary.
        var lookup = new Meta(DateTimeOffset.UtcNow, (string?)meta?["releaseKey"], "GetCallResult", id, Outcome.ReadFailed,
            Execution.ReadOnly, BehaviorPolicy.NotApplicable, Completeness.None, null, Array.Empty<Warning>());
        var error = new Error($"The full {tool} result exceeded the response limit. Original outcome: {data["outcome"] ?? "unavailable"}.",
            new LimitExceededDetails("response", ResponseBytes, null));
        var limited = JsonNode.Parse(V4Json.Serialize(Envelope.Create(data, error, lookup)))!.AsObject();
        return JsonSerializer.SerializeToUtf8Bytes(SummaryResult(limited), ModelContextProtocol.McpJsonUtilities.DefaultOptions);
    }
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
        while (completed.Count > RetainedResults)
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
        var data = new JsonObject { ["tool"] = body["meta"]?["tool"]?.DeepClone() ?? running["tool"]?.DeepClone(),
            ["outcome"] = body["meta"]?["outcome"]?.DeepClone(),
            ["utc"] = body["meta"]?["timestamp"]?.DeepClone() ?? running["sinceUtc"]?.DeepClone(),
            ["nativeOutcomeUnknown"] = body["data"]?["nativeOutcomeUnknown"]?.DeepClone(),
            ["fullResultEvicted"] = true, ["note"] = "The full result payload is unavailable. This stored outcome summary acknowledges the call. "
                + "Inspect its target before repeating a write." };
        if (body["data"]?["settlement"] is { } settlement) data["settlement"] = settlement.DeepClone();
        return SummaryEnvelope(body, data);
    }
    private static JsonObject SummaryEnvelope(JsonNode body, JsonObject data)
    {
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
            ["meta"] = meta, ["error"] = error, ["data"] = data };
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
        var meta = new Meta(DateTimeOffset.UtcNow, release, tool, id, Outcome.InProgress, Execution.Unknown,
            BehaviorPolicy.Current, Completeness.Unknown, null, new[] { new Warning(WarningCode.UnverifiedBehavior,
                "Native behavior has not been verified on a real machine.", new Dictionary<string, System.Text.Json.JsonElement>()) });
        var envelope = Envelope.Create(new JsonObject { ["activeRequestId"] = id, ["running"] = running.DeepClone() },
            new Error("The call is still running. Do not repeat this call. Read GetCallResult(requestId). "
                + (string?)running["message"] + RunningDiagnostic(running), new OperationInProgressDetails(id)), meta);
        var body = JsonNode.Parse(V4Json.Serialize(envelope));
        return new CallToolResult { IsError = true, StructuredContent = body,
            Content = new[] { new TextContentBlock { Text = body!.ToJsonString() } } };
    }
    public static Error BusyError(JsonObject running) => new(
        $"Worker call {running["tool"]} (request {running["requestId"]}, started {running["sinceUtc"]}) is still running. "
            + "Read GetCallResult(requestId) before repeating it." + RunningDiagnostic(running),
        new PreconditionFailedDetails("worker-call-running", (string?)running["requestId"]));

    public static string RunningDiagnostic(JsonObject running)
    {
        string text = "";
        if (running["dialog"] is JsonObject dialog)
            text += $" TIA reported at {dialog["utc"]}, and it may still be open. {dialog["caption"]} {dialog["text"]} {dialog["detailText"]}";
        if ((bool?)running["window"]?["mainWindowDisabled"] == true && running["window"]?["titles"] is JsonArray { Count: > 0 } titles)
            text += " TIA has a window open titled " + string.Join(" · ", titles.Select(title => (string?)title));
        return text;
    }
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
        internal string? RetrievedId;
        internal volatile HashSet<string>? UnsettledIds;
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
        private (string Id, byte[] Result)? PrepareOrdinary(CallToolResult result)
        {
            var body = result.StructuredContent as JsonObject;
            if (result.StructuredContent == null && result.Content is [TextContentBlock text] && text.Text.AsSpan().TrimStart().StartsWith("{"))
                body = TiaMcpServer.ModelContextProtocol.McpServer.ResultBody(result) as JsonObject;
            var meta = body?["meta"] as JsonObject;
            string ordinaryId = (string?)meta?["requestId"] ?? id;
            string ordinaryTool = (string?)meta?["tool"] ?? tool;
            if (tool == "CallTool" && (ordinaryTool == "GetCallResult" || ordinaryId == RetrievedId)) return null;
            return (ordinaryId, OrdinaryResult(ordinaryId, ordinaryTool, body, result));
        }
        public void Complete(CallToolResult result, UnsettledCalls? unsettled = null)
        {
            if (tool == "GetCallResult" && !Detached) return;
            var structured = result.StructuredContent;
            bool unknown = structured != null && UnsettledCalls.HasUnknownOutcome(structured);
            // A detach or inner unsettled write during serialization takes precedence when publishing below.
            var ordinary = !Detached && !unknown && UnsettledIds == null ? PrepareOrdinary(result) : null;
            lock (owner.gate)
            {
                if (tool == "GetCallResult" && entry == null) return;
                string resultId = (string?)structured?["meta"]?["requestId"] ?? id;
                string? resultTool = (string?)structured?["meta"]?["tool"];
                if (entry == null)
                {
                    if (tool == "CallTool" && (resultTool == "GetCallResult" || resultId == RetrievedId)) return;
                    if (structured == null || !unknown && UnsettledIds == null)
                    {
                        if (ordinary.HasValue && UnsettledIds == null)
                        {
                            owner.ordinary.Enqueue(ordinary.Value);
                            if (owner.ordinary.Count > RetainedResults) owner.ordinary.Dequeue();
                        }
                        return;
                    }
                    owner.entries.TryGetValue(resultId, out entry);
                    if (entry == null)
                    {
                        entry = new Entry(new JsonObject { ["requestId"] = resultId, ["tool"] = resultTool ?? tool,
                            ["sinceUtc"] = DateTimeOffset.UtcNow.ToString("O") }, Digest);
                        owner.Index(entry);
                    }
                }
                entry.Ids.Add(resultId);
                resultTool ??= (string?)entry.Running["tool"] ?? tool;
                entry.Running["tool"] = resultTool;
                if (UnsettledIds != null) entry.Ids.UnionWith(UnsettledIds);
                owner.Index(entry);
                if (ReferenceEquals(owner.running, entry)) owner.running = null;
                if (entry.Completed != null) owner.completed.Remove(entry.Completed);
                entry.Completed = owner.completed.AddLast(entry);
                entry.Unsettled = unsettled;
                entry.Result = Copy(result);
                entry.Summary = OutcomeSummary(result, entry.Running);
                bool notApplied = (string?)result.StructuredContent?["data"]?["settlement"]?["outcome"] == "not-applied"
                    || unsettled?.Settlement(resultId)?.Outcome == "not-applied";
                if (entry.Digest != null && !entry.Read)
                {
                    if (!notApplied) owner.unreadWrites[entry.Digest] = (string)entry.Running["requestId"]!;
                    else if (owner.unreadWrites.TryGetValue(entry.Digest, out var guardedId) && entry.Ids.Contains(guardedId))
                        owner.unreadWrites.Remove(entry.Digest);
                }
                if (unknown && entry.Digest != null)
                    unsettled?.Record(resultId, resultTool, entry.Digest, DateTimeOffset.UtcNow);
                else if (!unknown && result.StructuredContent != null)
                    foreach (string alias in entry.Ids.Where(alias => UnsettledIds?.Contains(alias) != true)) unsettled?.Settle(alias);
                owner.Trim();
            }
        }
        public void Dispose() { registration.Dispose(); cancellation.Dispose(); }
    }
    private sealed class Scope(Action restore) : IDisposable { public void Dispose() => restore(); }
}
