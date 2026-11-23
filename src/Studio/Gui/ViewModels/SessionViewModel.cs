using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Contracts.Models.Errors;

namespace TiaOpenness.Gui.ViewModels;

public sealed class ProjectProcessRow(ProjectProcessObservation observation) : ObservableObject
{
    private PortalProcessInfo Process => observation.Process;
    private TiaMcp.Versioning.TiaVersionDescriptor Version => TiaMcp.Versioning.TiaVersionCatalog.FromApiVersion(Process.Release);
    public int ProcessId => Process.ProcessId;
    public string Release => Version.DisplayName;
    public string ProjectPath => string.IsNullOrWhiteSpace(Process.ProjectPath) ? Loc.Current["ProcessPicker.NoProject"] : Process.ProjectPath;
    public string Details => $"PID {ProcessId} · {Version.OwnerApartment} · {UiMode} · {LeaseLabel}";
    private string UiMode => Process.UiMode switch
    {
        "WithUserInterface" => Loc.Current["Project.WithUi"],
        "WithoutUserInterface" => Loc.Current["Project.HeadlessProcess"],
        _ => Loc.Current["ProcessPicker.UnknownMode"],
    };
    private string LeaseLabel => observation.Lease switch
    {
        ProjectLeaseState.Available => Loc.Current["Project.LeaseAvailable"],
        ProjectLeaseState.Held => Loc.Current["Project.LeaseHeld"],
        _ => Loc.Current["Project.LeaseUnknown"],
    };
    public string AccessibleName => Release + " · " + ProjectPath + " · " + Details;
    internal void RefreshLanguage() { Raise(nameof(ProjectPath)); Raise(nameof(Details)); Raise(nameof(AccessibleName)); }
}

/// <summary>Owns the selected release and the shared session's project presentation.</summary>
public sealed class SessionViewModel : ObservableObject, IDisposable
{
    private readonly IProjectClient _client;
    private readonly IDialogService _dialogs;
    private readonly WorkbenchActivity _activity;
    private readonly Func<string, Task> _loadProject;
    private bool _started;
    private string _selectedReleaseKey;
    private bool _serviceOwnsRelease;
    private string _projectPath = string.Empty;
    private string _projectName = string.Empty;
    private string _openProjectFile = string.Empty;
    private string? _opennessVersion;
    private bool _useMock;
    private bool _headless;
    private bool _isConnected;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private bool _disposed;

    public SessionViewModel(IProjectClient client, IDialogService dialogs, WorkbenchActivity activity,
        Func<string, Task> loadProject)
    {
        _client = client;
        _selectedReleaseKey = (client as McpProjectClient)?.Session.State?["releaseKey"]?.ToString() ?? TiaOpenness.Core.Environment.WorkbenchReleaseSelection
            .DefaultKey();
        _dialogs = dialogs;
        _activity = activity;
        _loadProject = loadProject;
        Connect = new AsyncCommand(ConnectAsync, () => !_activity.Busy);
        PickProcess = new AsyncCommand(PickProcessAsync, () => !_activity.Busy && _client is McpProjectClient { IsRunning: true });
        Disconnect = new AsyncCommand(DisconnectAsync, () => IsConnected && !_activity.Busy);
        OpenProject = client is McpProjectClient
            ? new McpPanelCommand(OpenProjectAsync, () => !_activity.Busy && ProjectPath.Length > 0)
            : new AsyncCommand(OpenProjectAsync, () => !_activity.Busy && ProjectPath.Length > 0);
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

    public bool CanSelectRelease => (_client is McpProjectClient ? !_client.IsRunning : !_started) && !_activity.Busy && !ServiceOwnsRelease;

    public string ProjectFilePlaceholder => Loc.Current.T("Project.FilePlaceholder",
        SelectedReleaseKey switch { "14sp1" => "14", "15.1" => "15_1", _ => SelectedReleaseKey });
    public string SelectedReleaseKey
    {
        get => _selectedReleaseKey;
        set { if (CanSelectRelease && Set(ref _selectedReleaseKey, value)) { ProcessId = 0; ClearProcessList(); Raise(nameof(ProjectFilePlaceholder)); } }
    }

    public AsyncCommand Connect { get; }
    public AsyncCommand PickProcess { get; }
    public AsyncCommand Disconnect { get; }
    [PrefillTarget(Executes = "mcp")]
    public AsyncCommand OpenProject { get; }
    private int _processId;
    public int ProcessId
    {
        get => (_client as McpProjectClient)?.ProcessId ?? _processId;
        set { if (_client is McpProjectClient project) project.ProcessId = value; _processId = value; Raise(); }
    }
    private bool _scannedProcesses;
    private IReadOnlyList<PortalProcessInfo> _processes = [];
    public IReadOnlyList<ProjectProcessRow> Processes { get; private set; } = [];
    public string ProcessListHint => _scannedProcesses ? Loc.Current["Project.NoInstances"]
        : _client is McpProjectClient && !_client.IsRunning ? Loc.Current["Project.ScanSession"] : Loc.Current["Project.ScanFirst"];
    public bool HasProcesses => Processes.Count > 0;
    public bool CanOpenConnection => !_activity.Busy;
    public bool ProcessScanAvailable => !_activity.Busy && (_client is not McpProjectClient || _client.IsRunning);
    public bool ShowConnectionForm => !LostConnection && (!IsConnected || ProjectName.Length == 0);
    public bool LostConnection => !IsConnected && OpenProjectFile.Length > 0;
    public string ConnectionLabel => IsConnected ? Loc.Current["Project.NoOpenFile"] : Loc.Current["Project.Disconnected"];

    internal async Task RefreshProcessesAsync()
    {
        if (!ProcessScanAvailable) return;
        ClearProcessList();
        string release = SelectedReleaseKey;
        await _activity.Guarded("Project.Scan", async () =>
        {
            var processes = await _client.ListProcessesAsync();
            if (release != SelectedReleaseKey || _client is McpProjectClient && !_client.IsRunning) return;
            _scannedProcesses = true; Raise(nameof(ProcessListHint));
            _processes = processes.Select(row => row.Process).ToArray();
            Processes = processes.Select(row => new ProjectProcessRow(row)).ToArray();
            Raise(nameof(Processes)); Raise(nameof(HasProcesses));
        });
    }

    internal async Task ChooseProcessAsync(bool attach)
    {
        await RefreshProcessesAsync();
        if (!ProcessScanAvailable || !HasProcesses) return;
        if (_dialogs.PickPortalProcess(_processes) is int pid) await SelectProcessAsync(pid, attach);
    }

    internal async Task SelectProcessAsync(int pid, bool attach)
    {
        if (!ProcessScanAvailable || !_processes.Any(process => process.ProcessId == pid)) return;
        ProcessId = pid;
        if (attach) await _activity.Guarded("Status.Connecting", () => ConnectCoreAsync(true, pid));
    }

    private void ClearProcessList()
    {
        _scannedProcesses = false; Raise(nameof(ProcessListHint));
        _processes = [];
        Processes = [];
        Raise(nameof(Processes)); Raise(nameof(HasProcesses)); Raise(nameof(ProcessScanAvailable));
    }
    private string? _loadedSharedBinding;
    public async Task ApplySharedStateAsync(JsonObject? state)
    {
        if (_client is not McpProjectClient project) return;
        Raise(nameof(ProcessScanAvailable)); Raise(nameof(ProcessListHint));
        PickProcess.RaiseCanExecuteChanged();
        if ((string?)state?["releaseKey"] is { } release && Set(ref _selectedReleaseKey, release, nameof(SelectedReleaseKey)))
            Raise(nameof(ProjectFilePlaceholder));
        Raise(nameof(CanSelectRelease));
        if (state == null || (string?)state["worker"]?["state"] == "disconnected")
        {
            IsConnected = false;
            if (!(state == null && project.LostTransport) && (string?)state?["endReason"] != "worker-restart") OpenProjectFile = "";
            ProjectName = ""; _loadedSharedBinding = null; SetOpennessVersion(null);
            return;
        }
        ProcessId = (int?)state["binding"]?["processId"] ?? 0;
        IsConnected = ProcessId > 0;
        string file = (string?)state["binding"]?["projectFile"] ?? "";
        project.ExpectedProjectFile = file;
        SetOpennessVersion((string?)state["releaseKey"]);
        OpenProjectFile = file;
        if (file.Length == 0) { ProjectName = ""; _loadedSharedBinding = null; return; }
        ProjectPath = file; ProjectName = System.IO.Path.GetFileNameWithoutExtension(file);
        string binding = project.Session.SessionKey + ":" + state["binding"]?["bindingEpoch"] + ":" + file;
        if (_loadedSharedBinding == binding) return;
        _loadedSharedBinding = binding;
        try { await _loadProject(ProjectName); }
        catch (Exception error)
        {
            _activity.AppendLocalized("Log.Error", error.Message);
            _activity.Notify(ToastKind.Error, LocalizedText.Literal(error.Message), "project-load");
        }
    }

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

    public string ProjectName { get => _projectName; private set { if (Set(ref _projectName, value)) Raise(nameof(ShowConnectionForm)); } }
    public string OpenProjectFile
    {
        get => _openProjectFile;
        private set { if (Set(ref _openProjectFile, value)) { Raise(nameof(LostConnection)); Raise(nameof(ShowConnectionForm)); } }
    }
    public string BoundProjectFile => (_client as McpProjectClient)?.ExpectedProjectFile ?? ProjectPath;

    public bool IsConnected
    {
        get => _isConnected;
        private set { if (Set(ref _isConnected, value))
            {
                Raise(nameof(ConnectionLabel)); Raise(nameof(LostConnection)); Raise(nameof(ShowConnectionForm)); Disconnect.RaiseCanExecuteChanged();
            } }
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

    private async Task ConnectAsync() => await _activity.Guarded("Status.Connecting", async () =>
    {
        await ConnectCoreAsync(adoptOpenProject: true);
    });

    private async Task PickProcessAsync() => await _activity.Guarded("Status.Connecting", async () =>
    {
        var client = (McpProjectClient)_client;
        var key = client.Session.SessionKey;
        var processes = await ScanProcessesAsync();
        int? pid = _dialogs.PickPortalProcess(processes, ScanProcessesAsync);
        if (pid.HasValue && key != null && key == client.Session.SessionKey)
            await ConnectCoreAsync(adoptOpenProject: true, pid);
    });

    private async Task<IReadOnlyList<PortalProcessInfo>> ScanProcessesAsync(CancellationToken token = default)
    {
        var client = (McpProjectClient)_client;
        var data = await client.Read("ListPortalProcessProjects", token: token);
        var rows = (data["processes"] as JsonArray ?? data["items"] as JsonArray ?? []).Select(McpProjectClient.Decode).OfType<JsonObject>().ToArray();
        if (rows.Length == 0) return [];
        var info = await client.Read("GetPortalInfo", new() { ["includeProcesses"] = true, ["includeSessions"] = false, ["includeProducts"] = false }, token);
        var modes = (info["evidence"]?["processes"] as JsonArray ?? []).Select(McpProjectClient.Decode).OfType<JsonObject>()
            .ToDictionary(p => (int)p["id"]!, p => (string?)p["mode"]);
        return rows.Select(p => new PortalProcessInfo
        {
            ProcessId = (int)p["processId"]!, ProjectPath = (string?)p["projectPath"],
            Release = (string?)data["releaseKey"] ?? SelectedReleaseKey,
            UiMode = modes.GetValueOrDefault((int)p["processId"]!),
        }).ToArray();
    }

    private async Task DisconnectAsync() => await _activity.Guarded("Pages.Disconnecting", async () =>
    {
        if (_client is McpProjectClient project)
        {
            var decision = _dialogs.ShowMessage(
                Loc.Current.T("Session.DisconnectWarning", (string?)project.Session.State?["participants"]?["mcpClient"] ?? "MCP"),
                Loc.Current["Pages.Disconnect"], DialogButtons.OKCancel, DialogIcon.Warning,
                new(ConfirmationAction.EndSharedSession));
            if (decision == DialogResult.Detach) { await project.Session.DetachAsync(); _activity.SetStatus("Session.Detached"); return; }
            if (decision != DialogResult.OK) return;
        }
        await _client.DisconnectAsync();
        IsConnected = false;
        OpenProjectFile = string.Empty;
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
    private async Task<bool> ConnectCoreAsync(bool adoptOpenProject, int? processId = null)
    {
        EnsureBridge();
        if (_client is McpProjectClient project) project.ExpectedProjectFile = ProjectPath;
        SessionState state;
        int? selectedPid = processId;
        try { state = await _client.ConnectAsync(!Headless, processId ?? (ProcessId > 0 ? ProcessId : null)); }
        catch (PortalProcessSelectionRequiredException selection) when (!selectedPid.HasValue)
        {
            processId = _client is McpProjectClient ? _dialogs.PickPortalProcess(selection.Processes, ScanProcessesAsync)
                : _dialogs.PickPortalProcess(selection.Processes);
            if (!processId.HasValue) { _activity.SetStatus("Status.NotConnected", ToastKind.Warning); return false; }
            state = await _client.ConnectAsync(!Headless, processId);
        }
        if (processId.HasValue) ProcessId = processId.Value;
        IsConnected = true;
        SetOpennessVersion(state.OpennessVersion);
        _activity.SetStatus("Status.Connected", ToastKind.Success, state.Mode, state.OpennessVersion);

        if (adoptOpenProject && state.OpenProject == null)
        {
            OpenProjectFile = ""; ProjectName = "";
        }
        // Attaching to a running TIA inherits whatever project it already had open.
        if (adoptOpenProject && state.OpenProject is not null)
        {
            OpenProjectFile = ProjectPath = state.OpenProject.Path ?? string.Empty;
            ProjectName = state.OpenProject.Name ?? string.Empty;
            _activity.AppendLocalized("Log.Attached", state.OpenProject.Name);
            await _loadProject(ProjectName);
        }
        else if (adoptOpenProject && selectedPid.HasValue)
        {
            OpenProjectFile = ProjectName = string.Empty;
            if (_client is McpProjectClient selected) selected.ExpectedProjectFile = string.Empty;
        }
        return true;
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
            if (!await ConnectCoreAsync(adoptOpenProject: false)) return;
            _activity.SetStatus("Status.OpeningProject", kind: null);
        }

        var project = await _client.OpenProjectAsync(ProjectPath);
        OpenProjectFile = project.Path ?? ProjectPath;
        ProjectName = project.Name ?? string.Empty;
        _activity.AppendLocalized("Log.Opened", project.Name, project.Path);
        _activity.SetStatus("Status.ProjectOpen", ToastKind.Success, project.Name);
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
            if (UseMock != _client.IsMock) throw new InvalidOperationException(Loc.Current["Session.RestartMode"]);
            return;
        }

        _client.Start(forceMock: UseMock, opennessVersion: SelectedReleaseKey);
        _started = true;
        Raise(nameof(CanSelectRelease));
        if (_client is McpProjectClient project) _activity.AppendLocalized("Session.Attached", project.Session.SessionKey);
        else _activity.AppendLocalized("Log.BridgeStarted", UseMock ? "mock" : "openness");
    }

    public void BrowseProject()
    {
        var files = _dialogs.OpenFiles(Loc.Current["Dialog.OpenProject.Title"], Loc.Current["Dialog.OpenProject.Filter"]);
        if (files is not null) ProjectPath = files[0];
    }

    public async Task ApplyStartupAsync(string[] args)
    {
        if (_client is McpProjectClient && args.Any(a => string.Equals(a, "--mock", StringComparison.OrdinalIgnoreCase)))
        { _activity.Append("--mock: select an offline operator fixture session for demonstrations."); return; }
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
        if (!_dispatcher.CheckAccess())
        { _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => OnBridgeExited(sender, e))); return; }
        if (_disposed) return;
        if (_client is McpProjectClient && _client.IsRunning) return;
        IsConnected = false;
        ClearProcessList();
        if (_client is McpProjectClient) _activity.AppendLocalized("Session.Detached");
        else _activity.AppendLocalized("Log.BridgeExited");
    }

    private void OnActivityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkbenchActivity.Busy))
        {
            Raise(nameof(CanSelectRelease));
            Raise(nameof(CanOpenConnection));
            Raise(nameof(ProcessScanAvailable)); Raise(nameof(ConnectionLabel)); Raise(nameof(LostConnection));
            Disconnect.RaiseCanExecuteChanged();
            Connect.RaiseCanExecuteChanged();
            PickProcess.RaiseCanExecuteChanged();
            OpenProject.RaiseCanExecuteChanged();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Raise(nameof(OpennessBadge));
        Raise(nameof(ProjectFilePlaceholder)); Raise(nameof(ModeLabel)); Raise(nameof(ConnectionLabel)); Raise(nameof(ProcessListHint));
        foreach (var process in Processes) process.RefreshLanguage();
    }

    public void Dispose()
    {
        _disposed = true;
        _client.Exited -= OnBridgeExited;
        _activity.PropertyChanged -= OnActivityChanged;
        Loc.Current.LanguageChanged -= OnLanguageChanged;
    }
}
