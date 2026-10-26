using System.Text.Json;
using TiaMcp.ReleaseTool;
using TiaMcp.Versioning;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class PrereleaseTests
{
    private static string Root
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Version.props")) && Directory.Exists(Path.Combine(directory.FullName, "build-tools/release"))) return directory.FullName;
            throw new DirectoryNotFoundException("repository root");
        }
    }

    [Theory]
    [InlineData("4.0.0-beta.2", "4.0.0-beta.1", 1)]
    [InlineData("4.0.0", "4.0.0-rc.1", 1)]
    [InlineData("3.3.0", "4.0.0-beta.1", -1)]
    [InlineData("4.0.0-rc.1", "4.0.0-beta.2", 1)]
    [InlineData("4.0.0-beta.10", "4.0.0-beta.9", 1)]
    [InlineData("4.0.0-beta.1", "4.0.0-beta.1", 0)]
    [InlineData("3.10.0", "3.9.0", 1)]
    [InlineData("4.0.1-beta.1", "4.0.0", 1)]
    public void SemanticVersioningPrecedence(string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(ProductReleaseVersion.Parse(left).CompareTo(ProductReleaseVersion.Parse(right))));
        Assert.Equal(-expected, Math.Sign(ProductReleaseVersion.Parse(right).CompareTo(ProductReleaseVersion.Parse(left))));
        Assert.Equal(expected, ReleaseVersions.Compare(left, right));
    }

    [Theory]
    [InlineData("4.0.0", "4.0.0", "", "4.0.0.0", false)]
    [InlineData("4.0.0-beta.1", "4.0.0", "beta.1", "4.0.0.0", true)]
    [InlineData("4.1.2-rc.3", "4.1.2", "rc.3", "4.1.2.0", true)]
    public void VersionModelSplitsCoreAndLabel(string text, string core, string prerelease, string fileVersion, bool isPrerelease)
    {
        var version = ProductReleaseVersion.Parse(text);
        Assert.Equal(core, version.Core);
        Assert.Equal(prerelease, version.Prerelease);
        Assert.Equal(text, version.Label);
        Assert.Equal("v" + text, version.Tag);
        Assert.Equal(fileVersion, version.FileVersion);
        Assert.Equal(isPrerelease, version.IsPrerelease);
        Assert.Equal(fileVersion, ReleaseVersions.FileVersionOf(text));
    }

    [Theory]
    [InlineData("4.0.0-alpha.1")]
    [InlineData("4.0.0-beta")]
    [InlineData("4.0.0-beta.01")]
    [InlineData("4.0.0-beta.1+build")]
    [InlineData("v4.0.0")]
    [InlineData("4.0")]
    [InlineData("4.0.0.0")]
    [InlineData(" 4.0.0")]
    [InlineData("4.0.0-fork")]
    public void OnlyPlainBetaAndRcVersionsAreAccepted(string text)
    {
        Assert.False(ReleaseVersions.IsValid(text));
        Assert.Throws<ReleaseException>(() => ReleaseVersions.Require(text, "invalid"));
    }

    [Fact]
    public void VersionPropsCarriesOneSourceSplitIntoReleaseAndPrerelease()
    {
        var directory = Directory.CreateTempSubdirectory("p890-props-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "Version.props"), "<Project><PropertyGroup><TiaMcpRelease>4.0.0</TiaMcpRelease><TiaMcpPrerelease>beta.1</TiaMcpPrerelease></PropertyGroup></Project>");
            Assert.Equal("4.0.0-beta.1", ReleaseVersions.ReadLabel(directory));
            Assert.Equal("4.0.0.0", ReleaseVersions.ReadVersionProps(directory).FileVersion);
            File.WriteAllText(Path.Combine(directory, "Version.props"), "<Project><PropertyGroup><TiaMcpRelease>4.0.0</TiaMcpRelease><TiaMcpPrerelease></TiaMcpPrerelease></PropertyGroup></Project>");
            Assert.Equal("4.0.0", ReleaseVersions.ReadLabel(directory));
            File.WriteAllText(Path.Combine(directory, "Version.props"), "<Project><PropertyGroup><TiaMcpRelease>4.0.0</TiaMcpRelease></PropertyGroup></Project>");
            Assert.Equal("4.0.0", ReleaseVersions.ReadLabel(directory));
            File.WriteAllText(Path.Combine(directory, "Version.props"), "<Project><PropertyGroup><TiaMcpRelease>4.0.0</TiaMcpRelease><TiaMcpPrerelease>feature-x</TiaMcpPrerelease></PropertyGroup></Project>");
            Assert.Throws<ReleaseException>(() => ReleaseVersions.ReadVersionProps(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void RepositoryVersionPropsKeepsTheNumericAssemblyVersions()
    {
        var text = File.ReadAllText(Path.Combine(Root, "Version.props"));
        Assert.Contains("<AssemblyVersion>$(TiaMcpRelease)</AssemblyVersion>", text, StringComparison.Ordinal);
        Assert.Contains("<FileVersion>$(TiaMcpRelease).0</FileVersion>", text, StringComparison.Ordinal);
        Assert.Contains("<InformationalVersion>$(TiaMcpReleaseLabel)</InformationalVersion>", text, StringComparison.Ordinal);
        Assert.Matches("<TiaMcpPrerelease>(?:|(?:beta|rc)\\.(?:0|[1-9]\\d*))</TiaMcpPrerelease>", text);
        Assert.True(ReleaseVersions.IsValid(ReleaseVersions.ReadLabel(Root)));
    }

    [Fact]
    public void ChangelogEntriesUnderstandPrereleasesAndIgnoreUnreleased()
    {
        var changelog = "# Change Log\n\n## [Unreleased]\n\n- draft\n\n## [4.0.0-beta.1] - 2026-10-21\n\n- beta\n\n## [3.3.0] - 2026-10-03\n";
        Assert.Equal("4.0.0-beta.1", ReleaseVersions.NewestChangelogEntry(changelog).Groups["v"].Value);
        Assert.Equal("3.3.0", ReleaseVersions.NewestChangelogEntry("## [Unreleased]\n## [3.3.0]\n").Groups["v"].Value);
        Assert.Equal("3.3.0", ReleaseVersions.NewestChangelogEntry("##  [9.9.9]\n## [3.3.0]\n").Groups["v"].Value);
        Assert.Equal("9.9.9", ReleaseVersions.NewestChangelogEntry("##  [9.9.9]\n## [3.3.0]\n", anySpace: true).Groups["v"].Value);
        Assert.False(ReleaseVersions.NewestChangelogEntry("## [4.0.0-alpha.1]\n").Success);
    }

    [Fact]
    public void RepositoryChangelogCheckComparesTheFullLabel()
    {
        var directory = Directory.CreateTempSubdirectory("p890-changelog-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "Version.props"), "<Project><PropertyGroup><TiaMcpRelease>4.0.0</TiaMcpRelease><TiaMcpPrerelease>beta.1</TiaMcpPrerelease></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(directory, "CHANGELOG.md"), "## [4.0.0-beta.1] - 2026-10-21\n## [3.3.0]\n");
            Assert.Empty(RepositoryChecks.ChangelogErrors(directory));
            File.WriteAllText(Path.Combine(directory, "CHANGELOG.md"), "## [4.0.0] - 2026-10-21\n## [3.3.0]\n");
            Assert.Single(RepositoryChecks.ChangelogErrors(directory));
            File.WriteAllText(Path.Combine(directory, "Version.props"), "<Project><PropertyGroup><TiaMcpRelease>3.3.0</TiaMcpRelease><TiaMcpPrerelease></TiaMcpPrerelease></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(directory, "CHANGELOG.md"), "## [4.0.0-beta.1] - draft\n## [3.3.0]\n");
            Assert.Single(RepositoryChecks.ChangelogErrors(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("4.0.0-beta.1", "3.3.0", true, true, true)]
    [InlineData("4.0.0-beta.1", "3.3.0", false, true, false)]
    [InlineData("4.0.0", "4.0.0-rc.1", true, true, true)]
    [InlineData("4.0.0-beta.1", "4.0.0", true, true, false)]
    [InlineData("4.0.0-beta.2", "4.0.0-beta.1", true, true, true)]
    [InlineData("4.0.0-beta.1", "4.0.0-beta.1", false, false, true)]
    [InlineData("4.0.0-alpha.1", "3.3.0", true, true, false)]
    public void ChangelogVersionUsesPrecedenceForPrereleases(string newest, string released, bool repositoryOnly, bool noteExists, bool expected) =>
        Assert.Equal(expected, ReleaseValidation.ChangelogVersion(newest, released, repositoryOnly, noteExists));

    [Fact]
    public void PlainReleaseRequestBodiesAreUnchanged()
    {
        Assert.Equal("{\"tag_name\":\"v4.0.0\",\"target_commitish\":\"abc\",\"name\":\"v4.0.0\",\"body\":\"notes\",\"draft\":true,\"prerelease\":false,\"make_latest\":\"false\"}",
            JsonSerializer.Serialize(ReleaseRequestBodies.Draft("v4.0.0", "abc", "notes", prerelease: false)));
        Assert.Equal("{\"name\":\"v4.0.0\",\"body\":\"notes\",\"draft\":false,\"prerelease\":false,\"make_latest\":\"true\"}",
            JsonSerializer.Serialize(ReleaseRequestBodies.Publish("v4.0.0", "notes", prerelease: false)));
    }

    [Fact]
    public void PrereleaseRequestBodiesAreNeverLatest()
    {
        Assert.Equal("{\"tag_name\":\"v4.0.0-beta.1\",\"target_commitish\":\"abc\",\"name\":\"v4.0.0-beta.1\",\"body\":\"notes\",\"draft\":true,\"prerelease\":true,\"make_latest\":\"false\"}",
            JsonSerializer.Serialize(ReleaseRequestBodies.Draft("v4.0.0-beta.1", "abc", "notes", prerelease: true)));
        Assert.Equal("{\"name\":\"v4.0.0-beta.1\",\"body\":\"notes\",\"draft\":false,\"prerelease\":true,\"make_latest\":\"false\"}",
            JsonSerializer.Serialize(ReleaseRequestBodies.Publish("v4.0.0-beta.1", "notes", prerelease: true)));
    }

    [Theory]
    [InlineData("4.0.0-alpha.1")]
    [InlineData("4.0.0-beta")]
    [InlineData("4.0")]
    [Trait("Category", "ReleaseParity")]
    public void ReleaseRejectsOtherSuffixesBeforeAnyEffect(string version)
    {
        var options = Options.Parse(["-Tier", "full", "-Version", version, "-DryRun"], new HashSet<string>(["DryRun"]));
        Assert.Contains("-Version must be", Assert.Throws<ReleaseException>(() => ReleaseCommands.Run("release", options)).Message);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void ReleaseAcceptsAPrereleaseVersion()
    {
        // The version passes validation; the conflicting switches are then refused before any effect.
        var options = Options.Parse(["-Tier", "full", "-Version", "4.0.0-beta.1", "-DryRun", "-EarlyGatesOnly"], new HashSet<string>(["DryRun", "EarlyGatesOnly"]));
        Assert.Contains("cannot be combined", Assert.Throws<ReleaseException>(() => ReleaseCommands.Run("release", options)).Message);
    }

    private const string Ledger = "# Ledger\n\n## 未发布改动的验收边界\n\nintro\n\n| 改动 | 验收 |\n|---|---|\n| A item | **NOT RUN**；check A |\n\n" +
        "## V20/V21 host + worker acceptance (P6-54)\n\n| releaseKey | State | Steps |\n|---|---|---|\n| 20 | **NOT RUN** | V20 steps |\n\n" +
        "## 4.0 安全策略验收计划（P6-R2）\n\n| ID | 状态 | 项目 |\n|---|---|---|\n| P6-DEVICE 设备创建 | **NOT RUN**；19/20/21 | device |\n\n" +
        "### 4.0 发布前仍需的真实机器步骤\n\n以下均为 **NOT RUN**\n\n| 门槛 | 步骤 |\n|---|---|\n| P6-PRODUCT（八版） | start the package |\n\n" +
        "<!-- behavior-capabilities:start -->\n\n| releaseKey | family | state | L5 | entries |\n|---|---|---|---|---|\n| 14sp1 | P6-DEVICE | current | NOT RUN | CreateDevice |\n| 21 | F04 | current | NOT RUN | — |\n\n<!-- behavior-capabilities:end -->\n\n## v3.2.0 验收边界\n\n| x | y |\n|---|---|\n| old | **NOT RUN** |\n";

    private const string Checklist = "# API audit VM checklist\n\nintro\n\n## Online (P8-81a)\n\n1. F1: item one.\n2. F2: item two\n   continued.\n\n" +
        "## Pending\n\nintro\n\n- session.md F1: item three\n  continued.\n";

    [Fact]
    public void NotVerifiedSectionComesFromTheLedgerAndTheChecklist()
    {
        var block = PrereleaseNotes.Generate(Ledger, Checklist);
        Assert.StartsWith(PrereleaseNotes.Begin + "\n" + PrereleaseNotes.Heading + "\n", block, StringComparison.Ordinal);
        Assert.EndsWith(PrereleaseNotes.End + "\n", block, StringComparison.Ordinal);
        // One running item number across the ledger rows, the behavior summary and the checklist items (the report form asks for it).
        Assert.Contains("\n1. **A item**: **NOT RUN**；check A\n", block, StringComparison.Ordinal);
        Assert.Contains("\n2. **20**: **NOT RUN** — V20 steps\n", block, StringComparison.Ordinal);
        Assert.Contains("\n3. **P6-DEVICE 设备创建**: **NOT RUN**；19/20/21 — device\n", block, StringComparison.Ordinal);
        Assert.Contains("\n4. **P6-PRODUCT（八版）**: start the package\n", block, StringComparison.Ordinal);
        Assert.Contains("\n5. All 2 release/family rows", block, StringComparison.Ordinal);
        Assert.Contains("#### Online (P8-81a)", block, StringComparison.Ordinal);
        Assert.Contains("\n6. F1: item one.\n7. F2: item two\n   continued.\n", block, StringComparison.Ordinal);
        Assert.Contains("#### Pending\n\nintro\n\n8. session.md F1: item three\n  continued.\n", block, StringComparison.Ordinal);
        Assert.DoesNotContain("\n- ", block, StringComparison.Ordinal);
        Assert.DoesNotContain("\n9. ", block, StringComparison.Ordinal);
        Assert.DoesNotContain("old", block, StringComparison.Ordinal);
        Assert.DoesNotContain("**14sp1**", block, StringComparison.Ordinal);
        Assert.Throws<ReleaseException>(() => PrereleaseNotes.Generate(Ledger.Replace("## V20/V21 host + worker acceptance (P6-54)", "## Renamed", StringComparison.Ordinal), Checklist));
        Assert.Throws<ReleaseException>(() => PrereleaseNotes.Generate(Ledger, "no sections"));
    }

    [Fact]
    public void NotVerifiedSectionReplacesOnlyItsMarkers()
    {
        var notes = "# v4.0.0-beta.1\n\nintro\n\n" + PrereleaseNotes.Begin + "\nstale\n" + PrereleaseNotes.End + "\n\n## After\n";
        var updated = PrereleaseNotes.Apply(notes, PrereleaseNotes.Begin + "\nnew\n" + PrereleaseNotes.End + "\n");
        Assert.Equal("# v4.0.0-beta.1\n\nintro\n\n" + PrereleaseNotes.Begin + "\nnew\n" + PrereleaseNotes.End + "\n\n## After\n", updated);
        Assert.Null(PrereleaseNotes.Apply("# no markers\n", "x"));
        Assert.Null(PrereleaseNotes.Apply(PrereleaseNotes.Begin + PrereleaseNotes.End + PrereleaseNotes.Begin + PrereleaseNotes.End, "x"));
        Assert.True(PrereleaseNotes.HasSection(updated!.Replace("new", PrereleaseNotes.Heading, StringComparison.Ordinal)));
        Assert.False(PrereleaseNotes.HasSection(updated));
    }

    [Theory]
    [InlineData("TIA_MCP_Delivery_v4.0.0-beta.1_20261021", "4.0.0-beta.1", true)]
    [InlineData("TIA_MCP_Delivery_v4.0.0_20261021", "4.0.0", true)]
    [InlineData("TIA_MCP_Delivery_v4.0.0-beta.1_20261021", "4.0.0", false)]
    [InlineData("TIA_MCP_Delivery_v4.0.0_20261021", "4.0.0-beta.1", false)]
    [InlineData("TIA_MCP_Delivery_v4.0.0-fork_20261021", "4.0.0", false)]
    [InlineData("TIA_MCP_Delivery_v4.0.0_2026102", "4.0.0", false)]
    [InlineData("TIA_MCP_Delivery_v4.0.0_20261341", "4.0.0", false)]
    [InlineData("TIA_MCP_Delivery_v4.0.0_20261021.zip", "4.0.0", false)]
    [InlineData("Other_v4.0.0_20261021", "4.0.0", false)]
    [InlineData("TIA_MCP_Delivery_v4x0x0_20261021", "4.0.0", false)]
    public void PackageNameIsExactlyTheDeliveryPattern(string packageName, string version, bool valid) =>
        Assert.Equal(valid, ReleaseValidation.PackageNameError(packageName, version) is null);

    [Fact]
    public void RepositoryPackageManifestNameMatchesItsBundleVersion()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "manifest/package-manifest.json")));
        Assert.Null(ReleaseValidation.PackageNameError(manifest.RootElement.GetProperty("packageName").GetString()!, manifest.RootElement.GetProperty("bundleVersion").GetString()!));
    }

    [Fact]
    public void PluginVersionsCompareWithTheDeliveryRecordOrThePendingRelease()
    {
        var directory = Directory.CreateTempSubdirectory("p890-plugin-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".claude-plugin"));
            Directory.CreateDirectory(Path.Combine(directory, "manifest"));
            void Write(string plugin, string marketplace, string delivery)
            {
                File.WriteAllText(Path.Combine(directory, ".claude-plugin/plugin.json"), "{\"name\":\"p\",\"version\":\"" + plugin + "\"}");
                File.WriteAllText(Path.Combine(directory, ".claude-plugin/marketplace.json"), "{\"plugins\":[{\"name\":\"other\",\"version\":\"9.9.9\"},{\"name\":\"p\",\"version\":\"" + marketplace + "\"}]}");
                File.WriteAllText(Path.Combine(directory, "manifest/delivery.json"), "{\"release\":\"" + delivery + "\"}");
            }
            // Without -PendingRelease: exactly the delivery record, as before.
            Write("3.3.0", "3.3.0", "3.3.0");
            Assert.Null(ReleaseCommands.PluginVersionError(directory, null));
            Write("4.0.0-beta.1", "4.0.0-beta.1", "3.3.0");
            Assert.Equal("Plugin version differs from release version", ReleaseCommands.PluginVersionError(directory, null));
            Write("3.3.0", "4.0.0", "3.3.0");
            Assert.Equal("Marketplace plugin version differs from release version", ReleaseCommands.PluginVersionError(directory, null));
            // With -PendingRelease (pre-build gate after the mechanical bump): exactly the pending version, not the delivery record.
            Write("4.0.0-beta.1", "4.0.0-beta.1", "3.3.0");
            Assert.Null(ReleaseCommands.PluginVersionError(directory, "4.0.0-beta.1"));
            Assert.Equal("Plugin version differs from release version", ReleaseCommands.PluginVersionError(directory, "4.0.0"));
            Write("3.3.0", "3.3.0", "3.3.0");
            Assert.Equal("Plugin version differs from release version", ReleaseCommands.PluginVersionError(directory, "4.0.0-beta.1"));
            Write("4.0.0-beta.1", "3.3.0", "3.3.0");
            Assert.Equal("Marketplace plugin version differs from release version", ReleaseCommands.PluginVersionError(directory, "4.0.0-beta.1"));
            Assert.NotNull(ReleaseCommands.PluginVersionError(directory, "4.0.0-alpha.1"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MarketplaceVersionUpdateKeepsEveryOtherByte(bool bom)
    {
        var directory = Directory.CreateTempSubdirectory("p890-marketplace-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".claude-plugin"));
            File.WriteAllText(Path.Combine(directory, ".claude-plugin/plugin.json"), "{ \"name\": \"tia\", \"version\": \"3.3.0\" }");
            const string before = "{\r\n  \"name\": \"tia\",\r\n  \"owner\": { \"name\": \"asckye\" },\r\n  \"plugins\": [\r\n    { \"name\": \"other\", \"version\": \"3.3.0\" },\r\n" +
                "    {\r\n      \"name\": \"tia\",\r\n      \"version\": \"3.3.0\",\r\n      \"source\": { \"name\": \"tia\", \"version\": \"keep\" },\r\n      \"description\": \"MCP — runtimes \\u2014 kept\"\r\n    }\r\n  ]\r\n}";
            byte[] prefix = bom ? [0xEF, 0xBB, 0xBF] : [];
            var path = Path.Combine(directory, ".claude-plugin/marketplace.json");
            File.WriteAllBytes(path, [.. prefix, .. System.Text.Encoding.UTF8.GetBytes(before)]);

            ReleaseCommands.UpdateMarketplaceVersion(directory, "4.0.0-beta.1");

            var expected = before.Replace("\"tia\",\r\n      \"version\": \"3.3.0\"", "\"tia\",\r\n      \"version\": \"4.0.0-beta.1\"", StringComparison.Ordinal);
            Assert.NotEqual(before, expected);
            byte[] expectedBytes = [.. prefix, .. System.Text.Encoding.UTF8.GetBytes(expected)];
            Assert.Equal(expectedBytes, File.ReadAllBytes(path));
            ReleaseCommands.UpdateMarketplaceVersion(directory, "4.0.0-beta.1");
            Assert.Equal(expectedBytes, File.ReadAllBytes(path));
            File.WriteAllText(path, "{\"plugins\":[{\"name\":\"tia\"}]}");
            Assert.Throws<ReleaseException>(() => ReleaseCommands.UpdateMarketplaceVersion(directory, "4.0.0"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void RepositoryMarketplaceVersionIsFoundInPlace()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Root, ".claude-plugin/marketplace.json"));
        using var plugin = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, ".claude-plugin/plugin.json")));
        var (start, length) = ReleaseCommands.MarketplaceVersionSpan(bytes, plugin.RootElement.GetProperty("name").GetString()!);
        Assert.Equal("\"" + plugin.RootElement.GetProperty("version").GetString() + "\"", System.Text.Encoding.UTF8.GetString(bytes, start, length));
    }

    [Theory]
    [InlineData("4.0.0.0", "4.0.0-beta.1", "4.0.0-beta.1")]
    [InlineData("4.0.0.0", "4.0.0-rc.2", "4.0.0-rc.2")]
    [InlineData("3.3.0.0", "3.3.0", "3.3.0")]
    [InlineData("3.3.0.0", null, "3.3.0")]
    [InlineData("3.3.0.0", "", "3.3.0")]
    [InlineData("3.3.0.0", "4.0.0-beta.1", "3.3.0")]
    [InlineData("4.0.0.0", "4.0.0-alpha.1", "4.0.0")]
    public void VersionAuditFindsTheEvidenceFolderOfAPrerelease(string fileVersion, string? recordRelease, string expected) =>
        Assert.Equal(expected, CatalogAudits.ReleaseDirectoryVersion(fileVersion, recordRelease));

    [Fact]
    public void DraftBetaNotesCarryTheCurrentGeneratedSection()
    {
        Assert.Null(PrereleaseNotes.CurrentError(Root, "4.0.0-beta.2"));
        Assert.Throws<ReleaseException>(() => PrereleaseNotes.Outputs(Root, "4.0.0"));
    }

    [Fact]
    public void DraftBetaNotesSendReportsToThePrereleaseFormWithTheItemNumber()
    {
        var notes = File.ReadAllText(Path.Combine(Root, PrereleaseNotes.NotesPath("4.0.0-beta.2")));
        var report = notes[notes.IndexOf("## Report problems", StringComparison.Ordinal)..notes.IndexOf(PrereleaseNotes.Begin, StringComparison.Ordinal)];
        Assert.Contains("https://github.com/asckye/TIA_Portal_Openness_MCP/issues/new?template=prerelease_report.md", report, StringComparison.Ordinal);
        Assert.Contains("item number", report, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Root, ".github/ISSUE_TEMPLATE/prerelease_report.md")), "pre-release report form missing");
        Assert.Contains("Item number in the release notes' \"Not verified on a real TIA Portal\" list",
            File.ReadAllText(Path.Combine(Root, ".github/ISSUE_TEMPLATE/prerelease_report.md")), StringComparison.Ordinal);
    }
}
