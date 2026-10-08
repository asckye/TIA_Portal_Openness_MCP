using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public class DotnetSuitesTests
{
    public static IEnumerable<object[]> SelfTests() => DotnetSuiteSelfTests.Cases.Select((test, i) => new object[] { i, test.Name });
    [Theory]
    [MemberData(nameof(SelfTests))]
    public void OriginalGateSelfTests(int index, string name)
    {
        Assert.Equal(name, DotnetSuiteSelfTests.Cases[index].Name);
        DotnetSuiteSelfTests.Cases[index].Run();
    }

    [Fact]
    public void CommandsAndLegacyOptionSpellingsAreRegistered()
    {
        Assert.Contains("test-suites", CommandLine.Commands);
        Assert.Contains("host-parity", CommandLine.Commands);
        var options = Options.Parse(["--suite", "offline", "-Suite", "offline-v20", "--no-restore", "--dotnet-arg=--no-build", "--results-directory", "results"], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NoRestore" });
        Assert.Equal(new[] { "offline", "offline-v20" }, options.All("Suite"));
        Assert.True(options.Has("NoRestore"));
        Assert.Equal("--no-build", options.Get("DotnetArg"));
        Assert.Equal("results", options.Get("ResultsDirectory"));
    }

    [Theory]
    [InlineData("repeat-execution")]
    [InlineData("missing-definition")]
    [InlineData("bad-summary")]
    [InlineData("inconsistent-skips")]
    public void TrxRejectsIncompleteEvidence(string defect)
    {
        using var fixture = new OfflineFixtures();
        var path = Path.Combine(fixture.DirectoryPath, "test.trx");
        DotnetSuiteSelfTests.SyntheticTrx(path, ["Passed", "Passed"]);
        var doc = XDocument.Load(path);
        var ns = doc.Root!.Name.Namespace;
        if (defect == "repeat-execution") doc.Descendants(ns + "UnitTestResult").Last().SetAttributeValue("executionId", "0");
        if (defect == "missing-definition") doc.Descendants(ns + "TestDefinitions").Remove();
        if (defect == "bad-summary") doc.Descendants(ns + "ResultSummary").Single().SetAttributeValue("outcome", "Failed");
        if (defect == "inconsistent-skips") doc.Descendants(ns + "Counters").Single().SetAttributeValue("notExecuted", "1");
        doc.Save(path);
        if (defect is "repeat-execution" or "missing-definition") Assert.ThrowsAny<Exception>(() => DotnetSuites.EvaluateTrx(path, 2, 0));
        else Assert.NotEmpty(DotnetSuites.EvaluateTrx(path, 2, 0).Errors);
    }

    [Fact]
    public void RunProcessesEveryRequestedSuiteInOrderAfterFailure()
    {
        using var fixture = new OfflineFixtures();
        var root = fixture.DirectoryPath;
        File.WriteAllText(Path.Combine(root, "example.csproj"), "");
        var catalog = Path.Combine(root, "catalog.json");
        File.WriteAllText(catalog, "{\"first\":{\"project\":\"example.csproj\",\"minimumPassed\":2,\"maximumSkipped\":0,\"arguments\":[]},\"second\":{\"project\":\"example.csproj\",\"minimumPassed\":2,\"maximumSkipped\":0,\"arguments\":[]}}");
        var options = Options.Parse(["-Suite", "first", "-Suite", "second", "-Catalog", catalog, "-ResultsDirectory", root], new HashSet<string>());
        var names = new List<string>();
        var result = DotnetSuites.Run(root, options, process: (_, args, _, _) =>
        {
            var logger = args.Single(a => a.StartsWith("trx;", StringComparison.Ordinal));
            var file = logger.Split('=')[1];
            names.Add(file);
            DotnetSuiteSelfTests.SyntheticTrx(Path.Combine(root, file), ["Passed", "Passed"]);
            return new CommandResult(names.Count == 1 ? 1 : 0, "", "");
        });
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(new[] { "first.trx", "second.trx" }, names);
        Assert.Contains("COMPLETE: 2 second", result.StandardOutput);
    }

    [Fact]
    public void PublicApiRootComesFromTheIsolatedEnvironment()
    {
        using var fixture = new OfflineFixtures();
        var suite = JsonNode.Parse("{\"project\":\"example.csproj\",\"minimumPassed\":1,\"maximumSkipped\":0,\"arguments\":[],\"publicApiRoot\":true}")!.AsObject();
        var result = DotnetSuites.RunSuite(fixture.DirectoryPath, "example", suite, fixture.DirectoryPath,
            environment: new Dictionary<string, string?> { ["TIA_MCP_TEST_PUBLIC_API_ROOT"] = "isolated-sdk" }, process: (_, args, _, env) =>
            {
                Assert.Contains("-p:TiaPublicApiRoot=isolated-sdk", args);
                Assert.Equal("isolated-sdk", env!["TIA_MCP_TEST_PUBLIC_API_ROOT"]);
                DotnetSuiteSelfTests.SyntheticTrx(Path.Combine(fixture.DirectoryPath, "example.trx"), ["Passed"]);
                return new CommandResult(0, "", "");
            });
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("20", false)]
    [InlineData("21", false)]
    [InlineData("20", true)]
    public void HostParityPreservesCommandsEnvironmentAndSummary(string major, bool noBuild)
    {
        using var fixture = new OfflineFixtures();
        var options = Options.Parse(["-EngineMajor", major, "-ResultsDirectory", fixture.DirectoryPath, "-NoBuild", noBuild.ToString()], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NoBuild" });
        var environment = new Dictionary<string, string?> { ["TEST_INHERITED"] = "value" };
        var result = HostParity.Run(Repository.FindRoot(), options, environment, (_, args, _, env) =>
        {
            Assert.Equal(major, env!["TIA_MCP_PARITY_ENGINE_RELEASE"]);
            Assert.Equal("value", env["TEST_INHERITED"]);
            Assert.Equal(noBuild, args.Contains("--no-build"));
            Assert.Equal(noBuild, args.Contains("--no-restore"));
            Assert.Equal(!noBuild && major == "20", args.Contains("-p:DefineConstants=TIA_V20"));
            Assert.Contains("--disable-build-servers", args);
            DotnetSuiteSelfTests.SyntheticTrx(Path.Combine(fixture.DirectoryPath, "host-behavior-parity.trx"), Enumerable.Repeat("Passed", 103).ToArray());
            return new CommandResult(0, "", "");
        });
        Assert.Equal(0, result.ExitCode);
        Assert.False(environment.ContainsKey("TIA_MCP_PARITY_ENGINE_RELEASE"));
        var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.DirectoryPath, "host-behavior-parity.json")))!;
        Assert.Equal(major, summary["engineRelease"]!.GetValue<string>());
        Assert.Equal(103, summary["passed"]!.GetValue<int>());
        Assert.Equal(3, summary["paths"]!.AsArray().Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostParityCannotReuseStaleSuccess(bool writeTrx)
    {
        using var fixture = new OfflineFixtures();
        var path = Path.Combine(fixture.DirectoryPath, "host-behavior-parity.trx");
        DotnetSuiteSelfTests.SyntheticTrx(path, Enumerable.Repeat("Passed", 103).ToArray());
        var options = Options.Parse(["-ResultsDirectory", fixture.DirectoryPath], new HashSet<string>());
        var result = HostParity.Run(Repository.FindRoot(), options, process: (_, _, _, _) =>
        {
            if (writeTrx) DotnetSuiteSelfTests.SyntheticTrx(path, ["Passed"]);
            return new CommandResult(1, "", "");
        });
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("dotnet test exited 1", result.StandardOutput);
        Assert.Contains(writeTrx ? "below minimum" : "missing TRX", result.StandardOutput);
    }
}
