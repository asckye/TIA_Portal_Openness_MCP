extern alias enginehost;
using System.Text.Json.Nodes;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Microsoft.Extensions.DependencyInjection;
using TiaMcp.FoundationHost;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Services = enginehost::TiaMcpServer.Siemens.Services;
using Xunit;

public sealed class VersionControlAdmissionTests
{
    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Git_usage_preserves_the_commit_message_parameter_and_needs_no_worker(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tools = LegacyHostToolRegistry.Create(worker, release, false);
        var server = DispatchProxy.Create<IMcpServer, ServerProxy>();
        var usage = await tools.Single(t => t.ProtocolTool.Name == "GetToolUsage").InvokeAsync(new(server) {
            Params = new() { Name = "GetToolUsage", Arguments = Arguments("{\"toolName\":\"ManagePlcGitRepository\"}") } });
        var schema = usage.StructuredContent!["data"]!["inputSchema"]!;
        Assert.True(JsonNode.DeepEquals(schema, JsonNode.Parse(tools.Single(t => t.ProtocolTool.Name == "ManagePlcGitRepository").ProtocolTool.InputSchema.GetRawText())));
        Assert.NotNull(schema["properties"]!["message"]);
        Assert.Null(schema["properties"]!["summary"]);
        await tools.Single(t => t.ProtocolTool.Name == "ManagePlcGitRepository").InvokeAsync(new(server) {
            Params = new() { Name = "ManagePlcGitRepository", Arguments = Arguments("{\"repositoryPath\":\"\"}") } });
        Assert.Equal(0, worker.Calls);
    }

    private static Dictionary<string, JsonElement> Arguments(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    public class ServerProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
            => method!.Name == "get_Services" ? new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider() : null;
    }

    [Theory]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void An_unbound_workspace_read_keeps_the_original_project_refusal_without_dispatch(string release)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        var service = new Services.VersionControlPortService((_, _) => throw new InvalidOperationException("No worker dispatch expected."), () => "");
        var result = new Host.VersionControlTools(service).ListVersionControlWorkspacesV4();
        var body = result.StructuredContent!;
        Assert.Equal("PROJECT_NOT_BOUND", (string?)body["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.False((bool)body["meta"]!["requiresSessionReset"]!);
        Assert.Empty(body["data"]!["items"]!.AsArray());
        Assert.Equal("PROJECT_NOT_BOUND", (string?)body["data"]?["evidence"]?["v4Rejection"]);
    }
}
