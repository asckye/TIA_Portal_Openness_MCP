extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using Host = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;
using Xunit;

namespace TiaMcp.FoundationHost.Tests;

public sealed class UnsettledStateTests
{
    [Theory, InlineData("14sp1"), InlineData("19"), InlineData("21")]
    public async Task Get_session_state_answers_from_the_host_while_unsettled(string release)
    {
        using var worker = new WorkerClient(release, "missing", "missing", false) { Bundled = false };
        var inner = new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "GetSessionState"), worker);
        inner.Unsettled.Record("unknown-request", "CreatePlcTag", "digest", DateTimeOffset.UtcNow);
        var result = await new FoundationV4Tool(inner, release).InvokeAsync(Request("GetSessionState"));
        Assert.True((bool?)result.StructuredContent?["ok"]);
        Assert.Equal("unknown-request", (string?)Assert.Single(result.StructuredContent!["data"]!["sessionState"]!["unsettled"]!.AsArray())!["requestId"]);
        Assert.Null(worker.OperatorSnapshot()["pid"]);
    }

    [Theory]
    [InlineData("GetProjectInfo")]
    [InlineData("ListPlcBlocks")]
    [InlineData("SaveProjectCopy")]
    [InlineData("GenerateAcceptanceReport")]
    [InlineData("RunOnlineMonitoringSafetySelfTest")]
    public async Task Independent_tools_pass_approval_precheck_while_unsettled(string name)
    {
        using var session = EngineHostParity.EnterScope();
        Host.Worker.Unsettled.Record("unknown-request", "CreatePlcTag", "digest", DateTimeOffset.UtcNow);
        var refusal = await Host.PrecheckBeforeApproval(name, "{}",
            _ => throw new Exception("A call with no write preview must not dispatch a precheck."), CancellationToken.None);
        Assert.Null(refusal);
        int calls = 0, approvals = 0;
        Host.ApprovalWaitOverrideForTests = (pending, _, _) => {
            approvals++;
            return Task.FromResult(new TiaOpenness.Shared.ApprovalOutcome(pending, false, null));
        };
        using var metadataWorker = new FakeWorker();
        var schema = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.Current.Catalog!.All.TryGetValue(name, out var descriptor)
            ? descriptor.Tool.InputSchema : FoundationTools.Create(metadataWorker).Single(item => item.ProtocolTool.Name == name).ProtocolTool.InputSchema;
        var tool = Host.WrapTools(new McpServerTool[] { new SessionStateTests.HostOnlyTool(_ => {
            calls++;
            return ValueTask.FromResult(Host.V4Result(name, new JsonObject()));
        }, name, schema) }).Single();
        var result = await tool.InvokeAsync(Request(name, name == "SaveProjectCopy" ? new() { ["newProjectPath"] = "C:/FixtureCopy" } : new()));
        Assert.True((bool?)result.StructuredContent?["ok"]);
        Assert.Equal(1, calls);
        Assert.Equal(name == "SaveProjectCopy" ? 1 : 0, approvals);
        Assert.Single(Host.Worker.Unsettled.Snapshot());
    }

    [Fact]
    public async Task Host_only_success_carries_no_reset_recovery()
    {
        using var worker = new WorkerClient("21", "missing", "missing", false) { Bundled = false };
        var inner = PortedToolContract.Create(worker, "21").OfType<FoundationTool>()
            .Single(t => t.ProtocolTool.Name == "PlanOnlineReadOnlyMonitoring");
        inner.Unsettled.Record("unknown-request", "CreatePlcTag", "digest", DateTimeOffset.UtcNow);
        var result = await new FoundationV4Tool(inner, "21").InvokeAsync(Request("PlanOnlineReadOnlyMonitoring", new JsonObject {
            ["softwarePath"] = "PLC_1", ["tagPaths"] = new JsonArray("DB_Status.Ready") }));
        Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent?.ToJsonString());
        Assert.False((bool?)result.StructuredContent?["meta"]?["requiresSessionReset"]);
        Assert.Null(result.StructuredContent?["data"]?["recovery"]);
    }

    private static RequestContext<CallToolRequestParams> Request(string name, JsonObject? arguments = null) => new(
        DispatchProxy.Create<IMcpServer, WorkerRobustnessTests.ServerProxy>()) {
            Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                (arguments ?? new JsonObject()).ToJsonString()) } };
}
