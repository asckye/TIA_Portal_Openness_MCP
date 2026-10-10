using System.Text.Json;
using TiaMcp.Adapters.Contracts;
using TiaMcp.PlcWorker;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class PlcIntegrityPortTests
{
    [Theory]
    [InlineData("14sp1", 0)]
    [InlineData("15.1", 2)]
    [InlineData("16", 5)]
    [InlineData("17", 5)]
    [InlineData("18", 5)]
    [InlineData("19", 5)]
    [InlineData("20", 5)]
    [InlineData("21", 6)]
    public void RegistrationRequiresAUsableApi(string release, int count)
    {
        var family = PortedFamilies.ForTool("ListPlcSoftwareUnits");
        Assert.Equal(count, family.Tools.Count(tool => PortedFamilies.Available(release, tool)));
        foreach (var tool in family.Tools)
            Assert.Equal(!PortedFamilies.Available(release, tool), PlcSoftwareCapabilities.Unsupported(release, tool) != null);
    }

    [Theory]
    [InlineData("15.1", "GetPlcObjectFingerprints", "unit", "unit")]
    [InlineData("16", "GetPlcObjectFingerprints", "unit", "safety")]
    [InlineData("17", "ListPlcSoftwareUnits", "", "safety")]
    [InlineData("17", "ManagePlcSoftwareUnit", "", "safety")]
    public void UnavailableUnitScopesAreRefused(string release, string tool, string name, string kind)
        => Assert.NotNull(PlcSoftwareCapabilities.Unsupported(release, tool, unitName: name, unitKind: kind));

    [Theory]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    public void OlderMasterCopyOverloadRemainsUsable(string release)
    {
        Assert.Null(PlcSoftwareCapabilities.Unsupported(release, "ManagePlcSoftwareUnit", "createFromMasterCopy"));
        Assert.NotNull(PlcSoftwareCapabilities.Unsupported(release, "ManagePlcSoftwareUnit", "createFromMasterCopy", copyMode: "Replace"));
    }

    [Fact]
    public void TypedRequestPreservesPublicationProtectionAndUnitDetails()
    {
        var request = new PlcSoftwareRequest { Operation = "ManagePlcSoftwareUnit", SoftwarePath = "PLC", UnitKind = "safety", RelatedUnit = "Peer", RelationType = "SoftwareUnit", Access = "Published", NewPassword = "fixture", Comments = new[] { new PlcTagComment { Culture = "en-US", Text = "comment" } } };
        var copy = JsonSerializer.Deserialize<PlcSoftwareRequest>(JsonSerializer.Serialize(request))!;
        Assert.Equal(request.RelatedUnit, copy.RelatedUnit);
        Assert.Equal(request.RelationType, copy.RelationType);
        Assert.Equal(request.UnitKind, copy.UnitKind);
        Assert.Equal(request.Access, copy.Access);
        Assert.Equal(request.NewPassword, copy.NewPassword);
        Assert.Equal("comment", Assert.Single(copy.Comments).Text);
    }

    [Fact]
    public void IntegrityDispatchUsesTheExplicitFamilyAllowList()
    {
        Assert.Equal(new[] { "ExecutePlcOrganisation" }, WorkerOperations.FamilyNames("F16"));
        Assert.True(WorkerOperations.IsFamilyOperation("plc-integrity.ExecutePlcOrganisation"));
        Assert.False(WorkerOperations.IsFamilyOperation("plc-integrity.DeleteEverything"));
    }
}
