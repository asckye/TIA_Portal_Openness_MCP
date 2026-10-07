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

    [Fact]
    public void RootNormalizationCoversCaseSeparatorsJsonAndPathBoundaries()
    {
        var root = Path.Combine(Path.GetTempPath(), "cache-repo");
        foreach (var spelling in new[] { root, root.ToUpperInvariant(), root.Replace('\\', '/'), root.Replace("\\", "\\\\") })
        {
            Assert.Equal("<repo>/src/input.cs", ReleaseCommands.NormalizeCacheText(spelling + "/src/input.cs", root));
            Assert.Equal("<repo>", ReleaseCommands.NormalizeCacheText(spelling, root));
        }
        Assert.Equal(root + "-external/input.cs", ReleaseCommands.NormalizeCacheText(root + "-external/input.cs", root));
        Assert.Equal("src/input.cs", ReleaseCommands.CacheInputPath(Path.Combine(root, "src/input.cs"), root));
        var external = Path.GetFullPath(root + "-external/input.cs");
        Assert.Equal(external, ReleaseCommands.CacheInputPath(external, root));
    }

    [Fact]
    public void OnlyBuildEnvironmentIsKeyedAndItsRepositoryPathsArePortable()
    {
        var firstRoot = Path.Combine(Path.GetTempPath(), "first-cache-repo");
        var secondRoot = Path.Combine(Path.GetTempPath(), "second-cache-repo");
        var first = new Dictionary<string, string?>
        {
            ["DOTNET_ROLL_FORWARD"] = "LatestMajor", ["MSBuildSDKsPath"] = Path.Combine(firstRoot, "sdk"),
            ["NUGET_PACKAGES"] = Path.Combine(firstRoot, "packages"), ["Configuration"] = "Release", ["Platform"] = "x64",
            ["TIA_MCP_SHARED_INPUT"] = Path.Combine(firstRoot, "input"), ["UseSharedCompilation"] = "false", ["NuGetAudit"] = "false",
            ["UNRELATED_RELEASE_SESSION"] = "one", ["DOTNET_CLI_HOME"] = "one", ["TIA_MCP_BUILD_CACHE_DIRECTORY"] = "one",
            ["TIA_MCP_TEST_PUBLIC_API_ROOT"] = "one", ["TIA_MCP_OFFLINE_NUGET_CONFIG"] = "one", ["TEMP"] = "one"
        };
        var second = first.ToDictionary(row => row.Key.ToLowerInvariant(), row => (string?)row.Value!.Replace(firstRoot, secondRoot));
        foreach (var name in new[] { "unrelated_release_session", "dotnet_cli_home", "tia_mcp_build_cache_directory", "tia_mcp_test_public_api_root", "tia_mcp_offline_nuget_config", "temp" }) second[name] = "two";
        var before = ReleaseCommands.CacheEnvironment(firstRoot, first);
        Assert.Equal(8, before.Length);
        Assert.Equal(before, ReleaseCommands.CacheEnvironment(secondRoot, second));
        second["configuration"] = "Debug";
        Assert.NotEqual(before, ReleaseCommands.CacheEnvironment(secondRoot, second));
    }

    [Fact]
    public void CacheRestoreUsesTheBuildPropertiesIncludingPublishAndOfflineConfig()
    {
        var args = ReleaseCommands.CacheRestoreArguments("project.csproj", ["publish", "project.csproj", "-c", "Release", "-f", "net10.0",
            "-r", "win-x64", "-o", "published", "--no-restore", "--nologo", "-p:RestoreConfigFile=offline.config", "-p:TiaReleaseKey=20"]);
        Assert.Equal("msbuild", args[0]);
        foreach (var property in new[] { "-t:Restore", "-p:Configuration=Release", "-p:TargetFramework=net10.0", "-p:RuntimeIdentifier=win-x64",
            "-p:PublishDir=published", "-p:RestoreConfigFile=offline.config", "-p:TiaReleaseKey=20", "-p:_IsPublishing=true" }) Assert.Contains(property, args);
        Assert.DoesNotContain("--no-restore", args);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("publish")]
    public void CleanProjectRestoresBeforeKeyingAndHitsWithoutCompilingAgain(string command)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "TiaPortalOpenness.slnx"))) repo = repo.Parent;
        var root = Path.Combine(repo!.FullName, "bin-build/P6-66/key-tests", Guid.NewGuid().ToString("N"));
        var cacheVariable = "TIA_MCP_BUILD_CACHE_DIRECTORY";
        var disabledVariable = "TIA_MCP_BUILD_CACHE_DISABLED";
        var priorCache = Environment.GetEnvironmentVariable(cacheVariable);
        var priorDisabled = Environment.GetEnvironmentVariable(disabledVariable);
        try
        {
            var projectFolder = Path.Combine(root, "unit");
            System.IO.Directory.CreateDirectory(projectFolder);
            var project = Path.Combine(projectFolder, "unit.csproj");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(projectFolder, "input.cs"), "public class Input {}\n");
            var config = Path.Combine(root, "nuget.config");
            File.WriteAllText(config, "<configuration><packageSources><clear /></packageSources></configuration>");
            var args = new List<string> { command, project, "-c", "Release", "--no-restore", "-p:RestoreConfigFile=" + config, "-p:NuGetAudit=false" };
            var published = Path.Combine(root, "published");
            if (command == "publish") args.AddRange(["-o", published]);
            var original = args.ToArray();
            var invocations = new List<string[]>();
            CommandResult Run()
            {
                invocations.Add(args.ToArray());
                return ProcessRunner.Run("dotnet", args, repo.FullName);
            }
            Environment.SetEnvironmentVariable(cacheVariable, Path.Combine(root, "cache"));
            Environment.SetEnvironmentVariable(disabledVariable, "0");
            Assert.False(File.Exists(Path.Combine(projectFolder, "obj/project.assets.json")));
            Assert.Equal(0, ReleaseCommands.CachedBuild("dotnet", args, Run).ExitCode);
            Assert.Equal("msbuild", invocations[0][0]);
            Assert.Contains("-t:Restore", invocations[0]);
            Assert.Equal(command == "publish" ? 3 : 2, invocations.Count);
            var output = command == "publish" ? Path.Combine(published, "unit.dll") : Path.Combine(projectFolder, "bin/Release/net10.0/unit.dll");
            var hash = ReleaseRecords.HashFile(output);
            System.IO.Directory.Delete(Path.Combine(projectFolder, "obj"), true);
            System.IO.Directory.Delete(Path.Combine(projectFolder, "bin"), true);
            if (System.IO.Directory.Exists(published)) System.IO.Directory.Delete(published, true);
            invocations.Clear();
            Assert.Equal(0, ReleaseCommands.CachedBuild("dotnet", args, Run).ExitCode);
            Assert.Equal("msbuild", Assert.Single(invocations)[0]);
            Assert.Equal(hash, ReleaseRecords.HashFile(output));
            Assert.Equal(original, args);
        }
        finally
        {
            Environment.SetEnvironmentVariable(cacheVariable, priorCache);
            Environment.SetEnvironmentVariable(disabledVariable, priorDisabled);
            if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
        }
    }

    [Fact]
    public void EveryBuildUnitHasTheSameKeyInTwoRepositoryLocations()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "TiaPortalOpenness.slnx"))) repo = repo.Parent;
        var parent = Path.Combine(repo!.FullName, "bin-build/P6-66/key-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var roots = new[] { Path.Combine(parent, "first"), Path.Combine(parent, "different-second") };
            var units = new[] { "worker-14sp1", "worker-15.1", "worker-16", "worker-17", "worker-18", "worker-19", "worker-20", "worker-21",
                "engine-20", "engine-21", "foundation-host", "studio", "gui", "adapter", "harness", "weaver", "release-tool" };
            foreach (var root in roots)
            {
                System.IO.Directory.CreateDirectory(Path.Combine(root, "build-tools/release"));
                File.WriteAllText(Path.Combine(root, "build-tools/release/orchestrator.cs"), "// identical orchestrator\n");
                System.IO.Directory.CreateDirectory(Path.Combine(root, "scripts/build"));
                File.WriteAllText(Path.Combine(root, "scripts/build/bundled-dotnet.json"), "{}");
                File.WriteAllText(Path.Combine(root, "nuget.config"), "<configuration><packageSources><clear /></packageSources></configuration>");
                File.WriteAllText(Path.Combine(root, "linked.cs"), "public class Linked {}\n");
                var folder = Path.Combine(root, "unit");
                System.IO.Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "input.cs"), "public class Input {}\n");
                File.WriteAllText(Path.Combine(folder, "unit.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" +
                    "<ItemGroup><Compile Include=\"../linked.cs\" /></ItemGroup></Project>");
                ProcessRunner.RequireSuccess(ProcessRunner.Run("dotnet", ["restore", Path.Combine(folder, "unit.csproj"), "--configfile", Path.Combine(root, "nuget.config"), "-p:NuGetAudit=false"], root), "Restore portable key fixture");
            }
            foreach (var unit in units)
            {
                string Key(string root, string session)
                {
                    var project = Path.Combine(root, "unit/unit.csproj");
                    return ReleaseCommands.BuildKey("dotnet", project, [unit is "foundation-host" or "gui" ? "publish" : "build", project, "-c", "Release",
                        "-p:TiaReleaseKey=" + unit, "-p:AdapterSourceRoot=" + root, "-p:RestoreConfigFile=" + Path.Combine(root, "nuget.config")], root,
                        new Dictionary<string, string?> { ["UNRELATED_RELEASE_SESSION"] = session }).Key;
                }
                Assert.Equal(Key(roots[0], "first"), Key(roots[1], "second"));
            }
            // The legacy csc unit shares the same input and argument normalization.
            string Configurator(string root) => BuildOutputCache.Key([new(ReleaseCommands.CacheInputPath(Path.Combine(root, "linked.cs"), root), ReleaseRecords.HashFile(Path.Combine(root, "linked.cs")))],
                new[] { "/out:" + Path.Combine(root, "TiaOpenness.exe"), Path.Combine(root, "linked.cs") }.Select(arg => ReleaseCommands.NormalizeCacheText(arg, root)));
            Assert.Equal(Configurator(roots[0]), Configurator(roots[1]));
        }
        finally { if (System.IO.Directory.Exists(parent)) System.IO.Directory.Delete(parent, true); }
    }

    [Fact]
    public void WeaverBytesUnderAnotherUnitsOutputAreKeyInputs()
    {
        // Woven assemblies embed the weaver's SHA-256; reusing one woven by other weaver bytes fails verification.
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "TiaPortalOpenness.slnx"))) repo = repo.Parent;
        var parent = Path.Combine(repo!.FullName, "bin-build/P6-66/key-tests", Guid.NewGuid().ToString("N"));
        try
        {
            string Key(string root, string weaverBytes)
            {
                System.IO.Directory.CreateDirectory(Path.Combine(root, "build-tools/release"));
                File.WriteAllText(Path.Combine(root, "build-tools/release/orchestrator.cs"), "// identical orchestrator\n");
                System.IO.Directory.CreateDirectory(Path.Combine(root, "scripts/build"));
                File.WriteAllText(Path.Combine(root, "scripts/build/bundled-dotnet.json"), "{}");
                File.WriteAllText(Path.Combine(root, "nuget.config"), "<configuration><packageSources><clear /></packageSources></configuration>");
                var weaver = Path.Combine(root, "weaver/bin/Release/net10.0/Weaver.dll");
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(weaver)!);
                File.WriteAllText(weaver, weaverBytes);
                var folder = Path.Combine(root, "unit");
                System.IO.Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "input.cs"), "public class Input {}\n");
                File.WriteAllText(Path.Combine(folder, "unit.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" +
                    "<ItemGroup><CustomAdditionalCompileInputs Include=\"$(NativeCallWeaverPath)\" /></ItemGroup></Project>");
                var project = Path.Combine(folder, "unit.csproj");
                ProcessRunner.RequireSuccess(ProcessRunner.Run("dotnet", ["restore", project, "--configfile", Path.Combine(root, "nuget.config"), "-p:NuGetAudit=false"], root), "Restore weaver key fixture");
                return ReleaseCommands.BuildKey("dotnet", project, ["build", project, "-c", "Release", "-p:NativeCallWeaverPath=" + weaver,
                    "-p:RestoreConfigFile=" + Path.Combine(root, "nuget.config")], root).Key;
            }
            var first = Key(Path.Combine(parent, "first"), "weaver A");
            Assert.Equal(first, Key(Path.Combine(parent, "second"), "weaver A"));
            Assert.NotEqual(first, Key(Path.Combine(parent, "third"), "weaver B"));
        }
        finally { if (System.IO.Directory.Exists(parent)) System.IO.Directory.Delete(parent, true); }
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
