using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Online;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed class OnlineTransitionRefusalTests
{
    [Fact]
    public void Route_write_followed_by_refusal_keeps_prior_changes()
    {
        var adapter = new OnlineDownloadAdapter();
        adapter.FixtureSession.Download.Configuration.Apply = () => false;
        var result = adapter.DownloadToPlc(new OnlineRequest { SoftwarePath = "PLC", TargetIpAddress = "192.0.2.1", DownloadScope = "software" });
        Assert.Equal(0, adapter.FixtureSession.Download.Calls);
        Assert.False(result.Ok);
        Assert.Equal(true, result.Meta!["mayHaveChanged"]);
        Assert.Equal(false, result.Meta["writeAttempted"]);
        var completion = OnlineCompletionPolicy.Complete(new OnlineRequest { Operation = "DownloadPlc", DryRun = false }, result, true);
        Assert.True(completion.MayHaveChanged);
        Assert.True(completion.OutcomeUnknown);
    }

    [Theory]
    [InlineData("missing", false)]
    [InlineData("provider", false)]
    [InlineData("route", false)]
    [InlineData("download", true)]
    public void Download_reply_records_dispatch(string failure, bool issued)
    {
        var adapter = new OnlineDownloadAdapter();
        adapter.FixtureSession.Failure = failure;
        if (failure == "route") adapter.FixtureSession.Download.Configuration.Modes.Clear();
        var reply = adapter.DownloadToPlc(new OnlineRequest { Operation = "DownloadPlc", SoftwarePath = "PLC",
            TargetIpAddress = "192.0.2.1", DryRun = false, DownloadScope = "software" });
        Assert.Equal(issued ? 1 : 0, adapter.FixtureSession.Download.Calls);
        Assert.False(reply.Ok);
        Assert.NotNull(reply.Meta);
        Assert.Equal(issued, reply.Meta["writeAttempted"]);
        var completion = OnlineCompletionPolicy.Complete(new OnlineRequest { Operation = "DownloadPlc", DryRun = false }, reply, true);
        Assert.Equal(issued, completion.OutcomeUnknown);
    }

}
