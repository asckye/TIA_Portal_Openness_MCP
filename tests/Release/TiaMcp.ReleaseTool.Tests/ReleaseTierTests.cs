using System.IO.Compression;
using System.Text.Json;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class ReleaseTierTests
{
    private static string Root => FindRoot();
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "build-tools/release/release-checks.json"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root missing");
    }

    [Fact]
    [Trait("Category", "ReviewerChain")]
    public void ReviewedMapReachesEveryCheckAndUnknownPathsSelectEverything()
    {
        var policy = ReleaseCheckPolicy.Load(Root);
        Assert.Empty(policy.Checks.Except(policy.Rules.SelectMany(rule => rule.Checks)));
        foreach (var rule in policy.Rules)
        {
            var plan = policy.Select("quick", [rule.Path.EndsWith('/') ? rule.Path + "Example.cs" : rule.Path]);
            policy.ValidatePlan(plan);
            Assert.Empty(rule.Checks.Except(plan.SelectedChecks));
        }
        Assert.Equal(policy.Checks, policy.Select("quick", ["new-component/Example.cs"]).SelectedChecks);
        Assert.Equal(policy.Checks, policy.Select("quick", null).SelectedChecks);
        Assert.All(policy.SelfTests, test => Assert.True(File.Exists(Path.Combine(Root, test.Script))));
    }

    [Theory]
    [InlineData("src/FoundationHost/Program.cs", "foundation-transport")]
    [InlineData("src/PlcWorker/Program.cs", "foundation-approval")]
    [InlineData("src/Engine/Program.cs", "engine-responses")]
    [InlineData("src/Studio/Gui/App.xaml.cs", "gui-tests")]
    [InlineData("scripts/checks/Snapshot-ToolResponses.py", "responses-self-test")]
    public void ChangesSelectRelatedChecks(string path, string expected)
    {
        var plan = ReleaseCheckPolicy.Load(Root).Select("quick", [path]);
        Assert.Contains(expected, plan.SelectedChecks);
        if ((path.StartsWith("src/Engine/") || path.StartsWith("src/PlcWorker/"))) Assert.Contains("engine-stability", plan.SelectedChecks);
        else Assert.Contains("engine-stability", plan.SkippedChecks);
    }

    [Fact]
    public void DocumentationQuickKeepsAllMandatoryChecksAndFullNeverSkips()
    {
        var policy = ReleaseCheckPolicy.Load(Root);
        Assert.Equal(policy.Always.Order(), policy.Select("quick", ["docs/development/validation.md"]).SelectedChecks.Order());
        Assert.Empty(policy.Select("full", []).SkippedChecks);
        Assert.Throws<ReleaseException>(() => policy.ValidatePlan(policy.Select("full", []) with { SkippedChecks = ["engine-stability"] }));
        var quick = policy.Select("quick", ["src/Engine/Program.cs"]);
        Assert.Throws<ReleaseException>(() => policy.ValidatePlan(quick with {
            SelectedChecks = quick.SelectedChecks.Where(check => check != "engine-approval").ToArray(),
            SkippedChecks = [.. quick.SkippedChecks, "engine-approval"] }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Quick")]
    [InlineData("other")]
    public void ReviewerChainRequiresAnExplicitValidTier(string? tier) =>
        Assert.Throws<ReleaseException>(() => ReleaseCheckPolicy.Tier(tier, required: true));

    [Theory]
    [InlineData("quick")]
    [InlineData("package")]
    [Trait("Category", "ReleaseParity")]
    public void ReleaseRejectsQuickBeforePrerequisitesOrOtherEffects(string tier)
    {
        var options = Options.Parse(["-Tier", tier, "-Version", "3.3.1", "-DryRun"], new HashSet<string>(["DryRun"]));
        Assert.Contains("full", Assert.Throws<ReleaseException>(() => ReleaseCommands.Run("release", options)).Message);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void PublicationCannotSkipItsColdBuild()
    {
        var options = Options.Parse(["-Tier", "full", "-Version", "3.3.1", "-DryRun", "-SkipBuild"], new HashSet<string>(["DryRun", "SkipBuild"]));
        Assert.Contains("cold rebuild", Assert.Throws<ReleaseException>(() => ReleaseCommands.Run("release", options)).Message);
    }

    [Theory]
    [InlineData("package", "passed", true)]
    [InlineData("quick", "passed", false)]
    [InlineData("full", "pending", false)]
    [InlineData("full", "passed", false)]
    [InlineData("full", "passed", true)]
    public void ReleaseRequiresEveryFullPackageCheck(string tier, string status, bool complete)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        var policy = ReleaseCheckPolicy.Load(Root);
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("bundle/manifest/package-manifest.json").Open()))
                writer.Write(JsonSerializer.Serialize(new { tier, checkStatus = status,
                    checksRan = complete ? policy.Checks : policy.Always, checksSkipped = Array.Empty<string>() }));
            if (tier == "full" && status == "passed" && complete) policy.RequireFullPackage(path);
            else Assert.Throws<ReleaseException>(() => policy.RequireFullPackage(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PackageSelectsExactlyTheInstallableChecksEvenWithoutABaseline()
    {
        var policy = ReleaseCheckPolicy.Load(Root);
        var plan = policy.Select("package", null);
        policy.ValidatePlan(plan);
        Assert.Equal(ReleaseCheckPolicy.PackageChecks.Order(), plan.SelectedChecks.Order());
        Assert.DoesNotContain("offline-suites", plan.SelectedChecks);
        Assert.Throws<ReleaseException>(() => policy.ValidatePlan(plan with { SelectedChecks = policy.Checks, SkippedChecks = [] }));
    }

    [Theory]
    [InlineData("src/Studio/Gui/MainWindow.xaml", "gui-tests")]
    [InlineData("src/Shared/ProcessArguments.cs", "gui-tests")]
    public void StudioOwnershipDoesNotSelectEngineSoaks(string path, string expected)
    {
        var policy = ReleaseCheckPolicy.Load(Root);
        var plan = policy.Select("quick", [path]);
        policy.ValidatePlan(plan);
        Assert.Contains(expected, plan.SelectedChecks);
        Assert.DoesNotContain("engine-stability", plan.SelectedChecks);
        Assert.DoesNotContain("engine-isolated-stability", plan.SelectedChecks);
    }

    [Fact]
    public void InvalidBaselineSelectsAllChecks()
    {
        var policy = ReleaseCheckPolicy.Load(Root);
        Assert.Null(policy.ChangesSinceFull(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), []));
    }

    [Fact]
    public void BaselineBindsFullPackageAndDetectsAddedRemovedAndChangedInputs()
    {
        var policy = ReleaseCheckPolicy.Load(Root);
        var root = Path.Combine(Path.GetTempPath(), "tier-baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var package = Path.Combine(root, "full.zip");
        var baseline = Path.Combine(root, "baseline.json");
        try
        {
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("bundle/manifest/package-manifest.json").Open()))
                writer.Write(JsonSerializer.Serialize(new { tier = "full", checkStatus = "passed", checksRan = policy.Checks, checksSkipped = Array.Empty<string>() }));
            var sources = new[] { new ReleaseArtifact("same.cs", "1"), new ReleaseArtifact("removed.cs", "2"), new ReleaseArtifact("changed.cs", "3") };
            File.WriteAllText(baseline, JsonSerializer.Serialize(new { tier = "full", package, packageSha256 = ReleaseRecords.HashFile(package), sourceFiles = sources },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            Assert.Empty(policy.ChangesSinceFull(baseline, sources)!);
            Assert.Equal(["added.cs", "changed.cs", "removed.cs"], policy.ChangesSinceFull(baseline,
                [new("same.cs", "1"), new("changed.cs", "4"), new("added.cs", "5")])!);
            File.AppendAllText(package, "tampered");
            Assert.Null(policy.ChangesSinceFull(baseline, sources));
        }
        finally { Directory.Delete(root, true); }
    }
}
