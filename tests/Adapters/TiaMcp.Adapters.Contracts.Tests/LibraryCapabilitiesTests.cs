using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class LibraryCapabilitiesTests
{
    [Fact]
    public void Overview_counts_stop_at_the_cap()
    {
        var result = LibraryCapabilities.OverviewCounts(51, 1, 50);
        Assert.Equal(50, result["typeItems"]);
        Assert.Equal(1, result["masterCopyItems"]);
        Assert.Equal(true, result["truncated"]);
        Assert.Equal(false, result["dataComplete"]);
        Assert.Equal(true, LibraryCapabilities.OverviewCounts(50, 1, 50)["dataComplete"]);
    }
}
