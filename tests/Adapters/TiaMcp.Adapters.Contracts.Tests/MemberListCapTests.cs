using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Reflection;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class MemberListCapTests
{
    [Theory, InlineData(5, 5), InlineData(-1, 1), InlineData(5000, 2000)]
    public void Effective_preserves_small_caps(int requested, int expected) => Assert.Equal(expected, MemberListCap.Effective(requested));

    [Fact]
    public void Metadata_reports_total_and_truncation()
    {
        var meta = MemberListCap.Meta(29, 20);
        Assert.Equal(29, meta["totalMembers"]);
        Assert.Equal(true, meta["truncated"]);
        Assert.Equal(false, MemberListCap.Meta(20, 20)["truncated"]);
    }

    [Fact]
    public void Describe_members_report_truncation()
    {
        var session = new ReflectionSession((_, _, _, _) => null, () => null);
        var value = new TwelveProperties();
        var rows = session.DescribeMembers(value, MemberListCap.Effective(5)).ToArray();
        Assert.Equal(5, rows.Length);
        Assert.Equal(16, session.CountMembers(value));
        Assert.Equal(true, MemberListCap.Meta(session.CountMembers(value), rows.Length)["truncated"]);
    }

    [Fact]
    public void Transfer_pages_require_all_rows_without_nested_attribute_failures()
    {
        var attributes = new Dictionary<string, object?> { ["values"] = new Dictionary<string, object?>(), ["failures"] = Array.Empty<object>() };
        object[] rows = { new Dictionary<string, object?> { ["addresses"] = new[] {
            new Dictionary<string, object?> { ["attributes"] = attributes } } } };
        Assert.True(TiaMcp.Adapters.PlcFoundationEngine.HardwarePageComplete(rows, 0, rows));
        Assert.False(TiaMcp.Adapters.PlcFoundationEngine.HardwarePageComplete(rows, 1, rows));
        Assert.False(TiaMcp.Adapters.PlcFoundationEngine.HardwarePageComplete(rows, 0, Array.Empty<object>()));
        attributes["failures"] = new object[] { new Dictionary<string, object?> { ["attribute"] = "Name", ["error"] = "Read refused" } };
        Assert.False(TiaMcp.Adapters.PlcFoundationEngine.HardwarePageComplete(rows, 0, rows));
    }

    private sealed class TwelveProperties
    {
        public int A => 1; public int B => 2; public int C => 3; public int D => 4;
        public int E => 5; public int F => 6; public int G => 7; public int H => 8;
        public int I => 9; public int J => 10; public int K => 11; public int L => 12;
    }
}
