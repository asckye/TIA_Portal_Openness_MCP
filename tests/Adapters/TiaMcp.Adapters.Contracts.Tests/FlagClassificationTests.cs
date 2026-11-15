using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class FlagClassificationTests
{
    public static IEnumerable<object[]> Releases => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });

    public static IEnumerable<object[]> ConnectionSteps => Releases.SelectMany(row =>
        new[] { new object[] { row[0], false }, new object[] { row[0], true } });

    [Theory, MemberData(nameof(ConnectionSteps))]
    public void Hmi_connection_loss_is_unknown_only_for_a_writing_step(string release, bool writing)
    {
        var fixture = HardwareWriteFailureTests.NativeFixture(release);
        int calls = 0;
        var reply = fixture.Engine.RunHmiStep("Fixture", meta => {
            calls++;
            meta["connectionUnavailable"] = true;
            throw new IOException("Synthetic connection lost before mutation evidence was recorded.");
        }, true, writing);
        Assert.Equal(1, calls);
        Assert.Equal(false, reply.Meta["operationSuccess"]);
        Assert.True((bool)reply.Meta["connectionUnavailable"]!);
        Assert.False(reply.MayHaveChanged);
        Assert.Equal(writing, reply.OutcomeUnknown);
        Assert.Equal(writing, reply.Meta.TryGetValue("writeOutcomeUnknown", out var unknown) && unknown is true);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Failed_hmi_read_does_not_set_outcome_unknown(string release)
    {
        var fixture = HardwareWriteFailureTests.NativeFixture(release);
        var reply = fixture.Engine.RunHmiStep("GetHmiProgramInfo", meta => {
            meta["connectionUnavailable"] = true;
            throw new IOException("Synthetic connection lost during a read.");
        }, true, writing: false);
        Assert.Equal(false, reply.Meta["operationSuccess"]);
        Assert.True((bool)reply.Meta["connectionUnavailable"]!);
        Assert.False(reply.OutcomeUnknown);
        Assert.False(reply.MayHaveChanged);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Compile_with_errors_does_not_set_outcome_unknown(string release)
    {
        var fixture = HardwareWriteFailureTests.NativeFixture(release);
        var reply = fixture.Engine.RunHmiStep("CompileDevice", meta => {
            meta["mayHaveChanged"] = true;
            meta["nativeCompleted"] = true;
            meta["apiCallSuccess"] = true;
            meta["operationSuccess"] = false;
            meta["errors"] = new[] { "Synthetic compiler error." };
            return "Compile finished with errors.";
        }, true, writing: false);
        Assert.Equal(false, reply.Meta["operationSuccess"]);
        Assert.True(reply.MayHaveChanged);
        Assert.False(reply.OutcomeUnknown);
    }
}
