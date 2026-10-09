using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Logic.V4;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaOpenness.Shared;
using Xunit;

public sealed class ApprovalHostTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Foundation_call_carries_transport_actor_through_approval_journal_and_audit(bool human)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "actor-audit", Guid.NewGuid().ToString("N"));
        var log = new AuditLog(root); using var audit = AuditInvocation.UseLog(log);
        using var actor = human ? ActorScope.EnterWorkbench("attached-session", new string('c', 32)) : ActorScope.EnterCall("attached-session");
        var rows = new List<string>(); TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureOutput(rows.Add);
        try
        {
            var worker = new Worker(); PendingApproval? approval = null;
            var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "SaveProject"), worker), "19", _ => BehaviorPolicy.SafeV4,
                () => new ApprovalSettings(true, 2), (pending, _, _) =>
                { approval = pending; return Task.FromResult(new ApprovalOutcome(pending, false, null)); });
            var result = await tool.InvokeAsync(Request("SaveProject", "{\"mode\":\"apply\",\"confirm\":true,\"expectedPlanHash\":\"" + new string('a', 64) + "\",\"expectedProjectFile\":\"C:\\\\fixture.ap19\"}"));
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
    [InlineData("SaveProject", "{}")]
    [InlineData("CloseProject", "{}")]
    [InlineData("CompileSoftware", "{\"softwarePath\":\"PLC\"}")]
    [InlineData("ImportPlcExternalSource", "{\"sourceName\":\"fixture.scl\",\"filePath\":\"C:/fixture.scl\"}")]
    [InlineData("AttachToOpenProject", "{\"processId\":1,\"processStartUtc\":\"2026-10-05T00:00:00Z\",\"projectPath\":\"C:/fixture.ap19\"}")]
    public async Task Candidate_apply_runs_its_own_preview_before_requesting_approval(string source, string json)
    {
        var worker = new Worker(); var definition = FoundationTools.Definitions.Single(d => d.Name == source);
        var tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19", _ => TiaMcp.Logic.V4.BehaviorPolicy.SafeV4, () => new ApprovalSettings(true, 1));
        var args = JsonNode.Parse(json)!.AsObject(); args["mode"] = "apply"; args["confirm"] = true;
        args["expectedPlanHash"] = new string('a', 64); args["expectedProjectFile"] = "C:/fixture.ap19";
        var result = await tool.InvokeAsync(Request(tool.ProtocolTool.Name, args.ToJsonString()));
        if (source is "SaveProject" or "CloseProject")
        {
            Assert.Equal("CONFIRMATION_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal("workbench-unavailable", (string?)result.StructuredContent?["error"]?["details"]?["reason"]);
        }
        else Assert.Single(result.StructuredContent!["meta"]!["warnings"]!.AsArray(), w => (string?)w?["details"]?["stage"] == "approval-precheck");
        Assert.Equal(source == "ImportPlcExternalSource" ? 0 : 1, worker.Calls);
    }

    [Theory]
    [InlineData("SaveProject")]
    [InlineData("CloseProject")]
    public async Task Current_session_save_and_close_are_approved_without_changing_catalog_policy(string source)
    {
        var before = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
        try
        {
            var worker = new Worker(); var definition = FoundationTools.Definitions.Single(d => d.Name == source);
            var tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19", null, () => new ApprovalSettings(true, 1));
            var result = await tool.InvokeAsync(Request(tool.ProtocolTool.Name, "{\"dryRun\":false,\"confirm\":true,\"expectedProjectFile\":\"C:/fixture.ap19\"}"));
            Assert.Equal("CONFIRMATION_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal("rejected-before-operation", (string?)result.StructuredContent?["meta"]?["outcome"]);
            Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
            Assert.Equal(1, worker.Calls);

            tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19", null, () => new ApprovalSettings(false, 1));
            result = await tool.InvokeAsync(Request(tool.ProtocolTool.Name, "{\"dryRun\":false,\"confirm\":true,\"expectedProjectFile\":\"C:/fixture.ap19\"}"));
            Assert.Single(result.StructuredContent!["meta"]!["warnings"]!.AsArray(), row => (string?)row?["code"] == "APPROVAL_DISABLED");
        }
        finally { before.Save(ApprovalSettings.SettingsPath); }
    }

    [Theory]
    [InlineData("Connect", "{\"processId\":123}")]
    [InlineData("Disconnect", "{}")]
    public async Task Foundation_connect_and_disconnect_remain_ungated(string source, string arguments)
    {
        var worker = new Worker(); var definition = FoundationTools.Definitions.Single(d => d.Name == source);
        var tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19", null, () => new ApprovalSettings(true, 1));
        var result = await tool.InvokeAsync(Request(tool.ProtocolTool.Name, arguments));
        Assert.NotEqual("CONFIRMATION_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.True(worker.Calls > 0);
    }

    [Theory]
    [InlineData("SaveProject")]
    [InlineData("CloseProject")]
    public async Task Approved_foundation_session_candidate_passes_the_fake_workbench_gate(string source)
    {
        var worker = new Worker(); var definition = FoundationTools.Definitions.Single(d => d.Name == source);
        bool approved = false;
        var tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19", _ => BehaviorPolicy.SafeV4,
            () => new ApprovalSettings(true, 2), (pending, _, _) =>
            {
                Assert.Equal(source, pending.Tool);
                approved = true;
                return Task.FromResult(new ApprovalOutcome(pending, false, null));
            });
        var args = new JsonObject { ["mode"] = "apply", ["confirm"] = true,
            ["expectedPlanHash"] = new string('a', 64), ["expectedProjectFile"] = @"C:\fixture.ap19" };
        var invocation = await tool.InvokeAsync(Request(source, args.ToJsonString()));
        Assert.True(approved);
        Assert.NotEqual("CONFIRMATION_REQUIRED", (string?)invocation.StructuredContent?["error"]?["code"]);
        Assert.True(worker.Calls > 0);
    }

    [Theory]
    [InlineData("granted")]
    [InlineData("denied")]
    [InlineData("timeout")]
    [InlineData("disabled")]
    public async Task Foundation_write_audit_uses_response_id_and_starts_only_after_approval(string decision)
    {
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
        var tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19", _ => BehaviorPolicy.SafeV4,
            () => new ApprovalSettings(enabled, 1), wait);
        var args = new JsonObject { ["mode"] = "apply", ["confirm"] = true,
            ["expectedPlanHash"] = new string('a', 64), ["expectedProjectFile"] = @"C:\fixture.ap19" };
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
        var tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19", null,
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

    [Fact]
    public async Task Foundation_missing_exact_installation_refuses_worker_tools_before_approval()
    {
        var worker = new Worker();
        int waits = 0;
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "CreatePlcTagTable"), worker), "19", null,
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

    private sealed class Worker : IFoundationWorker
    {
        public int Calls;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            Calls++;
            if (operation == "SaveCloseCandidate")
                return Task.FromResult(JsonSerializer.SerializeToNode(new SaveCloseReply { Observation = new SaveCloseObservation {
                    Binding = new SessionState { ProcessId = 1, ProcessStartUtc = DateTimeOffset.Parse("2026-10-05T00:00:00Z"), ProjectFile = @"C:\fixture.ap19", Ownership = "owned", Epoch = 1, WorkerEpoch = 1 },
                    Dirty = false, ObjectValidity = "valid", DisconnectSupported = true, WorkerCleanup = "deferred-until-channel-close" } }));
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
        var worker = new Worker(); var definition = FoundationTools.Definitions.Single(d => d.Name == "ImportBlock");
        var tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19", null, () => new ApprovalSettings(true, 1));
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
        var worker = new Worker(); var definition = FoundationTools.Definitions.Single(d => d.Name == "ImportBlock");
        var tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19", null, () => new ApprovalSettings(true, 1));
        var result = await tool.InvokeAsync(Request("ImportPlcBlock", "{\"unexpected\":true}"));
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal(0, worker.Calls);
    }
}
