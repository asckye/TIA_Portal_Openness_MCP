using TiaMcp.Adapters.Contracts;
using Xunit;

public sealed class AdapterContractTests
{
    public static IEnumerable<object[]> Errors() =>
        from code in Enum.GetValues<AdapterErrorCode>()
        from outcome in Enum.GetValues<AdapterOutcome>()
        select new object[] { code, outcome };

    [Theory]
    [MemberData(nameof(Errors))]
    public void ErrorPreservesHostMessageCauseAndClassification(AdapterErrorCode code, AdapterOutcome outcome)
    {
        var cause = new InvalidOperationException("native cause");
        var error = new AdapterException(code, outcome, "host-owned text", innerException: cause);
        Assert.Equal(code, error.Code);
        Assert.Equal(outcome, error.Outcome);
        Assert.Equal("host-owned text", error.Message);
        Assert.Same(cause, error.InnerException);
        Assert.Empty(error.Evidence);
    }

    [Fact]
    public void EvidenceIsAnImmutableSnapshotWithOrdinalKeys()
    {
        var evidence = new Dictionary<string, string> { ["Operation"] = "Import", ["operation"] = "unknown" };
        var error = new AdapterException(AdapterErrorCode.NativeFailure, AdapterOutcome.Unknown, "failed", evidence);
        evidence["Operation"] = "changed";
        evidence.Clear();
        Assert.Equal("Import", error.Evidence["Operation"]);
        Assert.Equal("unknown", error.Evidence["operation"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)error.Evidence).Add("new", "value"));
    }

    [Fact]
    public void ContractAssemblyHasNoNativeOrJsonReferences()
    {
        var references = typeof(IOpennessAdapter).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, r => r.Name!.StartsWith("Siemens", StringComparison.OrdinalIgnoreCase)
            || r.Name.Contains("Json", StringComparison.OrdinalIgnoreCase));
        Assert.All(GoldenSamples.Types, t => Assert.Same(typeof(IOpennessAdapter).Assembly, t.Assembly));
    }

    [Fact]
    public void FacetsRemainMarkersAndCapabilitiesCanBeCombined()
    {
        foreach(var facet in new[] { typeof(IPortalSession), typeof(IPlcProgram), typeof(IPlcData),
            typeof(IHardware), typeof(IVersionControl), typeof(IHmiExport) })
        {
            Assert.True(facet.IsInterface);
            Assert.Empty(facet.GetMembers());
        }
        var capabilities = AdapterCapabilities.PortalSession | AdapterCapabilities.PlcProgram;
        Assert.True(capabilities.HasFlag(AdapterCapabilities.PlcProgram));
        Assert.False(capabilities.HasFlag(AdapterCapabilities.HmiExport));
    }
}
