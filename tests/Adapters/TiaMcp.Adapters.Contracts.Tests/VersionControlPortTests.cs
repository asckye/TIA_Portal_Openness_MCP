using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class VersionControlPortTests
{
    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    public void Project_graphics_keep_globalization_available_before_the_graphics_provider(string release)
    {
        Assert.True(PortedFamilies.Available(release, "GetClassicHmiGlobalization"));
        Assert.DoesNotContain("GetClassicHmiGlobalization", PortedFamilies.Withheld(release));
        Assert.True(PortedFamilies.Available("21", "GetClassicHmiGlobalization"));
        Assert.DoesNotContain("GetClassicHmiGlobalization", PortedFamilies.Withheld("21"));
    }

    public static IEnumerable<object[]> ReleasesAndTools =>
        from release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        from tool in PortedFamilies.All.Single(family => family.Name == "F33").Tools
        select new object[] { release, tool };

    [Theory, MemberData(nameof(ReleasesAndTools))]
    public void Registration_matches_the_available_api(string release, string tool)
    {
        bool supported = tool == "ManagePlcGitRepository"
            || tool.Contains("Teamcenter", StringComparison.Ordinal) && release is "18" or "19" or "20" or "21"
            || !tool.Contains("Teamcenter", StringComparison.Ordinal) && release is "16" or "17" or "18" or "19" or "20" or "21";
        Assert.Equal(supported, PortedFamilies.Available(release, tool));
        Assert.Equal("F33", PortedFamilies.ForTool(tool).Name);
    }

    [Fact]
    public void Native_reply_keeps_partial_evidence_and_unknown_write_flags()
    {
        var reply = new VersionControlResult
        {
            Message = "partial",
            Items = new[] { "A | synchronized", "B | FAILED" },
            Meta = new Dictionary<string, object?> { ["success"] = false, ["mayHaveChanged"] = true },
            RequiresSessionReset = true
        };
        string wire = System.Text.Json.JsonSerializer.Serialize(reply);
        var copy = System.Text.Json.JsonSerializer.Deserialize<VersionControlResult>(wire)!;
        Assert.Equal(reply.Items, copy.Items);
        Assert.True(copy.RequiresSessionReset);
        Assert.True(copy.MayHaveChanged);
        Assert.True(reply.BlockReadsAfterUncertain);
        Assert.False(copy.Meta["success"] is null);
    }

    [Theory]
    [InlineData("saveAsNewItem", true)]
    [InlineData("saveAsNewRevisionWithProxyObject", true)]
    [InlineData("readCustomAttributes", true)]
    [InlineData("save", false)]
    public void Workflow_plan_retains_the_custom_attribute_branch(string action, bool needsAttributes)
        => Assert.Equal(needsAttributes, new TeamcenterPlan { Action = action }.NeedsAttributes);
}
