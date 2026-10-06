using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using TiaOpenness.Shared;

namespace TiaOpenness.Gui.Services;

internal sealed record TiaEvidenceQuery(string LogName, string? Provider, int[] EventIds);
internal sealed record TiaEvidenceEvent(DateTimeOffset TimeUtc, string Log, string Provider, int EventId, long? RecordId, string Message);
internal sealed record TiaEvidenceProcess(int ProcessId, string Name, DateTimeOffset StartUtc);
internal sealed record TiaEvidenceDump(string Path, long Bytes, DateTimeOffset ModifiedUtc);

internal interface ITiaExitEvidenceSource
{
    IReadOnlyList<TiaEvidenceEvent> ReadEvents(TiaEvidenceQuery query, DateTimeOffset sinceUtc, int maximum);
    IReadOnlyList<TiaEvidenceProcess> ReadProcesses();
    IReadOnlyList<TiaEvidenceDump> ReadDumps(string dumpDirectory, DateTimeOffset sinceUtc, int maximum);
}

internal static class TiaExitEvidenceCollector
{
    internal static readonly TiaEvidenceQuery[] Queries =
    {
        new("Application", null, new[] { 1000, 1001, 1026 }),
        new("Application", "Microsoft-Windows-ProcessExitMonitor", new[] { 3000 }),
        new("System", "Microsoft-Windows-Resource-Exhaustion-Detector", new[] { 2004 }),
    };

    internal static string Capture() => Capture(DateTimeOffset.UtcNow, DataLocations.Current.LocalApplicationDataDirectory, new WindowsEventLogSource());

    internal static string Capture(DateTimeOffset nowUtc, string localAppData, ITiaExitEvidenceSource source)
    {
        DateTimeOffset captured = nowUtc.ToUniversalTime();
        DateTimeOffset since = captured.AddHours(-24);
        var errors = new List<string>();
        var events = new List<TiaEvidenceEvent>();
        foreach (var query in Queries)
        {
            try
            {
                events.AddRange(source.ReadEvents(query, since, 100)
                    .Where(item => item.TimeUtc >= since && item.TimeUtc <= captured && query.EventIds.Contains(item.EventId)
                        && (query.Provider == null || string.Equals(query.Provider, item.Provider, StringComparison.OrdinalIgnoreCase)))
                    .Take(100));
            }
            catch (Exception ex) { errors.Add("Event query: " + ex.Message); }
        }

        IReadOnlyList<TiaEvidenceProcess> processes;
        try { processes = source.ReadProcesses(); }
        catch (Exception ex) { processes = Array.Empty<TiaEvidenceProcess>(); errors.Add("Processes: " + ex.Message); }

        IReadOnlyList<TiaEvidenceDump> dumps;
        try { dumps = source.ReadDumps(Path.Combine(localAppData, "CrashDumps"), since, 50); }
        catch (Exception ex) { dumps = Array.Empty<TiaEvidenceDump>(); errors.Add("Dump inventory: " + ex.Message); }

        var report = new
        {
            schemaVersion = 1,
            capturedUtc = captured,
            scope = new { sinceUtc = since, hours = 24, maxEventsPerQuery = 100, dumpContentsCopied = false,
                attachesToTia = false, changesDumpPolicy = false },
            events = events.Select(item => new { utc = item.TimeUtc, log = item.Log, provider = item.Provider,
                eventId = item.EventId, recordId = item.RecordId, message = item.Message }),
            processes = processes.Select(item => new { pid = item.ProcessId, name = item.Name, startUtc = item.StartUtc }),
            dumpInventory = dumps.Select(item => new { path = item.Path, bytes = item.Bytes, modifiedUtc = item.ModifiedUtc }),
            errors,
            complete = errors.Count == 0,
            note = "Local evidence only; missing events or dump files do not prove absence of a crash. Events and paths may contain private information. No dump content or project was copied."
        };
        return JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    }

    internal static DiagnosticInput NativeExportLogInput(string tempDirectory)
        => new("host-log", Path.Combine(tempDirectory, "TiaMcpServer.native-export.log"), "logs/hosts/native-export.log", true);

    private sealed class WindowsEventLogSource : ITiaExitEvidenceSource
    {
        private const string ProductDumpName = @"^(Siemens\.Automation\.Portal|TiaMcp\.Engine\.V(?:20|21)|TiaMcp\.FoundationHost)\.";
        private static readonly string[] ProductProcesses =
            { "Siemens.Automation.Portal", "TiaMcp.Engine.V20", "TiaMcp.Engine.V21", "TiaMcp.FoundationHost" };

        public IReadOnlyList<TiaEvidenceEvent> ReadEvents(TiaEvidenceQuery query, DateTimeOffset sinceUtc, int maximum)
        {
            long ageMilliseconds = Math.Max(0, (long)(DateTimeOffset.UtcNow - sinceUtc).TotalMilliseconds);
            string ids = string.Join(" or ", query.EventIds.Select(id => "EventID=" + id));
            string system = query.Provider == null
                ? "(" + ids + ")"
                : "Provider[@Name='" + query.Provider + "'] and (" + ids + ")";
            string xpath = "*[System[" + system + " and TimeCreated[timediff(@SystemTime) <= " + ageMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]]]";
            var eventQuery = new EventLogQuery(query.LogName, PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(eventQuery);
            var rows = new List<TiaEvidenceEvent>();
            for (int count = 0; count < maximum; count++)
            {
                using EventRecord? record = reader.ReadEvent();
                if (record == null) break;
                var time = record.TimeCreated;
                if (time == null) continue;
                string message;
                try { message = record.FormatDescription() ?? ""; }
                catch (EventLogException ex) { message = "Event message unavailable: " + ex.Message; }
                rows.Add(new(new DateTimeOffset(time.Value.ToUniversalTime()), record.LogName ?? query.LogName,
                    record.ProviderName ?? "", record.Id, record.RecordId, message));
            }
            return rows;
        }

        public IReadOnlyList<TiaEvidenceProcess> ReadProcesses()
        {
            var rows = new List<TiaEvidenceProcess>();
            foreach (string name in ProductProcesses)
            {
                foreach (Process process in Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        try { rows.Add(new(process.Id, process.ProcessName, new DateTimeOffset(process.StartTime.ToUniversalTime()))); }
                        catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException)
                        { Trace.TraceWarning("TIA exit evidence process metadata unavailable: {0}", ex.Message); }
                    }
                }
            }
            return rows;
        }

        public IReadOnlyList<TiaEvidenceDump> ReadDumps(string dumpDirectory, DateTimeOffset sinceUtc, int maximum)
        {
            if (!Directory.Exists(dumpDirectory)) return Array.Empty<TiaEvidenceDump>();
            return new DirectoryInfo(dumpDirectory).EnumerateFiles("*.dmp", SearchOption.TopDirectoryOnly)
                .Where(file => file.LastWriteTimeUtc >= sinceUtc.UtcDateTime
                    && Regex.IsMatch(file.Name, ProductDumpName, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .Take(maximum)
                .Select(file => new TiaEvidenceDump(file.FullName, file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)))
                .ToArray();
        }
    }
}
