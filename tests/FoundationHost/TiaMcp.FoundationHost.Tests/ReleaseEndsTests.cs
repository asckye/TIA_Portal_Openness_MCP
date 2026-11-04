using TiaMcp.Adapters.Contracts;
using Xunit;

public sealed class ReleaseEndsTests
{
    [Fact]
    public void A_tool_available_from_19_keeps_19_and_21()
    {
        var rows = new[] { ("Unified", "21"), ("Unified", "19"), ("Unified", "20") };
        Assert.Equal(new[] { ("Unified", "19"), ("Unified", "21") },
            ReleaseEnds.OldestAndNewest(rows, row => row.Item1, row => row.Item2));
    }

    [Fact]
    public void A_single_release_keeps_one_row()
    {
        var rows = new[] { ("OnlyV19", "19") };
        Assert.Equal(rows, ReleaseEnds.OldestAndNewest(rows, row => row.Item1, row => row.Item2));
    }

    [Fact]
    public void Plain_and_combined_calls_keep_their_own_release_ends()
    {
        var rows = ReleaseRange.All.SelectMany(release =>
            (release is "20" or "21" ? new[] { false, true } : new[] { false })
                .Select(combined => (Name: "Tool", Release: release, Combined: combined)));
        Assert.Equal(new[] { ("Tool", "14sp1", false), ("Tool", "21", false), ("Tool", "20", true), ("Tool", "21", true) },
            ReleaseEnds.OldestAndNewest(rows, row => row.Name + ":" + row.Combined, row => row.Release));
    }

    [Fact]
    public void Every_subject_survives_even_when_absent_from_global_ends()
    {
        var rows = new[] { ("All", "14sp1"), ("All", "21"), ("Middle", "19"), ("Late", "20"), ("Late", "21") };
        var selected = ReleaseEnds.OldestAndNewest(rows, row => row.Item1, row => row.Item2).ToArray();
        Assert.Equal(rows.Select(row => row.Item1).Distinct(), selected.Select(row => row.Item1).Distinct());
        Assert.Contains(("Middle", "19"), selected);
    }

    [Fact]
    public void Every_variant_at_each_release_end_survives()
    {
        var rows = from release in new[] { "20", "19", "21" }
            from parameter in new[] { "objectPath", "tagPath" }
            from encoded in new[] { false, true }
            select (Name: "Tool", Release: release, Parameter: parameter, Encoded: encoded);
        var selected = ReleaseEnds.OldestAndNewest(rows, row => row.Name + ":" + row.Parameter, row => row.Release).ToArray();
        Assert.Equal(8, selected.Length);
        foreach (var row in rows.Where(row => row.Release is "19" or "21")) Assert.Contains(row, selected);
    }

    [Fact]
    public void Release_order_comes_from_the_catalog()
    {
        var rows = new[] { "21", "16", "15.1", "14sp1", "20" };
        Assert.Equal(new[] { "14sp1", "21" }, ReleaseEnds.OldestAndNewest(rows, _ => "Tool", row => row));
    }

    [Fact]
    public void Empty_input_keeps_no_rows()
        => Assert.Empty(ReleaseEnds.OldestAndNewest(Array.Empty<string>(), row => row, row => row));
}
