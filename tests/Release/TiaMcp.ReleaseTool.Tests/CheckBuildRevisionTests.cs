using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using TiaMcp.ReleaseTool;
using Xunit;
using Xunit.Abstractions;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class CheckBuildRevisionTests(ITestOutputHelper output)
{
    [Fact]
    public void FileProgramCheckMarkerHasItsOwnCacheIdentity()
    {
        using var tree = new StaticTree();
        tree.Write("scripts/app.cs", "Console.WriteLine(1);\n");
        int builds = 0;
        bool checking = true;
        CommandResult Run(string _, IEnumerable<string> arguments, string __, IDictionary<string, string?>? environment)
        {
            var args = arguments.ToArray();
            if (args[0] == "--version") return new(0, "fixture-sdk", "");
            builds++;
            foreach (var argument in BuildReproducibility.CheckArguments) Assert.Equal(checking, args.Contains(argument));
            var target = tree.Write("bin-build/payload/bin/app.dll", checking ? "check" : "ordinary");
            var intermediate = Path.GetDirectoryName(tree.Write("bin-build/payload/obj/input", "generated"))!;
            File.WriteAllText(Path.Combine(environment!["TIA_MCP_ANALYZER_REPORT_DIRECTORY"]!, "app.sarif"), "diagnostics");
            return new(0, JsonSerializer.Serialize(new { Properties = new { TargetPath = target, IntermediateOutputPath = intermediate } }), "");
        }
        CommandResult Build(string name) => FileBasedPrograms.Build(tree.Root, Path.Combine(tree.Root, "bin-build", name), "dotnet-fixture", null,
            process: Run, checkBuild: checking);
        Assert.Equal(0, Build("first").ExitCode);
        Assert.Equal(0, Build("second").ExitCode);
        Assert.Equal(1, builds);
        checking = false;
        Assert.Equal(0, Build("ordinary").ExitCode);
        Assert.Equal(2, builds);
    }

    [Fact]
    public void ParityKeysIgnoreHeadButStillReevaluateChangedInputs()
    {
        using var fixture = new RevisionFixture();
        var firstRevision = fixture.Revision("first", "public class Value { public const int Number = 42; }\n");
        var unit = new CheckBuild("leaf/leaf.csproj", "", []);
        string Key(string name) => new CheckAllLayout.KeyPhase(new()).Compute(fixture.Root, [unit], "dotnet", Path.Combine(fixture.Root, "offline.config"),
            fixture.Root, Path.Combine(fixture.Root, "bin-build", name), 1, "keys")[unit]!;
        var first = Key("first");
        var secondRevision = fixture.Revision("docs", "public class Value { public const int Number = 42; }\n", firstRevision);
        Assert.Equal(first, Key("second"));
        fixture.Revision("docs", "public class Value { public const int Number = 43; }\n", secondRevision);
        Assert.NotEqual(first, Key("third"));
    }

    [Fact]
    public void DocumentationCommitRestoresIdenticalCheckOutputsButAnInputCommitRebuilds()
    {
        using var fixture = new RevisionFixture();
        var firstRevision = fixture.Revision("first", "public class Value { public const int Number = 42; }\n");
        var timer = Stopwatch.StartNew();
        var cold = fixture.Check("cold");
        var coldSeconds = timer.Elapsed.TotalSeconds;
        Assert.Equal(0, (int)cold["outputsRestored"]!);
        var first = cold["units"]!.AsArray().ToDictionary(row => (string)row!["unit"]!, row => row!.DeepClone());
        var secondRevision = fixture.Revision("documentation changed", "public class Value { public const int Number = 42; }\n", firstRevision);
        var diff = ProcessRunner.Run("git", ["diff", "--name-only", firstRevision, secondRevision], fixture.Root);
        ProcessRunner.RequireSuccess(diff, "Documentation-only commit fixture");
        Assert.Equal("README.md", diff.StandardOutput.Trim());
        Assert.NotEqual(firstRevision, secondRevision);

        timer.Restart();
        var warm = fixture.Check("warm");
        var warmSeconds = timer.Elapsed.TotalSeconds;
        Assert.True((int)warm["outputsRestored"]! > 0, warm.ToJsonString());
        Assert.Equal(first.Count, (int)warm["outputsRestored"]!);
        foreach (var row in warm["units"]!.AsArray())
        {
            var prior = first[(string)row!["unit"]!];
            Assert.Equal("restored", (string?)row["provenance"]);
            Assert.Equal((string?)prior["inputKey"], (string?)row["inputKey"]);
            Assert.True(JsonNode.DeepEquals(prior["outputs"], row["outputs"]));
            var assembly = row["outputs"]!.AsArray().SelectMany(item => item!["files"]!.AsArray()
                .Where(file => ((string)file!["path"]!).EndsWith(".dll", StringComparison.Ordinal))
                .Select(file => Path.Combine(fixture.Root, (string)item!["path"]!, (string)file!["path"]!))).First();
            Assert.EndsWith("+check", FileVersionInfo.GetVersionInfo(assembly).ProductVersion);
        }
        var problems = new List<string>();
        // Exercise the existing cold spot-check with the unchanged hash gate.
        Assert.True(VerifiedBuildEvidence.Validate(fixture.Root, warm.AsObject(), problems, 1), string.Join("\n", problems));
        Assert.Empty(problems);

        fixture.Revision("documentation changed", "public class Value { public const int Number = 43; }\n", secondRevision);
        Assert.Contains("43", File.ReadAllText(Path.Combine(fixture.Root, "leaf/Value.cs")));
        var changed = fixture.Check("input-changed");
        // The executable graph also contains the weaver and build-task prerequisites.
        // They do not consume Value.cs; only the leaf and its consumer must rebuild.
        Assert.Equal(2, (int)changed["outputsRestored"]!);
        int rebuilt = 0;
        Assert.All(changed["units"]!.AsArray(), row =>
        {
            var name = (string)row!["unit"]!;
            var prior = first[name];
            if (name.StartsWith("leaf/", StringComparison.Ordinal) || name.StartsWith("entry/", StringComparison.Ordinal))
            {
                rebuilt++;
                Assert.Equal("cold", (string?)row["provenance"]);
                Assert.NotEqual((string?)prior["inputKey"], (string?)row["inputKey"]);
                Assert.False(JsonNode.DeepEquals(prior["outputs"], row["outputs"]));
                string Assemblies(JsonNode unit) => string.Join('|', unit["outputs"]!.AsArray().SelectMany(item => item!["files"]!.AsArray())
                    .Where(file => ((string)file!["path"]!).EndsWith(".dll", StringComparison.Ordinal)).Select(file => (string)file!["sha256"]!));
                Assert.NotEqual(Assemblies(prior), Assemblies(row));
            }
            else
            {
                Assert.Equal("restored", (string?)row["provenance"]);
                Assert.Equal((string?)prior["inputKey"], (string?)row["inputKey"]);
                Assert.True(JsonNode.DeepEquals(prior["outputs"], row["outputs"]));
            }
        });
        Assert.Equal(2, rebuilt);
        var checkTime = (double)warm["prepareSeconds"]! + (double)warm["completeSeconds"]!;
        output.WriteLine($"P-C fixture: cold {coldSeconds:F3}s, documentation-only commit {warmSeconds:F3}s, saving {coldSeconds - warmSeconds:F3}s; restored {(int)warm["outputsRestored"]!}/{first.Count} units, with byte-identical outputs; key verification {checkTime:F3}s. Changed input rebuilt {rebuilt} dependent units and restored two unchanged prerequisites.");
    }

    [Fact]
    public void ReleaseKeysAndAssembliesRetainTheCommitAndCannotUseCheckEntries()
    {
        using var fixture = new RevisionFixture();
        var firstRevision = fixture.Revision("first", "public class Value { public const int Number = 42; }\n");
        var first = fixture.Release(firstRevision);
        Assert.EndsWith("+" + firstRevision, first.Version);
        var secondRevision = fixture.Revision("docs", "public class Value { public const int Number = 42; }\n", firstRevision);
        var second = fixture.Release(secondRevision);
        Assert.EndsWith("+" + secondRevision, second.Version);
        Assert.NotEqual(first.Hash, second.Hash);
        Assert.NotEqual(first.Key, second.Key);
        var project = Path.Combine(fixture.Root, "leaf/leaf.csproj");
        var args = fixture.Arguments(project, secondRevision);
        var cache = new BuildOutputCache(Path.Combine(fixture.Root, "bin-build/release-cache"), "leaf");
        cache.RecordBuild(second.Key, fixture.Root, ["leaf/bin/Release/net10.0"], "release", true);
        var checkArgs = args.Where(arg => !arg.StartsWith("-p:SourceRevisionId=", StringComparison.Ordinal)).Concat(BuildReproducibility.CheckArguments).ToArray();
        var checkKey = ReleaseCommands.BuildKey("dotnet", project, checkArgs, fixture.Root).Key;
        Assert.NotEqual(second.Key, checkKey);
        Assert.False(cache.RestoreVerified(checkKey, Path.Combine(fixture.Root, "bin-build/wrong-release")));
        ProcessRunner.RequireSuccess(ProcessRunner.Run("dotnet", [.. checkArgs, "--no-incremental", "-m:1", "-nodeReuse:false"], fixture.Root), "Check marker build");
        Assert.EndsWith("+check", FileVersionInfo.GetVersionInfo(Path.Combine(fixture.Root, "leaf/bin/Release/net10.0/leaf.dll")).ProductVersion);
        var checkOnly = new BuildOutputCache(Path.Combine(fixture.Root, "bin-build/check-only-cache"), "leaf");
        checkOnly.RecordBuild(checkKey, fixture.Root, ["leaf/bin/Release/net10.0"], "check", true);
        Assert.False(checkOnly.RestoreVerified(second.Key, Path.Combine(fixture.Root, "bin-build/release-from-check")));
        var normalized = ReleaseCommands.CacheArguments(project, checkArgs, fixture.Root);
        Assert.Contains("tiacheckbuild=true", normalized);
        Assert.DoesNotContain(normalized, arg => arg.Contains("sourcerevision", StringComparison.OrdinalIgnoreCase) || arg.Contains(secondRevision, StringComparison.Ordinal));
        Assert.Throws<IOException>(() => ReleaseCommands.CacheArguments(project,
            [.. args, "-p:TiaCheckBuild=true"], fixture.Root));
    }

    private sealed class RevisionFixture : IDisposable
    {
        private readonly OfflineFixtures fixture = new();
        internal string Root => fixture.DirectoryPath;
        private string Nuget => Path.Combine(Root, "offline.config");
        internal RevisionFixture()
        {
            var repo = Repository.FindRoot();
            foreach (var file in new[] { "CheckAll.Build.props", "CheckAll.Build.targets", "check-build/TiaMcp.CheckBuild.csproj", "check-build/CheckBuildTasks.cs" })
                Write("build-tools/release/" + file, File.ReadAllText(Path.Combine(repo, "build-tools/release", file)));
            Write("src/Shared/ReproducibleBuild.props", File.ReadAllText(Path.Combine(repo, "src/Shared/ReproducibleBuild.props")));
            Write("Directory.Build.props", "<Project><Import Project=\"src/Shared/ReproducibleBuild.props\" /></Project>");
            Write("build-tools/bundled-dotnet.json", "{}");
            Write("build-tools/native-call-weaver/NativeCallWeaver.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            Write("leaf/leaf.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            Write("entry/entry.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../leaf/leaf.csproj\" /></ItemGroup></Project>");
            Write("entry/Entry.cs", "public class Entry { public int Read() => Value.Number; }\n");
            Write("offline.config", "<configuration><packageSources><clear /></packageSources></configuration>");
            ProcessRunner.RequireSuccess(ProcessRunner.Run("git", ["init", "--quiet", Root], Root), "Revision fixture repository");
        }
        private void Write(string relative, string content)
        {
            var path = Path.Combine(Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content);
        }
        internal string Revision(string docs, string source, string? parent = null)
        {
            Write("README.md", docs); Write("leaf/Value.cs", source);
            // Real Git objects without staging, committing, or touching worktree Git metadata.
            string Object(string type, byte[] bytes)
            {
                var data = Encoding.UTF8.GetBytes(type + " " + bytes.Length + "\0").Concat(bytes).ToArray();
                var id = Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();
                var path = Path.Combine(Root, ".git/objects", id[..2], id[2..]);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var file = File.Create(path); using var compressed = new ZLibStream(file, CompressionLevel.Optimal);
                compressed.Write(data); return id;
            }
            string Tree(string directory)
            {
                using var bytes = new MemoryStream();
                var entries = Directory.EnumerateFileSystemEntries(directory).Where(path => Path.GetFileName(path) is not (".git" or "bin-build" or "bin" or "obj"))
                    .OrderBy(path => Path.GetFileName(path) + (Directory.Exists(path) ? "/" : ""), StringComparer.Ordinal);
                foreach (var path in entries)
                {
                    bool child = Directory.Exists(path);
                    var id = child ? Tree(path) : Object("blob", File.ReadAllBytes(path));
                    bytes.Write(Encoding.UTF8.GetBytes((child ? "40000 " : "100644 ") + Path.GetFileName(path) + "\0"));
                    bytes.Write(Convert.FromHexString(id));
                }
                return Object("tree", bytes.ToArray());
            }
            var tree = Tree(Root);
            var commit = Object("commit", Encoding.UTF8.GetBytes("tree " + tree + "\n" + (parent == null ? "" : "parent " + parent + "\n") +
                "author Fixture <fixture@example.invalid> 1700000000 +0000\ncommitter Fixture <fixture@example.invalid> 1700000000 +0000\n\nfixture\n"));
            Write(".git/HEAD", commit + "\n");
            Assert.Equal(commit, BuildReproducibility.SourceRevision(Root));
            return commit;
        }
        internal JsonNode Check(string name)
        {
            var directory = Path.Combine(Root, "bin-build", name, "build");
            var result = CheckAllBuild.Run(Root, [new("entry/entry.csproj", "", [])], directory, "dotnet", null, Nuget, 1,
                cold: name != "warm", cacheRoot: Path.Combine(Root, "bin-build/cache"), runId: name, keyParallelism: 4);
            Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
            var report = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "bin-build", name, "build-cache.json")))!;
            Assert.Empty(report["failures"]!.AsArray());
            return report;
        }
        internal string[] Arguments(string project, string revision) => ["build", project, "-c", "Release", "-p:NuGetAudit=false",
            "-p:UseSharedCompilation=false", "-p:RestoreConfigFile=" + Nuget, "-p:SourceRevisionId=" + revision];
        internal (string Key, string Version, string Hash) Release(string revision)
        {
            var project = Path.Combine(Root, "leaf/leaf.csproj");
            var args = Arguments(project, revision);
            ProcessRunner.RequireSuccess(ProcessRunner.Run("dotnet", [.. args, "--no-incremental", "-m:1", "-nodeReuse:false"], Root), "Release revision build");
            var file = Path.Combine(Root, "leaf/bin/Release/net10.0/leaf.dll");
            return (ReleaseCommands.BuildKey("dotnet", project, args, Root).Key, FileVersionInfo.GetVersionInfo(file).ProductVersion!, ReleaseRecords.HashFile(file));
        }
        public void Dispose() => fixture.Dispose();
    }
}
