using System.Text.Json.Nodes;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

// The CLI and xUnit suite share the original gate cases and runtime manifest regressions.
internal static class DotnetSuiteSelfTests
{
    internal static IReadOnlyList<(string Name, Action Run)> Cases { get; } =
    [
        ("platform override", PlatformOverride),
        ("minimum and growth", () => WithFixture((path, _) => { foreach (var count in new[] { 2, 3 }) { var r = Check(path, Enumerable.Repeat("Passed", count).ToArray()); Require(r.Errors.Count == 0 && r.Counts["methods"]![0]!["passed"]!.GetValue<int>() == count); } })),
        ("deliberate failure", () => WithFixture((path, _) => Require(Check(path, ["Passed", "Passed", "Failed"]).Errors.Contains("1 failed checks")))),
        ("below minimum", () => WithFixture((path, _) => Require(Check(path, ["Passed"]).Errors.Contains("passed 1 below minimum 2")))),
        ("zero executed even with zero minimum", () => WithFixture((path, _) => Require(Check(path, [], 0).Errors.Contains("zero tests executed")))),
        ("skip limit and allowed skip", () => WithFixture((path, _) => { Require(Check(path, ["Passed", "Passed", "NotExecuted"]).Errors.Contains("skipped 1 above maximum 0")); var r = Check(path, ["Passed", "Passed", "NotExecuted"], maximum: 1); Require(r.Errors.Count == 0 && r.Counts["methods"]![0]!["skipped"]!.GetValue<int>() == 1); })),
        ("all skipped is not execution", () => WithFixture((path, _) => Require(Check(path, ["NotExecuted"], 0, 1).Errors.Contains("zero tests executed")))),
        ("vstest skip counter is zero", () => WithFixture((path, _) => { var r = Check(path, ["Passed", "Passed", "NotExecuted"], maximum: 1, overrides: new() { ["notExecuted"] = "0" }); Require(r.Errors.Count == 0 && r.Counts["skipped"]!.GetValue<int>() == 1); Require(Check(path, ["Passed", "Passed", "NotExecuted"], overrides: new() { ["notExecuted"] = "0" }).Errors.Contains("skipped 1 above maximum 0")); })),
        ("other failures", () => WithFixture((path, _) => { foreach (var key in new[] { "error", "timeout", "aborted", "inconclusive" }) Require(Check(path, ["Passed", "Passed"], overrides: new() { [key] = "1" }).Errors.Count != 0); })),
        ("inflated counters", () => WithFixture((path, _) => Require(Check(path, ["Passed"], overrides: new() { ["total"] = "2", ["executed"] = "2", ["passed"] = "2" }).Errors.Count != 0))),
        ("unknown result", () => WithFixture((path, _) => Require(Check(path, ["Passed", "Error"]).Errors.Count != 0))),
        ("invalid xml and counters", InvalidXml),
        ("missing trx removes stale success", MissingTrx),
        ("runner command report and nonzero exit", RunnerCommand),
        ("hang evidence cannot pass or reuse stale artifacts", HangEvidence),
        ("planted hang produces a mini dump in both runners", PlantedHang),
        ("missing variant runtime row", MissingVariantRow),
        ("missing variant fails before test process", MissingVariantProcess),
        ("concurrent runtime manifests stay independent", ConcurrentManifests),
        ("split suite repository coverage", SplitSuiteRepositoryCoverage),
        ("split suite missing CI child is rejected", MissingSplitCi),
        ("split suite missing release child is rejected", MissingSplitRelease)
    ];

    internal static List<string> SplitSuiteCoverageErrors(JsonObject catalog, string workflow, IEnumerable<string> releaseSuites)
    {
        var release = releaseSuites.ToHashSet(StringComparer.Ordinal);
        var jobs = Regex.Matches(workflow, @"(?ms)^  ([a-zA-Z0-9_-]+):[ \t]*\r?\n(.*?)(?=^  [a-zA-Z0-9_-]+:[ \t]*\r?$|\z)")
            .Select(job => (Name: job.Groups[1].Value, Suites: Regex.Matches(job.Groups[2].Value, @"(?m)^        run:[ \t]*(.*(?:\r?\n          [^\r\n]*)*)")
                .SelectMany(run => Regex.Matches(run.Value, @"(?i)(?:-Suite|--suite)(?:[ \t]+|=)([a-z0-9-]+)").Select(match => match.Groups[1].Value))
                .ToHashSet(StringComparer.Ordinal))).ToArray();
        var errors = new List<string>();
        foreach (var (name, row) in catalog)
        {
            if (row?["splitFrom"]?.GetValue<string>() is not { } parent) continue;
            if (!catalog.ContainsKey(parent)) errors.Add(name + ": missing registered parent " + parent);
            var owners = jobs.Where(job => job.Suites.Contains(parent)).ToArray();
            if (owners.Length == 0) errors.Add(parent + ": no CI workflow job");
            foreach (var job in owners)
                if (!job.Suites.Contains(name)) errors.Add(name + ": missing from CI job " + job.Name);
            foreach (var suite in new[] { parent, name })
                if (!release.Contains(suite)) errors.Add(suite + ": missing from release pipeline");
        }
        return errors;
    }

    private static void SplitSuiteRepositoryCoverage()
    {
        var root = Repository.FindRoot();
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "tests/test-suites.json")))!.AsObject();
        Require((string?)catalog["kept-code"]?["splitFrom"] == "offline" && (string?)catalog["kept-code-v20"]?["splitFrom"] == "offline-v20");
        var errors = SplitSuiteCoverageErrors(catalog, File.ReadAllText(Path.Combine(root, ".github/workflows/offline-checks.yml")), ReleaseCommandTable.OfflineSuites);
        if (errors.Count != 0) throw new ReleaseException(string.Join("\n", errors));
        foreach (var name in ReleaseCommandTable.OfflineSuites)
            Require(ReleaseCommandTable.Get(name).Command == "test-suites -Suite " + name);
    }

    private static void MissingSplitCi()
    {
        var catalog = JsonNode.Parse("{\"parent\":{},\"moved\":{\"splitFrom\":\"parent\"},\"another\":{\"splitFrom\":\"parent\"}}")!.AsObject();
        var workflow = "jobs:\n  regression:\n    steps:\n      - name: Suites\n        run: test-suites -Suite parent -Suite moved\n";
        var errors = SplitSuiteCoverageErrors(catalog, workflow, ["parent", "moved", "another"]);
        Require(errors.SequenceEqual(new[] { "another: missing from CI job regression" }));
        Require(SplitSuiteCoverageErrors(catalog, workflow.Replace("-Suite moved", "-Suite moved -Suite another", StringComparison.Ordinal), ["parent", "moved", "another"]).Count == 0);
    }

    private static void MissingSplitRelease()
    {
        var catalog = JsonNode.Parse("{\"parent\":{},\"moved\":{\"splitFrom\":\"parent\"}}")!.AsObject();
        var workflow = "jobs:\n  regression:\n    steps:\n      - name: Suites\n        run: |\n          test-suites -Suite parent -Suite moved\n";
        Require(SplitSuiteCoverageErrors(catalog, workflow, ["parent"]).SequenceEqual(new[] { "moved: missing from release pipeline" }));
        Require(SplitSuiteCoverageErrors(catalog, workflow, ["parent", "moved"]).Count == 0);
    }

    internal static void SyntheticTrx(string path, string[] outcomes, Dictionary<string, string>? overrides = null)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var counts = new Dictionary<string, string>
        {
            ["total"] = outcomes.Length.ToString(), ["executed"] = outcomes.Count(x => x != "NotExecuted").ToString(),
            ["passed"] = outcomes.Count(x => x == "Passed").ToString(), ["failed"] = outcomes.Count(x => x == "Failed").ToString(),
            ["notExecuted"] = outcomes.Count(x => x == "NotExecuted").ToString()
        };
        if (overrides is not null) foreach (var (key, value) in overrides) counts[key] = value;
        var root = new XElement(ns + "TestRun", new XElement(ns + "ResultSummary", new XAttribute("outcome", "Completed"),
            new XElement(ns + "Counters", counts.Select(p => new XAttribute(p.Key, p.Value)))),
            new XElement(ns + "TestDefinitions", outcomes.Select((_, i) => new XElement(ns + "UnitTest", new XAttribute("id", i),
                new XElement(ns + "TestMethod", new XAttribute("className", "Example.Checks"), new XAttribute("name", "Check"))))),
            new XElement(ns + "Results", outcomes.Select((outcome, i) => new XElement(ns + "UnitTestResult", new XAttribute("testId", i), new XAttribute("executionId", i), new XAttribute("testName", "Example.Checks.Check(row: " + i + ")"), new XAttribute("outcome", outcome)))));
        new XDocument(root).Save(path);
    }

    private static (JsonObject Counts, List<string> Errors) Check(string path, string[] outcomes, int minimum = 2, int maximum = 0, Dictionary<string, string>? overrides = null)
    { SyntheticTrx(path, outcomes, overrides); return DotnetSuites.EvaluateTrx(path, minimum, maximum); }

    private static void WithFixture(Action<string, string> test)
    {
        using var fixture = new OfflineFixtures("dotnet-gate-self-test-");
        test(Path.Combine(fixture.DirectoryPath, "example.trx"), fixture.DirectoryPath);
    }
    private static void Require(bool value) { if (!value) throw new ReleaseException("dotnet gate self-check failed"); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ReleaseException or ArgumentException or FormatException or System.Xml.XmlException) { return; }
        throw new ReleaseException("dotnet gate self-check expected rejection");
    }
    private static JsonObject Suite() => new() { ["project"] = "example.csproj", ["minimumPassed"] = 2, ["maximumSkipped"] = 0, ["arguments"] = new JsonArray("-p:DefineConstants=TIA_V20") };

    private static void PlatformOverride() => WithFixture((_, directory) =>
    {
        var catalog = Path.Combine(directory, "catalog.json");
        File.WriteAllText(Path.Combine(directory, "example.csproj"), "");
        var value = new JsonObject { ["example"] = new JsonObject { ["project"] = "example.csproj", ["minimumPassed"] = 3, ["maximumSkipped"] = 0,
            ["arguments"] = new JsonArray(), ["platforms"] = new JsonObject { ["linux"] = new JsonObject { ["minimumPassed"] = 2, ["reason"] = "one Windows-only check" } } } };
        File.WriteAllText(catalog, value.ToJsonString());
        Require(DotnetSuites.LoadCatalog(catalog, directory, "linux")["example"]!["minimumPassed"]!.GetValue<int>() == 2);
        Require(DotnetSuites.LoadCatalog(catalog, directory, "windows")["example"]!["minimumPassed"]!.GetValue<int>() == 3);
        value["example"]!["platforms"]!["linux"]!.AsObject().Remove("reason");
        File.WriteAllText(catalog, value.ToJsonString());
        Reject(() => DotnetSuites.LoadCatalog(catalog, directory, "linux"));
    });

    private static void InvalidXml() => WithFixture((path, _) =>
    {
        foreach (var text in new[] { "<broken", "<TestRun />" }) { File.WriteAllText(path, text); Reject(() => DotnetSuites.EvaluateTrx(path, 2, 0)); }
        foreach (var count in new[] { "-1", "bad" }) Reject(() => Check(path, ["Passed"], overrides: new() { ["passed"] = count }));
    });

    private static void MissingTrx() => WithFixture((path, directory) =>
    {
        SyntheticTrx(path, ["Passed", "Passed"]);
        var result = DotnetSuites.RunSuite(Repository.FindRoot(), "example", Suite(), directory, process: (_, _, _, _) => new CommandResult(0, "", ""));
        Require(result.ExitCode != 0 && !File.Exists(path) && File.ReadAllText(Path.Combine(directory, "example.json")).Contains("missing TRX", StringComparison.Ordinal));
        result = DotnetSuites.RunSuite(Repository.FindRoot(), "example", Suite(), directory, process: (_, _, _, _) => throw new System.ComponentModel.Win32Exception("dotnet executable missing"));
        Require(result.ExitCode != 0 && File.ReadAllText(Path.Combine(directory, "example.json")).Contains("dotnet executable missing", StringComparison.Ordinal));
    });

    private static void RunnerCommand() => WithFixture((path, directory) =>
    {
        foreach (var code in new[] { 0, 1 })
        {
            var result = DotnetSuites.RunSuite(Repository.FindRoot(), "example", Suite(), directory, arguments: ["--no-restore"], process: (_, arguments, _, _) =>
            {
                var args = arguments.ToArray();
                Require(args[0] == "test" && args.Contains("-p:DefineConstants=TIA_V20") && args.Contains("--no-restore") && args.Contains("trx;LogFileName=example.trx"));
                Require(SuiteHang.TestArguments.All(args.Contains));
                SyntheticTrx(path, ["Passed", "Passed"]);
                return new CommandResult(code, "", "");
            });
            var report = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "example.json")))!;
            Require((result.ExitCode == 0) == (code == 0) && report["passed"]!.GetValue<int>() == 2 && report["methods"]![0]!["testMethod"]!.GetValue<string>() == "Check");
        }
    });

    private static void HangEvidence() => WithFixture((path, directory) =>
    {
        var sequence = Path.Combine(directory, "fixture_Sequence.xml");
        var dump = Path.Combine(directory, "testhost_hangdump.dmp");
        var before = SuiteHang.Begin(directory, "example");
        File.WriteAllText(sequence, "<TestSequence><Test Name=\"Fixture.PlantedHang\" Completed=\"False\" /></TestSequence>");
        File.WriteAllText(dump, "synthetic dump marker");
        Require(SuiteHang.Complete(directory, "example", before, new(0, "", "")).ExitCode == 1);
        SyntheticTrx(path, ["Passed", "Passed"]);
        var result = SuiteSharding.Merge("example", Suite(), [path], Path.Combine(directory, "merged"));
        Require(result.ExitCode == 1 && result.StandardError.Contains("Fixture.PlantedHang", StringComparison.Ordinal)
            && File.ReadAllText(Path.Combine(directory, "merged/example.json")).Contains("testhost_hangdump.dmp", StringComparison.Ordinal));
        File.Delete(path);
        try { SuiteSharding.Merge("example", Suite(), [path], Path.Combine(directory, "missing")); }
        catch (ReleaseException ex) { Require(ex.Message.Contains("Fixture.PlantedHang", StringComparison.Ordinal)); }
        Require(File.ReadAllText(Path.Combine(directory, "missing/example.json")).Contains("Fixture.PlantedHang", StringComparison.Ordinal));
        before = SuiteHang.Begin(directory, "example");
        Require(SuiteHang.Complete(directory, "example", before, new(0, "", "")).ExitCode == 0 && !SuiteHang.Errors(directory, "example").Any());
    });

    private static void PlantedHang()
    {
        var root = Repository.FindRoot();
        // Retain the real collector artifacts for the reviewer, below this worktree.
        var directory = Path.Combine(root, "bin-build/suite-hang-self-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var project = Path.Combine(directory, "HangFixture.csproj");
        var config = Path.Combine(directory, "nuget.config");
        File.WriteAllText(config, "<configuration><packageSources><clear /></packageSources></configuration>");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject>
                <ImportDirectoryBuildProps>false</ImportDirectoryBuildProps><ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets>
                <NuGetAudit>false</NuGetAudit></PropertyGroup>
              <ItemGroup><PackageReference Include="xunit" Version="2.9.2" />
                <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, "Tests.cs"), """
            namespace HangFixture;
            public sealed class Tests
            {
                [Xunit.Fact] public async System.Threading.Tasks.Task PlantedHang()
                {
                    if (System.Environment.GetEnvironmentVariable("TIA_MCP_PLANTED_HANG") == "1")
                        await System.Threading.Tasks.Task.Delay(System.Threading.Timeout.Infinite);
                    Xunit.Assert.Equal(4, 2 + 2);
                }
            }
            """);
        var build = ProcessRunner.Run("dotnet", ["build", project, "-c", "Release", "-p:RestoreConfigFile=" + config,
            "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-m:1", "-nodeReuse:false"], root);
        File.WriteAllText(Path.Combine(directory, "build.log"), build.StandardOutput + build.StandardError);
        ProcessRunner.RequireSuccess(build, "hang fixture build");
        var suite = new JsonObject { ["project"] = Path.GetRelativePath(root, project), ["minimumPassed"] = 1,
            ["maximumSkipped"] = 0, ["arguments"] = new JsonArray() };
        var unit = CheckAllBuild.Suite(suite);
        var manifest = Path.Combine(directory, "runtime-paths.tsv");
        File.WriteAllText(manifest, project + "\t" + unit.Variant + "\tbin\tbin\t" + Path.Combine(directory, "bin/Release/net10.0/HangFixture.dll") + "\n");
        var failures = new List<string>();
        foreach (var mode in new[] { "test", "vstest", "test-shard" })
        foreach (var hang in new[] { false, true })
        {
            var fast = mode != "test";
            var output = Path.Combine(directory, mode + (hang ? "-hang" : "-normal"));
            try
            {
                CommandResult Process(string executable, IEnumerable<string> arguments, string working, IDictionary<string, string?>? environment)
                {
                    var args = arguments.ToArray();
                    Require(mode == "vstest" ? args.Contains(SuiteHang.VstestArgument) : SuiteHang.TestArguments.All(args.Contains));
                    // Only this planted fixture uses a short limit; production has no override.
                    args = args.Select(a => a == SuiteHang.Timeout ? "2s" : a.Replace("TestTimeout=" + SuiteHang.Timeout, "TestTimeout=2s", StringComparison.Ordinal)).ToArray();
                    args = mode == "vstest" ? [.. args, "--Diag:" + Path.Combine(output, "vstest.log")] : [.. args, "--diag", Path.Combine(output, "vstest.log")];
                    var env = environment == null ? new Dictionary<string, string?>() : new Dictionary<string, string?>(environment);
                    env["TIA_MCP_PLANTED_HANG"] = hang ? "1" : null;
                    var result = ProcessRunner.Run(executable, args, working, env);
                    File.WriteAllText(Path.Combine(output, "process.log"), result.StandardOutput + result.StandardError);
                    return result;
                }
                var result = fast
                    ? FastSuites.RunShard(root, "fixture", suite, output, "dotnet", null, unit, 0, 1,
                        extra: mode == "test-shard" ? ["-p:NuGetAudit=false"] : null, manifest: manifest, process: Process)
                    : DotnetSuites.RunSuite(root, "fixture", suite, output, arguments: ["--no-build", "--no-restore"], process: Process);
                Require(hang ? result.ExitCode != 0 : result.ExitCode == 0);
                if (hang)
                {
                    Require((result.StandardOutput + result.StandardError).Contains("HangFixture.Tests.PlantedHang", StringComparison.Ordinal));
                    var evidence = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "fixture.hang.json")))!;
                    Require(evidence["tests"]!.AsArray().Any(t => t!.GetValue<string>() == "HangFixture.Tests.PlantedHang"));
                    if (fast)
                    {
                        var merged = SuiteSharding.Merge("fixture", suite, [Path.Combine(output, "fixture.trx")], Path.Combine(output, "merged"));
                        Require(merged.ExitCode != 0 && merged.StandardError.Contains("HangFixture.Tests.PlantedHang", StringComparison.Ordinal));
                    }
                    if (!evidence["artifacts"]!.AsArray().Any(p => p!.GetValue<string>().EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)
                        && new FileInfo(p.GetValue<string>()).Length > 0)) throw new ReleaseException("Missing real mini dump; see " + output);
                }
                else Require(DotnetSuites.EvaluateTrx(Path.Combine(output, "fixture.trx"), 1, 0).Errors.Count == 0);
            }
            catch (ReleaseException ex) { failures.Add(mode + (hang ? "-hang: " : "-normal: ") + ex.Message); }
        }
        if (failures.Count > 0) throw new ReleaseException(string.Join("\n", failures));
    }

    private static void MissingVariantRow() => WithFixture((_, directory) =>
    {
        var manifest = Path.Combine(directory, "runtime-paths.tsv");
        var project = Path.Combine(directory, "example.csproj");
        foreach (var unit in new[] { new CheckBuild("example.csproj", "", ["-p:TestVciFamily=initial"]),
            new CheckBuild("example.csproj", "custom-output", []) })
        {
            foreach (var row in new string?[] { null, project + "\tdefault\tbin\tbin\twrong.dll",
                project + "\t" + unit.Variant + "\tbin\tbin\t" })
            {
                File.Delete(manifest);
                if (row != null) File.WriteAllText(manifest, row + "\n");
                try { CheckAllBuild.TestAssembly(directory, unit, manifest, "variant-suite"); }
                catch (ReleaseException ex)
                {
                    Require(ex.Message.Contains("variant-suite", StringComparison.Ordinal) && ex.Message.Contains(project, StringComparison.Ordinal)
                        && ex.Message.Contains(unit.Variant, StringComparison.Ordinal) && ex.Message.Contains(manifest, StringComparison.Ordinal));
                    continue;
                }
                throw new ReleaseException("Missing variant row silently fell back");
            }
        }
    });

    private static void MissingVariantProcess() => WithFixture((_, directory) =>
    {
        var result = FastSuites.RunShard(directory, "variant-suite", Suite(), directory, "must-not-start-dotnet", null,
            CheckAllBuild.Suite(Suite()), 0, 1, manifest: Path.Combine(directory, "missing-runtime-paths.tsv"));
        Require(result.ExitCode == 1 && result.StandardError.Contains("Suite variant-suite: missing runtime manifest row", StringComparison.Ordinal)
            && result.StandardError.Contains("refusing to fall back", StringComparison.Ordinal) && !File.Exists(Path.Combine(directory, "variant-suite.trx")));
    });

    private static void ConcurrentManifests() => WithFixture((_, directory) =>
    {
        using var barrier = new Barrier(2);
        var unit = new CheckBuild("example.csproj", "", ["-p:TestVciFamily=initial"]);
        void Run(string name)
        {
            var output = Path.Combine(directory, name, "build"); Directory.CreateDirectory(output);
            var manifest = Path.Combine(output, "runtime-paths.tsv");
            File.WriteAllText(manifest, Path.Combine(directory, unit.Project) + "\t" + unit.Variant + "\tbin\tbin\t" + name + ".dll\n");
            Require(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
            Require(CheckAllBuild.TestAssembly(directory, unit, manifest, name) == name + ".dll");
        }
        Parallel.Invoke(() => Run("first"), () => Run("second"));
        Require(!File.Exists(Path.Combine(directory, "bin-build/check-all/runtime-paths.tsv")));
    });
}
