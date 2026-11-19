using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class OpennessApiTests
{
    [Theory]
    [InlineData("14sp1", false, 2, false)]
    [InlineData("15.1", false, 3, false)]
    [InlineData("16", false, 4, false)]
    [InlineData("17", true, 4, false)]
    [InlineData("18", true, 5, true)]
    [InlineData("19", true, 5, true)]
    [InlineData("20", true, 5, true)]
    [InlineData("21", true, 5, true)]
    public void Declaration_format_preserves_each_releases_selected_schema(string release, bool sdkHasV5, int schemaVersion, bool hasObjectNamespace)
    {
        Assert.Equal(sdkHasV5, OpennessApi.InterfaceSectionsV5.Includes(release));
        var format = PlcDeclarationXmlFormat.ForRelease(release);
        Assert.Equal($"SW.InterfaceSections_v{schemaVersion}.xsd", format.InterfaceSchemaFile);
        Assert.Equal(hasObjectNamespace, format.HasObjectNamespace);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("V20")]
    [InlineData("020")]
    [InlineData("22")]
    public void Document_availability_requires_a_known_exact_release_key(string? release)
        => Assert.False(OpennessApi.PlcDocuments.Includes(release));
}
