using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class HierarchySources
{
    [Fact]
    public void Hierarchy_reads_structure_only()
    {
        string root = Repository.FindRoot(AppContext.BaseDirectory);
        string source = Repository.ReadSource(Path.Combine(root, "src/Adapters/Native/Plc/PlcNativeReadContracts.cs"));
        string hierarchy = source[source.IndexOf("private static PlcBlockHierarchy Hierarchy", StringComparison.Ordinal)..];
        Assert.DoesNotContain("ReadAttributes", hierarchy, StringComparison.Ordinal);
        Assert.DoesNotContain("BlockDetails)", hierarchy, StringComparison.Ordinal);
        Assert.DoesNotContain("ToString", hierarchy, StringComparison.Ordinal);
        Assert.Contains("Name=b.Name", hierarchy, StringComparison.Ordinal);
        Assert.Contains("TypeName=b.GetType().Name", hierarchy, StringComparison.Ordinal);
        Assert.Contains("b.ProgrammingLanguage", hierarchy, StringComparison.Ordinal);
    }
}
