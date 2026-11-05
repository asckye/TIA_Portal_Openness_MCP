using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;
using TiaMcp.Tests.Shared;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class PlcVerifiedImportWiring
{
    [Fact]
    public void Lease_release_does_not_index_connectionUnavailable()
    {
        string source = SourceFamily.Read(Path.Combine(Repository.FindRoot(AppContext.BaseDirectory),
            "src/Adapters/Native/Plc/PlcVerifiedImportBoundary.cs"));
        string final = Regex.Replace(source.Substring(source.IndexOf("finally", StringComparison.Ordinal)), @"\s+", " ");
        Assert.Contains("meta.TryGetValue(\"connectionUnavailable\", out var lost) && Equals(lost, true)", final, StringComparison.Ordinal);
        Assert.DoesNotContain("meta[\"connectionUnavailable\"]", final, StringComparison.Ordinal);
        Assert.Contains("else exclusive?.Dispose();", final, StringComparison.Ordinal);
    }
}
