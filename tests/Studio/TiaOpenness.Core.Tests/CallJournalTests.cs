using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;
using Xunit;

namespace TiaOpenness.Core.Tests;

public sealed class CallJournalTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "journal-tests", Guid.NewGuid().ToString("N"));
    private static string Envelope(string outcome = "succeeded", string completeness = "complete") =>
        new JsonObject { ["schemaVersion"] = 4, ["ok"] = outcome == "succeeded", ["data"] = new JsonObject { ["token"] = "private-result" },
            ["error"] = outcome == "succeeded" ? null : new JsonObject { ["code"] = outcome == "partial" ? "PARTIAL_FAILURE" : "OUTCOME_UNKNOWN", ["message"] = "failure" },
            ["meta"] = new JsonObject { ["requestId"] = "request", ["outcome"] = outcome, ["execution"] = outcome == "succeeded" ? "read-only" : outcome,
                ["completeness"] = completeness } }.ToJsonString();

    public CallJournalTests() => Directory.CreateDirectory(root);
    private static string[] Rows(string id, string args = "{}", string result = null, string host = "engine")
    {
        var rows = new List<string>();
        InvocationJournal.ConfigureOutput(rows.Add);
        try
        {
            using var call = InvocationJournal.Observe(id, "GetPlcBlockInfo", host, "21", false, () => args);
            if (result != null) call.Complete(() => result);
        }
        finally { InvocationJournal.ConfigureOutput(null); }
        return rows.ToArray();
    }

    [Fact]
    public void Real_writer_rows_reach_reader_with_fields_redacted_before_disk()
    {
        string path = Path.Combine(root, "calls-one.jsonl");
        InvocationJournal.ConfigureOutput(row => File.AppendAllText(path, row + "\n", new UTF8Encoding(false)));
        try
        {
            using var call = InvocationJournal.Observe("call", "GetPlcBlockInfo", "foundation", "19", true,
                () => """{"softwarePath":["PLC_1"],"password":"private-argument"}""");
            call.Complete(() => Envelope());
        }
        finally { InvocationJournal.ConfigureOutput(null); }
        using var reader = new CallJournalReader(root, false);
        var row = Assert.Single(reader.Calls);
        Assert.Equal("foundation", row.Host); Assert.Equal("19", row.Release); Assert.Equal("request", row.RequestId);
        Assert.True(row.IsWrite); Assert.Equal("succeeded", row.DisplayOutcome); Assert.Equal("read-only", row.Execution);
        Assert.Equal("complete", row.Completeness); Assert.NotNull(row.DurationMs); Assert.Contains("PLC_1", row.Target);
        Assert.DoesNotContain("private-", File.ReadAllText(path)); Assert.Contains("••••", row.Arguments); Assert.Contains("••••", row.Result);
    }

    [Theory]
    [InlineData("partial", "partial", "partial")]
    [InlineData("unknown", "unknown", "unknown")]
    [InlineData("succeeded", "partial", "partial")]
    [InlineData("succeeded", "unknown", "unknown")]
    [InlineData("succeeded", "none", "read-failed")]
    public void Projection_classification_matches_the_P6_25_reader(string outcome, string completeness, string expected)
    {
        File.WriteAllLines(Path.Combine(root, "calls-status.jsonl"), Rows("status", result: Envelope(outcome, completeness)));
        using var reader = new CallJournalReader(root, false);
        Assert.Equal(expected, Assert.Single(reader.Calls).DisplayOutcome);
    }

    [Fact]
    public void Rotation_duplicates_truncated_tail_and_disappearing_files_are_handled()
    {
        string path = Path.Combine(root, "calls-process.jsonl");
        var rows = Rows("one", result: Envelope());
        File.WriteAllText(path, rows[0] + "\n" + rows[1].Substring(0, rows[1].Length / 2));
        using var reader = new CallJournalReader(root, false);
        Assert.Equal("executing", Assert.Single(reader.Calls).DisplayOutcome);
        File.AppendAllText(path, rows[1].Substring(rows[1].Length / 2) + "\n"); reader.Poll();
        Assert.Equal("succeeded", Assert.Single(reader.Calls).DisplayOutcome);
        File.Move(path, path + ".previous");
        File.WriteAllLines(path, Rows("two", result: Envelope("unknown", "unknown"))); reader.Poll(); Assert.Equal(2, reader.Calls.Count);
        File.Copy(path + ".previous", path + ".2"); reader.Poll(); Assert.Equal(2, reader.Calls.Count);
        File.Delete(path + ".previous"); File.Delete(path + ".2"); reader.Poll(); Assert.Single(reader.Calls);
        File.Delete(path); reader.Poll(); Assert.Empty(reader.Calls);
    }

    [Fact]
    public async Task Independent_writers_can_append_while_the_reader_polls()
    {
        var fixtures = Enumerable.Range(0, 8).Select(i => Rows("parallel-" + i, result: Envelope(), host: "host-" + i)).ToArray();
        using var reader = new CallJournalReader(root, false);
        var tasks = fixtures.Select((rows, index) => Task.Run(async () =>
        {
            using var stream = new FileStream(Path.Combine(root, "calls-" + index + ".jsonl"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            foreach (string row in rows)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(row + "\n");
                stream.Write(bytes, 0, bytes.Length / 2); stream.Flush();
                await Task.Delay(10);
                stream.Write(bytes, bytes.Length / 2, bytes.Length - bytes.Length / 2); stream.Flush();
            }
        })).ToArray();
        while (tasks.Any(t => !t.IsCompleted)) { reader.Poll(); await Task.Delay(5); }
        await Task.WhenAll(tasks); reader.Poll();
        Assert.Equal(8, reader.Calls.Count); Assert.All(reader.Calls, c => Assert.Equal("succeeded", c.DisplayOutcome));
    }

    [Fact]
    public async Task Live_polling_and_disposal_follow_file_changes()
    {
        using var reader = new CallJournalReader(root);
        var changed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.Changed += (_, _) => changed.TrySetResult(true);
        File.WriteAllLines(Path.Combine(root, "calls-live.jsonl"), Rows("live", result: Envelope()));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Single(reader.Calls);
        reader.Dispose(); File.Delete(Path.Combine(root, "calls-live.jsonl")); reader.Poll(); Assert.Single(reader.Calls);
    }

    [Fact]
    public void Oversized_and_malformed_rows_do_not_hide_the_next_valid_call()
    {
        File.WriteAllText(Path.Combine(root, "calls-bad.jsonl"), "{broken\n" + new string('x', 150000) + "\n"
            + string.Join("\n", Rows("good", result: Envelope())) + "\n");
        using var reader = new CallJournalReader(root, false); Assert.Single(reader.Calls);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("Pass_Word")]
    [InlineData("passwd")]
    [InlineData("pwd")]
    [InlineData("api-key")]
    [InlineData("key")]
    [InlineData("privateKey")]
    [InlineData("keys")]
    [InlineData("accessToken")]
    [InlineData("refresh_token")]
    [InlineData("clientSecret")]
    [InlineData("credentials")]
    [InlineData("Authorization")]
    [InlineData("Proxy-Authorization")]
    public void Every_credential_field_shape_is_redacted(string key)
    {
        string input = JsonSerializer.Serialize(new Dictionary<string, object> { ["nested"] = new[] { new Dictionary<string, object>
            { [key] = new { value = "private-value" }, ["echo"] = "private-value" } } });
        string output = CallJournalPayload.Redact(input);
        Assert.DoesNotContain("private-value", output); Assert.Contains("••••", output);
    }

    [Theory]
    [InlineData("{\"text\":\"Bearer private-value\"}")]
    [InlineData("{\"text\":\"Bearer private-value\",\"echo\":\"private-value\"}")]
    [InlineData("{\"text\":\"Basic private-value\"}")]
    [InlineData("{\"url\":\"http://user:private-value@localhost/?token=private-value\"}")]
    [InlineData("{\"name\":\"password\",\"value\":\"private-value\"}")]
    [InlineData("{\"text\":\"{\\\"password\\\":\\\"private-value\\\"}\"}")]
    [InlineData("{\"text\":\"-----BEGIN PRIVATE KEY-----private-value-----END PRIVATE KEY-----\"}")]
    public void Credentials_embedded_in_any_string_or_named_value_are_redacted(string input)
        => Assert.DoesNotContain("private-value", CallJournalPayload.Redact(input));

    [Fact]
    public void Fixed_limits_apply_after_redaction_and_preserve_unicode_boundaries()
    {
        string args = JsonSerializer.Serialize(new { password = "private-value", blockPath = new string('x', 8000) });
        var rows = Rows("large", args, Envelope());
        using var row = JsonDocument.Parse(rows.Last());
        Assert.True(row.RootElement.GetProperty("argumentsTruncated").GetBoolean());
        string text = row.RootElement.GetProperty("arguments").GetString();
        Assert.True(text.Length <= CallJournalPayload.Limit); Assert.EndsWith(CallJournalPayload.Truncated, text);
        Assert.DoesNotContain("private-value", string.Join("\n", rows));
        Assert.Equal(CallJournalPayload.Hidden, CallJournalPayload.Redact("{broken private-value"));
        Assert.True(CallJournalPayload.Redact(JsonSerializer.Serialize(new { data = new string('z', 12000) })).Length <= CallJournalPayload.Limit);
        var largeResult = JsonSerializer.Serialize(new { data = new string('z', 12000), token = "private-value" });
        using var resultRow = JsonDocument.Parse(Rows("large-result", result: largeResult).Last());
        Assert.True(resultRow.RootElement.GetProperty("resultTruncated").GetBoolean());
        Assert.True(resultRow.RootElement.GetProperty("result").GetString().Length <= CallJournalPayload.Limit);
        string unicode = new string('x', 4096 - CallJournalPayload.Truncated.Length - 1) + "😀" + new string('z', 100);
        string bounded = CallJournalPayload.Bound(unicode);
        Assert.False(char.IsHighSurrogate(bounded[bounded.Length - CallJournalPayload.Truncated.Length - 1]));
    }

    [Fact]
    public void A_result_echo_of_an_argument_credential_is_redacted_across_payloads()
    {
        string[] rows = Rows("echo", "{\"password\":\"private-value\"}", "{\"data\":{\"message\":\"private-value\"}}");
        Assert.DoesNotContain("private-value", string.Join("\n", rows));
        Assert.DoesNotContain("private-value", CallJournalPayload.Redact("\"Bearer private-value\""));
    }

    public void Dispose() { InvocationJournal.ConfigureOutput(null); Directory.Delete(root, true); }
}
