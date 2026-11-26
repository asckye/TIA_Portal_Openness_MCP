extern alias foundationhost;
extern alias toolpipeline;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaOpenness.Shared;
using Pipeline = toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline;
using ReadinessGuard = toolpipeline::TiaMcpServer.Dispatch.OpennessReadinessGuard;
using Server = toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer;
using Xunit;

// The packaged worker cannot start without its Openness assemblies. Exercise the public pipeline with a missing
// installation and assert native admission, capability precedence and a fault-free, cold worker on every release.
[Collection(BundleSelectionCollection.Name)]
public sealed class PackagedNoTiaReadinessTests
{
    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17")]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Worker_restart_refuses_before_requesting_Workbench_approval(string release)
    {
        var realPath = ApprovalSettings.SettingsPath;
        using var host = new Host(release);
        using var data = new IsolatedData(host.Scratch);
        var previousWait = Server.ApprovalWaitOverrideForTests;
        int requests = 0;
        int? requestedTimeout = null;
        try
        {
            Assert.Equal(Path.Combine(host.Scratch, "config", "approval.settings"), ApprovalSettings.SettingsPath);
            Assert.NotEqual(realPath, ApprovalSettings.SettingsPath);
            const int isolatedTimeout = 137;
            new ApprovalSettings(true, 1).Save(ApprovalSettings.SettingsPath);
            Assert.Equal(1, ApprovalSettings.Load(ApprovalSettings.SettingsPath).TimeoutSeconds);
            Server.ApprovalWaitOverrideForTests = (card, _, _) => {
                requests++;
                requestedTimeout = card.TimeoutSeconds;
                return Task.FromResult(new ApprovalOutcome(card, false, "workbench-unavailable"));
            };
            const string name = "RestartOpennessWorker";
            foreach (var target in new[] { new JsonObject(), new JsonObject { ["confirmRestart"] = true } })
            foreach (var route in new[] { name, "CallTool" })
            {
                var arguments = route == name ? target : new JsonObject { ["name"] = name, ["arguments"] = target };
                var reply = await host.Call(route, arguments);
                Assert.Equal(0, requests);
                Assert.Equal("RESOURCE_UNAVAILABLE", (string?)reply["error"]?["code"]);
                Assert.Equal("tia-openness-environment", (string?)reply["error"]?["details"]?["resource"]);
                var environment = reply["data"]!["environment"]!;
                Assert.False((bool?)environment["ready"]);
                Assert.Contains(TiaMcp.Versioning.TiaVersionCatalog.Get(release).DisplayName, (string?)environment["cause"]);
                Assert.False(string.IsNullOrWhiteSpace((string?)environment["recommendedFix"]));
                Assert.Equal("rejected-before-operation", (string?)reply["meta"]?["outcome"]);
                Assert.Equal("not-started", (string?)reply["meta"]?["execution"]);
                Assert.Null(host.Worker.OperatorSnapshot()["pid"]);
                Assert.False(host.Worker.WorkerFaulted);
            }
            using var files = new ApiFiles(release);
            using var ready = new Host(release, installedApi: files.InstalledApi);
            new ApprovalSettings(true, isolatedTimeout).Save(ApprovalSettings.SettingsPath);
            var confirmation = await ready.Call(name, new JsonObject { ["confirmRestart"] = true });
            Assert.Equal(1, requests);
            Assert.Equal(isolatedTimeout, requestedTimeout);
            Assert.Equal("CONFIRMATION_REQUIRED", (string?)confirmation["error"]?["code"]);
            Assert.Equal("workbench-unavailable", (string?)confirmation["error"]?["details"]?["reason"]);
        }
        finally { Server.ApprovalWaitOverrideForTests = previousWait; }
    }

    [Fact]
    public void Standalone_protocol_simulator_keeps_its_own_admission()
    {
        using var files = new ApiFiles("20");
        var simulator = Path.Combine(files.Bundle, "TiaMcp.PlcWorker.20.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe"), simulator);
        using var client = new WorkerClient("20", simulator, files.Api, true) { Bundled = false };
        Assert.False(client.IsReleasePlcWorker());
        using var host = new Host("20", simulator);
        using var session = host.EnterSession();
        foreach (var name in HostedWorkerTools)
            Assert.False(ReadinessGuard.RequiresOpenness(name), name);
    }

    // Independent audit of host implementations with a worker path, including conditional branches.
    private static readonly string[] HostedWorkerTools = {
        "RestartOpennessWorker", "RunPlcTestLibrary", "RunOnlineMonitoringSafetySelfTest", "GenerateAcceptanceReport",
        "RunOfflineReleaseValidationSuite", "ComparePlcBlockDocuments", "RenderPlcBlockDocument", "PreviewToolBatch", "ApplyToolBatch", "RunReadOnlyToolBatch"
    };

    public static IEnumerable<object[]> WorkerCases() => ReleaseRange.All.SelectMany(release =>
        HostedWorkerTools.Where(name => name != "RestartOpennessWorker").Select(name => new object[] { release, name }));

    [Theory, MemberData(nameof(WorkerCases))]
    public async Task Hosted_worker_paths_refuse_before_approval(string release, string name)
    {
        using var host = new Host(release);
        using var data = new IsolatedData(host.Scratch);
        using var session = host.EnterSession();
        var arguments = name switch {
            "RunPlcTestLibrary" => new JsonObject { ["dryRun"] = false, ["expectedPlanHash"] = new string('a', 64) },
            "RunOfflineReleaseValidationSuite" => new JsonObject { ["workspaceRoot"] = host.Scratch, ["reportDirectory"] = host.Scratch },
            "ComparePlcBlockDocuments" => new JsonObject { ["softwarePath"] = "PLC_1", ["leftBlockPath"] = "Main", ["rightBlockPath"] = "Other" },
            "RenderPlcBlockDocument" => new JsonObject { ["softwarePath"] = "PLC_1", ["blockPath"] = "Main" },
            "RunReadOnlyToolBatch" => JsonSerializer.SerializeToNode(new { expectedProject = "Example", operations = new[] {
                new { name = "GetSessionState", arguments = new { } } } })!.AsObject(),
            "PreviewToolBatch" => JsonSerializer.SerializeToNode(new { expectedProject = "Example", operations = new[] {
                new { name = "ManagePlcExternalSources", arguments = new { softwarePath = "PLC_1", action = "createGroup", name = "ExampleSources",
                    dryRun = true, confirm = true } } } })!.AsObject(),
            "ApplyToolBatch" => new JsonObject { ["previewToken"] = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.Current.BatchPlans.Add(
                new TiaMcpServer.ModelContextProtocol.BatchPlanStore.Plan { Project = "Example" }, DateTime.UtcNow) },
            _ => new JsonObject()
        };
        var previousWait = Server.ApprovalWaitOverrideForTests;
        int requests = 0;
        try
        {
            Server.ApprovalWaitOverrideForTests = (card, _, _) => {
                requests++;
                return Task.FromResult(new ApprovalOutcome(card, false, "workbench-unavailable"));
            };
            foreach (var route in new[] { name, "CallTool" })
            {
                var reply = await host.Call(route, route == name ? arguments : new JsonObject { ["name"] = name, ["arguments"] = arguments });
                Assert.Equal("RESOURCE_UNAVAILABLE", (string?)reply["error"]?["code"]);
                Assert.Equal("tia-openness-environment", (string?)reply["error"]?["details"]?["resource"]);
                Assert.Equal("rejected-before-operation", (string?)reply["meta"]?["outcome"]);
                Assert.Equal("not-started", (string?)reply["meta"]?["execution"]);
                Assert.Equal(0, requests);
                Assert.Null(host.Worker.OperatorSnapshot()["pid"]);
                Assert.False(host.Worker.WorkerFaulted);
            }
        }
        finally { Server.ApprovalWaitOverrideForTests = previousWait; }
    }

    [Theory, InlineData("14sp1"), InlineData("21")]
    public async Task Library_previews_and_file_only_block_operations_run_on_a_cold_host(string release)
    {
        using var host = new Host(release);
        using var data = new IsolatedData(host.Scratch);
        var store = new TiaMcp.Logic.Testing.PlcTestLibrary(DataLocations.Current.TestLibraryDirectory);
        store.Save(TiaMcp.Logic.Testing.PlcTestCaseDocument.Parse("""
            {"schemaVersion":1,"id":"ready","title":"Ready","tags":[],"kind":"plcsimScenario",
             "scenario":{"instance":"PLC_1","mode":"default","steps":[{"assert":{"Ready":true}}]}}
            """));
        var file = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SimaticMl", "17", "MCP_RC_FC.xml");
        foreach (var (name, args) in new (string, JsonObject)[] {
            ("ManagePlcTestLibrary", new() { ["action"] = "read", ["id"] = "ready" }),
            ("RunPlcTestLibrary", new() { ["ids"] = new JsonArray("ready") }),
            ("RenderPlcBlockDocument", new() { ["filePath"] = file }),
            ("ComparePlcBlockDocuments", new() { ["leftFilePath"] = file, ["rightFilePath"] = file })
        })
        foreach (var route in new[] { name, "CallTool" })
        {
            var reply = await host.Call(route, route == name ? args : new JsonObject { ["name"] = name, ["arguments"] = args });
            Assert.True((bool?)reply["ok"] == true, reply.ToJsonString());
            Assert.Null(host.Worker.OperatorSnapshot()["pid"]);
            Assert.False(host.Worker.WorkerFaulted);
        }
    }

    [Theory, InlineData("20"), InlineData("21")]
    public void Shared_tool_names_have_the_same_no_Tia_admission_as_the_engine(string release)
    {
        var current = ReadinessByTool(release);
        foreach (var older in new[] { "14sp1", "15.1", "16", "17", "18", "19" })
        {
            var engine = ReadinessByTool(older);
            var common = current.Keys.Intersect(engine.Keys).Order(StringComparer.Ordinal).ToArray();
            var refused = common.Where(name => current[name]).ToArray();
            Assert.Contains("RestartOpennessWorker", refused);
            Assert.Equal(common.Where(name => engine[name]), refused);
        }
    }

    private static Dictionary<string, bool> ReadinessByTool(string release)
    {
        using var host = new Host(release);
        using var session = host.EnterSession();
        // Exercise the installation check for delegated targets without running their operations.
        var environment = FoundationPassiveDiagnostics.Readiness(release, _ => null, () => false);
        var native = FoundationTools.Create(host.Worker, release).Concat(PortedToolContract.Create(host.Worker, release))
            .Append(PlcCrossReferenceAnalysis.Create(host.Worker, release))
            .Select(tool => new FoundationV4Tool(tool, release, readinessForTest: () => environment))
            .Where(tool => (string?)tool.ReadinessRefusal("parity")?.StructuredContent?["error"]?["details"]?["resource"] == "tia-openness-environment")
            .Select(tool => tool.ProtocolTool.Name).ToHashSet(StringComparer.Ordinal);
        // Runtime and VCI host endpoints share the guard with the worker-backed host tools.
        // Inspect their public admission independently of the production taxonomy and family table.
        string[] guardedHostEndpoints = {
            "ManagePlcTestLibrary", "GetPlcLiveValuesOpcUa", "GetPlcLiveValuesS7", "GetPlcRunStateS7", "GetPlcWebDiagnostics", "GetPlcWebVars",
            "GetUnifiedRuntimeAlarms", "GetUnifiedRuntimeTags", "InvokeUnifiedOpenPipe", "ManagePlcGitRepository", "ProbeS7CpuIdentity",
            "RunPlcCompanionTool", "SamplePlcLiveValuesS7", "SetPlcWebOperatingMode", "WritePlcWebVars", "WriteUnifiedRuntimeTags"
        };
        foreach (var tool in Server.CatalogView.All.Values.Where(tool => tool.Execution != "foundation"))
        {
            bool expected = tool.Execution == "worker" ? tool.Name != "InitializeEnvironment"
                : HostedWorkerTools.Contains(tool.Name) || guardedHostEndpoints.Contains(tool.Name);
            Assert.True(expected == ReadinessGuard.RequiresOpenness(tool.Name), $"{release}/{tool.Name}: expected requires Openness = {expected}");
        }
        return Server.CatalogView.All.Values.ToDictionary(tool => tool.Name, tool => ReadinessGuard.RequiresOpenness(tool.Name)
            || tool.Execution == "foundation" && native.Contains(tool.Name));
    }

    [Theory, InlineData("20"), InlineData("21")]
    public async Task Explicit_plc_worker_refuses_native_apply_before_approval(string release)
    {
        using var files = new ApiFiles(release);
        var worker = Path.Combine(files.Bundle, "explicit-worker.exe");
        var assembly = new PersistedAssemblyBuilder(new AssemblyName("TiaMcp.PlcWorker." + release), typeof(object).Assembly);
        assembly.DefineDynamicModule("worker");
        assembly.Save(worker);
        using var host = new Host(release, worker);
        const string name = "SetDeviceItemAttribute";
        var arguments = new JsonObject {
            ["deviceItemPath"] = "PLC_1/CPU_1", ["attributeName"] = "Name", ["value"] = "ApprovalProbe",
            ["dryRun"] = false, ["expectedPlanHash"] = new string('a', 64), ["expectedProjectFile"] = "C:/Projects/ApprovalProbe.ap21"
        };
        foreach (var route in new[] { name, "CallTool" })
        {
            var input = route == name ? arguments : new JsonObject { ["name"] = name, ["arguments"] = arguments };
            var reply = await host.Call(route, input);
            Assert.Equal("RESOURCE_UNAVAILABLE", (string?)reply["error"]?["code"]);
            Assert.Equal("tia-openness-environment", (string?)reply["error"]?["details"]?["resource"]);
            Assert.Equal("rejected-before-operation", (string?)reply["meta"]?["outcome"]);
            Assert.Equal("not-started", (string?)reply["meta"]?["execution"]);
        }
        Assert.False(host.Worker.WorkerFaulted);
        Assert.Null(host.Worker.OperatorSnapshot()["pid"]);
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17")]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Write_preview_stops_at_readiness_and_does_not_start_or_fault_the_worker(string release)
    {
        using var host = new Host(release);
        var reply = await host.Call("CreatePlcTagTable", new JsonObject { ["plc"] = "PLC_1", ["group"] = "", ["name"] = "ExampleTags", ["dryRun"] = true, ["confirm"] = false });
        Assert.True(reply["ok"]?.GetValue<bool>() == false, reply.ToJsonString());
        Assert.Equal("RESOURCE_UNAVAILABLE", (string?)reply["error"]?["code"]);
        Assert.Equal("tia-openness-environment", (string?)reply["error"]?["details"]?["resource"]);
        Assert.Contains("TIA Portal " + TiaMcp.Versioning.TiaVersionCatalog.Get(release).DisplayName + " or its Openness API files were not found.",
            (string?)reply["error"]?["message"]);
        Assert.Equal("rejected-before-operation", (string?)reply["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)reply["meta"]?["execution"]);
        Assert.False(host.Worker.WorkerFaulted);
        Assert.Null(host.Worker.OperatorSnapshot()["pid"]);
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17")]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Session_read_stops_at_native_admission(string release)
    {
        using var host = new Host(release);
        var reply = await host.Call("GetSessionState", new JsonObject());
        Assert.True(reply["ok"]?.GetValue<bool>() == false, reply.ToJsonString());
        Assert.Equal("PRECONDITION_FAILED", (string?)reply["error"]?["code"]);
        Assert.Equal("native-admission", (string?)reply["error"]?["details"]?["condition"]);
        Assert.Null(reply["data"]);
        Assert.Equal("rejected-before-operation", (string?)reply["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)reply["meta"]?["execution"]);
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17")]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Status_read_reports_the_environment_without_faulting_the_worker(string release)
    {
        using var host = new Host(release);
        var status = await host.Call("GetOpennessWorkerStatus", new JsonObject());
        Assert.True(status["ok"]?.GetValue<bool>() == true, status.ToJsonString());
        var worker = status["data"]?["evidence"]?["worker"];
        Assert.NotEqual("Faulted", (string?)worker?["state"]);
        Assert.Null(worker?["sessionLocked"]);
        Assert.False(worker?["readiness"]?["ready"]?.GetValue<bool>());
        Assert.False(host.Worker.WorkerFaulted);
        Assert.Null(host.Worker.OperatorSnapshot()["pid"]);
        // A later read keeps the admission answer instead of a session reset.
        var state = await host.Call("GetSessionState", new JsonObject());
        Assert.Equal("PRECONDITION_FAILED", (string?)state["error"]?["code"]);
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17")]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Bootstrap_reports_the_no_tia_cause(string release)
    {
        using var host = new Host(release);
        var reply = await host.Call("InitializeEnvironment", new JsonObject());
        Assert.True(reply["ok"]?.GetValue<bool>() == true, reply.ToJsonString());
        Assert.False(reply["data"]?["ready"]?.GetValue<bool>());
        Assert.Contains("TIA Portal " + TiaMcp.Versioning.TiaVersionCatalog.Get(release).DisplayName + " or its Openness API files were not found.",
            (string?)reply["data"]?["recommendedReason"]);
        Assert.False(host.Worker.WorkerFaulted);
        Assert.Null(host.Worker.OperatorSnapshot()["pid"]);
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17")]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Bridged_capability_refusal_precedes_readiness_like_the_engine(string release)
    {
        using var host = new Host(release);
        // Unified list entries start at V20. Earlier releases expose PLC user groups and refuse these rename actions.
        bool hasLists = TiaMcp.Adapters.Contracts.PortedFamilies.Available(release, "ManageUnifiedListEntries");
        foreach (var example in hasLists ? new[] { "create", "update", "delete" } : new[] { "blocks", "types", "externalSources" })
        {
            var target = hasLists ? "ManageUnifiedListEntries" : "ManagePlcUserGroup";
            var arguments = hasLists
                ? new JsonObject { ["softwarePath"] = "HMI_RT_1", ["category"] = "textLists", ["listName"] = "Fixture",
                    ["action"] = example, ["dryRun"] = true }
                : new JsonObject { ["softwarePath"] = "PLC_1", ["family"] = example, ["groupPath"] = "Group",
                    ["action"] = "rename", ["newName"] = "Renamed", ["dryRun"] = true };
            var reply = await host.Call("CallTool", new JsonObject { ["name"] = target, ["arguments"] = arguments });
            Assert.True(reply["ok"]?.GetValue<bool>() == false, reply.ToJsonString());
            Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)reply["error"]?["code"]);
            Assert.Equal("rejected-before-operation", (string?)reply["meta"]?["outcome"]);
            Assert.Equal("not-started", (string?)reply["meta"]?["execution"]);
        }
        // A bridged native target without a capability refusal keeps the readiness refusal.
        var save = await host.Call("CallTool", new JsonObject { ["name"] = "SaveProject", ["arguments"] = new JsonObject() });
        Assert.True((string?)save["error"]?["code"] == "RESOURCE_UNAVAILABLE", save.ToJsonString());
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17")]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Explicit_public_api_is_not_a_tia_installation(string release)
    {
        using var files = new ApiFiles(release);
        var options = foundationhost::TiaMcp.FoundationHost.HostOptions.Parse(["--bundle-root", files.Bundle, "--release-key", release, "--public-api", files.Api, "--offline"], files.Bundle);
        Assert.True(options.PublicApiOption);
        Assert.Equal(files.Api, options.ApiDirectory);
        Assert.Equal(((string?)null, (string?)null), foundationhost::TiaMcp.FoundationHost.SharedReleaseHost.ReadinessInstallation(options));
        options = foundationhost::TiaMcp.FoundationHost.HostOptions.Parse(["--bundle-root", files.Bundle, "--release-key", release, "--public-api", files.Api, "--tia-portal-location", files.Installation, "--offline"], files.Bundle);
        Assert.Equal((files.InstalledApi, "host-option"), foundationhost::TiaMcp.FoundationHost.SharedReleaseHost.ReadinessInstallation(options));
        // Without --public-api the API directory found from the TIA location stays the installation, as before.
        options = foundationhost::TiaMcp.FoundationHost.HostOptions.Parse(["--bundle-root", files.Bundle, "--release-key", release, "--tia-portal-location", files.Installation, "--offline"], files.Bundle);
        Assert.False(options.PublicApiOption);
        Assert.Equal((files.InstalledApi, "host-option"), foundationhost::TiaMcp.FoundationHost.SharedReleaseHost.ReadinessInstallation(options));
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19")]
    public void Sdk_only_directory_does_not_supply_an_old_release_installation(string release)
    {
        using var files = new ApiFiles(release);
        var options = foundationhost::TiaMcp.FoundationHost.HostOptions.Parse(["--bundle-root", files.Bundle, "--release-key", release, "--public-api", files.Api, "--offline"], files.Bundle);
        Assert.Equal(((string?)null, (string?)null), foundationhost::TiaMcp.FoundationHost.SharedReleaseHost.ReadinessInstallation(options));
    }

    private sealed class IsolatedData : IDisposable
    {
        private readonly DataLocations? previous = DataLocations.OverrideForTests.Value;
        internal IsolatedData(string root) => DataLocations.OverrideForTests.Value = DataLocations.Resolve(root, root, root, root);
        public void Dispose() => DataLocations.OverrideForTests.Value = previous;
    }

    private sealed class Host : IDisposable
    {
        private readonly string previous = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey;
        private readonly string scratch = Path.Combine(Path.GetTempPath(), "packaged-no-tia-" + Guid.NewGuid().ToString("N"));
        internal string Scratch => scratch;
        private readonly Pipeline pipeline;
        internal readonly WorkerClient Worker;
        internal Host(string release, string? workerExe = null, string? installedApi = null)
        {
            // As EngineReleaseHost does before it builds the registry: the version policy that wraps the ported tools reads the
            // process release key. Without it a V21 case depended on the key an earlier test left (the beta.1 release run failed
            // the five V21 cases when a V20 test ran before them in the same shard).
            toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = release;
            var bundledPath = Path.Combine(scratch, "runtime", "v" + release, "worker", "TiaMcp.PlcWorker." + release + ".exe");
            Worker = new WorkerClient(release, workerExe ?? bundledPath, "", true) { Bundled = workerExe == null, SharedSession = true };
            Func<JsonObject> environment = () => FoundationPassiveDiagnostics.Readiness(release, _ => installedApi, () => installedApi != null);
            Func<JsonObject> readiness = () => Worker.Readiness(environment);
            IReadOnlyList<McpServerTool>? roster = null;
            var shared = LegacyHostToolRegistry.Create(Worker, release, true, readinessProvider: readiness, usageRoster: () => roster!, hostPorts: false);
            var services = FoundationHostPorts.CreateServices(Worker, release, readiness, "");
            pipeline = new Pipeline("", Path.Combine(scratch, "worker.exe"), release, services, "full", shared, null);
            services.Roster = () => roster!;
            roster = pipeline.AllTools;

        }
        internal IDisposable EnterSession() => pipeline.EnterSession();
        internal async Task<JsonObject> Call(string name, JsonObject arguments)
        {
            var tool = pipeline.Tools.Single(t => t.ProtocolTool.Name == name);
            var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>())
            { Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } };
            // As the MCP server's serialized-call wrapper does: a bridged target is invoked on the current call's server.
            using var call = toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer.UseProgressRequest(request, CancellationToken.None);
            var result = await tool.InvokeAsync(request);
            return result.StructuredContent?.AsObject() ?? JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)!.AsObject();
        }
        public void Dispose()
        {
            pipeline.Dispose(); Worker.Dispose();
            toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = previous;
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }
    }

    // A bundle root plus a PublicAPI folder that holds only this release's API assembly name (no TIA executable).
    private sealed class ApiFiles : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "packaged-api-" + Guid.NewGuid().ToString("N"));
        internal string Bundle => Path.Combine(root, "bundle");
        internal string Api => Path.Combine(root, "sdk-api");
        internal string Installation => Path.Combine(root, "installation");
        internal string InstalledApi { get; }
        internal ApiFiles(string release)
        {
            var version = TiaMcp.Versioning.TiaVersionCatalog.Get(release);
            Directory.CreateDirectory(Path.Combine(Bundle, "manifest"));
            File.WriteAllText(Path.Combine(Bundle, "manifest/package-manifest.json"), "fixture");
            Directory.CreateDirectory(Api);
            File.WriteAllText(Path.Combine(Api, version.ApiAssembly), "fixture");
            InstalledApi = Path.Combine(Installation, "PublicAPI", version.ApiFolder);
            Directory.CreateDirectory(InstalledApi);
            File.WriteAllText(Path.Combine(InstalledApi, version.ApiAssembly), "fixture");
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
