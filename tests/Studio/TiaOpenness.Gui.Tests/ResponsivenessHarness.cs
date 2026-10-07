using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Services.Stubs;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using TiaDesktop.Glass;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Shared;
using Xunit;

namespace TiaOpenness.Gui.Tests;

// Opt-in, render-free, real dispatcher workload. All data stays below the worktree.
[Collection(WpfCollection.Name)]
public sealed class ResponsivenessHarness(WpfContext wpf)
{
    [Fact]
    public async Task Measure_dispatcher_under_fake_engine_load()
    {
        string? output = Environment.GetEnvironmentVariable("TIA_PERFORMANCE_OUTPUT");
        if (string.IsNullOrEmpty(output)) return;
        string root = Path.GetFullPath(Path.Combine("bin-build", "P6-57", "perf-" + Guid.NewGuid().ToString("N")));
        string directory = Path.Combine(root, "audit");
        Directory.CreateDirectory(directory);
        var writer = new AuditLog(directory);
        for (int i = 0; i < 1000; i++) writer.Append("end", "seed-" + i, "fixture", "21", "Fixture", "succeeded");
        FeaturePagesViewModel model = null!;
        FakeApprovals source = null!;
        ApprovalService approvals = null!;
        AuditLogService audit = null!;
        GlassLogView log = null!;
        var journal = new FeaturePageFixtures.Journal();
        var rows = Enumerable.Range(0, 5000).Select(i => journal.Calls[0] with
        { RequestId = "call-" + i, JournalKey = "call-" + i, Time = DateTimeOffset.UtcNow.AddMilliseconds(-i) }).ToArray();
        journal.Replace(rows);
        int notifications = 0, fullRefreshes = 0;
        wpf.Run(() =>
        {
            approvals = new ApprovalService(Path.Combine(root, "config", "approval"), "perf-" + Guid.NewGuid().ToString("N"), writer);
            audit = new AuditLogService(directory, Path.Combine(root, "diagnostics"), Path.Combine(root, "config", "retention"), _ => { });
            source = new FakeApprovals(approvals);
            model = new FeaturePagesViewModel(source, journal, audit, new FeaturePageFixtures.Environment(), new FeaturePageFixtures.Diagnostics());
            log = new GlassLogView();
            model.PropertyChanged += (_, e) =>
            {
                notifications++;
                if (string.IsNullOrEmpty(e.PropertyName)) fullRefreshes++;
                // Simulate binding getters without generating 5000 visual elements.
                if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(model.Calls))
                { _ = model.Calls; _ = model.CallsEmpty; _ = model.CallsCount; _ = model.SelectedCall; }
                if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(model.AuditRows)) _ = model.AuditRows;
            };
        });
        try
        {
            async Task<object> Sample(bool busy)
            {
                var latency = new ConcurrentBag<double>();
                int lines = 0;
                string text = string.Concat(Enumerable.Range(0, 750).Select(i => "12:34:56  initial stderr output " + i + new string('x', 20) + "\n"));
                wpf.Run(() => log.LogText = text);
                await Task.Delay(1000); wpf.Run(() => { });
                var process = Process.GetCurrentProcess();
                var cpu = process.TotalProcessorTime;
                int before = notifications, fullBefore = fullRefreshes;
                var watch = Stopwatch.StartNew();
                if (busy) wpf.Run(() => source.Seed());
                using var probes = new Timer(_ =>
                {
                    long queued = Stopwatch.GetTimestamp();
                    log.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => latency.Add(Stopwatch.GetElapsedTime(queued).TotalMilliseconds)));
                }, null, 0, 10);
                while (watch.Elapsed.TotalSeconds < 6)
                {
                    if (busy)
                    {
                        int index = lines++ % rows.Length;
                        rows[index] = rows[index] with { DurationMs = lines, Result = lines % 2 == 0 ? CallResult.Success : CallResult.Executing };
                        journal.Replace(rows.ToArray());
                        writer.Append("end", "burst-" + lines, "fixture", "21", "Fixture", "succeeded");
                        text += "12:34:56  fixture stderr line " + lines + "\n";
                        if (text.Length > 40000) text = text[(text.IndexOf('\n', text.Length - 40000) + 1)..];
                        if (lines % 10 == 0) wpf.Run(() => source.Advance());
                        string next = text;
                        _ = log.Dispatcher.BeginInvoke(new Action(() => log.LogText = next));
                    }
                    await Task.Delay(50);
                }
                probes.Change(Timeout.Infinite, Timeout.Infinite);
                wpf.Run(() => { });
                var values = latency.Order().ToArray();
                return new { seconds = watch.Elapsed.TotalSeconds, samples = values.Length, lines,
                    p50Ms = values[(int)(values.Length * .50)], p95Ms = values[(int)(values.Length * .95)], maxMs = values[^1],
                    cpuCorePercent = (process.TotalProcessorTime - cpu).TotalSeconds / watch.Elapsed.TotalSeconds * 100,
                    notificationsPerSecond = (notifications - before) / watch.Elapsed.TotalSeconds,
                    fullRefreshesPerSecond = (fullRefreshes - fullBefore) / watch.Elapsed.TotalSeconds };
            }
            await Task.Delay(1200);
            var idle = await Sample(false);
            var quietProcess = Process.GetCurrentProcess();
            var quietCpu = quietProcess.TotalProcessorTime; int quietNotifications = notifications, quietFull = fullRefreshes;
            var quietWatch = Stopwatch.StartNew(); await Task.Delay(6000);
            var quietIdle = new { seconds = quietWatch.Elapsed.TotalSeconds,
                cpuCorePercent = (quietProcess.TotalProcessorTime - quietCpu).TotalSeconds / quietWatch.Elapsed.TotalSeconds * 100,
                notificationsPerSecond = (notifications - quietNotifications) / quietWatch.Elapsed.TotalSeconds,
                fullRefreshesPerSecond = (fullRefreshes - quietFull) / quietWatch.Elapsed.TotalSeconds };
            var busy = await Sample(true);
            if (Environment.GetEnvironmentVariable("TIA_PERFORMANCE_ASSERT") == "1")
            {
                var measured = JsonSerializer.SerializeToElement(busy);
                Assert.True(measured.GetProperty("p95Ms").GetDouble() < 50);
                Assert.True(measured.GetProperty("maxMs").GetDouble() < 200);
                Assert.Equal(0, quietIdle.notificationsPerSecond);
                Assert.True(quietIdle.cpuCorePercent < 1);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(new { calls = 5000, auditSeed = 1000, logRate = "20 lines/s, rolling 40 KB; 50 approval requests; independent 100 Hz probes", idle, quietIdle, busy }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { wpf.Run(() => { model.Dispose(); audit.Dispose(); approvals.Dispose(); }); }
    }
    private sealed class FakeApprovals : ObservableObject, IApprovalService
    {
        private readonly ApprovalService _real;
        private readonly List<ApprovalRequest> _rows = [];
        private int _index;
        internal FakeApprovals(ApprovalService real) { _real = real; real.PropertyChanged += (_, e) => Raise(e.PropertyName); }
        public bool Enabled { get => _real.Enabled; set => _real.Enabled = value; }
        public int TimeoutSeconds { get => _real.TimeoutSeconds; set => _real.TimeoutSeconds = value; }
        public int PendingCount => _rows.Count(r => r.State == ApprovalState.Pending);
        public IReadOnlyList<ApprovalRequest> Requests => _rows;
        public event EventHandler<ApprovalRequest>? NewRequest;
        public bool CanDecide(string id) => _rows.Any(r => r.Id == id && r.State == ApprovalState.Pending);
        public bool Approve(string id) => false;
        public bool Deny(string id) => false;
        public void Refresh() => _real.Refresh();
        internal void Seed()
        {
            for (int i = 0; i < 50; i++) _rows.Add(FeaturePageFixtures.Request("pending-" + i) with { Deadline = DateTimeOffset.UtcNow.AddMinutes(5), TimeoutSeconds = 300 });
            Raise(nameof(Requests)); Raise(nameof(PendingCount)); NewRequest?.Invoke(this, _rows[0]);
        }
        internal void Advance()
        {
            int index = _index++ % _rows.Count;
            _rows[index] = _rows[index] with { State = _rows[index].State == ApprovalState.Pending ? ApprovalState.Completed : ApprovalState.Pending };
            Raise(nameof(Requests)); Raise(nameof(PendingCount));
        }
    }

}
