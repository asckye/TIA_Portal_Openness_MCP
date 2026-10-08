using TiaMcp.FoundationHost;
using Xunit;

public sealed class ConcurrencyTests
{
    private static WorkerClient Worker() => new("19", "unused-worker.exe", "unused-api", false);

    [Fact]
    public async Task Worker_lanes_are_independent_and_queued_cancellation_releases_no_extra_permit()
    {
        using var first = Worker(); using var second = Worker();
        var held = await first.AcquireLane(CancellationToken.None);
        using (await second.AcquireLane(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2))) { }
        using var cancel = new CancellationTokenSource();
        var queued = first.AcquireLane(cancel.Token);
        Assert.False(queued.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queued);
        held!.Dispose();
        using (await first.AcquireLane(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2))) { }
    }

    [Fact]
    public async Task Renderer_has_no_worker_lane_even_when_that_worker_is_busy()
    {
        using var worker = Worker();
        using var held = await worker.AcquireLane(CancellationToken.None);
        var renderer = FoundationTools.Create(worker, "19").OfType<FoundationTool>()
            .First(tool => !tool.IsNative);
        Assert.Null(await renderer.AcquireLane(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Native_tool_waits_for_worker_and_reentrant_scope_does_not_reacquire()
    {
        using var worker = Worker();
        var native = FoundationTools.Create(worker, "19").OfType<FoundationTool>().First(tool => tool.IsNative);
        var held = await worker.AcquireLane(CancellationToken.None);
        var queued = native.AcquireLane(CancellationToken.None);
        Assert.False(queued.IsCompleted);
        held!.Dispose();
        using var acquired = await queued.WaitAsync(TimeSpan.FromSeconds(2));
        WorkerClient.ActivateLane(acquired);
        Assert.Null(await native.AcquireLane(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
    }
}
