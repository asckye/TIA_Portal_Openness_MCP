using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Settings;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Shared;

namespace TiaOpenness.Gui.Services;

public sealed class AuditLogService : ObservableObject, IAuditLogService, IDisposable
{
    public const int PageSize = 1000;
    private readonly object _sync = new(), _saveSync = new();
    private readonly string _directory, _diagnostics, _settingsPath;
    private readonly string[]? _chains;
    private string[] Directories => _chains ?? DataLocations.Current.AuditReadRoots;
    private string _viewDirectory;
    private readonly Action<string> _open;
    private ChangeMonitor? _monitor;
    private AuditTailReader _reader;
    private IReadOnlyList<AuditEvent> _events = Array.Empty<AuditEvent>();
    private JournalCoverage _coverage = new(null, null);
    private JournalRetention _retention = new();
    private volatile bool _disposed, _active = true;
    private int _total;
    private long? _pageEnd, _loadedPageEnd;
    private AuditEvent[] _page = [];
    private int _pageGeneration;
    internal long BytesRead => _reader.BytesRead;
    internal int ReadThreadId => _reader.ReadThreadId;

    public AuditLogService(string? directory = null, string? diagnostics = null, string? settingsPath = null, Action<string>? open = null, string[]? chains = null)
    {
        _directory = directory ?? DataLocations.Current.AuditDirectory;
        _chains = chains ?? (directory == null ? null : new[] { directory });
        _viewDirectory = _directory; _reader = new AuditTailReader(_viewDirectory);
        _diagnostics = diagnostics ?? DataLocations.Current.DiagnosticsDirectory;
        _settingsPath = settingsPath ?? JournalRetention.SettingsPath;
        _open = open ?? (path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));
        _ = Task.Run(() =>
        {
            lock (_sync)
            {
                if (_disposed) return;
                _monitor = new ChangeMonitor(Refresh, Directories.Append(_diagnostics).Append(_settingsPath).ToArray());
                if (_disposed) { _monitor.Dispose(); return; }
                _monitor.SetActive(_active);
            }
            if (_active) Refresh();
        });
    }
    public IReadOnlyList<AuditEvent> Events => Volatile.Read(ref _events);
    public int TotalCount => Volatile.Read(ref _total);
    public int FileSizeMb { get => _retention.FileSizeMb; set => SaveRetention(value, null); }
    public int Copies { get => _retention.Copies; set => SaveRetention(null, value); }
    public LocalizedText Coverage => _coverage.Start.HasValue && _coverage.End.HasValue
        ? LocalizedText.Key("Audit.Window", _coverage.Start.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), _coverage.End.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
        : LocalizedText.Key("Audit.NoCoverage");
    public bool HasOlder => Events.Count > 0 && Events[0].Index > 1;
    public bool IsLatest => _pageEnd == null;
    public Task OlderAsync() => Task.Run(() => { lock (_sync) { _pageEnd = Math.Max(1, (Events.FirstOrDefault()?.Index ?? 1) - 1); Refresh(); } });
    public Task LatestAsync() => Task.Run(() => { lock (_sync) { _pageEnd = null; Refresh(); } });
    public Task ShowIndexAsync(long index) => Task.Run(() => { lock (_sync) { _pageEnd = index + PageSize / 2; Refresh(); } });
    public void SetActive(bool active) { _active = active; _monitor?.SetActive(active); }
    private void SaveRetention(int? size, int? copies)
    {
        bool sizeChanged, copiesChanged;
        lock (_saveSync)
        {
            var next = new JournalRetention(size ?? FileSizeMb, copies ?? Copies);
            sizeChanged = next.FileSizeMb != FileSizeMb; copiesChanged = next.Copies != Copies;
            if (!sizeChanged && !copiesChanged) return;
            UiSettings.SaveRetention(_settingsPath, next.FileSizeMb, next.Copies);
            _retention = next;
        }
        if (sizeChanged) Raise(nameof(FileSizeMb));
        if (copiesChanged) Raise(nameof(Copies));
    }
    public AuditVerification Verify() => Verify(null);
    internal AuditVerification Verify(Action<int>? progress)
    {
        var directories = Directories;
        var reports = directories.Select((path, index) =>
        {
            progress?.Invoke(index * 100 / directories.Length);
            return new AuditLog(path).Verify();
        }).ToArray();
        var broken = reports.FirstOrDefault(report => !report.Passed);
        lock (_sync)
        {
            if (broken?.Chain != null && _viewDirectory != broken.Chain)
            {
                _viewDirectory = broken.Chain; _reader = new AuditTailReader(_viewDirectory); _loadedPageEnd = null;
                Volatile.Write(ref _events, Array.Empty<AuditEvent>()); Volatile.Write(ref _total, 0);
                Raise(nameof(Events)); Raise(nameof(TotalCount));
            }
            if (broken?.BreakIndex != null) _pageEnd = broken.BreakIndex + PageSize / 2;
        }
        Refresh(); progress?.Invoke(100);
        return new AuditVerification(broken == null, broken?.Count ?? reports.Sum(report => report.Count), broken?.BreakIndex)
        { Chain = broken?.Chain, File = broken?.File, Chains = reports.Select(report => new AuditChainVerification(report.Chain!, report.Passed, report.Count, report.BreakIndex, report.File)).ToArray() };
    }
    public LocalizedText OpenFolder()
    {
        try { Directory.CreateDirectory(_viewDirectory); _open(_viewDirectory); return LocalizedText.Empty; }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        { throw new IOException("DIAGNOSTIC_WRITE_FAILED: " + _viewDirectory + ": " + ex.Message, ex); }
    }
    internal Task RefreshAsync() => Task.Run(Refresh);
    internal void Refresh()
    {
        lock (_sync)
        {
            if (_disposed) return;
            try
            {
                var recent = _reader.Poll();
                if (_pageEnd.HasValue && (_pageEnd != _loadedPageEnd || _pageGeneration != _reader.Generation)) { _page = _reader.Page(_pageEnd.Value); _loadedPageEnd = _pageEnd; _pageGeneration = _reader.Generation; }
                var events = _pageEnd.HasValue ? _page : recent;
                if (!_events.SequenceEqual(events))
                { Volatile.Write(ref _events, events); Raise(nameof(Events)); Raise(nameof(HasOlder)); Raise(nameof(IsLatest)); }
                if (_total != _reader.TotalCount) { Volatile.Write(ref _total, _reader.TotalCount); Raise(nameof(TotalCount)); }
                var coverage = JournalRetention.Coverage(_diagnostics);
                if (_coverage.Start != coverage.Start || _coverage.End != coverage.End) { _coverage = coverage; Raise(nameof(Coverage)); }
                lock (_saveSync)
                {
                    var retention = UiSettings.LoadRetention(_settingsPath);
                    bool sizeChanged = _retention.FileSizeMb != retention.FileSizeMb, copiesChanged = _retention.Copies != retention.Copies;
                    _retention = retention;
                    if (sizeChanged) Raise(nameof(FileSizeMb));
                    if (copiesChanged) Raise(nameof(Copies));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { Trace.TraceWarning("Audit view unavailable: " + ex.GetType().Name); }
        }
    }
    public void Dispose() { _disposed = true; _monitor?.Dispose(); }
}
