using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class VerifiedBuildCacheTests
{
    [Fact]
    public void CachedTraversalKeepsMultiTargetProjectReferencesAfterAnOuterBuild()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath; var repo = Repository.FindRoot();
        void Write(string relative, string content) { var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); }
        foreach (var file in new[] { "CheckAll.Build.props", "CheckAll.Build.targets", "check-build/TiaMcp.CheckBuild.csproj", "check-build/CheckBuildTasks.cs" })
            Write("build-tools/release/" + file, File.ReadAllText(Path.Combine(repo, "build-tools/release", file)));
        Write("build-tools/bundled-dotnet.json", "{}");
        Write("build-tools/native-call-weaver/NativeCallWeaver.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Write("src/WorkerChannel/TiaMcp.WorkerChannel.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>netstandard2.1</TargetFramework></PropertyGroup></Project>");
        Write("src/WorkerChannel/Channel.cs", "namespace Shared { public class Channel { } }\n");
        Write("core/core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>netstandard2.1;net10.0</TargetFrameworks></PropertyGroup><ItemGroup><ProjectReference Include=\"../src/WorkerChannel/TiaMcp.WorkerChannel.csproj\" /></ItemGroup></Project>");
        Write("core/Core.cs", "namespace Core { public class Session : Shared.Channel { } }\n");
        Write("entry/entry.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../core/core.csproj\" /></ItemGroup><Target Name=\"CaptureReferences\" AfterTargets=\"ResolveReferences\"><WriteLinesToFile File=\"$(TargetDir)references.txt\" Lines=\"@(ReferencePath->'%(Filename)')\" Overwrite=\"true\" /></Target></Project>");
        Write("entry/Entry.cs", "public class Entry { public Core.Session Open() => new Core.Session(); }\n");
        Write("offline.config", "<configuration><packageSources><clear /></packageSources></configuration>");
        CheckBuild[] builds = [new("core/core.csproj", "", []), new("entry/entry.csproj", "", []),
            new("entry/entry.csproj", "", ["-p:TiaSharedAdapterPaths=true"])];
        var directory = Path.Combine(root, "report");
        var result = CheckAllBuild.Run(root, builds, directory, "dotnet", null, Path.Combine(root, "offline.config"), 1,
            cold: false, cacheRoot: Path.Combine(root, "cache"), noCacheReads: true);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        var runtime = File.ReadLines(Path.Combine(directory, "runtime-paths.tsv")).Select(line => line.Split('\t')).ToArray();
        var entries = runtime.Where(row => row[0].EndsWith("entry.csproj", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, entries.Length);
        foreach (var row in entries)
        {
            var references = File.ReadAllLines(Path.Combine(row[2], "references.txt"));
            Assert.Contains("core", references); Assert.Contains("TiaMcp.WorkerChannel", references);
        }
        var graph = System.Xml.Linq.XDocument.Load(Path.Combine(directory, "build-nodes.props"));
        var sharedNodes = graph.Descendants("CheckGraphProject").Where(node => node.Attribute("Include")!.Value.EndsWith("TiaMcp.WorkerChannel.csproj", StringComparison.Ordinal)).ToArray();
        Assert.Single(sharedNodes);
        Assert.Contains("CheckAllVariant=shared", sharedNodes[0].Element("AdditionalProperties")!.Value);

        // An outer build is also a traversal root. Its wrapper must return the
        // assemblies it built, even though the SDK's outer Build returns none.
        var outer = graph.Descendants("CheckGraphProject").Single(node => node.Attribute("Include")!.Value.EndsWith("core.csproj", StringComparison.Ordinal) &&
            !node.Element("AdditionalProperties")!.Value.Split(';').Any(property => property.StartsWith("TargetFramework=", StringComparison.OrdinalIgnoreCase)));
        var output = Path.Combine(root, "outer-outputs.txt");
        var probe = Path.Combine(root, "probe.proj");
        new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement("Project",
            new System.Xml.Linq.XElement("ItemGroup", new System.Xml.Linq.XElement("Outer", new System.Xml.Linq.XAttribute("Include", outer.Attribute("Include")!.Value),
                new System.Xml.Linq.XElement(outer.Element("AdditionalProperties")!))),
            new System.Xml.Linq.XElement("Target", new System.Xml.Linq.XAttribute("Name", "Probe"),
                new System.Xml.Linq.XElement("MSBuild", new System.Xml.Linq.XAttribute("Projects", "@(Outer)"), new System.Xml.Linq.XAttribute("Targets", "CheckCachedBuild"),
                    new System.Xml.Linq.XElement("Output", new System.Xml.Linq.XAttribute("TaskParameter", "TargetOutputs"), new System.Xml.Linq.XAttribute("ItemName", "Assemblies"))),
                new System.Xml.Linq.XElement("WriteLinesToFile", new System.Xml.Linq.XAttribute("File", output), new System.Xml.Linq.XAttribute("Lines", "@(Assemblies->'%(FullPath)')"),
                    new System.Xml.Linq.XAttribute("Overwrite", "true"))))).Save(probe);
        result = ProcessRunner.Run("dotnet", ["msbuild", probe, "-t:Probe", "-m:1", "-nodeReuse:false", "-nologo"], root);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        Assert.Equal(runtime.Where(row => row[0].EndsWith("core.csproj", StringComparison.Ordinal)).Select(row => Path.GetFullPath(row[4])).Order(StringComparer.Ordinal),
            File.ReadAllLines(output).Select(Path.GetFullPath).Order(StringComparer.Ordinal));

        // Compare the consumer's reference closure with an ordinary SDK build.
        result = ProcessRunner.Run("dotnet", ["build", Path.Combine(root, "entry/entry.csproj"), "-c", "Release", "-m:1", "-nodeReuse:false",
            "-p:RestoreConfigFile=" + Path.Combine(root, "offline.config"), "-p:NuGetAudit=false", "-p:UseSharedCompilation=false"], root);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        var ordinary = File.ReadAllLines(Path.Combine(root, "entry/bin/Release/net10.0/references.txt")).Order(StringComparer.Ordinal);
        Assert.All(entries, row => Assert.Equal(ordinary, File.ReadAllLines(Path.Combine(row[2], "references.txt")).Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void FileProgramKeysArePortableAndIncludeTheBuildOrchestrator()
    {
        using var fixture = new OfflineFixtures();
        var roots = new[] { Path.Combine(fixture.DirectoryPath, "first"), Path.Combine(fixture.DirectoryPath, "second") };
        foreach (var root in roots)
        {
            foreach (var (relative, content) in new Dictionary<string, string> {
                ["build-tools/common/input.cs"] = "common", ["scripts/checks/sample.cs"] = "sample",
                ["build-tools/release/orchestrator.cs"] = "build", ["Version.props"] = "version", ["offline.config"] = "offline" })
            {
                var file = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, content);
            }
        }
        string Key(string root) => ReleaseCommands.CheckAllScriptInputKey(root, "sample", Path.Combine(root, "offline.config"), "sdk");
        var first = Key(roots[0]);
        Assert.Equal(first, Key(roots[1]));
        File.WriteAllText(Path.Combine(roots[1], "build-tools/release/orchestrator.cs"), "changed arguments");
        Assert.NotEqual(first, Key(roots[1]));
    }

    [Fact]
    public void OnlyColdMatchedEntriesCanBeRestoredForEvidenceAndMismatchEvicts()
    {
        using var fixture = new OfflineFixtures();
        var source = Path.Combine(fixture.DirectoryPath, "source"); Directory.CreateDirectory(source);
        var target = Path.Combine(fixture.DirectoryPath, "target");
        var cache = new BuildOutputCache(Path.Combine(fixture.DirectoryPath, "cache"), "library");
        var key = BuildOutputCache.Key([], ["same inputs"]);
        File.WriteAllText(Path.Combine(source, "library.dll"), "bytes");
        cache.Populate(key, source);
        Assert.False(cache.RestoreVerified(key, target));
        Assert.False(Directory.Exists(target));
        cache.RecordBuild(key, source, "cold-1", cold: true);
        Assert.True(cache.RestoreVerified(key, target));
        Assert.Equal(new[] { "cold-1" }, cache.Record(key)!.ColdRunIds);
        cache.RecordBuild(key, source, "cold-2", cold: true);
        Assert.Equal(new[] { "cold-1", "cold-2" }, cache.Record(key)!.ColdRunIds);
        cache.Populate(key, source);
        Assert.Equal("cold-2", cache.Record(key)!.RunId);
        var metadata = Path.Combine(cache.UnitDirectory, key, "files.json");
        var damaged = JsonNode.Parse(File.ReadAllText(metadata))!.AsObject(); damaged.Remove("runId");
        File.WriteAllText(metadata, damaged.ToJsonString());
        Assert.False(cache.RestoreVerified(key, target));
        File.WriteAllText(Path.Combine(source, "library.dll"), "different bytes");
        var error = Assert.Throws<BuildNonDeterminismException>(() => cache.RecordBuild(key, source, "cold-3", cold: true));
        Assert.Contains("library.dll", error.Message);
        Assert.Null(cache.Record(key));
        Assert.False(cache.RestoreVerified(key, target));
    }

    [Fact]
    public void VerifiedEntriesStillRejectCorruptPayloadsBeforeWritingAnything()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        var cache = new BuildOutputCache(Path.Combine(root, "cache"), "unit");
        var key = BuildOutputCache.Key([], []);
        File.WriteAllText(Path.Combine(source, "a.dll"), "a");
        cache.RecordBuild(key, source, "nightly", true);
        File.WriteAllText(Path.Combine(cache.UnitDirectory, key, "payload/a.dll"), "bad");
        Assert.False(cache.RestoreVerified(key, Path.Combine(root, "target")));
        Assert.False(Directory.Exists(Path.Combine(root, "target")));
    }

    [Fact]
    public void ReproducibilityFixtureIncludesCallerFilePathPdbAndRevisionStamp()
    {
        using var fixture = new OfflineFixtures();
        Assert.Equal(0, BuildReproducibility.Run(Repository.FindRoot(), Options.Parse(["-SelfTest", "-OutputDirectory", Path.Combine(fixture.DirectoryPath, "repro")],
            new HashSet<string> { "SelfTest" })));
        Assert.Equal(new[] { "a.dll", "missing.dll" }, BuildReproducibility.Differences([new("a.dll", "1"), new("missing.dll", "2")], [new("a.dll", "3")]));
    }

    [Fact]
    public void AdoptionRehashesEveryFileAndRejectsUnverifiedRestores()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        Directory.CreateDirectory(Path.Combine(root, "output")); File.WriteAllText(Path.Combine(root, "output/a.dll"), "bytes");
        var graph = Path.Combine(root, "graph.props");
        new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement("Project", new System.Xml.Linq.XElement("ItemGroup",
            new System.Xml.Linq.XElement("CheckGraphProject", new System.Xml.Linq.XAttribute("Include", Path.Combine(root, "library")),
                new System.Xml.Linq.XElement("AdditionalProperties", "CheckAllNodeId=id"))))).Save(graph);
        var cache = new JsonObject { ["mode"] = "verified-build-units", ["buildRoot"] = root, ["outputsRestored"] = 1,
            ["graphPath"] = graph, ["graphSha256"] = ReleaseRecords.HashFile(graph),
            ["failures"] = new JsonArray(), ["units"] = System.Text.Json.JsonSerializer.SerializeToNode(new[] {
                new CheckUnitEvidence("library id", new string('a', 64), "restored", "prior-run", ["nightly"],
                    [new BuildCacheOutput("output", BuildReproducibility.Inventory(Path.Combine(root, "output")))]) },
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }) };
        var problems = new List<string>();
        Assert.True(VerifiedBuildEvidence.Validate(root, cache, problems, 0)); Assert.Empty(problems);
        cache["units"]![0]!["coldRunIds"] = new JsonArray();
        Assert.False(VerifiedBuildEvidence.Validate(root, cache, problems, 0)); Assert.Contains(problems, problem => problem.Contains("unverified restored", StringComparison.Ordinal));
        problems.Clear(); cache["units"]![0]!["coldRunIds"] = new JsonArray("nightly");
        File.AppendAllText(Path.Combine(root, "output/a.dll"), "corrupted");
        Assert.False(VerifiedBuildEvidence.Validate(root, cache, problems, 0)); Assert.Contains(problems, problem => problem.Contains("hashes changed", StringComparison.Ordinal));
    }

    [Fact]
    public void ExecutableGraphRestoresVerifiedUnitsAndNoBuildCacheStillPopulatesColdEvidence()
    {
        using var fixture = new OfflineFixtures(); var root = Path.Combine(fixture.DirectoryPath, "first"); var repo = Repository.FindRoot();
        var sources = new Dictionary<string, string>();
        void Write(string relative, string content) { sources[relative] = content; var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); }
        foreach (var file in new[] { "CheckAll.Build.props", "CheckAll.Build.targets", "check-build/TiaMcp.CheckBuild.csproj", "check-build/CheckBuildTasks.cs" })
            Write("build-tools/release/" + file, File.ReadAllText(Path.Combine(repo, "build-tools/release", file)));
        Write("src/Shared/ReproducibleBuild.props", File.ReadAllText(Path.Combine(repo, "src/Shared/ReproducibleBuild.props")));
        Write("Directory.Build.props", "<Project><Import Project=\"src/Shared/ReproducibleBuild.props\" /></Project>");
        Write("build-tools/bundled-dotnet.json", "{}");
        Write("build-tools/native-call-weaver/NativeCallWeaver.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Write("leaf/leaf.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Write("leaf/Value.cs", "public class Value { public const int Number = 42; }\n");
        Write("entry/entry.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../leaf/leaf.csproj\" /></ItemGroup></Project>");
        Write("entry/Entry.cs", "public class Entry { public int Read() => Value.Number; }\n");
        Write("offline.config", "<configuration><packageSources><clear /></packageSources></configuration>");
        CheckBuild[] builds = [new("entry/entry.csproj", "", [])];
        var cache = Path.Combine(fixture.DirectoryPath, "cache");
        foreach (var mode in new[] { "cold", "warm", "nightly" })
        {
            if (mode == "warm")
            {
                root = Path.Combine(fixture.DirectoryPath, "different-second");
                foreach (var (relative, content) in sources.ToArray()) Write(relative, content);
            }
            var result = CheckAllBuild.Run(root, builds, Path.Combine(root, mode, "build"), "dotnet", null, Path.Combine(root, "offline.config"), 1,
                cold: mode != "warm", cacheRoot: cache, noCacheReads: mode == "nightly", runId: mode, keyParallelism: 4);
            Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
            var report = JsonNode.Parse(File.ReadAllText(Path.Combine(root, mode, "build-cache.json")))!;
            if (mode == "warm")
            {
                Assert.True((int)report["outputsRestored"]! > 0, report.ToJsonString());
                Assert.Contains("Verified cached unit", result.StandardOutput);
                var problems = new List<string>();
                Assert.True(VerifiedBuildEvidence.Validate(root, report.AsObject(), problems, 1), string.Join("\n", problems));
                Assert.Empty(problems);
            }
            else Assert.Equal(0, (int)report["outputsRestored"]!);
            Assert.Empty(report["failures"]!.AsArray());
        }
    }
}
