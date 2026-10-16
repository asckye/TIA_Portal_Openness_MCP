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
    public void Fixture_units_are_isolated_and_cover_all_candidate_jobs()
    {
        string output = Path.GetFullPath("bin-build/plc-fixture-unit-tests");
        var units = PlcWorkerFixtures.Builds(PlcWorkerFixtures.Keys).ToArray();
        Assert.Equal(24, units.Length);
        Assert.Equal(24, units.Select(unit => unit.Worker(output)).Distinct().Count());
        Assert.All(units, unit => Assert.Contains("-p:TiaMcpPlcWorkerSdkFixture=true", unit.Properties(output)));
        Assert.Equal(8, units.Count(unit => unit.TestPolicy == null));
        foreach (string key in new[] { "20", "21" })
        foreach (string family in PlcWorkerFixtures.Families)
        {
            var current = PlcWorkerFixtures.Candidate(key, family, "current");
            Assert.Null(current.TestPolicy); Assert.Equal("sdk", current.Variant);
            var safe = PlcWorkerFixtures.Candidate(key, family, "safe-v4");
            Assert.Equal("P6-" + family + ":safe-v4", safe.TestPolicy);
            Assert.Contains(safe, units);
        }
        Assert.Contains("build-plc-worker-fixtures", CommandLine.Commands);
        Assert.Contains("check-plc-workers", CommandLine.Commands);
        Assert.Throws<ReleaseException>(() => PlcWorkerFixtures.Candidate("19", "IMPORT", "safe-v4"));
        Assert.Throws<ReleaseException>(() => PlcWorkerFixtures.Candidate("20", "TYPO", "safe-v4"));
        Assert.Throws<ReleaseException>(() => PlcWorkerFixtures.Candidate("21", "SOURCE", "invalid"));
    }
}
