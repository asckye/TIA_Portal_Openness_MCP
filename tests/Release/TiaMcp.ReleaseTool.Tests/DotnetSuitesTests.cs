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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShardedBuildKeepsRestorePropertiesAndOnlyRegisteredSdkInputs(bool sdk)
    {
        using var tree = new StaticTree();
        tree.Write("fixture.csproj", "<Project />");
        var suite = new JsonObject { ["project"] = "fixture.csproj", ["arguments"] = new JsonArray(),
            ["minimumPassed"] = 1, ["maximumSkipped"] = 0, ["publicApiRoot"] = sdk };
        var catalog = tree.Write("catalog.json", new JsonObject { ["fixture"] = suite }.ToJsonString());
        var options = Options.Parse(["-Suite", "fixture", "-Shards", "8", "-PublicApiRoot", "sdk", "-Catalog", catalog,
            "-ResultsDirectory", tree.Root], Program.BooleanOptions);
        int builds = 0;
        var result = DotnetSuites.Run(tree.Root, options, process: (_, arguments, _, _) =>
        {
            var args = arguments.ToArray();
            if (args[0] == "build")
            {
                Assert.Contains("-p:UseSharedCompilation=false", args);
                Assert.Contains("-p:NuGetAudit=false", args);
                Assert.Equal(sdk, args.Contains("-p:TiaPublicApiRoot=sdk"));
                builds++;
            }
            else
            {
                Assert.Equal(1, builds);
                Assert.Contains("--no-build", args);
                var directory = args[Array.IndexOf(args, "--results-directory") + 1];
                DotnetSuiteSelfTests.SyntheticTrx(Path.Combine(directory, "fixture.trx"), ["Passed"]);
            }
            return new(0, "", "");
        });
        Assert.Equal(1, builds);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void KeptSuiteKeepsItsDefineAndPlatformGates()
    {
        var root = Repository.FindRoot();
        var windows = DotnetSuites.LoadCatalog(Path.Combine(root, "tests/test-suites.json"), root, "windows");
        var linux = DotnetSuites.LoadCatalog(Path.Combine(root, "tests/test-suites.json"), root, "linux");
        var features = TiaFeatures.ProjectCases(root);
        Assert.Equal(new[] { "kept-code" }, DotnetSuites.OfflineSuites);
        foreach (var name in DotnetSuites.OfflineSuites)
        {
            int Minimum(JsonObject catalog, string name) => catalog[name]!["minimumPassed"]!.GetValue<int>();
            Assert.Equal(5155, Minimum(windows, name));
            Assert.Equal(5155, Minimum(linux, name));
            Assert.Equal(windows[name]!["arguments"]!.ToJsonString(), linux[name]!["arguments"]!.ToJsonString());
            Assert.Equal(0, windows[name]!["maximumSkipped"]!.GetValue<int>());
            Assert.Equal(0, linux[name]!["maximumSkipped"]!.GetValue<int>());
            Assert.Contains(features, row => row.Project == (string?)windows[name]!["project"] &&
                row.Properties.GetValueOrDefault("DefineConstants") == null);
        }
        Assert.True(TimeSensitiveTests.ReserveShard((string)windows["kept-code"]!["project"]!));
    }

    [Fact]
    public void FeatureCasesEvaluateEachProjectConfigurationOnce()
    {
        var cases = TiaFeatures.ProjectCases(Repository.FindRoot());
        var identities = cases.Select(row => (row.Project,
            Properties: string.Join("\0", row.Properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "\0" + p.Value)))).ToArray();
        Assert.Equal(identities.Length, identities.Distinct().Count());
        const string project = "tests/KeptCode/TiaMcp.KeptCode.Tests/TiaMcp.KeptCode.Tests.csproj";
        Assert.Single(cases, row => row.Project == project && row.Properties.Count == 0);
        Assert.DoesNotContain(cases, row => row.Project == project && row.Properties.GetValueOrDefault("DefineConstants") == "TIA_V20");
    }

    [Fact]
    public void Retired_engine_parents_leave_their_PlcWorker_successors_registered_with_their_gates()
    {
        string root = Repository.FindRoot();
        var catalog = DotnetSuites.LoadCatalog(Path.Combine(root, "tests/test-suites.json"), root, "windows");
        // P8-04l: the engine parents plc-exchange-v20/v21 and worker-progress retired with the engine (D5);
        // their successors stand alone with their own zero-skip gates.
        foreach (var (retired, successor) in new[] { ("plc-exchange-v20", "plc-exchange-worker-v20"), ("plc-exchange-v21", "plc-exchange-worker-v21"),
            ("worker-progress", "worker-progress-foundation") })
        {
            Assert.Null(catalog[retired]);
            Assert.Equal(new[] { successor }, DotnetSuites.WithCompanions(new[] { successor }, catalog));
            Assert.Equal(0, (int?)catalog[successor]!["maximumSkipped"]);
        }
        foreach (var pair in new[] { ("shared-session", "shared-lifecycle-plc"), ("shared-lifecycle", "shared-lifecycle-plc") })
        {
            Assert.Equal(new[] { pair.Item1, pair.Item2 }, DotnetSuites.WithCompanions(new[] { pair.Item1 }, catalog));
            Assert.Equal(0, (int?)catalog[pair.Item2]!["maximumSkipped"]);
        }
        Assert.Equal(new[] { "shared-session", "shared-lifecycle-plc", "shared-lifecycle" },
            DotnetSuites.WithCompanions(new[] { "shared-session", "shared-lifecycle", "shared-lifecycle-plc" }, catalog));

    }

    [Fact]
    public void Missing_registered_companion_is_an_error()
    {
        var catalog = JsonNode.Parse("{\"engine\":{\"companions\":[\"missing\"]}}")!.AsObject();
        Assert.Throws<ReleaseException>(() => DotnetSuites.WithCompanions(new[] { "engine" }, catalog));
    }

    [Fact]
    public void SuiteBuildsTakeOnlyTheBuildProperties()
    {
        Assert.Equal(new[] { "-p:EngineRelease=21" }, DotnetSuites.BuildArguments(["--filter", "FullyQualifiedName!~Operator", "-p:EngineRelease=21"]));
        Assert.Empty(DotnetSuites.BuildArguments(["--filter", "FullyQualifiedName~WorkbenchControl"]));
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.FindRoot(), "tests/test-suites.json")))!.AsObject();
        foreach (var (name, suite) in catalog)
            if (suite is JsonObject spec && spec["arguments"] is JsonArray arguments)
                Assert.DoesNotContain(DotnetSuites.BuildArguments(arguments.Select(a => a!.GetValue<string>())), a => a.StartsWith("--", StringComparison.Ordinal));
    }

    [Fact]
    public void CommandsAndLegacyOptionSpellingsAreRegistered()
    {
        Assert.Contains("test-suites", CommandLine.Commands);
        Assert.DoesNotContain("host-parity", CommandLine.Commands);
        var options = Options.Parse(["--suite", "kept-code", "-Suite", "version-policy", "--no-restore", "--dotnet-arg=--no-build", "--results-directory", "results"], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NoRestore" });
        Assert.Equal(new[] { "kept-code", "version-policy" }, options.All("Suite"));
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


}
