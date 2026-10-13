extern alias enginehost;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaOpenness.Shared;
using Xunit;
using Host = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;
using Argument = TiaMcp.FoundationHost.Argument;

public sealed class PlcSimReadinessTests
{
    public static IEnumerable<object[]> Cases =>
        from release in new[] { "14sp1", "20", "21" }
        from name in PortedFamilies.WorkerTools(PortedFamilies.ForTool("ListPlcSimAdvancedInstances"))
        from latched in new[] { false, true }
        select new object[] { release, name, latched };

    [Theory, MemberData(nameof(Cases))]
    public async Task Simulator_runs_without_a_project_and_despite_a_project_session_latch(string release, string name, bool latched)
    {
        using var engine = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(engine, release);
        using var simulator = new Simulator { Poisoned = latched };
        if (latched) Host.MarkSharedSessionUncertain();
        Assert.Null(Host.SessionPrecheckRefusal(name));
        int approvals = 0;
        var tool = Tool(simulator, release, name, (pending, _, _) => {
            approvals++; return Task.FromResult(new ApprovalOutcome(pending, false, null)); });
        var body = (await tool.InvokeAsync(Request(name, Arguments(name)))).StructuredContent!;
        Assert.True((bool)body["ok"]!, body.ToJsonString());
        Assert.Equal(1, simulator.Executions);
        Assert.Equal(0, simulator.ProjectCalls);
        Assert.Equal(name is "ManagePlcSimAdvancedInstance" or "WritePlcSimAdvancedTags" or "RunPlcSimAdvancedTestScenario" ? 1 : 0, approvals);
        Assert.Equal(0, simulator.UncertainMarks);
        if (latched)
        {
            Assert.Equal("SESSION_RESET_REQUIRED", (string?)Host.SessionPrecheckRefusal("GetSessionState")?.StructuredContent?["error"]?["code"]);
            var project = new FoundationV4Tool(new FoundationTool(new Definition("GetProject", "Project", "Fixture project read.", Array.Empty<Argument>()), simulator), release);
            var refused = (await project.InvokeAsync(Request("GetProjectInfo", new JsonObject()))).StructuredContent!;
            Assert.Equal("SESSION_RESET_REQUIRED", (string?)refused["error"]?["code"]);
            Assert.Equal(0, simulator.ProjectCalls);
        }
    }

    public static IEnumerable<object[]> Gated => from release in new[] { "14sp1", "20", "21" }
        from name in new[] { "WritePlcSimAdvancedTags", "RunPlcSimAdvancedTestScenario" }
        select new object[] { release, name };

    [Theory, MemberData(nameof(Gated))]
    public async Task Approval_remains_required_even_when_the_project_is_latched(string release, string name)
    {
        using var engine = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(engine, release);
        using var simulator = new Simulator { Poisoned = true };
        int approvals = 0;
        var tool = Tool(simulator, release, name, (pending, _, _) => {
            approvals++; return Task.FromResult(new ApprovalOutcome(pending, false, "denied")); });
        var body = (await tool.InvokeAsync(Request(name, Arguments(name)))).StructuredContent!;
        Assert.Equal("CONFIRMATION_REQUIRED", (string?)body["error"]?["code"]);
        Assert.Equal(1, approvals);
        Assert.Equal(0, simulator.Executions);
        Assert.Equal(1, simulator.Previews);
        Assert.Equal(0, simulator.ProjectCalls);
    }

    [Theory, MemberData(nameof(Gated))]
    public async Task Unknown_simulator_outcome_does_not_latch_the_TIA_project(string release, string name)
    {
        using var engine = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(engine, release);
        using var simulator = new Simulator { Unknown = true };
        var tool = Tool(simulator, release, name, (pending, _, _) => Task.FromResult(new ApprovalOutcome(pending, false, null)));
        var result = await tool.InvokeAsync(Request(name, Arguments(name)));
        Assert.Equal("unknown", (string?)result.StructuredContent!["meta"]?["outcome"]);
        Assert.False((bool)result.StructuredContent["meta"]!["requiresSessionReset"]!);
        Assert.Equal(1, simulator.Executions);
        Assert.Equal(0, simulator.UncertainMarks);
        Host.ObserveSessionOutcome(name, result, write: true, nativeCallIssued: true);
        Assert.Null(Host.SessionPrecheckRefusal("GetSessionState"));
        Assert.False(simulator.Poisoned);
    }

    private static FoundationV4Tool Tool(Simulator worker, string release, string name,
        Func<PendingApproval, ApprovalSettings, CancellationToken, Task<ApprovalOutcome>> approval)
        => new(PortedToolContract.Create(worker, release).OfType<FoundationTool>().Single(tool => tool.ProtocolTool.Name == name),
            release, null, () => new ApprovalSettings(true, 1), approval,
            () => new JsonObject { ["ready"] = false, ["cause"] = "Synthetic no TIA installation." });

    private static JsonObject Arguments(string name) => name switch {
        "ListPlcSimAdvancedInstances" => new(),
        "GetPlcSimAdvancedTags" => new() { ["instanceName"] = "OwnedFixture" },
        "ManagePlcSimAdvancedInstance" => new() { ["instanceName"] = "OwnedFixture", ["action"] = "stop", ["confirmInstanceChange"] = true, ["dryRun"] = false },
        "WritePlcSimAdvancedTags" => new() { ["instanceName"] = "OwnedFixture", ["values"] = new JsonArray(new JsonObject { ["name"] = "FixtureInput", ["value"] = true }), ["confirmWrite"] = true, ["dryRun"] = false },
        _ => new() { ["scenario"] = new JsonObject { ["instance"] = "OwnedFixture", ["mode"] = "default", ["steps"] = new JsonArray(new JsonObject { ["waitMs"] = 1 }) }, ["confirmRun"] = true, ["dryRun"] = false }
    };

    [Fact]
    public void Simulator_wire_is_validated_without_a_project_reset_field()
    {
        const string operation = "runtime-channels.ExecutePlcSimAdvanced";
        var reply = new JsonObject { ["Ok"] = false, ["Message"] = "Unknown synthetic write.", ["Data"] = new JsonObject(),
            ["Meta"] = new JsonObject { ["success"] = false, ["outcomeUnknown"] = true } };
        var state = new WorkerOutcomeState();
        state.AcceptResult(operation, new JsonObject(), reply);
        Assert.False(state.Poisoned);
        reply["RequiresSessionReset"] = true;
        Assert.Throws<IOException>(() => state.AcceptResult(operation, new JsonObject(), reply));
        reply.Remove("RequiresSessionReset"); reply["Ok"] = "malformed";
        Assert.Throws<IOException>(() => state.AcceptResult(operation, new JsonObject(), reply));
    }
    private static RequestContext<CallToolRequestParams> Request(string name, JsonObject arguments) =>
        new(System.Reflection.DispatchProxy.Create<IMcpServer, VersionControlAdmissionTests.ServerProxy>()) {
            Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) }
        };

    private sealed class Simulator : IFoundationSessionWorker
    {
        public bool Poisoned { get; set; }
        internal bool Unknown;
        internal int Executions, Previews, ProjectCalls, UncertainMarks;
        public string ApprovalIdentity => "{}";
        public bool Bundled => true;
        public bool SharedSession => false;
        public TiaMcp.Logic.ModelContextProtocol.ImportStagingSession? StagingOwner => null;
        public IDisposable? EnterRequest(RequestContext<CallToolRequestParams> request) => null;
        public Task<IDisposable?> AcquireLane(CancellationToken token) => Task.FromResult<IDisposable?>(null);
        public void ActivateLane(IDisposable? lane) { }
        public void MarkUncertain() { UncertainMarks++; Poisoned = true; }
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            if (operation != "runtime-channels.ExecutePlcSimAdvanced") { ProjectCalls++; throw new InvalidOperationException("A simulator call must not touch project state."); }
            bool preview = (bool?)arguments["request"]?["arguments"]?["dryRun"] == true;
            if (preview) Previews++; else Executions++;
            bool unknown = Unknown && !preview;
            return Task.FromResult<JsonNode?>(new JsonObject { ["Ok"] = !unknown, ["Message"] = "Synthetic simulator result.",
                ["Data"] = new JsonObject { ["instance"] = "OwnedFixture" },
                ["Meta"] = new JsonObject { ["success"] = !unknown, ["operationSuccess"] = !unknown,
                    ["mayHaveChanged"] = unknown, ["outcomeUnknown"] = unknown, ["dryRun"] = preview } });
        }
        public void Dispose() { }
    }
}
