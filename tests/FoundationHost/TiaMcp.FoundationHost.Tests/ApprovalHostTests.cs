extern alias toolpipeline;
extern alias adaptertests;
extern alias foundationhost;
using CloseHost = foundationhost::TiaMcp.FoundationHost;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaOpenness.Shared;
using Xunit;

public sealed class ApprovalHostTests
{
    private sealed class CloseWorker : CloseHost.IFoundationSessionWorker
    {
        private readonly adaptertests::TiaMcp.Adapters.PlcFoundationEngine engine = new("19");
        internal int Calls { get; private set; }
        internal adaptertests::Siemens.Engineering.Project OpenModifiedProject(bool workerStarted = true)
        {
            if (workerStarted) engine.ProjectConnectionCreated!(engine.Connection);
            else engine.ReattachForTest(Environment.ProcessId);
            engine.Connection.Projects.NextIsPrimary = true;
            var project = engine.Connection.Projects.Open(new FileInfo(@"C:\fixture.ap19"), null, adaptertests::Siemens.Engineering.ProjectOpenMode.Primary);
            project.IsModified = true;
            engine.ProjectRetrieved!(project);
            return project;
        }
        public string ApprovalIdentity => "{}";
        public bool SharedSession => toolpipeline::TiaMcp.FoundationHost.ReleaseCatalogData.Releases["19"].SharedSession;
        public bool Bundled => false;
        public TiaMcp.Logic.ModelContextProtocol.ImportStagingSession? StagingOwner => null;
        public IDisposable? EnterRequest(RequestContext<CallToolRequestParams> request) => null;
        public Task<IDisposable?> AcquireLane(CancellationToken token) => Task.FromResult<IDisposable?>(null);
        public void ActivateLane(IDisposable? lane) { }
        public void Dispose() { }
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
        {
            Assert.Equal("CloseProject", operation);
            Calls++;
            var result = engine.CloseProject((bool)args["dryRun"]!, (string?)args["secondaryProjectFile"] ?? "", (bool)args["discardChanges"]!,
                (string)args["expectedPlanHash"]!, (bool)args["bindingOwnedByCaller"]!, (bool)args["confirm"]!, (string)args["expectedProjectFile"]!);
            return Task.FromResult(JsonSerializer.SerializeToNode(result, result.GetType()));
        }
    }

    private static CloseHost.HostSession CloseSession(McpServerTool tool) =>
        new("19", "", false, [tool], [], () => new JsonObject(), () => { }, Path.GetTempPath());

    private static CloseHost.FoundationTool CloseTool(CloseWorker worker, string release = "19") =>
        new(CloseHost.FoundationTools.ForRelease(CloseHost.FoundationTools.Definitions.Single(d => d.Name == "CloseProject"), release), worker);

    private static IMcpServer CloseClient() => System.Reflection.DispatchProxy.Create<IMcpServer, WorkerRobustnessTests.ServerProxy>();

    private static JsonObject DiscardApply(JsonNode preview) => new() {
        ["discardChanges"] = true, ["dryRun"] = false, ["confirm"] = true,
        ["expectedProjectFile"] = preview["data"]!["projectFile"]!.DeepClone(), ["expectedPlanHash"] = preview["data"]!["planHash"]!.DeepClone() };

    private static async Task<JsonNode> Close(McpServerTool tool, IMcpServer client, JsonObject arguments)
    {
        var request = new RequestContext<CallToolRequestParams>(client) { Params = new CallToolRequestParams {
            Name = "CloseProject", Arguments = arguments.Deserialize<Dictionary<string, JsonElement>>() } };
        return (await tool.InvokeAsync(request)).StructuredContent!;
    }

    [Theory, InlineData(true, false), InlineData(true, true), InlineData(false, false), InlineData(false, true)]
    public async Task Discard_apply_requires_a_Workbench_grant_even_with_ordinary_write_approval_disabled(bool enabled, bool grant)
    {
        using var session = LiveApprovalSession();
        using var worker = new CloseWorker();
        Assert.True(worker.SharedSession);
        var project = worker.OpenModifiedProject();
        int decisions = 0;
        var tool = new CloseHost.FoundationV4Tool(CloseTool(worker), "19", () => new ApprovalSettings(enabled, 1),
            (pending, settings, _) =>
            {
                if (!settings.Enabled) return Task.FromResult(new ApprovalOutcome(pending, true, null));
                decisions++;
                return Task.FromResult(new ApprovalOutcome(pending, false, grant ? null : "denied"));
            });
        using var owner = CloseSession(tool);
        CloseHost.SharedSessionState.For(worker).HostSessionKey = () => owner.SessionKey;
        var client = CloseClient();
        var args = new JsonObject { ["discardChanges"] = true };
        var preview = await Close(owner.Tools.Single(), client, args);
        Assert.True((bool?)preview["ok"], preview.ToJsonString());
        Assert.True((bool?)preview["data"]?["isModified"]);
        Assert.Equal(project.Path.FullName, (string?)preview["data"]?["projectFile"]);
        Assert.Equal(0, project.CloseCalls);
        Assert.Equal(0, decisions);
        var applied = await Close(owner.Tools.Single(), client, DiscardApply(preview));
        Assert.Equal(1, decisions);
        Assert.Equal(grant, (bool?)applied["ok"]);
        if (!grant) Assert.Equal("CONFIRMATION_REQUIRED", (string?)applied["error"]?["code"]);
        Assert.Equal(grant ? 1 : 0, project.CloseCalls);
        Assert.Equal(0, project.SaveCalls);
    }

    [Theory, InlineData(false, false, true), InlineData(true, false, true), InlineData(true, true, true), InlineData(true, false, false)]
    public async Task Another_session_cannot_discard_even_with_the_owners_preview(bool apply, bool nested, bool approvalEnabled)
    {
        using var session = LiveApprovalSession();
        using var worker = new CloseWorker();
        var project = worker.OpenModifiedProject();
        var tool = new CloseHost.FoundationV4Tool(CloseTool(worker), "19", () => new ApprovalSettings(approvalEnabled, 1),
            (pending, settings, _) => settings.Enabled ? throw new InvalidOperationException("A foreign session must be refused before approval.")
                : Task.FromResult(new ApprovalOutcome(pending, true, null)));
        using var owner = CloseSession(tool);
        using var other = CloseSession(nested ? owner.Tools.Single() : tool);
        CloseHost.SharedSessionState.For(worker).HostSessionKey = () => owner.SessionKey;
        var args = new JsonObject { ["discardChanges"] = true };
        var preview = await Close(owner.Tools.Single(), CloseClient(), args);
        Assert.True((bool?)preview["ok"], preview.ToJsonString());
        if (apply) args = DiscardApply(preview);
        Assert.False(tool.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("sharedSession", out _));
        var otherClient = CloseClient();
        var refused = await Close(other.Tools.Single(), otherClient, args);
        Assert.Equal("PRECONDITION_FAILED", (string?)refused["error"]?["code"]);
        Assert.Equal("tia-held-by-session", (string?)refused["error"]?["details"]?["condition"]);
        args["sharedSession"] = false;
        var spoofed = await Close(other.Tools.Single(), otherClient, args);
        Assert.Equal("INVALID_ARGUMENT", (string?)spoofed["error"]?["code"]);
        args.Remove("sharedSession");
        args["bindingOwnedByCaller"] = true;
        Assert.Equal("INVALID_ARGUMENT", (string?)(await Close(other.Tools.Single(), otherClient, args))["error"]?["code"]);
        Assert.Equal(1, worker.Calls);
        Assert.Equal(0, project.CloseCalls);
    }

    [Fact]
    public async Task Discard_rechecks_the_lease_holder_after_the_approval_wait()
    {
        using var session = LiveApprovalSession();
        using var worker = new CloseWorker();
        var project = worker.OpenModifiedProject();
        var state = CloseHost.SharedSessionState.For(worker);
        using var newOwner = CloseSession(CloseTool(worker));
        int grants = 0;
        var tool = new CloseHost.FoundationV4Tool(CloseTool(worker), "19", () => new ApprovalSettings(true, 1),
            (pending, _, _) => {
                grants++;
                state.HostSessionKey = () => newOwner.SessionKey;
                return Task.FromResult(new ApprovalOutcome(pending, false, null));
            });
        using var owner = CloseSession(tool);
        state.HostSessionKey = () => owner.SessionKey;
        var client = CloseClient();
        var args = new JsonObject { ["discardChanges"] = true };
        var preview = await Close(owner.Tools.Single(), client, args);
        Assert.True((bool?)preview["ok"], preview.ToJsonString());
        var refused = await Close(owner.Tools.Single(), client, DiscardApply(preview));
        Assert.Equal(1, grants);
        Assert.Equal("PRECONDITION_FAILED", (string?)refused["error"]?["code"]);
        Assert.Equal("tia-held-by-session", (string?)refused["error"]?["details"]?["condition"]);
        Assert.Equal(0, project.CloseCalls);
        Assert.Equal(0, project.SaveCalls);
    }

    [Fact]
    public async Task Binding_owner_cannot_discard_a_project_in_a_maintainer_started_portal()
    {
        using var session = LiveApprovalSession();
        using var worker = new CloseWorker();
        var project = worker.OpenModifiedProject(workerStarted: false);
        var tool = new CloseHost.FoundationV4Tool(CloseTool(worker), "19");
        using var owner = CloseSession(tool);
        CloseHost.SharedSessionState.For(worker).HostSessionKey = () => owner.SessionKey;
        var refused = await Close(owner.Tools.Single(), CloseClient(), new JsonObject { ["discardChanges"] = true });
        Assert.Equal("PRECONDITION_FAILED", (string?)refused["error"]?["code"]);
        Assert.Equal(1, worker.Calls);
        Assert.Equal(0, project.CloseCalls);
        Assert.Equal(0, project.SaveCalls);
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"),
        InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Discard_is_an_explicit_option_on_the_destructive_session_tool(string release)
    {
        using var worker = new CloseWorker();
        var tool = new CloseHost.FoundationV4Tool(CloseTool(worker, release), release).ProtocolTool;
        var properties = tool.InputSchema.GetProperty("properties");
        Assert.True(tool.Annotations!.DestructiveHint);
        Assert.Contains("[SESSION]", tool.Description);
        Assert.Equal("boolean", properties.GetProperty("discardChanges").GetProperty("type").GetString());
        Assert.False(properties.GetProperty("discardChanges").GetProperty("default").GetBoolean());
        Assert.Equal("string", properties.GetProperty("expectedPlanHash").GetProperty("type").GetString());
        Assert.Equal("", properties.GetProperty("expectedPlanHash").GetProperty("default").GetString());
        Assert.False(properties.TryGetProperty("sharedSession", out _));
        Assert.False(properties.TryGetProperty("bindingOwnedByCaller", out _));
    }

    [Theory]
    [InlineData("DownloadPlc", "{}", "destructive")]
    [InlineData("DeletePlcBlock", "{}", "destructive")]
    [InlineData("ImportPlcBlock", "{\"confirmDelete\":true}", "destructive")]
    [InlineData("ImportPlcBlock", "{\"deleteAtTarget\":true}", "destructive")]
    [InlineData("ImportPlcBlock", "{\"overwrite\":true}", "write")]
    [InlineData("ImportPlcBlock", "{\"confirmDelete\":false,\"deleteAtTarget\":false}", "write")]
    [InlineData("SetPlcBlockAttribute", "{}", "write")]
    public async Task Host_assigns_narrow_risk_and_the_private_frame_preserves_it(string tool, string arguments, string expected)
    {
        var pending = PendingApproval.Create("foundation", "21", tool, arguments, null, 120);
        Assert.Equal(expected, pending.Risk);
        Assert.Equal(expected == "write" ? "execute" : tool == "DownloadPlc" ? "online" : "delete", pending.RiskAction);
        using var frame = new MemoryStream();
        await ApprovalFrames.Write(frame, pending, CancellationToken.None);
        frame.Position = 0;
        var received = await ApprovalFrames.Read<PendingApproval>(frame, CancellationToken.None);
        received.Validate();
        Assert.Equal(expected, received.Risk); Assert.Equal(pending.ArgumentDigest, received.ArgumentDigest);
        received.Risk = "unknown";
        Assert.Throws<InvalidDataException>(received.Validate);
        var missingRisk = JsonSerializer.SerializeToNode(pending)!.AsObject(); missingRisk.Remove("Risk");
        using var legacy = new MemoryStream();
        await ApprovalFrames.Write(legacy, missingRisk, CancellationToken.None); legacy.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => ApprovalFrames.Read<PendingApproval>(legacy, CancellationToken.None));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Foundation_call_carries_transport_actor_through_approval_journal_and_audit(bool human)
    {
        using var approvalSession = LiveApprovalSession();
        string root = Path.Combine(AppContext.BaseDirectory, "actor-audit", Guid.NewGuid().ToString("N"));
        var log = new AuditLog(root); using var audit = AuditInvocation.UseLog(log);
        using var actor = human ? ActorScope.EnterWorkbench("attached-session", new string('c', 32)) : ActorScope.EnterCall("attached-session");
        var rows = new List<string>(); TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureOutput(rows.Add);
        try
        {
            var worker = new Worker(); PendingApproval? approval = null;
            var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "SaveProject"), worker), "19",
                () => new ApprovalSettings(true, 2), (pending, _, _) =>
                { approval = pending; return Task.FromResult(new ApprovalOutcome(pending, false, null)); });
            var result = await tool.InvokeAsync(Request("SaveProject", "{\"dryRun\":false,\"confirm\":true,\"expectedProjectFile\":\"C:\\\\fixture.ap19\"}"));
            Assert.NotNull(approval); Assert.Equal(human ? 2 : 1, approval.Version);
            Assert.Equal(human ? "workbench" : "mcp", approval.EffectiveActor);
            Assert.True(worker.Calls > 0); Assert.True(log.Verify().Passed);
            Assert.NotEmpty(log.Read()); Assert.All(log.Read(), row => Assert.Equal(human ? "workbench" : "mcp", row.Actor));
            var projected = rows.Select(row => JsonNode.Parse(row)!).Where(row => (int?)row["callProjection"] == 1).ToArray();
            Assert.Equal(2, projected.Length);
            Assert.All(projected, row => { Assert.Equal(human ? "workbench" : "mcp", (string?)row["actor"]); Assert.Equal(ActorScope.McpSession, (string?)row["mcpSession"]); });
            Assert.Null(result.StructuredContent?["meta"]?["actor"]);
        }
        finally { TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureOutput(null); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("SaveProject")]
    [InlineData("CloseProject")]
    public async Task Current_session_save_and_close_are_approved_without_changing_catalog_policy(string source)
    {
        using var approvalSession = LiveApprovalSession();
        var before = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
        try
        {
            var worker = new Worker(); var definition = FoundationTools.Definitions.Single(d => d.Name == source);
            var tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19", () => new ApprovalSettings(true, 1));
            var result = await tool.InvokeAsync(Request(tool.ProtocolTool.Name, "{\"dryRun\":false,\"confirm\":true,\"expectedProjectFile\":\"C:/fixture.ap19\"}"));
            Assert.Equal("CONFIRMATION_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal("rejected-before-operation", (string?)result.StructuredContent?["meta"]?["outcome"]);
            Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
            Assert.Equal(1, worker.Calls);

            tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19", () => new ApprovalSettings(false, 1));
            result = await tool.InvokeAsync(Request(tool.ProtocolTool.Name, "{\"dryRun\":false,\"confirm\":true,\"expectedProjectFile\":\"C:/fixture.ap19\"}"));
            Assert.Single(result.StructuredContent!["meta"]!["warnings"]!.AsArray(), row => (string?)row?["code"] == "APPROVAL_DISABLED");
        }
        finally { before.Save(ApprovalSettings.SettingsPath); }
    }

    [Theory]
    [InlineData("ConnectPortal", "{\"processId\":123}")]
    [InlineData("DisconnectPortal", "{}")]
    public async Task Foundation_connect_and_disconnect_remain_ungated(string source, string arguments)
    {
        var worker = new Worker(); var definition = FoundationTools.Definitions.Single(d => d.Name == source);
        var tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19", () => new ApprovalSettings(true, 1));
        var result = await tool.InvokeAsync(Request(tool.ProtocolTool.Name, arguments));
        Assert.NotEqual("CONFIRMATION_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.True(worker.Calls > 0);
    }

    [Theory]
    [InlineData("granted")]
    [InlineData("denied")]
    [InlineData("timeout")]
    [InlineData("disabled")]
    public async Task Foundation_write_audit_uses_response_id_and_starts_only_after_approval(string decision)
    {
        using var approvalSession = LiveApprovalSession();
        string auditDirectory = Path.GetFullPath(Path.Combine("bin-build", "P6-55", "foundation-audit", Guid.NewGuid().ToString("N")));
        var audit = new AuditLog(auditDirectory);
        using var auditScope = AuditInvocation.UseLog(audit);
        var worker = new Worker();
        var definition = FoundationTools.Definitions.Single(d => d.Name == "SaveProject");
        bool enabled = decision != "disabled";
        Func<PendingApproval, ApprovalSettings, CancellationToken, Task<ApprovalOutcome>>? wait = enabled
            ? (pending, _, _) =>
            {
                audit.Approval(pending.RequestId, pending.Host, pending.ReleaseKey, pending.Tool,
                    decision == "granted" ? "granted" : decision, pending.PlanHash);
                return Task.FromResult(new ApprovalOutcome(pending, false, decision == "granted" ? null : decision));
            }
            : null;
        var tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19",
            () => new ApprovalSettings(enabled, 1), wait);
        var args = new JsonObject { ["dryRun"] = false, ["confirm"] = true,
            ["expectedProjectFile"] = @"C:\fixture.ap19" };
        var result = await tool.InvokeAsync(Request("SaveProject", args.ToJsonString()));
        string requestId = (string)result.StructuredContent!["meta"]!["requestId"]!;
        var rows = audit.Read();
        string[] expected = decision switch
        {
            "granted" => new[] { "request", "approval-granted", "start", "end" },
            "denied" => new[] { "request", "approval-denied", "end" },
            "timeout" => new[] { "request", "approval-timeout", "end" },
            _ => new[] { "request", "start", "end" }
        };
        Assert.Equal(expected, rows.Select(row => row.Event));
        Assert.All(rows, row => Assert.Equal(requestId, row.RequestId));
        Assert.False(string.IsNullOrWhiteSpace(rows[0].PlanHash));
        Assert.All(rows.Where(row => row.Event.StartsWith("approval-", StringComparison.Ordinal)), row => Assert.Equal(rows[0].PlanHash, row.PlanHash));
        Assert.Equal(enabled ? decision == "granted" ? 2 : 1 : 1, worker.Calls);
        Assert.True(audit.Verify().Passed);
    }

    [Fact]
    public async Task Foundation_dry_run_does_not_request_approval_or_enter_write_audit()
    {
        string auditDirectory = Path.GetFullPath(Path.Combine("bin-build", "P6-55", "foundation-preview-audit", Guid.NewGuid().ToString("N")));
        var audit = new AuditLog(auditDirectory);
        using var auditScope = AuditInvocation.UseLog(audit);
        var worker = new Worker();
        var definition = FoundationTools.Definitions.Single(d => d.Name == "CreatePlcTagTable");
        int waits = 0;
        var tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19",
            () => new ApprovalSettings(true, 1), (_, _, _) =>
            {
                waits++;
                throw new InvalidOperationException("A dry-run preview must not request approval.");
            });

        var result = await tool.InvokeAsync(Request("CreatePlcTagTable", "{\"plc\":\"PLC_1\",\"group\":\"\",\"name\":\"Preview\",\"dryRun\":true}"));
        var defaultPreview = await tool.InvokeAsync(Request("CreatePlcTagTable", "{\"plc\":\"PLC_1\",\"group\":\"\",\"name\":\"SchemaDefaultPreview\"}"));

        Assert.Equal(0, waits);
        Assert.Equal(2, worker.Calls);
        Assert.Equal("succeeded", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.Equal("succeeded", (string?)defaultPreview.StructuredContent?["meta"]?["outcome"]);
        Assert.Empty(audit.Read());
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("\"false\"")]
    [InlineData("1")]
    [InlineData("null")]
    public async Task Foundation_non_boolean_dry_run_is_a_write_and_never_reaches_the_worker(string dryRun)
    {
        // Only an explicit boolean true previews; a present value of another JSON type fails closed as a write.
        var worker = new Worker();
        int waits = 0;
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "CreatePlcTagTable"), worker), "19",
            () => new ApprovalSettings(true, 1), (pending, _, _) =>
            {
                waits++;
                return Task.FromResult(new ApprovalOutcome(pending, false, "denied"));
            });
        var isWrite = typeof(FoundationV4Tool).GetMethod("IsApprovalWrite", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.True((bool)isWrite.Invoke(tool, new object[] { JsonNode.Parse("{\"plc\":\"PLC_1\",\"dryRun\":" + dryRun + "}")!.AsObject() })!);
        Assert.False((bool)isWrite.Invoke(tool, new object[] { JsonNode.Parse("{\"plc\":\"PLC_1\",\"dryRun\":true}")!.AsObject() })!);
        Assert.False((bool)isWrite.Invoke(tool, new object[] { JsonNode.Parse("{\"plc\":\"PLC_1\"}")!.AsObject() })!);
        var result = await tool.InvokeAsync(Request("CreatePlcTagTable", "{\"plc\":\"PLC_1\",\"group\":\"\",\"name\":\"Typed\",\"dryRun\":" + dryRun + "}"));
        Assert.NotEqual("succeeded", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.Equal(0, worker.Calls);
        Assert.Equal(0, waits);
    }

    [Fact]
    public void Download_approval_request_lists_every_prompt_answer_the_call_will_give()
    {
        var pending = PendingApproval.Create("foundation", "21", "DownloadPlc",
            "{\"softwarePath\":\"PLC_1\",\"keepActualValues\":false,\"allowExpandedDownload\":true,\"allowNonTlsLogin\":true,\"password\":\"fixture-cpu-secret\"}", null, 120);
        var display = JsonNode.Parse(pending.ParametersJson)!;
        var plan = display["promptAnswerPlan"]!;
        var answers = plan["answers"]!.AsObject();
        Assert.Equal("StopPlcAndReinitialize", (string?)answers["DataBlockReinitialization"]!["answer"]);
        Assert.Equal("StopAll", (string?)answers["StopModules"]!["answer"]);
        Assert.Equal("builtin", (string?)answers["StopModules"]!["source"]);
        Assert.Equal("Download", (string?)answers["ExpandDownload"]!["answer"]);
        Assert.Equal("refused", (string?)answers["SafetyProgram"]!["source"]);
        Assert.Equal("allowSafetyProgramDownload", (string?)answers["SafetyProgram"]!["allowWith"]);
        Assert.Contains(plan["loginPrompts"]!.AsArray(), row => (string?)row!["prompt"] == "ModuleReadAccessPassword" && (bool)row["answered"]!);
        Assert.Contains("allowNonTlsLogin=true", (string?)plan["loginChannel"], StringComparison.Ordinal);
        Assert.True((bool)display["allowNonTlsLogin"]!);
        Assert.DoesNotContain("fixture-cpu-secret", pending.ParametersJson, StringComparison.Ordinal);
        Assert.Null(JsonNode.Parse(PendingApproval.Create("foundation", "21", "CreatePlcTag", "{\"softwarePath\":\"PLC_1\"}", null, 120).ParametersJson)!["promptAnswerPlan"]);
        var upload = JsonNode.Parse(PendingApproval.Create("foundation", "21", "UploadStationFromPlc", "{\"targetIpAddress\":\"192.0.2.1\"}", null, 120).ParametersJson)!;
        Assert.Equal("TryUpload", (string?)upload["promptAnswerPlan"]!["answers"]!["UploadMissingProducts"]!["answer"]);
    }

    [Theory]
    [InlineData("PLCSIM", "plcsim", "PLCSIM")]
    [InlineData("Intel(R) Ethernet", "physical", "Intel(R) Ethernet")]
    [InlineData("", "physical", "automatic")]
    public void Download_approval_card_names_the_online_target(string adapter, string kind, string displayAdapter)
    {
        using var evidence = TiaMcpServer.Siemens.DownloadTargetPolicy.UseSources(
            () => kind == "plcsim" ? DownloadSafetyPolicyTests.RunningInstance("192.0.2.10") : null,
            _ => TiaMcp.Engine.Tests.Shared.DownloadTargetCases.Network());
        var args = new JsonObject { ["softwarePath"] = "PLC_1", ["targetIpAddress"] = "192.0.2.10",
            ["pgPcInterface"] = adapter, ["rhTarget"] = "backup", ["password"] = "fixture-secret" };
        var pending = PendingApproval.Create("foundation", "21", "DownloadPlc", args.ToJsonString(), null, 120);
        var target = JsonNode.Parse(pending.ParametersJson)!["onlineTarget"]!;
        Assert.Equal(kind, (string?)target["kind"]);
        Assert.Equal("192.0.2.10", (string?)target["targetIpAddress"]);
        Assert.Equal(displayAdapter, (string?)target["pgPcInterface"]);
        Assert.Equal("backup", (string?)target["rhTarget"]);
        Assert.DoesNotContain("fixture-secret", pending.ParametersJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Foundation_missing_exact_installation_refuses_worker_tools_before_approval()
    {
        var worker = new Worker();
        int waits = 0;
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "CreatePlcTagTable"), worker), "19",
            () => new ApprovalSettings(true, 1), (_, _, _) =>
            {
                waits++;
                throw new InvalidOperationException("Readiness refusal must precede approval.");
            }, () => FoundationPassiveDiagnostics.Readiness("19", _ => null, () => true));

        var result = await tool.InvokeAsync(Request("CreatePlcTagTable", "{\"plc\":\"PLC_1\",\"group\":\"\",\"name\":\"Blocked\",\"dryRun\":true}"));
        Assert.Equal("RESOURCE_UNAVAILABLE", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("tia-openness-environment", (string?)result.StructuredContent?["error"]?["details"]?["resource"]);
        Assert.Equal("rejected-before-operation", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.False((bool)result.StructuredContent!["data"]!["environment"]!["ready"]!);
        Assert.Equal(0, waits);
        Assert.Equal(0, worker.Calls);
    }

    private static IDisposable LiveApprovalSession()
        => toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(
            new toolpipeline::TiaMcp.FoundationHost.EngineSessionContext(new HardwareAddressingAdmissionTests.UnboundWorker()));

    private sealed class Worker : IFoundationWorker
    {
        public int Calls;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            Calls++;
            return Task.FromResult<JsonNode?>(new JsonObject { ["Executed"] = arguments["dryRun"]?.GetValue<bool>() != true, ["ProjectFile"] = @"C:\fixture.ap19" });
        }
        public void Dispose() { }
    }
    private static RequestContext<CallToolRequestParams> Request(string tool, string json)
        => new(System.Reflection.DispatchProxy.Create<IMcpServer, ServerProxy>())
        { Params = new CallToolRequestParams { Name = tool, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } };
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Write_is_refused_without_workbench_before_any_worker_call(bool dryRun)
    {
        var worker = new Worker(); var definition = FoundationTools.Definitions.Single(d => d.Name == "ImportPlcBlock");
        var tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19", () => new ApprovalSettings(true, 1));
        var result = await tool.InvokeAsync(Request("ImportPlcBlock", "{\"softwarePath\":\"PLC\",\"groupPath\":\"\",\"importPath\":\"C:\\\\fixture.xml\",\"dryRun\":" + dryRun.ToString().ToLowerInvariant() + "}"));
        var body = result.StructuredContent!;
        if (dryRun)
        {
            Assert.NotEqual("CONFIRMATION_REQUIRED", (string?)body["error"]?["code"]);
            Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
            Assert.Equal(0, worker.Calls);
            return;
        }
        Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
        Assert.Single(body["meta"]!["warnings"]!.AsArray(), w => (string?)w?["details"]?["stage"] == "approval-precheck");
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal(0, worker.Calls);
    }
    [Fact]
    public async Task Invalid_input_retains_its_admission_error_before_approval()
    {
        var worker = new Worker(); var definition = FoundationTools.Definitions.Single(d => d.Name == "ImportPlcBlock");
        var tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19", () => new ApprovalSettings(true, 1));
        var result = await tool.InvokeAsync(Request("ImportPlcBlock", "{\"unexpected\":true}"));
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal(0, worker.Calls);
    }
}
