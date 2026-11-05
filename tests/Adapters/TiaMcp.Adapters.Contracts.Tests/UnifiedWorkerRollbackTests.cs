extern alias unifiedworkerfixture;
using TiaMcp.Adapters.Contracts;
using Xunit;
using Worker = unifiedworkerfixture::TiaMcp.Adapters.PlcFoundationEngine;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class UnifiedWorkerRollbackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Completed_reply_without_rollback_returns_normally(bool changed)
    {
        var worker = new Worker { NativeMayHaveChanged = changed, NativeReply = new HmiMessage {
            Message = "completed", Meta = new Dictionary<string, object?> { ["operationSuccess"] = true } } };
        var reply = worker.ExecuteUnifiedHmi(Request(), false);
        Assert.Same(worker.NativeReply, reply.Value);
        Assert.Null(reply.Failure);
        Assert.Equal(1, worker.NativeCalls);
        Assert.Equal(changed, reply.MayHaveChanged);
        Assert.False(reply.RequiresSessionReset);
    }

    [Theory]
    [InlineData("partial", false)]
    [InlineData("unknown", true)]
    public void Rollback_reply_retains_its_reset_policy(string outcome, bool reset)
    {
        var worker = new Worker { NativeMayHaveChanged = true, NativeReply = new HmiMessage {
            Meta = new Dictionary<string, object?> { ["operationSuccess"] = false, ["rollbackOutcome"] = outcome } } };
        var reply = worker.ExecuteUnifiedHmi(Request(), false);
        Assert.Same(worker.NativeReply, reply.Value);
        Assert.Null(reply.Failure);
        Assert.Equal(1, worker.NativeCalls);
        Assert.True(reply.MayHaveChanged);
        Assert.Equal(reset, reply.RequiresSessionReset);
    }

    private static UnifiedHmiRequest Request() => new() {
        Operation = "EnsureUnifiedHmiScreen", HmiSoftwarePath = "HMI", ScreenName = "Main", DryRun = false };
}
