using System.Diagnostics;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using TiaMcp.ReleaseTool;
using Xunit;
using Xunit.Abstractions;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class ParityKeyTests(ITestOutputHelper output)
{
    [Fact]
    public void ParityKeysRestoreOncePerPropertyGroupAndMatchSerialKeys()
    {
        using var fixture = new ParityFixture();
        var phase = new CheckAllLayout.KeyPhase(new());
        var actual = phase.Compute(fixture.Root, fixture.Units, "dotnet", fixture.Nuget, fixture.Api,
            fixture.Directory("keys"), 1, "keys");
        Assert.Equal(2, phase.Restores);
        // Three roots + one leaf, then two roots + one leaf. A root has
        // evaluation/reference/import probes, a leaf two, and each group one SDK probe.
        Assert.Equal(12 + 9, phase.ProbeProcesses);
        var serial = Stopwatch.StartNew();
        int serialProbes = 0;
        foreach (var unit in fixture.Units)
        {
            var args = CheckAllLayout.BaselineArguments(fixture.Root, unit, fixture.Nuget, fixture.Api);
            ProcessRunner.RequireSuccess(ProcessRunner.Run("dotnet", ReleaseCommands.CacheRestoreArguments(args[1], args), fixture.Root), "Serial parity restore");
            var memo = new BuildKeyMemo();
            Assert.Equal(ReleaseCommands.BuildKey("dotnet", args[1], args, fixture.Root, memo: memo).Key, actual[unit]);
            serialProbes += memo.ProbeProcesses;
        }
        output.WriteLine($"P-B fixture: grouped {phase.Seconds:F3}s/{phase.Restores} restores/{phase.ProbeProcesses} probes; serial {serial.Elapsed.TotalSeconds:F3}s/{fixture.Units.Length} restores/{serialProbes} probes.");
        Assert.Equal(30, serialProbes);
        Assert.All(actual.Values, key => Assert.Equal(64, key!.Length));
    }

    [Fact]
    public void WarmParityRunRestoresVerifiedInventoriesWithoutAnOrdinaryBuild()
    {
        using var fixture = new ParityFixture();
        var first = fixture.Verify("cold");
        Assert.Equal(fixture.Units.Length, (int)first["rebuilt"]!);
        var timer = Stopwatch.StartNew();
        var second = fixture.Verify("warm");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(60), timer.Elapsed.ToString());
        Assert.Equal(0, (int)second["rebuilt"]!);
        Assert.Equal(fixture.Units.Length, (int)second["cached"]!);
        Assert.All(second["units"]!.AsArray(), unit => Assert.Equal("restored", (string?)unit!["provenance"]));
        Assert.False(File.Exists(Path.Combine(fixture.Directory("warm"), "ordinary.proj")));
        Assert.False(File.Exists(Path.Combine(fixture.Directory("warm"), "ordinary-build.log")));
        var phases = fixture.Read("warm", "phases.json");
        Assert.Equal(2, (int)phases["keyRestores"]!);
        Assert.Equal(21, (int)phases["keyProbeProcesses"]!);
        Assert.Equal(0, (double)phases["afterKeySeconds"]!);
        Assert.Equal(0, (int)phases["afterKeyRestores"]!);
        var coldPhases = fixture.Read("cold", "phases.json");
        Assert.Equal(2, (int)coldPhases["afterKeyRestores"]!);
        Assert.Equal(21, (int)coldPhases["afterKeyProbeProcesses"]!);
        Assert.All(fixture.Read("warm", "comparison.json").AsArray(), row => Assert.Empty(row!["missing"]!.AsArray()));
        output.WriteLine($"Warm parity {timer.Elapsed.TotalSeconds:F3}s; cold keys {(double)coldPhases["keySeconds"]!:F3}s + afterKeys {(double)coldPhases["afterKeySeconds"]!:F3}s. P-A could remove these phases (42 probes, four restores cold; 21 probes, two restores warm).");

        // A restored manifest still compares every file against the current runtime.
        var runtimeFile = Path.Combine(fixture.Root, "runtime/A/default/A.dll");
        File.Delete(runtimeFile);
        var result = CheckAllLayout.Verify(fixture.Root, fixture.Units, "dotnet", fixture.Nuget, fixture.Api,
            fixture.Directory("missing"), fixture.Manifest, 1, independent: true);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("A.dll", result.StandardError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParityKeyPhaseFailsAtTheJobDeadlineInsteadOfSkippingCache(bool duringKey)
    {
        using var fixture = new ParityFixture();
        var now = DateTime.UtcNow; var end = now.AddMinutes(1);
        var deadline = new BuildUnitDeadline(("suite-output-parity", end), () => now);
        if (!duringKey) now = end;
        if (duringKey)
        {
            // Expire on the first hash, after the real group restore has finished.
            int checks = 0;
            deadline = new BuildUnitDeadline(("suite-output-parity", end), () => ++checks >= 6 ? end : now);
        }
        using var captured = new StringWriter(); var prior = Console.Out;
        try
        {
            Console.SetOut(captured);
            var error = Assert.Throws<ReleaseException>(() => CheckAllLayout.Verify(fixture.Root, fixture.Units, "dotnet", fixture.Nuget, fixture.Api,
                fixture.Directory("expired"), fixture.Manifest, 1, independent: true, budget: deadline));
            Assert.Contains("Check job timed out: suite-output-parity during build unit ParityKeys;", error.Message);
            Assert.DoesNotContain("Parity manifest cannot be cached", captured.ToString());
        }
        finally { Console.SetOut(prior); }
        var phases = fixture.Read("expired", "phases.json");
        Assert.Equal(duringKey ? 1 : 0, (int)phases["keyRestores"]!);
        Assert.Equal(0, (int)phases["keyProbeProcesses"]!);
        Assert.False((bool)phases["runtimeManifestRead"]!);
        Assert.False(File.Exists(Path.Combine(fixture.Directory("expired"), "ordinary.proj")));
    }

    [Fact]
    public void InputsChangedAfterTheOrdinaryBuildCannotPopulateAVerifiedManifest()
    {
        using var fixture = new ParityFixture();
        var report = fixture.Verify("changed", () =>
        {
            var path = Path.Combine(fixture.Root, "src/Lib/Input.cs");
            var stamp = File.GetLastWriteTimeUtc(path);
            File.AppendAllText(path, "// changed after the ordinary build\n"); File.SetLastWriteTimeUtc(path, stamp);
        });
        Assert.Equal(fixture.Units.Length, (int)report["rebuilt"]!);
        Assert.All(report["units"]!.AsArray(), row =>
        {
            Assert.Equal("cold", (string?)row!["provenance"]);
            Assert.Null(row["sourceRunId"]);
            Assert.False(File.Exists((string)row["manifest"]!));
            Assert.Empty(row["missing"]!.AsArray());
        });
        Assert.Equal(21, (int)fixture.Read("changed", "phases.json")["afterKeyProbeProcesses"]!);
    }

    [Fact]
    public async Task ExpiredSchedulerBudgetFailsTheJobAsParityKeysBeforeStartingAProcess()
    {
        using var fixture = new ParityFixture();
        var logs = fixture.Directory("scheduler");
        var row = await CheckScheduler.Start(new CheckJob("suite-output-parity", () =>
            CheckAllLayout.Verify(fixture.Root, fixture.Units, "dotnet", fixture.Nuget, fixture.Api, fixture.Directory("expired"),
                fixture.Manifest, independent: true, awaitInputs: () => { Thread.Sleep(10); return new(0, "", ""); }), [])
            { Timeout = TimeSpan.FromMilliseconds(1) }, logs);
        Assert.False(CheckTiming.Passed(row));
        Assert.Contains("during build unit ParityKeys; 5 of 5 units not completed", row.Error);
        Assert.DoesNotContain("Parity manifest cannot be cached", File.ReadAllText(Path.Combine(logs, "suite-output-parity.log")));
        Assert.False(File.Exists(Path.Combine(fixture.Directory("expired"), "ordinary.proj")));
    }

    [Fact]
    public void AfterKeysReevaluateOrdinaryImportsAndSourceBytesWithPreservedTimestamps()
    {
        using var fixture = new ParityFixture();
        var before = new CheckAllLayout.KeyPhase(new());
        var keys = before.Compute(fixture.Root, fixture.Units, "dotnet", fixture.Nuget, fixture.Api, fixture.Directory("before"), 1, "keys");
        var input = Path.Combine(fixture.Root, "src/Lib/Input.cs");
        var stamp = File.GetLastWriteTimeUtc(input);
        File.AppendAllText(input, "// changed\n"); File.SetLastWriteTimeUtc(input, stamp);
        fixture.Write("Directory.Build.targets", "<Project><PropertyGroup Condition=\"'$(CheckAllVariant)' == ''\"><DefineConstants>ORDINARY_ONLY</DefineConstants></PropertyGroup></Project>");
        var after = new CheckAllLayout.KeyPhase(new());
        var changed = after.Compute(fixture.Root, fixture.Units, "dotnet", fixture.Nuget, fixture.Api, fixture.Directory("after"), 1, "after", before.Memo);
        Assert.All(fixture.Units, unit => Assert.NotEqual(keys[unit], changed[unit]));
        Assert.Equal(2, after.Restores); Assert.Equal(21, after.ProbeProcesses);
    }

    private sealed class ParityFixture : IDisposable
    {
        private readonly OfflineFixtures fixture = new();
        internal string Root => fixture.DirectoryPath;
        internal string Nuget => Path.Combine(Root, "offline.config");
        internal string Api => Path.Combine(Root, "api");
        internal string Manifest => Path.Combine(Root, "bin-build/runtime-paths.tsv");
        internal CheckBuild[] Units { get; } = [
            new("tests/A/A.csproj", "", []), new("tests/B/B.csproj", "", []), new("tests/C/C.csproj", "", []),
            new("tests/D/D.csproj", "x", ["-p:DefineConstants=X"]), new("tests/A/A.csproj", "x", ["-p:DefineConstants=X"])
        ];

        internal ParityFixture()
        {
            Write(".gitignore", "bin-build/\nruntime/\n**/bin/\n**/obj/\n");
            Write("Directory.Build.props", "<Project><Import Project=\"src/Shared/ReproducibleBuild.props\" /></Project>");
            Write("src/Shared/ReproducibleBuild.props", File.ReadAllText(Path.Combine(Repository.FindRoot(), "src/Shared/ReproducibleBuild.props")));
            Write("offline.config", "<configuration><packageSources><clear /></packageSources></configuration>");
            Write("build-tools/bundled-dotnet.json", "{}"); Write("build-tools/release/orchestrator.cs", "// fixture orchestrator\n");
            Write("src/Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            Write("src/Lib/Input.cs", "public class Library { public const int Number = 42; }\n");
            foreach (var name in new[] { "A", "B", "C", "D" })
            {
                Write($"tests/{name}/{name}.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../../src/Lib/Lib.csproj\" /></ItemGroup></Project>");
                Write($"tests/{name}/Input.cs", "public class " + name + " { public int Read() => Library.Number; }\n");
            }
            System.IO.Directory.CreateDirectory(Api);
            ProcessRunner.RequireSuccess(ProcessRunner.Run("git", ["init", "--quiet", Root], Root), "Parity fixture repository");
        }

        internal string Directory(string name) => Path.Combine(Root, "bin-build", name);
        internal JsonNode Read(string name, string file) => JsonNode.Parse(File.ReadAllText(Path.Combine(Directory(name), file)))!;
        internal void Write(string relative, string content)
        {
            var path = Path.Combine(Root, relative); System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content);
        }
        internal JsonNode Verify(string name, Action? beforeComparison = null)
        {
            var result = CheckAllLayout.Verify(Root, Units, "dotnet", Nuget, Api, Directory(name), Manifest, 1, independent: true, awaitRuntime: () =>
            {
                if (File.Exists(Manifest)) return new(0, "", "");
                var rows = Units.Select(unit =>
                {
                    var receipt = Path.Combine(Directory(name), CheckAllLayout.InputKey([], [unit.Project, unit.Variant]) + ".tsv");
                    var lines = File.ReadAllLines(receipt);
                    var output = Path.Combine(Root, "runtime", Path.GetFileNameWithoutExtension(unit.Project), unit.Variant);
                    foreach (var file in System.IO.Directory.EnumerateFiles(lines[1], "*", SearchOption.AllDirectories))
                    {
                        var target = Path.Combine(output, Path.GetRelativePath(lines[1], file));
                        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true);
                    }
                    return string.Join('\t', Path.Combine(Root, unit.Project), unit.Variant, output, output,
                        Path.Combine(output, Path.GetFileNameWithoutExtension(unit.Project) + ".dll"));
                }).ToArray();
                File.WriteAllLines(Manifest, rows);
                beforeComparison?.Invoke();
                return new(0, "", "");
            });
            Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
            return Read(name, "inventory-cache.json");
        }
        public void Dispose() => fixture.Dispose();
    }
}
