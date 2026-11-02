using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.BuildCommon;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class BuildUnitKeyTests
{
    [Fact]
    public void DecoderPayloadInputsInvalidateEveryConsumingBuildUnit()
    {
        using var fixture = new KeyGraph();
        var runtime = Path.Combine(fixture.Root, "runtime/decoder-python");
        Directory.CreateDirectory(runtime);
        var receipt = Path.Combine(runtime, "runtime.json");
        File.WriteAllText(receipt, "{\"tag\":\"python312\",\"version\":\"3.12.10\",\"architecture\":\"x64\",\"license\":\"LICENSE.txt\",\"files\":{}}\n");
        var project = XDocument.Load(fixture.Projects[2]);
        project.Root!.Add(new XElement("ItemGroup", new XElement("Content", new XAttribute("Include", "../runtime/decoder-python/runtime.json"))));
        project.Save(fixture.Projects[2]);
        var before = fixture.Projects.Select(path => fixture.Key(path, new BuildKeyMemo())).ToArray();
        var timestamp = File.GetLastWriteTimeUtc(receipt);
        File.AppendAllText(receipt, "\n"); File.SetLastWriteTimeUtc(receipt, timestamp);
        var after = fixture.Projects.Select(path => fixture.Key(path, new BuildKeyMemo())).ToArray();
        Assert.All(Enumerable.Range(0, before.Length), index => Assert.NotEqual(before[index], after[index]));
    }

    [Fact]
    public void SequentialAndParallelKeysAreByteIdenticalForASharedMultiProjectGraph()
    {
        using var fixture = new KeyGraph();
        var sequential = new BuildKeyMemo();
        var expected = fixture.Projects.Select(project => fixture.Key(project, sequential)).ToArray();
        Assert.Equal(expected[0], fixture.Key(fixture.Projects[0], null));
        var parallel = new BuildKeyMemo();
        var actual = new string[expected.Length];
        Parallel.For(0, actual.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, index =>
            actual[index] = fixture.Key(fixture.Projects[index], parallel));
        Assert.Equal(expected, actual);
        Assert.All(actual, key => Assert.Equal(64, key.Length));
        Assert.NotEmpty(parallel.Probes);
        Assert.NotEmpty(parallel.Imports);
        Assert.Equal(sequential.ProbeProcesses, parallel.ProbeProcesses);
        Assert.Equal(parallel.Probes.Count + parallel.Imports.Count, parallel.ProbeProcesses);
    }

    [Fact]
    public void CompletionOnlyReusesRawImmutableExternalHashesAndComputesSharedInputsOnce()
    {
        using var fixture = new OfflineFixtures();
        var root = Path.Combine(fixture.DirectoryPath, "repo");
        var external = Path.Combine(fixture.DirectoryPath, "packages/library.dll");
        var repository = Path.Combine(root, "sdk/library.dll");
        var mutable = Path.Combine(fixture.DirectoryPath, "settings.props");
        var memo = new BuildKeyMemo();
        int reads = 0;
        Parallel.For(0, 32, _ => Assert.Equal("external", memo.Hash(external, root, false, true, () => { Interlocked.Increment(ref reads); return "external"; })));
        Assert.Equal(1, reads);
        Assert.Equal("before", memo.Hash(repository, root, false, true, () => "before"));
        Assert.Equal("before", memo.Hash(mutable, root, false, false, () => "before"));
        Assert.Equal("normalized", memo.Hash(external, root, true, true, () => "normalized"));
        memo.Probes["probe"] = new(() => new(0, "before", ""));
        memo.Imports["imports"] = new(() => "before");
        var after = memo.ForCompletion();
        Assert.Empty(after.Probes); Assert.Empty(after.Imports);
        Assert.Equal("external", after.Hash(external, root, false, true, () => throw new InvalidOperationException("Immutable input was reread")));
        Assert.Equal("after", after.Hash(repository, root, false, true, () => "after"));
        Assert.Equal("after", after.Hash(mutable, root, false, false, () => "after"));
        Assert.Equal("renormalized", after.Hash(external, root, true, true, () => "renormalized"));
    }

    [Fact]
    public void CompletionReevaluatesImportsAndRepositoryBytesWithPreservedTimestamps()
    {
        using var fixture = new KeyGraph();
        var memo = new BuildKeyMemo();
        var before = fixture.Key(fixture.Projects[0], memo);
        var input = Path.Combine(fixture.Root, "Shared/Input.cs");
        var stamp = File.GetLastWriteTimeUtc(input);
        File.WriteAllText(input, "public class Shared { public const int Number = 43; }\n");
        File.SetLastWriteTimeUtc(input, stamp);
        var after = memo.ForCompletion();
        Assert.NotEqual(before, fixture.Key(fixture.Projects[0], after));
        Assert.NotEmpty(after.Probes); Assert.NotEmpty(after.Imports);
        var import = Path.Combine(fixture.Root, "settings.props");
        stamp = File.GetLastWriteTimeUtc(import);
        var key = fixture.Key(fixture.Projects[0], after);
        File.WriteAllText(import, "<Project><PropertyGroup><DefineConstants>CHANGED</DefineConstants></PropertyGroup></Project>");
        File.SetLastWriteTimeUtc(import, stamp);
        Assert.NotEqual(key, fixture.Key(fixture.Projects[0], after.ForCompletion()));
    }

    [Fact]
    public void ColdEvidenceWithoutPayloadStillPassesAdoptionAndChecksNondeterminism()
    {
        using var fixture = new KeyGraph();
        var cache = fixture.Cache(noReads: true);
        cache.Prepare(fixture.Graph);
        fixture.Build();
        var report = Path.Combine(fixture.Root, "build-cache.json");
        cache.Complete(report, true);
        var data = JsonNode.Parse(File.ReadAllText(report))!.AsObject();
        Assert.Empty(data["failures"]!.AsArray());
        Assert.Equal(fixture.Projects.Length, data["units"]!.AsArray().Count);
        Assert.All(data["units"]!.AsArray(), row =>
        {
            Assert.Equal("cold", (string?)row!["provenance"]);
            Assert.Equal("cold-run", (string?)row["runId"]);
            Assert.Contains("cold-run", row["coldRunIds"]!.AsArray().Select(id => (string)id!));
        });
        Assert.Empty(Directory.EnumerateDirectories(fixture.CacheRoot, "payload", SearchOption.AllDirectories));
        var problems = new List<string>();
        Assert.True(VerifiedBuildEvidence.Validate(fixture.Root, data, problems, 0), string.Join("\n", problems));
        var unit = data["units"]![0]!;
        var output = (string)unit["outputs"]![0]!["path"]!;
        var name = (string)unit["unit"]!; var key = (string)unit["inputKey"]!;
        var stored = new BuildOutputCache(fixture.CacheRoot, name);
        Assert.False(stored.RestoreVerified(key, fixture.Root, unit["outputs"]!.AsArray().Select(row => (string)row!["path"]!)));
        File.AppendAllText(Path.Combine(fixture.Root, output, "extra.txt"), "different output");
        Assert.Throws<BuildNonDeterminismException>(() => stored.RecordBuild(key, fixture.Root,
            unit["outputs"]!.AsArray().Select(row => (string)row!["path"]!), "second-cold", true, copyOutputs: false));
        Assert.Null(stored.Record(key));
    }

    [Fact]
    public void MetadataOnlyVerificationPreservesExistingVerifiedPayload()
    {
        using var fixture = new OfflineFixtures();
        var output = Path.Combine(fixture.DirectoryPath, "output"); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "library.dll"), "bytes");
        var cache = new BuildOutputCache(Path.Combine(fixture.DirectoryPath, "cache"), "unit");
        var key = BuildOutputCache.Key([], []);
        cache.RecordBuild(key, output, "first", true);
        cache.RecordBuild(key, output, "second", true, copyOutputs: false);
        Assert.True(cache.RestoreVerified(key, Path.Combine(fixture.DirectoryPath, "restored")));
        Assert.Equal(new[] { "first", "second" }, cache.Record(key)!.ColdRunIds);
    }

    [Fact]
    public void DeadlineStopsTheCurrentLoopAndReportsRemainingUnitsOnce()
    {
        var now = DateTime.UtcNow; var end = now.AddSeconds(1);
        var deadline = new BuildUnitDeadline(("build-once", end), () => now);
        var started = new List<int>();
        var error = deadline.ForUnits(5, 1, "Complete", index =>
        {
            started.Add(index);
            if (index == 1) { now = end; deadline.Check(); }
        });
        Assert.Equal(new[] { 0, 1 }, started);
        Assert.Equal("Check job timed out: build-once during build unit Complete; 4 of 5 units not completed.", error);
        int furtherWork = 0;
        Assert.Contains("5 of 5", deadline.ForUnits(5, 4, "Prepare", _ => Interlocked.Increment(ref furtherWork)));
        Assert.Equal(0, furtherWork);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpiredCompletionWritesOneFailureWithoutHashingOrCopyingOutputs(bool expireDuringProjection)
    {
        using var fixture = new KeyGraph();
        var now = DateTime.UtcNow; var end = now.AddHours(1);
        var deadline = new BuildUnitDeadline(("build-once", end), () => now);
        var cache = fixture.Cache(noReads: true, deadline);
        cache.Prepare(fixture.Graph);
        if (!expireDuringProjection) now = end;
        var report = Path.Combine(fixture.Root, "build-cache.json");
        bool projected = false;
        var error = Assert.Throws<ReleaseException>(() => cache.Complete(report, true, checkDeadline =>
        {
            projected = true; now = end; checkDeadline();
        }));
        Assert.Equal(expireDuringProjection, projected);
        Assert.Contains("3 of 3 units not completed", error.Message);
        var data = JsonNode.Parse(File.ReadAllText(report))!;
        Assert.Single(data["failures"]!.AsArray()); Assert.Empty(data["units"]!.AsArray());
        Assert.False(Directory.Exists(fixture.CacheRoot));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "bin-build/unit-diagnostics")));
    }

    [Fact]
    public void CompleteRejectsChangedRepositoryInputsEvenWhenTheirTimestampsAreUnchanged()
    {
        using var fixture = new KeyGraph();
        var cache = fixture.Cache(noReads: true);
        cache.Prepare(fixture.Graph);
        var input = Path.Combine(fixture.Root, "Shared/Input.cs");
        var stamp = File.GetLastWriteTimeUtc(input);
        File.AppendAllText(input, "// changed during compilation\n"); File.SetLastWriteTimeUtc(input, stamp);
        var report = Path.Combine(fixture.Root, "build-cache.json");
        var error = Assert.Throws<ReleaseException>(() => cache.Complete(report, true));
        Assert.Contains("Build unit inputs changed during build", error.Message);
        Assert.Equal(3, JsonNode.Parse(File.ReadAllText(report))!["failures"]!.AsArray().Count);
        Assert.False(Directory.Exists(fixture.CacheRoot));
    }

    [Fact]
    public void ChunkedHashIsIdenticalAndStopsInsideALargeFile()
    {
        using var fixture = new OfflineFixtures();
        var path = Path.Combine(fixture.DirectoryPath, "large.bin");
        File.WriteAllBytes(path, Enumerable.Range(0, 1024 * 1024).Select(index => (byte)index).ToArray());
        Assert.Equal(ReleaseRecords.HashFile(path), ReleaseRecords.HashFile(path, () => { }));
        int chunks = 0;
        Assert.Throws<OperationCanceledException>(() => ReleaseRecords.HashFile(path, () => { if (++chunks == 3) throw new OperationCanceledException(); }));
        Assert.Equal(3, chunks);
    }

    [Fact]
    public void RuntimeProjectionStopsBeforeCopyingAnotherFileAfterTheDeadline()
    {
        using var fixture = new OfflineFixtures();
        var source = Path.Combine(fixture.DirectoryPath, "source"); Directory.CreateDirectory(source);
        var destination = Path.Combine(fixture.DirectoryPath, "destination");
        File.WriteAllText(Path.Combine(source, "a.dll"), "first"); File.WriteAllText(Path.Combine(source, "b.dll"), "second");
        var manifest = Path.Combine(fixture.DirectoryPath, "runtime-paths.tsv");
        File.WriteAllText(manifest, string.Join('\t', Path.Combine(fixture.DirectoryPath, "unit/unit.csproj"), "default", source, destination, Path.Combine(source, "a.dll")));
        Assert.Throws<OperationCanceledException>(() => ReleaseCommands.PrepareCheckAllRuntime(fixture.DirectoryPath, [], manifest, () =>
        {
            if (Directory.Exists(destination) && Directory.EnumerateFiles(destination).Any()) throw new OperationCanceledException();
        }));
        Assert.Single(Directory.EnumerateFiles(destination));
        Assert.Equal(2, Directory.GetFiles(source).Length);
    }

    private sealed class KeyGraph : IDisposable
    {
        private readonly OfflineFixtures fixture = new();
        internal string Root => Path.Combine(fixture.DirectoryPath, "repo");
        internal string CacheRoot => Path.Combine(fixture.DirectoryPath, "cache");
        internal string Graph => Path.Combine(Root, "build/build-nodes.props");
        internal string[] Projects => new[] { "Entry", "Other", "Shared" }.Select(name => Path.Combine(Root, name, name + ".csproj")).ToArray();
        internal KeyGraph()
        {
            void Write(string relative, string content) { var path = Path.Combine(Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); }
            Write("Directory.Build.props", "<Project><Import Project=\"settings.props\" /></Project>");
            Write("settings.props", "<Project />");
            Write("offline.config", "<configuration><packageSources><clear /></packageSources></configuration>");
            Write("build-tools/bundled-dotnet.json", "{}"); Write("build-tools/release/orchestrator.cs", "// build inputs");
            foreach (var project in Projects)
            {
                var name = Path.GetFileNameWithoutExtension(project);
                Write(name + "/" + name + ".csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" +
                    (name == "Shared" ? "" : "<ItemGroup><ProjectReference Include=\"../Shared/Shared.csproj\" /></ItemGroup>") + "</Project>");
                Write(name + "/Input.cs", "public class " + name + " { public const int Number = 42; }\n");
                ProcessRunner.RequireSuccess(ProcessRunner.Run("dotnet", ["msbuild", project, "-t:Restore", "-m:1", "-nodeReuse:false", "-p:NuGetAudit=false",
                    "-p:RestoreConfigFile=" + Path.Combine(Root, "offline.config")], Root), "Restore key graph");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Graph)!);
            new XDocument(new XElement("Project", new XElement("ItemGroup", Projects.Select((project, index) => new XElement("CheckGraphProject",
                new XAttribute("Include", project), new XElement("AdditionalProperties", "Configuration=Release;CheckAllNodeId=unit-" + index)))))).Save(Graph);
        }
        internal string Key(string project, BuildKeyMemo? memo) => ReleaseCommands.BuildKey("dotnet", project,
            ["build", project, "-c", "Release"], Root, memo: memo).Key;
        internal CheckAllUnitCache Cache(bool noReads, BuildUnitDeadline? deadline = null) => new(Root, Path.GetDirectoryName(Graph)!, CacheRoot, "dotnet",
            new Dictionary<string, string?>(), noReads, "cold-run", 4, deadline);
        internal void Build()
        {
            foreach (var project in Projects)
                ProcessRunner.RequireSuccess(ProcessRunner.Run("dotnet", ["msbuild", project, "-t:Build", "-m:1", "-nodeReuse:false", "-p:Configuration=Release", "-p:UseSharedCompilation=false"], Root), "Build key graph");
        }
        public void Dispose() => fixture.Dispose();
    }
}
