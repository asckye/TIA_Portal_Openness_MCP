extern alias toolpipeline;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using Xunit;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Suite = toolpipeline::TiaMcp.Shared.TestSuitePortService;

public sealed class TestSuiteOutcomeTests
{
    [Theory]
    [InlineData("Success", "succeeded")]
    [InlineData("Information", "succeeded")]
    [InlineData("Error", "failed")]
    [InlineData("Warning", "failed")]
    public void Returned_native_verdict_is_known_in_the_shared_contract_and_the_actual_tool(string state, string outcome)
    {
        using var release = Host.HardwareContract.UseRelease("21");
        var reply = Reply(state);
        var mapped = Host.OptionalPackageContract.Map("RunTestSuiteCase", new Host.ResponseMessage {
            Message = "Native TestResults", Meta = reply["Meta"]!.DeepClone().AsObject() }, true).StructuredContent!;
        var tool = new Host.TestSuiteTools(new Suite((operation, args) => {
            Assert.Equal("optional-products.ExecuteOptionalProduct", operation);
            Assert.Equal("RunTestSuiteCase", (string?)args["request"]?["Operation"]);
            Assert.True((bool?)args["confirm"]); Assert.False((bool?)args["dryRun"]);
            return reply.DeepClone();
        }, () => @"D:\fixture\project.ap21"));
        var result = tool.RunTestSuiteCase("application", "TC_A", confirmExternalExecution: true, dryRun: false).StructuredContent!;
        foreach (var body in new[] { mapped, result })
        {
            Assert.Equal(outcome, (string?)body["meta"]?["outcome"]);
            Assert.Equal("completed", (string?)body["meta"]?["execution"]);
            Assert.Equal("complete", (string?)body["meta"]?["completeness"]);
            Assert.Equal(outcome == "failed" ? "NATIVE_OPERATION_FAILED" : null, (string?)body["error"]?["code"]);
        }
    }

    public static IEnumerable<object[]> UncertainResults()
        => new[] { "Success", "Information", "Error", "Warning" }.SelectMany(state =>
            new[] { "writeOutcomeUnknown", "connectionUnavailable", "missing-result", "lost-portal" }
                .Select(reason => new object[] { state, reason }));

    [Theory, MemberData(nameof(UncertainResults))]
    public void Explicit_uncertainty_and_missing_native_results_override_a_test_verdict(string state, string reason)
    {
        using var release = Host.HardwareContract.UseRelease("21");
        var reply = Reply(state); var meta = reply["Meta"]!.AsObject();
        if (reason == "missing-result") meta["nativeResultReturned"] = false;
        else if (reason == "lost-portal") meta["status"] = "HmiConnectionUnavailable";
        else meta[reason] = true;
        var tool = new Host.TestSuiteTools(new Suite((_, _) => reply, () => @"D:\fixture\project.ap21"));
        var body = tool.RunTestSuiteCase("application", "TC_A", confirmExternalExecution: true, dryRun: false).StructuredContent!;
        Assert.Equal("unknown", (string?)body["meta"]?["outcome"]);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
        Assert.Equal("unknown", (string?)body["meta"]?["execution"]);
    }

    private static JsonObject Reply(string state) => new() { ["Message"] = "Native TestResults", ["Meta"] = new JsonObject {
        ["operationStarted"] = true, ["nativeResultReturned"] = true, ["nativeState"] = state,
        ["nativeStateType"] = NativeResultStates.TestSuite, ["mayHaveChanged"] = true,
        ["success"] = true, ["apiCallSuccess"] = true, ["operationSuccess"] = true, ["testPassed"] = state is "Success" or "Information",
        ["result"] = new JsonObject { ["state"] = state, ["errorCount"] = state == "Error" ? 1 : 0,
            ["warningCount"] = state == "Warning" ? 1 : 0, ["messages"] = new JsonArray() } } };
}
