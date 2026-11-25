using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using TiaMcp.Engine.Tests.Shared;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using Xunit;

public sealed class ExternalSourceFailureTests
{
    private const string Tool = "GenerateBlocksFromExternalSource";

    private static JsonNode Record(string release)
    {
        string path = Path.Combine(TestPaths.RepositoryRoot(AppContext.BaseDirectory),
            "tests/FoundationHost/TiaMcp.FoundationHost.Tests/Fixtures/ExternalSourceFailures.json");
        return JsonNode.Parse(File.ReadAllText(path))![release]!;
    }

    private static JsonObject FailedReply(string release, string message, string source = "Pump.scl")
    {
        var raw = ExternalSourceWorkflowTests.Payload(Tool, new JsonObject { ["externalSourceName"] = source, ["softwarePath"] = "devices/PLC_1/CPU" });
        raw["Release"] = release;
        if (release == "14sp1")
        {
            raw["GenerationOption"] = "parameterless";
            raw["ResultBasis"] = "inventory-observation-only; native API returns void";
        }
        raw["Status"] = "failed";
        raw["Attempted"] = true;
        raw["Error"] = message;
        return raw;
    }

    private static JsonNode Envelope(JsonNode raw) => Response(raw).StructuredContent!;

    private static CallToolResult Response(JsonNode raw)
    {
        var args = new JsonObject {
            ["externalSourceName"] = raw["SourceName"]!.DeepClone(), ["softwarePath"] = raw["SoftwarePath"]!.DeepClone(),
            ["expectedProjectFile"] = raw["ProjectFile"]!.DeepClone(), ["expectedPlanHash"] = raw["PlanHash"]!.DeepClone(),
            ["dryRun"] = false, ["confirm"] = true };
        WorkerProtocol.ValidateExchangeResult(Tool, args, raw);
        var definition = FoundationTools.Definitions.Single(item => item.Name == Tool);
        return FoundationV4Result.Worker((string)raw["Release"]!, definition, "generation-failure", args, raw, raw);
    }

    private static void AssertRecordedFailure(JsonNode envelope, string recorded)
    {
        var data = envelope["data"]!;
        Assert.Equal(recorded, (string?)data["error"]);
        Assert.True((bool?)data["attempted"]);
        Assert.False((bool?)data["executed"]);
        Assert.False((bool?)data["outcomeUnknown"]);
        Assert.Empty(data["objectsAfter"]!.AsArray());
        Assert.Empty(data["generatedObjects"]!.AsArray());
        Assert.Empty(data["observedChanges"]!.AsArray());
        Assert.Equal("failed", (string?)envelope["meta"]!["outcome"]);
        Assert.Equal("completed", (string?)envelope["meta"]!["execution"]);
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)envelope["error"]!["code"]);
        string expected = recorded.TrimEnd().Replace("\r\n\r\n", " ");
        Assert.Equal(expected, (string?)envelope["error"]!["message"]);
        Assert.Equal(expected, (string?)envelope["error"]!["details"]!["nativeMessage"]);
    }

    [Theory]
    [InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17")]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Host_reports_the_recorded_compiler_reason(string release)
    {
        var record = Record(release);
        string message = (string)record["error"]!;
        AssertRecordedFailure(Envelope(FailedReply(release, message, (string)record["sourceName"]!)), message);
    }

    [Fact]
    public void Net48_generation_refusal_carries_the_complete_recorded_text_to_the_host()
    {
        string recorded = (string)Record("19")["error"]!;
        const string prefix = "EngineeringTargetInvocationException: ";
        Assert.StartsWith(prefix, recorded);
        var input = new JsonObject { ["message"] = recorded[prefix.Length..] };
        var raw = SafetyLoginFrameworkTests.RunFixture("generate-source", "compiler-message", input)["generation"]!;
        AssertRecordedFailure(Envelope(raw), recorded);
    }

    [Fact]
    public void Compiler_line_numbers_and_symbols_survive_a_single_digit_assignment()
    {
        const string raw = "Line 000011: #MonKey := 1; invalid expression in FC11, operand #A_1.";
        const string expected = "Line 000011: #MonKey := ••••; invalid expression in FC11, operand #A_1.";
        var result = Response(FailedReply("21", raw));
        var envelope = result.StructuredContent!;
        Assert.Equal(expected, (string?)envelope["error"]!["message"]);
        Assert.Equal(expected, (string?)envelope["error"]!["details"]!["nativeMessage"]);
        Assert.Equal(raw, (string?)envelope["data"]!["error"]);
        string text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(JsonNode.DeepEquals(envelope, JsonNode.Parse(text)));
    }

    [Fact]
    public void Request_secret_redaction_timeout_returns_the_generic_generation_error()
    {
        var error = FoundationV4Result.GenerationFailure(new JsonObject(), "unsanitized compiler text", _ => throw new RegexMatchTimeoutException());
        Assert.Equal(ErrorCode.NativeOperationFailed, error.Code);
        Assert.Equal("The operation did not establish success; inspect the returned result.", error.Message);
        Assert.Null(Assert.IsType<NativeOperationFailedDetails>(error.Details).NativeMessage);
    }

    [Fact]
    public void Error_messages_mask_sensitive_details_and_stop_at_the_stack()
    {
        const string raw = "Generation failed\r\n\r\nBad symbol\u2028[Error] password = private pass phrase\nPath C:\\private\\x.scl"
            + "\n   at Siemens.Engineering.Provider.Invoke()\nstack tail";
        var envelope = Envelope(FailedReply("20", raw));
        const string expected = "Generation failed Bad symbol [Error] •••• Path <path>";
        Assert.Equal(expected, (string?)envelope["error"]!["message"]);
        Assert.Equal(expected, (string?)envelope["error"]!["details"]!["nativeMessage"]);
        Assert.Equal(raw, (string?)envelope["data"]!["error"]);
    }

    [Fact]
    public void Quoted_credentials_are_masked_before_splitting_compiler_lines()
    {
        const string raw = "Generation failed\r\npassword=\"private\r\ncontinuation\"\r\nBad symbol";
        var envelope = Envelope(FailedReply("18", raw));
        const string expected = "Generation failed password=•••• Bad symbol";
        Assert.Equal(expected, (string?)envelope["error"]!["message"]);
        Assert.Equal(expected, (string?)envelope["error"]!["details"]!["nativeMessage"]);
    }

    [Fact]
    public void Structured_detail_keeps_credential_values_private_across_lines()
    {
        const string raw = "Generation failed\n[Error] {\n  \"password\": [\n    \"private entry\"\n  ]\n}";
        var envelope = Envelope(FailedReply("20", raw));
        string message = (string)envelope["error"]!["message"]!;
        Assert.StartsWith("Generation failed", message);
        Assert.DoesNotContain("private entry", message);
        Assert.Equal(message, (string?)envelope["error"]!["details"]!["nativeMessage"]);
    }

    [Fact]
    public void Interrupted_generation_keeps_the_unknown_outcome_and_recovery_message()
    {
        const string interrupted = "Connection lost during generation";
        var raw = FailedReply("16", interrupted);
        raw["Status"] = "outcome-unknown";
        raw["OutcomeUnknown"] = true;
        var envelope = Envelope(raw);
        Assert.Equal("unknown", (string?)envelope["meta"]!["outcome"]);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)envelope["error"]!["code"]);
        Assert.Contains("Do not repeat", (string?)envelope["error"]!["message"]);
        Assert.Equal(interrupted, (string?)envelope["data"]!["error"]);
    }

    [Fact]
    public void Bound_compiler_detail_on_UTF8_boundaries()
    {
        string raw = "Generation failed\n错误 " + new string('块', 4096) + " final diagnostic";
        var envelope = Envelope(FailedReply("21", raw));
        string message = (string)envelope["error"]!["message"]!;
        Assert.StartsWith("Generation failed 错误 块", message);
        Assert.DoesNotContain("final diagnostic", message);
        Assert.DoesNotContain('\uFFFD', message);
        Assert.InRange(Encoding.UTF8.GetByteCount(message), 1, 4096);
        Assert.Equal(message, (string?)envelope["error"]!["details"]!["nativeMessage"]);
        Assert.Equal(raw, (string?)envelope["data"]!["error"]);
    }

    [Fact]
    public void Excerpt_contains_at_most_forty_nonempty_compiler_lines()
    {
        var lines = Enumerable.Range(1, 45).Select(i => $"Source line {i}").ToArray();
        string raw = string.Join("\r\n\r\n", lines);
        var envelope = Envelope(FailedReply("21", raw));
        string expected = string.Join(" ", lines.Take(40));
        Assert.Equal(expected, (string?)envelope["error"]!["message"]);
        Assert.Equal(expected, (string?)envelope["error"]!["details"]!["nativeMessage"]);
        Assert.Equal(raw, (string?)envelope["data"]!["error"]);
    }
}
