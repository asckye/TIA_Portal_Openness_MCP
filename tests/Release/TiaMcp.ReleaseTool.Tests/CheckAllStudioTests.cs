using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.BuildCommon;
using TiaMcp.Engine.Tests.Shared;
using Xunit;
using SensitiveTests = TiaMcp.Engine.Tests.Shared.TimeSensitiveTests;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class CheckAllStudioTests
{
    private sealed record Case(string Class, string Method, bool Late = false)
    {
        internal string Name => Class + "." + Method;
        internal string Key => TestShard.CostKey(Class, Method, Name, Late);
    }

    private static Case[] Cases(string project) => SensitiveTests.Methods
        .Where(pair => pair.Key.StartsWith("TiaOpenness." + project + ".Tests.", StringComparison.Ordinal))
        .SelectMany(pair => pair.Value.Select(method => new Case(pair.Key, method,
            method == "Human_execution_of_prefilled_MCP_forms_previews_and_requires_the_normal_approval")))
        // Unknown ordinary methods in a registered class and unknown classes must remain in the ordinary partition.
        .Concat([new("TiaOpenness." + project + ".Tests.NewTests", "New_case"),
            new("TiaOpenness." + project + ".Tests." + (project == "Gui" ? "WorkbenchControlLoadTests" : "BridgeChannelTests"), "New_ordinary_method")])
        .ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WholeStudioSuitesPartitionEveryCaseOnceAndKeepRequiredEvidenceNames(bool measured)
    {
        using var fixture = new OfflineFixtures();
        var root = Repository.FindRoot(); var output = Path.Combine(fixture.DirectoryPath, "studio");
        var history = Path.Combine(fixture.DirectoryPath, "history"); Directory.CreateDirectory(history);
        var manifest = Path.Combine(fixture.DirectoryPath, "runtime.tsv");
        var names = new List<string>(); var rows = new List<string>();
        foreach (var mode in new[] { "false", "true" })
        foreach (var project in new[] { "Core", "Gui" })
        {
            var name = $"studio-{project.ToLowerInvariant()}-{mode}"; names.Add(name);
            var unit = new CheckBuild($"tests/Studio/TiaOpenness.{project}.Tests/TiaOpenness.{project}.Tests.csproj", "",
                ["-p:TiaSharedAdapterPaths=" + mode]);
            var assembly = Path.Combine(fixture.DirectoryPath, mode, $"TiaOpenness.{project}.Tests.dll");
            rows.Add(string.Join('\t', Path.Combine(root, unit.Project), unit.Variant, "", "", assembly));
            if (measured) File.WriteAllText(Path.Combine(history, name + ".json"),
                JsonSerializer.Serialize(Cases(project).ToDictionary(test => test.Key, _ => 60d)));
        }
        File.WriteAllLines(manifest, rows);
        var seen = new Dictionary<string, List<string>>();
        CommandResult Run(string executable, IEnumerable<string> arguments, string workingDirectory, IDictionary<string, string?>? environment)
        {
            Assert.Equal("dotnet", executable); Assert.Equal(root, workingDirectory); Assert.NotNull(environment);
            var args = arguments.ToArray(); Assert.Equal("vstest", args[0]);
            Assert.DoesNotContain(args, arg => arg.StartsWith("--TestCaseFilter:", StringComparison.Ordinal));
            Assert.Contains(SuiteHang.VstestArgument, args);
            string project = args[1].Contains(".Gui.", StringComparison.Ordinal) ? "Gui" : "Core";
            string mode = Path.GetFileName(Path.GetDirectoryName(args[1]))!;
            string name = $"studio-{project.ToLowerInvariant()}-{mode}";
            int count = int.Parse(environment["TIA_MCP_TEST_SHARD_COUNT"]!);
            int index = int.Parse(environment["TIA_MCP_TEST_SHARD_INDEX"]!);
            Assert.Equal(2, count); Assert.Equal("1", environment["TIA_MCP_TEST_RESERVE_SENSITIVE_SHARD"]);
            Assert.Equal("1", environment["TIA_MCP_TEST_SENSITIVE_SHARDS"]);
            Assert.Equal(root, environment["TIA_MCP_BUNDLE_ROOT"]);
            var plan = File.ReadLines(environment["TIA_MCP_TEST_SHARD_PLAN"]!).Select(line => line.Split('\t'))
                .ToDictionary(fields => Encoding.UTF8.GetString(Convert.FromBase64String(fields[0])), fields => int.Parse(fields[1]));
            var selected = Cases(project).Where(test => TestShard.Assign(test.Class, test.Key, test.Name, count, plan, true) == index).ToArray();
            Assert.NotEmpty(selected);
            Assert.All(selected, test => Assert.Equal(index == 1, SensitiveTests.Contains(test.Class, test.Method)));
            var identities = selected.SelectMany(test => Enumerable.Range(0, test.Late ? 3 : 1).Select(row => test.Name + "(" + row + ")")).ToArray();
            if (!seen.TryGetValue(name, out var list)) seen[name] = list = [];
            list.AddRange(identities);
            string directory = args.Single(arg => arg.StartsWith("--ResultsDirectory:", StringComparison.Ordinal))["--ResultsDirectory:".Length..];
            WriteTrx(Path.Combine(directory, name + ".trx"), identities);
            return new(0, "", "");
        }
        var jobs = new List<CheckJob>();
        ReleaseCommands.AddCheckAllStudioJobs(jobs, root, output, "dotnet", manifest, process: Run, history: history);
        Assert.Equal(12, jobs.Count);
        foreach (var name in names)
        {
            var gate = Assert.Single(jobs, job => job.Name == name);
            Assert.Equal(new[] { name + "-shard-0", name + "-shard-1" }, gate.Dependencies);
            Assert.True(gate.AllowFailedDependencies); Assert.False(gate.Exclusive);
            Assert.Contains((name, true), ReleaseEvidence.RequiredJobs(root));
            Assert.Contains(name, ReleaseEvidence.StudioJobs);
        }
        // Exercise the generated commands and the real TRX merge without native/WPF dependencies.
        var timings = CheckScheduler.Run(jobs, 1, Path.Combine(fixture.DirectoryPath, "logs"));
        Assert.All(timings, row => Assert.Equal("passed", row.Status));
        var evidencePath = ReleaseEvidence.Write(new JsonObject(), fixture.DirectoryPath, jobs.Select(job => job.Name).ToArray(),
            timings, 0, 1, true, [], DateTimeOffset.UtcNow);
        var evidence = JsonNode.Parse(File.ReadAllText(evidencePath))!;
        foreach (var name in names)
        {
            var recorded = Assert.Single(evidence["jobs"]!.AsArray(), row => (string?)row!["name"] == name)!;
            Assert.Equal("passed", (string?)recorded["status"]); Assert.Equal(64, ((string)recorded["logSha256"]!).Length);
            Assert.Contains(evidence["planned"]!.AsArray(), row => (string?)row == name);
            string project = name.Contains("-gui-", StringComparison.Ordinal) ? "Gui" : "Core";
            var expected = Cases(project).SelectMany(test => Enumerable.Range(0, test.Late ? 3 : 1).Select(row => test.Name + "(" + row + ")")).ToArray();
            Assert.Equal(expected.Order(), seen[name].Order());
            Assert.Equal(expected.Length, seen[name].Distinct().Count());
            var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(output, name + ".json")))!;
            Assert.Equal(expected.Length, (int)summary["passed"]!); Assert.Equal(0, (int)summary["failed"]!);
            Assert.Equal(expected.Length, SuiteSharding.Identities([Path.Combine(output, name + ".trx")]).Count);
        }
        var lanes = CheckAffinity.AssignLanes(jobs, new CheckAffinity.CheckAllCpuLayout(0xf, 0xf0, 0xf00, CheckAffinity.FullIsolation, null), 2,
            Enumerable.Range(0, 12).Select(bit => 1UL << bit).ToArray());
        Assert.NotEmpty(lanes);
        Assert.All(jobs.Where(job => job.Name.EndsWith("-shard-1", StringComparison.Ordinal)), job => {
            Assert.True(job.Exclusive); Assert.StartsWith("sensitive-", job.Lane);
            Assert.Equal(0UL, job.Affinity!.Value & 0xf00);
        });
        Assert.All(jobs.Where(job => !job.Exclusive), job => Assert.Equal("general", job.Lane));
    }

    [Fact]
    public void AllStudioVariantsSubsetsAndConfigurationDeclareTheSameWriteConflict()
    {
        using var fixture = new OfflineFixtures(); var root = Repository.FindRoot();
        var catalog = DotnetSuites.LoadCatalog(Path.Combine(root, "tests/test-suites.json"), root);
        var original = catalog.ToJsonString(); var jobs = new List<CheckJob>();
        foreach (var (name, node) in catalog.Where(pair => ((string)pair.Value!["project"]!).StartsWith("tests/Studio/", StringComparison.Ordinal)))
            FastSuites.AddJobs(jobs, root, name, node!.AsObject(), fixture.DirectoryPath, "dotnet", 1, null, [], history: Path.Combine(fixture.DirectoryPath, "history"));
        ReleaseCommands.AddCheckAllStudioJobs(jobs, root, fixture.DirectoryPath, "dotnet", history: Path.Combine(fixture.DirectoryPath, "history"));
        jobs.Add(new("configuration-page", () => new(0, "", ""), []));
        jobs = jobs.Select(ReleaseCommands.CheckAllStudioConflict).ToList();
        Assert.All(jobs.Where(job => job.SensitiveGroup != null || job.Name == "configuration-page"),
            job => Assert.Equal("studio-writes", job.ConflictGroup));
        Assert.Contains(jobs, job => job.Exclusive); Assert.Contains(jobs, job => !job.Exclusive && job.ConflictGroup != null);
        Assert.Null(ReleaseCommands.CheckAllStudioConflict(new("other", () => new(0, "", ""), [])).ConflictGroup);
        Assert.Equal(original, catalog.ToJsonString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteConflictsCrossLanesReleaseAfterFailureAndAllowUnrelatedWork(bool fail)
    {
        using var fixture = new OfflineFixtures(); using var barrier = new Barrier(2);
        int active = 0, completed = 0;
        CommandResult Studio(bool first)
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            try
            {
                if (first) Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
                else Assert.True(Volatile.Read(ref completed) >= 1);
                Thread.Sleep(25);
                Interlocked.Increment(ref completed);
                return new(first && fail ? 1 : 0, "", "");
            }
            finally { Interlocked.Decrement(ref active); }
        }
        var jobs = new[] {
            new CheckJob("false-ordinary", () => Studio(true), []) { ConflictGroup = "studio-writes", Priority = 2 },
            new CheckJob("true-sensitive", () => Studio(false), [], Exclusive: true) { ConflictGroup = "studio-writes", Lane = "sensitive-0" },
            new CheckJob("subset-sensitive", () => Studio(false), [], Exclusive: true) { ConflictGroup = "studio-writes", Lane = "sensitive-1" },
            new CheckJob("unrelated", () => { Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10))); return new(0, "", ""); }, [])
        };
        var rows = CheckScheduler.Run(jobs, 2, fixture.DirectoryPath,
            new Dictionary<string, int> { ["general"] = 2, ["sensitive-0"] = 1, ["sensitive-1"] = 1 });
        Assert.Equal(3, completed);
        Assert.Equal(fail ? "failed" : "passed", rows[0].Status);
        Assert.All(rows.Skip(1), row => Assert.Equal("passed", row.Status));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public void RequiredStudioJobCannotPassWithIncompleteOrFailedSensitiveResults(string failure)
    {
        using var fixture = new OfflineFixtures(); var jobs = new List<CheckJob>();
        ReleaseCommands.AddCheckAllStudioJobs(jobs, Repository.FindRoot(), fixture.DirectoryPath, "dotnet", history: Path.Combine(fixture.DirectoryPath, "history"));
        const string name = "studio-gui-true";
        foreach (int shard in new[] { 0, 1 })
        {
            string directory = Path.Combine(fixture.DirectoryPath, name, "shard-" + shard); Directory.CreateDirectory(directory);
            if (shard == 1 && failure == "missing") continue;
            WriteTrx(Path.Combine(directory, name + ".trx"), [failure == "duplicate" ? "Same.Case(0)" : "Case" + shard + ".Check(0)"],
                shard == 1 && failure == "failed" ? "Failed" : "Passed");
        }
        var gate = Assert.Single(jobs, job => job.Name == name);
        if (failure == "missing") Assert.Throws<ReleaseException>(() => gate.Run());
        else Assert.Equal(1, gate.Run().ExitCode);
    }

    private static void WriteTrx(string path, string[] identities, string outcome = "Passed")
    {
        DotnetSuiteSelfTests.SyntheticTrx(path, identities.Select(_ => outcome).ToArray());
        var doc = XDocument.Load(path); var ns = doc.Root!.Name.Namespace;
        var definitions = doc.Descendants(ns + "UnitTest").ToArray(); var results = doc.Descendants(ns + "UnitTestResult").ToArray();
        for (int i = 0; i < identities.Length; i++)
        {
            var identity = identities[i]; var method = identity[..identity.IndexOf('(')]; int dot = method.LastIndexOf('.');
            definitions[i].SetAttributeValue("id", identity);
            definitions[i].Element(ns + "TestMethod")!.SetAttributeValue("className", method[..dot]);
            definitions[i].Element(ns + "TestMethod")!.SetAttributeValue("name", method[(dot + 1)..]);
            results[i].SetAttributeValue("testId", identity); results[i].SetAttributeValue("testName", identity);
            results[i].SetAttributeValue("executionId", Guid.NewGuid().ToString("N"));
        }
        doc.Save(path);
    }
}
