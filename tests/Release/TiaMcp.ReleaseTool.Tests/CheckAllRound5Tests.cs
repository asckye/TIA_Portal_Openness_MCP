using System.Text.Json;
using TiaMcp.ReleaseTool;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class CheckAllRound5Tests
{
    [Fact]
    public void FileAppCacheInvalidatesOnActualSourceImportAndSdkChanges()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        void Write(string file, string value) { var path = Path.Combine(root, file); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); }
        Write("build-tools/common/fixture.cs", "one"); Write("scripts/checks/sample.cs", "sample");
        Write("Version.props", "version"); Write("offline.config", "offline");
        string Key(string sdk = "10.0.401") => ReleaseCommands.CheckAllScriptInputKey(root, "sample", Path.Combine(root, "offline.config"), sdk);
        var original = Key(); Write("build-tools/common/fixture.cs", "two"); Assert.NotEqual(original, Key());
        Write("build-tools/common/fixture.cs", "one"); Assert.Equal(original, Key());
        Write("Directory.Build.props", "import"); Assert.NotEqual(original, Key());
        File.Delete(Path.Combine(root, "Directory.Build.props")); Assert.Equal(original, Key());
        Write("build-tools/common/extra.cs", "new"); Assert.NotEqual(original, Key());
        File.Delete(Path.Combine(root, "build-tools/common/extra.cs")); Assert.Equal(original, Key());
        Assert.NotEqual(original, Key("10.0.402"));
    }

    [Fact]
    public void CurrentCasePlanOverridesObsoleteWholeShardHistory()
    {
        var history = new Dictionary<string, double> { ["suite-shard-0"] = 351, ["capture"] = 173 };
        Assert.Equal(60, ReleaseCommands.CheckAllJobCost(new CheckJob("suite-shard-0", () => new(0, "", ""), []) { EstimatedSeconds = 60 }, history));
        Assert.Equal(173, ReleaseCommands.CheckAllJobCost(new("capture", () => new(0, "", ""), []), history));
    }

    [Fact]
    public void IdleReservedLaneGetsItsInputsBeforeTheGeneralBacklog()
    {
        using var fixture = new OfflineFixtures(); var order = new List<string>();
        CommandResult Run(string name) { lock (order) order.Add(name); return new(0, "", ""); }
        var rows = CheckScheduler.Run([
            new CheckJob("long-suite", () => Run("long-suite"), []) { EstimatedSeconds = 300 },
            new CheckJob("production-catalog", () => Run("production-catalog"), []) { EstimatedSeconds = 2 },
            new CheckJob("stability", () => Run("stability"), ["production-catalog"]) { Lane = "stability", EstimatedSeconds = 40 }
        ], 1, fixture.DirectoryPath, new Dictionary<string, int> { ["general"] = 1, ["stability"] = 1 });
        Assert.All(rows, row => Assert.Equal("passed", row.Status));
        Assert.Equal("production-catalog", order[0]);
    }

    [Fact]
    public void DirectVstestPreservesEveryRegisteredFilterAndExactVariant()
    {
        var root = Repository.FindRoot();
        var catalog = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(root, "tests/test-suites.json")))!.AsObject();
        foreach (var (name, value) in catalog)
        {
            var source = value!["arguments"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
            var actual = FastSuites.VstestArguments("exact-variant.dll", source, name, "results");
            Assert.Equal("vstest", actual[0]); Assert.Equal("exact-variant.dll", actual[1]);
            var index = Array.IndexOf(source, "--filter");
            if (index >= 0) Assert.Contains("--TestCaseFilter:" + source[index + 1], actual);
            else Assert.DoesNotContain(actual, argument => argument.StartsWith("--TestCaseFilter:", StringComparison.Ordinal));
        }
        Assert.Throws<ReleaseException>(() => FastSuites.VstestArguments("suite.dll", ["--unknown"], "suite", "results"));
    }

    [Fact]
    public void OutputInventoryRejectsMissingContentInEveryVariant()
    {
        using var fixture = new OfflineFixtures();
        var normal = Path.Combine(fixture.DirectoryPath, "normal"); Directory.CreateDirectory(normal);
        File.WriteAllText(Path.Combine(normal, "suite.dll"), "assembly");
        Directory.CreateDirectory(Path.Combine(normal, "manifest"));
        File.WriteAllText(Path.Combine(normal, "manifest/package-manifest.json"), "{}");
        foreach (var variant in new[] { "default", "sdk-20", "sdk-21", "shared-false", "shared-true" })
        {
            var isolated = Path.Combine(fixture.DirectoryPath, variant); Directory.CreateDirectory(isolated);
            File.WriteAllText(Path.Combine(isolated, "suite.dll"), "assembly");
            Assert.Equal(new[] { "manifest/package-manifest.json" }, CheckAllLayout.Missing(normal, isolated, ["suite.dll", "manifest/package-manifest.json"]));
            Directory.CreateDirectory(Path.Combine(isolated, "manifest"));
            File.Copy(Path.Combine(normal, "manifest/package-manifest.json"), Path.Combine(isolated, "manifest/package-manifest.json"));
            Assert.Empty(CheckAllLayout.Missing(normal, isolated, ["suite.dll", "manifest/package-manifest.json"]));
        }
    }

    [Fact]
    public void LayoutInventoryCacheInvalidatesOnInputAdditionEditRemovalAndProperties()
    {
        var inputs = new Dictionary<string, string> { ["entry.cs"] = "one", ["content.json"] = "two" };
        var key = CheckAllLayout.InputKey(inputs, ["DefineConstants=ONE"]);
        Assert.Equal(key, CheckAllLayout.InputKey(inputs.Reverse(), ["DefineConstants=ONE"]));
        using var fixture = new OfflineFixtures();
        var source = Path.Combine(fixture.DirectoryPath, "source"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "fixture.dll"), "payload");
        var cache = new BuildOutputCache(Path.Combine(fixture.DirectoryPath, "cache"), "input-key");
        cache.Populate(key, source);
        Assert.True(cache.Restore(key, Path.Combine(fixture.DirectoryPath, "restored")));
        inputs["content.json"] = "changed";
        Assert.NotEqual(key, CheckAllLayout.InputKey(inputs, ["DefineConstants=ONE"]));
        inputs["content.json"] = "two"; inputs["new.props"] = "import";
        Assert.NotEqual(key, CheckAllLayout.InputKey(inputs, ["DefineConstants=ONE"]));
        inputs.Remove("new.props"); inputs.Remove("content.json");
        Assert.NotEqual(key, CheckAllLayout.InputKey(inputs, ["DefineConstants=ONE"]));
        inputs["content.json"] = "two";
        Assert.NotEqual(key, CheckAllLayout.InputKey(inputs, ["DefineConstants=TWO"]));
    }

    [Fact]
    public void TrxClassHistorySplitsNewExpensiveClassesAndRetainsLateProducerCost()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        var trx = Path.Combine(root, "suite.trx");
        File.WriteAllText(trx, """
            <TestRun><TestDefinitions>
            <UnitTest id="large"><TestMethod className="NewLargeClass" name="Check" /></UnitTest>
            <UnitTest id="small"><TestMethod className="SmallClass" name="Check" /></UnitTest>
            <UnitTest id="late"><TestMethod className="Legacy" name="Rows" /></UnitTest>
            </TestDefinitions><Results>
            <UnitTestResult testId="large" testName="NewLargeClass.Check(case: 1)" duration="00:00:40" />
            <UnitTestResult testId="large" testName="NewLargeClass.Check(case: 2)" duration="00:00:40" />
            <UnitTestResult testId="small" testName="SmallClass.Check" duration="00:00:05" />
            <UnitTestResult testId="late" testName="Legacy.Rows(row: one)" duration="00:00:00.000001" />
            </Results></TestRun>
            """);
        var costs = new Dictionary<string, double> { ["M:Legacy.Rows"] = 70 };
        SuiteCosts.ImportTrx([trx], costs, Path.Combine(root, "sample.classes.json"));
        File.WriteAllText(Path.Combine(root, "sample.json"), JsonSerializer.Serialize(costs));
        var plan = SuiteCosts.Prepare("sample", Path.Combine(root, "plan"), root, null, 8);
        var keys = File.ReadLines(plan.Path).Select(line => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(line.Split('\t')[0]))).ToArray();
        Assert.Contains("C:SmallClass", keys);
        Assert.Contains("T:NewLargeClass.Check(case: 1)", keys); Assert.Contains("T:NewLargeClass.Check(case: 2)", keys);
        Assert.Contains("M:Legacy.Rows", keys); Assert.Equal(155, plan.Costs.Sum());
        var classes = JsonSerializer.Deserialize<Dictionary<string, SuiteCosts.ClassCost>>(File.ReadAllText(Path.Combine(root, "sample.classes.json")))!;
        Assert.Equal(80, classes["NewLargeClass"].TrxSeconds); Assert.Equal(2, classes["NewLargeClass"].Cases);
        Assert.Equal(70, classes["Legacy"].Seconds);
    }
}
