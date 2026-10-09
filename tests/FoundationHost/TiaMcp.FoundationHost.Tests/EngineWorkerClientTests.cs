extern alias foundationhost;
using Xunit;
using System.Text.Json.Nodes;
using System.Diagnostics;
using TiaMcp.FoundationHost;
using TiaOpenness.Shared;
using Client = foundationhost::TiaMcp.FoundationHost.EngineWorkerClient;
using Options = foundationhost::TiaMcp.FoundationHost.HostOptions;

public sealed class EngineWorkerClientTests
{
    private static Client Create() => new(new Options { ReleaseKey = "21", BundleRoot = AppContext.BaseDirectory,
        WorkerExe = "unused", ApiDirectory = "unused", EngineWorkerExe = Path.Combine(AppContext.BaseDirectory, "missing-fixture-worker.exe") }, "unused");

    [Fact]
    public async Task ManagedObservationsUseTheEngineChannelProfile()
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
        using var worker = new Client(new Options { ReleaseKey = "20", BundleRoot = AppContext.BaseDirectory,
            WorkerExe = "unused", ApiDirectory = "unused", EngineWorkerExe = executable },
            Client.Hash(executable));
        var result = await worker.Observe("assemblies", new System.Text.Json.Nodes.JsonObject(), CancellationToken.None);
        Assert.Empty(result!.AsArray());
        Assert.Equal("Ready", (string?)worker.Snapshot()["state"]);
    }

    [Fact]
    public async Task RestartPreviewAndBusyRefusalNeverAdvanceGeneration()
    {
        using var worker = Create();
        object key = worker.SessionKey;
        var preview = await worker.Restart(false, CancellationToken.None);
        Assert.True((bool?)preview["dryRun"]);
        Assert.Same(key, worker.SessionKey);
        using var active = await worker.Acquire(CancellationToken.None);
        var queued = worker.Acquire(CancellationToken.None);
        Assert.Equal(1, (int?)worker.Snapshot()["queued"]);
        var refused = await worker.Restart(true, CancellationToken.None);
        Assert.False((bool?)refused["success"]);
        Assert.Equal(1, (long?)worker.Snapshot()["generation"]);
        Assert.Same(key, worker.SessionKey);
        active.Dispose();
        using var drained = await queued;
    }

    [Fact]
    public async Task FailedExplicitRestartRetainsPriorLockAndNeverRestartsImplicitly()
    {
        using var worker = Create();
        object prior = worker.SessionKey;
        worker.SessionLocked = () => ReferenceEquals(prior, worker.SessionKey);
        await Assert.ThrowsAsync<FileNotFoundException>(() => worker.Restart(true, CancellationToken.None));
        Assert.NotSame(prior, worker.SessionKey);
        var snapshot = worker.Snapshot();
        Assert.Equal(2, (long?)snapshot["generation"]);
        Assert.True((bool?)snapshot["previousGenerations"]![0]!["sessionLocked"]);
        Assert.Null(snapshot["previousGenerations"]![0]!["previousGenerations"]);
        Assert.True(worker.Faulted);
        await worker.Status(CancellationToken.None);
        Assert.Equal(2, (long?)worker.Snapshot()["generation"]);
    }
    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Missing_portal_and_refused_connect_do_not_poison_either_client(string release)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter." + release + ".dll"), "offline session fixture");
        using var worker = new ClientBridge(release, exe);
        var args = new JsonObject { ["projectName"] = "Project1", ["expectedProjectFile"] = "C:/fixture.ap" + release };
        foreach (bool afterRefusedConnect in new[] { false, true })
        {
            if (afterRefusedConnect) await Assert.ThrowsAnyAsync<Exception>(() => worker.Call("Attach", new JsonObject { ["processId"] = 901 }, CancellationToken.None));
            var error = await Assert.ThrowsAsync<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() => worker.Call("BindProject", args, CancellationToken.None));
            var body = FoundationV4Result.Failure(release, "AttachOpenProject", "fixture", true, true, null, error).StructuredContent!;
            Assert.Contains("ConnectPortal", (string?)body["error"]?["message"]);
            Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
            Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
            Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
            Assert.False((bool?)body["meta"]?["requiresSessionReset"]);
            Assert.False((bool)error.Data["foundationRequestSent"]!);
            Assert.False(worker.Poisoned);
        }
        await worker.Call("ReadState", new JsonObject(), CancellationToken.None);
        await worker.Call("Disconnect", new JsonObject(), CancellationToken.None);
    }

    [Theory, InlineData("14sp1", false), InlineData("14sp1", true), InlineData("15.1", false), InlineData("15.1", true), InlineData("16", false), InlineData("16", true), InlineData("17", false), InlineData("17", true), InlineData("18", false), InlineData("18", true), InlineData("19", false), InlineData("19", true), InlineData("20", false), InlineData("20", true), InlineData("21", false), InlineData("21", true)]
    public async Task Session_end_and_host_stopping_release_without_explicit_disconnect(string release, bool hostStopping)
    {
        string root = Path.Combine(Path.GetTempPath(), "tia-client-stop-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string? previous = Environment.GetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT");
        Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT", root);
        try
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter." + release + ".dll"), "offline session fixture");
            using var worker = new ClientBridge(release, exe);
            await worker.Call("Attach", new JsonObject { ["processId"] = 123 }, CancellationToken.None);
            var watch = Stopwatch.StartNew();
            if (hostStopping)
            {
                if (release is "20" or "21") foundationhost::TiaMcp.FoundationHost.WorkerShutdown.StopAll();
                else WorkerShutdown.StopAll();
            }
            else worker.Dispose();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
            Assert.Equal("RELEASED\n", File.ReadAllText(Path.Combine(root, "123-1.lease")));
        }
        finally { Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT", previous); Directory.Delete(root, true); }
    }
    [Theory, InlineData("19", false), InlineData("19", true), InlineData("20", false), InlineData("20", true), InlineData("21", false), InlineData("21", true)]
    public async Task Stop_during_native_dispatch_or_after_unknown_retains_refusal(string release, bool answeredUnknown)
    {
        string root = Path.Combine(Path.GetTempPath(), "tia-client-busy-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string? previous = Environment.GetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT");
        Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT", root);
        try
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter." + release + ".dll"), "offline session fixture");
            using var worker = new ClientBridge(release, exe);
            await worker.Call("Attach", new JsonObject { ["processId"] = 123 }, CancellationToken.None);
            Task<JsonNode?> running = worker.Call(answeredUnknown ? "FixtureUnknown" : "FixtureHang", new JsonObject(), CancellationToken.None);
            if (answeredUnknown) await Assert.ThrowsAnyAsync<Exception>(() => running);
            else
            {
                for (int i = 0; i < 500 && !File.Exists(Path.Combine(root, "busy")); i++) await Task.Delay(10);
                Assert.True(File.Exists(Path.Combine(root, "busy")));
            }
            var watch = Stopwatch.StartNew(); worker.Dispose();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
            if (!answeredUnknown) await Assert.ThrowsAnyAsync<Exception>(() => running);
            string path = Path.Combine(root, "123-1.lease");
            for (int i = 0; i < 100; i++)
            {
                try { File.ReadAllText(path); break; } catch (IOException) { await Task.Delay(10); }
            }
            Assert.Equal(answeredUnknown ? "UNCERTAIN\n" : "BUSY\n", File.ReadAllText(path));
            Assert.Equal(SessionBehavior.LeaseNotReleased, Assert.Throws<InvalidOperationException>(() => TiaMcpServer.Siemens.PortalProcessLease.Acquire(root, 123, 1)).Message);
        }
        finally { Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT", previous); Directory.Delete(root, true); }
    }

    [Fact]
    public void Portal_admission_keeps_offline_runtime_and_connection_discovery_available()
    {
        foreach (string name in new[] { "AttachOpenProject", "OpenProject", "GetProjectInfo", "CompileDevice", "ReadPlcTags", "GetHmiScreenSnapshot" })
            Assert.True(TiaMcpServer.ModelContextProtocol.ToolTaxonomy.RequiresConnectedPortal(name), name);
        foreach (string name in new[] { "ConnectPortal", "DisconnectPortal", "GetSessionState", "ListPortalProcessProjects", "GetPortalInfo",
            "ComparePlcBlockDocuments", "GeneratePlcDocumentation", "ExtractPlcBlockMetrics", "BuildStructuredText", "CheckProductUpdate", "GetUnifiedRuntimeTags" })
            Assert.False(TiaMcpServer.ModelContextProtocol.ToolTaxonomy.RequiresConnectedPortal(name), name);
        foreach (string name in new[] { "GetUnifiedRuntimeTags", "BuildStructuredText", "CheckProductUpdate", "GeneratePlcDocumentation", "DescribeUnifiedScreenItemType" })
            Assert.False(TiaMcpServer.ModelContextProtocol.ToolTaxonomy.MayCallOpenness(name), name);
        Assert.True(TiaMcpServer.ModelContextProtocol.ToolTaxonomy.MayCallOpenness("ConnectPortal"));
    }

    private sealed class ClientBridge : IFoundationWorker
    {
        private readonly Client? engine;
        private readonly WorkerClient? foundation;
        internal bool Poisoned => engine?.Poisoned ?? foundation!.Poisoned;
        internal ClientBridge(string release, string exe)
        {
            if (release is "20" or "21") engine = new Client(new Options { ReleaseKey = release, BundleRoot = AppContext.BaseDirectory,
                WorkerExe = exe, ApiDirectory = AppContext.BaseDirectory, EngineWorkerExe = exe, NativeEnabled = true }, Client.Hash(exe));
            else foundation = new WorkerClient(release, exe, AppContext.BaseDirectory, true);
        }
        public Task<JsonNode?> Call(string name, JsonObject args, CancellationToken token) => engine != null ? engine.Call(name, args, token) : foundation!.Call(name, args, token);
        public void Dispose() { engine?.Dispose(); foundation?.Dispose(); }
    }
}
