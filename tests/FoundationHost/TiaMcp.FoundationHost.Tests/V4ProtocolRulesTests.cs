using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using Xunit;

public sealed class V4ProtocolRulesTests
{
    [Theory]
    [InlineData(false, "rejected-before-operation", "not-started", "tool-queue")]
    [InlineData(true, "read-failed", "read-only", "tool-call")]
    public void Read_cancellation_reports_the_actual_dispatch_stage(bool dispatched, string outcome, string execution, string stage)
    {
        var result = FoundationV4Result.Failure("19", "GetProjectInfo", "id", dispatched, false, null, new OperationCanceledException());
        var body = result.StructuredContent!;
        Assert.Equal("CANCELLED", (string?)body["error"]!["code"]);
        Assert.Equal(stage, (string?)body["error"]!["details"]!["stage"]);
        Assert.Equal(outcome, (string?)body["meta"]!["outcome"]);
        Assert.Equal(execution, (string?)body["meta"]!["execution"]);
        V4Json.Deserialize<Envelope>(body.ToJsonString());
    }

    [Fact]
    public void Invalid_update_repository_is_an_argument_refusal_with_stable_message()
    {
        var raw = new JsonObject { ["success"] = false, ["summary"] = "repository must be 'owner/name'." };
        var args = new Dictionary<string, JsonElement> { ["repository"] = JsonSerializer.SerializeToElement("x") };
        var result = FoundationV4Result.Host("19", "CheckProductUpdate", "id", raw, false, args: args);
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent!["error"]!["code"]);
        Assert.Equal("repository", (string?)result.StructuredContent!["error"]!["details"]!["parameter"]);
        Assert.Equal("The operation did not establish success; inspect the returned result.", (string?)result.StructuredContent!["error"]!["message"]);
        Assert.Equal("rejected-before-operation", (string?)result.StructuredContent!["meta"]!["outcome"]);
    }

    [Fact]
    public void Native_capability_limit_is_used_and_output_schema_is_shared()
    {
        var result = FoundationV4Result.Reject("19", "CompileDevice", "id",
            new Error("Unavailable.", new UnsupportedCapabilityDetails("19", "compile", "read")));
        Assert.Contains(result.StructuredContent!["meta"]!["warnings"]!.AsArray(), w => (string?)w!["code"] == "NATIVE_CAPABILITY_LIMIT");
        Assert.Equal(EnvelopeSchema.Value.GetRawText(), FoundationV4Result.Schema.GetRawText());
        var meta = FoundationV4Result.Schema.GetProperty("properties").GetProperty("meta");
        Assert.False(meta.TryGetProperty("additionalProperties", out _));
    }
}
