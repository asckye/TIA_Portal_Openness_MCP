extern alias foundationhost;
using Xunit;
using Client = foundationhost::TiaMcp.FoundationHost.EngineWorkerClient;
using Options = foundationhost::TiaMcp.FoundationHost.HostOptions;

public sealed class EngineWorkerClientTests
{
    private static Client Create() => new(new Options { ReleaseKey = "21", BundleRoot = AppContext.BaseDirectory,
        WorkerExe = "unused", ApiDirectory = "unused", EngineWorkerExe = Path.Combine(AppContext.BaseDirectory, "missing-fixture-worker.exe") }, "unused");

    [Fact]
    public async Task RestartPreviewAndBusyRefusalNeverAdvanceGeneration()
    {
        using var worker = Create();
        object key = worker.SessionKey;
        var preview = await worker.Restart(false, CancellationToken.None);
        Assert.True((bool?)preview["dryRun"]);
        Assert.Same(key, worker.SessionKey);
        using var active = await worker.Acquire(CancellationToken.None);
        var queued = worker.Acquire(CancellationToken.None);
        Assert.Equal(1, (int?)worker.Snapshot()["queued"]);
        var refused = await worker.Restart(true, CancellationToken.None);
        Assert.False((bool?)refused["success"]);
        Assert.Equal(1, (long?)worker.Snapshot()["generation"]);
        Assert.Same(key, worker.SessionKey);
        active.Dispose();
        using var drained = await queued;
    }

    [Fact]
    public async Task FailedExplicitRestartRetainsPriorLockAndNeverRestartsImplicitly()
    {
        using var worker = Create();
        object prior = worker.SessionKey;
        worker.SessionLocked = () => ReferenceEquals(prior, worker.SessionKey);
        await Assert.ThrowsAsync<FileNotFoundException>(() => worker.Restart(true, CancellationToken.None));
        Assert.NotSame(prior, worker.SessionKey);
        var snapshot = worker.Snapshot();
        Assert.Equal(2, (long?)snapshot["generation"]);
        Assert.True((bool?)snapshot["previousGenerations"]![0]!["sessionLocked"]);
        Assert.Null(snapshot["previousGenerations"]![0]!["previousGenerations"]);
        Assert.True(worker.Faulted);
        await worker.Status(CancellationToken.None);
        Assert.Equal(2, (long?)worker.Snapshot()["generation"]);
    }
}
