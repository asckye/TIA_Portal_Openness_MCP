using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using TiaOpenness.Client;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.ViewModels;

/// <summary>A block row with the selection state the export list needs.</summary>
public sealed class BlockRow(BlockInfo info) : ObservableObject
{
    private bool _selected;

    public BlockInfo Info { get; } = info;
    public string Path => Info.Path;
    public string Name => Info.Name;
    public string Kind => Info.Kind.ToString();
    public string Number => Info.Number?.ToString() ?? "-";
    public string Language => Info.ProgrammingLanguage ?? "-";
    public string Author => Info.HeaderAuthor ?? "-";

    /// <summary>Short reason the block cannot be exported, or empty when it can.</summary>
    public string Status =>
        Info.IsKnowHowProtected ? Loc.Current["Block.Protected"]
        : !Info.IsConsistent ? Loc.Current["Block.NeedsCompiling"]
        : string.Empty;

    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    /// <summary>Called when the language changes; <see cref="Status"/> is translated on read.</summary>
    public void RefreshLocalizedText() => Raise(nameof(Status));
}

/// <summary>Which of the main views the window is showing.</summary>
public enum MainView
{
    Blocks,
    VersionControl,
    Log,
}

/// <summary>Drives the single main window.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly TiaClient _client = new();
    private bool _started;
    private string _selectedReleaseKey;
    public IEnumerable<TiaMcp.Versioning.TiaVersionDescriptor> Releases => TiaMcp.Versioning.TiaVersionCatalog.Runnable;
    public bool CanSelectRelease => !_started && !Busy;
    public string SelectedReleaseKey
    {
        get => _selectedReleaseKey;
        set { if (CanSelectRelease) Set(ref _selectedReleaseKey, value); }
    }

    private string _projectPath = string.Empty;
    private string _projectName = string.Empty;
    private string _outputDirectory = string.Empty;
    private LocalizedText _status = LocalizedText.Key("Status.NotConnected");
    private string _log = string.Empty;
    private string _blockFilter = string.Empty;
    private string _namePattern = "^(OB|FB|FC|DB|UDT)_";
    private string? _opennessVersion;
    private DeviceInfo? _selectedDevice;
    private bool _useMock;
    private bool _headless;
    private bool _sourceFormat;
    private bool _busy;
    private bool _isConnected;
    private MainView _view = MainView.Blocks;
    private bool _suppressDeviceLoad;
    private int _progressValue;
    private int _progressMax;

    private bool _vcSupported;
    private bool _vcDryRun = true;
    private bool _vcShowAll;
    private WorkspaceInfo? _selectedWorkspace;
    private MappedObjectInfo? _selectedVcItem;
    private string _vcDiffCaption = string.Empty;
    private string _newWorkspaceName = "git";
    private string _newWorkspaceFolder = string.Empty;

    public MainViewModel()
    {
        Blocks = new ObservableCollection<BlockRow>();
        BlocksView = CollectionViewSource.GetDefaultView(Blocks);
        BlocksView.Filter = FilterBlock;

        Devices.CollectionChanged += (_, _) => Raise(nameof(HasDevices));
        Blocks.CollectionChanged += OnBlocksChanged;
        VcStatusItems.CollectionChanged += (_, _) => Raise(nameof(HasVcItems));

        // Anything shown as text but stored as a key has to be re-read when the language flips.
        Loc.Current.LanguageChanged += OnLanguageChanged;

        _client.Bridge.Log += (_, e) => Append("bridge: " + e.Line);
        _client.Bridge.Progress += (_, e) => OnProgress(e.Progress);
        _client.Bridge.Exited += (_, _) =>
        {
            IsConnected = false;
            Append(Loc.Current["Log.BridgeExited"]);
        };

        RunDoctor = new AsyncCommand(DoctorAsync);
        Connect = new AsyncCommand(ConnectAsync);
        OpenProject = new AsyncCommand(OpenProjectAsync, () => ProjectPath.Length > 0);
        Refresh = new AsyncCommand(RefreshBlocksAsync, () => SelectedDevice is not null);
        Export = new AsyncCommand(ExportAsync, () => SelectedDevice is not null && OutputDirectory.Length > 0);
        Import = new AsyncCommand(ImportAsync, () => SelectedDevice is not null);
        Compile = new AsyncCommand(CompileAsync, () => SelectedDevice is not null);
        Inspect = new AsyncCommand(InspectAsync, () => SelectedDevice is not null);
        Save = new AsyncCommand(SaveAsync, () => SelectedDevice is not null);

        VcRefresh = new AsyncCommand(VcRefreshAsync);
        VcCreate = new AsyncCommand(VcCreateAsync,
            () => VcSupported && NewWorkspaceName.Length > 0 && NewWorkspaceFolder.Length > 0);
        VcMap = new AsyncCommand(VcMapAsync, () => VcSupported && SelectedWorkspace is not null);
        VcStatus = new AsyncCommand(VcStatusAsync, () => VcSupported && SelectedWorkspace is not null);
        VcPush = new AsyncCommand(VcPushAsync, () => VcSupported && SelectedWorkspace is not null);
        VcPull = new AsyncCommand(VcPullAsync, () => VcSupported && SelectedWorkspace is not null);
    }

    private void RaiseVcCommands()
    {
        foreach (var command in new[] { VcCreate, VcMap, VcStatus, VcPush, VcPull })
        {
            command.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Re-reads everything whose text is a catalogue key rather than a literal. The log is left
    /// alone on purpose: it is a record of what was reported at the time.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var name in new[]
                 {
                     nameof(Status), nameof(SelectionSummary), nameof(WorkspaceRootDisplay),
                     nameof(OpennessBadge), nameof(ModeLabel),
                 })
        {
            Raise(name);
        }

        foreach (var row in Blocks) row.RefreshLocalizedText();

        // The tree carries TIA's category names, which are captured when it is built.
        RebuildTree();
    }

    private void OnBlocksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Raise(nameof(HasBlocks));
        Raise(nameof(SelectionSummary));
    }

    // ---- bindable state ----------------------------------------------------

    public ObservableCollection<DeviceInfo> Devices { get; } = [];
    public ObservableCollection<BlockRow> Blocks { get; }
    public ICollectionView BlocksView { get; }

    /// <summary>The blocks as TIA shows them: its categories, then the engineer's own folders.</summary>
    public ObservableCollection<BlockNode> BlockTree { get; } = [];

    /// <summary>The devices as TIA shows them: the project, its device groups, then the devices.</summary>
    public ObservableCollection<DeviceNode> DeviceTree { get; } = [];

    public ObservableCollection<WorkspaceInfo> Workspaces { get; } = [];
    public ObservableCollection<MappedObjectInfo> VcStatusItems { get; } = [];

    /// <summary>The selected object's uncommitted change, as Git sees it.</summary>
    public ObservableCollection<DiffLine> VcDiffLines { get; } = [];

    public AsyncCommand RunDoctor { get; }
    public AsyncCommand Connect { get; }
    public AsyncCommand OpenProject { get; }
    public AsyncCommand Refresh { get; }
    public AsyncCommand Export { get; }
    public AsyncCommand Import { get; }
    public AsyncCommand Compile { get; }
    public AsyncCommand Inspect { get; }
    public AsyncCommand Save { get; }

    public AsyncCommand VcRefresh { get; }
    public AsyncCommand VcCreate { get; }
    public AsyncCommand VcMap { get; }
    public AsyncCommand VcStatus { get; }
    public AsyncCommand VcPush { get; }
    public AsyncCommand VcPull { get; }

    /// <summary>Shown at the foot of the sidebar, from the assembly the app was built as.</summary>
    public static string AppVersion { get; } =
        "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");

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

    public string OutputDirectory
    {
        get => _outputDirectory;
        set { if (Set(ref _outputDirectory, value)) Export.RaiseCanExecuteChanged(); }
    }

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

    public string NamePattern { get => _namePattern; set => Set(ref _namePattern, value); }
    public bool SourceFormat { get => _sourceFormat; set => Set(ref _sourceFormat, value); }
    public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }
    public int ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }
    public int ProgressMax { get => _progressMax; private set => Set(ref _progressMax, value); }

    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) Raise(nameof(CanSelectRelease)); } }

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

    public bool HasDevices => Devices.Count > 0;
    public bool HasBlocks => Blocks.Count > 0;
    public bool HasVcItems => VcStatusItems.Count > 0;
    public bool HasLog => Log.Length > 0;

    public int LogLineCount => Log.Length == 0 ? 0 : Log.Count(c => c == '\n');

    // The views are one choice, so they are one field with a bindable face each; independent
    // bools would let all of them be false and show nothing at all.
    private MainView View
    {
        get => _view;
        set
        {
            if (_view == value) return;
            _view = value;
            Raise(nameof(IsBlocksTab));
            Raise(nameof(IsVcTab));
            Raise(nameof(IsLogTab));
        }
    }

    public bool IsBlocksTab
    {
        get => _view == MainView.Blocks;
        set { if (value) View = MainView.Blocks; }
    }

    public bool IsVcTab
    {
        get => _view == MainView.VersionControl;
        set { if (value) View = MainView.VersionControl; }
    }

    public bool IsLogTab
    {
        get => _view == MainView.Log;
        set { if (value) View = MainView.Log; }
    }

    public string BlockFilter
    {
        get => _blockFilter;
        set
        {
            if (!Set(ref _blockFilter, value)) return;
            BlocksView.Refresh();
            RebuildTree();
        }
    }

    /// <summary>Whether the current project exposes the Version Control Interface.</summary>
    public bool VcSupported
    {
        get => _vcSupported;
        private set { if (Set(ref _vcSupported, value)) RaiseVcCommands(); }
    }

    /// <summary>
    /// Selecting a changed object loads its diff. The comparison is against the files, so it only
    /// says anything after a push - which is exactly when it is worth reading, just before commit.
    /// </summary>
    public MappedObjectInfo? SelectedVcItem
    {
        get => _selectedVcItem;
        set
        {
            if (!Set(ref _selectedVcItem, value)) return;
            Raise(nameof(HasVcDiff));
            _ = LoadVcDiffAsync();
        }
    }

    public bool HasVcDiff => VcDiffLines.Count > 0;

    public string VcDiffCaption { get => _vcDiffCaption; private set => Set(ref _vcDiffCaption, value); }

    public WorkspaceInfo? SelectedWorkspace
    {
        get => _selectedWorkspace;
        set
        {
            if (!Set(ref _selectedWorkspace, value)) return;
            Raise(nameof(WorkspaceRootDisplay));
            RaiseVcCommands();
        }
    }

    /// <summary>The workspace's folder, or the sentence that explains there is not one yet.</summary>
    public string WorkspaceRootDisplay
        => SelectedWorkspace?.RootPath ?? Loc.Current["Vc.NoWorkspace"];

    public string NewWorkspaceName
    {
        get => _newWorkspaceName;
        set { if (Set(ref _newWorkspaceName, value)) RaiseVcCommands(); }
    }

    public string NewWorkspaceFolder
    {
        get => _newWorkspaceFolder;
        set { if (Set(ref _newWorkspaceFolder, value)) RaiseVcCommands(); }
    }

    /// <summary>
    /// On by default. Mapping writes into the project and a pull overwrites blocks, so the
    /// destructive step is always one deliberate click away rather than the default.
    /// </summary>
    public bool VcDryRun { get => _vcDryRun; set => Set(ref _vcDryRun, value); }

    public bool VcShowAll { get => _vcShowAll; set => Set(ref _vcShowAll, value); }

    public DeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!Set(ref _selectedDevice, value)) return;
            foreach (var command in new[] { Refresh, Export, Import, Compile, Inspect, Save })
            {
                command.RaiseCanExecuteChanged();
            }
            // A device picked in the list loads its blocks. Suppressed while the app selects one
            // itself, where the caller awaits the load instead of racing this fire-and-forget one.
            if (value is not null && !_suppressDeviceLoad) _ = RefreshBlocksAsync();
        }
    }

    public string SelectionSummary
    {
        get
        {
            var selected = Blocks.Count(b => b.Selected);
            return selected == 0
                ? Loc.Current.T("Blocks.Count", Blocks.Count)
                : Loc.Current.T("Blocks.Selected", selected, Blocks.Count);
        }
    }

    // ---- commands ----------------------------------------------------------

    private async Task DoctorAsync() => await Guarded("Status.CheckingEnvironment", async () =>
    {
        var report = await Task.Run(TiaOpenness.Core.Environment.OpennessDoctor.Run);

        Append(Loc.Current["Log.EnvironmentHeader"]);
        Append($"{report.MachineName} / {report.UserName} / {(report.Is64BitProcess ? "x64" : "x86")}");
        foreach (var check in report.Checks)
        {
            Append($"[{check.Status}] {check.Title}: {check.Detail}");
            if (!string.IsNullOrWhiteSpace(check.Remedy)) Append($"        -> {check.Remedy}");
        }
        foreach (var install in report.Installations)
        {
            Append(Loc.Current.T("Log.Installed", install.Version, install.EngineeringDllPath));
        }

        if (report.CanRunOpenness) SetStatus("Status.EnvOk");
        else SetStatus("Status.EnvNotReady");
    });

    private async Task ConnectAsync() => await Guarded("Status.Connecting", async () =>
    {
        await ConnectCoreAsync(adoptOpenProject: true);
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
        SetStatus("Status.Connected", state.Mode, state.OpennessVersion);

        // Attaching to a running TIA inherits whatever project it already had open.
        if (adoptOpenProject && state.OpenProject is not null)
        {
            ProjectPath = state.OpenProject.Path ?? string.Empty;
            ProjectName = state.OpenProject.Name ?? string.Empty;
            Append(Loc.Current.T("Log.Attached", state.OpenProject.Name));
            await LoadDevicesAsync();
        }
    }

    private void SetOpennessVersion(string? version)
    {
        _opennessVersion = string.IsNullOrWhiteSpace(version) ? null : "V" + version;
        Raise(nameof(OpennessBadge));
    }

    private async Task OpenProjectAsync() => await Guarded("Status.OpeningProject", async () =>
    {
        EnsureBridge();

        // Opening implies connecting, the way `tia devices --project ...` does on the command
        // line. Leaving the ordering to the operator only ever produced "Not connected. Call
        // session.connect first." - a rule the app knows and can follow itself.
        if (!IsConnected)
        {
            await ConnectCoreAsync(adoptOpenProject: false);
            SetStatus("Status.OpeningProject");
        }

        var project = await _client.OpenProjectAsync(ProjectPath);
        ProjectName = project.Name ?? string.Empty;
        Append(Loc.Current.T("Log.Opened", project.Name, project.Path));
        SetStatus("Status.ProjectOpen", project.Name);
        await LoadDevicesAsync();
    });

    private async Task LoadDevicesAsync()
    {
        var devices = await _client.ListDevicesAsync();
        Devices.Clear();
        foreach (var device in devices) Devices.Add(device);

        DeviceTree.Clear();
        foreach (var node in DeviceNode.Build(devices, ProjectName)) DeviceTree.Add(node);

        Append(Loc.Current.T("Log.DeviceCount", devices.Count));

        // Pick a device and load its blocks as one awaited step. Letting the property setter
        // start that load meant a fire-and-forget task racing the rest of this method, and on a
        // real project - where reading blocks takes seconds - it looked like nothing had loaded
        // until Reload was pressed.
        _suppressDeviceLoad = true;
        try
        {
            SelectedDevice = Devices.FirstOrDefault(d => d.Category == "Plc") ?? Devices.FirstOrDefault();
        }
        finally
        {
            _suppressDeviceLoad = false;
        }

        if (SelectedDevice is not null) await RefreshBlocksAsync();

        // Populate the version-control tab up front, so the feature is visibly absent on
        // projects without a VCI service before offering the related commands.
        await VcRefreshAsync();
    }

    private async Task RefreshBlocksAsync()
    {
        if (SelectedDevice is null) return;

        var deviceId = SelectedDevice.Id;
        await Guarded("Status.ReadingBlocks", async () =>
        {
            var blocks = await _client.ListBlocksAsync(deviceId);
            Blocks.Clear();
            foreach (var block in blocks)
            {
                var row = new BlockRow(block);
                row.PropertyChanged += OnRowChanged;
                Blocks.Add(row);
            }
            RebuildTree();
            SetStatus("Status.BlocksIn", blocks.Count, deviceId);
        }, deviceId);
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BlockRow.Selected)) return;

        Raise(nameof(SelectionSummary));

        // Folders show what their blocks add up to, so ticking one block can change the tick of
        // every folder above it.
        foreach (var node in BlockTree) node.Refresh();
    }

    private async Task ExportAsync() => await Guarded("Status.Exporting", async () =>
    {
        var selected = Blocks.Where(b => b.Selected).Select(b => b.Path).ToList();
        var format = SourceFormat ? ExportFormat.Source : ExportFormat.SimaticMl;

        var result = await _client.ExportBlocksAsync(SelectedDevice!.Id, selected, OutputDirectory, format);

        foreach (var item in result.Items.Where(i => !i.Succeeded))
        {
            Append(Loc.Current.T("Log.Failed", item.BlockPath, item.Error));
        }

        if (result.Failed > 0)
        {
            SetStatus("Status.ExportedFailed",
                result.Succeeded, result.Requested, result.OutputDirectory, result.Failed);
        }
        else
        {
            SetStatus("Status.Exported", result.Succeeded, result.Requested, result.OutputDirectory);
        }

        Append(Status);
    });

    private async Task ImportAsync() => await Guarded("Status.Importing", async () =>
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.Current["Dialog.Import.Title"],
            Filter = Loc.Current["Dialog.Import.Filter"],
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;

        var overwrite = MessageBox.Show(
            Loc.Current["Dialog.Import.Text"],
            Loc.Current["Dialog.Import.Caption"], MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (overwrite == MessageBoxResult.Cancel) return;

        var result = await _client.ImportBlocksAsync(
            SelectedDevice!.Id, dialog.FileNames, overwrite == MessageBoxResult.Yes);

        foreach (var item in result.Items.Where(i => !i.Succeeded))
        {
            Append(Loc.Current.T("Log.Failed", item.FilePath, item.Error));
        }

        SetStatus("Status.Imported", result.Succeeded, result.Requested);
        Append(Status);
        await RefreshBlocksAsync();
    });

    private async Task CompileAsync() => await Guarded("Status.Compiling", async () =>
    {
        var result = await _client.CompileAsync(SelectedDevice!.Id);

        foreach (var message in Flatten(result.Messages).Where(m => m.Severity != CompileSeverity.Information))
        {
            Append($"{message.Severity}: {message.Target} - {message.Description}");
        }

        SetStatus("Status.CompileResult",
            result.State, result.ErrorCount, result.WarningCount,
            result.Duration.TotalSeconds.ToString("F1", CultureInfo.CurrentCulture));

        Append(Status);
        await RefreshBlocksAsync();
    });

    private async Task InspectAsync() => await Guarded("Status.Inspecting", async () =>
    {
        var report = await _client.InspectAsync(SelectedDevice!.Id,
            string.IsNullOrWhiteSpace(NamePattern) ? null : NamePattern);

        Append(Loc.Current.T("Log.InspectionHeader", report.DeviceId));
        foreach (var group in report.Findings.GroupBy(f => f.RuleId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Append($"{group.Key} ({group.Count()})");
            foreach (var finding in group) Append($"    [{finding.Severity}] {finding.Target}: {finding.Message}");
        }

        SetStatus("Status.InspectResult", report.Findings.Count, report.BlocksScanned);
        Append(Status);
    });

    private async Task SaveAsync() => await Guarded("Status.SavingProject", async () =>
    {
        if (MessageBox.Show(
                Loc.Current["Dialog.Save.Text"], Loc.Current["Dialog.Save.Caption"],
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await _client.SaveProjectAsync();
        SetStatus("Status.ProjectSaved");
        Append(Status);
    });

    // ---- plumbing ----------------------------------------------------------

    /// <summary>
    /// Starts the bridge on first use rather than at construction, so the window opens even
    /// when the bridge is missing and the error lands in the log instead of a startup crash.
    /// </summary>
    private void EnsureBridge()
    {
        if (_started && _client.Bridge.IsRunning)
        {
            if (UseMock != _client.Bridge.IsMock) throw new InvalidOperationException("Restart Studio to change backend mode.");
            return;
        }

        _client.Start(forceMock: UseMock, opennessVersion: SelectedReleaseKey);
        _started = true;
        Raise(nameof(CanSelectRelease));
        Append(Loc.Current.T("Log.BridgeStarted", UseMock ? "mock" : "openness"));
    }

    private void SetStatus(string key, params object?[] args)
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

    private async Task Guarded(string workingKey, Func<Task> action, params object?[] workingArgs)
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

    private void OnProgress(TiaOpenness.Contracts.Rpc.ProgressPayload payload)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            ProgressMax = payload.Total;
            ProgressValue = payload.Current;
            SetStatusLiteral($"{payload.Operation} {payload.Current}/{payload.Total}: {payload.Message}");
        });
    }

    private void Append(string line)
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

    private bool FilterBlock(object item)
    {
        if (BlockFilter.Length == 0) return true;
        return item is BlockRow row
               && row.Path.IndexOf(BlockFilter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Rebuilds the tree from whatever the filter is showing, so a filtered tree contains the
    /// matching blocks and the folders that lead to them, and nothing else.
    /// </summary>
    private void RebuildTree()
    {
        BlockTree.Clear();
        foreach (var node in BlockNode.Build(BlocksView.Cast<BlockRow>())) BlockTree.Add(node);
        Raise(nameof(HasBlocks));
    }

    private static IEnumerable<CompileMessage> Flatten(IEnumerable<CompileMessage> messages)
    {
        foreach (var message in messages)
        {
            yield return message;
            if (message.Children is null) continue;
            foreach (var child in Flatten(message.Children)) yield return child;
        }
    }

    public void SelectAll(bool selected)
    {
        foreach (var row in BlocksView.Cast<BlockRow>()) row.Selected = selected;
    }

    public void BrowseProject()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.Current["Dialog.OpenProject.Title"],
            Filter = Loc.Current["Dialog.OpenProject.Filter"],
        };
        if (dialog.ShowDialog() == true) ProjectPath = dialog.FileName;
    }

    public void BrowseWorkspaceFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.Current["Dialog.Workspace.Title"] };
        if (dialog.ShowDialog() == true) NewWorkspaceFolder = dialog.FolderName;
    }

    public void BrowseOutput()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.Current["Dialog.Export.Title"] };
        if (dialog.ShowDialog() == true) OutputDirectory = dialog.FolderName;
    }

    // ---- version control ---------------------------------------------------

    /// <summary>
    /// Loads the VCI panel. If the project exposes no service, the panel disables itself
    /// rather than offering buttons that can only fail.
    /// </summary>
    private async Task VcRefreshAsync() => await Guarded("Status.ReadingVc", async () =>
    {
        EnsureBridge();
        VcSupported = await _client.VcSupportedAsync();

        Workspaces.Clear();
        VcStatusItems.Clear();

        if (!VcSupported)
        {
            SetStatus("Status.VcUnsupported");
            Append(Status);
            return;
        }

        foreach (var workspace in await _client.VcListWorkspacesAsync()) Workspaces.Add(workspace);
        SelectedWorkspace ??= Workspaces.FirstOrDefault();

        if (Workspaces.Count == 0)
        {
            SetStatus("Status.VcNoWorkspace");
            return;
        }

        SetStatus("Status.VcWorkspaces", Workspaces.Count);

        // Load the diff straight away: opening the tab should answer "what changed?" without
        // a second click, and it is read-only.
        await LoadVcStatusAsync();
    });

    /// <summary>
    /// Loads the selected object's diff. Failures land in the caption rather than as an error
    /// dialog: no Git, or a workspace folder that is not a repository, is a normal state for
    /// someone who has not set that up, not something that went wrong.
    /// </summary>
    private async Task LoadVcDiffAsync()
    {
        VcDiffLines.Clear();
        Raise(nameof(HasVcDiff));

        if (SelectedVcItem is null)
        {
            VcDiffCaption = string.Empty;
            return;
        }

        try
        {
            var diff = await _client.VcDiffAsync(SelectedWorkspace?.Name, SelectedVcItem.FilePath);

            if (!diff.Available)
            {
                VcDiffCaption = diff.Detail ?? string.Empty;
                return;
            }

            foreach (var line in diff.Lines) VcDiffLines.Add(line);

            VcDiffCaption = VcDiffLines.Count > 0
                ? SelectedVcItem.Name
                : Loc.Current.T("Vc.Diff.Unchanged", SelectedVcItem.Name);
        }
        catch (Exception ex)
        {
            VcDiffCaption = ex.Message;
        }
        finally
        {
            Raise(nameof(HasVcDiff));
        }
    }

    private async Task LoadVcStatusAsync()
    {
        var report = await _client.VcStatusAsync(SelectedWorkspace?.Name, changedOnly: !VcShowAll);

        VcStatusItems.Clear();
        foreach (var item in report.Items) VcStatusItems.Add(item);

        // Show the first change straight away. The question after a status is always "what
        // changed", and answering it should not need a second click.
        SelectedVcItem = VcStatusItems.FirstOrDefault();

        if (report.InSync) SetStatus("Status.VcInSync", report.Total);
        else SetStatus("Status.VcDiffer", report.Total, report.Differing);
    }

    private async Task VcCreateAsync() => await Guarded("Status.CreatingWorkspace", async () =>
    {
        var workspace = await _client.VcCreateWorkspaceAsync(NewWorkspaceName, NewWorkspaceFolder);
        Append(Loc.Current.T("Log.WorkspaceCreated", workspace.Name, workspace.RootPath));
        await VcRefreshAsync();
        SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Name == workspace.Name);
        SetStatus("Status.VcCreated");
    });

    private async Task VcMapAsync() => await Guarded("Status.MappingProject", async () =>
    {
        var result = await _client.VcMapProjectAsync(SelectedWorkspace?.Name, SelectedDevice?.Id, VcDryRun);

        foreach (var item in result.Items.Where(i => i.Outcome is "failed" or "unsupported"))
        {
            Append($"{item.Outcome}: {item.Target} - {item.Error}");
        }

        if (result.DryRun)
        {
            SetStatus("Status.VcMapDry", result.Mapped, result.AlreadyMapped, result.Unsupported);
        }
        else
        {
            SetStatus("Status.VcMapApplied",
                result.Mapped, result.AlreadyMapped, result.Unsupported, result.Failed);
        }

        Append(Status);
        if (!result.DryRun) await VcStatusAsync();
    });

    private async Task VcStatusAsync() => await Guarded("Status.ComparingWorkspace", async () =>
    {
        await LoadVcStatusAsync();
        Append(Status);
    });

    private async Task VcPushAsync() => await VcSyncAsync(SyncDirection.ProjectToWorkspace);

    private async Task VcPullAsync()
    {
        if (!VcDryRun && MessageBox.Show(
                Loc.Current["Dialog.Pull.Text"], Loc.Current["Dialog.Pull.Caption"],
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }
        await VcSyncAsync(SyncDirection.WorkspaceToProject);
    }

    private async Task VcSyncAsync(SyncDirection direction)
        => await Guarded("Status.Synchronizing", async () =>
    {
        var result = await _client.VcSyncAsync(SelectedWorkspace?.Name, direction, VcDryRun);

        foreach (var item in result.Items.Where(i => i.Error is not null))
        {
            Append(Loc.Current.T("Log.Failed", item.Name, item.Error));
        }

        if (result.DryRun)
        {
            SetStatus("Status.VcSyncDry", result.Synchronized, direction, result.SkippedEqual);
        }
        else
        {
            SetStatus("Status.VcSyncApplied", result.Synchronized, result.Failed, result.SkippedEqual);
        }

        Append(Status);

        if (!result.DryRun)
        {
            Append(direction == SyncDirection.ProjectToWorkspace
                ? Loc.Current.T("Log.CommitHint", result.RootPath)
                : Loc.Current["Log.PullHint"]);
            await VcStatusAsync();
        }
    }, direction);

    /// <summary>
    /// Applies command-line startup options: <c>--mock</c> to use the synthetic backend,
    /// <c>--project &lt;path&gt;</c> to open one immediately and <c>--tab</c> to land on a view.
    /// Lets the app be demonstrated, and screenshotted, without a TIA Portal installation.
    /// </summary>
    public async Task ApplyStartupAsync(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--mock", StringComparison.OrdinalIgnoreCase)))
        {
            UseMock = true;
        }

        var tab = Array.FindIndex(args, a => string.Equals(a, "--tab", StringComparison.OrdinalIgnoreCase));
        if (tab >= 0 && tab + 1 < args.Length)
        {
            View = args[tab + 1].ToLowerInvariant() switch
            {
                "vc" or "version-control" => MainView.VersionControl,
                "log" => MainView.Log,
                _ => MainView.Blocks,
            };
        }

        var index = Array.FindIndex(args, a => string.Equals(a, "--project", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length) ProjectPath = args[index + 1];
        else if (UseMock) ProjectPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TiaOpennessStudioMock", "Line.ap21");

        if (!UseMock) return;

        await ConnectAsync();
        if (ProjectPath.Length > 0) await OpenProjectAsync();
    }

    public void Dispose()
    {
        Loc.Current.LanguageChanged -= OnLanguageChanged;
        _client.Dispose();
    }
}
