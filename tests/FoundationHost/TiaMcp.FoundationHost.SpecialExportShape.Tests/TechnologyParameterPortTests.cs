using Siemens.Engineering;
using Siemens.Engineering.SW.TechnologicalObjects;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using Xunit;

public sealed class TechnologyParameterPortTests
{
    [Theory]
    [InlineData(EngineeringAttributeAccessMode.Read, true)]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData(EngineeringAttributeAccessMode.ReadWrite, false)]
    public void Set_parameter_uses_instance_metadata_and_defers_to_the_setter_if_unavailable(EngineeringAttributeAccessMode? access, bool rejectWrite)
    {
        const string name = "Retain.CtrlParams.Gain";
        var session = new WatchShapeSession();
        var port = new PlcOrganisationAdapter(session);
        var db = session.Software.TechnologicalObjectGroup.TechnologicalObjects.Create("MCP_RC_PIDC", "PID_Compact", new Version(2, 3));
        var parameter = new TechnologicalParameter { Name = name, ValueAccess = access, RejectValueWrite = rejectWrite };
        db.Parameters.Add(parameter);
        HardwareAddressingReply Call(bool preview) => port.ManageTechnologyObject("PLC", db.Name, "setParameter", "", "", name,
            new HardwareScalar { Kind = "number", Text = "2.5" }, "", preview);

        if (access == EngineeringAttributeAccessMode.Read)
        {
            var refusal = Assert.Throws<AdapterPreconditionException>(() => Call(true));
            Assert.Contains(name, refusal.Message);
            Assert.Equal("parameter", refusal.ParamName);
        }
        else
        {
            var preview = Call(true);
            Assert.Equal(access == null, preview.Meta.ContainsKey("writabilityNote"));
            if (access == null) Assert.Contains("setter", Assert.IsType<string>(preview.Meta["writabilityNote"]));
        }
        Assert.Equal(1, parameter.MetadataReads);
        Assert.Equal(0, parameter.SetCalls);
        Assert.Equal(1.0f, parameter.Value);
        if (access == EngineeringAttributeAccessMode.Read) return;
        if (rejectWrite)
        {
            var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => Call(false));
            Assert.Equal("Value is read-only.", error.GetBaseException().Message);
            Assert.True(parameter.SetCalls >= 1);
            Assert.Equal(1.0f, parameter.Value);
        }
        else
        {
            Call(false);
            Assert.Equal(1, parameter.SetCalls);
            Assert.Equal(2.5f, parameter.Value);
        }
        Assert.Equal(2, parameter.MetadataReads);
    }
}
