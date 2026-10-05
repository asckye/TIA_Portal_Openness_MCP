using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using TiaMcpConfigurator;
using TiaOpenness.Core;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.Services;

public sealed class CallJournalService : ObservableObject, ICallJournalService, IDisposable
{
    private readonly object _sync = new();
    private readonly CallJournalReader _reader;
    private readonly Timer? _timer;
    private readonly string _configDirectory;
    private string _release;
    private bool _disposed;
    private IReadOnlyList<CallRecord> _calls = Array.Empty<CallRecord>();
    private ConnectionInfo _connection = new("", "", "{}");
    public IReadOnlyList<CallRecord> Calls => Volatile.Read(ref _calls);
    public ConnectionInfo Connection => Volatile.Read(ref _connection);

    public CallJournalService(string release = "21", string? directory = null, string? configDirectory = null, bool live = true)
    {
        _release = release; _configDirectory = configDirectory ?? ConfigCore.StateDirectory;
        _reader = new CallJournalReader(directory, false);
        _reader.Changed += OnChanged;
        OnChanged(this, EventArgs.Empty);
        Refresh();
        if (live) _timer = new Timer(_ => Refresh(), null, CallJournalReader.PollMilliseconds, CallJournalReader.PollMilliseconds);
    }

    public void SetRelease(string release) { lock (_sync) { _release = release; Refresh(); } }
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
            if (!File.Exists(path)) return new("", "", "{}");
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
        Volatile.Write(ref _calls, _reader.Calls.Select(c => new CallRecord(c.RequestId, c.Time, c.Host, c.Release, c.Tool,
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
        }).ToArray());
        Raise(nameof(Calls));
    }

    public void Dispose()
    {
        lock (_sync) { _disposed = true; _timer?.Dispose(); _reader.Changed -= OnChanged; _reader.Dispose(); }
    }
}
