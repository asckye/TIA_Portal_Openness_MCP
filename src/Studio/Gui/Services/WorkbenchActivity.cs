using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.Services;

/// <summary>Shared operation status, progress and timestamped activity log.</summary>
public sealed class WorkbenchActivity : ObservableObject, IDisposable
{
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
        _status = LocalizedText.Key(key, args);
        Raise(nameof(Status));
    }

    /// <summary>For text that is already final - an exception message from the bridge.</summary>
    private void SetStatusLiteral(string text)
    {
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
            Append(Loc.Current.T("Log.Error", ex.Message));
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

    public void Append(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss}  {line}{System.Environment.NewLine}";

        // Bridge log lines arrive on a background reader thread.
        if (Application.Current?.Dispatcher.CheckAccess() == false)
        {
            Application.Current.Dispatcher.Invoke(() => Log += stamped);
            return;
        }
        Log += stamped;
    }

    public void ClearLog() => Log = string.Empty;

    private void OnLanguageChanged(object? sender, EventArgs e) => Raise(nameof(Status));

    public void Dispose() => Loc.Current.LanguageChanged -= OnLanguageChanged;
}
