using System;
using TiaMcpConfigurator;
using Xunit;

namespace TiaOpenness.Gui.Tests;

// P8-90: the Workbench update band follows the same channel rule as the updater.
public sealed class UpdateCheckPrereleaseTests
{
    private static string Release(string tag, bool prerelease, bool draft = false) =>
        "{\"tag_name\":\"" + tag + "\",\"prerelease\":" + (prerelease ? "true" : "false") + ",\"draft\":" + (draft ? "true" : "false") +
        ",\"html_url\":\"https://github.com/o/r/releases/tag/" + tag + "\",\"assets\":[{\"name\":\"TIA_MCP_Delivery_" + tag + "_20261021.zip\",\"size\":1048576},{\"name\":\"TIA_MCP_Delivery_" + tag + "_20261021.sha256\",\"size\":80}]}";

    [Theory]
    [InlineData("4.0.0-beta.2", "4.0.0-beta.1", 1)]
    [InlineData("4.0.0", "4.0.0-rc.1", 1)]
    [InlineData("3.3.0", "4.0.0-beta.1", -1)]
    [InlineData("v4.0.0-rc.1", "4.0.0-beta.2", 1)]
    [InlineData("2.8.0", "2.7.62", 1)]
    public void VersionsFollowSemanticVersioningPrecedence(string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(UpdateCheck.Compare(left, right)));
        Assert.Equal(-expected, Math.Sign(UpdateCheck.Compare(right, left)));
    }

    [Fact]
    public void EndpointDependsOnTheInstalledVersion()
    {
        Assert.Equal("https://api.github.com/repos/o/r/releases/latest", UpdateCheck.UpdateApiUrl("o/r", "3.3.0"));
        Assert.Equal("https://api.github.com/repos/o/r/releases?per_page=100", UpdateCheck.UpdateApiUrl("o/r", "4.0.0-beta.1"));
    }

    [Fact]
    public void PlainInstallIsNeverOfferedAPrerelease()
    {
        var info = UpdateCheck.ParseRelease(Release("v4.0.0-beta.1", prerelease: true), "3.3.0", "o/r");
        Assert.Equal("4.0.0-beta.1", info.Latest);
        Assert.True(info.Prerelease);
        Assert.False(info.UpdateAvailable);
        Assert.False(UpdateCheck.ParseRelease(Release("v4.0.0", prerelease: true), "3.3.0", "o/r").UpdateAvailable);
        Assert.True(UpdateCheck.ParseRelease(Release("v4.0.0", prerelease: false), "3.3.0", "o/r").UpdateAvailable);
    }

    [Fact]
    public void PrereleaseInstallTakesTheNewestPublishedReleaseFromTheList()
    {
        var list = "[" + Release("v3.3.0", false) + "," + Release("v4.0.0-beta.2", true) + "," + Release("v4.0.0-beta.1", true) + "," + Release("v4.0.0-beta.3", true, draft: true) + "]";
        var info = UpdateCheck.ParseReleaseList(list, "4.0.0-beta.1", "o/r");
        Assert.Equal("v4.0.0-beta.2", info.Tag);
        Assert.Equal("4.0.0-beta.2", info.Latest);
        Assert.True(info.UpdateAvailable);
        Assert.Equal("TIA_MCP_Delivery_v4.0.0-beta.2_20261021.zip", info.ZipName);
        var plain = UpdateCheck.ParseReleaseList("[" + Release("v4.0.0-rc.1", true) + "," + Release("v4.0.0", false) + "]", "4.0.0-rc.1", "o/r");
        Assert.Equal("4.0.0", plain.Latest);
        Assert.True(plain.UpdateAvailable);
    }
}
