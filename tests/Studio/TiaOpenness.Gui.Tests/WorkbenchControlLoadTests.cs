using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using TiaOpenness.Gui.ControlChannel;
using TiaOpenness.Shared;
using Xunit;
using static TiaOpenness.Gui.Tests.WorkbenchControlTests;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class WorkbenchControlLoadTests(WpfContext wpf)
{
    [Fact]
    public async Task Continuous_control_queue_keeps_existing_dispatcher_latency_budget()
    {
        var (window, _, client) = wpf.Run(() => Window());
        using var server = new WorkbenchControlServer(window.ControlSurface, window.Dispatcher, _ => Environment.ProcessPath!);
        var latency = new ConcurrentBag<double>();
        var sampling = Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                long start = Stopwatch.GetTimestamp();
                await window.Dispatcher.InvokeAsync(() => latency.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds), DispatcherPriority.Background);
                await Task.Delay(5);
            }
        });
        try
        {
            for (int i = 0; i < 100; i++)
            {
                var request = Request(session: i.ToString("x16"));
                request.Arguments = new WorkbenchDisplayPageArguments { Page = i % 2 == 0 ? WorkbenchPage.Log : WorkbenchPage.Environment };
                Assert.Equal(WorkbenchControlStatus.Done, (await server.Execute(request)).Status);
                Assert.Equal(WorkbenchControlStatus.Done, (await server.Execute(Request(WorkbenchControlOperation.ReadState))).Status);
                await Task.Delay(5);
            }
            await sampling;
            var samples = latency.Order().ToArray();
            Assert.Equal(100, samples.Length);
            Assert.True(samples[95] < 50, "Control dispatcher p95: " + samples[95] + "ms");
            Assert.True(samples[^1] < 200, "Control dispatcher max: " + samples[^1] + "ms");
            Assert.Empty(client.Calls);
        }
        finally { await sampling; wpf.Run(window.Close); }
    }
}
