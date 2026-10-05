using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Settings;
using TiaOpenness.Shared;

namespace TiaOpenness.Gui.Services;

public sealed class AuditLogService : ObservableObject, IAuditLogService, IDisposable
{
    private readonly string _directory, _diagnostics, _settingsPath;
    private readonly Action<string> _open;
    private readonly DispatcherTimer _timer;
    private IReadOnlyList<AuditEvent> _events = Array.Empty<AuditEvent>();
    private JournalCoverage _coverage = new(null, null);
    private JournalRetention _retention;
    private bool _disposed, _refreshing;
    private string _stamp = "";

    public AuditLogService(string? directory = null, string? diagnostics = null, string? settingsPath = null, Action<string>? open = null)
    {
        _directory = directory ?? DataLocations.Current.AuditDirectory;
        _diagnostics = diagnostics ?? DataLocations.Current.DiagnosticsDirectory;
        _settingsPath = settingsPath ?? JournalRetention.SettingsPath;
        _open = open ?? (path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));
        _retention = UiSettings.LoadRetention(_settingsPath);
        Refresh();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnTick, Dispatcher.CurrentDispatcher);
    }
    public IReadOnlyList<AuditEvent> Events => _events;
    public int TotalCount => _events.Count;
    public int FileSizeMb { get => _retention.FileSizeMb; set => SaveRetention(value, Copies); }
    public int Copies { get => _retention.Copies; set => SaveRetention(FileSizeMb, value); }
    public LocalizedText Coverage => _coverage.Start.HasValue && _coverage.End.HasValue
        ? LocalizedText.Key("Audit.Window", _coverage.Start.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), _coverage.End.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
        : LocalizedText.Key("Audit.NoCoverage");

    private void SaveRetention(int size, int copies)
    {
        UiSettings.SaveRetention(_settingsPath, size, copies);
        _retention = UiSettings.LoadRetention(_settingsPath); Raise(null);
    }
    public AuditVerification Verify()
    {
        var report = new AuditLog(_directory).Verify();
        Refresh();
        return new AuditVerification(report.Passed, report.Count, report.BreakIndex);
    }
    public LocalizedText OpenFolder()
    {
        Directory.CreateDirectory(_directory); _open(_directory); return LocalizedText.Empty;
    }
    internal void Refresh()
    {
        if (_disposed) return;
        try
        {
            var snapshot = ReadSnapshot();
            Apply(snapshot.Events, snapshot.Coverage, snapshot.Retention); _stamp = Stamp();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        { Trace.TraceWarning("Audit view unavailable: " + ex.GetType().Name); }
    }
    private (AuditEvent[] Events, JournalCoverage Coverage, JournalRetention Retention) ReadSnapshot()
    {
        var rows = new AuditLog(_directory).Read();
        var events = rows.Select((row, index) => new AuditEvent(index + 1L, DateTimeOffset.Parse(row.Utc), row.Event switch
        {
            "request" => AuditEventType.Request, "start" => AuditEventType.Start, "end" => AuditEventType.End,
            "approval-granted" => AuditEventType.Approve, "approval-denied" => AuditEventType.Reject,
            "approval-timeout" => AuditEventType.Timeout, _ => AuditEventType.Toggle,
        }, row.Tool, row.Outcome ?? (row.ApprovalEnabled.HasValue ? (row.ApprovalEnabled.Value ? "enabled" : "disabled") : ""), AuditLog.Hash(row)[..8])).ToArray();
        return (events, JournalRetention.Coverage(_diagnostics), UiSettings.LoadRetention(_settingsPath));
    }
    private void Apply(AuditEvent[] events, JournalCoverage coverage, JournalRetention retention)
    {
        if (!_events.SequenceEqual(events)) { _events = events; Raise(nameof(Events)); Raise(nameof(TotalCount)); }
        _coverage = coverage; _retention = retention;
        Raise(nameof(Coverage)); Raise(nameof(FileSizeMb)); Raise(nameof(Copies));
    }
    private string Stamp() => string.Join("|", new[] { _directory, _diagnostics }.Where(Directory.Exists)
        .SelectMany(d => Directory.GetFiles(d)).Where(p => !p.EndsWith(".lock", StringComparison.Ordinal))
        .Append(_settingsPath).Where(File.Exists).OrderBy(p => p, StringComparer.Ordinal)
        .Select(p => { var file = new FileInfo(p); return p + ":" + file.Length + ":" + file.LastWriteTimeUtc.Ticks; }));
    private async void OnTick(object? sender, EventArgs e)
    {
        if (_disposed || _refreshing) return;
        _refreshing = true;
        try
        {
            var stamp = Stamp();
            if (_stamp == stamp) return;
            var snapshot = await Task.Run(ReadSnapshot);
            if (_disposed) return;
            Apply(snapshot.Events, snapshot.Coverage, snapshot.Retention); _stamp = stamp;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        { Trace.TraceWarning("Audit view refresh unavailable: " + ex.GetType().Name); }
        finally { _refreshing = false; }
    }
    public void Dispose() { _disposed = true; _timer.Stop(); _timer.Tick -= OnTick; }
}
