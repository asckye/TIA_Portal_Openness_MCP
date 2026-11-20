using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using TiaMcpServer.Siemens;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed class NativeCrossReferenceDefaultsTests
{
    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void Native_queries_are_allowed_by_default_where_the_release_has_the_API(string release)
    {
        var compiled = new[] { (Path: "Main", Consistent: (bool?)true) };
        Assert.Null(CrossReferenceGuardLogic.Refusal(compiled, "PLC"));
        Assert.Null(PlcCrossReferencePolicy.Refusal(compiled, "PLC"));
        Assert.Equal(release is "18" or "19" or "20" or "21",
            PlcSoftwareCapabilities.Unsupported(release, "GetPlcCrossReferences") == null);
        Assert.Equal(release is "19" or "20" or "21", PortedFamilies.Available(release, "GetUnifiedCrossReferences"));
    }

    [Fact]
    public void Inconsistent_blocks_and_reflection_are_still_refused()
    {
        Assert.False(PortedFamilies.Available("future", "GetPlcCrossReferences"));
        Assert.False(PortedFamilies.Available("future", "GetUnifiedCrossReferences"));
        foreach (bool? consistent in new bool?[] { false, null })
        {
            var blocks = new[] { (Path: "Group/Block", Consistent: consistent) };
            var refusal = CrossReferenceGuardLogic.Refusal(blocks, "PLC");
            Assert.Equal(refusal, PlcCrossReferencePolicy.Refusal(blocks, "PLC"));
            Assert.Contains("No native query was made", refusal);
        }
        foreach (var service in new[] { "CrossReferenceService", "CrossReference", "Siemens.Engineering.CrossReference.CrossReferenceService" })
        {
            var refusal = CrossReferenceGuardLogic.ReflectionRefusal(service, "GetCrossReferences");
            Assert.NotNull(refusal);
            Assert.Equal(refusal, PlcCrossReferencePolicy.ReflectionRefusal(service, "GetCrossReferences"));
            Assert.Equal(refusal, OpennessReflectionPolicy.ReflectionRefusal(service, "GetCrossReferences"));
        }
    }

    [Fact]
    public void Removed_consent_contract_is_absent_from_the_adapter_assembly()
    {
        Assert.Null(typeof(PlcSoftwareCapabilities).Assembly.GetType("TiaMcp.Adapters.Contracts.NativeCrossReferenceConsent"));
    }
}
