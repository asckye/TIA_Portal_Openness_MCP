extern alias foundationhost;
extern alias toolpipeline;
using Host = foundationhost::TiaMcp.FoundationHost;
using Pipeline = toolpipeline::TiaMcpServer.ModelContextProtocol;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

public sealed class NativeDiagnosticProbeToolTests : IDisposable
{
    private const string OptIn = "TIA_MCP_CRASH_PROBES";
    private readonly string? previousOptIn = Environment.GetEnvironmentVariable(OptIn);

    public NativeDiagnosticProbeToolTests() => Environment.SetEnvironmentVariable(OptIn, "1");
    public void Dispose() => Environment.SetEnvironmentVariable(OptIn, previousOptIn);

    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    public async Task Without_the_host_opt_in_no_probe_reaches_approval_or_the_worker(string? optIn)
    {
        Environment.SetEnvironmentVariable(OptIn, optIn);
        using var worker = new Worker("ok");
        var previous = Pipeline.McpServer.ApprovalWaitOverrideForTests;
        int approvals = 0;
        Pipeline.McpServer.ApprovalWaitOverrideForTests = (pending, _, _) => {
            approvals++; return Task.FromResult(new TiaOpenness.Shared.ApprovalOutcome(pending, false, null, delegated: true)); };
        try
        {
            foreach (var arguments in new object[] {
                new { probe = "mastercopy-noexclusive", sourceKind = "block", softwarePath = "MCP_RC_PLC", sourcePath = "MCP_RC_DB", dryRun = false,
                    expectedProjectFile = @"C:\Projects\MCP_RC_Probe\MCP_RC_Probe.ap16" },
                new { probe = "sivarc-count", softwarePath = "MCP_RC_PLC", blockPath = "MCP_RC_SvFC" },
                new { probe = "portal-info" } })
            {
                var result = await new Host.NativeDiagnosticProbeTool(worker, "16").InvokeAsync(Request(arguments));
                Assert.True(result.IsError);
                Assert.Equal("disabled", (string?)result.StructuredContent?["status"]);
            }
            Assert.Equal(0, approvals);
            Assert.Equal(0, worker.Calls);
        }
        finally { Pipeline.McpServer.ApprovalWaitOverrideForTests = previous; }
    }

    [Theory]
    [InlineData("portal-info", "ok", 1)]
    [InlineData("portal-info", "disabled", 1)]
    [InlineData("unknown", "unsupported", 0)]
    public async Task Hidden_read_uses_the_session_and_returns_only_diagnostic_fields(string probe, string status, int calls)
    {
        using var worker = new Worker(status);
        var tool = new Host.NativeDiagnosticProbeTool(worker);
        using var session = new Host.HostSession("17", "", false, [], [], () => new JsonObject(), () => { });
        var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, WorkerRobustnessTests.ServerProxy>()) {
            Params = new() { Name = "ProbeNativeCrash",
                Arguments = new Dictionary<string, JsonElement> { ["probe"] = JsonSerializer.SerializeToElement(probe) } }
        };
        var result = await tool.Handler(session)(request, CancellationToken.None);
        Assert.Equal(calls, worker.Calls);
        Assert.Equal(status, (string?)result.StructuredContent?["status"]);
        Assert.Equal(status != "ok", result.IsError);
        Assert.DoesNotContain("private", Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        Assert.Empty(session.Tools);
        Assert.Empty(session.OperatorTools);
    }

    [Fact]
    public async Task Scan_forwards_only_the_selected_workbook_layout_and_returns_counts()
    {
        using var worker = new Worker("ok");
        using var session = new Host.HostSession("16", "", false, [], [], () => new JsonObject(), () => { });
        var fields = new Dictionary<string, string> { ["probe"] = "alarm-projecttexts-scan", ["sourceCulture"] = "en-US", ["targetCulture"] = "de-DE",
            ["plcPath"] = "Devices/MCP_RC_PLC", ["ownerColumn"] = "Owner", ["categoryColumn"] = "Kind", ["textListCategory"] = "List",
            ["alarmCategory"] = "Alarm" };
        var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, WorkerRobustnessTests.ServerProxy>()) {
            Params = new() { Name = "ProbeNativeCrash", Arguments = fields.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)) }
        };
        var result = await new Host.NativeDiagnosticProbeTool(worker).Handler(session)(request, CancellationToken.None);
        Assert.Equal("diagnostics.ReadProbeProjectTexts", worker.Operation);
        Assert.Equal(fields.Count - 1, worker.Input!.Count);
        foreach (var field in fields.Where(p => p.Key != "probe")) Assert.Equal(field.Value, (string?)worker.Input[field.Key]);
        Assert.False(result.IsError);
        Assert.Equal(3, (int?)result.StructuredContent?["textListRows"]);
        Assert.Equal(0, (int?)result.StructuredContent?["alarmTextRows"]);
        Assert.Equal("Kind", (string?)result.StructuredContent?["worksheetHeaders"]?["Texts"]?[1]);
        Assert.Equal(2, (int?)result.StructuredContent?["worksheetColumnCounts"]?["Texts"]);
        Assert.DoesNotContain("private", Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
    }

    [Fact]
    public async Task A_layout_request_can_discover_headers_before_supplying_column_or_category_names()
    {
        using var worker = new Worker("layout-only");
        var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, WorkerRobustnessTests.ServerProxy>()) {
            Params = new() { Name = "ProbeNativeCrash", Arguments = new Dictionary<string, string> {
                ["probe"] = "alarm-projecttexts-scan", ["sourceCulture"] = "en-US", ["targetCulture"] = "de-DE", ["plcPath"] = "MCP_RC_PLC"
            }.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)) }
        };
        var result = await new Host.NativeDiagnosticProbeTool(worker).InvokeAsync(request);
        Assert.False(result.IsError);
        Assert.Equal("layout-only", (string?)result.StructuredContent?["status"]);
        Assert.Equal("Owner", (string?)result.StructuredContent?["worksheetHeaders"]?["Texts"]?[0]);
        Assert.DoesNotContain("private", Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        Assert.False(worker.Input!.ContainsKey("ownerColumn"));
    }

    private sealed class Worker(string status) : Host.IFoundationSessionWorker
    {
        internal int Calls;
        internal string? Operation;
        internal JsonObject? Input;
        public string ApprovalIdentity => "";
        public bool SharedSession => false;
        public bool Bundled => false;
        public TiaMcp.Logic.ModelContextProtocol.ImportStagingSession? StagingOwner => null;
        public IDisposable? EnterRequest(RequestContext<CallToolRequestParams> request) => null;
        public Task<IDisposable?> AcquireLane(CancellationToken token) => Task.FromResult<IDisposable?>(null);
        public void ActivateLane(IDisposable? lane) { }
        public void Dispose() { }
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
        {
            Calls++;
            Operation = operation;
            Input = args;
            if (operation == "diagnostics.ReadProbeProjectTexts")
            {
                var reply = new JsonObject { ["status"] = status, ["text"] = "private",
                    ["worksheetHeaders"] = new JsonObject { ["Texts"] = new JsonArray("Owner", "Kind") },
                    ["worksheetColumnCounts"] = new JsonObject { ["Texts"] = 2 } };
                if (status == "ok") { reply["textListRows"] = 3; reply["alarmTextRows"] = 0; }
                return Task.FromResult<JsonNode?>(reply);
            }
            if (operation is "diagnostics.ReadProbeSivarc" or "diagnostics.ExecuteProbeMasterCopy" or "diagnostics.ExportProbeAlarmTextLists")
                return Task.FromResult<JsonNode?>(new JsonObject { ["status"] = status, ["blockName"] = "private", ["tagDefinitionCount"] = 2,
                    ["tagFound"] = false, ["created"] = status == "ok", ["fileBytes"] = 9, ["steps"] = new JsonArray("PlcBlock.Resolve") });
            Assert.Equal("diagnostics.ReadProbePortalInfo", operation);
            Assert.Empty(args);
            return Task.FromResult<JsonNode?>(new JsonObject { ["status"] = status, ["objectName"] = "private" });
        }
    }

    private static RequestContext<CallToolRequestParams> Request(object arguments)
        => new(DispatchProxy.Create<IMcpServer, WorkerRobustnessTests.ServerProxy>()) {
            Params = new() { Name = "ProbeNativeCrash",
                Arguments = JsonSerializer.SerializeToElement(arguments).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) }
        };

    [Theory]
    [InlineData("sivarc-count", false)]
    [InlineData("sivarc-item", false)]
    [InlineData("sivarc-find", true)]
    public async Task Sivarc_probes_forward_the_block_path_and_return_only_diagnostic_fields(string probe, bool named)
    {
        using var worker = new Worker("ok");
        var arguments = new Dictionary<string, string> { ["probe"] = probe, ["softwarePath"] = "MCP_RC_PLC", ["blockPath"] = "MCP_RC_SvFC" };
        if (named) arguments["name"] = "MCP_RC_None";
        var result = await new Host.NativeDiagnosticProbeTool(worker, "20").InvokeAsync(Request(arguments));
        Assert.False(result.IsError);
        Assert.Equal("diagnostics.ReadProbeSivarc", worker.Operation);
        Assert.Equal(arguments.Count, worker.Input!.Count);
        foreach (var pair in arguments) Assert.Equal(pair.Value, (string?)worker.Input[pair.Key]);
        Assert.Equal(2, (int?)result.StructuredContent?["tagDefinitionCount"]);
        Assert.DoesNotContain("private", Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        var missing = await new Host.NativeDiagnosticProbeTool(worker, "20").InvokeAsync(Request(new { probe = "sivarc-find", softwarePath = "P",
            blockPath = "B" }));
        Assert.Equal("unsupported", (string?)missing.StructuredContent?["status"]);
        Assert.Equal(1, worker.Calls);
    }

    [Theory]
    [InlineData("Group/private_DB", true)]
    [InlineData("MCP_RC_DB", false)]
    public async Task Master_copy_refuses_non_test_sources_and_writes_without_a_project_before_any_call(string sourcePath, bool dryRun)
    {
        using var worker = new Worker("ok");
        var result = await new Host.NativeDiagnosticProbeTool(worker, "16").InvokeAsync(Request(new { probe = "mastercopy-noexclusive",
            sourceKind = "block", softwarePath = "MCP_RC_PLC", sourcePath, dryRun }));
        Assert.True(result.IsError);
        Assert.Equal("invalid-argument", (string?)result.StructuredContent?["status"]);
        Assert.Equal(0, worker.Calls);
    }

    [Fact]
    public async Task Master_copy_preview_needs_no_approval_and_sends_no_confirmation()
    {
        using var worker = new Worker("preview");
        var result = await new Host.NativeDiagnosticProbeTool(worker, "16").InvokeAsync(Request(new { probe = "mastercopy-noexclusive",
            sourceKind = "blockGroup", softwarePath = "MCP_RC_PLC", sourcePath = "MCP_RC_Group" }));
        Assert.False(result.IsError);
        Assert.Equal("diagnostics.ExecuteProbeMasterCopy", worker.Operation);
        Assert.True((bool?)worker.Input!["dryRun"]);
        Assert.False(worker.Input.ContainsKey("confirm"));
        Assert.False(worker.Input.ContainsKey("probe"));
        Assert.Null(result.StructuredContent?["approval"]);
    }

    [Theory]
    [InlineData(null, "ok", 1)]
    [InlineData("denied", "approval-denied", 0)]
    public async Task Master_copy_execution_waits_for_the_host_approval_before_confirming(string? reason, string status, int calls)
    {
        using var worker = new Worker("ok");
        var previous = Pipeline.McpServer.ApprovalWaitOverrideForTests;
        TiaOpenness.Shared.PendingApproval? card = null;
        Pipeline.McpServer.ApprovalWaitOverrideForTests = (pending, _, _) => {
            card = pending; return Task.FromResult(new TiaOpenness.Shared.ApprovalOutcome(pending, false, reason, delegated: true)); };
        try
        {
            var result = await new Host.NativeDiagnosticProbeTool(worker, "16").InvokeAsync(Request(new { probe = "mastercopy-noexclusive",
                sourceKind = "tagTable", softwarePath = "MCP_RC_PLC", sourcePath = "MCP_RC_Tags", dryRun = false,
                expectedProjectFile = @"C:\Projects\MCP_RC_Probe\MCP_RC_Probe.ap16" }));
            Assert.Equal(status, (string?)result.StructuredContent?["status"]);
            Assert.Equal(calls, worker.Calls);
            Assert.Equal("ProbeNativeCrash", card!.Tool);
            Assert.Equal("16", card.ReleaseKey);
            if (calls == 0) return;
            Assert.Equal("granted", (string?)result.StructuredContent?["approval"]);
            Assert.False((bool?)worker.Input!["dryRun"]);
            Assert.True((bool?)worker.Input["confirm"]);
            Assert.Equal(@"C:\Projects\MCP_RC_Probe\MCP_RC_Probe.ap16", (string?)worker.Input["expectedProjectFile"]);
        }
        finally { Pipeline.McpServer.ApprovalWaitOverrideForTests = previous; }
    }

    [Fact]
    public async Task Alarm_export_writes_under_the_session_output_root_and_reports_only_the_size()
    {
        using var worker = new Worker("ok");
        var result = await new Host.NativeDiagnosticProbeTool(worker, "16").InvokeAsync(Request(new { probe = "alarm-export-overload",
            softwarePath = "MCP_RC_PLC", textListNames = "MCP_RC_None", cultures = "en-US" }));
        string? root = TiaOpenness.Shared.DataLocations.Current.OutputRoot;
        if (root == null)
        {
            Assert.Equal("no-output-root", (string?)result.StructuredContent?["status"]);
            return;
        }
        Assert.Equal("diagnostics.ExportProbeAlarmTextLists", worker.Operation);
        string path = (string)worker.Input!["exportPath"]!;
        Assert.StartsWith(Path.Combine(root, "crash-probes"), path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".xlsx", path, StringComparison.Ordinal);
        Assert.Equal(9, (int?)result.StructuredContent?["fileBytes"]);
        Assert.DoesNotContain("crash-probes", Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
    }
}
