using System.Text.Json.Nodes;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class SourceRootsTests
{
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, SourceRoots.PolicyPath))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Repository root missing");
        }
    }

    private static string Scratch() => Directory.CreateDirectory(Path.Combine(Root, "bin-build", "source-roots-" + Guid.NewGuid().ToString("N"))).FullName;

    [Theory]
    [InlineData("engine")]
    [InlineData("multi")]
    public void EngineHostCanBeAbsentAndItsSourcesAreTrackedWhenPresent(string kind)
    {
        var root = Scratch();
        try
        {
            File.WriteAllText(Path.Combine(root, "Version.props"), "<Project />");
            Assert.True(SourceRoots.Load(root).Roots.Single(row => row.Path == "src/EngineHost").Optional);
            Assert.DoesNotContain(ReleaseRecords.GetSources(root, kind), row => row.Path.StartsWith("src/EngineHost/"));
            Assert.DoesNotContain(ReleaseRecords.GetValidationInputs(root, kind), row => row.Path.StartsWith("src/EngineHost/"));
            Directory.CreateDirectory(Path.Combine(root, "src/EngineHost/obj"));
            var path = Path.Combine(root, "src/EngineHost/Host.cs");
            File.WriteAllText(path, "first\r\n");
            File.WriteAllText(Path.Combine(root, "src/EngineHost/obj/Generated.cs"), "generated");
            foreach (var inventory in new[] { ReleaseRecords.GetSources(root, kind), ReleaseRecords.GetValidationInputs(root, kind) })
            {
                Assert.Contains(inventory, row => row.Path == "src/EngineHost/Host.cs" && row.Sha256 == ReleaseValidation.SourceHash(path));
                Assert.DoesNotContain(inventory, row => row.Path.Contains("/obj/"));
            }
            var previous = ReleaseRecords.GetSources(root, kind).Single(row => row.Path == "src/EngineHost/Host.cs").Sha256;
            File.WriteAllText(path, "second\n");
            Assert.NotEqual(previous, ReleaseRecords.GetSources(root, kind).Single(row => row.Path == "src/EngineHost/Host.cs").Sha256);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CompilerInputsUseTheSharedSourceRootPolicy()
    {
        var root = Scratch();
        try
        {
            var names = new[] { "Version.props", "tests/test-suites.json", "src/EngineHost/Host.cs", "src/Updater/app.manifest",
                "src/Tools/WriteGuard/Guard.resx", "src/EngineHost/readme.md", "src/Studio/Gui/View.xaml" };
            foreach (var name in names)
            {
                var path = Path.Combine(root, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "source");
            }
            var expected = names.Where(name => !name.EndsWith(".md", StringComparison.Ordinal) && !name.EndsWith(".xaml", StringComparison.Ordinal)).Order(StringComparer.Ordinal);
            Assert.Equal(expected, ReleaseRecords.GetSources(root, "engine").Select(row => row.Path).Order(StringComparer.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("C:/absolute")]
    [InlineData("src//Engine")]
    [InlineData("src/./Engine")]
    [InlineData("src\\Engine")]
    public void SourceRootPolicyRejectsUnsafePaths(string path)
    {
        var root = Scratch();
        try
        {
            var policy = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, SourceRoots.PolicyPath)))!;
            policy["roots"]![0]!["path"] = path;
            var file = Path.Combine(root, SourceRoots.PolicyPath);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, policy.ToJsonString());
            Assert.Throws<ReleaseException>(() => SourceRoots.Load(root));

        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void StandaloneCheckMapsKeepTheirOwnChecksWithoutRepositoryRules()
    {
        var root = Scratch();
        try
        {
            var policy = new ReleaseCheckPolicy(["review"], ["review"], [new("docs/", ["review"])], []);
            var file = Path.Combine(root, "build-tools/release/release-checks.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(policy,
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
            var actual = ReleaseCheckPolicy.Load(root);
            Assert.Single(actual.Rules);
            Assert.Equal(policy.Checks, actual.Select("quick", ["docs/example.md"]).SelectedChecks);
            var selected = actual.Select("quick", ["docs/example.md"]);
            actual.ValidatePlan(selected);
            Assert.Equal(new[] { "review" }, selected.SelectedChecks);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ExternalBundleUsesTheInvokingToolsRequiredSourcePolicy()
    {
        Assert.Equal(BundleManifestRequirements.GuiRequiredPaths(Root), BundleManifestRequirements.GuiRequiredPaths("missing-bundle"));
    }
}
