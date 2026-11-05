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

public sealed class SessionResetGateTests
{
    [Theory, InlineData("14sp1"), InlineData("19"), InlineData("21")]
    public async Task Get_session_state_answers_from_the_host_during_a_reset(string release)
    {
        using var worker = new WorkerClient(release, "missing", "missing", false) { Bundled = false };
        var inner = new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "GetSessionState"), worker);
        inner.MarkSessionUncertain();
        var result = await new FoundationV4Tool(inner, release).InvokeAsync(Request("GetSessionState"));
        Assert.True((bool?)result.StructuredContent?["ok"]);
        Assert.True((bool?)result.StructuredContent?["data"]?["sessionState"]?["requiresSessionReset"]);
        Assert.Null(worker.OperatorSnapshot()["pid"]);
    }

    [Theory]
    [InlineData("ExtractPlcBlockMetrics")]
    [InlineData("GenerateErrorReport")]
    [InlineData("InstantiatePlcTemplates")]
    [InlineData("ManagePlcGitRepository")]
    [InlineData("ManageUnifiedCwcPackage")]
    [InlineData("RunHmiActionScriptRecipeSafetySelfTest")]
    [InlineData("RunOfflineReleaseValidationSuite")]
    public void Host_tools_pass_the_reset_precheck(string name)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, "21");
        Host.MarkSharedSessionUncertain();
        Assert.True(Host.SessionLatched());
        Assert.Null(Host.SessionPrecheckRefusal(name));
    }

    [Theory]
    [InlineData("GetProjectInfo")]
    [InlineData("ListPlcBlocks")]
    [InlineData("SaveProjectCopy")]
    [InlineData("GenerateAcceptanceReport")]
    [InlineData("RunOnlineMonitoringSafetySelfTest")]
    public void Native_and_worker_observing_tools_stay_refused_during_a_reset(string name)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, "21");
        Host.MarkSharedSessionUncertain();
        Assert.Equal("SESSION_RESET_REQUIRED", (string?)Host.SessionPrecheckRefusal(name)?.StructuredContent?["error"]?["code"]);
    }

    [Fact]
    public async Task Host_only_success_carries_no_reset_recovery()
    {
        using var worker = new WorkerClient("21", "missing", "missing", false) { Bundled = false };
        var inner = PortedToolContract.Create(worker, "21").OfType<FoundationTool>()
            .Single(t => t.ProtocolTool.Name == "PlanOnlineReadOnlyMonitoring");
        inner.MarkSessionUncertain();
        var result = await new FoundationV4Tool(inner, "21").InvokeAsync(Request("PlanOnlineReadOnlyMonitoring", new JsonObject {
            ["softwarePath"] = "PLC_1", ["tagPaths"] = new JsonArray("DB_Status.Ready") }));
        Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent?.ToJsonString());
        Assert.False((bool?)result.StructuredContent?["meta"]?["requiresSessionReset"]);
        Assert.Null(result.StructuredContent?["data"]?["recovery"]);
    }

    [Fact]
    public void Session_lock_probe_is_unchanged()
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, "21");
        Assert.False(Host.SessionLatched());
        Host.MarkSharedSessionUncertain();
        Assert.True(Host.SessionLatched());
        Assert.Null(Host.SessionPrecheckRefusal("GetSessionState"));
        Assert.NotNull(Host.SessionPrecheckRefusal("SaveProject"));
    }

    private static RequestContext<CallToolRequestParams> Request(string name, JsonObject? arguments = null) => new(
        DispatchProxy.Create<IMcpServer, WorkerRobustnessTests.ServerProxy>()) {
            Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                (arguments ?? new JsonObject()).ToJsonString()) } };
}
