using System;
using System.IO;
using System.Linq;
using System.Text;
using TiaOpenness.Shared;
using Xunit;

namespace TiaOpenness.Core.Tests;

public sealed class AuditRetentionTests
{
    private static string Fresh() => Path.GetFullPath(Path.Combine("bin-build", "P6-46", "core-fixtures", Guid.NewGuid().ToString("N")));
    [Fact]
    public void Settings_round_trip_fallback_and_bounds()
    {
        string path = Path.Combine(Fresh(), "config", "journal-retention.settings");
        Assert.Equal(50, JournalRetention.Load(path).FileSizeMb); Assert.Equal(16, JournalRetention.Load(path).Copies);
        new JournalRetention(1, 3).Save(path);
        Assert.Equal(1, JournalRetention.Load(path).FileSizeMb); Assert.Equal(3, JournalRetention.Load(path).Copies);
        File.WriteAllText(path, "fileSizeMb=bogus\ncopies=-1\n");
        Assert.Equal(50, JournalRetention.Load(path).FileSizeMb); Assert.Equal(16, JournalRetention.Load(path).Copies);
        Assert.Throws<ArgumentOutOfRangeException>(() => new JournalRetention(0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JournalRetention(1025, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JournalRetention(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JournalRetention(1, 257));
    }
    [Fact]
    public void Rotation_keeps_configured_total_and_shrinking_prunes_copies()
    {
        string directory = Fresh(); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "calls-fixture.jsonl");
        for (int i = 0; i < 6; i++)
        {
            using (var stream = new FileStream(path, FileMode.OpenOrCreate)) stream.SetLength(1024 * 1024);
            new JournalRetention(1, 3).Rotate(path, 100);
            File.WriteAllText(path, "row" + i);
        }
        Assert.Equal(3, Directory.GetFiles(directory, "calls-*").Length);
        new JournalRetention(1, 1).Rotate(path, 1);
        Assert.Single(Directory.GetFiles(directory, "calls-*"));
        Assert.Equal("row5", File.ReadAllText(path));
    }
    [Fact]
    public void Coverage_uses_retained_rows_across_processes_copies_and_interrupted_tail()
    {
        string directory = Fresh(); Directory.CreateDirectory(directory);
        var start = DateTimeOffset.Parse("2026-10-05T08:00:00+00:00");
        var end = start.AddHours(8);
        string Row(DateTimeOffset time) => "{\"utc\":\"" + time.ToString("O") + "\",\"phase\":\"BEFORE\"}\n";
        File.WriteAllText(Path.Combine(directory, "calls-one.jsonl.1"), Row(start) + Row(start.AddHours(2)), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "calls-two.jsonl"), Row(start.AddHours(4)) + Row(end) + "{\"utc\":\"2099", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "calls-one.jsonl.lock"), Row(start.AddDays(-10)), new UTF8Encoding(false));
        var coverage = JournalRetention.Coverage(directory);
        Assert.Equal(start, coverage.Start); Assert.Equal(end, coverage.End);
        Assert.Null(JournalRetention.Coverage(Fresh()).Start);
    }
    [Fact]
    public void Interrupted_audit_tail_is_reported_and_append_does_not_silently_repair_it()
    {
        string directory = Fresh(); var log = new AuditLog(directory);
        log.Append("request", "r", "test", "20", "WriteFixture");
        string path = Directory.GetFiles(directory, "audit-*.jsonl").Single();
        File.AppendAllText(path, "{interrupted");
        Assert.False(log.Verify().Passed); Assert.Equal(2, log.Verify().BreakIndex);
        Assert.Throws<InvalidDataException>(() => log.Append("start", "r", "test", "20", "WriteFixture"));
    }
    [Fact]
    public void Missing_rotated_middle_file_reports_expected_index()
    {
        string directory = Fresh(); var log = new AuditLog(directory, 1);
        for (int i = 0; i < 4; i++) log.Append("request", "r" + i, "test", "21", "WriteFixture");
        File.Delete(Directory.GetFiles(directory, "audit-*.jsonl").OrderBy(p => p, StringComparer.Ordinal).ElementAt(1));
        Assert.False(log.Verify().Passed); Assert.Equal(2, log.Verify().BreakIndex);
    }
}
