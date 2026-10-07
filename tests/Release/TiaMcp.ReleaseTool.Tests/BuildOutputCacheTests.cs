using System.Text.Json;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class BuildOutputCacheTests
{
    [Fact]
    public void KeyIncludesInputContentInventoryAndBuildProperties()
    {
        var inputs = new ReleaseArtifact[] { new("source.cs", "1"), new("project.csproj", "2"), new("sdk.dll", "3") };
        var key = BuildOutputCache.Key(inputs, ["sdk=10", "configuration=Release"]);
        Assert.Equal(key, BuildOutputCache.Key(inputs.Reverse(), ["sdk=10", "configuration=Release"]));
        Assert.NotEqual(key, BuildOutputCache.Key([.. inputs, new("linked.cs", "4")], ["sdk=10", "configuration=Release"]));
        Assert.NotEqual(key, BuildOutputCache.Key(inputs[..2], ["sdk=10", "configuration=Release"]));
        Assert.NotEqual(key, BuildOutputCache.Key([inputs[0] with { Sha256 = "changed" }, inputs[1], inputs[2]], ["sdk=10", "configuration=Release"]));
        Assert.NotEqual(key, BuildOutputCache.Key(inputs, ["sdk=11", "configuration=Release"]));
    }

    [Fact]
    public void LoggingAndArgumentOrderDoNotInvalidateBuildOutputs()
    {
        var first = ReleaseCommands.CacheArguments("project.csproj", ["build", "project.csproj", "-c", "Release", "-p:Version=4.0.0", "-p:TiaReleaseKey=20", "-v:q", "--no-restore"]);
        var second = ReleaseCommands.CacheArguments("project.csproj", ["build", "other-spelling.csproj", "-c", "Release", "-p:TiaReleaseKey=20", "-p:Version=4.0.0", "-v:minimal", "--nologo"]);
        Assert.Equal(first, second);
        Assert.NotEqual(first, ReleaseCommands.CacheArguments("project.csproj", ["build", "project.csproj", "-c", "Release", "-p:Version=4.0.0", "-p:TiaReleaseKey=21"]));
    }

    [Theory]
    [InlineData("change")]
    [InlineData("remove")]
    [InlineData("add")]
    [InlineData("manifest")]
    public void CorruptCacheIsAMissWithoutChangingTheDestination(string corruption)
    {
        using var fixture = new CacheFixture();
        fixture.Cache.Populate(fixture.Key, fixture.Source);
        var payload = Path.Combine(fixture.Cache.UnitDirectory, fixture.Key, "payload");
        if (corruption == "change") File.AppendAllText(Path.Combine(payload, "engine.exe"), "corrupt");
        if (corruption == "remove") File.Delete(Path.Combine(payload, "engine.exe"));
        if (corruption == "add") File.WriteAllText(Path.Combine(payload, "extra.dll"), "extra");
        if (corruption == "manifest") File.WriteAllText(Path.Combine(fixture.Cache.UnitDirectory, fixture.Key, "files.json"), "{}");
        File.WriteAllText(Path.Combine(fixture.Destination, "sentinel"), "keep");
        Assert.False(fixture.Cache.Restore(fixture.Key, fixture.Destination));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(fixture.Destination, "sentinel")));
    }

    [Fact]
    public void VerifiedHitRestoresCompleteOutputAndRemovesStaleFiles()
    {
        using var fixture = new CacheFixture();
        fixture.Cache.Populate(fixture.Key, fixture.Source);
        File.WriteAllText(Path.Combine(fixture.Destination, "stale.dll"), "stale");
        Assert.True(fixture.Cache.Restore(fixture.Key, fixture.Destination));
        Assert.False(File.Exists(Path.Combine(fixture.Destination, "stale.dll")));
        Assert.Equal("engine", File.ReadAllText(Path.Combine(fixture.Destination, "engine.exe")));
        Assert.Equal("nested", File.ReadAllText(Path.Combine(fixture.Destination, "bridge/adapter.dll")));
        Assert.Empty(System.IO.Directory.EnumerateDirectories(fixture.Cache.UnitDirectory, ".pending-*"));
    }

    [Fact]
    public void UnitIndexIsReadableAndSurvivesCorruptIndexRecovery()
    {
        using var fixture = new CacheFixture();
        var created = new DateTime(2026, 10, 7, 1, 0, 0, DateTimeKind.Utc);
        var time = created;
        var cache = new BuildOutputCache(fixture.Directory, "engine-v20", clock: () => time);
        cache.Populate(fixture.Key, fixture.Source);
        var storedBytes = 12 + new FileInfo(Path.Combine(cache.UnitDirectory, fixture.Key, "files.json")).Length;
        var index = Path.Combine(cache.UnitDirectory, "index.json");
        using (var document = JsonDocument.Parse(File.ReadAllText(index)))
        {
            Assert.Equal("engine-v20", document.RootElement.GetProperty("unit").GetString());
            var entry = document.RootElement.GetProperty("entries")[0];
            Assert.Equal(fixture.Key, entry.GetProperty("inputHash").GetString());
            Assert.Equal(created, entry.GetProperty("createdUtc").GetDateTime());
            Assert.Equal(storedBytes, entry.GetProperty("sizeBytes").GetInt64());
        }
        File.WriteAllText(index, "broken");
        time = time.AddMinutes(1);
        Assert.True(cache.Restore(fixture.Key, fixture.Destination));
        var info = BuildOutputCache.Info(fixture.Directory);
        Assert.Equal(storedBytes, info.SizeBytes);
        var recovered = Assert.Single(Assert.Single(info.Units).Entries);
        Assert.Equal(created, recovered.CreatedUtc);
        Assert.Equal(time, recovered.LastHitUtc);
        Assert.Contains("\n", File.ReadAllText(index));
    }

    [Fact]
    public void UnreadableMetadataStillCountsTowardTheCapAndCanBeCleared()
    {
        using var fixture = new CacheFixture();
        fixture.Cache.Populate(fixture.Key, fixture.Source);
        File.WriteAllText(Path.Combine(fixture.Cache.UnitDirectory, "index.json"), "broken");
        File.WriteAllText(Path.Combine(fixture.Cache.UnitDirectory, fixture.Key, "files.json"), "broken");
        Assert.False(fixture.Cache.Restore(fixture.Key, fixture.Destination));
        Assert.Equal(18, BuildOutputCache.Info(fixture.Directory).SizeBytes);
        Assert.Equal(1, BuildOutputCache.Clear(fixture.Directory));
        Assert.Equal(0, BuildOutputCache.Info(fixture.Directory).SizeBytes);
    }

    [Fact]
    public void SizeCapPrunesAcrossUnitsByLastSuccessfulHit()
    {
        using var fixture = new CacheFixture();
        var time = new DateTime(2026, 10, 7, 1, 0, 0, DateTimeKind.Utc);
        var first = new BuildOutputCache(fixture.Directory, "engine-v20", clock: () => time);
        first.Populate(fixture.Key, fixture.Source);
        var limit = BuildOutputCache.Info(fixture.Directory).SizeBytes * 2;
        first = new BuildOutputCache(fixture.Directory, "engine-v20", limit, () => time);
        var second = new BuildOutputCache(fixture.Directory, "studio-v20", limit, () => time);
        time = time.AddMinutes(1);
        second.Populate(fixture.Key, fixture.Source);
        time = time.AddMinutes(1);
        Assert.True(first.Restore(fixture.Key, fixture.Destination));
        time = time.AddMinutes(1);
        var newer = BuildOutputCache.Key([], ["newer input"]);
        first.Populate(newer, fixture.Source);
        Assert.False(second.Restore(fixture.Key, fixture.Destination));
        Assert.True(first.Restore(fixture.Key, fixture.Destination));
        Assert.True(first.Restore(newer, fixture.Destination));
        Assert.Equal(limit, BuildOutputCache.Info(fixture.Directory, limit).SizeBytes);
    }

    [Fact]
    public void OversizeEntryIsNotRetainedAndClearCanSelectOneUnit()
    {
        using var fixture = new CacheFixture();
        var small = new BuildOutputCache(fixture.Directory, "small-limit", 11);
        small.Populate(fixture.Key, fixture.Source);
        Assert.False(small.Restore(fixture.Key, fixture.Destination));
        Assert.Equal(0, BuildOutputCache.Info(fixture.Directory).SizeBytes);
        fixture.Cache.Populate(fixture.Key, fixture.Source);
        new BuildOutputCache(fixture.Directory, "studio").Populate(fixture.Key, fixture.Source);
        var unrelated = Path.Combine(fixture.Directory, "keep.txt");
        File.WriteAllText(unrelated, "keep");
        Assert.Equal(1, BuildOutputCache.Clear(fixture.Directory, "engine-v20"));
        Assert.False(fixture.Cache.Restore(fixture.Key, fixture.Destination));
        Assert.True(new BuildOutputCache(fixture.Directory, "studio").Restore(fixture.Key, fixture.Destination));
        Assert.Equal(1, BuildOutputCache.Clear(fixture.Directory));
        Assert.Equal("keep", File.ReadAllText(unrelated));
        Assert.Equal(0, BuildOutputCache.Info(fixture.Directory).SizeBytes);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("not-a-sha256")]
    public void UnsafeEntryNamesAreRejectedBeforeWriting(string key)
    {
        using var fixture = new CacheFixture();
        Assert.Throws<IOException>(() => fixture.Cache.Populate(key, fixture.Source));
        Assert.False(fixture.Cache.Restore(key, fixture.Destination));
    }

    [Fact]
    public void CacheCommandsAcceptAnExplicitDirectoryAndReportInvalidCaps()
    {
        using var fixture = new CacheFixture();
        fixture.Cache.Populate(fixture.Key, fixture.Source);
        Options Parse(params string[] args) => Options.Parse(args, new HashSet<string>());
        Assert.Equal(0, ReleaseCommands.Run("cache-info", Parse("-BuildCacheDirectory", fixture.Directory, "-BuildCacheMaxBytes", "24")));
        Assert.Equal(64, Assert.Throws<ReleaseException>(() => ReleaseCommands.Run("cache-info", Parse("-BuildCacheDirectory", fixture.Directory, "-BuildCacheMaxBytes", "0"))).ExitCode);
        Assert.Equal(0, ReleaseCommands.Run("cache-clear", Parse("-BuildCacheDirectory", fixture.Directory, "-Unit", "engine-v20")));
        Assert.Equal(0, BuildOutputCache.Info(fixture.Directory).SizeBytes);
    }

    [Fact]
    public void InputChangeInvalidatesExactlyTheEvaluatedDependentProjects()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "TiaPortalOpenness.slnx"))) repo = repo.Parent;
        var root = Path.Combine(repo!.FullName, "bin-build/P6-66/key-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var config = Path.Combine(root, "nuget.config");
            File.WriteAllText(config, "<configuration><packageSources><clear /></packageSources></configuration>");
            foreach (var name in new[] { "Shared", "Worker20", "Studio" })
            {
                var folder = Path.Combine(root, name);
                System.IO.Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "Input.cs"), "public class " + name + " {}\n");
                File.WriteAllText(Path.Combine(folder, name + ".csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" +
                    (name == "Worker20" ? "<ItemGroup><ProjectReference Include=\"../Shared/Shared.csproj\" /><Compile Include=\"../linked.cs\" /></ItemGroup>" : "") + "</Project>");
            }
            var linked = Path.Combine(root, "linked.cs");
            File.WriteAllText(linked, "public class Linked {}\n");
            string Project(string name) => Path.Combine(root, name, name + ".csproj");
            foreach (var name in new[] { "Worker20", "Studio" })
                ProcessRunner.RequireSuccess(ProcessRunner.Run("dotnet", ["restore", Project(name), "--configfile", config, "-p:NuGetAudit=false"], root), "Restore cache fixture");
            string Key(string name) => ReleaseCommands.BuildKey("dotnet", Project(name), ["build", Project(name), "-c", "Release"]).Key;
            var before = new[] { Key("Shared"), Key("Worker20"), Key("Studio") };
            File.AppendAllText(linked, "// linked change\n");
            Assert.Equal(before[0], Key("Shared"));
            Assert.NotEqual(before[1], Key("Worker20"));
            Assert.Equal(before[2], Key("Studio"));
            var worker = Key("Worker20");
            File.AppendAllText(Path.Combine(root, "Shared/Input.cs"), "// shared change\n");
            Assert.NotEqual(before[0], Key("Shared"));
            Assert.NotEqual(worker, Key("Worker20"));
            Assert.Equal(before[2], Key("Studio"));
        }
        finally { System.IO.Directory.Delete(root, true); }
    }

    [Fact]
    public void ReferencedMultiTargetImportsRetainEveryEvaluationContext()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "TiaPortalOpenness.slnx"))) repo = repo.Parent;
        var root = Path.Combine(repo!.FullName, "bin-build/P6-66/key-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var config = Path.Combine(root, "nuget.config");
            File.WriteAllText(config, "<configuration><packageSources><clear /></packageSources></configuration>");
            var import = Path.Combine(root, "first.props");
            File.WriteAllText(import, "<Project><PropertyGroup><DefineConstants>FIRST</DefineConstants></PropertyGroup></Project>");
            foreach (var name in new[] { "Multi", "Consumer", "Unrelated" })
            {
                var folder = Path.Combine(root, name);
                System.IO.Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "Input.cs"), "public class " + name + " {}\n");
                var framework = name == "Multi" ? "<TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks>" : "<TargetFramework>net10.0</TargetFramework>";
                var body = name == "Multi" ? "<Import Project=\"../first.props\" Condition=\"'$(TargetFramework)' == 'net10.0'\" />" :
                    name == "Consumer" ? "<ItemGroup><ProjectReference Include=\"../Multi/Multi.csproj\" /></ItemGroup>" : "";
                File.WriteAllText(Path.Combine(folder, name + ".csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" + framework + "</PropertyGroup>" + body + "</Project>");
            }
            string Project(string name) => Path.Combine(root, name, name + ".csproj");
            foreach (var name in new[] { "Consumer", "Unrelated" })
                ProcessRunner.RequireSuccess(ProcessRunner.Run("dotnet", ["restore", Project(name), "--configfile", config, "-p:NuGetAudit=false"], root), "Restore multi-target cache fixture");
            string Key(string name) => ReleaseCommands.BuildKey("dotnet", Project(name), ["build", Project(name), "-c", "Release"]).Key;
            var dependent = Key("Consumer");
            var unrelated = Key("Unrelated");
            File.WriteAllText(import, "<Project><PropertyGroup><DefineConstants>CHANGED_FIRST</DefineConstants></PropertyGroup></Project>");
            Assert.NotEqual(dependent, Key("Consumer"));
            Assert.Equal(unrelated, Key("Unrelated"));
        }
        finally { System.IO.Directory.Delete(root, true); }
    }

    private sealed class CacheFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "build-cache-tests-" + Guid.NewGuid().ToString("N"));
        internal string Directory => Path.Combine(root, "cache");
        internal string Source => Path.Combine(root, "source");
        internal string Destination => Path.Combine(root, "destination");
        internal string Key => BuildOutputCache.Key([new("input.cs", "hash")], ["Release"]);
        internal BuildOutputCache Cache => new(Directory, "engine-v20");
        internal CacheFixture()
        {
            System.IO.Directory.CreateDirectory(Path.Combine(Source, "bridge"));
            System.IO.Directory.CreateDirectory(Destination);
            File.WriteAllText(Path.Combine(Source, "engine.exe"), "engine");
            File.WriteAllText(Path.Combine(Source, "bridge/adapter.dll"), "nested");
        }
        public void Dispose() => System.IO.Directory.Delete(root, true);
    }
}
