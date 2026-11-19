extern alias toolpipeline;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer;
using Xunit;

namespace TiaMcp.FoundationHost.Tests;

public sealed class UnsettledStateTests
{
    [Fact]
    public async Task CallTool_records_only_its_target_even_when_the_wrapper_has_its_own_request_id()
    {
        using var context = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(
            new toolpipeline::TiaMcp.FoundationHost.EngineSessionContext(new SharedPipelineTests.Worker()));
        using var scope = new SharedPipelineTests.Scope();
        const string name = "CreatePlcTag";
        scope.Worker.FoundationUnknown = true;
        var services = FoundationHostPorts.CreateServices(scope.Worker, "21", () => new JsonObject { ["ready"] = true }, null);
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", services, new[] { scope.Foundation(name) });
        using var session = pipeline.EnterSession();
        var args = new JsonObject { ["plc"] = "PLC_1", ["table"] = "T", ["name"] = "Ready", ["dataType"] = "Bool", ["address"] = "%M0.0",
            ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = "C:/fixture.ap21" };
        var invocation = new JsonObject { ["name"] = name, ["arguments"] = args };
        var result = await pipeline.AllTools.Single(tool => tool.ProtocolTool.Name == "CallTool").InvokeAsync(Request("CallTool", invocation));
        Assert.Equal("OUTCOME_UNKNOWN", (string?)result.StructuredContent?["error"]?["code"]);
        var target = Assert.Single(scope.Worker.Unsettled.Snapshot());
        Assert.Equal(name, target.Tool);
        Assert.Equal(scope.Worker.FoundationId, target.RequestId);
        var outer = result.StructuredContent!.DeepClone();
        outer["meta"]!["requestId"] = "wrapper-id";
        var observed = new CallToolResult { StructuredContent = outer, IsError = true,
            Content = new[] { new TextContentBlock { Text = outer.ToJsonString() } } };
        Host.ObserveSessionOutcome("CallTool", observed, true, invocation.ToJsonString());
        Assert.Equal(target, Assert.Single(scope.Worker.Unsettled.Snapshot()));
        Assert.Equal(1, scope.Worker.FoundationWrites);
        var save = Host.ObserveSessionOutcome("CallTool", Host.V4Result("SaveProject", new JsonObject()), true,
            """{"name":"SaveProject","arguments":{}}""");
        Assert.Contains(save.StructuredContent!["meta"]!["warnings"]!.AsArray(), item => (string?)item?["code"] == "UNSETTLED_CALLS");
    }

    [Fact]
    public async Task An_exited_portal_refuses_bind_and_takeover_before_dispatch_and_clears_unsettled_calls()
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
        TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter.20.dll"), "offline session fixture");
        using var holder = new WorkerClient("20", executable, AppContext.BaseDirectory, true) { Bundled = false };
        using var worker = new WorkerClient("20", executable, AppContext.BaseDirectory, true) { Bundled = false };
        await holder.Call("FixtureCorrelation", new(), CancellationToken.None);
        int pid = (int)holder.OperatorSnapshot()["pid"]!;
        worker.Unsettled.Record("unbound", "WritePlcSclSourceFile", "local-file", DateTimeOffset.UtcNow);
        await worker.Call("Attach", new() { ["processId"] = pid }, CancellationToken.None);
        worker.Unsettled.Record("unknown-target", "CreatePlcTag", "digest", DateTimeOffset.UtcNow);
        Assert.Null(worker.ExitedBinding());
        Assert.Equal(2, worker.Unsettled.Snapshot().Count);
        const string retained = "retained-target";
        worker.Unsettled.Record(retained, "CreatePlcTag", "retained-digest", DateTimeOffset.UtcNow);
        worker.LateResults.RecordUnknown(retained, "CreatePlcTag", "retained-digest", FoundationV4Result.LateFailure("20", "CreatePlcTag", retained));
        const string late = "completed-after-exit";
        worker.Unsettled.Record(late, "CreatePlcTag", "late-digest", DateTimeOffset.UtcNow);
        using var child = System.Diagnostics.Process.GetProcessById(pid);
        Assert.Equal(executable, child.MainModule!.FileName, ignoreCase: true);
        holder.Dispose();
        Assert.True(child.HasExited);
        foreach (string operation in new[] { "BindProject", "Attach" })
        {
            var args = operation == "Attach" ? new JsonObject { ["processId"] = Environment.ProcessId, ["takeOver"] = true }
                : new JsonObject { ["projectName"] = "G", ["expectedProjectFile"] = "C:/fixture/G.ap20" };
            var refused = await Assert.ThrowsAsync<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() => worker.Call(operation, args,
                CancellationToken.None));
            var result = FoundationV4Result.Failure("20", operation == "Attach" ? "ConnectPortal" : "AttachOpenProject",
                "refused", true, true, null, refused).StructuredContent!;
            Assert.Equal("PRECONDITION_FAILED", (string?)result["error"]?["code"]);
            Assert.Equal("session-lost", (string?)result["error"]?["details"]?["condition"]);
            Assert.Contains("DisconnectPortal", (string?)result["error"]?["message"]);
            Assert.Equal(false, refused.Data["foundationRequestSent"]);
        }
        var state = SharedSessionState.For(worker).RuntimeData("20");
        Assert.False((bool?)state["isAttached"]);
        Assert.Null(state["processId"]);
        Assert.Equal("unbound", (string?)Assert.Single(state["sessionState"]!["unsettled"]!.AsArray())!["requestId"]);
        var services = FoundationHostPorts.CreateServices(worker, "20", () => new JsonObject(), null);
        var getState = new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "GetSessionState"), worker);
        var stateTool = new FoundationV4Tool(getState, "20");
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("20", services, new[] { stateTool });
        using var context = pipeline.EnterSession();
        var current = await stateTool.InvokeAsync(Request("GetSessionState"));
        var settled = current.StructuredContent!["data"]!["sessionState"]!["settled"]!.AsArray();
        Assert.Equal(3, settled.Count);
        worker.LateResults.RecordUnknown(late, "CreatePlcTag", "late-digest", FoundationV4Result.LateFailure("20", "CreatePlcTag", late));
        var saved = worker.Unsettled.WarnOnSave("SaveProject", Host.V4Result("SaveProject", new JsonObject()).StructuredContent!);
        var warning = Assert.Single(saved["meta"]!["warnings"]!.AsArray(), w => (string?)w?["code"] == "UNSETTLED_CALLS");
        foreach (var (id, digest) in new[] { ("unknown-target", "digest"), (retained, "retained-digest"), (late, "late-digest") })
        {
            var row = Assert.Single(settled, item => (string?)item?["requestId"] == id);
            Assert.Equal("process-exited", (string?)row!["outcome"]);
            Assert.Contains("still unknown", (string?)row["message"]);
            Assert.Contains("Inspect the project", (string?)row["message"]);
            Assert.Contains(warning!["details"]!["unsettled"]!.AsArray(), item => (string?)item?["requestId"] == id);
            Assert.Equal(id, worker.Unsettled.FindWrite(digest)?.RequestId);
            var result = Host.GetCallResult(id).StructuredContent!;
            Assert.Equal("unknown", (string?)result["meta"]?["outcome"]);
            Assert.True(JsonNode.DeepEquals(row, result["data"]?["settlement"]), $"Session row {row} and call result {result} differ.");
            Assert.Null(worker.Unsettled.FindWrite(digest));
        }
        Assert.Equal("unbound", (string?)Assert.Single(worker.Unsettled.SaveWarnings())!["requestId"]);
        await worker.Call("Disconnect", new(), CancellationToken.None);
        await worker.Call("Attach", new() { ["processId"] = Environment.ProcessId }, CancellationToken.None);
        Assert.Equal(Environment.ProcessId, (int?)SharedSessionState.For(worker).RuntimeData("20")["processId"]);
    }

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
        var schema = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.Current.Catalog!.All.TryGetValue(name, out var descriptor)
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
        Assert.NotEqual("unknown", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.Null(result.StructuredContent?["data"]?["recovery"]);
    }

    private static RequestContext<CallToolRequestParams> Request(string name, JsonObject? arguments = null) => new(
        DispatchProxy.Create<IMcpServer, WorkerRobustnessTests.ServerProxy>()) {
            Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                (arguments ?? new JsonObject()).ToJsonString()) } };
}
