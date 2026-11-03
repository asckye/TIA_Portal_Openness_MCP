using TiaMcp.ReleaseTool;
using Xunit;

public sealed class PlcWorkerFixtureTests
{
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
        Assert.Contains("check-plc-workers", CommandLine.Commands);
        Assert.Throws<ReleaseException>(() => PlcWorkerFixtures.Sdk("14"));
        Assert.Throws<ReleaseException>(() => PlcWorkerFixtures.Sdk("15"));
        Assert.Throws<ReleaseException>(() => PlcWorkerFixtures.Sdk("TYPO"));
    }
}
