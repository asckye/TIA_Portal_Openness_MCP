using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.Settings;
using TiaOpenness.Gui.Themes;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Shared;
using Xunit;
using static TiaOpenness.Gui.Tests.WorkbenchRenderFeaturePagesTests;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class ResponsivenessTests(WpfContext wpf)
{
    private static string Scratch() => Path.GetFullPath(Path.Combine("bin-build", "P6-57", Guid.NewGuid().ToString("N")));
    private static FeaturePagesViewModel Model(FeaturePageFixtures.Journal journal)
        => new(FeaturePageFixtures.Approvals(), journal, new FeaturePageFixtures.Audit(), new FeaturePageFixtures.Environment(),
            new FeaturePageFixtures.Diagnostics(), () => FeaturePageFixtures.Now);

    [Fact]
    public void Idle_services_do_not_notify_and_repeated_reads_keep_rows_and_redacted_json()
    {
        wpf.Run(() =>
        {
            string root = Scratch(); Directory.CreateDirectory(root);
            using var approvals = new ApprovalService(Path.Combine(root, "approval"), "idle-" + Guid.NewGuid().ToString("N"));
            int notifications = 0; approvals.PropertyChanged += (_, _) => notifications++;
            for (int i = 0; i < 100; i++) approvals.Refresh();
            Assert.Equal(0, notifications);
            using var model = Model(new FeaturePageFixtures.Journal());
            var calls = model.Calls; var row = calls[0]; string json = row.Parameters;
            Assert.Same(calls, model.Calls); Assert.Same(row, model.Calls[0]); Assert.Same(json, row.Parameters);
            string parameters = model.Requests[0].Parameters; Assert.Same(parameters, model.Requests[0].Parameters);
        });
    }

    [Fact]
    public void Bursts_coalesce_and_hidden_pages_do_not_project_calls()
    {
        wpf.Run(() =>
        {
            var journal = new FeaturePageFixtures.Journal(); var approvals = FeaturePageFixtures.Approvals();
            using var model = new FeaturePagesViewModel(approvals, journal, new FeaturePageFixtures.Audit(),
                new FeaturePageFixtures.Environment(), new FeaturePageFixtures.Diagnostics(), () => FeaturePageFixtures.Now);
            int full = 0; model.PropertyChanged += (_, e) => { if (string.IsNullOrEmpty(e.PropertyName)) full++; };
            var original = model.Calls.Single(c => c.Tool == "GetProjectInfo");
            for (int i = 0; i < 20; i++) journal.Replace(journal.Calls.Select(c => c.Tool == "GetProjectInfo" ? c with { DurationMs = i } : c).ToArray());
            WpfContext.Drain();
            Assert.Equal(1, model.RefreshCount); Assert.Equal(0, full);
            Assert.Same(original, model.Calls.Single(c => c.Tool == "GetProjectInfo")); Assert.Equal("19 ms", original.Duration);
            model.OpenCall(original); model.SetVisibility(false, false, false);
            journal.Replace([]); approvals.Receive(FeaturePageFixtures.Request("hidden-request"));
            WpfContext.Drain(); Assert.Contains(original, model.Calls);
            Assert.DoesNotContain(model.Calls, row => row.Record.RequestId == "hidden-request");
            model.SetVisibility(true, false, false); Assert.DoesNotContain(original, model.Calls);
        });
    }

    [Fact]
    public void Virtualization_limits_realized_rows_and_bursts_preserve_scrolled_position()
    {
        wpf.Run(() =>
        {
            var journal = new FeaturePageFixtures.Journal();
            journal.Replace(Enumerable.Range(0, 5000).Select(i => journal.Calls[0] with
                { RequestId = "row-" + i, JournalKey = "row-" + i, Time = FeaturePageFixtures.Now.AddMilliseconds(-i) }).ToArray());
            using var model = Model(journal);
            var view = new Views.AiCallsView { DataContext = model };
            var host = new Border { Child = view, Width = 1200, Height = 780 }; Layout(host);
            var list = (ListBox)view.FindName("CallList");
            Assert.True(VirtualizingPanel.GetIsVirtualizing(list));
            Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(list));
            Assert.Contains(Descendants<VirtualizingStackPanel>(view), _ => true);
            Assert.InRange(Descendants<ListBoxItem>(list).Count(), 1, 100);
            var scroll = Descendants<ScrollViewer>(list).Single(); scroll.ScrollToVerticalOffset(1500); Layout(host);
            double offset = scroll.VerticalOffset; Assert.True(offset > 0);
            var rows = model.Calls.ToArray();
            journal.Replace(journal.Calls.Select((c, i) => i == 40 ? c with { DurationMs = 44 } : c).ToArray()); WpfContext.Drain(); Layout(host);
            Assert.Equal(offset, scroll.VerticalOffset, 1); Assert.Same(rows[200], model.Calls[200]);
            foreach (FrameworkElement panel in new FrameworkElement[] { new Views.AuditLogView(), new Views.ApprovalsDrawer(), new Views.LogView() })
            {
                var control = (ItemsControl)panel.FindName(panel is Views.AuditLogView ? "AuditList" : panel is Views.ApprovalsDrawer ? "RequestList" : "LogList");
                Assert.True(VirtualizingPanel.GetIsVirtualizing(control)); Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(control));
            }
        });
    }

    [Fact]
    public async Task Continuous_changes_are_throttled_without_starving_the_reader_and_inactive_readers_sleep()
    {
        string root = Scratch(); Directory.CreateDirectory(root); int work = 0;
        using var monitor = new ChangeMonitor(() => System.Threading.Interlocked.Increment(ref work), root);
        for (int i = 0; i < 30; i++) { monitor.Request(); await Task.Delay(20); }
        Assert.InRange(work, 3, 15);
        monitor.SetActive(false); int stopped = work;
        for (int i = 0; i < 10; i++) monitor.Request();
        await Task.Delay(200); Assert.Equal(stopped, work);
        monitor.SetActive(true);
        for (int i = 0; i < 100 && work == stopped; i++) await Task.Delay(10);
        Assert.True(work > stopped);
    }

    [Theory]
    [InlineData(LowEffectsMode.Auto, 0, false, false, true)]
    [InlineData(LowEffectsMode.Auto, 2, true, false, true)]
    [InlineData(LowEffectsMode.Auto, 2, false, true, true)]
    [InlineData(LowEffectsMode.Auto, 2, false, false, false)]
    [InlineData(LowEffectsMode.On, 2, false, false, true)]
    [InlineData(LowEffectsMode.Off, 0, true, true, false)]
    public void Low_effects_resolves_hardware_remote_and_vm_hints(LowEffectsMode mode, int tier, bool remote, bool vm, bool expected)
        => Assert.Equal(expected, ThemeManager.ResolveLowEffects(mode, tier, remote, vm));

    [Fact]
    public void Compatibility_effects_setting_round_trips_and_Primer_has_no_effects_in_either_mode()
    {
        wpf.Run(() =>
        {
            var manager = ThemeManager.Current; var old = manager.LowEffects;
            try
            {
                manager.LowEffects = LowEffectsMode.Off;
                var card = new Border { Style = (Style)Application.Current.FindResource("Primer.Container") };
                var host = new Window { Content = card };
                Assert.Null(card.Effect);
                manager.LowEffects = LowEffectsMode.On; WpfContext.Drain(); Assert.Null(card.Effect);
                Assert.Equal(System.Windows.Controls.Primitives.PopupAnimation.None, Application.Current.FindResource("Primer.PopupAnimation"));
                manager.LowEffects = LowEffectsMode.Off; WpfContext.Drain(); Assert.Null(card.Effect); host.Close();
                string path = Path.Combine(Scratch(), "ui"); new UiSettings { LowEffects = LowEffectsMode.On }.Save(path);
                Assert.Equal(LowEffectsMode.On, UiSettings.Load(path).LowEffects);
            }
            finally { manager.LowEffects = old; }
        });
    }

    [Fact]
    public void Engine_log_appends_reuse_the_document_and_trim_only_expired_rows()
    {
        wpf.Run(() =>
        {
            var view = new WorkbenchLogView { LogText = "12:34:56  first\n12:34:57  second\n" }; WpfContext.Drain();
            var table = (Table)view.Document.Blocks.Single(); var row = table.RowGroups[0].Rows[1];
            view.LogText += "12:34:58  third\n"; WpfContext.Drain();
            Assert.Same(table, view.Document.Blocks.Single()); Assert.Same(row, table.RowGroups[0].Rows[1]);
            view.LogText = "12:34:57  second\n12:34:58  third\n12:34:59  fourth\n"; WpfContext.Drain();
            Assert.Same(table, view.Document.Blocks.Single()); Assert.Same(row, table.RowGroups[0].Rows[0]); Assert.Equal(3, table.RowGroups[0].Rows.Count);
        });
    }

    [Fact]
    public void Activity_log_is_bounded_incremental_and_notifications_are_batched()
    {
        wpf.Run(() =>
        {
            using var main = new MainViewModel(new FakeStudioClient(), new FakeDialogService());
            using var results = new Controls.ResultPresentation(main); int notifications = 0;
            main.Activity.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(WorkbenchActivity.Log)) notifications++; };
            var rows = results.LogRows;
            for (int i = 0; i < 2000; i++) main.Activity.Append("entry-" + i);
            WpfContext.Drain(); Assert.Equal(1, notifications); Assert.InRange(main.Activity.Entries.Count, 1, WorkbenchActivity.MaximumEntries);
            Assert.InRange(main.Activity.Log.Length, 1, 40000); Assert.Contains("entry-1999", main.Activity.Log);
            Assert.Same(rows, results.LogRows); Assert.True(results.LogRows.Count <= WorkbenchActivity.MaximumEntries);
        });
    }

    [Fact]
    public async Task Large_audit_history_loads_off_thread_with_bounded_pages_and_incremental_tail()
    {
        string root = Scratch(), directory = Path.Combine(root, "audit"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "audit-00000000000000000001.jsonl");
        // Valid display records; chain integrity is covered by the real AuditLog tests.
        using (var stream = new StreamWriter(path, false, new UTF8Encoding(false)))
            for (int i = 1; i <= 150000; i++) stream.WriteLine(AuditLog.Canonical(new AuditRecord { Index = i, Utc = DateTimeOffset.UtcNow.ToString("O"),
                Event = "end", Tool = "fixture", ProcessId = 1, PreviousHash = AuditLog.Genesis }));
        AuditLogService service = null!; int uiThread = 0; var loadWatch = Stopwatch.StartNew();
        wpf.Run(() => { uiThread = Environment.CurrentManagedThreadId; service = new AuditLogService(directory, Path.Combine(root, "diagnostics"), Path.Combine(root, "config", "retention"), _ => { }); });
        try
        {
            await service.RefreshAsync(); Assert.NotEqual(uiThread, service.ReadThreadId);
            Assert.Equal(150000, service.TotalCount); Assert.Equal(AuditLogService.PageSize, service.Events.Count);
            Assert.Equal(149001, service.Events[0].Index);
            double loadMs = loadWatch.Elapsed.TotalMilliseconds;
            long bytes = service.BytesRead; int notifications = 0; service.PropertyChanged += (_, _) => notifications++;
            await service.RefreshAsync(); Assert.Equal(bytes, service.BytesRead); Assert.Equal(0, notifications);
            File.AppendAllText(path, AuditLog.Canonical(new AuditRecord { Index = 150001, Utc = DateTimeOffset.UtcNow.ToString("O"),
                Event = "end", Tool = "tail", ProcessId = 1, PreviousHash = AuditLog.Genesis }) + "\n");
            await service.RefreshAsync(); Assert.Equal(150001, service.TotalCount); Assert.InRange(service.BytesRead - bytes, 1, 1024);
            long tailBytes = service.BytesRead - bytes;
            await service.OlderAsync(); Assert.Equal(149001, service.Events[^1].Index); Assert.Equal(AuditLogService.PageSize, service.Events.Count);
            await service.LatestAsync(); Assert.Equal(150001, service.Events[^1].Index);
            File.WriteAllText(Path.Combine(Path.GetFullPath("bin-build/P6-57"), "large-history.json"), JsonSerializer.Serialize(new
                { rows = 150000, displayed = AuditLogService.PageSize, loadMs, initialBytes = bytes, tailBytes, uiThread, readerThread = service.ReadThreadId }));
        }
        finally { service.Dispose(); }
    }

    [Fact]
    public async Task Contended_audit_and_retention_locks_do_not_block_dispatcher_actions()
    {
        string root = Scratch(), directory = Path.Combine(root, "audit"), retention = Path.Combine(root, "config", "retention");
        var log = new AuditLog(directory); log.Append("end", "seed", "fixture", "21", "tool", "succeeded");
        using var service = new AuditLogService(directory, Path.Combine(root, "diagnostics"), retention, _ => { }); await service.RefreshAsync();
        using var lease = JournalFileLock.Acquire(Path.Combine(directory, ".audit.lock"));
        FeaturePagesViewModel model = null!;
        wpf.Run(() => model = new FeaturePagesViewModel(FeaturePageFixtures.Approvals(), new FeaturePageFixtures.Journal(), service,
            new FeaturePageFixtures.Environment(), new FeaturePageFixtures.Diagnostics()));
        try
        {
            Task verify = null!; wpf.Run(() => { verify = model.VerifyAsync(); });
            Assert.False(verify.IsCompleted); bool responsive = false; wpf.Run(() => responsive = true); Assert.True(responsive);
            lease.Dispose(); await verify.WaitAsync(TimeSpan.FromSeconds(5));
            using var settingLock = JournalFileLock.Acquire(retention + ".lock");
            Task save = null!; wpf.Run(() => { save = model.SetRetentionAsync(128, true); });
            wpf.Run(() => { }); Assert.False(save.IsCompleted); settingLock.Dispose(); await save.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(128, service.FileSizeMb);
        }
        finally { wpf.Run(() => model.Dispose()); }
    }
}
