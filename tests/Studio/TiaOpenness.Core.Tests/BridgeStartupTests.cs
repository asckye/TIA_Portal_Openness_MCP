using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Client;
using Xunit;

namespace TiaOpenness.Core.Tests;

public sealed class BridgeStartupTests
{
    [Fact]
    public async Task Cancelled_start_launches_no_process_and_a_later_mock_handshake_can_complete()
    {
        var image = Path.Combine(AppContext.BaseDirectory, "bridge", "TiaOpenness.Bridge.exe");
        using var bridge = new BridgeClient([]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.StartAsync(image, true, "19", cancelled.Token));
        Assert.False(bridge.IsRunning);
        await bridge.StartAsync(image, true, "19");
        Assert.True(bridge.IsRunning);
        Assert.Null((await bridge.CallRawAsync("session.state")).Error);
    }
}
