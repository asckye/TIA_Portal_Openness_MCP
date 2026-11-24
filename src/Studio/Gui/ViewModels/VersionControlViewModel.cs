using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Versioning;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;

namespace TiaOpenness.Gui.ViewModels;

/// <summary>Owns workspace mapping, status, diff and synchronization.</summary>
public sealed class VersionControlViewModel : ObservableObject, IDisposable
{
    private readonly IVersionControlClient _client;
    private readonly IDialogService _dialogs;
    private readonly WorkbenchActivity _activity;
    private readonly Func<string?> _selectedDeviceId;
    private bool _vcSupported;
    private bool _vcDryRun = true;
    private bool _vcShowAll;
    private WorkspaceInfo? _selectedWorkspace;
    private MappedObjectInfo? _selectedVcItem;
    private string _vcDiffCaption = string.Empty;
    private string _newWorkspaceName = "git";
    private string _newWorkspaceFolder = string.Empty;

    public VersionControlViewModel(IStudioClient client, IDialogService dialogs, WorkbenchActivity activity,
        Action ensureBridge, Func<string?> selectedDeviceId, bool legacyAccess = true)
        : this(new LegacyVersionControlClient(client, ensureBridge, legacyAccess), dialogs, activity, selectedDeviceId) { }

    public VersionControlViewModel(IVersionControlClient client, IDialogService dialogs, WorkbenchActivity activity, Func<string?> selectedDeviceId)
    {
        _client = client;
        _dialogs = dialogs;
        _activity = activity;
        _selectedDeviceId = selectedDeviceId;
        VcStatusItems.CollectionChanged += (_, _) =>
        {
            Raise(nameof(HasVcItems)); Raise(nameof(ModifiedCount)); Raise(nameof(ModifiedLabel));
        };
        Loc.Current.LanguageChanged += OnLanguageChanged;
        VcRefresh = new AsyncCommand(VcRefreshAsync, () => _client.CanRead && !_activity.Busy);
        VcCreate = new AsyncCommand(VcCreateAsync,
            () => _client.CanWrite && !_activity.Busy && VcSupported && NewWorkspaceName.Length > 0 && NewWorkspaceFolder.Length > 0);
        VcMap = new AsyncCommand(VcMapAsync, () => _client.CanWrite && !_activity.Busy && VcSupported && SelectedWorkspace is not null);
        VcStatus = new AsyncCommand(VcStatusAsync, () => _client.CanRead && !_activity.Busy && VcSupported && SelectedWorkspace is not null);
        VcPush = new AsyncCommand(VcPushAsync, () => _client.CanWrite && !_activity.Busy && VcSupported && SelectedWorkspace is not null);
        VcPull = new AsyncCommand(VcPullAsync, () => _client.CanWrite && !_activity.Busy && VcSupported && SelectedWorkspace is not null);
    }

    public ObservableCollection<WorkspaceInfo> Workspaces { get; } = [];

    public ObservableCollection<MappedObjectInfo> VcStatusItems { get; } = [];

    /// <summary>The selected object's uncommitted change, as Git sees it.</summary>
    public ObservableCollection<DiffLine> VcDiffLines { get; } = [];

    public AsyncCommand VcRefresh { get; }
    public AsyncCommand VcCreate { get; }
    public AsyncCommand VcMap { get; }
    public AsyncCommand VcStatus { get; }
    public AsyncCommand VcPush { get; }
    public AsyncCommand VcPull { get; }

    internal bool CancelBridgeRead() => _client is McpVersionControlClient mcp && mcp.CancelRead();

    internal void RefreshCommands() => RaiseVcCommands();
    public bool IsReadOnly => _client.CanRead && !_client.CanWrite;
    public string AccessNotice
    {
        get
        {
            if (_client is not McpVersionControlClient { UsesFallback: true } session)
                return IsReadOnly ? Loc.Current["Vc.ReadOnlySession"] : "";
            if (session.BoundReleaseKey is { } release && !OpennessApi.VersionControl.Includes(release))
                return Loc.Current.T("Vc.Unavailable", TiaVersionCatalog.Get(release).DisplayName);
            return _client.CanRead ? Loc.Current["Vc.ReadOnlyBridge"] : Loc.Current["Vc.ReaderUnavailable"];
        }
    }

    private void RaiseVcCommands()
    {
        Raise(nameof(ShowCreateWorkspace)); Raise(nameof(AllMappingsLabel));
        Raise(nameof(IsReadOnly)); Raise(nameof(AccessNotice));
        foreach (var command in new[] { VcRefresh, VcCreate, VcMap, VcStatus, VcPush, VcPull })
        {
            command.RaiseCanExecuteChanged();
        }
    }

    private bool _hasStatus;
    public bool HasStatus { get => _hasStatus; private set => Set(ref _hasStatus, value); }
    public int ModifiedCount => VcStatusItems.Count(item => item.CompareState == VcCompareState.Unequal);
    public string ModifiedLabel => !HasStatus ? "" : ModifiedCount == 1
        ? Loc.Current["Project.OneModified"] : Loc.Current.T("Project.ModifiedCount", ModifiedCount);
    public bool HasVcItems => VcStatusItems.Count > 0;

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

    public string DiffHint => VcDiffCaption.Length > 0 ? VcDiffCaption : Loc.Current["Pages.NoDiff"];
    public string VcDiffCaption
    {
        get => _vcDiffCaption;
        private set { if (Set(ref _vcDiffCaption, value)) Raise(nameof(DiffHint)); }
    }

    private TimeSpan _syncElapsed;
    public string ElapsedLabel => SyncResult is { DryRun: false } ? Loc.Current.T("Project.SyncElapsed", _syncElapsed.TotalSeconds) : "";
    public sealed record SyncRow(string Name, string Outcome, string Error, string? RawOutcome);
    public IEnumerable<SyncRow> SyncRows => SyncResult is { } result ? result.Items.Select(item =>
        new SyncRow(item.Name, result.DryRun ? PreviewOutcome(item) : CompletedOutcome(item.Outcome), item.Error, item.Outcome)) : [];

    private static string CompletedOutcome(string? outcome) => outcome?.ToLowerInvariant() switch
    {
        "completed" or "synchronized" => Loc.Current["Project.SyncCompleted"],
        "failed" => Loc.Current["Project.SyncFailed"],
        "equal" or "skip" or "skipped" or "skipped-equal" => Loc.Current["Project.SyncSkipped"],
        _ => Loc.Current["Project.SyncOther"],
    };

    private string PreviewOutcome(SyncItem item)
    {
        switch (item.Outcome?.ToLowerInvariant())
        {
            case "create": case "created": case "would create": return "CREATE";
            case "update": case "updated": case "would update": return "UPDATE";
            case "skip": case "equal": case "skipped": case "skipped-equal": return "SKIP";
            case "planned": case "would sync projecttoworkspace":
                var matches = VcStatusItems.Where(row => row.Name == item.Name || row.FilePath == item.Name).Take(2).ToArray();
                if (matches.Length == 1)
                {
                    if (matches[0].CompareState == VcCompareState.WorkspaceFileMissing) return "CREATE";
                    if (matches[0].CompareState == VcCompareState.Unequal) return "UPDATE";
                }
                break;
        }
        return Loc.Current["Project.SyncAction"];
    }

    private bool _synchronizing;
    private SyncResult? _syncResult;
    public bool Synchronizing { get => _synchronizing; private set { if (Set(ref _synchronizing, value)) RefreshSyncState(); } }
    public SyncResult? SyncResult { get => _syncResult; private set { if (Set(ref _syncResult, value)) RefreshSyncState(); } }
    public bool ShowWorkspace => HasWorkspace && !Synchronizing && SyncResult == null;
    public bool ShowSyncResult => SyncResult != null && !Synchronizing;
    public string SyncTitle => Synchronizing ? Loc.Current["Project.Syncing"]
        : SyncResult?.DryRun == true ? Loc.Current["Project.SyncPreview"] + " · "
            + (SyncResult.Direction == SyncDirection.ProjectToWorkspace ? Loc.Current["Primer.ToWorkspace"] : Loc.Current["Primer.ToProject"])
            : Loc.Current["Project.Synced"];
    public string SyncCounts => SyncResult is { } r ? Loc.Current.T("Project.SyncCounts", r.Synchronized, r.SkippedEqual, r.Failed) : "";
    internal void CloseSyncResult() => SyncResult = null;
    private void RefreshSyncState()
    {
        Raise(nameof(SyncRows)); Raise(nameof(ElapsedLabel));
        Raise(nameof(ShowWorkspace)); Raise(nameof(ShowSyncResult)); Raise(nameof(SyncTitle)); Raise(nameof(SyncCounts));
    }

    public bool ShowCreateWorkspace => !HasWorkspace && VcSupported && _client.CanRead;
    private int? _reportedTotal;
    public int MappedCount => _reportedTotal ?? SelectedWorkspace?.MappedObjectCount ?? 0;
    public string AllMappingsLabel => HasWorkspace ? Loc.Current.T("Project.AllMappings", MappedCount) : Loc.Current["Primer.All"];
    public bool HasWorkspace => SelectedWorkspace != null;

    public WorkspaceInfo? SelectedWorkspace
    {
        get => _selectedWorkspace;
        set
        {
            if (!Set(ref _selectedWorkspace, value)) return;
            _reportedTotal = null;
            SyncResult = null;
            RefreshSyncState();
            HasStatus = false;
            SelectedVcItem = null;
            VcStatusItems.Clear();
            Raise(nameof(ModifiedLabel));
            Raise(nameof(WorkspaceRootDisplay));
            Raise(nameof(HasWorkspace));
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

    /// <summary>
    /// Loads the VCI panel. If the project exposes no service, the panel disables itself
    /// rather than offering buttons that can only fail.
    /// </summary>
    public Task VcRefreshAsync() => RefreshWorkspaceAsync(requested: true);

    private Task? _refresh;
    private int _projectGeneration;
    internal Task RefreshWorkspaceAsync(bool requested)
        => _refresh is { IsCompleted: false } ? _refresh : _refresh = ReadWorkspacesAsync(requested);

    private async Task ReadWorkspacesAsync(bool requested) => await _activity.Guarded("Status.ReadingVc", async () =>
    {
        if (!_client.CanRead) { ClearPresentation(); return; }
        int generation = _projectGeneration;
        bool supported = await _client.VcSupportedAsync();
        if (_projectGeneration != generation) return;
        VcSupported = supported;

        string? selectedName = SelectedWorkspace?.Name;
        Workspaces.Clear();
        SelectedWorkspace = null;
        HasStatus = false;
        VcStatusItems.Clear();

        if (!VcSupported)
        {
            _activity.SetStatus("Status.VcUnsupported", requested ? ToastKind.Warning : null);
            _activity.AppendStatus();
            return;
        }

        var workspaces = await _client.VcListWorkspacesAsync();
        if (_projectGeneration != generation) return;
        foreach (var workspace in workspaces) Workspaces.Add(workspace);
        SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Name == selectedName) ?? Workspaces.FirstOrDefault();

        if (Workspaces.Count == 0)
        {
            _activity.SetStatus("Status.VcNoWorkspace", requested ? ToastKind.Info : null);
            return;
        }

        _activity.SetStatus("Status.VcWorkspaces", kind: null, Workspaces.Count);

        // Load the diff straight away: opening the tab should answer "what changed?" without
        // a second click, and it is read-only.
        await LoadVcStatusAsync(requested: false);
    });

    /// <summary>
    /// Loads the selected object's diff. The caption describes the selected file, including an unavailable diff.
    /// </summary>
    private async Task LoadVcDiffAsync()
    {
        VcDiffLines.Clear();
        Raise(nameof(HasVcDiff));

        var item = SelectedVcItem;
        var workspace = SelectedWorkspace;
        if (item is null)
        {
            VcDiffCaption = string.Empty;
            return;
        }

        try
        {
            var diff = await _client.VcDiffAsync(workspace?.Name, item.FilePath);
            if (item != SelectedVcItem || workspace != SelectedWorkspace) return;

            if (!diff.Available)
            {
                VcDiffCaption = diff.Detail ?? string.Empty;
                return;
            }

            foreach (var line in diff.Lines) VcDiffLines.Add(line);

            VcDiffCaption = VcDiffLines.Count > 0
                ? item.Name
                : Loc.Current.T("Vc.Diff.Unchanged", item.Name);
        }
        catch (Exception ex)
        {
            if (item != SelectedVcItem || workspace != SelectedWorkspace) return;
            VcDiffCaption = item.Name;
            _activity.Notify(ToastKind.Error, LocalizedText.Literal(ex.Message), "workspace-diff");
        }
        finally
        {
            Raise(nameof(HasVcDiff));
        }
    }

    private async Task LoadVcStatusAsync(bool requested)
    {
        var workspace = SelectedWorkspace;
        HasStatus = false;
        Raise(nameof(ModifiedLabel));
        var report = await _client.VcStatusAsync(workspace?.Name, changedOnly: !VcShowAll);
        if (workspace != SelectedWorkspace) return;
        _reportedTotal = report.Total;
        Raise(nameof(MappedCount)); Raise(nameof(AllMappingsLabel));
        HasStatus = true;

        VcStatusItems.Clear();
        foreach (var item in report.Items) VcStatusItems.Add(item);

        // Show the first change straight away. The question after a status is always "what
        // changed", and answering it should not need a second click.
        SelectedVcItem = VcStatusItems.FirstOrDefault();

        if (report.InSync) _activity.SetStatus("Status.VcInSync", requested ? ToastKind.Info : null, report.Total);
        else _activity.SetStatus("Status.VcDiffer", requested ? ToastKind.Info : null, report.Total, report.Differing);
    }

    private async Task VcCreateAsync() => await _activity.Guarded("Status.CreatingWorkspace", async () =>
    {
        var workspace = await _client.VcCreateWorkspaceAsync(NewWorkspaceName, NewWorkspaceFolder);
        _activity.AppendLocalized("Log.WorkspaceCreated", workspace.Name, workspace.RootPath);
        await RefreshWorkspaceAsync(requested: false);
        SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Name == workspace.Name);
        _activity.SetStatus("Status.VcCreated", ToastKind.Success);
    });

    private async Task VcMapAsync() => await _activity.Guarded("Status.MappingProject", async () =>
    {
        var result = await _client.VcMapProjectAsync(SelectedWorkspace?.Name, _selectedDeviceId(), VcDryRun);

        foreach (var item in result.Items.Where(i => i.Outcome is "failed" or "unsupported"))
        {
            _activity.Append($"{item.Outcome}: {item.Target} - {item.Error}");
        }

        if (result.DryRun)
        {
            _activity.SetStatus("Status.VcMapDry", result.Mapped, result.AlreadyMapped, result.Unsupported);
        }
        else
        {
            _activity.SetStatus("Status.VcMapApplied", result.Failed > 0 ? ToastKind.Error : ToastKind.Success,
                result.Mapped, result.AlreadyMapped, result.Unsupported, result.Failed);
        }

        _activity.AppendStatus();
        if (!result.DryRun) await CompareWorkspaceAsync(requested: false);
    });

    private Task VcStatusAsync() => CompareWorkspaceAsync(requested: true);

    private async Task CompareWorkspaceAsync(bool requested) => await _activity.Guarded("Status.ComparingWorkspace", async () =>
    {
        await LoadVcStatusAsync(requested);
        _activity.AppendStatus();
    });

    private async Task VcPushAsync() => await VcSyncAsync(SyncDirection.ProjectToWorkspace);
    private async Task VcPullAsync()
    {
        if (!VcDryRun && _dialogs.ShowMessage(
                Loc.Current["Dialog.Pull.Text"], Loc.Current["Dialog.Pull.Caption"],
                DialogButtons.OKCancel, DialogIcon.Warning, new(ConfirmationAction.OverwriteProject)) != DialogResult.OK)
        {
            return;
        }
        await VcSyncAsync(SyncDirection.WorkspaceToProject);
    }

    private async Task VcSyncAsync(SyncDirection direction)
        => await _activity.Guarded("Status.Synchronizing", async () =>
    {
        var workspace = SelectedWorkspace;
        bool preview = VcDryRun;
        SyncResult = null;
        Synchronizing = !preview;
        SyncResult result;
        long start = Stopwatch.GetTimestamp();
        try { result = await _client.VcSyncAsync(workspace?.Name, direction, preview); }
        finally { Synchronizing = false; }
        if (SelectedWorkspace != workspace) return;
        _syncElapsed = Stopwatch.GetElapsedTime(start);
        SyncResult = result;

        foreach (var item in result.Items.Where(i => i.Error is not null))
        {
            _activity.AppendLocalized("Log.Failed", item.Name, item.Error);
        }

        if (result.DryRun)
        {
            _activity.SetStatus("Status.VcSyncDry", result.Synchronized, direction, result.SkippedEqual);
        }
        else
        {
            _activity.SetStatus("Status.VcSyncApplied", result.Failed > 0 ? ToastKind.Error : ToastKind.Success,
                result.Synchronized, result.Failed, result.SkippedEqual);
        }

        _activity.AppendStatus();

        if (!result.DryRun)
        {
            _activity.Append(direction == SyncDirection.ProjectToWorkspace
                ? Loc.Current.T("Log.CommitHint", result.RootPath)
                : Loc.Current["Log.PullHint"]);
            await CompareWorkspaceAsync(requested: false);
        }
    }, direction);

    public void BrowseWorkspaceFolder()
    {
        var folder = _dialogs.OpenFolder(Loc.Current["Dialog.Workspace.Title"]);
        if (folder is not null) NewWorkspaceFolder = folder;
    }

    internal void ClearPresentation()
    {
        _projectGeneration++;
        _refresh = null;
        SelectedVcItem = null;
        SelectedWorkspace = null;
        Workspaces.Clear();
        VcStatusItems.Clear();
        VcDiffLines.Clear();
        VcSupported = false;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Raise(nameof(DiffHint)); Raise(nameof(AllMappingsLabel)); Raise(nameof(AccessNotice));
        Raise(nameof(WorkspaceRootDisplay)); Raise(nameof(ModifiedLabel)); RefreshSyncState();
    }

    public void Dispose() { Loc.Current.LanguageChanged -= OnLanguageChanged; _client.Dispose(); }
}
