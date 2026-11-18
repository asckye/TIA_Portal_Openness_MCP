using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Engineering;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class PlcTechnologyRefusalTests
{
    private const string MissingType = "The type or version of the library element \"PID_Compact.2.3\" does not exist or is not a valid technology object.";

    [Theory]
    [InlineData(MissingType, true, true)]
    [InlineData(MissingType, false, false)]
    [InlineData("A different native create error.", true, false)]
    public void Only_the_reported_native_type_failure_after_verified_rollback_is_a_precondition(string nativeMessage, bool absent, bool expected)
    {
        var engine = new PlcFoundationEngine("21");
        var result = engine.RunHmiStep("ManageTechnologyObject", meta =>
        {
            meta["mayHaveChanged"] = true;
            meta["typeIdentifier"] = "PID_Compact";
            meta["version"] = "2.3";
            EngineeringWriteJournal.Creating(() => absent ? null : new object(), "PLC_1/TechnologicalObjects/PID");
            throw new InvalidOperationException(nativeMessage);
        }, requiresProject: false);
        Assert.Equal(expected, PlcTechnologyRefusal.Record(result, "PID_Compact", "2.3"));
        Assert.Equal(expected, result.Meta.ContainsKey("nothingCreated"));
        if (!expected)
        {
            Assert.False(result.Meta.ContainsKey("precondition"));
            return;
        }
        Assert.False(result.MayHaveChanged);
        Assert.False(result.OutcomeUnknown);
        Assert.Equal("restored", result.Meta["rollbackOutcome"]);
        Assert.Equal(true, result.Meta["nothingCreated"]);
        Assert.Equal("PreconditionFailed", result.Meta["status"]);
        Assert.Equal("technology-type-version-unavailable", result.Meta["precondition"]);
        Assert.Contains("PID_Compact", result.Message);
        Assert.Contains("2.3", result.Message);
        Assert.Contains("Technology objects > Add new object", result.Message);
        Assert.Contains("action=read", result.Message);
    }
}
