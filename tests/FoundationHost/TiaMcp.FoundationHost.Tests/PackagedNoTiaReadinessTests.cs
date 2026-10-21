extern alias foundationhost;
extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaOpenness.Shared;
using Pipeline = enginehost::TiaMcp.FoundationHost.EngineHostPipeline;
using Xunit;

// P8-04j2: a packaged V20/V21 host now runs its bundled PlcWorker. Without TIA the PlcWorker cannot start (it needs the release's
// Openness assemblies), while the engine started and reported "not ready". These cases use a real bundled WorkerClient that cannot
// start (no worker file, no PublicAPI directory) and the no-TIA environment answer, and require the engine's replies:
// readiness for a write preview, native admission for a session read, a fault-free status read and capability before readiness.
[Collection(BundleSelectionCollection.Name)]
public sealed class PackagedNoTiaReadinessTests
{
    [Theory, InlineData("20"), InlineData("21")]
    public async Task Write_preview_stops_at_readiness_and_does_not_start_or_fault_the_worker(string release)
    {
        using var host = new Host(release);
        var reply = await host.Call("CreatePlcTagTable", new JsonObject { ["plc"] = "PLC_1", ["group"] = "", ["name"] = "ExampleTags", ["dryRun"] = true, ["confirm"] = false });
        Assert.True(reply["ok"]?.GetValue<bool>() == false, reply.ToJsonString());
        Assert.Equal("RESOURCE_UNAVAILABLE", (string?)reply["error"]?["code"]);
        Assert.Equal("tia-openness-environment", (string?)reply["error"]?["details"]?["resource"]);
        Assert.Contains("TIA Portal V" + release + " or its Openness API files were not found.", (string?)reply["error"]?["message"]);
        Assert.Equal("rejected-before-operation", (string?)reply["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)reply["meta"]?["execution"]);
        Assert.False(host.Worker.WorkerFaulted);
    }

    [Theory, InlineData("20"), InlineData("21")]
    public async Task Session_read_stops_at_native_admission_like_the_engine(string release)
    {
        using var host = new Host(release);
        var reply = await host.Call("GetSessionState", new JsonObject());
        Assert.True(reply["ok"]?.GetValue<bool>() == false, reply.ToJsonString());
        Assert.Equal("PRECONDITION_FAILED", (string?)reply["error"]?["code"]);
        Assert.Equal("native-admission", (string?)reply["error"]?["details"]?["condition"]);
        Assert.Null(reply["data"]);
        Assert.Equal("rejected-before-operation", (string?)reply["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)reply["meta"]?["execution"]);
        Assert.False(reply["meta"]?["requiresSessionReset"]?.GetValue<bool>());
    }

    [Theory, InlineData("20"), InlineData("21")]
    public async Task Status_read_reports_the_environment_without_faulting_the_worker(string release)
    {
        using var host = new Host(release);
        var status = await host.Call("GetOpennessWorkerStatus", new JsonObject());
        Assert.True(status["ok"]?.GetValue<bool>() == true, status.ToJsonString());
        var worker = status["data"]?["evidence"]?["worker"];
        Assert.NotEqual("Faulted", (string?)worker?["state"]);
        Assert.False(worker?["sessionLocked"]?.GetValue<bool>());
        Assert.False(worker?["readiness"]?["ready"]?.GetValue<bool>());
        Assert.False(host.Worker.WorkerFaulted);
        // A later read keeps the admission answer instead of a session reset.
        var state = await host.Call("GetSessionState", new JsonObject());
        Assert.Equal("PRECONDITION_FAILED", (string?)state["error"]?["code"]);
    }

    [Theory, InlineData("20"), InlineData("21")]
    public async Task Bootstrap_reports_the_no_tia_cause(string release)
    {
        using var host = new Host(release);
        var reply = await host.Call("InitializeEnvironment", new JsonObject());
        Assert.True(reply["ok"]?.GetValue<bool>() == true, reply.ToJsonString());
        Assert.False(reply["data"]?["ready"]?.GetValue<bool>());
        Assert.Contains("TIA Portal V" + release + " or its Openness API files were not found.", (string?)reply["data"]?["recommendedReason"]);
        Assert.False(host.Worker.WorkerFaulted);
    }

    [Theory, InlineData("20"), InlineData("21")]
    public async Task Bridged_capability_refusal_precedes_readiness_like_the_engine(string release)
    {
        using var host = new Host(release);
        foreach (var action in new[] { "create", "update", "delete" })
        {
            var reply = await host.Call("CallTool", new JsonObject { ["name"] = "ManageUnifiedListEntries", ["arguments"] = new JsonObject {
                ["softwarePath"] = "HMI_RT_1", ["category"] = "textLists", ["listName"] = "Fixture", ["action"] = action, ["dryRun"] = true } });
            Assert.True(reply["ok"]?.GetValue<bool>() == false, reply.ToJsonString());
            Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)reply["error"]?["code"]);
            Assert.Equal("rejected-before-operation", (string?)reply["meta"]?["outcome"]);
            Assert.Equal("not-started", (string?)reply["meta"]?["execution"]);
        }
        // A bridged native target without a capability refusal keeps the readiness refusal.
        var save = await host.Call("CallTool", new JsonObject { ["name"] = "SaveProject", ["arguments"] = new JsonObject() });
        Assert.True((string?)save["error"]?["code"] == "RESOURCE_UNAVAILABLE", save.ToJsonString());
    }

    [Theory, InlineData("20"), InlineData("21")]
    public void Explicit_public_api_is_not_a_tia_installation_for_the_engine_releases(string release)
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
    public void Old_releases_keep_their_explicit_public_api_installation(string release)
    {
        using var files = new ApiFiles(release);
        var options = foundationhost::TiaMcp.FoundationHost.HostOptions.Parse(["--bundle-root", files.Bundle, "--release-key", release, "--public-api", files.Api, "--offline"], files.Bundle);
        Assert.Equal((files.Api, "host-option"), foundationhost::TiaMcp.FoundationHost.SharedReleaseHost.ReadinessInstallation(options));
    }

    private sealed class Host : IDisposable
    {
        private readonly string previous = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey;
        private readonly string scratch = Path.Combine(Path.GetTempPath(), "packaged-no-tia-" + Guid.NewGuid().ToString("N"));
        private readonly Pipeline pipeline;
        internal readonly WorkerClient Worker;
        internal Host(string release)
        {
            // The packaged layout's bundled worker path, with neither the worker nor any PublicAPI directory present.
            Worker = new WorkerClient(release, Path.Combine(scratch, "runtime", "v" + release, "worker", "TiaMcp.PlcWorker." + release + ".exe"), "", true)
            { Bundled = true, SharedSession = true };
            // The host's no-TIA answer: no installation found (the engine's "not found" cause).
            Func<JsonObject> environment = () => FoundationPassiveDiagnostics.Readiness(release, _ => null, () => false);
            Func<JsonObject> readiness = () => Worker.Readiness(environment, environment);
            IReadOnlyList<McpServerTool>? roster = null;
            var shared = LegacyHostToolRegistry.Create(Worker, release, true, readinessProvider: readiness, usageRoster: () => roster!, hostPorts: false);
            var services = FoundationHostPorts.CreateServices(Worker, release, readiness, "");
            pipeline = new Pipeline("", Path.Combine(scratch, "worker.exe"), release, services, "full", shared, null);
            services.Roster = () => roster!;
            roster = pipeline.AllTools;
            Worker.SessionLocked = () => pipeline.SessionLocked;
        }
        internal async Task<JsonObject> Call(string name, JsonObject arguments)
        {
            var tool = pipeline.AllTools.Single(t => t.ProtocolTool.Name == name);
            var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>())
            { Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } };
            // As the MCP server's serialized-call wrapper does: a bridged target is invoked on the current call's server.
            using var call = enginehost::TiaMcpServer.ModelContextProtocol.McpServer.UseProgressRequest(request, CancellationToken.None);
            var result = await tool.InvokeAsync(request);
            return result.StructuredContent?.AsObject() ?? JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)!.AsObject();
        }
        public void Dispose()
        {
            pipeline.Dispose(); Worker.Dispose();
            enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = previous;
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
