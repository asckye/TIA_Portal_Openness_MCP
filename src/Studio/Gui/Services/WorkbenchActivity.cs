using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.Services;

/// <summary>Shared operation status, progress and timestamped activity log.</summary>
public sealed class WorkbenchActivity : ObservableObject, IDisposable
{
    public enum Severity { Default, Info, Warning, Error, Debug }
    public sealed record Entry(string Time, string Message, Severity Level, string? Key, object?[] Arguments);
    private readonly List<Entry> _entries = new();
    public IReadOnlyList<Entry> Entries => _entries.AsReadOnly();
    private string? _statusKey;
    private object?[] _statusArgs = Array.Empty<object?>();
    private LocalizedText _status = LocalizedText.Key("Status.NotConnected");
    private string _log = string.Empty;
    private bool _busy;
    private int _progressValue;
    private int _progressMax;

    public WorkbenchActivity() => Loc.Current.LanguageChanged += OnLanguageChanged;

    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    public string Status => _status.Resolve();

    public string Log
    {
        get => _log;
        private set
        {
            if (!Set(ref _log, value)) return;
            Raise(nameof(HasLog));
            Raise(nameof(LogLineCount));
        }
    }

    public int ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }

    public int ProgressMax { get => _progressMax; private set => Set(ref _progressMax, value); }

    public bool HasLog => Log.Length > 0;

    public int LogLineCount => Log.Length == 0 ? 0 : Log.Count(c => c == '\n');

    public void SetStatus(string key, params object?[] args)
    {
        _statusKey = key;
        _statusArgs = (object?[])args.Clone();
        _status = LocalizedText.Key(key, args);
        Raise(nameof(Status));
    }

    /// <summary>For text that is already final - an exception message from the bridge.</summary>
    private void SetStatusLiteral(string text)
    {
        _statusKey = null;
        _status = LocalizedText.Literal(text);
        Raise(nameof(Status));
    }

    public async Task Guarded(string workingKey, Func<Task> action, params object?[] workingArgs)
    {
        Busy = true;
        _status = LocalizedText.Working(workingKey, workingArgs);
        Raise(nameof(Status));

        try
        {
            await action();
        }
        catch (Exception ex)
        {
            SetStatusLiteral(ex.Message);
            AppendLocalized("Log.Error", ex.Message);
        }
        finally
        {
            Busy = false;
            ProgressMax = 0;
        }
    }

    public void OnProgress(TiaOpenness.Contracts.Rpc.ProgressPayload payload)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            ProgressMax = payload.Total;
            ProgressValue = payload.Current;
            SetStatusLiteral($"{payload.Operation} {payload.Current}/{payload.Total}: {payload.Message}");
        });
    }

    public void AppendStatus() => AppendEntry(Status, Severity.Default, _statusKey, _statusArgs);

    public void AppendLocalized(string key, params object?[] args)
        => AppendEntry(Loc.Current.T(key, args), key == "Log.Error" || key == "Log.Failed" ? Severity.Error : Severity.Default, key, args);

    public void AppendDiagnostic(string target, string description, Severity severity)
        => AppendEntry($"{severity}: {target} - {description}", severity, "compile-diagnostic", new object?[] { target, description });

    public void AppendRule(string rule, int count)
        => AppendEntry($"{rule} ({count})", Severity.Default, "inspection-rule", new object?[] { rule, count });

    public void Append(string line, Severity severity = Severity.Default) => AppendEntry(line, severity, null, Array.Empty<object?>());

    private void AppendEntry(string line, Severity severity, string? key, object?[] args)
    {
        string time = DateTime.Now.ToString("HH:mm:ss");
        var stamped = $"{time}  {line}{System.Environment.NewLine}";
        void Add()
        {
            _entries.Add(new Entry(time, line, severity, key, (object?[])args.Clone()));
            Log += stamped;
        }

        // Bridge log lines arrive on a background reader thread.
        if (Application.Current?.Dispatcher.CheckAccess() == false)
        {
            Application.Current.Dispatcher.Invoke(Add);
            return;
        }
        Add();
    }

    public void ClearLog() { _entries.Clear(); Log = string.Empty; }

    private void OnLanguageChanged(object? sender, EventArgs e) => Raise(nameof(Status));

    public void Dispose() => Loc.Current.LanguageChanged -= OnLanguageChanged;
}
