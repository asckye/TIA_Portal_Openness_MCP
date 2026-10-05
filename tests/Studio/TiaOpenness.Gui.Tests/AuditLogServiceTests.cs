using System;
using System.IO;
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
            Assert.Equal(3, service.TotalCount); Assert.True(service.Verify().Passed); Assert.Equal(AuditEventType.End, service.Events.Last().Type);
            int notifications = 0; service.PropertyChanged += (_, _) => notifications++;
            service.FileSizeMb = 50; service.Copies = 3;
            using var reopened = new AuditLogService(directory, calls, settings, _ => { });
            Assert.Equal(50, reopened.FileSizeMb); Assert.Equal(3, reopened.Copies); Assert.True(notifications > 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => service.Copies = 0);
            service.OpenFolder(); Assert.Equal(directory, opened);
            string file = Directory.GetFiles(directory, "audit-*.jsonl").Single();
            File.WriteAllText(file, File.ReadAllText(file).Replace("WriteFixture", "Changed", StringComparison.Ordinal));
            Assert.False(service.Verify().Passed); Assert.Equal(2, service.Verify().BreakIndex);
            service.Dispose(); log.Append("request", "after", "engine", "21", "WriteFixture");
            service.Refresh(); Assert.Equal(3, service.TotalCount);
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
            Assert.Equal(empty, service.Coverage.Resolve());
            var row = new AuditRow(new AuditEvent(1, DateTimeOffset.UtcNow, AuditEventType.End, "WriteFixture", "partial", "abc"), false);
            Assert.Equal(partial, row.Result);
            Directory.CreateDirectory(Path.Combine(root, "diagnostics"));
            File.WriteAllText(Path.Combine(root, "diagnostics", "calls-test.jsonl"), "{\"utc\":\"2026-10-05T08:00:00.0000000+00:00\"}\n{\"utc\":\"2026-10-05T16:00:00.0000000+00:00\"}\n");
            service.Refresh(); Assert.DoesNotContain(empty, service.Coverage.Resolve()); Assert.Contains("2026-10-05", service.Coverage.Resolve());
        });
    }
}
