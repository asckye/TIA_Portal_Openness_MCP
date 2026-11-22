using System.Diagnostics;
using System.Text.Json;
using TiaMcp.BuildCommon;
using TiaMcp.Engine.Tests.Shared;
using TiaMcp.ReleaseTool;
using TiaMcp.ReleaseTool.Tests;
using TiaMcp.WorkerChannel;
using Xunit;

public sealed class PlcWorkerFixtureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Smoke_worker_returns_its_state_or_is_stopped_by_the_active_gate(bool hold)
    {
        using var fixture = new OfflineFixtures();
        string key = Path.GetFileName(fixture.DirectoryPath);
        string worker = Path.Combine(AppContext.BaseDirectory, "TiaMcp.ReleaseTool.Tests.exe");
        if (!OperatingSystem.IsWindows())
        {
            await Assert.ThrowsAsync<PlatformNotSupportedException>(() => PlcWorkerFixtures.Smoke(worker, fixture.DirectoryPath, key));
            return;
        }
        string adapter = Path.Combine(AppContext.BaseDirectory, $"TiaMcp.Adapter.{key}.dll");
        File.Copy(typeof(ChannelClient).Assembly.Location, adapter);
        try
        {
            var clock = new GateWatchdogTests.Clock();
            var gate = new GateWatchdog("smoke", TimeSpan.FromMinutes(1), clock, _ => { });
            using var job = new GateWatchdog.Job(gate, "worker", null);
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, key + ".ready");
            using var release = new EventWaitHandle(false, EventResetMode.ManualReset, key + ".release");
            var smoke = PlcWorkerFixtures.Smoke(worker, fixture.DirectoryPath, key, environmentFault: hold ? "gate-hold" : null);
            if (!hold)
            {
                Assert.Contains(key, await smoke.WaitAsync(TestDeadlines.Hang));
                return;
            }
            Exception? failure;
            try
            {
                Assert.True(ready.WaitOne(TestDeadlines.Hang));
                using var child = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(fixture.DirectoryPath, "pid"))));
                Assert.Equal(worker, child.MainModule!.FileName, ignoreCase: true);
                clock.Advance(TimeSpan.FromMinutes(1));
                Assert.Throws<ReleaseException>(() => gate.Poll());
                Assert.True(child.HasExited);
            }
            finally
            {
                release.Set();
                failure = await Record.ExceptionAsync(() => smoke.WaitAsync(TestDeadlines.Hang));
            }
            Assert.NotNull(failure);
            Assert.IsNotType<TimeoutException>(failure);
        }
        finally { File.Delete(adapter); }
    }

    internal static int RunSmokeWorker(string key, string api, string nonce)
    {
        string worker = Environment.ProcessPath!;
        var identity = new ChannelIdentity(key, FileHash.Sha256(worker),
            FileHash.Sha256(Path.Combine(Path.GetDirectoryName(worker)!, $"TiaMcp.Adapter.{key}.dll")), Environment.ProcessId, nonce);
        new ChannelServer(Console.OpenStandardInput(), Console.OpenStandardOutput(), identity, () => new ChannelBinding(0, false), request =>
        {
            if (request.Method != "adapter.ReadState") throw new InvalidOperationException(request.Method);
            if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("TIA_MCP_PLC_WORKER_FAULT") == "gate-hold")
            {
                File.WriteAllText(Path.Combine(api, "pid"), Environment.ProcessId.ToString());
                using var ready = EventWaitHandle.OpenExisting(key + ".ready");
                using var release = EventWaitHandle.OpenExisting(key + ".release");
                ready.Set();
                release.WaitOne(TestDeadlines.Hang);
            }
            return ChannelResponse.Success(JsonSerializer.Serialize(new { ReleaseKey = key, IsAttached = false }));
        }).Run();
        return 0;
    }

    [Theory]
    [InlineData("TiaMcpPlcWorkerSdkFixture=true")]
    [InlineData("TiaMcpTestPolicy=P6-SOURCE:safe-v4")]
    public void Production_outputs_refuse_fixture_build_properties(string property)
    {
        string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        var result = ProcessRunner.Run("dotnet", ["msbuild", Path.Combine(root, "src/PlcWorker/TiaMcp.PlcWorker.csproj"),
            "-t:ValidateWorkerAdapter", "-p:TiaReleaseKey=19", "-p:" + property, "-m:1", "-nodeReuse:false"], root);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("production outputs cannot enable fixtures", result.StandardOutput + result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixture_units_are_isolated_and_cover_all_releases()
    {
        string output = Path.GetFullPath("bin-build/plc-fixture-unit-tests");
        var units = PlcWorkerFixtures.Builds(PlcWorkerFixtures.Keys).ToArray();
        Assert.Equal(8, units.Length);
        Assert.Equal(8, units.Select(unit => unit.Worker(output)).Distinct().Count());
        Assert.All(units, unit => Assert.Contains("-p:TiaMcpPlcWorkerSdkFixture=true", unit.Properties(output)));
        Assert.All(units, unit => Assert.DoesNotContain(unit.Properties(output), property => property.StartsWith("-p:TiaMcpTestPolicy=", StringComparison.Ordinal)));
        foreach (string key in PlcWorkerFixtures.Keys)
        {
            var current = PlcWorkerFixtures.Sdk(key);
            Assert.Equal("sdk", current.Variant);
            Assert.Contains(current, units);
        }
        Assert.Contains("build-plc-worker-fixtures", CommandLine.Commands);
        Assert.Throws<ReleaseException>(() => PlcWorkerFixtures.Sdk("14"));
        Assert.Throws<ReleaseException>(() => PlcWorkerFixtures.Sdk("15"));
        Assert.Throws<ReleaseException>(() => PlcWorkerFixtures.Sdk("TYPO"));
    }
}
