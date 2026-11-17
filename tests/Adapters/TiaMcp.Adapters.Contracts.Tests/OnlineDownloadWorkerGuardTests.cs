using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Engine.Tests.Shared;
using TiaMcp.PlcWorker;
using Xunit;

public sealed class OnlineDownloadWorkerGuardTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void Raw_worker_download_cannot_bypass_physical_confirmation(bool preview, bool confirmed, bool simulated)
    {
        var request = new OnlineRequest { Operation = "DownloadPlc", DryRun = preview, ConfirmPhysicalTarget = confirmed,
            PgPcInterface = "PLCSIM", TargetIpAddress = DownloadTargetCases.Target };
        int nativeCalls = 0;
        void RawWorkerCall()
        {
            OnlineDownloadGuard.Require(request, preview, () => DownloadTargetCases.Listing(simulated ? request.TargetIpAddress : "198.51.100.1"),
                _ => DownloadTargetCases.Network());
            nativeCalls++;
        }
        if (!preview && !confirmed && !simulated)
        {
            var error = Assert.Throws<AdapterPreconditionException>(RawWorkerCall);
            Assert.Contains("confirmPhysicalTarget=true", error.Message);
            Assert.Equal(0, nativeCalls);
        }
        else
        {
            RawWorkerCall();
            Assert.Equal(1, nativeCalls);
        }
    }

    [Fact]
    public void Conflicting_preview_flags_do_not_bypass_the_worker_guard()
    {
        foreach (bool preview in new[] { false, true })
        {
            var request = new OnlineRequest { Operation = "DownloadPlc", DryRun = !preview,
                PgPcInterface = "PLCSIM", TargetIpAddress = DownloadTargetCases.Target };
            Assert.Throws<AdapterPreconditionException>(() => OnlineDownloadGuard.Require(request, preview, () => DownloadTargetCases.Listing(),
                _ => DownloadTargetCases.Network()));
        }
    }

    [Theory, MemberData(nameof(DownloadTargetCases.Cases), MemberType = typeof(DownloadTargetCases))]
    public void Raw_worker_target_evidence_matches_host_policy(string name, string adapter, string target, string listingJson, string networkJson,
        string expected)
    {
        output.WriteLine(name);
        var request = new OnlineRequest { Operation = "DownloadPlc", DryRun = false,
            PgPcInterface = adapter, TargetIpAddress = target };
        int nativeCalls = 0;
        void Dispatch()
        {
            OnlineDownloadGuard.Require(request, false, () => JsonNode.Parse(listingJson)!.AsObject(),
                _ => JsonNode.Parse(networkJson)!.AsObject());
            nativeCalls++;
        }
        if (expected == "physical")
        {
            Assert.Throws<AdapterPreconditionException>(Dispatch);
            Assert.Equal(0, nativeCalls);
        }
        else
        {
            Dispatch();
            Assert.Equal(1, nativeCalls);
        }
    }

    [Fact]
    public void Dispatcher_checks_the_guard_before_entering_the_native_operation()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "AGENTS.md"))) root = root.Parent;
        Assert.NotNull(root);
        string source = File.ReadAllText(Path.Combine(root!.FullName, "src/PlcWorker/FoundationWorkerDispatcher.cs"));
        Assert.Contains("OnlineDownloadGuard.Require(onlineRequest", source);
        Assert.Contains("PlcSimAdvancedWorkerModule.Adapter.ReadPlcSimAdvancedInstances()", source);
        Assert.True(source.IndexOf("OnlineDownloadGuard.Require(onlineRequest", StringComparison.Ordinal)
            < source.IndexOf("enteredOperation=true;", StringComparison.Ordinal));
    }
}
