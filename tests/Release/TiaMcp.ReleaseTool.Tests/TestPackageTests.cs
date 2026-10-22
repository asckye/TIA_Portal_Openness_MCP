using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

// package-test: a test package from the current checkout; never published, tagged or written into release records.
public sealed class TestPackageTests
{
    private static readonly DateTime Built = new(2026, 10, 21, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CommandIsRegisteredWithBooleanBuildSwitch()
    {
        Assert.Contains("package-test", CommandLine.Commands);
        var options = Options.Parse(["-Build", "-OutputDirectory", "out"], new HashSet<string>(["Build"], StringComparer.OrdinalIgnoreCase));
        Assert.True(options.Has("Build"));
        Assert.Equal("out", options.Get("OutputDirectory"));
    }

    [Fact]
    public void ContentsAreTheDeliverySetWithoutVerifierSymbolsOrPublicApi()
    {
        using var tree = new StaticTree();
        var rules = DeliveryRules.Load(StaticCheckVectorTests.Root);
        string[] tracked = ["README.md", "docs/guides/a.md", "src/Logic/Code.cs", "AGENTS.md", "runtime/README.md", "docs/guides/deleted.md"];
        foreach (var name in tracked.Where(name => name != "docs/guides/deleted.md")) tree.Write(name, name);
        foreach (var name in new[]
        {
            "runtime/v16/TiaMcp.FoundationHost.dll", "runtime/v16/TiaMcp.FoundationHost.pdb", "runtime/v16/worker/Siemens.Engineering.dll", "runtime/v16/Siemens.Simatic.S7.Webserver.API.dll",
            "runtime/v16/Siemens.Collaboration.Net.dll", "runtime/verification/NativeCallWeaver.dll", "runtime/studio/TiaOpenness.exe",
            "runtime/tools/TiaMcp.Updater.exe", "bin-build/updater/TiaMcp.Updater.exe", "bin-build/updater/TiaMcp.Updater.exe.config",
            "bin-build/updater/TiaMcp.Updater.pdb", "TiaOpenness.exe", "TiaOpenness.pdb", "bin-build/other/file.dll", "src/Logic/bin/Release/x.dll"
        }) tree.Write(name, name);

        var contents = ReleaseCommands.TestPackageContents(tree.Root, tracked, rules.Delivered);

        Assert.Equal(
        [
            "README.md", "TiaOpenness.exe", "docs/guides/a.md", "runtime/README.md", "runtime/studio/TiaOpenness.exe",
            "runtime/tools/TiaMcp.Updater.exe", "runtime/tools/TiaMcp.Updater.exe.config", "runtime/v16/Siemens.Simatic.S7.Webserver.API.dll",
            "runtime/v16/TiaMcp.FoundationHost.dll"
        ], contents.Keys);
        // The updater is the build output in bin-build/updater, never a stale copy under runtime/tools.
        Assert.Equal(Path.Combine(tree.Root, "bin-build", "updater", "TiaMcp.Updater.exe"), contents["runtime/tools/TiaMcp.Updater.exe"]);
        Assert.All(contents.Keys, name => Assert.True(rules.Delivered(name)));
        Assert.True(ReleaseCommands.TestPackageExcluded("runtime/verification/Mono.Cecil.dll"));
        Assert.True(ReleaseCommands.TestPackageExcluded("runtime/studio/TiaOpenness.PDB"));
        Assert.True(ReleaseCommands.TestPackageExcluded("runtime/v21/worker/Siemens.Engineering.Base.dll"));
        Assert.False(ReleaseCommands.TestPackageExcluded("runtime/v21/worker/TiaMcp.Siemens.Helpers.dll"));
        // The redistributable Webserver API the hosts load and the Siemens licence texts ship as in a release.
        Assert.False(ReleaseCommands.TestPackageExcluded("runtime/v21/Siemens.Simatic.S7.Webserver.API.dll"));
        Assert.False(ReleaseCommands.TestPackageExcluded("docs/licenses/Siemens.Collaboration.Net.md"));
        Assert.False(rules.Delivered("runtime/v16/Siemens.Collaboration.Net.dll"));
    }

    [Fact]
    public void RefusesMissingAndStaleOutputsAndNamesTheNewestSource()
    {
        using var tree = ProductTree();
        Assert.Empty(ReleaseCommands.TestPackageOutputProblems(tree.Root, null));

        File.Delete(Path.Combine(tree.Root, "runtime/v16/worker/TiaMcp.PlcWorker.16.exe"));
        File.Delete(Path.Combine(tree.Root, "bin-build/updater/TiaMcp.Updater.exe"));
        Directory.Delete(Path.Combine(tree.Root, "runtime/dotnet/shared/Microsoft.WindowsDesktop.App"), true);
        var missing = ReleaseCommands.TestPackageOutputProblems(tree.Root, null);
        Assert.Equal(
        [
            "missing: runtime/v16/worker/TiaMcp.PlcWorker.16.exe", "missing: bin-build/updater/TiaMcp.Updater.exe",
            "missing: runtime/dotnet/shared/Microsoft.WindowsDesktop.App/" + RuntimeVersion() + " (bundled .NET)"
        ], missing);

        using var fresh = ProductTree();
        var source = fresh.Write("src/Logic/Changed.cs", "class Changed {}");
        File.SetLastWriteTimeUtc(source, Built.AddMinutes(5));
        // Build output folders and Markdown are not product sources.
        File.SetLastWriteTimeUtc(fresh.Write("src/Logic/obj/Release/generated.cs", ""), Built.AddHours(1));
        File.SetLastWriteTimeUtc(fresh.Write("src/Logic/README.md", ""), Built.AddHours(1));
        var stale = ReleaseCommands.TestPackageOutputProblems(fresh.Root, null);
        Assert.Equal(ReleaseCommands.TestPackageProducts().Count, stale.Count);
        Assert.All(stale, problem => Assert.Matches(@"\Astale: \S+ \(.+\) is older than src/Logic/Changed\.cs \(.+\)\z", problem));
        Assert.Contains(stale, problem => problem.StartsWith("stale: runtime/v14sp1/TiaMcp.FoundationHost.dll ", StringComparison.Ordinal));
        Assert.Contains(stale, problem => problem.StartsWith("stale: TiaOpenness.exe ", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildStampVouchesOnlyForTheBytesItBuiltAndOnlyFromItsStart()
    {
        using var tree = ProductTree();
        File.SetLastWriteTimeUtc(tree.Write("src/Logic/Changed.cs", "class Changed {}"), Built.AddMinutes(5));
        var hashes = ReleaseCommands.TestPackageProducts().ToDictionary(path => path, path => ReleaseRecords.HashFile(Path.Combine(tree.Root, path)), StringComparer.Ordinal);

        Assert.Empty(ReleaseCommands.TestPackageOutputProblems(tree.Root, new TestPackageStamp(Built.AddMinutes(10), hashes)));
        // A source edited after the build started stays newer than the stamp.
        Assert.Equal(hashes.Count, ReleaseCommands.TestPackageOutputProblems(tree.Root, new TestPackageStamp(Built.AddMinutes(1), hashes)).Count);
        File.WriteAllText(Path.Combine(tree.Root, "runtime/studio/TiaOpenness.dll"), "replaced after the build");
        File.SetLastWriteTimeUtc(Path.Combine(tree.Root, "runtime/studio/TiaOpenness.dll"), Built);
        Assert.Equal(["stale: runtime/studio/TiaOpenness.dll"], ReleaseCommands.TestPackageOutputProblems(tree.Root, new TestPackageStamp(Built.AddMinutes(10), hashes))
            .Select(problem => problem[..problem.IndexOf(" (", StringComparison.Ordinal)]));

        var work = Path.Combine(tree.Root, "bin-build", "test-package-work");
        ReleaseCommands.WriteTestPackageStamp(work, tree.Root, Built.AddMinutes(10));
        var stamp = ReleaseCommands.ReadTestPackageStamp(work)!;
        Assert.Equal(Built.AddMinutes(10), stamp.StartedUtc);
        Assert.Equal(ReleaseRecords.HashFile(Path.Combine(tree.Root, "runtime/studio/TiaOpenness.dll")), stamp.Files["runtime/studio/TiaOpenness.dll"]);
        Assert.Empty(ReleaseCommands.TestPackageOutputProblems(tree.Root, stamp));
        File.WriteAllText(Path.Combine(work, "last-build.json"), "{ not json");
        Assert.Null(ReleaseCommands.ReadTestPackageStamp(work));
    }

    [Fact]
    public void NameCarriesVersionTimeCommitAndDirtyState()
    {
        var time = new DateTime(2026, 10, 21, 23, 5, 59);
        Assert.Equal("TIA_MCP_TestPackage_v4.0.0-beta.1_20261021-2305_08ca19b3-dirty", ReleaseCommands.TestPackageName("4.0.0-beta.1", time, "08ca19b3", dirty: true));
        Assert.Equal("TIA_MCP_TestPackage_v3.3.0_20261021-2305_08ca19b3", ReleaseCommands.TestPackageName("3.3.0", time, "08ca19b3", dirty: false));
        Assert.DoesNotContain("Delivery", ReleaseCommands.TestPackageName("3.3.0", time, "08ca19b3", dirty: false), StringComparison.Ordinal);
    }

    [Fact]
    public void ArchiveHoldsThePackageFolderAndTheMarker()
    {
        using var tree = new StaticTree();
        var readme = tree.Write("stage/README.md", "read me");
        var host = tree.Write("stage/runtime/v21/TiaMcp.FoundationHost.dll", "host bytes");
        const string name = "TIA_MCP_TestPackage_v3.3.0_20261021-2305_08ca19b3-dirty";
        var marker = ReleaseCommands.TestPackageMarkerText("3.3.0", "08ca19b3cafe", dirty: true, new DateTimeOffset(Built), new DateTimeOffset(Built.AddMinutes(-3)));
        Assert.StartsWith("Test package - not a release; no release records are bound.\n", marker, StringComparison.Ordinal);
        Assert.Contains("Commit: 08ca19b3cafe\n", marker, StringComparison.Ordinal);
        Assert.Contains("Working tree: dirty", marker, StringComparison.Ordinal);
        Assert.Contains("Build time: 2026-10-21T10:00:00Z\n", marker, StringComparison.Ordinal);
        Assert.Contains("package-test -Build, started 2026-10-21T09:57:00Z", marker, StringComparison.Ordinal);
        Assert.Contains("Working tree: clean\n", ReleaseCommands.TestPackageMarkerText("3.3.0", "c", dirty: false, new DateTimeOffset(Built), null), StringComparison.Ordinal);

        var archive = Path.Combine(tree.Root, name + ".zip");
        ReleaseCommands.WriteTestPackageArchive(archive, name, [("runtime/v21/TiaMcp.FoundationHost.dll", host), ("README.md", readme)], marker);
        using (var zip = ZipFile.OpenRead(archive))
        {
            Assert.Equal([name + "/README.md", name + "/runtime/v21/TiaMcp.FoundationHost.dll", name + "/TEST-PACKAGE.txt"], zip.Entries.Select(entry => entry.FullName));
            using var reader = new StreamReader(zip.GetEntry(name + "/" + ReleaseCommands.TestPackageMarker)!.Open(), Encoding.UTF8);
            Assert.Equal(marker, reader.ReadToEnd());
        }
        // A test package is never overwritten.
        Assert.Throws<IOException>(() => ReleaseCommands.WriteTestPackageArchive(archive, name, [("README.md", readme)], marker));
    }

    [Fact]
    public void TrackedFileGuardReportsRewrittenTrackedFilesOnly()
    {
        var tree = new StaticTree();
        try
        {
            tree.Write("a.txt", "a");
            tree.Write("b.txt", "b");
            tree.Write(".gitignore", "bin-build/\n");
            ProcessRunner.RequireSuccess(ProcessRunner.Run("git", ["init", "--quiet"], tree.Root), "fixture repository");
            ProcessRunner.RequireSuccess(ProcessRunner.Run("git", ["add", "a.txt", "b.txt", ".gitignore"], tree.Root), "fixture index");
            var before = ReleaseCommands.TrackedFileState(tree.Root);
            Assert.Equal([".gitignore", "a.txt", "b.txt"], before.Keys.Order(StringComparer.Ordinal));

            tree.Write("untracked.txt", "new");
            tree.Write("bin-build/output.dll", "built");
            Assert.Empty(ReleaseCommands.ChangedTrackedFiles(before, ReleaseCommands.TrackedFileState(tree.Root)));

            File.SetLastWriteTimeUtc(tree.Write("a.txt", "a"), Built);
            File.Delete(Path.Combine(tree.Root, "b.txt"));
            Assert.Equal(["a.txt", "b.txt"], ReleaseCommands.ChangedTrackedFiles(before, ReleaseCommands.TrackedFileState(tree.Root)));
        }
        finally
        {
            // Git keeps its object files read-only.
            foreach (var file in Directory.EnumerateFiles(tree.Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            tree.Dispose();
        }
    }

    [Fact]
    public void BuildReusesReleaseStepsWithoutRecordsTestsOrVersionChanges()
    {
        var root = StaticCheckVectorTests.Root;
        var source = File.ReadAllText(Path.Combine(root, "build-tools/release/TestPackage.cs"));
        foreach (var step in new[] { "\"build-plc-workers\"", "\"build-studio\"", "\"get-bundled-dotnet\"", "\"-NoRecord\"", "InstallReleasePlcWorker(Root, key)", "AssertBundledRuntime(" })
            Assert.Contains(step, source, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "\"build-multi-version\"", "\"build-release\"", "\"prepare-delivery\"", "\"build-configurator\"", "\"-Test\"", "\"test\"", "WriteJson(", "catalog-tools-list" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        var commands = File.ReadAllText(Path.Combine(root, "build-tools/release/ReleaseCommands.cs"));
        var gate = commands.IndexOf("if (!options.Has(\"NoRecord\"))", StringComparison.Ordinal);
        Assert.True(gate > 0 && gate < commands.IndexOf("WriteJson(Path.Combine(Root, \"manifest/configurator-build.json\"), record)", StringComparison.Ordinal));
        Assert.Contains("if (options.Has(\"NoRecord\")) configArgs.Add(\"-NoRecord\");", commands, StringComparison.Ordinal);
        // Only package-test asks for -NoRecord; the release chain keeps writing the configurator record.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "build-tools/release"), "*.cs").Where(path => Path.GetFileName(path) is not ("TestPackage.cs" or "ReleaseCommands.cs" or "Program.cs")))
            Assert.DoesNotContain("NoRecord", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredPathsIncludeTheBinariesThatTheNoBinariesValidationSkips()
    {
        var root = StaticCheckVectorTests.Root;
        var paths = ReleaseCommands.TestPackageRequiredPaths(root, DeliveryRules.Load(root));
        foreach (var path in new[] { "TiaOpenness.exe", "runtime", "runtime/v21/TiaMcp.FoundationHost.exe", "runtime/tools/TiaMcp.Updater.exe", "runtime/tools/TiaMcp.Updater.exe.config", "runtime/tools/TiaMcp.WriteGuard.exe", "manifest/multi-version-build.json", "templates" })
            Assert.Contains(path, paths);
        Assert.DoesNotContain(ReleaseRecords.EnginePath, paths);
    }

    [Fact]
    public void TestPolicyAssembliesAreRejected()
    {
        using var tree = new StaticTree();
        tree.Write("runtime/v16/Clean.dll", "MZ ordinary assembly");
        Assert.Empty(ReleaseCommands.TestPolicyAssemblyErrors(tree.Root));
        tree.Write("runtime/v16/worker/Policy.dll", "MZ TiaMcpTestPolicy P6-DEVICE:safe-v4");
        Assert.Single(ReleaseCommands.TestPolicyAssemblyErrors(tree.Root), error => error.EndsWith("Policy.dll", StringComparison.Ordinal));
    }

    private static string RuntimeVersion()
    {
        using var pin = JsonDocument.Parse(File.ReadAllText(Path.Combine(StaticCheckVectorTests.Root, "build-tools/bundled-dotnet.json")));
        return pin.RootElement.GetProperty("version").GetString()!;
    }

    // Every product output and the pinned bundled runtime, all built after the only product source.
    private static StaticTree ProductTree()
    {
        var tree = new StaticTree();
        tree.Copy("build-tools/bundled-dotnet.json");
        File.SetLastWriteTimeUtc(tree.Write("src/Logic/Code.cs", "class Code {}"), Built.AddHours(-1));
        File.SetLastWriteTimeUtc(tree.Write("Version.props", "<Project />"), Built.AddHours(-2));
        File.SetLastWriteTimeUtc(Path.Combine(tree.Root, "build-tools/bundled-dotnet.json"), Built.AddHours(-2));
        foreach (var product in ReleaseCommands.TestPackageProducts()) File.SetLastWriteTimeUtc(tree.Write(product, product), Built);
        var version = RuntimeVersion();
        foreach (var framework in new[] { "Microsoft.NETCore.App", "Microsoft.AspNetCore.App", "Microsoft.WindowsDesktop.App" })
            tree.Write($"runtime/dotnet/shared/{framework}/{version}/{framework}.deps.json", "{}");
        tree.Write($"runtime/dotnet/host/fxr/{version}/hostfxr.dll", "fxr");
        return tree;
    }
}
