using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaOpenness.Gui.Services;
using Xunit;

namespace TiaOpenness.Gui.Tests;

public sealed class CrashEvidencePortTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Runs_the_three_expected_eventlog_queries_with_a_24_hour_window_and_100_event_limit()
    {
        var source = new FakeSource();
        JsonNode report = JsonNode.Parse(TiaExitEvidenceCollector.Capture(Now, "C:/fake/local", source))!;
        Assert.Equal(3, source.Queries.Count);
        Assert.All(source.Queries, query => { Assert.Equal(Now.AddHours(-24), query.Since); Assert.Equal(100, query.Maximum); });
        Assert.Equal(new[] { "Application", "Application", "System" }, source.Queries.Select(item => item.Query.LogName));
        Assert.Equal(new[] { "1000,1001,1026", "3000", "2004" }, source.Queries.Select(item => string.Join(",", item.Query.EventIds)));
        Assert.Equal("Microsoft-Windows-ProcessExitMonitor", source.Queries[1].Query.Provider);
        Assert.Equal("Microsoft-Windows-Resource-Exhaustion-Detector", source.Queries[2].Query.Provider);
        Assert.Equal(Now.AddHours(-24), report["scope"]!["sinceUtc"]!.GetValue<DateTimeOffset>());
        Assert.Equal(24, report["scope"]!["hours"]!.GetValue<int>());
    }

    [Fact]
    public void Collects_only_matching_events_inside_the_window_and_caps_each_query_at_100()
    {
        var source = new FakeSource
        {
            EventRows = query => Enumerable.Range(0, 130).Select(index => new TiaEvidenceEvent(
                index == 129 ? Now.AddHours(-25) : Now.AddMinutes(-index), query.LogName,
                query.Provider ?? "ApplicationFixture", query.EventIds[0], index, "Synthetic event " + index)).ToArray()
        };
        JsonNode report = JsonNode.Parse(TiaExitEvidenceCollector.Capture(Now, "C:/fake/local", source))!;
        Assert.Equal(3, source.Queries.Count);
        Assert.Equal(300, report["events"]!.AsArray().Count);
        Assert.DoesNotContain(report["events"]!.AsArray(), item => item!["message"]!.GetValue<string>().Contains("Synthetic event 129"));
    }

    [Fact]
    public void Records_portal_and_product_processes_without_starting_or_stopping_them()
    {
        var source = new FakeSource { Processes = new[] { new TiaEvidenceProcess(42, "Siemens.Automation.Portal", Now.AddHours(-2)) } };
        JsonNode report = JsonNode.Parse(TiaExitEvidenceCollector.Capture(Now, "C:/fake/local", source))!;
        Assert.Equal(42, report["processes"]![0]!["pid"]!.GetValue<int>());
        Assert.Equal("Siemens.Automation.Portal", report["processes"]![0]!["name"]!.GetValue<string>());
        Assert.False(report["scope"]!["attachesToTia"]!.GetValue<bool>());
        Assert.False(report["scope"]!["changesDumpPolicy"]!.GetValue<bool>());
    }

    [Fact]
    public void Inventories_recent_product_dump_files_but_never_copies_dump_contents()
    {
        string root = Scratch();
        try
        {
            string dumpRoot = Path.Combine(root, "CrashDumps"); Directory.CreateDirectory(dumpRoot);
            string productDump = Path.Combine(dumpRoot, "TiaMcp.Engine.V20.exe.1.dmp");
            string otherDump = Path.Combine(dumpRoot, "unrelated.exe.1.dmp");
            File.WriteAllText(productDump, "not a real dump"); File.WriteAllText(otherDump, "unrelated");
            File.SetLastWriteTimeUtc(productDump, Now.UtcDateTime.AddHours(-1));
            var source = new FakeSource { DumpRoot = dumpRoot };
            JsonNode report = JsonNode.Parse(TiaExitEvidenceCollector.Capture(Now, root, source))!;
            Assert.Single(report["dumpInventory"]!.AsArray());
            Assert.Equal(productDump, report["dumpInventory"]![0]!["path"]!.GetValue<string>());
            Assert.False(report["scope"]!["dumpContentsCopied"]!.GetValue<bool>());
            Assert.DoesNotContain("not a real dump", report.ToJsonString());
            Assert.True(File.Exists(productDump));
            Assert.DoesNotContain(Directory.GetFiles(root, "*.dmp", SearchOption.AllDirectories),
                path => !path.StartsWith(dumpRoot, StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Missing_dump_directory_is_an_empty_inventory_and_is_not_a_crash_claim()
    {
        var source = new FakeSource();
        JsonNode report = JsonNode.Parse(TiaExitEvidenceCollector.Capture(Now, "C:/missing/local", source))!;
        Assert.Empty(report["dumpInventory"]!.AsArray());
        Assert.True(report["complete"]!.GetValue<bool>());
        Assert.Contains("do not prove absence", report["note"]!.GetValue<string>());
    }

    [Fact]
    public void Event_query_failures_are_visible_and_mark_the_capture_incomplete()
    {
        var source = new FakeSource { ThrowOnEvents = true };
        JsonNode report = JsonNode.Parse(TiaExitEvidenceCollector.Capture(Now, "C:/fake/local", source))!;
        Assert.Equal(3, report["errors"]!.AsArray().Count);
        Assert.False(report["complete"]!.GetValue<bool>());
    }

    [Fact]
    public void Native_export_log_is_added_from_the_temp_directory_as_a_bounded_log_input()
    {
        var input = TiaExitEvidenceCollector.NativeExportLogInput("C:/fake/temp");
        Assert.Equal(Path.Combine("C:/fake/temp", "TiaMcpServer.native-export.log"), input.Path);
        Assert.Equal("logs/hosts/native-export.log", input.Entry);
        Assert.True(input.Tail);
    }

    private static string Scratch()
    {
        string root = Path.Combine(FindRepositoryRoot(), "bin-build", "P6-50", "crash-evidence-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Version.props"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class FakeSource : ITiaExitEvidenceSource
    {
        internal readonly List<(TiaEvidenceQuery Query, DateTimeOffset Since, int Maximum)> Queries = new();
        internal Func<TiaEvidenceQuery, IReadOnlyList<TiaEvidenceEvent>> EventRows = query => new[]
        {
            new TiaEvidenceEvent(Now, query.LogName, query.Provider ?? "ApplicationFixture", query.EventIds[0], 7, "Synthetic local evidence")
        };
        internal IReadOnlyList<TiaEvidenceProcess> Processes = Array.Empty<TiaEvidenceProcess>();
        internal string? DumpRoot;
        internal bool ThrowOnEvents;

        public IReadOnlyList<TiaEvidenceEvent> ReadEvents(TiaEvidenceQuery query, DateTimeOffset sinceUtc, int maximum)
        {
            Queries.Add((query, sinceUtc, maximum));
            if (ThrowOnEvents) throw new InvalidOperationException("fake event query failure");
            return EventRows(query);
        }
        public IReadOnlyList<TiaEvidenceProcess> ReadProcesses() => Processes;
        public IReadOnlyList<TiaEvidenceDump> ReadDumps(string dumpDirectory, DateTimeOffset sinceUtc, int maximum)
        {
            string selected = DumpRoot ?? dumpDirectory;
            if (!Directory.Exists(selected)) return Array.Empty<TiaEvidenceDump>();
            return Directory.GetFiles(selected, "*.dmp").Where(path => Path.GetFileName(path).StartsWith("TiaMcp.Engine.V20.", StringComparison.OrdinalIgnoreCase))
                .Select(path => new TiaEvidenceDump(path, new FileInfo(path).Length, new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)))
                .Where(item => item.ModifiedUtc >= sinceUtc).Take(maximum).ToArray();
        }
    }
}
