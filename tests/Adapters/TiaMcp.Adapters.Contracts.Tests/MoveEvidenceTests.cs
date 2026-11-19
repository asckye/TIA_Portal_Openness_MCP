using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class MoveEvidenceTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("restoreAttempted", false)]
    [InlineData("restored", false)]
    [InlineData("originalRetained", false)]
    [InlineData("targetGroupsCreated", true)]
    [InlineData("cleanupRequired", true)]
    [InlineData("rollbackCompleted", false)]
    [InlineData("writeOutcomeUnknown", true)]
    [InlineData("unrestoredWrite", true)]
    [InlineData("connectionUnavailable", true)]
    public void Only_a_verified_restore_without_outstanding_effects_settles_a_move(string? key, bool flag)
    {
        var evidence = new Dictionary<string, object?> { ["restoreAttempted"] = true, ["restored"] = true, ["originalRetained"] = true,
            ["mayHaveChanged"] = true, ["targetGroupsCreated"] = Array.Empty<string>(), ["cleanupRequired"] = new List<object?>() };
        if (key != null) evidence[key] = key is "targetGroupsCreated" or "cleanupRequired" ? new[] { "G" } : flag;
        Assert.Equal(key == null, PlcMovePolicy.SettledByRestore(evidence));
    }

    [Fact]
    public void Outer_move_evidence_wins_over_nested_group_evidence()
    {
        var inner = new InvalidOperationException("Group creation failed.");
        inner.Data["nativeResultEvidence"] = new Dictionary<string, object?>(StringComparer.Ordinal) {
            ["mayHaveChanged"] = false, ["rollbackCompleted"] = true, ["restored"] = false };
        var outer = new IOException("The source block was restored.", inner);
        outer.Data["nativeResultEvidence"] = new Dictionary<string, object?>(StringComparer.Ordinal) {
            ["mayHaveChanged"] = true, ["restored"] = true };
        var evidence = new Dictionary<string, object?>(StringComparer.Ordinal) { ["deleteStarted"] = true };
        var method = typeof(PlcMovePolicy).GetMethod("MergeEvidence");
        Assert.NotNull(method);
        var merge = method.CreateDelegate<Action<Exception, IDictionary<string, object?>>>();
        merge(outer, evidence);
        Assert.Equal(true, evidence["mayHaveChanged"]);
        Assert.Equal(true, evidence["restored"]);
        Assert.Equal(true, evidence["rollbackCompleted"]);
        Assert.Equal(true, evidence["deleteStarted"]);
    }
}
