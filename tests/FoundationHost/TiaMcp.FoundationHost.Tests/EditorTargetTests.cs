extern alias toolpipeline;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed class EditorTargetTests
{
    [Theory]
    [InlineData("ShowObjectInEditor")]
    [InlineData("ShowObjectInEditorV4")]
    public void Editor_ui_requires_a_project_while_workbench_ui_stays_independent(string tool)
    {
        Assert.True(PreflightLogic.NeedsProject("UI", tool));
        Assert.False(PreflightLogic.NeedsProject("UI", "ShowWorkbenchPage"));
        var text = string.Join(" ", PreflightLogic.Precautions("UI", "Project", new PreflightLogic.Report(), false,
            Array.Empty<PreflightLogic.ParameterSpec>()));
        Assert.Contains("Workbench or TIA Portal editor", text, StringComparison.Ordinal);
        Assert.Contains("no project change", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("setScope")]
    [InlineData("copyScope")]
    [InlineData("createFromMasterCopy")]
    public void Every_mutating_test_suite_action_keeps_write_approval(string action)
    {
        Assert.True(TiaMcp.Logic.V4.HostBehavior.ApprovalWrite("ManageTestSuiteCase",
            new JsonObject { ["action"] = action, ["dryRun"] = false }, true, true, true));
    }

    [Theory, InlineData(true), InlineData(false)]
    public async Task V20_omitted_kind_gives_device_view_guidance_before_dispatch(bool dryRun)
    {
        using var fixture = new Fixture("20");
        var body = await fixture.Call("ShowObjectInEditor", new JsonObject { ["devicePath"] = new JsonArray("Station"), ["dryRun"] = dryRun });
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)body["error"]?["code"]);
        Assert.Contains("view=device|network|topology", (string?)body["error"]?["message"], StringComparison.Ordinal);
        Assert.Equal(0, fixture.Approvals);
        Assert.Equal(0, fixture.Worker.Calls);
    }

    private static readonly string[] Releases = { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" };
    public static IEnumerable<object[]> Targets() => from release in Releases
        from target in new[] { "plcWatchTable", "plcForceTable", "device", "network", "topology", "" }
        from preview in new[] { false, true }
        select new object[] { release, target, preview };

    [Theory, MemberData(nameof(Targets))]
    public async Task Official_editor_targets_forward_exact_paths_and_views_without_approval(string release, string target, bool preview)
    {
        bool table = target.StartsWith("plc", StringComparison.Ordinal);
        var arguments = new JsonObject { ["kind"] = table ? target : "device", ["dryRun"] = preview };
        if (table) { arguments["softwarePath"] = "PLC"; arguments["objectPath"] = "组/表%2F一"; }
        else { arguments["devicePath"] = new JsonArray("Station/Exact"); arguments["view"] = target; }
        using var fixture = new Fixture(release);
        var body = await fixture.Call("ShowObjectInEditor", arguments);
        bool v20Device = !table && target == "" && release == "20";
        bool unsupported = table && release == "14sp1" || v20Device;
        if (unsupported)
        {
            Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)body["error"]?["code"]);
            if (v20Device) Assert.Contains("view=device|network|topology", (string?)body["error"]?["message"], StringComparison.Ordinal);
        }
        else
        {
            Assert.True((bool?)body["ok"], body.ToJsonString());
            var request = fixture.Worker.Request!;
            Assert.Equal(table ? target : "device", (string?)request["Kind"]);
            Assert.Equal(table ? "组/表%2F一" : "", (string?)request["ObjectPath"]);
            Assert.Equal(table ? "" : target, (string?)request["View"]);
            if (!table) Assert.Equal("Station/Exact", (string?)request["DevicePath"]![0]);
        }
        Assert.Equal(0, fixture.Approvals);
        Assert.Equal(unsupported ? 0 : 1, fixture.Worker.Calls);
        Assert.Equal(unsupported || preview ? 0 : 1, fixture.Worker.UiCalls);
    }

    [Theory]
    [InlineData("plcBlock", "network")]
    [InlineData("plcWatchTable", "network")]
    [InlineData("plcForceTable", "topology")]
    [InlineData("deviceItem", "device")]
    [InlineData("device", "bad")]
    public async Task Invalid_view_refuses_before_worker_dispatch(string kind, string view)
    {
        using var fixture = new Fixture("21");
        var body = await fixture.Call("ShowObjectInEditor", new JsonObject { ["kind"] = kind, ["view"] = view,
            ["devicePath"] = new JsonArray("Station"), ["itemPath"] = new JsonArray("CPU"),
            ["softwarePath"] = "PLC", ["objectPath"] = "Table" });
        Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
        Assert.Equal(0, fixture.Worker.Calls);
        Assert.Equal(0, fixture.Approvals);
    }

    public static IEnumerable<object[]> Cases() => from release in new[] { "17", "18", "19", "20", "21" }
        from category in new[] { "styleGuide", "application" }
        from kind in category == "application" && release is "20" or "21" ? new[] { "case", "testSet" } : new[] { "case" }
        from preview in new[] { false, true }
        select new object[] { release, category, kind, preview };

    [Theory, MemberData(nameof(Cases))]
    public async Task Test_suite_editor_action_is_ui_only_while_the_tool_retains_write_classification(
        string release, string category, string kind, bool preview)
    {
        Assert.Equal("WRITE", ToolMetadata.Find("ManageTestSuiteCase")!.Operation);
        Assert.Equal("WRITE", ToolMetadata.RuntimeOperation("ManageTestSuiteCase"));
        using var fixture = new Fixture(release);
        var body = await fixture.Call("ManageTestSuiteCase", new JsonObject { ["category"] = category, ["kind"] = kind,
            ["name"] = "ExactCase", ["action"] = "showInEditor", ["dryRun"] = preview });
        Assert.True((bool?)body["ok"], body.ToJsonString());
        Assert.Equal(0, fixture.Approvals);
        Assert.Equal(1, fixture.Worker.Calls);
        Assert.Equal(preview ? 0 : 1, fixture.Worker.UiCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Test_suite_editor_requires_a_portal_window_even_for_preview(bool preview)
    {
        using var fixture = new Fixture("21");
        fixture.Worker.WithUserInterface = false;
        var body = await fixture.Call("ManageTestSuiteCase", new JsonObject { ["category"] = "styleGuide", ["name"] = "ExactCase",
            ["action"] = "showInEditor", ["dryRun"] = preview });
        Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal(0, fixture.Approvals);
        Assert.Equal(0, fixture.Worker.UiCalls);
    }

    [Fact]
    public async Task Served_test_suite_editor_example_executes_the_ui_action()
    {
        using var fixture = new DiscoveryFixture("21");
        var body = await fixture.Call("GetToolUsage", new { toolName = "ManageTestSuiteCase", operation = "showInEditor" });
        Assert.True((bool?)body["ok"], body.ToJsonString());
        var arguments = body["data"]!["example"]!["request"]!["params"]!["arguments"]!;
        Assert.Equal("showInEditor", (string?)arguments["action"]);
        Assert.False((bool)arguments["dryRun"]!);
    }

    [Theory]
    [InlineData("read", true, 0)]
    [InlineData("rename", true, 0)]
    [InlineData("rename", false, 1)]
    public async Task Other_test_suite_actions_keep_their_existing_approval_policy(string action, bool preview, int approvals)
    {
        using var fixture = new Fixture("21");
        var body = await fixture.Call("ManageTestSuiteCase", new JsonObject { ["category"] = "styleGuide", ["name"] = "ExactCase",
            ["action"] = action, ["newName"] = action == "rename" ? "NextCase" : "", ["dryRun"] = preview });
        Assert.Equal(approvals, fixture.Approvals);
        if (approvals != 0) Assert.Equal("CONFIRMATION_REQUIRED", (string?)body["error"]?["code"]);
        else Assert.True((bool?)body["ok"], body.ToJsonString());
        Assert.Equal(0, fixture.Worker.UiCalls);
        Assert.Equal(0, fixture.Worker.Writes);
    }

    [Theory]
    [InlineData("17", "application", "testSet")]
    [InlineData("19", "application", "testSet")]
    [InlineData("18", "system", "case")]
    [InlineData("21", "system", "case")]
    public async Task Unavailable_test_suite_editors_still_refuse_before_dispatch(string release, string category, string kind)
    {
        using var fixture = new Fixture(release);
        var body = await fixture.Call("ManageTestSuiteCase", new JsonObject { ["category"] = category, ["kind"] = kind,
            ["name"] = "ExactCase", ["action"] = "showInEditor", ["dryRun"] = false });
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)body["error"]?["code"]);
        Assert.Equal(0, fixture.Approvals);
        Assert.Equal(0, fixture.Worker.Calls);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HardwareAddressingAdmissionTests.UnboundWorker contextWorker = new();
        private readonly toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline pipeline;
        private readonly string previousRelease = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey;
        private readonly Func<PendingApproval, ApprovalSettings, CancellationToken, Task<ApprovalOutcome>>? previous;
        private readonly IReadOnlyList<global::ModelContextProtocol.Server.McpServerTool> tools;
        internal readonly Worker Worker = new();
        internal int Approvals;
        internal Fixture(string release)
        {
            toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = release;
            previous = Host.McpServer.ApprovalWaitOverrideForTests;
            Host.McpServer.ApprovalWaitOverrideForTests = (pending, _, _) =>
            { Approvals++; return Task.FromResult(new ApprovalOutcome(pending, false, "denied")); };
            var shared = LegacyHostToolRegistry.Create(Worker, release, false, hostPorts: false);
            pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, contextWorker, shared);
            tools = pipeline.Tools.ToArray();
        }
        internal Task<JsonNode> Call(string tool, JsonObject args)
            => ToolHintOnceTests.Invoke(tools.Single(t => t.ProtocolTool.Name == tool), args);
        public void Dispose()
        {
            Host.McpServer.ApprovalWaitOverrideForTests = previous; pipeline.Dispose(); contextWorker.Dispose(); Worker.Dispose();
            toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = previousRelease;
        }
    }

    private sealed class Worker : IFoundationWorker
    {
        internal int Calls, UiCalls, Writes;
        internal bool WithUserInterface = true;
        internal JsonNode? Request;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject {
                ["ProcessId"] = 123, ["IsAttached"] = true, ["ProjectFile"] = "C:/Fixture.ap21" });
            Assert.Contains(operation, new[] { "engineering-services.ExecuteEngineeringServices", "optional-products.ExecuteOptionalProduct" });
            Calls++; Request = arguments["request"]!.DeepClone();
            bool preview = (bool)Request["DryRun"]!;
            string action = (string)Request["Action"]!;
            if (action == "showInEditor" && !WithUserInterface)
                throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException(
                    "ManageTestSuiteCase.showInEditor needs a TIA Portal with user interface", isArgument: false);
            if (!preview && ((string?)Request["Operation"] == "ShowObjectInEditor" || action == "showInEditor")) UiCalls++;
            if ((string?)Request["Operation"] == "ManageTestSuiteCase" && action is not ("showInEditor" or "read") && !preview) Writes++;
            return Task.FromResult<JsonNode?>(new JsonObject { ["Message"] = "Editor stand-in", ["Meta"] = new JsonObject {
                ["operationSuccess"] = true, ["apiCallSuccess"] = true, ["mayHaveChanged"] = false, ["dryRun"] = preview } });
        }
        public void Dispose() { }
    }
}
