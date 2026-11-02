using TiaMcp.FoundationHost;
using Xunit;

public sealed class AuditClassificationTests
{
    [Theory]
    [InlineData("CreateProject", true)]
    [InlineData("ImportPlcBlock", true)]
    [InlineData("CreateHardwareDevice", true)]
    [InlineData("ListPlcBlocks", false)]
    [InlineData("ExportPlcBlock", false)]
    [InlineData("CompilePlcSoftware", false)]
    [InlineData("ConnectPortal", false)]
    [InlineData("DisconnectPortal", false)]
    [InlineData("SaveProject", false)]
    [InlineData("OpenProject", false)]
    public void AuditUsesCatalogOperationRatherThanPreviewOrSessionArguments(string name, bool write)
    {
        var definition = FoundationTools.Definitions.Single(d => d.Name == name);
        Assert.Equal(write, new FoundationTool(definition, null!).IsWrite);
    }
}
