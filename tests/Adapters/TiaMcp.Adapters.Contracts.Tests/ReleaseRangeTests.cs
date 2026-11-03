using TiaMcp.Versioning;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class ReleaseRangeTests
{
    [Fact]
    public void AllMatchesVersionCatalogInOrder()
        => Assert.Equal(TiaVersionCatalog.All.Select(release => release.Key), ReleaseRange.All);

    [Fact]
    public void AllReturnsIndependentCopies()
    {
        var first = ReleaseRange.All;
        first[0] = "changed";
        Assert.Equal("14sp1", ReleaseRange.All[0]);
    }

    [Fact]
    public void SinceIncludesEveryReleaseFromFirst()
        => Assert.Equal(new[] { "19", "20", "21" }, ReleaseRange.Since("19"));

    [Fact]
    public void BetweenIncludesBothEndpoints()
        => Assert.Equal(new[] { "17", "18", "19", "20" }, ReleaseRange.Between("17", "20"));

    [Theory]
    [InlineData("22", "21")]
    [InlineData("20", "22")]
    [InlineData("20", "17")]
    public void BetweenRejectsUnknownOrReversedEndpoints(string first, string last)
        => Assert.Throws<ArgumentException>(() => ReleaseRange.Between(first, last));

    [Fact]
    public void SinceRejectsUnknownRelease()
        => Assert.Contains("22", Assert.Throws<ArgumentException>(() => ReleaseRange.Since("22")).Message);
}
