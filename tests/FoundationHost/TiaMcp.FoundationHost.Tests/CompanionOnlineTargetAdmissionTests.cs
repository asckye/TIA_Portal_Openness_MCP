extern alias toolpipeline;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.V4.Inputs;
using TiaOpenness.Shared;
using Xunit;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Engine = toolpipeline::TiaMcp.FoundationHost;

public sealed class CompanionOnlineTargetAdmissionTests
{
    public static IEnumerable<object[]> ConfiguredLists =>
        from release in ReleaseRange.All
        from state in new[] { "listed", "empty", "invalid", "unreadable" }
        from commandGroup in new[] { "net", "sim", "trace" }
        from preview in new[] { false, true }
        select new object[] { release, state, commandGroup, preview };

    [Theory, MemberData(nameof(ConfiguredLists))]
    public async Task Run_refuses_configured_lists_before_approval_and_companion_start(
        string release, string state, string group, bool preview)
    {
        using var settings = new OnlineTargetSettingsScope(state switch {
            "empty" => "", "invalid" => "target=invalid", _ => "target=192.168.1.10" });
        using var locked = state == "unreadable"
            ? new FileStream(OnlineTargetSettings.SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        using var counter = new CompanionCounter();
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release);
        using var session = fixture.EnterSession();
        counter.Install();
        var input = new { workingDirectory = fixture.Root, mode = "run", arguments = new[] { group, "--help" }, dryRun = preview };
        foreach (bool nested in new[] { false, true })
        {
            var body = (await fixture.Call(nested ? "CallTool" : "RunPlcCompanionTool",
                nested ? new { name = "RunPlcCompanionTool", arguments = input } : (object)input)).StructuredContent!;
            Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
            Assert.Equal(state is "invalid" or "unreadable" ? "online-target-list-invalid" : "online-target-not-allowed",
                (string?)body["error"]?["details"]?["condition"]);
            Assert.Contains("companion cannot be limited to listed hosts", (string?)body["error"]?["message"]);
            Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        }
        Assert.Equal(0, counter.Approvals);
        Assert.Equal(0, counter.Starts);
        Assert.Equal(0, fixture.Worker.Calls);
    }

    public static IEnumerable<object[]> AllowedModes =>
        from release in ReleaseRange.All
        from mode in new[] { "run", "catalog", "help" }
        select new object[] { release, mode };

    [Theory, MemberData(nameof(AllowedModes))]
    public async Task Missing_list_preserves_run_and_configured_lists_allow_non_connecting_modes(string release, string mode)
    {
        using var settings = new OnlineTargetSettingsScope(mode == "run" ? null : "target=invalid");
        using var counter = new CompanionCounter();
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release);
        using var session = fixture.EnterSession();
        counter.Install();
        var input = new { workingDirectory = fixture.Root, mode, arguments = new[] { "net", "--help" }, dryRun = false };
        Assert.Null(Host.McpServer.ToolInvoker.Bind("RunPlcCompanionTool", new ToolArguments(JsonSerializer.SerializeToElement(input)), out var call));
        Assert.NotNull(call);
        var body = (await fixture.Call("RunPlcCompanionTool", input)).StructuredContent!;
        Assert.DoesNotContain("online-target-", body.ToJsonString());
        Assert.Equal(mode == "run" ? 1 : 2, counter.Starts);
        Assert.Equal(1, counter.Approvals);
    }

    private sealed class CompanionCounter : IDisposable
    {
        private readonly string? previousPython = Environment.GetEnvironmentVariable("TIA_MCP_PLC_TOOLS_PYTHON");
        private readonly Func<PendingApproval, ApprovalSettings, CancellationToken, Task<ApprovalOutcome>>? previousWait
            = Host.McpServer.ApprovalWaitOverrideForTests;
        internal int Approvals;
        internal int Starts;
        internal CompanionCounter()
        {
            Environment.SetEnvironmentVariable("TIA_MCP_PLC_TOOLS_PYTHON", Environment.ProcessPath);
            Host.McpServer.ApprovalWaitOverrideForTests = (card, _, _) => {
                Approvals++; return Task.FromResult(new ApprovalOutcome(card, true, null)); };
        }
        internal void Install() => ((Engine.WorkerToolInvoker)Host.McpServer.ToolInvoker).SetLocalTarget(typeof(Host.EcosystemTools),
            new Host.EcosystemTools { CompanionRun = (_, _, _, _, _) => {
                Starts++; return Task.FromResult(new JsonObject { ["success"] = true, ["exitCode"] = 0, ["stdout"] = "fixture" }); } });
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("TIA_MCP_PLC_TOOLS_PYTHON", previousPython);
            Host.McpServer.ApprovalWaitOverrideForTests = previousWait;
        }
    }
}
