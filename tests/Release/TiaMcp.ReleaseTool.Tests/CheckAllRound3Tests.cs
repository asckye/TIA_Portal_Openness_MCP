using System.Diagnostics;
using System.Text;
using TiaMcp.BuildCommon;
using TiaMcp.Engine.Tests.Shared;
using Xunit;
using Xunit.Abstractions;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class CheckAllRound3Tests(ITestOutputHelper output)
{
    private static CommandResult Passed() => new(0, "passed\n", "");

    [Fact]
    public void IsolatedLanesOverlapEvenWhenGeneralWorkIsExclusive()
    {
        using var fixture = new OfflineFixtures(); using var barrier = new Barrier(3);
        var lanes = new Dictionary<string, int> { ["general"] = 1, ["stability"] = 2 };
        CommandResult Run() { Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10))); return Passed(); }
        var rows = CheckScheduler.Run([
            new("suite-and-capture", Run, [], Exclusive: true),
            new CheckJob("stability-one", Run, []) { Lane = "stability" },
            new CheckJob("stability-two", Run, []) { Lane = "stability" }], 1, fixture.DirectoryPath, lanes);
        Assert.All(rows, row => Assert.Equal("passed", row.Status));
        Assert.True(rows.Max(row => row.StartedAtUtc) < rows.Min(row => row.StartedAtUtc.AddSeconds(row.Seconds)));
    }

    [Fact]
    public void FailedShardStillMergesWhileOrdinaryDependentsRemainBlocked()
    {
        using var fixture = new OfflineFixtures(); bool merged = false;
        var rows = CheckScheduler.Run([
            new("shard", () => new(1, "", "assertion failed"), []),
            new("capture", () => throw new Exception("must not run"), ["shard"]),
            new CheckJob("merge", () => { merged = true; return new(1, "partial results retained", ""); }, ["shard"]) { AllowFailedDependencies = true }], 2, fixture.DirectoryPath);
        Assert.True(merged); Assert.Equal(new[] { "failed", "blocked", "failed" }, rows.Select(row => row.Status));
    }

    [Fact]
    public void CheapCatalogPrerequisitesStartBeforeLongIndependentSuites()
    {
        using var fixture = new OfflineFixtures(); var order = new List<string>();
        CommandResult Run(string name) { order.Add(name); return Passed(); }
        var jobs = new[] { new CheckJob("suite", () => Run("suite"), []) { EstimatedSeconds = 50 },
            new CheckJob("catalog", () => Run("catalog"), []) { EstimatedSeconds = 1 },
            new CheckJob("capture", () => Run("capture"), ["catalog"]) { EstimatedSeconds = 60 } };
        CheckScheduler.Run(jobs, 1, fixture.DirectoryPath);
        Assert.Equal(new[] { "catalog", "capture", "suite" }, order);
    }

    [Fact]
    public void ExplicitParallelismOverridesOldNonIdleCpuMeasurements()
    {
        using var fixture = new OfflineFixtures(); var directory = Path.Combine(fixture.DirectoryPath, "bin-build/check-timings"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "cpu-budget.json");
        File.WriteAllText(path, "{\"logicalCores\":" + Environment.ProcessorCount + ",\"parallelism\":8}");
        Assert.Equal(16, FastSuites.CpuParallelism(fixture.DirectoryPath, Options.Parse(["-MaxParallelism", "16"], new HashSet<string>())));
        Assert.Equal(Environment.ProcessorCount, FastSuites.CpuParallelism(fixture.DirectoryPath, Options.Parse([], new HashSet<string>())));
        File.WriteAllText(path, "{\"logicalCores\":" + Environment.ProcessorCount + ",\"parallelism\":8,\"representativeIdleMeasurement\":true}");
        Assert.Equal(8, FastSuites.CpuParallelism(fixture.DirectoryPath, Options.Parse([], new HashSet<string>())));
    }

    [Theory]
    [InlineData(0xffffffUL, 8, 0xffUL, 0xffff00UL)]
    [InlineData(0xf0f0UL, 4, 0xf0UL, 0xf000UL)]
    [InlineData(0x8000000000000001UL, 1, 1UL, 0x8000000000000000UL)]
    public void AffinityMasksAreDisjointAndCoverAllAvailableCores(ulong available, int count, ulong stability, ulong general)
    {
        Assert.Equal((stability, general), CheckAffinity.Split(available, count));
        Assert.Equal(0UL, stability & general); Assert.Equal(available, stability | general);
        Assert.Throws<ReleaseException>(() => CheckAffinity.Split(available, 0));
        Assert.Throws<ReleaseException>(() => CheckAffinity.Split(available, 64));
        Assert.Equal((0x33UL, 0xccUL), CheckAffinity.SplitPreferred(0xff, 4, [0x3, 0x30, 0xc, 0xc0]));
        Assert.Throws<ReleaseException>(() => CheckAffinity.SplitPreferred(0xff, 3, [0x3, 0x30, 0xc, 0xc0]));
    }

    [Fact]
    public void FailedBuildPreventsEveryPostBuildCheck()
    {
        var rows = new[] { new CheckTiming("generate-catalogs", "passed", 0, 1, null), new CheckTiming("build-once", "failed", 1, 1, "CS2012") };
        Assert.False(ReleaseCommands.CheckAllPrerequisitesPassed(rows));
        Assert.True(ReleaseCommands.CheckAllPrerequisitesPassed(rows.Take(1)));
        Assert.False(ReleaseCommands.CheckAllPrerequisitesPassed([new("generate-tool-usage", "blocked", 1, 0, "failed prerequisite")]));
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        File.WriteAllText(Path.Combine(root, "fake.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(root, "Program.cs"), "using System.IO; if (args[0] == \"msbuild\") return 1; if (args.Length > 1 && args[1].StartsWith(\"generate-\")) return 0; File.WriteAllText(\"unexpected-check\", string.Join(\"|\", args)); return 2;");
        File.WriteAllText(Path.Combine(root, "offline.config"), "<configuration><packageSources><clear /></packageSources></configuration>");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        ProcessRunner.RequireSuccess(ProcessRunner.Run(dotnet, ["build", Path.Combine(root, "fake.csproj"), "-c", "Release", "-m:1", "-nodeReuse:false",
            "-p:RestoreConfigFile=" + Path.Combine(root, "offline.config"), "-p:UseSharedCompilation=false"], root), "fake compiler fixture");
        var runDirectory = Path.Combine(root, "failed-run");
        using var current = Process.GetCurrentProcess();
        ulong available = OperatingSystem.IsWindows() ? unchecked((ulong)current.ProcessorAffinity.ToInt64())
            : throw new PlatformNotSupportedException("Windows check-all fixture");
        int reserved = CheckAffinity.PhysicalCores().Where(core => (core.Mask & available) != 0)
            .Min(core => System.Numerics.BitOperations.PopCount(core.Mask & available));
        var result = ProcessRunner.Run(dotnet, [typeof(ReleaseCommands).Assembly.Location, "check-all", "-PublicApiRoot", root,
            "-NuGetConfig", Path.Combine(root, "offline.config"), "-Dotnet", Path.Combine(root, "bin/Release/net10.0/fake.exe"),
            "-CompanionPython", Path.Combine(root, "bin/Release/net10.0/fake.exe"), "-StabilityCores", reserved.ToString(),
            "-BuildParallelism", "1", "-MaxParallelism", "16", "-OutputDirectory", runDirectory], Repository.FindRoot());
        output.WriteLine("Fake build: logical cores={0}, available mask={1:X}, reserved cores={2}", Environment.ProcessorCount, available, reserved);
        output.WriteLine(result.StandardOutput + result.StandardError);
        Assert.Equal(1, result.ExitCode);
        var checks = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(runDirectory, "report.json")))!["checks"]!.AsArray();
        Assert.Equal(new[] { "generate-catalogs", "generate-tool-usage", "build-once", "tracked-tree-drift" }, checks.Select(row => (string)row!["Name"]!));
        Assert.Equal("failed", (string)checks[2]!["Status"]!);
        Assert.False(File.Exists(Path.Combine(Repository.FindRoot(), "unexpected-check")));
    }

    [Fact]
    public void CompanionPythonOptionIsResolvedAndNotReplacedByIsolation()
    {
        using var fixture = new OfflineFixtures(); var python = Path.Combine(fixture.DirectoryPath, "companion python.exe"); File.WriteAllText(python, "fixture");
        var options = Options.Parse(["-CompanionPython", python], new HashSet<string>());
        Assert.Equal(python, ReleaseCommands.CheckAllCompanionPython(options));
        Assert.Throws<ReleaseException>(() => ReleaseCommands.CheckAllCompanionPython(Options.Parse(["-CompanionPython", python + ".missing"], new HashSet<string>())));
    }

    [Fact]
    public void FourLogicalCoresWithSmtRejectTheDefaultSingleCoreReservationBeforeBuild()
    {
        int defaultReservation = Math.Min(8, Math.Max(1, 4 / 3));
        Assert.Equal(1, defaultReservation);
        var error = Assert.Throws<ReleaseException>(() => CheckAffinity.SplitPreferred(0xf, defaultReservation, [0x3, 0xc]));
        Assert.Equal(64, error.ExitCode);
        Assert.Equal((0x3UL, 0xcUL), CheckAffinity.SplitPreferred(0xf, 2, [0x3, 0xc]));
    }

    [Fact]
    public void PreflightBuildVariantPreservesItsPropertiesAndAllSuiteGates()
    {
        var catalog = DotnetSuites.LoadCatalog(Path.Combine(Repository.FindRoot(), "tests/test-suites.json"), Repository.FindRoot());
        var original = catalog["release-tool"]!.ToJsonString(); var suite = ReleaseCommands.CheckAllPreflightSuite(catalog);
        Assert.Equal(original, catalog["release-tool"]!.ToJsonString());
        Assert.Equal(catalog["release-tool"]!["minimumPassed"]!.ToJsonString(), suite["minimumPassed"]!.ToJsonString());
        Assert.Equal(catalog["release-tool"]!["maximumSkipped"]!.ToJsonString(), suite["maximumSkipped"]!.ToJsonString());
        Assert.Equal(new[] { "-p:UseAppHost=false", "-p:UseSharedCompilation=false" }, CheckAllBuild.Suite(suite).Properties);
    }

    [Fact]
    public void TestRuntimeUsesTheExactVariantAssemblyInsteadOfLastStagedOutput()
    {
        using var fixture = new OfflineFixtures(); var directory = Path.Combine(fixture.DirectoryPath, "bin-build/check-all"); Directory.CreateDirectory(directory);
        var one = new CheckBuild("tests/test.csproj", "", ["-p:DefineConstants=ONE"]); var two = new CheckBuild("tests/test.csproj", "", ["-p:DefineConstants=TWO"]);
        var project = Path.Combine(fixture.DirectoryPath, "tests/test.csproj");
        File.WriteAllLines(Path.Combine(directory, "runtime-paths.tsv"), new[] { project + "\t" + one.Variant + "\tone\tlegacy\tone.dll", project + "\t" + two.Variant + "\ttwo\tlegacy\ttwo.dll" });
        var manifest = Path.Combine(directory, "runtime-paths.tsv");
        Assert.Equal("one.dll", CheckAllBuild.TestAssembly(fixture.DirectoryPath, one, manifest)); Assert.Equal("two.dll", CheckAllBuild.TestAssembly(fixture.DirectoryPath, two, manifest));
        Assert.Throws<ReleaseException>(() => CheckAllBuild.TestAssembly(fixture.DirectoryPath, new("tests/other.csproj", "", []), manifest));
        var cli = Path.Combine(fixture.DirectoryPath, "build-tools/release/TiaMcp.ReleaseTool.csproj");
        var cliOutput = Path.Combine(fixture.DirectoryPath, "build-tools/release/bin/Release/net10.0"); Directory.CreateDirectory(cliOutput);
        var cliDll = Path.Combine(cliOutput, "TiaMcp.ReleaseTool.dll"); File.WriteAllText(cliDll, "running CLI");
        var compiled = Path.Combine(fixture.DirectoryPath, "compiled-cli"); Directory.CreateDirectory(compiled); File.WriteAllText(Path.Combine(compiled, "TiaMcp.ReleaseTool.dll"), "variant CLI");
        var tests = Path.Combine(fixture.DirectoryPath, "compiled-tests"); Directory.CreateDirectory(tests); File.WriteAllText(Path.Combine(tests, "test.dll"), "compiled tests");
        var destination = Path.Combine(fixture.DirectoryPath, "tests/bin");
        File.WriteAllLines(Path.Combine(directory, "runtime-paths.tsv"), new[] { cli + "\tdefault\t" + compiled + "\t" + cliOutput + "\t" + Path.Combine(compiled, "TiaMcp.ReleaseTool.dll"),
            project + "\t" + one.Variant + "\t" + tests + "\t" + destination + "\t" + Path.Combine(tests, "test.dll") });
        using var locked = new FileStream(cliDll, FileMode.Open, FileAccess.Read, FileShare.Read);
        ReleaseCommands.PrepareCheckAllRuntime(fixture.DirectoryPath, [one], Path.Combine(directory, "runtime-paths.tsv"));
        Assert.Equal("running CLI", File.ReadAllText(cliDll)); Assert.Equal("compiled tests", File.ReadAllText(Path.Combine(destination, "test.dll")));
    }

    [Fact]
    public void AffinityJobIncludesClientChildAndImmediatelySpawnedGrandchild()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows job object verification");
        using var fixture = new OfflineFixtures(); using var process = Process.GetCurrentProcess();
        ulong available = unchecked((ulong)process.ProcessorAffinity.ToInt64()); ulong mask = available & (~available + 1);
        string Print() => "[Diagnostics.Process]::GetCurrentProcess().ProcessorAffinity.ToInt64(); $env:TIA_MCP_PLC_TOOLS_PYTHON";
        var grandchild = Path.Combine(fixture.DirectoryPath, "grandchild.ps1");
        var child = Path.Combine(fixture.DirectoryPath, "child.ps1");
        var parent = Path.Combine(fixture.DirectoryPath, "parent.ps1");
        File.WriteAllText(grandchild, Print());
        File.WriteAllText(child, Print() + "; & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File '" + grandchild.Replace("'", "''") + "'");
        File.WriteAllText(parent, Print() + "; & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File '" + child.Replace("'", "''") + "'");
        using var affinity = CheckAffinity.Enter(mask);
        var result = ProcessRunner.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", parent], fixture.DirectoryPath,
            new Dictionary<string, string?> { ["TIA_MCP_PLC_TOOLS_PYTHON"] = "companion python.exe" }, timeoutMilliseconds: 30000, waitForDescendants: true);
        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Equal(Enumerable.Range(0, 3).SelectMany(_ => new[] { mask.ToString(), "companion python.exe" }), result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void AffinityJobWaitsForOwnedDescendantsAfterTheirParentExits()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows job object verification");
        using var fixture = new OfflineFixtures(); using var current = Process.GetCurrentProcess();
        ulong available = unchecked((ulong)current.ProcessorAffinity.ToInt64()); ulong mask = available & (~available + 1);
        string Quote(string text) => "'" + text.Replace("'", "''") + "'";
        var child = Path.Combine(fixture.DirectoryPath, "owned-child.ps1");
        var ready = Path.Combine(fixture.DirectoryPath, "child-ready.txt");
        var parent = Path.Combine(fixture.DirectoryPath, "owned-parent.ps1");
        File.WriteAllText(child, "$PID | Set-Content -LiteralPath " + Quote(ready) + "; Start-Sleep -Seconds 60");
        File.WriteAllText(parent, "$child = Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', " +
            Quote("\"" + child + "\"") + "); while (!(Test-Path -LiteralPath " + Quote(ready) + ")) { Start-Sleep -Milliseconds 25 }; $child.Id");
        using var affinity = CheckAffinity.Enter(mask);
        try
        {
            var result = ProcessRunner.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", parent], fixture.DirectoryPath,
                timeoutMilliseconds: 30000, waitForDescendants: true);
            Assert.True(result.ExitCode == 0, result.StandardError);
            int pid = int.Parse(result.StandardOutput.Trim());
            Assert.Equal(pid, int.Parse(File.ReadAllText(ready).Trim()));
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
        }
        catch (Exception ex) { output.WriteLine(ex.ToString()); throw; }
    }

    [Fact]
    public void ActualTraversalIsolatesGlobalPropertyVariantsOfSharedDependencies()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath; var repo = Repository.FindRoot();
        void Write(string relative, string content) { var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); }
        foreach (var file in new[] { "CheckAll.Build.props", "CheckAll.Build.targets", "check-build/TiaMcp.CheckBuild.csproj", "check-build/CheckBuildTasks.cs" })
            Write("build-tools/release/" + file, File.ReadAllText(Path.Combine(repo, "build-tools/release", file)));
        Write("build-tools/native-call-weaver/NativeCallWeaver.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Write("contracts/TiaMcp.Adapters.Contracts.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile></PropertyGroup></Project>");
        var originalLock = "{\"version\":1,\"dependencies\":{\"net10.0\":{}}}\n";
        Write("contracts/packages.lock.json", originalLock);
        Write("contracts/Value.cs", "public class Value {\n#if ONE\npublic const int Number = 1;\n#else\npublic const int Number = 2;\n#endif\n}\n");
        Write("entry/entry.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../contracts/TiaMcp.Adapters.Contracts.csproj\" /></ItemGroup></Project>");
        Write("offline.config", "<configuration><packageSources><clear /></packageSources></configuration>");
        var builds = new[] { new CheckBuild("entry/entry.csproj", "same", ["-p:DefineConstants=ONE"]), new CheckBuild("entry/entry.csproj", "same", ["-p:DefineConstants=TWO"]) };
        var directory = Path.Combine(root, "report");
        var result = CheckAllBuild.Run(root, builds, directory, "dotnet", null, Path.Combine(root, "offline.config"), 1, false);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        var assemblies = Directory.GetFiles(Path.Combine(root, "contracts/bin/check-all"), "TiaMcp.Adapters.Contracts.dll", SearchOption.AllDirectories);
        Assert.Equal(2, assemblies.Length); Assert.NotEqual(ReleaseRecords.HashFile(assemblies[0]), ReleaseRecords.HashFile(assemblies[1]));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "contracts/obj/check-all"), "check-global-properties.txt", SearchOption.AllDirectories).Length);
        var timestamps = assemblies.Select(File.GetLastWriteTimeUtc).ToArray();
        result = CheckAllBuild.Run(root, builds, Path.Combine(root, "warm"), "dotnet", null, Path.Combine(root, "offline.config"), 1, false);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        Assert.Equal(timestamps, assemblies.Select(File.GetLastWriteTimeUtc));
        Assert.Equal(originalLock, File.ReadAllText(Path.Combine(root, "contracts/packages.lock.json")));
    }

    [Fact]
    public void RestoreCoversTransitiveReferencesAfterLocalGlobalPropertyOverrides()
    {
        using var fixture = new OfflineFixtures();
        VerifyTransitiveRestore(fixture.DirectoryPath);
    }

    [Fact]
    public void LongCleanLayoutKeepsGeneratedInputsIncremental()
    {
        var ordinary = Path.Combine(Repository.FindRoot(), "bin-build", "restore-long-" + new string('0', 32));
        using var fixture = new OfflineFixtures("restore-long-" + new string('x', Math.Max(0, 118 - ordinary.Length)));
        Assert.True(fixture.DirectoryPath.Length >= 118);
        VerifyTransitiveRestore(fixture.DirectoryPath, verifyInvalidation: true);
    }

    private static void VerifyTransitiveRestore(string root, bool verifyInvalidation = false)
    {
        var repo = Repository.FindRoot();
        void Write(string relative, string content) { var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); }
        foreach (var file in new[] { "CheckAll.Build.props", "CheckAll.Build.targets", "check-build/TiaMcp.CheckBuild.csproj", "check-build/CheckBuildTasks.cs" })
            Write("build-tools/release/" + file, File.ReadAllText(Path.Combine(repo, "build-tools/release", file)));
        Write("build-tools/native-call-weaver/NativeCallWeaver.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        // Match adapters that disable Directory.Build imports before SDK props.
        const string leafProject = "leaf/TiaMcp.Restore.Transitive.Property.Variant.Dependency.csproj";
        Write(leafProject, "<Project><PropertyGroup><ImportDirectoryBuildProps>false</ImportDirectoryBuildProps><ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets><SiemensEngineeringDirectory Condition=\"'$(SiemensEngineeringDirectory)' == ''\">selected</SiemensEngineeringDirectory></PropertyGroup><Import Project=\"Sdk.props\" Sdk=\"Microsoft.NET.Sdk\" /><PropertyGroup><AssemblyName>leaf</AssemblyName><TargetFramework>netstandard2.1</TargetFramework><OutputPath>$(MSBuildProjectDirectory)/absolute-output/</OutputPath></PropertyGroup><Import Project=\"Sdk.targets\" Sdk=\"Microsoft.NET.Sdk\" /></Project>");
        Write("leaf/Value.cs", "public class Value {\n#if ONE\npublic const int Number = 1;\n#else\npublic const int Number = 2;\n#endif\n}\n");
        Write("middle/middle.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" TreatAsLocalProperty=\"DefineConstants\"><PropertyGroup><TargetFrameworks>netstandard2.1;net10.0</TargetFrameworks><DefineConstants>$(DefineConstants);LOCAL_OVERRIDE</DefineConstants></PropertyGroup><ItemGroup><ProjectReference Include=\"../" + leafProject + "\" GlobalPropertiesToRemove=\"BaseIntermediateOutputPath;MSBuildProjectExtensionsPath\" /></ItemGroup><Target Name=\"SelectReferenceProperties\" BeforeTargets=\"AssignProjectConfiguration\"><ItemGroup><ProjectReference Update=\"../" + leafProject + "\" AdditionalProperties=\"SiemensEngineeringDirectory=selected\" /></ItemGroup></Target></Project>");
        Write("entry/entry.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../middle/middle.csproj\" /></ItemGroup></Project>");
        Write("offline.config", "<configuration><packageSources><clear /></packageSources></configuration>");
        var builds = new[] { new CheckBuild("entry/entry.csproj", "same", ["-p:DefineConstants=ONE", "-p:SiemensEngineeringDirectory=initial"]), new CheckBuild("entry/entry.csproj", "same", ["-p:DefineConstants=TWO", "-p:SiemensEngineeringDirectory=initial"]),
            new CheckBuild(leafProject, "same", ["-p:DefineConstants=ONE"]), new CheckBuild("middle/middle.csproj", "same", ["-p:DefineConstants=ONE", "-p:SiemensEngineeringDirectory=initial"]) };
        var result = CheckAllBuild.Run(root, builds, Path.Combine(root, "report"), "dotnet", null, Path.Combine(root, "offline.config"), 1, false);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        Assert.False(File.Exists(Path.Combine(root, "bin-build/check-all/runtime-paths.tsv")));
        var runtime = File.ReadLines(Path.Combine(root, "report/runtime-paths.tsv")).Select(line => line.Split('\t')).Where(row => row[0].EndsWith("middle.csproj", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { "net10.0", "netstandard2.1" }, runtime.Select(row => Path.GetFileName(row[2].TrimEnd('/', '\\'))).Order());
        Assert.All(runtime, row => { Assert.True(Directory.Exists(row[2])); Assert.True(File.Exists(row[4])); });
        var leafRuntime = File.ReadLines(Path.Combine(root, "report/runtime-paths.tsv")).Select(line => line.Split('\t')).Single(row => row[0].EndsWith(Path.GetFileName(leafProject), StringComparison.Ordinal));
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "leaf/absolute-output/netstandard2.1")), leafRuntime[3].TrimEnd('/', '\\'));
        var receipts = Directory.GetFiles(Path.Combine(root, "leaf/obj/check-all"), "check-global-properties.txt", SearchOption.AllDirectories);
        // The SDK also adds entry -> leaf transitive references from restored
        // assets. Both inherited "initial" and target-selected property sets
        // must be restored, under each of ONE and TWO. A fifth node computes
        // "selected" locally; it must differ from the matching global value.
        Assert.Equal(5, receipts.Length);
        foreach (var receipt in receipts)
        {
            var variantDirectory = Directory.GetParent(Path.GetDirectoryName(receipt)!)!.Parent!.FullName;
            Assert.True(File.Exists(Path.Combine(variantDirectory, "project.assets.json")), receipt);
        }
        var assemblies = Directory.GetFiles(Path.Combine(root, "leaf/bin/check-all"), "leaf.dll", SearchOption.AllDirectories);
        Assert.Equal(5, assemblies.Length);
        Assert.Equal(new[] { 1, 1, 1, 2, 2 }, assemblies.Select(path => (int)System.Reflection.Assembly.Load(File.ReadAllBytes(path)).GetType("Value")!.GetField("Number")!.GetRawConstantValue()!).Order());
        var timestamps = assemblies.Select(File.GetLastWriteTimeUtc).ToArray();
        result = CheckAllBuild.Run(root, builds, Path.Combine(root, "warm"), "dotnet", null, Path.Combine(root, "offline.config"), 1, false);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        Assert.Equal(timestamps, assemblies.Select(File.GetLastWriteTimeUtc));
        if (verifyInvalidation)
        {
            var project = Path.Combine(root, leafProject);
            File.WriteAllText(project, File.ReadAllText(project).Replace("<AssemblyName>leaf</AssemblyName>", "<AssemblyName>leaf</AssemblyName><AssemblyVersion>2.0.0.0</AssemblyVersion>", StringComparison.Ordinal));
            Write("leaf/Value.cs", "public class Value {\n#if ONE\npublic const int Number = 3;\n#else\npublic const int Number = 4;\n#endif\n}\n");
            result = CheckAllBuild.Run(root, builds, Path.Combine(root, "changed"), "dotnet", null, Path.Combine(root, "offline.config"), 1, false);
            Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
            Assert.Equal(new[] { 3, 3, 3, 4, 4 }, assemblies.Select(path => (int)System.Reflection.Assembly.Load(File.ReadAllBytes(path)).GetType("Value")!.GetField("Number")!.GetRawConstantValue()!).Order());
            Assert.All(assemblies, path => Assert.Equal(new Version(2, 0, 0, 0), System.Reflection.Assembly.Load(File.ReadAllBytes(path)).GetName().Version));
        }
    }
}
