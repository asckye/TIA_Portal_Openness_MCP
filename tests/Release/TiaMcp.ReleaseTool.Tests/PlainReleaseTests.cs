using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class PlainReleaseTests
{
    [Fact]
    public void OnlyTheRetainedCommandsArePublic()
    {
        string[] expected = ["prepare", "publish-prepared", "upload-draft", "merge-check", "generate-catalogs", "generate-tool-usage",
            "generate-tool-counts", "generate-release-migration", "generate-prerelease-notes", "check-tools-list", "audit-version-tools",
            "audit-tool-usage", "check-analyzer-warnings", "check-ratchet", "check-adapter-boundary", "check-bundle-layout", "check-repository",
            "check-dead-tool-references", "check-tia-features", "check-script-tool-calls", "check-envelope-rewrite", "test-suites",
            "build-file-programs", "build-solutions", "build-plc-worker-fixtures", "prepare-decoder-python", "get-bundled-dotnet", "publish-vci-watch"];
        Assert.Equal(expected.Order(), CommandLine.Commands.Order());
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
            ["project"] = "fixture.csproj", ["arguments"] = new JsonArray(), ["minimumPassed"] = 8, ["maximumSkipped"] = 0 } };
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
                    DotnetSuiteSelfTests.SyntheticTrx(path, ["Passed"]);
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
    public void TimeoutTerminatesTheOwnedParentAndChild()
    {
        using var tree = new StaticTree();
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
            try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
            catch (ArgumentException) { }
        }
    }
}
