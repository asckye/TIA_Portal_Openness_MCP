using TiaMcp.Engine.Tests.Shared;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.BuildCommon;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class PlainReleaseTests
{
    [Theory]
    [InlineData("tests/Studio/TiaOpenness.Core.Tests/TiaOpenness.Core.Tests.csproj")]
    [InlineData("tests/Studio/TiaOpenness.Gui.Tests/TiaOpenness.Gui.Tests.csproj")]
    public void StudioShardsLeaveBundleSelectionToTheFixture(string project)
    {
        using var tree = new StaticTree();
        var suite = new JsonObject { ["arguments"] = new JsonArray() };
        var previous = Environment.GetEnvironmentVariable("TIA_MCP_BUNDLE_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_BUNDLE_ROOT", tree.Root);
            var result = FastSuites.RunShard(tree.Root, "studio", suite, tree.Root, "dotnet", null,
                new(project, "", []), 0, 1, process: (_, _, _, environment) =>
                {
                    Assert.True(environment!.ContainsKey("TIA_MCP_BUNDLE_ROOT"));
                    Assert.Null(environment["TIA_MCP_BUNDLE_ROOT"]);
                    DotnetSuiteSelfTests.SyntheticTrx(Path.Combine(tree.Root, "studio.trx"), ["Passed"]);
                    return new(0, "passed", "");
                });
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(tree.Root, Environment.GetEnvironmentVariable("TIA_MCP_BUNDLE_ROOT"));
        }
        finally { Environment.SetEnvironmentVariable("TIA_MCP_BUNDLE_ROOT", previous); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FoundationBuildRunsOnlyWhenNotAlreadyBuilt(bool prebuilt)
    {
        using var tree = new StaticTree();
        var catalog = new JsonObject { ["foundation-api"] = new JsonObject {
            ["project"] = "fixture.csproj", ["arguments"] = new JsonArray(), ["minimumPassed"] = 1, ["maximumSkipped"] = 0 } };
        int builds = 0;
        var results = ReleaseFoundationStage.Run(tree.Root, catalog, tree.Root, "sdk", "dotnet", null, 1, _ => { },
            run: (options, _, _) =>
            {
                Assert.True(options.Has("NoBuild"));
                var directory = options.Get("ResultsDirectory")!;
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "foundation-api.json"), "{}");
                return new(0, "passed", "");
            },
            process: (_, arguments, _, _) =>
            {
                Assert.False(prebuilt);
                Assert.Contains("-p:UseSharedCompilation=false", arguments);
                Assert.Contains("-p:NuGetAudit=false", arguments);
                builds++;
                return new(0, "", "");
            }, prebuilt: prebuilt);
        Assert.Equal(prebuilt ? 0 : 1, builds);
        Assert.Equal(0, results["foundation-api"].ExitCode);
    }

    [Theory]
    [InlineData(3, false, false, 1)]
    [InlineData(100, false, false, 8)]
    [InlineData(3, true, false, 1)]
    [InlineData(3, false, true, 1)]
    public void ShardsRespectSmallSuitesAndSdkRegistration(int minimum, bool sdk, bool foundation, int expectedShards)
    {
        using var tree = new StaticTree();
        var name = foundation ? "foundation-api" : "fixture";
        var suite = new JsonObject { ["project"] = "fixture.csproj", ["arguments"] = new JsonArray(),
            ["minimumPassed"] = minimum, ["maximumSkipped"] = 0, ["publicApiRoot"] = sdk };
        var options = Options.Parse(["-Suite", name, "-Shards", "8", "-PublicApiRoot", "sdk-root", "-ResultsDirectory", tree.Root], Program.BooleanOptions);
        int tests = 0;
        var result = PlainShards.Run(tree.Root, options, new JsonObject { [name] = suite }, (_, arguments, _, env) =>
        {
            var args = arguments.ToArray();
            Assert.Equal(sdk || foundation, args.Contains("-p:TiaPublicApiRoot=sdk-root"));
            Assert.Equal(sdk || foundation ? "sdk-root" : null, env!["TIA_MCP_TEST_PUBLIC_API_ROOT"]);
            Assert.Equal(expectedShards.ToString(), env["TIA_MCP_TEST_SHARD_COUNT"]);
            Assert.Equal("5m", SuiteHang.Timeout);
            Assert.All(SuiteHang.TestArguments, flag => Assert.Contains(flag, args));
            var directory = args[Array.IndexOf(args, "--results-directory") + 1];
            var path = Path.Combine(directory, name + ".trx");
            DotnetSuiteSelfTests.SyntheticTrx(path, Enumerable.Repeat("Passed", minimum).ToArray());
            var document = XDocument.Load(path);
            foreach (var attribute in document.Descendants().Attributes().Where(a => a.Name.LocalName is "id" or "testId" or "executionId" or "testName"))
                attribute.Value += "-" + env["TIA_MCP_TEST_SHARD_INDEX"];
            document.Save(path);
            Interlocked.Increment(ref tests);
            return new(0, "passed", "");
        });
        Assert.Equal(expectedShards, tests);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void AbortedShardsCollectDiagnosticsWithoutChangingTheHangDeadlineOrFailureGate()
    {
        using var fixture = new OfflineFixtures(); var directory = Path.Combine(fixture.DirectoryPath, "shard");
        var suite = new JsonObject { ["project"] = "tests/fixture.csproj", ["arguments"] = new JsonArray(), ["minimumPassed"] = 1, ["maximumSkipped"] = 0 };
        var result = FastSuites.RunShard(fixture.DirectoryPath, "fixture", suite, directory, "dotnet", null, new("tests/fixture.csproj", "", []), 0, 1,
            diagnostics: true, process: (_, args, _, _) => {
                Assert.Equal("5m", SuiteHang.Timeout);
                Assert.All(SuiteHang.TestArguments, flag => Assert.Contains(flag, args));
                Assert.Contains(Path.Combine(directory, "fixture.vstest.log") + ";TraceLevel=Info", args);
                DotnetSuiteSelfTests.SyntheticTrx(Path.Combine(directory, "fixture.trx"), ["Passed"]);
                File.WriteAllText(Path.Combine(directory, "Sequence_crash.xml"), "<TestSequence><Test Name=\"Unfinished\" DisplayName=\"Unfinished\" Completed=\"False\" /></TestSequence>");
                return new(1, "testhost crashed", "");
            });
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("VSTest hang/aborted test: Unfinished", result.StandardError);
        Assert.Contains("mini dump: not produced", result.StandardError);
        Assert.Equal("5m", (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "fixture.hang.json")))!["timeout"]);
    }


    [Fact]
    public void ShardedSdkRootFallsBackToTheEnvironment()
    {
        var previous = Environment.GetEnvironmentVariable("TIA_MCP_TEST_PUBLIC_API_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_TEST_PUBLIC_API_ROOT", "fallback-sdk");
            var suite = new JsonObject { ["publicApiRoot"] = true };
            var options = Options.Parse([], Program.BooleanOptions);
            Assert.Equal("fallback-sdk", PlainShards.PublicApiRoot("fixture", suite, options));
            Assert.Equal("fallback-sdk", PlainShards.PublicApiRoot("foundation-api", new JsonObject(), options));
            Assert.Null(PlainShards.PublicApiRoot("fixture", new JsonObject(), options));
            Assert.Equal("explicit-sdk", PlainShards.PublicApiRoot("fixture", suite,
                Options.Parse(["-PublicApiRoot", "explicit-sdk"], Program.BooleanOptions)));
        }
        finally { Environment.SetEnvironmentVariable("TIA_MCP_TEST_PUBLIC_API_ROOT", previous); }
    }

    [Fact]
    public void OnlyTheRetainedCommandsArePublic()
    {
        string[] expected = ["prepare", "publish-prepared", "upload-draft", "merge-check", "generate-catalogs", "generate-tool-usage",
            "generate-tool-capability-matrix", "generate-tool-counts", "generate-release-migration", "generate-prerelease-notes", "check-tools-list", "audit-version-tools",
            "audit-tool-usage", "check-analyzer-warnings", "check-ratchet", "check-adapter-boundary", "check-bundle-layout", "check-repository",
            "check-dead-tool-references", "check-tia-features", "check-script-tool-calls", "check-envelope-rewrite", "test-suites",
            "build-file-programs", "build-solutions", "build-plc-worker-fixtures", "prepare-decoder-python", "get-bundled-dotnet", "publish-vci-watch", "wrap-long-lines", "dump-il"];
        Assert.Equal(expected.Order(), CommandLine.Commands.Order());
        Assert.DoesNotContain("host-parity", CommandLine.Commands);
    }

    [Theory]
    [InlineData("feature", true, "", true)]
    [InlineData("feature", true, " M src/file.cs", false)]
    [InlineData("feature", false, "", false)]
    [InlineData("master", false, "", true)]
    public void BuildOnlyAcceptsAnyCleanBranch(string branch, bool buildOnly, string status, bool accepted)
    {
        if (accepted) Prepare.RequireRepository(status, branch, buildOnly);
        else Assert.Throws<ReleaseException>(() => Prepare.RequireRepository(status, branch, buildOnly));
    }

    [Fact]
    public void FoundationDefaultsToPlainShardsAfterOneBuild()
    {
        using var tree = new StaticTree();
        var catalog = new JsonObject { ["foundation-api"] = new JsonObject {
            ["project"] = "fixture.csproj", ["arguments"] = new JsonArray(), ["minimumPassed"] = 100, ["maximumSkipped"] = 0 } };
        int builds = 0, tests = 0;
        var results = ReleaseFoundationStage.Run(tree.Root, catalog, tree.Root, "sdk", "dotnet", null, 2, _ => { },
            process: (_, arguments, _, environment) =>
            {
                var args = arguments.ToArray();
                if (args[0] == "build") Interlocked.Increment(ref builds);
                else
                {
                    Assert.Equal("test", args[0]); Assert.Contains("--no-build", args);
                    Assert.Equal(1, Volatile.Read(ref builds));
                    var directory = args[Array.IndexOf(args, "--results-directory") + 1];
                    var path = Path.Combine(directory, "foundation-api.trx");
                    DotnetSuiteSelfTests.SyntheticTrx(path, Enumerable.Repeat("Passed", 13).ToArray());
                    var document = XDocument.Load(path);
                    foreach (var attribute in document.Descendants().Attributes().Where(a => a.Name.LocalName is "id" or "testId" or "executionId" or "testName"))
                        attribute.Value += "-" + environment!["TIA_MCP_TEST_SHARD_INDEX"];
                    document.Save(path);
                    Interlocked.Increment(ref tests);
                }
                return new(0, "passed", "");
            });
        Assert.Equal(1, builds); Assert.Equal(8, tests);
        Assert.Equal(0, results["foundation-api"].ExitCode);
    }

    [Fact]
    public void ProcessFixtureCleanupToleratesAnImageStillOpenAfterExit()
    {
        var tree = new StaticTree(tolerateLockedCleanup: true);
        var image = tree.Write("FixtureExit.exe", "fixture image");
        try
        {
            using var reader = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read);
            tree.Dispose();
            if (OperatingSystem.IsWindows()) Assert.True(File.Exists(image));
        }
        finally { if (Directory.Exists(tree.Root)) tree.Dispose(); }
        Assert.False(Directory.Exists(tree.Root));
    }

    [Fact]
    public void TimeoutTerminatesTheOwnedParentAndChild()
    {
        using var tree = new StaticTree(tolerateLockedCleanup: true);
        var project = tree.Write("FixtureExit/FixtureExit.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
        tree.Write("NuGet.Config", "<configuration><packageSources><clear /></packageSources></configuration>");
        tree.Write("FixtureExit/Program.cs", """
            using System.Diagnostics;
            File.WriteAllText(args[0] + ".pid", Environment.ProcessId.ToString());
            if (args[0] == "parent")
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("child");
                using var child = Process.Start(start);
            }
            Thread.Sleep(Timeout.Infinite);
            """);
        var build = ProcessRunner.Run("dotnet", ["build", project, "-c", "Debug", "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false"], tree.Root);
        ProcessRunner.RequireSuccess(build, "FixtureExit build");
        var executable = Path.Combine(tree.Root, "FixtureExit/bin/Debug/net10.0/FixtureExit.exe");
        var error = Assert.Throws<ReleaseException>(() => ProcessRunner.Run(executable, ["parent"], tree.Root, timeoutMilliseconds: 5000));
        Assert.Contains("timed out after 5000 ms", error.Message);
        foreach (var name in new[] { "parent", "child" })
        {
            var pid = int.Parse(File.ReadAllText(Path.Combine(tree.Root, name + ".pid")), System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                using var process = Process.GetProcessById(pid);
                // Kill(entireProcessTree: true) waits for the parent only. Keep the
                // child handle until its asynchronous termination releases the image.
                Assert.True(process.WaitForExit(TestDeadlines.HangMilliseconds));
                Assert.True(process.HasExited);
            }
            catch (ArgumentException) { }
        }
    }
}
