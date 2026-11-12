extern alias enginehost;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4.Inputs;
using Host = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;

internal static class UnsettledOutcomeAssertions
{
    internal static void Failure(Exception error, bool dispatched, bool mutation, bool expectedUnsettled,
        Action<bool, string> check, string message)
    {
        string tool = mutation ? "SaveProject" : "GetProjectInfo";
        var result = FoundationV4Result.Failure("21", tool, "observed-failure", dispatched, mutation, null, error);
        Result(tool, result, mutation, expectedUnsettled, check, message);
    }

    internal static void Result(string tool, CallToolResult result, bool mutation, bool expectedUnsettled,
        Action<bool, string> check, string message)
    {
        using var session = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(
            new enginehost::TiaMcp.FoundationHost.EngineSessionContext(new SharedPipelineTests.Worker()));
        using var scope = new SharedPipelineTests.Scope();
        Host.ObserveSessionOutcome(tool, result, mutation, "{\"dryRun\":false}");
        var calls = scope.Worker.Unsettled.Snapshot();
        check(expectedUnsettled ? calls.Count == 1 && calls[0].RequestId == (string?)result.StructuredContent?["meta"]?["requestId"]
            : calls.Count == 0, message + ": final outcome controls the real session list");
        var next = Host.CallTool(SessionFixtureTool.Name, new ToolArguments(JsonSerializer.SerializeToElement(new JsonObject())));
        check((bool?)next.StructuredContent?["ok"] == true && scope.Worker.EngineCalls == 1,
            message + ": the next independent call reaches the worker");
        check(scope.Worker.Unsettled.Snapshot().Count == calls.Count, message + ": the independent call retains earlier evidence");
    }

    internal static void Reply(string tool, JsonObject arguments, JsonNode raw, bool expectedUnsettled,
        Action<bool, string> check, string message)
    {
        var definition = FoundationTools.Definitions.Single(item => item.Name == tool);
        var result = FoundationV4Result.Worker("21", definition, "observed-reply", arguments, raw, raw);
        Result(tool, result, FoundationV4Result.IsMutation(definition, arguments), expectedUnsettled, check, message);
    }
}
