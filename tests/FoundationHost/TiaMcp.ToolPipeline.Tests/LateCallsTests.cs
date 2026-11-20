using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcp.EngineHost.Tests;

public sealed class LateCallsTests
{
    private static JsonObject Running(string id) => new() { ["requestId"] = id, ["tool"] = "SaveProject",
        ["sinceUtc"] = DateTimeOffset.UtcNow.ToString("O") };
    private static CallToolResult Completed(string id, string outcome = "succeeded") => new() {
        Content = new[] { new TextContentBlock { Text = "original content" } },
        StructuredContent = new JsonObject { ["meta"] = new JsonObject { ["requestId"] = id, ["outcome"] = outcome } } };

    [Fact]
    public void Seventeen_ordinary_completions_evict_only_the_oldest_ordinary_result()
    {
        var store = new LateCalls();
        var unsettled = new UnsettledCalls();
        store.RecordUnknown("unknown", "SaveProject", "digest", Completed("unknown", "unknown"));
        for (int i = 0; i < 17; i++)
        {
            using var read = store.CreateInvocation("GetProjectInfo", "outer-" + i, CancellationToken.None);
            read.Complete(Completed("read-" + i), unsettled);
        }
        Assert.Null(store.Read("21", "read-0", unsettled));
        for (int i = 1; i < 17; i++)
        {
            var result = store.Read("21", "read-" + i, unsettled)!;
            Assert.Equal("read-" + i, (string?)result.StructuredContent?["meta"]?["requestId"]);
            Assert.Equal("original content", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        }
        Assert.Equal("unknown", store.FindUnread("digest"));
        var unknown = store.Read("21", "unknown", unsettled)!;
        Assert.Equal("original content", Assert.IsType<TextContentBlock>(Assert.Single(unknown.Content)).Text);
        Assert.Null(new LateCalls().Read("21", "read-16", new UnsettledCalls()));
        store.Clear();
        Assert.Null(store.Read("21", "read-16", unsettled));
    }

    [Fact]
    public void Ordinary_writes_are_retrievable_without_creating_or_acknowledging_a_guard()
    {
        var store = new LateCalls();
        var unsettled = new UnsettledCalls();
        using var first = store.CreateInvocation("SaveProject", "first", CancellationToken.None);
        using (first.Enter()) LateCalls.IdentifyWrite("same-write");
        first.Complete(Completed("first"), unsettled);
        Assert.Null(store.FindUnread("same-write"));
        Assert.Empty(unsettled.Snapshot());
        store.RecordUnknown("second", "SaveProject", "same-write", Completed("second", "unknown"));
        unsettled.Record("second", "SaveProject", "same-write", DateTimeOffset.UtcNow);
        Assert.Equal("succeeded", (string?)store.Read("21", "first", unsettled)!.StructuredContent?["meta"]?["outcome"]);
        Assert.Equal("second", store.FindUnread("same-write"));
        Assert.True(unsettled.IsUnread("second"));
    }

    [Theory, InlineData("CallTool"), InlineData("GetCallResult")]
    public void Looking_up_a_result_does_not_extend_its_retention(string lookupTool)
    {
        var store = new LateCalls();
        var unsettled = new UnsettledCalls();
        for (int i = 0; i < 16; i++)
        {
            using var call = store.CreateInvocation("GetProjectInfo", "read-" + i, CancellationToken.None);
            call.Complete(Completed("read-" + i));
        }
        using var lookup = store.CreateInvocation(lookupTool, "lookup", CancellationToken.None);
        using (lookup.Enter()) lookup.Complete(store.Read("21", "read-0", unsettled)!);
        Assert.NotNull(store.Read("21", "read-0", unsettled));
        using var last = store.CreateInvocation("GetProjectInfo", "last", CancellationToken.None);
        last.Complete(Completed("last"));
        Assert.Null(store.Read("21", "read-0", unsettled));
        Assert.Null(store.Read("21", "lookup", unsettled));
    }

    [Theory, InlineData(false, false), InlineData(false, true), InlineData(true, false), InlineData(true, true)]
    public void Text_only_unknown_envelopes_do_not_classify_writes_or_supply_protected_ids(bool detached, bool innerUnsettled)
    {
        var store = new LateCalls();
        var unsettled = new UnsettledCalls();
        var meta = new Meta(DateTimeOffset.UtcNow, "21", "SaveProject", "text-id", Outcome.Unknown, Execution.Unknown,
            BehaviorPolicy.NotApplicable, Completeness.Unknown, null, Array.Empty<Warning>());
        var error = new Error("Unknown outcome.", new OutcomeUnknownDetails("write", new Dictionary<string, JsonElement>()));
        string text = V4Json.Serialize(Envelope.Create(new JsonObject(), error, meta));
        using var call = store.CreateInvocation("CallTool", "outer", CancellationToken.None);
        using (call.Enter())
        {
            LateCalls.IdentifyWrite("write");
            if (detached) LateCalls.Detach(Running("active"));
            if (innerUnsettled) unsettled.Record("inner", "SaveProject", "inner-write", DateTimeOffset.UtcNow);
            call.Complete(new CallToolResult { IsError = true, Content = new[] { new TextContentBlock { Text = text } } }, unsettled);
        }
        Assert.Equal(detached ? "active" : null, store.FindUnread("write"));
        Assert.Equal(innerUnsettled ? new[] { "inner" } : Array.Empty<string>(), unsettled.Snapshot().Select(item => item.RequestId));
        Assert.Equal(innerUnsettled, unsettled.IsUnread("inner"));
        if (detached)
        {
            Assert.Equal("active", (string?)Assert.Single(store.Snapshot())!["requestId"]);
            Assert.Null(store.Read("21", "text-id", unsettled));
            Assert.NotNull(store.Read("21", "outer", unsettled));
        }
        else
        {
            Assert.Empty(store.Snapshot());
            Assert.Null(store.Read("21", "outer", unsettled));
            var cached = store.Read("21", "text-id", unsettled);
            if (innerUnsettled) Assert.Null(cached);
            else Assert.Equal(text, Assert.IsType<TextContentBlock>(Assert.Single(cached!.Content)).Text);
        }
    }

    private sealed class SerializationProbe(Func<int> observe)
    {
        [System.Text.Json.Serialization.JsonPropertyName("value")]
        public int Value => observe();
    }

    [Fact]
    public void Serializing_an_ordinary_result_leaves_the_store_lock_available()
    {
        var store = new LateCalls();
        var gate = typeof(LateCalls).GetField("gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
        bool? locked = null;
        var result = Completed("read");
        result.StructuredContent!["data"] = JsonValue.Create(new SerializationProbe(() => { locked = Monitor.IsEntered(gate); return 42; }));
        using var read = store.CreateInvocation("GetProjectInfo", "read", CancellationToken.None);
        read.Complete(result);
        Assert.False(locked);
        Assert.Equal(42, (int?)store.Read("21", "read", new UnsettledCalls())!.StructuredContent?["data"]?["value"]);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void A_call_that_becomes_protected_during_serialization_does_not_evict_an_ordinary_result(bool detached)
    {
        var store = new LateCalls();
        var unsettled = new UnsettledCalls();
        for (int i = 0; i < 16; i++)
        {
            using var read = store.CreateInvocation("GetProjectInfo", "read-" + i, CancellationToken.None);
            read.Complete(Completed("read-" + i));
        }
        using var call = store.CreateInvocation("SaveProject", "outer", CancellationToken.None);
        using var scope = call.Enter();
        LateCalls.IdentifyWrite("write");
        var result = Completed("final");
        var probe = new SerializationProbe(() =>
        {
            if (detached) LateCalls.Detach(Running("active"));
            else LateCalls.ObserveUnsettled("inner");
            return 42;
        });
        result.StructuredContent!["data"] = new JsonObject { ["probe"] = JsonValue.Create(probe) };
        call.Complete(result, unsettled);
        string guardedId = detached ? "active" : "final";
        Assert.Equal(guardedId, (string?)Assert.Single(store.Snapshot())!["requestId"]);
        Assert.Equal(guardedId, store.FindUnread("write"));
        Assert.NotNull(store.Read("21", "read-0", unsettled));
        Assert.NotNull(store.Read("21", detached ? "outer" : "inner", unsettled));
        Assert.Null(store.FindUnread("write"));
    }

    [Theory, InlineData(0, false), InlineData(1, false), InlineData(1, true)]
    public void Ordinary_results_keep_the_full_MCP_response_up_to_the_byte_limit(int extraByte, bool write)
    {
        var store = new LateCalls();
        var unsettled = new UnsettledCalls();
        string tool = write ? "SaveProject" : "GetProjectInfo";
        using var call = store.CreateInvocation(tool, "outer", CancellationToken.None);
        var meta = new Meta(DateTimeOffset.UtcNow, "21", tool, "large", write ? Outcome.Failed : Outcome.Succeeded,
            write ? Execution.Completed : Execution.ReadOnly, BehaviorPolicy.NotApplicable, Completeness.Complete, null, Array.Empty<Warning>());
        var error = write ? new Error("Save failed.", new NativeOperationFailedDetails(null, null, new Dictionary<string, JsonElement>())) : null;
        var result = new CallToolResult { IsError = write, StructuredContent = JsonNode.Parse(V4Json.Serialize(Envelope.Create(new JsonObject(), error, meta))),
            Content = new[] { new TextContentBlock { Text = "" } } };
        var options = ModelContextProtocol.McpJsonUtilities.DefaultOptions;
        int overhead = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, options).Length;
        int size = TiaMcp.WorkerChannel.ChannelLimits.ResponseBytes - overhead + extraByte;
        result.Content = new[] { new TextContentBlock { Text = new string('x', size) } };
        call.Complete(result, unsettled);
        var stored = store.Read("21", "large", unsettled)!;
        string text = Assert.IsType<TextContentBlock>(Assert.Single(stored.Content)).Text;
        if (extraByte == 0)
            Assert.Equal(size, text.Length);
        else
        {
            var summary = V4Json.Deserialize<Envelope>(text);
            Assert.Equal("large", summary.Meta.RequestId);
            Assert.Equal(tool, summary.Meta.Tool);
            Assert.Equal(meta.Outcome, summary.Meta.Outcome);
            Assert.Equal(!write, summary.Ok);
            Assert.Equal(write, stored.IsError);
            Assert.Equal(error?.Code, summary.Error?.Code);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), stored.StructuredContent));
            var data = stored.StructuredContent?["data"];
            Assert.Equal(tool, (string?)data?["tool"]);
            Assert.Equal(write ? "failed" : "succeeded", (string?)data?["outcome"]);
            Assert.Equal(write ? "NATIVE_OPERATION_FAILED" : null, (string?)data?["errorCode"]);
            string expectedNote = write ? "Result too large. Inspect the target before repeating a write." : "Result too large; repeat the read.";
            Assert.Equal(expectedNote, (string?)data?["note"]);
            Assert.True(text.Length < 1024);
        }
    }

    [Fact]
    public void An_oversized_failure_without_a_v4_envelope_reports_the_lookup_limit_and_original_failure()
    {
        var store = new LateCalls();
        using var call = store.CreateInvocation("SaveProject", "write", CancellationToken.None);
        var body = new JsonObject { ["meta"] = new JsonObject { ["outcome"] = "failed" }, ["error"] = new JsonObject { ["code"] = "NATIVE_OPERATION_FAILED" } };
        call.Complete(new CallToolResult { IsError = true, StructuredContent = body,
            Content = new[] { new TextContentBlock { Text = new string('x', TiaMcp.WorkerChannel.ChannelLimits.ResponseBytes) } } });
        var stored = store.Read("21", "write", new UnsettledCalls())!;
        var summary = V4Json.Deserialize<Envelope>(Assert.IsType<TextContentBlock>(Assert.Single(stored.Content)).Text);
        Assert.True(stored.IsError);
        Assert.False(summary.Ok);
        Assert.Equal(Outcome.ReadFailed, summary.Meta.Outcome);
        Assert.Equal(ErrorCode.LimitExceeded, summary.Error!.Code);
        Assert.Equal("failed", (string?)stored.StructuredContent?["data"]?["outcome"]);
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)stored.StructuredContent?["data"]?["errorCode"]);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Bridged_unknown_completion_keeps_the_actual_tool_in_state_and_unsettled_entries(bool detached)
    {
        var store = new LateCalls();
        var unsettled = new UnsettledCalls();
        using var call = store.CreateInvocation("CallTool", "outer", CancellationToken.None);
        using (call.Enter())
        {
            LateCalls.IdentifyWrite("digest");
            if (detached) LateCalls.Detach(Running("active"));
            var result = Completed("actual", "unknown");
            result.StructuredContent!["meta"]!["tool"] = "SetUnifiedGlobalScript";
            call.Complete(result, unsettled);
        }
        Assert.Equal("SetUnifiedGlobalScript", (string?)Assert.Single(store.Snapshot())!["tool"]);
        Assert.Equal("SetUnifiedGlobalScript", Assert.Single(unsettled.Snapshot()).Tool);
        Assert.Equal("SetUnifiedGlobalScript", (string?)store.Read("21", "actual", unsettled)!.StructuredContent!["meta"]!["tool"]);
    }

    [Fact]
    public void Final_result_is_reachable_by_both_request_ids_and_releases_its_guard()
    {
        var store = new LateCalls();
        var unsettled = new UnsettledCalls();
        using var call = store.CreateInvocation("CallTool", "outer-id", CancellationToken.None);
        using var flow = call.Enter();
        LateCalls.IdentifyWrite("digest");
        LateCalls.Detach(Running("active-id"));
        Assert.True(store.ContinuesCurrentCall);
        using var other = store.CreateInvocation("GetProjectInfo", "other", CancellationToken.None);
        using (other.Enter())
            Assert.False(store.ContinuesCurrentCall);
        Assert.Equal("active-id", store.FindUnread("digest"));
        Assert.Equal("in-progress", (string?)store.Read("21", "active-id", unsettled)!.StructuredContent?["meta"]?["outcome"]);
        Assert.Equal("active-id", store.FindUnread("digest"));
        call.Complete(Completed("final-id"), unsettled);
        Assert.Equal("SaveProject", (string?)Assert.Single(store.Snapshot())!["tool"]);
        Assert.Equal("succeeded", (string?)store.Read("21", "active-id", unsettled)!.StructuredContent?["meta"]?["outcome"]);
        Assert.Equal("final-id", (string?)store.Read("21", "final-id", unsettled)!.StructuredContent?["meta"]?["requestId"]);
        Assert.NotNull(store.Read("21", "outer-id", unsettled));
        Assert.Equal("original content", Assert.IsType<TextContentBlock>(store.Read("21", "final-id", unsettled)!.Content.Single()).Text);
        Assert.Null(store.FindUnread("digest"));
    }

    private static int EntryCount(LateCalls store)
    {
        var entries = typeof(LateCalls).GetField("entries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(store)!;
        var values = entries.GetType().GetProperty("Values")?.GetValue(entries) as System.Collections.IEnumerable;
        return values?.Cast<object>().Distinct().Count() ?? (int)entries.GetType().GetProperty("Count")!.GetValue(entries)!;
    }

    [Fact]
    public void Unknown_completion_without_a_digest_retains_evidence_and_can_be_read()
    {
        var store = new LateCalls();
        var unsettled = new UnsettledCalls();
        using var call = store.CreateInvocation("SaveProject", "outer", CancellationToken.None);
        using (call.Enter())
        {
            unsettled.Record("unknown", "SaveProject", null, DateTimeOffset.UtcNow);
            call.Complete(Completed("unknown", "unknown"), unsettled);
        }
        var entry = Assert.Single(unsettled.Snapshot());
        Assert.Equal("unknown", entry.RequestId);
        Assert.Null(entry.Digest);
        Assert.Equal("unknown", (string?)store.Read("21", entry.RequestId, unsettled)!.StructuredContent?["meta"]?["outcome"]);
        Assert.Single(unsettled.Snapshot());
        Assert.False(unsettled.IsUnread(entry.RequestId));
    }

    [Fact]
    public void Unknown_record_without_a_digest_retains_the_full_result()
    {
        var store = new LateCalls();
        var unsettled = new UnsettledCalls();
        unsettled.Record("unknown", "SaveProject", null, DateTimeOffset.UtcNow);
        store.RecordUnknown("unknown", "SaveProject", null, Completed("unknown", "unknown"));
        Assert.Single(store.Snapshot());
        Assert.Equal("unknown", (string?)store.Read("21", "unknown", unsettled)!.StructuredContent?["meta"]?["outcome"]);
        Assert.Single(unsettled.Snapshot());
        Assert.False(unsettled.IsUnread("unknown"));
    }

    [Fact]
    public void Two_hundred_completed_reads_keep_at_most_sixteen_entries()
    {
        var store = new LateCalls();
        var unsettled = new UnsettledCalls();
        for (int index = 0; index < 200; index++)
        {
            string id = "read-" + index;
            using var call = store.CreateInvocation("GetProjectInfo", id, CancellationToken.None);
            using (call.Enter()) LateCalls.Detach(Running(id));
            call.Complete(Completed(id));
            Assert.NotNull(store.Read("21", id, unsettled));
        }
        var acknowledgements = (HashSet<string>)typeof(UnsettledCalls)
            .GetField("read", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(unsettled)!;
        Assert.Empty(acknowledgements);
        Assert.InRange(EntryCount(store), 0, 16);
        Assert.Equal(16, store.Snapshot().Count);
        Assert.Null(store.Read("21", "read-0", new UnsettledCalls()));
    }

    [Fact]
    public void Unsettled_snapshot_keeps_record_order_after_an_older_call_settles()
    {
        var unsettled = new UnsettledCalls();
        unsettled.Record("first", "SaveProject", "one", DateTimeOffset.UtcNow);
        unsettled.Record("second", "SaveProject", "two", DateTimeOffset.UtcNow);
        unsettled.Settle("first");
        unsettled.Record("third", "SaveProject", "three", DateTimeOffset.UtcNow);
        Assert.Equal(new[] { "second", "third" }, unsettled.Snapshot().Select(call => call.RequestId));
    }

    [Fact]
    public void Evicted_unread_write_summary_disappears_after_it_is_read()
    {
        var store = new LateCalls();
        var unsettled = new UnsettledCalls();
        store.RecordUnknown("write", "SaveProject", "digest", Completed("write", "unknown"));
        unsettled.Record("write", "SaveProject", "digest", DateTimeOffset.UtcNow);
        for (int index = 0; index < 16; index++)
        {
            string id = "read-" + index;
            using var call = store.CreateInvocation("GetProjectInfo", id, CancellationToken.None);
            using (call.Enter()) LateCalls.Detach(Running(id));
            call.Complete(Completed(id));
        }
        Assert.Equal(17, EntryCount(store));
        Assert.Equal("write", store.FindUnread("digest"));
        Assert.True((bool?)store.Read("21", "write", unsettled)!.StructuredContent?["data"]?["fullResultEvicted"]);
        Assert.Null(store.FindUnread("digest"));
        Assert.Null(unsettled.FindWrite("digest"));
        Assert.Equal(16, EntryCount(store));
        Assert.Null(store.Read("21", "write", unsettled));
    }

    [Fact]
    public void Unknown_result_is_published_after_the_whole_invocation_finishes()
    {
        var store = new LateCalls();
        using var call = store.CreateInvocation("SaveProject", "unknown", CancellationToken.None);
        using var flow = call.Enter();
        store.RecordUnknown("unknown", "SaveProject", "digest", Completed("unknown", "unknown"));
        Assert.Null(store.Read("21", "unknown", new UnsettledCalls()));
        call.Complete(Completed("unknown", "unknown"));
        Assert.Equal("unknown", (string?)store.Read("21", "unknown", new UnsettledCalls())!.StructuredContent?["meta"]?["outcome"]);
    }

    [Fact]
    public void Retention_keeps_sixteen_completed_results_and_the_running_call()
    {
        var store = new LateCalls();
        using var running = store.CreateInvocation("SaveProject", "running", CancellationToken.None);
        using (running.Enter()) LateCalls.Detach(Running("running"));
        for (int index = 0; index < 17; index++)
            store.RecordUnknown("completed-" + index, "SaveProject", "digest-" + index, Completed("completed-" + index, "unknown"));
        Assert.Equal(16, store.Snapshot().Count);
        Assert.NotNull(store.Read("21", "completed-0", new UnsettledCalls()));
        Assert.Equal("running", (string?)store.Running?["requestId"]);
        Assert.Equal("in-progress", (string?)store.Read("21", "running", new UnsettledCalls())!.StructuredContent?["meta"]?["outcome"]);
    }

    [Fact]
    public void Retention_uses_completion_order_when_a_running_call_finishes_last()
    {
        var store = new LateCalls();
        using var running = store.CreateInvocation("SaveProject", "running", CancellationToken.None);
        using (running.Enter()) LateCalls.Detach(Running("running"));
        for (int index = 0; index < 16; index++)
            store.RecordUnknown("completed-" + index, "SaveProject", "digest-" + index, Completed("completed-" + index, "unknown"));
        running.Complete(Completed("running"));
        Assert.Equal(16, store.Snapshot().Count);
        Assert.NotNull(store.Read("21", "running", new UnsettledCalls()));
        Assert.NotNull(store.Read("21", "completed-0", new UnsettledCalls()));
    }

    [Fact]
    public void Reading_an_old_result_does_not_release_a_new_write_with_the_same_digest()
    {
        var store = new LateCalls();
        var unsettled = new UnsettledCalls();
        using var first = store.CreateInvocation("SaveProject", "first", CancellationToken.None);
        using (first.Enter())
        {
            LateCalls.IdentifyWrite("digest");
            LateCalls.Detach(Running("first"));
            first.Complete(Completed("first"));
        }
        Assert.NotNull(store.Read("21", "first", unsettled));
        using var next = store.CreateInvocation("SaveProject", "next", CancellationToken.None);
        using (next.Enter())
        {
            LateCalls.IdentifyWrite("digest");
            LateCalls.Detach(Running("next"));
        }
        Assert.NotNull(store.Read("21", "first", unsettled));
        Assert.Equal("next", store.FindUnread("digest"));
        next.Complete(Completed("next"));
        Assert.NotNull(store.Read("21", "next", unsettled));
        Assert.Null(store.FindUnread("digest"));
    }

    [Fact]
    public void Unread_write_guard_survives_result_eviction_until_session_ends()
    {
        var store = new LateCalls();
        for (int index = 0; index < 17; index++)
        {
            using var call = store.CreateInvocation("SaveProject", "completed-" + index, CancellationToken.None);
            using var flow = call.Enter();
            LateCalls.IdentifyWrite("digest-" + index);
            LateCalls.Detach(Running("completed-" + index));
            call.Complete(Completed("completed-" + index));
        }
        Assert.Equal(16, store.Snapshot().Count);
        Assert.Equal("completed-0", store.FindUnread("digest-0"));
        var summary = store.Read("21", "completed-0", new UnsettledCalls());
        Assert.True((bool?)summary!.StructuredContent?["data"]?["fullResultEvicted"]);
        Assert.Equal("succeeded", (string?)summary.StructuredContent?["meta"]?["outcome"]);
        Assert.Null(store.FindUnread("digest-0"));
        using (var lookup = store.CreateInvocation("GetCallResult", "lookup", CancellationToken.None)) lookup.Complete(summary);
        Assert.DoesNotContain(store.Snapshot(), row => (string?)row?["requestId"] == "completed-0");
        Assert.NotNull(store.Read("21", "completed-1", new UnsettledCalls()));
        Assert.Null(store.FindUnread("digest-1"));
        store.Clear();
        Assert.Null(store.FindUnread("digest-0"));
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Evicted_summary_retains_outcome_time_and_excludes_the_original_payload(bool unknown)
    {
        var store = new LateCalls();
        var error = unknown ? new Error("Unknown outcome.", new OutcomeUnknownDetails("tool-call",
            new Dictionary<string, System.Text.Json.JsonElement> { ["payload"] = System.Text.Json.JsonSerializer.SerializeToElement("original-payload-only")
            })) : null;
        var envelope = Envelope.Create(new JsonObject { ["payload"] = "original-payload-only" }, error,
            new Meta(DateTimeOffset.UtcNow, "21", "SaveProject", "evicted", unknown ? Outcome.Unknown : Outcome.Succeeded,
                unknown ? Execution.Unknown : Execution.Completed, BehaviorPolicy.NotApplicable,
                unknown ? Completeness.Unknown : Completeness.Complete, null, Array.Empty<Warning>()));
        var body = V4Json.Serialize(envelope);
        store.RecordUnknown("evicted", "SaveProject", "digest", new CallToolResult {
            Content = new[] { new TextContentBlock { Text = body } } });
        for (int index = 0; index < 16; index++)
            store.RecordUnknown("later-" + index, "SaveProject", "digest-" + index, Completed("later-" + index));
        var summary = store.Read("21", "evicted", new UnsettledCalls())!;
        Assert.DoesNotContain("original-payload-only", summary.StructuredContent!.ToJsonString());
        Assert.Equal("SaveProject", (string?)summary.StructuredContent["data"]?["tool"]);
        Assert.NotNull(summary.StructuredContent["data"]?["utc"]);
        Assert.NotNull(V4Json.Deserialize<Envelope>(summary.StructuredContent.ToJsonString()));
        Assert.Null(store.FindUnread("digest"));
    }

    [Fact]
    public void Evicted_limit_result_keeps_the_native_unknown_evidence()
    {
        var store = new LateCalls();
        var envelope = Envelope.Create(new JsonObject { ["nativeOutcomeUnknown"] = true },
            new Error("Result exceeded its limit.", new LimitExceededDetails("response", 100, null)),
            new Meta(DateTimeOffset.UtcNow, "21", "SaveProject", "limited", Outcome.Failed, Execution.Completed, BehaviorPolicy.NotApplicable, Completeness
                    .None, null, Array.Empty<Warning>()));
        store.RecordUnknown("limited", "SaveProject", "digest", new CallToolResult {
            StructuredContent = JsonNode.Parse(V4Json.Serialize(envelope)) });
        for (int index = 0; index < 16; index++)
            store.RecordUnknown("later-" + index, "SaveProject", "digest-" + index, Completed("later-" + index));
        var summary = store.Read("21", "limited", new UnsettledCalls())!;
        Assert.True(UnsettledCalls.HasUnknownOutcome(summary.StructuredContent!));
        Assert.NotNull(V4Json.Deserialize<Envelope>(summary.StructuredContent!.ToJsonString()));
        Assert.Null(store.FindUnread("digest"));
    }

    [Fact]
    public void Unsettled_id_without_a_cached_result_can_be_read_and_releases_its_guard()
    {
        var store = new LateCalls();
        var unsettled = new UnsettledCalls();
        unsettled.Record("inner", "SaveProject", "inner-digest", DateTimeOffset.UtcNow);
        var result = store.Read("21", "inner", unsettled);
        Assert.NotNull(result);
        Assert.Equal("unknown", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.Equal("SaveProject", (string?)result.StructuredContent?["data"]?["tool"]);
        Assert.True((bool?)result.StructuredContent?["data"]?["fullResultEvicted"]);
        Assert.Null(result.StructuredContent?["data"]?["nativeOutcomeUnknown"]);
        Assert.NotNull(V4Json.Deserialize<Envelope>(result.StructuredContent!.ToJsonString()));
        Assert.Null(unsettled.FindWrite("inner-digest"));
    }

    [Theory, InlineData("unknown"), InlineData("succeeded")]
    public void Reading_a_batch_result_acknowledges_its_inner_unknown_writes(string outcome)
    {
        var store = new LateCalls();
        var unsettled = new UnsettledCalls();
        using var batch = store.CreateInvocation("Batch", "outer", CancellationToken.None);
        using (batch.Enter())
        {
            unsettled.Record("inner", "SaveProject", "inner-digest", DateTimeOffset.UtcNow);
            unsettled.Record("inner-second", "SaveProjectCopy", "second-digest", DateTimeOffset.UtcNow);
            batch.Complete(Completed("outer", outcome), unsettled);
        }
        Assert.NotNull(store.Read("21", "outer", unsettled));
        Assert.Null(unsettled.FindWrite("inner-digest"));
        Assert.Null(unsettled.FindWrite("second-digest"));
        Assert.Equal("outer", (string?)store.Read("21", "inner", unsettled)!.StructuredContent?["meta"]?["requestId"]);
        Assert.Equal("outer", (string?)store.Read("21", "inner-second", unsettled)!.StructuredContent?["meta"]?["requestId"]);
        Assert.Equal(2, unsettled.Snapshot().Count);
    }

    [Theory]
    [InlineData("c:/x/out.xml")]
    [InlineData("C:\\x\\out.xml ")]
    public void Digest_uses_the_normalized_filesystem_target(string equivalent)
    {
        var first = new JsonObject { ["outputPath"] = "C:\\x\\out.xml", ["dryRun"] = false };
        var retry = new JsonObject { ["outputPath"] = equivalent, ["dryRun"] = false };
        Assert.Equal(WriteDigest.Create("SaveExportContent", first, null), WriteDigest.Create("SaveExportContent", retry, null));
    }

    [Fact]
    public void Approval_digests_match_the_fixed_master_values()
    {
        var arguments = new JsonObject { ["outputPath"] = @"C:\MiXeD\Out.xml", ["content"] = "payload ",
            ["expectedProjectFile"] = @"C:\Fixture\Project.ap21 ", ["dryRun"] = false };
        var approval = PendingApproval.Create("fixture", "21", "SaveExportContent", arguments.ToJsonString(), null, 120);
        Assert.Equal("a477fdf01304cfe5eba57575251f81d3d1761ce23e038c3256bc896d75f455a7", approval.ArgumentDigest);
        Assert.Equal("a477fdf01304cfe5eba57575251f81d3d1761ce23e038c3256bc896d75f455a7", approval.PlanHash);
        Assert.Equal("0157763adb860ed781c5de5d62710cc1f14cb5602543ae122c681b0a4f33631a", approval.InvocationArgumentsDigest);
        var first = new JsonObject { ["outputPath"] = @"C:\x\out.xml", ["dryRun"] = false };
        var retry = new JsonObject { ["outputPath"] = "c:/x/out.xml ", ["dryRun"] = false };
        Assert.Equal(WriteDigest.Create("SaveExportContent", first, null), WriteDigest.Create("SaveExportContent", retry, null));
    }

    [Fact]
    public void Approval_hash_preserves_target_case_while_write_digest_normalizes_it()
    {
        var first = new JsonObject { ["outputPath"] = "C:\\x\\out.xml", ["dryRun"] = false };
        var retry = new JsonObject { ["outputPath"] = "c:/x/out.xml", ["dryRun"] = false };
        var approved = PendingApproval.Create("fixture", "21", "SaveExportContent", first.ToJsonString(), null, 1);
        var repeated = PendingApproval.Create("fixture", "21", "SaveExportContent", retry.ToJsonString(), null, 1);
        Assert.NotEqual(approved.ArgumentDigest, repeated.ArgumentDigest);
        Assert.NotEqual(approved.PlanHash, repeated.PlanHash);
        Assert.Equal(WriteDigest.Create("SaveExportContent", first, null), WriteDigest.Create("SaveExportContent", retry, null));
        retry["outputPath"] = "C:\\x\\out.xml ";
        Assert.Throws<ArgumentException>(() => PendingApproval.Create("fixture", "21", "SaveExportContent", retry.ToJsonString(), null, 1));
    }

    [Fact]
    public async Task No_progress_after_detach()
    {
        var store = new LateCalls();
        using var call = store.CreateInvocation("SaveProject", "active", CancellationToken.None);
        using var flow = call.Enter();
        int notifications = 0;
        using var heartbeat = ProgressHeartbeat.ForTest((_, _) => { notifications++; return Task.CompletedTask; },
            TiaMcp.Engine.Tests.Shared.TestDeadlines.Hang);
        var send = ProgressHeartbeat.CaptureSender()!;
        await send(new JsonObject());
        LateCalls.Detach(Running("active"));
        await send(new JsonObject());
        call.Complete(Completed("active"));
        await send(new JsonObject());
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void Digest_canonicalizes_objects_and_excludes_preview_and_confirmation()
    {
        var first = new JsonObject { ["dryRun"] = false, ["confirm"] = true, ["target"] = new JsonObject { ["b"] = 2, ["a"] = 1 } };
        var second = new JsonObject { ["target"] = new JsonObject { ["a"] = 1, ["b"] = 2 }, ["confirm"] = false, ["dryRun"] = true };
        Assert.Equal(WriteDigest.Create("SaveProject", first, "C:/project.ap21"), WriteDigest.Create("SaveProject", second, "c:/PROJECT.ap21"));
        second["target"]!["a"] = 3;
        Assert.NotEqual(WriteDigest.Create("SaveProject", first, "C:/project.ap21"), WriteDigest.Create("SaveProject", second, "C:/project.ap21"));
    }
}
