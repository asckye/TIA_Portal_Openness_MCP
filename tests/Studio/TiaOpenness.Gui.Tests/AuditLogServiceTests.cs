using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Linq;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Shared;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class AuditLogServiceTests(WpfContext wpf)
{
    [Fact]
    public void Host_approval_events_and_workbench_switch_share_the_visible_audit_chain()
    {
        wpf.Run(() =>
        {
            string root = Path.GetFullPath(Path.Combine("bin-build", "P6-44", "round2", "audit", Guid.NewGuid().ToString("N")));
            string directory = Path.Combine(root, "logs", "audit");
            var host = new AuditLog(directory); var workbench = new AuditLog(directory);
            host.Append("request", "r", "engine", "21", "WriteFixture");
            host.Approval("r", "engine", "21", "WriteFixture", "granted", new string('a', 64));
            host.Approval("r2", "foundation", "19", "WriteFixture", "denied");
            host.Approval("r3", "engine", "20", "WriteFixture", "timeout");
            new ApprovalSettings(false).Save(Path.Combine(root, "config", "approval.settings"), workbench);
            using var view = new AuditLogService(directory, Path.Combine(root, "diagnostics"), Path.Combine(root, "config", "retention"), _ => { });
            WpfContext.Complete(view.RefreshAsync());
            Assert.Equal(new[] { AuditEventType.Request, AuditEventType.Approve, AuditEventType.Reject, AuditEventType.Timeout, AuditEventType.Toggle },
                view.Events.Select(row => row.Type));
            using var cancellation = new System.Threading.CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() => view.Verify(_ => cancellation.Cancel(), cancellation.Token));
            var report = view.Verify(); Assert.True(report.Passed); Assert.Single(report.Chains); Assert.Equal(5, report.Count);
            Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, host.Read().Select(row => row.Index));
        });
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Verification_reports_records_and_stops_between_them(int directoryCount)
    {
        string root = Path.GetFullPath(Path.Combine("bin-build", "audit-progress", Guid.NewGuid().ToString("N")));
        string[] directories = Enumerable.Range(0, directoryCount).Select(i => Path.Combine(root, "chain-" + i)).ToArray();
        try
        {
            const int count = 40;
            foreach (string path in directories)
            {
                var chain = new AuditLog(path, 2700);
                for (int i = 0; i < count; i++) chain.Append("request", "r" + i, "engine", "21", "WriteFixture");
            }
            var log = new AuditLog(directories[0]);
            var fractions = new List<double>();
            using var stop = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() => log.Verify(fraction =>
            {
                fractions.Add(fraction);
                if (fractions.Count == 11) stop.Cancel();
            }, stop.Token));
            Assert.Equal(11, fractions.Count);
            Assert.True(fractions[0] > 0 && fractions[^1] < 1);
            Assert.Equal(fractions.Order(), fractions);

            using var service = new AuditLogService(directories[0], root, Path.Combine(root, "settings"), chains: directories);
            using var stopService = new CancellationTokenSource();
            int last = 0;
            Assert.Throws<OperationCanceledException>(() => service.Verify(percent =>
            {
                last = percent;
                if (percent >= 25) stopService.Cancel();
            }, stopService.Token));
            Assert.InRange(last, 25, 49);
            var percentages = new List<int>();
            var complete = service.Verify(percentages.Add);
            Assert.True(complete.Passed);
            Assert.Equal(directoryCount * count, complete.Count);
            Assert.Equal(directories, complete.Chains.Select(chain => chain.Chain));
            Assert.All(complete.Chains, chain => { Assert.True(chain.Passed); Assert.Equal(count, chain.Count); Assert.Null(chain.BreakIndex); });
            Assert.Equal(0, percentages[0]); Assert.Equal(100, percentages[^1]);
            Assert.Equal(percentages.Distinct().Order(), percentages);
            Assert.Contains(percentages, percent => percent is > 0 and < 50);
            Assert.Contains(percentages, percent => percent is > 50 and < 100);
            Assert.Equal(service.Verify().Count, complete.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Real_service_reads_verifies_persists_opens_and_notifies()
    {
        wpf.Run(() =>
        {
            string root = Path.GetFullPath(Path.Combine("bin-build", "P6-46", "gui-fixtures", Guid.NewGuid().ToString("N")));
            string directory = Path.Combine(root, "logs", "audit"), calls = Path.Combine(root, "diagnostics"), settings = Path.Combine(root, "config", "journal-retention.settings");
            var log = new AuditLog(directory);
            log.Append("request", "r", "engine", "21", "WriteFixture");
            log.Append("start", "r", "engine", "21", "WriteFixture");
            log.Append("end", "r", "engine", "21", "WriteFixture", "partial");
            string? opened = null;
            using var service = new AuditLogService(directory, calls, settings, path => opened = path);
            WpfContext.Complete(service.RefreshAsync());
            Assert.Equal(3, service.TotalCount); Assert.True(service.Verify().Passed); Assert.Equal(AuditEventType.End, service.Events.Last().Type);
            int notifications = 0; service.PropertyChanged += (_, _) => notifications++;
            service.FileSizeMb = 50; service.Copies = 3;
            using var reopened = new AuditLogService(directory, calls, settings, _ => { });
            WpfContext.Complete(reopened.RefreshAsync());
            Assert.Equal(50, reopened.FileSizeMb); Assert.Equal(3, reopened.Copies); Assert.True(notifications > 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => service.Copies = 0);
            service.OpenFolder(); Assert.Equal(directory, opened);
            string file = Directory.GetFiles(directory, "audit-*.jsonl").Single();
            File.WriteAllText(file, File.ReadAllText(file).Replace("WriteFixture", "Changed", StringComparison.Ordinal));
            Assert.False(service.Verify().Passed); Assert.Equal(2, service.Verify().BreakIndex);
            service.Dispose(); log.Append("request", "after", "engine", "21", "WriteFixture");
            WpfContext.Complete(service.RefreshAsync()); Assert.Equal(3, service.TotalCount);
        });
    }
    [Theory]
    [InlineData(AppLanguage.Chinese, "部分完成", "无保留的调用记录")]
    [InlineData(AppLanguage.English, "Partial", "No retained call records")]
    public void Audit_outcomes_and_actual_coverage_are_localized(AppLanguage language, string partial, string empty)
    {
        wpf.RunWithLanguage(language, () =>
        {
            string root = Path.GetFullPath(Path.Combine("bin-build", "P6-46", "gui-fixtures", Guid.NewGuid().ToString("N")));
            using var service = new AuditLogService(Path.Combine(root, "audit"), Path.Combine(root, "diagnostics"), Path.Combine(root, "config", "retention"), _ => { });
            WpfContext.Complete(service.RefreshAsync());
            Assert.Equal(empty, service.Coverage.Resolve());
            var row = new AuditRow(new AuditEvent(1, DateTimeOffset.UtcNow, AuditEventType.End, "WriteFixture", "partial", "abc"), false);
            Assert.Equal(partial, row.Result);
            var legacy = row with { PreviousHash = "visible-predecessor" };
            Assert.Equal(legacy.PreviousHash, legacy.RecordedLink);
            Assert.False(legacy.HasMismatchedLink);
            Directory.CreateDirectory(Path.Combine(root, "diagnostics"));
            File.WriteAllText(Path.Combine(root, "diagnostics", "calls-test.jsonl"), "{\"utc\":\"2026-10-05T08:00:00.0000000+00:00\"}\n{\"utc\":\"2026-10-05T16:00:00.0000000+00:00\"}\n");
            string active = Path.Combine(root, "diagnostics", "calls-test.jsonl");
            File.Copy(active, active + ".1");
            File.Copy(active, Path.Combine(root, "diagnostics", "calls-other.jsonl"));
            File.WriteAllText(active + ".lock", "excluded");
            WpfContext.Complete(service.RefreshAsync());
            Assert.Equal((new FileInfo(active).Length * 3, 3, 2), service.Usage);
            Assert.DoesNotContain(empty, service.Coverage.Resolve()); Assert.Contains("2026-10-05", service.Coverage.Resolve());
        });
    }
}
