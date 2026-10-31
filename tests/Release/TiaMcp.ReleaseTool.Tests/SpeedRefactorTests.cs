using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class SpeedRefactorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CandidateRunsStaticCategoryOnceAndWritesEveryNamedReference(int sharedExitCode)
    {
        using var fixture = new OfflineFixtures(); string root = fixture.DirectoryPath;
        var selected = ReleaseCheckPolicy.Load(Repository.FindRoot()).SelfTests.Where(ReleaseCommands.SharesStaticSelfTests).Select(test => test.Check)
            .Append("toolslist-self-test").ToArray();
        int common = 0, generators = 0;
        var steps = new List<string>();
        void Execute() => ReleaseCommands.RunCandidateChecks(new("quick", selected, [], []), root, root, root, root, "dotnet", "python", 2,
            (name, action) => { if (name != "11-product-smoke") { steps.Add(name); action(); } }, (_, _) => throw new InvalidOperationException("No Python selected"),
            (_, _) => throw new InvalidOperationException("No defaults selected"), (name, _, args, _, _) =>
            {
                if (name == "static-check-self-tests")
                {
                    common++; var command = args.ToArray();
                    var directory = command[Array.IndexOf(command, "--results-directory") + 1];
                    DotnetSuiteSelfTests.SyntheticTrx(Path.Combine(directory, "static-checks.trx"), Enumerable.Repeat("Passed", 331).ToArray());
                }
                else { Assert.Equal("toolslist-self-test", name); Assert.Equal(1, common); generators++; }
                return new(name == "static-check-self-tests" ? sharedExitCode : 0, "self-tests completed\n", "");
            });
        if (sharedExitCode == 0) Execute();
        else Assert.Throws<ReleaseException>(Execute);
        Assert.Equal(1, common); Assert.Equal(1, generators);
        Assert.Equal(selected.Order(), steps.Order());
        var references = Directory.GetFiles(root, "*.shared-result.json");
        Assert.Equal(selected.Length - 1, references.Length);
        Assert.Single(references.Select(path => (string)JsonNode.Parse(File.ReadAllText(path))!["sharedTrx"]!).Distinct());
    }

    [Fact]
    public void OnlyIdenticalStaticChecksWrappersShareAndGeneratorsStaySeparate()
    {
        var tests = ReleaseCheckPolicy.Load(Repository.FindRoot()).SelfTests;
        var shared = tests.Where(ReleaseCommands.SharesStaticSelfTests).ToArray();
        Assert.Equal(10, shared.Length);
        Assert.Contains(shared, test => test.Check == "dead-tool-references-self-test");
        Assert.DoesNotContain(shared, test => test.Check == "toolslist-self-test");
        var example = shared[0];
        Assert.False(ReleaseCommands.SharesStaticSelfTests(example with { Arguments = ["-OtherEnvironment"] }));
        Assert.False(ReleaseCommands.SharesStaticSelfTests(example with { Command = ["check-repository", "-SelfTest", "--filter", "Category=Other"] }));
        Assert.Contains("Category=StaticChecks", ReleaseCommands.StaticSelfTestArguments("results"));
        Assert.Contains("trx;LogFileName=static-checks.trx", ReleaseCommands.StaticSelfTestArguments("results"));
    }

    [Fact]
    public void HardLinkAllowListKeepsWritableInputsPrivateAndFallsBackToCopies()
    {
        using var tree = new OfflineFixtures();
        string source = Path.Combine(tree.DirectoryPath, "source"), destination = Path.Combine(tree.DirectoryPath, "linked");
        File.WriteAllBytes(source, [1, 2, 3]);
        var linked = SuiteWorkerCopies.CopyFile(source, destination, "System.Text.Json.dll");
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(destination));
        Assert.Contains(linked, new[] { "hard-link", "copy" });
        string fallback = Path.Combine(tree.DirectoryPath, "fallback");
        Assert.Equal("copy", SuiteWorkerCopies.CopyFile(source, fallback, "System.Text.Json.dll", (_, _) => false));
        foreach (string relative in new[] { "TiaMcp.FoundationHost.Tests.dll", "TiaMcp.Adapter.21.dll", "fixture.json", "host.runtimeconfig.json", Path.Combine("data", "System.Text.Json.dll") })
        {
            string copied = Path.Combine(tree.DirectoryPath, Guid.NewGuid().ToString("N"));
            Assert.Equal("copy", SuiteWorkerCopies.CopyFile(source, copied, relative, (_, _) => throw new InvalidOperationException("Writable input must never link")));
            File.WriteAllBytes(copied, [9]); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(source));
        }
        File.WriteAllBytes(fallback, [9]); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(source));
    }

    [Fact]
    public void FileProgramReceiptRequiresSuccessfulMatchingSdkInputsOutputsAndReports()
    {
        using var tree = new OfflineFixtures(); string root = tree.DirectoryPath;
        var output = Path.Combine(root, "app.dll"); File.WriteAllText(output, "built");
        var report = Path.Combine(root, "app.sarif"); File.WriteAllText(report, "diagnostics");
        var receipt = Path.Combine(root, "success.json"); var destination = Path.Combine(root, "reports"); Directory.CreateDirectory(destination);
        void Remember() => File.WriteAllText(receipt, JsonSerializer.Serialize(new FileBasedPrograms.Receipt("sdk-and-content", output,
            new() { [output] = ReleaseRecords.HashFile(output) }, new() { [report] = ReleaseRecords.HashFile(report) })));
        Assert.False(FileBasedPrograms.Reuse(receipt, "sdk-and-content", destination));
        Remember(); Assert.True(FileBasedPrograms.Reuse(receipt, "sdk-and-content", destination));
        Assert.Equal("diagnostics", File.ReadAllText(Path.Combine(destination, "app.sarif")));
        Assert.False(FileBasedPrograms.Reuse(receipt, "another-sdk-or-input", destination));
        File.AppendAllText(output, "changed"); Assert.False(FileBasedPrograms.Reuse(receipt, "sdk-and-content", destination));
        Remember(); File.AppendAllText(report, "changed"); Assert.False(FileBasedPrograms.Reuse(receipt, "sdk-and-content", destination));
        Remember(); File.Delete(output); Assert.False(FileBasedPrograms.Reuse(receipt, "sdk-and-content", destination));
        File.WriteAllText(receipt, "invalid JSON"); Assert.False(FileBasedPrograms.Reuse(receipt, "sdk-and-content", destination));
    }

    [Fact]
    public void FileProgramsWithProjectReferencesKeepOneWriterLane()
    {
        using var tree = new StaticTree();
        tree.Write("scripts/a.cs", "#:project ../shared/shared.csproj\nConsole.WriteLine(1);\n");
        tree.Write("scripts/b.cs", "#:project ../shared/shared.csproj\nConsole.WriteLine(2);\n");
        tree.Write("scripts/c.cs", "Console.WriteLine(3);\n");
        tree.Write("scripts/d.cs", "Console.WriteLine(4);\n");
        var groups = FileBasedPrograms.BuildGroups(tree.Root, ["scripts/a.cs", "scripts/b.cs", "scripts/c.cs", "scripts/d.cs"]);
        Assert.Equal(3, groups.Length);
        Assert.Equal(new[] { "scripts/a.cs", "scripts/b.cs" }, groups[0]);
        Assert.All(groups.Skip(1), group => Assert.Single(group));
    }

    [Fact]
    public void FileProgramBuildsAreBoundedAndReuseOnlyMatchingSuccessfulOutputs()
    {
        using var tree = new StaticTree();
        tree.Write("scripts/a.cs", "Console.WriteLine(1);\n"); tree.Write("scripts/b.cs", "Console.WriteLine(2);\n");
        int builds = 0, active = 0, peak = 0;
        string sdk = "10.0.403";
        CommandResult Run(string _, IEnumerable<string> arguments, string __, IDictionary<string, string?>? environment)
        {
            var args = arguments.ToArray(); if (args[0] == "--version") return new(0, sdk, "");
            Assert.Equal("1", environment!["MSBUILDDISABLENODEREUSE"]);
            Assert.Contains("-t:Build", args); Assert.Contains("-getProperty:TargetPath,IntermediateOutputPath", args);
            int running = Interlocked.Increment(ref active); Interlocked.Increment(ref builds);
            lock (tree) peak = Math.Max(peak, running);
            Thread.Sleep(50);
            string name = Path.GetFileNameWithoutExtension(args[1]);
            var target = tree.Write("bin-build/payload/" + name + "/bin/app.dll", name + sdk);
            var intermediate = Path.GetDirectoryName(tree.Write("bin-build/payload/" + name + "/obj/input", "generated"))!;
            var reports = environment["TIA_MCP_ANALYZER_REPORT_DIRECTORY"]!;
            File.WriteAllText(Path.Combine(reports, name + ".sarif"), "diagnostics");
            File.WriteAllText(Path.Combine(reports, name + ".inputs"), "inputs");
            Interlocked.Decrement(ref active);
            return new(0, "compiler warning retained in log\n" + JsonSerializer.Serialize(new { Properties = new { TargetPath = target, IntermediateOutputPath = intermediate } }), "");
        }
        CommandResult Build(string name, bool disabled = false) => FileBasedPrograms.Build(tree.Root, Path.Combine(tree.Root, "bin-build", name), "dotnet-fixture", null, 2, disabled, Run);
        Assert.Equal(0, Build("first").ExitCode); Assert.Equal(2, builds); Assert.InRange(peak, 1, 2);
        Assert.Equal(0, Build("second").ExitCode); Assert.Equal(2, builds);
        tree.Write("scripts/a.cs", "Console.WriteLine(3);\n");
        Assert.Equal(0, Build("content-change").ExitCode); Assert.Equal(3, builds);
        sdk = "next-sdk"; Assert.Equal(0, Build("sdk-change").ExitCode); Assert.Equal(5, builds);
        tree.Write("bin-build/payload/a/bin/app.dll", "tampered");
        Assert.Equal(0, Build("tampered").ExitCode); Assert.Equal(6, builds);
        Assert.Equal(0, Build("no-cache", true).ExitCode); Assert.Equal(8, builds);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(tree.Root, "bin-build/second"), "*.sarif").Length);
    }

    [Fact]
    public void ParityTestsUseBothExactBuiltVariantsWithoutAnyBuildWrites()
    {
        using var tree = new OfflineFixtures(); string root = tree.DirectoryPath;
        var manifest = Path.Combine(root, "runtime.tsv");
        File.WriteAllLines(manifest, new[] { "20", "21" }.Select(major =>
        {
            var directory = Path.Combine(root, major); Directory.CreateDirectory(directory);
            var assembly = Path.Combine(directory, "TiaMcp.FoundationHost.Tests.dll"); File.WriteAllText(assembly, major);
            return string.Join('\t', Path.Combine(root, SuiteWorkerCopies.FoundationProject), HostParity.Unit(major).Variant, directory, directory, assembly);
        }));
        Assert.Contains("-p:DefineConstants=TIA_V20", HostParity.Unit("20").Properties);
        Assert.Empty(HostParity.Unit("21").Properties);
        Parallel.ForEach(new[] { "20", "21" }, major =>
        {
            var directory = Path.Combine(root, "results-" + major);
            var options = Options.Parse(["-EngineMajor", major, "-ResultsDirectory", directory, "-NoBuild", "-RuntimeManifest", manifest], new HashSet<string> { "NoBuild" });
            var result = HostParity.Run(root, options, FastSuites.EnvironmentFor(directory), (_, args, working, environment) =>
            {
                Assert.Equal(directory, working);
                var command = args.ToArray(); Assert.Equal("vstest", command[0]);
                Assert.Equal(Path.Combine(root, major, "TiaMcp.FoundationHost.Tests.dll"), command[1]);
                Assert.Contains("--TestCaseFilter:FullyQualifiedName~BehaviorParityTests", command);
                Assert.Equal(major, environment!["TIA_MCP_PARITY_ENGINE_RELEASE"]);
                DotnetSuiteSelfTests.SyntheticTrx(Path.Combine(directory, "host-behavior-parity.trx"), Enumerable.Repeat("Passed", 103).ToArray());
                return new(0, "", "");
            });
            Assert.Equal(0, result.ExitCode);
        });
        Assert.Equal(2, Directory.GetFiles(root, "host-behavior-parity.json", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void SharedEngineHostReceiptIsWrittenOnlyAfterSuccessfulGateAndBindsBuiltBytes()
    {
        using var tree = new StaticTree();
        var project = "tests/engine/engine.csproj"; tree.Write(project, "<Project/>");
        var suite = new JsonObject { ["project"] = project, ["arguments"] = new JsonArray(), ["minimumPassed"] = 1, ["maximumSkipped"] = 0 };
        tree.Write("tests/test-suites.json", new JsonObject { ["engine-host"] = suite }.ToJsonString());
        var assembly = tree.Write("built/engine.dll", "binary");
        var directory = Path.GetDirectoryName(assembly)!;
        var manifest = tree.Write("runtime.tsv", string.Join('\t', Path.Combine(tree.Root, project), "default", directory, directory, assembly));
        var output = Path.Combine(tree.Root, "run");
        tree.Write("run/suites/engine-host.json", "{}"); tree.Write("run/suites/engine-host.trx", "trx");
        int code = 1;
        var jobs = new List<CheckJob> { new("engine-host-gate", () => new(code, "gate", ""), ["engine-host-shard-0"]) };
        ReleaseCommands.BindEngineHostResult(jobs, tree.Root, output, manifest);
        Assert.Equal(new[] { "engine-host-shard-0" }, jobs[0].Dependencies);
        Assert.Equal(new[] { "engine-host-gate", "engine-host-shard-0" }, jobs[1].Dependencies);
        Assert.False(jobs[1].AllowFailedDependencies);
        var path = Path.Combine(output, "suites/engine-host.reference.json");
        Assert.Equal(1, jobs[0].Run().ExitCode); Assert.False(File.Exists(path));
        code = 0; Assert.Equal(0, jobs[0].Run().ExitCode);
        Assert.Equal(0, jobs[1].Run().ExitCode);
        var proof = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(ReleaseRecords.HashFile(manifest), (string)proof["runtimeManifestSha256"]!);
        Assert.Equal(ReleaseRecords.HashFile(assembly), (string)proof["binaries"]![0]!["sha256"]!);
        code = 1; Assert.Equal(1, jobs[0].Run().ExitCode); Assert.False(File.Exists(path));
    }
}
