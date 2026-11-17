using TiaMcp.Adapters.Contracts;
using TiaMcp.PlcWorker;
using Xunit;

public sealed class OnlineDownloadWorkerGuardTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void Raw_worker_download_cannot_bypass_physical_confirmation(bool preview, bool confirmed, bool simulated)
    {
        var request = new OnlineRequest { Operation = "DownloadPlc", DryRun = preview, ConfirmPhysicalTarget = confirmed,
            PgPcInterface = "PLCSIM", TargetIpAddress = "192.168.1.10" };
        int nativeCalls = 0;
        void RawWorkerCall()
        {
            OnlineDownloadGuard.Require(request, preview, _ => simulated);
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
                PgPcInterface = "PLCSIM", TargetIpAddress = "192.168.1.10" };
            Assert.Throws<AdapterPreconditionException>(() => OnlineDownloadGuard.Require(request, preview, _ => false));
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
