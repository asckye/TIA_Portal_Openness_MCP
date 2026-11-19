using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class MutationEvidenceTests
{
    [Theory]
    [InlineData(null, null, false)]
    [InlineData(true, null, false)]
    [InlineData(false, null, true)]
    [InlineData(false, "writeOutcomeUnknown", false)]
    [InlineData(false, "connectionUnavailable", false)]
    [InlineData(false, "unrestoredWrite", false)]
    [InlineData(false, "cleanupRequired", false)]
    public void Only_explicit_unchanged_steps_without_outstanding_effects_prove_no_mutation(bool? changed, string? flag, bool expected)
    {
        var meta = new Dictionary<string, object?> { ["cleanupRequired"] = Array.Empty<string>() };
        if (changed.HasValue) meta["mayHaveChanged"] = changed.Value;
        if (flag != null) meta[flag] = flag == "cleanupRequired" ? new[] { "restore FC_A" } : true;
        Assert.Equal(expected, MutationEvidence.ProvesNoMutation(meta));
    }

    [Theory, InlineData("NotFound", "NOT_FOUND"), InlineData("OpennessError", "NATIVE_OPERATION_FAILED")]
    public void Native_status_decides_whether_a_lookup_failed(string status, string expected)
    {
        Assert.Equal(expected, MutationEvidence.FailureCode(new HardwareAddressingException(status, "native diagnostic")));
        Assert.Equal(expected, MutationEvidence.FailureCode(new PlcSoftwareException(status, "native diagnostic")));
        var inner = new InvalidOperationException("native context");
        var wrapped = new Exception("wrapper", new PlcSoftwareException(status, "native diagnostic", inner: inner));
        Assert.Equal(expected, MutationEvidence.FailureCode(wrapped));
        var evidence = new Dictionary<string, object?> { ["mayHaveChanged"] = false };
        MutationEvidence.RecordStepFailure(wrapped, evidence);
        Assert.Equal(status, evidence["v4FailureCode"]);
        Assert.Equal("NATIVE_OPERATION_FAILED", MutationEvidence.FailureCode(new Exception("not found")));
    }

    [Theory, InlineData("tag", "LogicalAddress", "%M4.2"), InlineData("constant", "Value", "42")]
    public void Worker_applies_the_same_tag_field_with_or_without_an_explicit_property(string kind, string property, string address)
    {
        var request = new PlcSoftwareRequest { Kind = kind, Action = "update", AddressOrValue = address };
        request.BindTagUpdate();
        Assert.Equal(address, request.Properties[property].Text);
        request.BindTagUpdate();
        Assert.Single(request.Properties);
        request.Properties[property].Text = "different";
        Assert.Throws<ArgumentException>(() => request.BindTagUpdate());
        Assert.Equal("different", request.Properties[property].Text);
    }

    [Fact]
    public void Archive_and_standard_transfer_area_require_native_target_eligibility()
    {
        foreach (var selection in new[] { (Cpu: true, Integrated: false), (Cpu: false, Integrated: true) })
        {
            var archive = Assert.Throws<AdapterPreconditionException>(() =>
                OptionalProductCapabilities.RequireArchivePlc(selection.Cpu, selection.Integrated));
            Assert.Equal("sinumerik-integrated-plc", archive.Condition);
            Assert.Equal("itemPath", archive.ParamName);
        }
        OptionalProductCapabilities.RequireArchivePlc(true, true);
        var transfer = Assert.Throws<AdapterPreconditionException>(() => Hardware.HardwareNetworkPolicy.RequireStandardTransferAreaMode(false));
        Assert.Equal("i-device-interface", transfer.Condition);
        Assert.Equal("itemPath", transfer.ParamName);
        Hardware.HardwareNetworkPolicy.RequireStandardTransferAreaMode(true);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Reflection_number_refusal_requires_matching_readback(bool changed)
    {
        var target = new Block(changed);
        var session = new TiaMcp.Adapters.Reflection.ReflectionSession((_, _, _, _) => target, () => null);
        var reply = new TiaMcp.Adapters.Reflection.OpennessReflection(session).InvokeObject("Block", "FC_A", "SetAttribute",
            new[] { new HardwareScalar { Kind = "string", Text = "Number" }, new HardwareScalar { Kind = "int", Text = "9" } }, allowWrite: true);
        Assert.Equal(1, target.Attempts);
        Assert.Equal(2, target.Reads);
        Assert.Equal("TIA refused the attribute.", reply.Meta["nativeMessage"]);
        Assert.Equal(!changed, MutationEvidence.ProvesNoMutation(reply.Meta));
    }

    private sealed class Block(bool changed) : Siemens.Engineering.IEngineeringObject
    {
        internal int Attempts, Reads;
        public object GetAttribute(string name) { Reads++; return (ushort)(changed && Attempts > 0 ? 9 : 7); }
        public void SetAttribute(string name, object? value) { Attempts++; throw new InvalidOperationException("TIA refused the attribute."); }
    }

    public static HardwareAddressingReply RunStep(string release, string tool, Func<Dictionary<string, object?>, string> execute)
        => HardwareWriteFailureTests.NativeFixture(release).Engine.RunHmiStep(tool, execute, true);
}
