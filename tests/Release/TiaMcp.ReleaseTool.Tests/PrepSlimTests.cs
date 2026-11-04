using System.Text.Json.Nodes;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class PrepSlimTests
{
    [Fact]
    public void PrepareHasExactlyThePackageChecks()
    {
        using var tree = new StaticTree();
        var context = new PrepareContext(tree.Root, "4.0.0-beta.3", "sdk", null, "python", "companion",
            Path.Combine(tree.Root, "prepared"), new(2026, 11, 4), 1);
        var plan = ReleaseCommands.PrepareCheckJobs(context, Path.Combine(tree.Root, "bundle"),
            new Dictionary<string, string?> { ["TEMP"] = Path.Combine(tree.Root, "temp") });
        var expected = new[] { "product-smoke", "relocated-bundle", "bundled-runtime-foundation-host", "bundled-runtime-studio",
            "bundle-write-guard", "foundation-approval", "approval-v20-bundle", "approval-v21-bundle",
            "native-mcp-session-self-test", "nativelifecycle-self-test" }
            .Concat(PlcWorkerFixtures.Keys.SelectMany(key => new[] { "weave-v" + key + "-bundle", "contracts-v" + key }));
        Assert.Equal(expected.Order(), plan.Jobs.Select(job => job.Name).Order());
    }

    [Theory]
    [InlineData("src/WorkerChannel/Channel.cs", false, true)]
    [InlineData("scripts/checks/Test-FoundationTransport.py", false, true)]
    [InlineData("tests/WorkerChannel/TiaMcp.WorkerChannel.TransportFixture/Program.cs", false, true)]
    [InlineData("docs/manuals/release.md", false, false)]
    [InlineData("Directory.Build.props", false, true)]
    [InlineData("", true, true)]
    public void TransportSelectionIncludesItsBuilds(string path, bool all, bool selected)
    {
        var closures = new Dictionary<string, HashSet<string>> { ["worker-channel"] = new() { path } };
        var options = Options.Parse(all ? ["-All"] : [], Program.BooleanOptions);
        var plan = MergeCheck.Select(path.Length == 0 ? [] : [path], options.Has("All"), new JsonObject(), closures,
            (_, _) => false, [], "dotnet", "python", "sdk", "output");
        foreach (var name in new[] { "foundation-transport", "foundation-transport-plc" })
            Assert.Equal(selected, plan.Checks.Any(job => job.Name == name));
        if (!selected) return;
        Assert.True(plan.Table);
        Assert.Contains("src/FoundationHost/TiaMcp.FoundationHost.csproj", plan.Builds.Keys);
        Assert.Contains("tests/WorkerChannel/TiaMcp.WorkerChannel.TransportFixture/TiaMcp.WorkerChannel.TransportFixture.csproj", plan.Builds.Keys);
        if (all) Assert.Contains(plan.Checks, job => job.Name == "configuration-page");
        Assert.All(plan.Checks.Where(job => job.Name.StartsWith("foundation-transport", StringComparison.Ordinal)),
            job =>
            {
                Assert.Contains("--host-exe", job.Arguments);
                Assert.True(job.PostSuite);
                Assert.False(job.Serial);
                Assert.True(job.Isolated);
            });
    }

    [Theory]
    [InlineData("src/WorkerChannel/channel.cs")]
    [InlineData("src/Adapters/Native/tool.cs")]
    [InlineData("reference/tool-catalog/tools.json")]
    [InlineData("build-tools/release/ToolUsageGenerator.cs")]
    [InlineData("Directory.Build.props")]
    public void WorkerChecksKeepNativeSafetyUsageAndCampaignInputs(string path)
    {
        var plan = MergeCheck.Select([path], false, new JsonObject(),
            new Dictionary<string, HashSet<string>> { ["worker-channel"] = new() { path } },
            (_, _) => false, [], "dotnet", "python", "sdk", "output");
        Assert.Contains(MergeCheck.ApiCompileProject, plan.Builds.Keys);
        Assert.Contains(MergeCheck.AdapterDiagnosticsProject, plan.Builds.Keys);
        foreach (var key in new[] { "20", "21" })
        {
            Assert.Contains($"tests/FoundationHost/TiaMcp.Engine.Native.Tests/V{key}/TiaMcp.Engine.Native.Tests.V{key}.csproj", plan.Builds.Keys);
            foreach (var name in new[] { "native-safety", "usage", "campaign-inputs" })
                Assert.Contains(plan.Checks, job => job.Name == name + "-v" + key + "-plc");
        }
    }
}
