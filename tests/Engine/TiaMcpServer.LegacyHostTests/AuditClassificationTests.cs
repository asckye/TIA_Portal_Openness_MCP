using TiaMcp.LegacyHost;
using Xunit;

public sealed class AuditClassificationTests
{
    [Theory]
    [InlineData("CreateProject", true)]
    [InlineData("ImportBlock", true)]
    [InlineData("AddDeviceWithFallback", true)]
    [InlineData("GetBlocks", false)]
    [InlineData("ExportBlock", false)]
    [InlineData("CompileSoftware", false)]
    [InlineData("Connect", false)]
    [InlineData("Disconnect", false)]
    [InlineData("SaveProject", false)]
    [InlineData("OpenProject", false)]
    public void AuditUsesCatalogOperationRatherThanPreviewOrSessionArguments(string name, bool write)
    {
        var definition = FoundationTools.Definitions.Single(d => d.Name == name);
        Assert.Equal(write, new FoundationTool(definition, null!).IsWrite);
    }
}
