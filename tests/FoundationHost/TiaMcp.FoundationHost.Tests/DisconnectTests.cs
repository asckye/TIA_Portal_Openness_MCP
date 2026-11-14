extern alias enginehost;
extern alias foundationhost;
using HostSession = foundationhost::TiaMcp.FoundationHost.HostSession;
using TiaMcp.Tests.Shared;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using TiaMcp.Adapters;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

internal static class DisconnectTests
{
    internal static async Task Run(IMcpServer server,Action<bool,string> check,[System.Runtime.CompilerServices.CallerFilePath] string sourceFile="")
    {
        void Reject(Action action,string title) {
            try { action(); throw new Exception("Accepted: "+title);
            } catch(Exception ex) when(ex is InvalidOperationException or IOException or NotSupportedException) { check(true,title); }
        }
        foreach(var bound in new[]{false,true})
        {
            var lifecycle=new PlcLifecycleState(); lifecycle.Attached(123);
            if(bound) lifecycle.Bound("C:/Projects/Test.ap17",false,false);
            var state=new PlcDisconnectState(); int detaches=0;
            var result=state.Execute(lifecycle.ProcessId,false,()=>detaches++);
            lifecycle.Detached();
            check(result.Detached && result.ProcessId==123 && result.WorkerAcknowledged && !result.SavedProject && !result.ClosedProject,
                    $"Acknowledged non-owning detach bound={bound}");
            check(detaches==1 && lifecycle.ProjectFile==null,"No project prerequisite or save/close callback");
            check(ReferenceEquals(state.Execute(null,null,()=>detaches++),result) && detaches==1,"Repeated disconnect never calls native again");
            Reject(state.RequireActive,"Subsequent explicit attach requires new session");
            DisconnectContract.Validate(JsonSerializer.SerializeToNode(result),true,123,true);
        }
        foreach(bool? ownership in new bool?[]{true,null})
        {
            var state=new PlcDisconnectState(); int calls=0;
            Reject(()=>state.Execute(123,ownership,()=>calls++),"Owned/unknown portal semantics gate");
            check(!state.Attempted && calls==0,"Ownership rejection precedes native disposal");
        }
        var unbound=new PlcLifecycleState();unbound.Attached(123);unbound.Bound("C:/Projects/Test.ap17",true,false);unbound.Unbound();
        check(new PlcDisconnectState().Execute(unbound.ProcessId,false,()=>{}).Detached,"Explicitly unbound attachment can disconnect");
        var engine=SourceFamily
            .Read(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!,"../../../src/Adapters/Native/Session/PlcFoundationEngine.cs")));
        var disconnectSource=engine.Split("public PlcDisconnectResult Disconnect()")[1].Split("public void Dispose()")[0];
        check(!disconnectSource.Contains("Project()") && !disconnectSource.Contains("IsModified") && !disconnectSource.Contains(".Save(") && !disconnectSource
                .Contains(".Close("),"Dirty/bound project is not inspected, saved or closed by Disconnect source");
        check(engine.Split("public void Dispose()")[1].Contains("if (!disconnect.Attempted) Disconnect();"),
                "Final Dispose source does not retry a failed explicit detach");
        var idle=new PlcDisconnectState();
        check(!idle.Execute(null,null,()=>throw new Exception("Must not detach")).Detached,"Worker idle acknowledged without native action");
        var failed=new PlcDisconnectState();int attempts=0;
        Reject(()=>failed.Execute(123,false,()=>{attempts++;throw new IOException("uncertain");}),"Native disposal failure retained");
        Reject(()=>failed.Execute(123,false,()=>attempts++),"Failed disconnect not retried");
        check(failed.Attempted && failed.Result==null && attempts==1,"Uncertain state retained without success acknowledgement");
        var valid=JsonSerializer.SerializeToNode(new PlcDisconnectState().Execute(123,false,()=>{}))!;
        foreach(var key in valid.AsObject().Select(x=>x.Key).ToArray())
        { var bad=valid.DeepClone().AsObject();bad.Remove(key);Reject(()=>DisconnectContract.Validate(bad,true,123,true),"Missing ack field rejected: "+key); }
        Reject(()=>DisconnectContract.Validate(valid,true,456,true),"Wrong selected PID acknowledgement rejected");
        Reject(()=>DisconnectContract.Validate(DisconnectContract.Idle(),true),"Host idle response cannot masquerade as worker acknowledgement");
        foreach(var enabled in new[]{false,true})
        {
            using var worker=new WorkerClient("17","/missing/never-launch-worker","/missing/no-api",enabled);
            var ack=await worker.Call("Disconnect",new JsonObject(),CancellationToken.None);
            DisconnectContract.Validate(ack,false,null,true);
            check(JsonNode.DeepEquals(ack,await worker.Call("Disconnect",new JsonObject(),CancellationToken.None)),
                    "Host idle disconnect repeats without launch");
            check(worker.OperatorSnapshot()["pid"] == null, "Idle disconnect leaves the configured worker unstarted");
        }
        string reconnectExe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
        TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(AppContext.BaseDirectory,
            "TiaMcp.Adapter.17.dll"), "offline session fixture");
        using (var worker = new WorkerClient("17", reconnectExe, AppContext.BaseDirectory, true) { Bundled = false })
        {
            DisconnectContract.Validate(await worker.Call("Disconnect", new(), CancellationToken.None), false, null, true);
            var connected = await worker.Call("Attach", new() { ["processId"] = 123 }, CancellationToken.None);
            check((string?)connected?["Stage"] == "attached", "Reconnect after idle disconnect succeeds through the fixture");
            check((long?)worker.OperatorSnapshot()["generation"] == 2, "Idle reconnect starts the next worker generation");
            DisconnectContract.Validate(await worker.Call("Disconnect", new(), CancellationToken.None), true, 123, true);
        }
        string gate = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
            "bin-build", "disconnect-row-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(gate);
        string? previousGate = Environment.GetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE");
        Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE", gate);
        try
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
            TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter.17.dll"),
                "offline session fixture");
            using var worker = new WorkerClient("17", exe, AppContext.BaseDirectory, true) { Bundled = false };
            await worker.Call("Attach", new JsonObject { ["processId"] = 123 }, CancellationToken.None);
            var running = worker.Call("FixtureHang", new JsonObject(), CancellationToken.None);
            check(SpinWait.SpinUntil(() => File.Exists(Path.Combine(gate, "dispatched")),
                TiaMcp.Engine.Tests.Shared.TestDeadlines.Hang), "Native call reached the disconnect gate");
            int pid = (int)worker.OperatorSnapshot()["pid"]!;
            var pending = worker.Call("Disconnect", new JsonObject(), CancellationToken.None);
            check(pending.IsCompleted,"Disconnect promptly refuses an occupied serialization gate");
            var refused = await Assert.ThrowsAsync<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() => pending);
            check(refused.Condition == "worker-call-running", "Disconnect refuses while the native call is running");
            check((int?)worker.OperatorSnapshot()["pid"] == pid && !System.Diagnostics.Process.GetProcessById(pid).HasExited,
                "Disconnect refusal preserves the running worker PID");
            File.WriteAllText(Path.Combine(gate, "release"), "");
            await running.WaitAsync(TiaMcp.Engine.Tests.Shared.TestDeadlines.Hang);
            var terminal = await worker.Call("Disconnect", new JsonObject(), CancellationToken.None);
            DisconnectContract.Validate(terminal, true, 123, true);
            check(terminal?["WorkerAcknowledged"]?.GetValue<bool>() == true && terminal?["Detached"]?.GetValue<bool>() == true,
                "Worker acknowledges and detaches after the native call ends");
            check(terminal?["RequiresTiaRestart"] == null,"Disconnect has no TIA restart field");
        }
        finally
        {
            File.WriteAllText(Path.Combine(gate, "release"), "");
            Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE", previousGate);
        }
        using(var worker=new WorkerClient("17","/missing/never-launch-worker","/missing/no-api",false))
        {
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            try { await worker.Call("Disconnect",new JsonObject(),cancelled.Token);throw new Exception("Cancellation ignored"); }
            catch(OperationCanceledException) { check(true,"Pre-send cancellation has no disconnect outcome"); }
            DisconnectContract.Validate(await worker.Call("Disconnect",new JsonObject(),CancellationToken.None),false,null,true);
        }
        var fake=new FakeWorker();var tool=FoundationTools.Create(fake).Single(t=>t.ProtocolTool.Name=="DisconnectPortal");
        var request=new RequestContext<CallToolRequestParams>(server) {
            Params=new CallToolRequestParams{Name="DisconnectPortal",Arguments=new Dictionary<string,JsonElement>()} };
        await tool.InvokeAsync(request);
        check(fake.Operation=="Disconnect" && fake.Arguments!.Count==0,"Public Disconnect requires no project or dryRun");
    }
}

[Collection(WorkerDataDirectoryCollection.Name)]
public sealed class FoundationRecoveryTests
{
    [Fact]
    public Task Disconnect_contract_checks_run_through_the_registered_tool()
        => DisconnectTests.Run(System.Reflection.DispatchProxy.Create<IMcpServer, ServerProxy>(),
            (passed, message) => Assert.True(passed, message));

    [Fact]
    public async Task Unsettled_transport_count_is_bounded_while_the_session_retains_all_evidence()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
        TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(AppContext.BaseDirectory,
            "TiaMcp.Adapter.19.dll"), "offline session fixture");
        using var worker = new WorkerClient("19", exe, AppContext.BaseDirectory, true) { Bundled = false };
        for (int index = 0; index < 4097; index++)
            worker.Unsettled.Record("prior-" + index, "FixtureWrite", null, DateTimeOffset.UtcNow);
        var previousError = Console.Error;
        using var errors = new StringWriter();
        try
        {
            Console.SetError(errors);
            for (int call = 0; call < 2; call++)
            {
                var reply = await worker.Call("FixtureCorrelation", new(), CancellationToken.None);
                Assert.Equal(4096, (int?)reply?["unsettledCount"]);
                Assert.Equal(4097, worker.Unsettled.Snapshot().Count);
                Assert.False(worker.Faulted);
            }
            await worker.Call("Disconnect", new(), CancellationToken.None);
            Assert.Single(errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
        finally { Console.SetError(previousError); }
    }

    [Fact]
    public async Task Unsettled_metadata_frame_limit_refuses_before_dispatch_and_keeps_the_worker_usable()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
        TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(AppContext.BaseDirectory,
            "TiaMcp.Adapter.19.dll"), "offline session fixture");
        using var worker = new WorkerClient("19", exe, AppContext.BaseDirectory, true) { Bundled = false };
        await worker.Call("ReadState", new(), CancellationToken.None);
        int pid = (int)worker.OperatorSnapshot()["pid"]!;
        foreach (char prefix in new[] { 'a', 'b' })
            worker.Unsettled.Record(new string(prefix, 128), "FixtureWrite", null, DateTimeOffset.UtcNow);
        var arguments = new JsonObject { ["value"] = new string('x', TiaMcp.WorkerChannel.ChannelLimits.RequestBytes - 256) };
        await Assert.ThrowsAsync<TiaMcp.WorkerChannel.ChannelLimitException>(() =>
            worker.Call("FixtureMetadata", arguments, CancellationToken.None));
        Assert.False(worker.Faulted);
        Assert.Equal(pid, (int?)worker.OperatorSnapshot()["pid"]);
        Assert.NotNull(await worker.Call("ReadState", new(), CancellationToken.None));
    }

    [Fact]
    public async Task Lost_reconnect_reply_keeps_the_dispatched_unknown_outcome_in_the_new_generation()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
        TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(AppContext.BaseDirectory,
            "TiaMcp.Adapter.19.dll"), "offline session fixture");
        using var worker = new WorkerClient("19", exe, AppContext.BaseDirectory, true) { Bundled = false };
        await worker.Call("ReadState", new(), CancellationToken.None);
        await worker.Call("ReadState", new(), CancellationToken.None);
        await worker.Call("Disconnect", new(), CancellationToken.None);
        var error = await Assert.ThrowsAsync<TiaMcp.WorkerChannel.ChannelFaultException>(() =>
            worker.Call("Attach", new() { ["processId"] = 905 }, CancellationToken.None));
        Assert.True(error.OutcomeUnknown);
        Assert.Equal(true, error.Data["foundationRequestSent"]);
        Assert.Equal(true, error.Data["workerOutcomeUnknown"]);
        var body = FoundationV4Result.Failure("19", "ConnectPortal", "reconnect", true, true, null, error).StructuredContent!;
        Assert.Equal("unknown", (string?)body["meta"]?["outcome"]);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
        Assert.Equal(2, (long?)worker.OperatorSnapshot()["generation"]);
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"),
        InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Connect_after_disconnect_works_in_the_same_session(string release)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
        TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(AppContext.BaseDirectory,
            "TiaMcp.Adapter." + release + ".dll"), "offline session fixture");
        using var worker = new WorkerClient(release, exe, AppContext.BaseDirectory, true) {
            Bundled = false, SharedSession = release is "20" or "21" };
        var services = FoundationHostPorts.CreateServices(worker, release, () => new() { ["ready"] = true }, AppContext.BaseDirectory);
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline(release, services, Array.Empty<McpServerTool>());
        using var engineSession = pipeline.EnterSession();
        var tools = new[] { "ConnectPortal", "AttachOpenProject", "DisconnectPortal", "GetSessionState" }
            .Select(name => (ModelContextProtocol.Server.McpServerTool)new FoundationV4Tool(
                new FoundationTool(FoundationTools.Definitions.Single(definition => definition.Name == name), worker),
                release, () => new(false, 1), readinessForTest: () => new JsonObject { ["ready"] = true })).ToArray();
        using var session = new HostSession(release, "fixture", false, tools, tools,
            () => SharedSessionState.For(worker).OperatorSnapshot(release), worker.Dispose,
            lateResults: worker.LateResults, unsettled: worker.Unsettled);
        string sessionKey = session.SessionKey;
        async Task<JsonNode> Call(string name, object arguments)
        {
            var request = new RequestContext<CallToolRequestParams>(
                System.Reflection.DispatchProxy.Create<IMcpServer, WorkerRobustnessTests.ServerProxy>()) {
                Params = new() { Name = name,
                    Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(arguments)) } };
            var result = (await session.Tools.Single(tool => tool.ProtocolTool.Name == name).InvokeAsync(request)).StructuredContent!;
            Assert.True((bool?)result["ok"], result.ToJsonString());
            return result;
        }
        await Call("ConnectPortal", new { processId = 123 });
        await Call("AttachOpenProject", new { projectName = "fixture", expectedProjectFile = "C:/fixture.ap" + release });
        int previousPid = (int)worker.OperatorSnapshot()["pid"]!;
        var disconnected = await Call("DisconnectPortal", new { });
        Assert.Equal("disconnected", (string?)disconnected["data"]?["sessionState"]);
        await Call("GetSessionState", new { });
        await Call("ConnectPortal", new { processId = 123 });
        await Call("AttachOpenProject", new { projectName = "fixture", expectedProjectFile = "C:/fixture.ap" + release });
        Assert.NotEqual(previousPid, (int?)worker.OperatorSnapshot()["pid"]);
        Assert.Equal(2, (long?)worker.OperatorSnapshot()["generation"]);
        Assert.Equal(sessionKey, session.SessionKey);
        Assert.False((bool?)session.Snapshot()["sessionState"]?["disconnected"]);
        await Call("DisconnectPortal", new { });
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"),
        InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Damaged_previous_owner_record_admits_and_native_calls_succeed(string release)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "damaged-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string record = "BUSY a,b,c\nb\n";
        string? previous = Environment.GetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT");
        Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT", root);
        try
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
            TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(AppContext.BaseDirectory,
                "TiaMcp.Adapter." + release + ".dll"), "offline session fixture");
            foreach (string contents in new[] { record, "BUSY a," + new string('x', 129) + ",bad/id,b\n" })
            {
                File.WriteAllText(Path.Combine(root, "123-1.lease"), contents);
                using var worker = new WorkerClient(release, exe, AppContext.BaseDirectory, true) { Bundled = false };
                var raw = await worker.Call("Attach", new() { ["processId"] = 123 }, CancellationToken.None);
                var body = FoundationV4Result.Worker(release, FoundationTools.Definitions.Single(row => row.Name == "ConnectPortal"),
                    "damaged-owner", new(), raw, null).StructuredContent!;
                Assert.Contains(body["meta"]!["warnings"]!.AsArray(), warning => (string?)warning?["code"] == "PREVIOUS_OWNER_UNCLEAN");
                string[] expected = contents == record ? new[] { "a", "b", "c" } : new[] { "a", "b" };
                Assert.Equal(expected, worker.Unsettled.Snapshot().Select(call => call.RequestId));
                Assert.All(worker.Unsettled.Snapshot(), call => Assert.True(call.FromPreviousOwner));
                Assert.NotNull(await worker.Call("ReadState", new(), CancellationToken.None));
                Assert.NotNull(await worker.Call("SaveProject", new() { ["dryRun"] = true }, CancellationToken.None));
                Assert.False(worker.Faulted);
                await worker.Call("Disconnect", new(), CancellationToken.None);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT", previous);
            Directory.Delete(root, true);
        }
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"),
        InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Discovery_and_isolated_project_calls_start_a_new_generation_after_disconnect(string release)
    {
        string extension = release == "14sp1" ? "14" : release == "15.1" ? "15_1" : release;
        string inputPath = Path.Combine(AppContext.BaseDirectory, "review-open-" + Guid.NewGuid().ToString("N") + ".ap" + extension);
        File.WriteAllText(inputPath, "Offline project fixture for the synthetic worker.");
        try
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
            TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(AppContext.BaseDirectory,
                "TiaMcp.Adapter." + release + ".dll"), "offline session fixture");
            using var worker = new WorkerClient(release, exe, AppContext.BaseDirectory, true) {
                Bundled = false, SharedSession = release is "20" or "21" };
            var services = FoundationHostPorts.CreateServices(worker, release, () => new() { ["ready"] = true }, AppContext.BaseDirectory);
            using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline(release, services, Array.Empty<McpServerTool>());
            using var engineSession = pipeline.EnterSession();
            await worker.Call("ReadState", new(), CancellationToken.None);
            int previousPid = (int)worker.OperatorSnapshot()["pid"]!;
            await worker.Call("Disconnect", new(), CancellationToken.None);
            var tool = new FoundationV4Tool(new FoundationTool(
                FoundationTools.Definitions.Single(row => row.Name == "ListPortalProcessProjects"), worker), release,
                () => new(false, 1), readinessForTest: () => new JsonObject { ["ready"] = true });
            var request = new RequestContext<CallToolRequestParams>(System.Reflection.DispatchProxy.Create<IMcpServer, ServerProxy>()) {
                Params = new() { Name = "ListPortalProcessProjects", Arguments = new Dictionary<string, JsonElement>() } };
            var discovered = (await tool.InvokeAsync(request)).StructuredContent!;
            Assert.True((bool?)discovered["ok"], discovered.ToJsonString());
            Assert.NotEqual(previousPid, (int?)worker.OperatorSnapshot()["pid"]);
            Assert.Equal(2, (long?)worker.OperatorSnapshot()["generation"]);
            foreach (string operation in new[] { "CreateProject", "OpenProject" })
            {
                await worker.Call("Disconnect", new(), CancellationToken.None);
                previousPid = (int)worker.OperatorSnapshot()["pid"]!;
                Assert.NotNull(await worker.Call("project-lifecycle.ConnectIsolatedPortal", new(), CancellationToken.None));
                Assert.NotEqual(previousPid, (int?)worker.OperatorSnapshot()["pid"]);
                var projectTool = new FoundationV4Tool(new FoundationTool(
                    FoundationTools.Definitions.Single(row => row.Name == operation), worker), release,
                    () => new(false, 1), readinessForTest: () => new JsonObject { ["ready"] = true });
                var arguments = operation == "CreateProject"
                    ? new JsonObject { ["directoryPath"] = AppContext.BaseDirectory, ["projectName"] = "fixture", ["dryRun"] = true }
                    : new JsonObject { ["path"] = inputPath, ["dryRun"] = true };
                var projectRequest = new RequestContext<CallToolRequestParams>(System.Reflection.DispatchProxy.Create<IMcpServer, ServerProxy>()) {
                    Params = new() { Name = operation,
                        Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } };
                var project = (await projectTool.InvokeAsync(projectRequest)).StructuredContent!;
                Assert.True((bool?)project["ok"], project.ToJsonString());
                Assert.Equal("read-only", (string?)project["meta"]?["execution"]);
                Assert.False(worker.Faulted);
            }
            Assert.Equal(4, (long?)worker.OperatorSnapshot()["generation"]);
            await worker.Call("Disconnect", new(), CancellationToken.None);
        }
        finally { File.Delete(inputPath); }
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"),
        InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Bound_operations_report_the_normal_precondition_in_the_next_generation(string release)
    {
        using var worker = new WorkerClient(release, "/missing/never-launch-worker", "/missing/no-api", true);
        await worker.Call("Disconnect", new(), CancellationToken.None);
        var failure = await Assert.ThrowsAsync<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() =>
            worker.Call("SaveProject", new() { ["dryRun"] = true }, CancellationToken.None));
        Assert.Equal(new TiaMcp.Adapters.Contracts.AdapterPreconditionException(
            TiaOpenness.Shared.SessionBehavior.PortalRequired, "session", false).Message, failure.Message);
        Assert.Equal("session", failure.ParamName);
        Assert.Null(failure.Condition);
        Assert.Equal(2, (long?)worker.OperatorSnapshot()["generation"]);
        Assert.Null(worker.OperatorSnapshot()["pid"]);
    }

    public static IEnumerable<object[]> PreviousOwnerCases()
        => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
            .SelectMany(release => new[] { "BUSY", "UNCERTAIN", "ACTIVE", "RELEASED" }.Select(state => new object[] { release, state }));

    [Theory, MemberData(nameof(PreviousOwnerCases))]
    public async Task Dead_holder_unknown_is_unsettled_in_the_next_session(string release, string state)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "previous-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "123-1.lease"), state + " prior-write\n");
        string? previous = Environment.GetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT");
        Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT", root);
        try
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
            TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(AppContext.BaseDirectory,
                "TiaMcp.Adapter." + release + ".dll"), "offline session fixture");
            using var worker = new WorkerClient(release, exe, AppContext.BaseDirectory, true) { Bundled = false };
            var definition = FoundationTools.Definitions.Single(row => row.Name == "ConnectPortal");
            var raw = await worker.Call("Attach", new JsonObject { ["processId"] = 123 }, CancellationToken.None);
            var body = FoundationV4Result.Worker(release, definition, "connect", new(), raw, null).StructuredContent!;
            Assert.Equal(state, (string?)body["data"]?["previousOwner"]?["state"]);
            Assert.Equal("prior-write", (string?)body["data"]?["previousOwner"]?["requests"]?[0]);
            Assert.Equal(state != "RELEASED", body["meta"]!["warnings"]!.AsArray()
                .Any(w => (string?)w?["code"] == "PREVIOUS_OWNER_UNCLEAN"));
            var inherited = Assert.Single(worker.Unsettled.Snapshot());
            Assert.Equal("prior-write", inherited.RequestId);
            Assert.True(inherited.FromPreviousOwner);
            var saveTool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(row => row.Name == "SaveProject"), worker),
                release, () => new(false, 1), readinessForTest: () => new JsonObject { ["ready"] = true });
            var saveRequest = new RequestContext<CallToolRequestParams>(System.Reflection.DispatchProxy.Create<IMcpServer, ServerProxy>()) {
                Params = new() { Name = "SaveProject", Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"dryRun\":true}") } };
            var saved = (await saveTool.InvokeAsync(saveRequest)).StructuredContent!;
            Assert.True((bool?)saved["ok"], saved.ToJsonString());
            Assert.Contains(saved["meta"]!["warnings"]!.AsArray(), w => (string?)w?["code"] == "UNSETTLED_CALLS");
            await worker.Call("Disconnect", new(), CancellationToken.None);
            Assert.Equal("RELEASED prior-write\n", File.ReadAllText(Path.Combine(root, "123-1.lease")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT", previous);
            Directory.Delete(root, true);
        }
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"),
        InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task No_field_requires_a_tia_restart(string release)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
        TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(AppContext.BaseDirectory,
            "TiaMcp.Adapter." + release + ".dll"), "offline session fixture");
        using var worker = new WorkerClient(release, exe, AppContext.BaseDirectory, true) { Bundled = false };
        void Check(JsonNode? reply)
        {
            string json = reply!.ToJsonString();
            foreach (string field in new[] { "requiresTiaRestart", "recoveryMessage", "priorUnknownRequestId", "requiresExplicitRebind" })
                Assert.DoesNotContain(field, json, StringComparison.OrdinalIgnoreCase);
        }
        Check(await worker.Call("Disconnect", new(), CancellationToken.None));
        Check(await worker.RestartHostWorker(false, CancellationToken.None));
        Check(await worker.RestartHostWorker(true, CancellationToken.None));
        await worker.Call("Attach", new() { ["processId"] = 123 }, CancellationToken.None);
        Check(await worker.Call("Disconnect", new(), CancellationToken.None));
        await worker.Call("Attach", new() { ["processId"] = 123 }, CancellationToken.None);
        await Assert.ThrowsAsync<WorkerOperationException>(() => worker.Call("FixtureUnknown", new(), CancellationToken.None));
        Check(await worker.Call("Disconnect", new(), CancellationToken.None));
        Check(await worker.RestartHostWorker(true, CancellationToken.None));
        var snapshot = worker.SupervisorSnapshot(new());
        if (release is "20" or "21")
        {
            var generations = Assert.IsType<JsonArray>(snapshot["previousGenerations"]);
            Assert.Equal(3, generations.Count);
            foreach (var item in generations)
                Assert.Equal(new[] { "generation", "endedUtc", "reason" }, item!.AsObject().Select(pair => pair.Key));
        }
        else Assert.False(snapshot.ContainsKey("previousGenerations"));
    }

    [Theory, InlineData("14sp1"), InlineData("19"), InlineData("21")]
    public Task Idle_worker_after_a_channel_fault_with_an_idle_lease_needs_no_TIA_restart(string release)
        => LeaseRestart(release, false);

    [Theory, InlineData("14sp1"), InlineData("19"), InlineData("21")]
    public Task Uncertain_lease_does_not_require_a_TIA_restart(string release) => LeaseRestart(release, true);

    private static async Task LeaseRestart(string release, bool uncertain)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "lease-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? previous = Environment.GetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT");
        Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT", root);
        try
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
            TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(
                Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter." + release + ".dll"), "offline session fixture");
            using var worker = new WorkerClient(release, exe, AppContext.BaseDirectory, true) {
                Bundled = false };
            await worker.Call("Attach", new JsonObject { ["processId"] = 123 }, CancellationToken.None);
            if (uncertain) await worker.Call("FixtureMarkUncertain", new(), CancellationToken.None);
            await Assert.ThrowsAsync<TiaMcp.WorkerChannel.ChannelFaultException>(() =>
                worker.Call("FixtureEpochAdvance", new(), CancellationToken.None));
            var reply = await worker.Call("Disconnect", new(), CancellationToken.None);
            Assert.Null(reply?["RequiresTiaRestart"]);
            Assert.False((bool?)reply?["WorkerAcknowledged"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT", previous);
            Directory.Delete(root, true);
        }
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"),
        InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public Task Live_holder_is_still_refused(string release)
        => Foundation_channel_preserves_the_live_holder_refusal_without_poisoning(release);

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19")]
    public async Task Foundation_channel_preserves_the_live_holder_refusal_without_poisoning(string release)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
        TiaMcp.Engine.Tests.Shared.TestPaths
            .WriteFixtureFile(Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter." + release + ".dll"), "offline session fixture");
        using var worker = new WorkerClient(release, exe, AppContext.BaseDirectory, true) { Bundled = false };
        foreach (var item in new[] { (904, TiaOpenness.Shared.SessionBehavior.LeaseReserved) })
        {
            var failure = await Assert
                .ThrowsAsync<WorkerOperationException>(() => worker.Call("Attach", new JsonObject { ["processId"] = item.Item1 }, CancellationToken.None));
            Assert.Equal(item.Item2, FoundationV4Result.WorkerRejection(failure).Message);
            Assert.Equal("rejected-before-operation", failure.Outcome);
            Assert.False(worker.Faulted);
        }
        var hidden = await Assert
            .ThrowsAsync<WorkerOperationException>(() => worker.Call("Attach", new JsonObject { ["processId"] = 902 }, CancellationToken.None));
        Assert.DoesNotContain("Internal secret", FoundationV4Result.WorkerRejection(hidden).Message);
        await worker.Call("ReadState", new JsonObject(), CancellationToken.None);
        await worker.Call("Disconnect", new JsonObject(), CancellationToken.None);
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"),
            InlineData("21")]
    public async Task Answered_unknown_can_detach_and_a_new_foundation_worker_can_attach(string release)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
        TiaMcp.Engine.Tests.Shared.TestPaths
            .WriteFixtureFile(Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter." + release + ".dll"), "offline session fixture");
        using var worker = new WorkerClient(release, exe, AppContext.BaseDirectory, true) { Bundled = false };
        await worker.Call("Attach", new JsonObject { ["processId"] = 123 }, CancellationToken.None);
        using (TiaMcpServer.ModelContextProtocol.InvocationJournal.UseCorrelation("prior-foundation-unknown"))
            await Assert.ThrowsAsync<WorkerOperationException>(() => worker.Call("FixtureUnknown", new JsonObject(), CancellationToken.None));
        Assert.False(worker.Faulted);
        var state = await worker.Call("ReadState", new JsonObject(), CancellationToken.None);
        Assert.Equal(release, (string?)state?["ReleaseKey"]);
        var ack = await worker.Call("Disconnect", new JsonObject(), CancellationToken.None);
        Assert.True((bool?)ack?["WorkerAcknowledged"]); Assert.True((bool?)ack?["Detached"]);
        Assert.Null(ack?["RequiresTiaRestart"]);
        Assert.Null(ack?["PriorUnknownRequestId"]);
        Assert.True(JsonNode.DeepEquals(ack, await worker.Call("Disconnect", new JsonObject(), CancellationToken.None)));
        int? previousPid = (int?)worker.OperatorSnapshot()["pid"];
        await worker.Call("Attach", new JsonObject { ["processId"] = 123 }, CancellationToken.None);
        Assert.NotEqual(previousPid, (int?)worker.OperatorSnapshot()["pid"]);
        Assert.Equal(2, (long?)worker.OperatorSnapshot()["generation"]);
        await worker.Call("Disconnect", new JsonObject(), CancellationToken.None);
    }

    [Theory, InlineData("14sp1"), InlineData("19")]
    public async Task Foundation_in_flight_disconnect_does_not_wait_for_a_hung_native_call(string release)
    {
        string gate = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
            "bin-build", "disconnect-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(gate);
        string? previousGate = Environment.GetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE");
        Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE", gate);
        try
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
            TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter." + release + ".dll"),
                "offline session fixture");
            using var worker = new WorkerClient(release, exe, AppContext.BaseDirectory, true) { Bundled = false };
            await worker.Call("Attach", new JsonObject { ["processId"] = 123 }, CancellationToken.None);
            var running = worker.Call("FixtureHang", new JsonObject(), CancellationToken.None);
            Assert.True(SpinWait.SpinUntil(() => File.Exists(Path.Combine(gate, "dispatched")),
                TiaMcp.Engine.Tests.Shared.TestDeadlines.Hang));
            int? pid = (int?)worker.OperatorSnapshot()["pid"];
            var refused = await Assert.ThrowsAsync<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() =>
                worker.Call("Disconnect", new JsonObject(), CancellationToken.None));
            Assert.Equal("worker-call-running", refused.Condition);
            Assert.Equal(pid, (int?)worker.OperatorSnapshot()["pid"]);
            Assert.False(System.Diagnostics.Process.GetProcessById(pid!.Value).HasExited);
            File.WriteAllText(Path.Combine(gate, "release"), "");
            Assert.NotNull(await running.WaitAsync(TiaMcp.Engine.Tests.Shared.TestDeadlines.Hang));
            var ack = await worker.Call("Disconnect", new JsonObject(), CancellationToken.None);
            Assert.Null(ack?["RequiresTiaRestart"]);
            Assert.True((bool?)ack?["Detached"]);
            Assert.True((bool?)ack?["WorkerAcknowledged"]);
        }
        finally
        {
            File.WriteAllText(Path.Combine(gate, "release"), "");
            Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE", previousGate);
        }
    }
}
