using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Online;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed class OnlineTransitionRefusalTests
{
    [Theory]
    [InlineData("apply", "Offline")]
    [InlineData("connect", "Online")]
    public void Online_refusal_retains_the_route_write_and_observed_state(string failure, string after)
    {
        var adapter = new OnlineDownloadAdapter();
        var provider = adapter.FixtureSession.Online;
        int applies = 0, connects = 0;
        provider.Read = () => connects == 0 ? "Offline" : after;
        provider.Configuration.Apply = () => { applies++; return failure != "apply"; };
        provider.Connect = () => { connects++; throw new InvalidOperationException("Transition refused"); };
        var reply = adapter.GoOnline("PLC", "192.0.2.1", null, null, null, "", null, false);
        Assert.Equal(1, applies);
        Assert.Equal(failure == "apply" ? 0 : 1, connects);
        Assert.Equal(after, reply.State);
        Assert.Equal(after == "Online", reply.IsOnline);
        Assert.True(reply.Meta!.TryGetValue("mayHaveChanged", out var changed) && changed is true);
        var completion = OnlineCompletionPolicy.Complete(new OnlineRequest { Operation = "ConnectOnlinePlc", DryRun = false }, reply, false);
        Assert.True(completion.OutcomeUnknown);
    }

    [Theory]
    [InlineData("primary", "Online")]
    [InlineData("backup", "Offline")]
    public void Failed_redundant_connection_reads_the_selected_CPU(string cpu, string state)
    {
        var adapter = new OnlineDownloadAdapter();
        var provider = adapter.FixtureSession.Redundant;
        provider.PrimaryState = Siemens.Engineering.Online.OnlineState.Online;
        provider.BackupState = Siemens.Engineering.Online.OnlineState.Offline;
        adapter.FixtureSession.Online.Read = () => throw new InvalidOperationException("Wrong PLC provider");
        var reply = adapter.GoOnline("PLC", "192.0.2.3", null, null, null, cpu, null, false);
        Assert.Equal(cpu, provider.Issued);
        Assert.Equal(state, reply.State);
        Assert.Equal(state == "Online", reply.IsOnline);
        var completion = OnlineCompletionPolicy.Complete(new OnlineRequest { Operation = "ConnectOnlinePlc", DryRun = false }, reply, true);
        Assert.True(completion.OutcomeUnknown);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    public void Unreadable_disconnect_state_is_unknown(string when)
    {
        var adapter = new OnlineDownloadAdapter();
        var provider = adapter.FixtureSession.Online;
        int calls = 0;
        provider.Disconnect = () => calls++;
        provider.Read = () => when == "before" || calls > 0 ? throw new IOException("State unavailable") : "Online";
        var reply = adapter.GoOffline("PLC");
        Assert.Equal(when == "before" ? 0 : 1, calls);
        Assert.NotNull(reply.Meta);
        Assert.Equal(false, reply.Meta!.TryGetValue("writeOutcomeKnown", out var known) ? known : null);
    }

    [Fact]
    public void Route_write_followed_by_refusal_keeps_prior_changes()
    {
        var adapter = new OnlineDownloadAdapter();
        adapter.FixtureSession.Failure = "route-write";
        var result = adapter.DownloadToPlc(new OnlineRequest { SoftwarePath = "PLC", TargetIpAddress = "192.0.2.1", DownloadScope = "software" });
        Assert.Equal(0, adapter.FixtureSession.Download.Calls);
        Assert.False(result.Ok);
        Assert.Equal(true, result.Meta!["mayHaveChanged"]);
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
        var reply = adapter.DownloadToPlc(new OnlineRequest { Operation = "DownloadPlc", SoftwarePath = "PLC",
            TargetIpAddress = "192.0.2.1", DryRun = false, DownloadScope = "software" });
        Assert.Equal(issued ? 1 : 0, adapter.FixtureSession.Download.Calls);
        Assert.False(reply.Ok);
        Assert.NotNull(reply.Meta);
        Assert.Equal(issued, reply.Meta["writeAttempted"]);
        var completion = OnlineCompletionPolicy.Complete(new OnlineRequest { Operation = "DownloadPlc", DryRun = false }, reply, true);
        Assert.Equal(issued, completion.OutcomeUnknown);
    }

    [Theory]
    [InlineData("Online", true)]
    [InlineData("Offline", false)]
    [InlineData("Connecting", false)]
    [InlineData("unreadable", false)]
    [InlineData("timeout", false)]
    public void Offline_refusal_requires_the_same_stable_state(string after, bool unchanged)
    {
        var adapter = new OnlineDownloadAdapter();
        int calls = 0;
        var provider = adapter.FixtureSession.Online;
        provider.Read = () => calls == 0 ? "Online" : after == "unreadable" ? throw new IOException() : after;
        provider.Disconnect = () => { calls++; if (after == "timeout") throw new TimeoutException(); throw new InvalidOperationException("Lost reply."); };
        var reply = adapter.GoOffline("PLC");
        Assert.Equal(1, calls);
        Assert.Equal(unchanged, reply.Meta.TryGetValue("writeOutcomeKnown", out var known) && known is true);
        var completion = OnlineCompletionPolicy.Complete(new OnlineRequest { Operation = "DisconnectOnlinePlc", DryRun = false }, reply, true);
        Assert.Equal(!unchanged, completion.OutcomeUnknown);
        Assert.Equal(!unchanged, completion.MayHaveChanged);
    }
}
