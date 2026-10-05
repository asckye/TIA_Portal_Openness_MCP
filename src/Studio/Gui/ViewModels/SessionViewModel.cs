using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;

namespace TiaOpenness.Gui.ViewModels;

/// <summary>Owns the selected release and the bridge/project session.</summary>
public sealed class SessionViewModel : ObservableObject, IDisposable
{
    private readonly IStudioClient _client;
    private readonly IDialogService _dialogs;
    private readonly WorkbenchActivity _activity;
    private readonly Func<string, Task> _loadProject;
    private bool _started;
    private string _selectedReleaseKey;
    private bool _serviceOwnsRelease;
    private string _projectPath = string.Empty;
    private string _projectName = string.Empty;
    private string? _opennessVersion;
    private bool _useMock;
    private bool _headless;
    private bool _isConnected;

    public SessionViewModel(IStudioClient client, IDialogService dialogs, WorkbenchActivity activity,
        Func<string, Task> loadProject)
    {
        _client = client;
        _dialogs = dialogs;
        _activity = activity;
        _loadProject = loadProject;
        RunDoctor = new AsyncCommand(DoctorAsync);
        Connect = new AsyncCommand(ConnectAsync);
        Disconnect = new AsyncCommand(DisconnectAsync, () => IsConnected && !_activity.Busy);
        OpenProject = new AsyncCommand(OpenProjectAsync, () => ProjectPath.Length > 0);
        _client.Exited += OnBridgeExited;
        _activity.PropertyChanged += OnActivityChanged;
        Loc.Current.LanguageChanged += OnLanguageChanged;
    }

    public IEnumerable<TiaMcp.Versioning.TiaVersionDescriptor> Releases => TiaMcp.Versioning.TiaVersionCatalog.Runnable;

    public bool ServiceOwnsRelease
    {
        get => _serviceOwnsRelease;
        set { if (Set(ref _serviceOwnsRelease, value)) Raise(nameof(CanSelectRelease)); }
    }

    public bool CanSelectRelease => !_started && !_activity.Busy && !ServiceOwnsRelease;

    public string SelectedReleaseKey
    {
        get => _selectedReleaseKey;
        set { if (CanSelectRelease) Set(ref _selectedReleaseKey, value); }
    }

    public AsyncCommand RunDoctor { get; }
    public AsyncCommand Connect { get; }
    public AsyncCommand Disconnect { get; }
    public AsyncCommand OpenProject { get; }

    /// <summary>
    /// The pill beside the product name. Before a session exists there is nothing truthful to
    /// put there, so it shows a dash rather than guessing at an installed version.
    /// </summary>
    public string OpennessBadge => _opennessVersion is null
        ? Loc.Current["Badge.NoVersion"]
        : Loc.Current.T("Badge.Version", _opennessVersion);

    public string ProjectPath
    {
        get => _projectPath;
        set { if (Set(ref _projectPath, value)) OpenProject.RaiseCanExecuteChanged(); }
    }

    public string ProjectName { get => _projectName; private set => Set(ref _projectName, value); }

    public bool IsConnected
    {
        get => _isConnected;
        private set { if (Set(ref _isConnected, value)) Disconnect.RaiseCanExecuteChanged(); }
    }

    public bool UseMock
    {
        get => _useMock;
        set { if (Set(ref _useMock, value)) Raise(nameof(ModeLabel)); }
    }

    public bool Headless
    {
        get => _headless;
        set { if (Set(ref _headless, value)) Raise(nameof(ModeLabel)); }
    }

    /// <summary>
    /// The right-hand end of the status bar: which of the two session modes is in force, if
    /// either. Mock wins when both are set, because it is the one that decides whether anything
    /// real is touched at all.
    /// </summary>
    public string ModeLabel
    {
        get
        {
            if (UseMock) return Loc.Current["Status.MockMode"];
            return Headless ? Loc.Current["Status.HeadlessMode"] : string.Empty;
        }
    }

    private async Task DoctorAsync() => await _activity.Guarded("Status.CheckingEnvironment", async () =>
    {
        var report = await Task.Run(TiaOpenness.Core.Environment.OpennessDoctor.Run);

        _activity.AppendLocalized("Log.EnvironmentHeader");
        _activity.Append($"{report.MachineName} / {report.UserName} / {(report.Is64BitProcess ? "x64" : "x86")}");
        foreach (var check in report.Checks)
        {
            _activity.Append($"[{check.Status}] {check.Title}: {check.Detail}");
            if (!string.IsNullOrWhiteSpace(check.Remedy)) _activity.Append($"        -> {check.Remedy}");
        }
        foreach (var install in report.Installations)
        {
            _activity.AppendLocalized("Log.Installed", install.Version, install.EngineeringDllPath);
        }

        if (report.CanRunOpenness) _activity.SetStatus("Status.EnvOk");
        else _activity.SetStatus("Status.EnvNotReady");
    });

    private async Task ConnectAsync() => await _activity.Guarded("Status.Connecting", async () =>
    {
        await ConnectCoreAsync(adoptOpenProject: true);
    });

    private async Task DisconnectAsync() => await _activity.Guarded("Pages.Disconnecting", async () =>
    {
        await _client.DisconnectAsync();
        IsConnected = false;
        ProjectName = string.Empty;
        SetOpennessVersion(null);
        _activity.SetStatus("Status.NotConnected");
        _activity.AppendStatus();
    });

    /// <summary>
    /// Opens the session. Shared with <see cref="OpenProjectAsync"/>, which connects on the
    /// operator's behalf rather than making them discover the ordering.
    /// </summary>
    /// <param name="adoptOpenProject">
    /// Take over whatever project a running TIA already had open. Right when the operator asked
    /// only to connect; wrong when they named a project to open, which would be overwritten.
    /// </param>
    private async Task ConnectCoreAsync(bool adoptOpenProject)
    {
        EnsureBridge();
        var state = await _client.ConnectAsync(!Headless);
        IsConnected = true;
        SetOpennessVersion(state.OpennessVersion);
        _activity.SetStatus("Status.Connected", state.Mode, state.OpennessVersion);

        // Attaching to a running TIA inherits whatever project it already had open.
        if (adoptOpenProject && state.OpenProject is not null)
        {
            ProjectPath = state.OpenProject.Path ?? string.Empty;
            ProjectName = state.OpenProject.Name ?? string.Empty;
            _activity.AppendLocalized("Log.Attached", state.OpenProject.Name);
            await _loadProject(ProjectName);
        }
    }

    private void SetOpennessVersion(string? version)
    {
        _opennessVersion = string.IsNullOrWhiteSpace(version) ? null : "V" + version;
        Raise(nameof(OpennessBadge));
    }

    private async Task OpenProjectAsync() => await _activity.Guarded("Status.OpeningProject", async () =>
    {
        EnsureBridge();

        // Opening implies connecting, the way `tia devices --project ...` does on the command
        // line. Leaving the ordering to the operator only ever produced "Not connected. Call
        // session.connect first." - a rule the app knows and can follow itself.
        if (!IsConnected)
        {
            await ConnectCoreAsync(adoptOpenProject: false);
            _activity.SetStatus("Status.OpeningProject");
        }

        var project = await _client.OpenProjectAsync(ProjectPath);
        ProjectName = project.Name ?? string.Empty;
        _activity.AppendLocalized("Log.Opened", project.Name, project.Path);
        _activity.SetStatus("Status.ProjectOpen", project.Name);
        await _loadProject(ProjectName);
    });

    /// <summary>
    /// Starts the bridge on first use rather than at construction, so the window opens even
    /// when the bridge is missing and the error lands in the log instead of a startup crash.
    /// </summary>
    public void EnsureBridge()
    {
        if (_started && _client.IsRunning)
        {
            if (UseMock != _client.IsMock) throw new InvalidOperationException("Restart Studio to change backend mode.");
            return;
        }

        _client.Start(forceMock: UseMock, opennessVersion: SelectedReleaseKey);
        _started = true;
        Raise(nameof(CanSelectRelease));
        _activity.AppendLocalized("Log.BridgeStarted", UseMock ? "mock" : "openness");
    }

    public void BrowseProject()
    {
        var files = _dialogs.OpenFiles(Loc.Current["Dialog.OpenProject.Title"], Loc.Current["Dialog.OpenProject.Filter"]);
        if (files is not null) ProjectPath = files[0];
    }

    public async Task ApplyStartupAsync(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--mock", StringComparison.OrdinalIgnoreCase))) UseMock = true;
        var index = Array.FindIndex(args, a => string.Equals(a, "--project", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length) ProjectPath = args[index + 1];
        else if (UseMock) ProjectPath = System.IO.Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "TiaOpennessStudioMock", "Line.ap21");

        if (!UseMock) return;
        await ConnectAsync();
        if (ProjectPath.Length > 0) await OpenProjectAsync();
    }

    private void OnBridgeExited(object? sender, EventArgs e)
    {
        IsConnected = false;
        _activity.AppendLocalized("Log.BridgeExited");
    }

    private void OnActivityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkbenchActivity.Busy))
        {
            Raise(nameof(CanSelectRelease));
            Disconnect.RaiseCanExecuteChanged();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Raise(nameof(OpennessBadge));
        Raise(nameof(ModeLabel));
    }

    public void Dispose()
    {
        _client.Exited -= OnBridgeExited;
        _activity.PropertyChanged -= OnActivityChanged;
        Loc.Current.LanguageChanged -= OnLanguageChanged;
    }
}
