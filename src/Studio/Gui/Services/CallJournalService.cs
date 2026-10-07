using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TiaMcpConfigurator;
using TiaOpenness.Core;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.Services;

public sealed class CallJournalService : ObservableObject, ICallJournalService, IDisposable
{
    private readonly object _sync = new();
    private readonly CallJournalReader _reader;
    private ChangeMonitor? _monitor;
    private volatile bool _active = true;
    private string _connectionStamp = "";
    private readonly Dictionary<string, (JournalCall Source, CallRecord Row)> _rows = new(StringComparer.Ordinal);
    private readonly string _configDirectory;
    private volatile string _release;
    private volatile bool _disposed;
    private IReadOnlyList<CallRecord> _calls = Array.Empty<CallRecord>();
    private ConnectionInfo _connection = new("", "", "{}");
    public IReadOnlyList<CallRecord> Calls => Volatile.Read(ref _calls);
    public ConnectionInfo Connection => Volatile.Read(ref _connection);

    public CallJournalService(string release = "21", string? directory = null, string? configDirectory = null, bool live = true)
    {
        _release = release; _configDirectory = configDirectory ?? ConfigCore.StateDirectory;
        _reader = new CallJournalReader(directory, false, false);
        _reader.Changed += OnChanged;
        if (!live) Refresh();
        else _ = Task.Run(() =>
        {
            lock (_sync)
            {
                if (_disposed) return;
                _monitor = new ChangeMonitor(Refresh, directory ?? TiaOpenness.Shared.DataLocations.Current.DiagnosticsDirectory, _configDirectory);
                if (_disposed) { _monitor.Dispose(); return; }
                _monitor.SetActive(_active);
            }
            if (_active) Refresh();
        });
    }

    public void SetRelease(string release) { if (_release == release) return; _release = release; _ = Task.Run(Refresh); }
    public void SetActive(bool active) { _active = active; _monitor?.SetActive(active); }
    public void Refresh()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _reader.Poll();
            var connection = ReadConnection();
            if (connection != _connection) { Volatile.Write(ref _connection, connection); Raise(nameof(Connection)); }
        }
    }

    private ConnectionInfo ReadConnection()
    {
        try
        {
            string path = Path.Combine(_configDirectory, "http-v" + _release + ".json");
            var file = new FileInfo(path);
            string stamp = path + ":" + (file.Exists ? file.Length + ":" + file.LastWriteTimeUtc.Ticks : "missing");
            if (stamp == _connectionStamp) return _connection;
            _connectionStamp = stamp;
            if (!file.Exists) return new("", "", "{}");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var settings = document.RootElement;
            string address = settings.GetProperty("Address").GetString()!;
            int port = settings.GetProperty("Port").GetInt32();
            return new(ConfigCore.Prefix(address, port) + "mcp", "HTTP", ClientProfiles.ConnectionSnippet(address, port));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { return new("", "", "{}"); }
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        Volatile.Write(ref _calls, _reader.Calls.Select(c =>
        {
            if (_rows.TryGetValue(c.Identity, out var old) && ReferenceEquals(old.Source, c)) return old.Row;
            var row = new CallRecord(c.RequestId, c.Time, c.Host, c.Release, c.Tool,
            c.IsWrite, c.DisplayOutcome switch
            {
                "succeeded" => CallResult.Success, "rejected-before-operation" => CallResult.Rejected,
                "read-failed" or "failed" => CallResult.Failed, "partial" => CallResult.Partial,
                "executing" => CallResult.Executing, _ => CallResult.Unknown
            }, c.DurationMs, c.Target, c.Arguments, LocalizedText.Key("Calls.JournalSummary", c.Outcome, c.Execution, c.Completeness),
            c.ErrorCode, LocalizedText.Empty, LocalizedText.Key("Calls.ApprovalUnavailable"))
        {
            JournalKey = c.Identity, ResultJson = c.Result, Outcome = c.Outcome, Execution = c.Execution, Completeness = c.Completeness,
            ParametersTruncated = c.ArgumentsTruncated, ResultTruncated = c.ResultTruncated
        };
            _rows[c.Identity] = (c, row); return row;
        }).ToArray());
        var keep = _reader.Calls.Select(c => c.Identity).ToHashSet(StringComparer.Ordinal);
        foreach (string key in _rows.Keys.Where(k => !keep.Contains(k)).ToArray()) _rows.Remove(key);
        Raise(nameof(Calls));
    }

    public void Dispose()
    {
        _disposed = true; _monitor?.Dispose(); _reader.Changed -= OnChanged; _ = Task.Run(_reader.Dispose);
    }
}
