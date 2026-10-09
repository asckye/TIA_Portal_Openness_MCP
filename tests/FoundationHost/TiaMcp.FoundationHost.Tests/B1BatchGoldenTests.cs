extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using Host = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;
using Xunit;

// Ported from ResponseGoldenTests.CaptureBatch: legacy verdicts never cross the
// tool boundary; the host aggregate retains the complete V4 child and its verdict.
public sealed class B1BatchGoldenTests
{
    [Theory]
    [InlineData("success", true)]
    [InlineData("operation-false", false)]
    [InlineData("argument", false)]
    [InlineData("portal", false)]
    [InlineData("target-invocation", false)]
    [InlineData("missing-verdict", false)]
    public void HostRetainsGoldenBatchVerdicts(string name, bool succeeded)
    {
        using var scope = EngineHostParity.EnterScope();
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        object? Invoke(string method, params object?[] values) => typeof(Host).GetMethod(method, flags)!.Invoke(null, values);
        var data = new JsonObject { ["Message"] = name, ["Meta"] = new JsonObject {
            ["rows"] = new JsonArray(1, "中文", null), ["timestamp"] = "2001-02-03T04:05:06.1234567+08:00"
        } };
        if (name != "missing-verdict") data["Meta"]!["success"] = succeeded;
        Assert.Null(Invoke("ResultSucceeded", data));
        var target = new enginehost::TiaMcpServer.ModelContextProtocol.ResponseMessage { Message = name, Meta = data["Meta"]!.DeepClone().AsObject() };
        var rejection = Assert.Throws<TargetInvocationException>(() => Invoke("ToolResult", target));
        Assert.True(rejection.InnerException is JsonException or InvalidDataException);
        var factory = typeof(Host).GetMethods(flags).Single(m => m.Name == "V4Result" && m.GetParameters().Length == 8);
        var parameters = factory.GetParameters();
        var error = succeeded ? null : Invoke("InvalidInput", "fixture");
        var child = (CallToolResult)factory.Invoke(null, new object?[] { "GoldenHmi", data, error,
            Enum.Parse(parameters[3].ParameterType, succeeded ? "Succeeded" : "ReadFailed"),
            Enum.Parse(parameters[4].ParameterType, "ReadOnly"),
            Enum.Parse(parameters[5].ParameterType, succeeded ? "Complete" : "None"), null, false })!;
        var row = (JsonObject)Invoke("BatchRow", 0, "GoldenHmi", child)!;
        var rows = new JsonArray(row);
        string retained = rows.ToJsonString();
        var result = (CallToolResult)Invoke("BatchResult", "RunReadOnlyToolBatch", rows, false)!;
        var body = result.StructuredContent!.AsObject();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(((TextContentBlock)Assert.Single(result.Content)).Text), body));
        Assert.Equal(4, (int)body["schemaVersion"]!);
        Assert.Equal(succeeded, (bool)body["ok"]!);
        Assert.Equal(!succeeded, result.IsError);
        Assert.Equal(succeeded, body["error"] == null);
        Assert.Equal(succeeded ? "succeeded" : "read-failed", (string?)body["meta"]!["outcome"]);
        Assert.Equal("read-only", (string?)body["meta"]!["execution"]);
        Assert.Equal(succeeded ? "complete" : "none", (string?)body["meta"]!["completeness"]);
        Assert.Equal(retained, body["data"]!["items"]!.ToJsonString());
        Assert.False((bool)body["data"]!["rollbackPerformed"]!);
    }
}
