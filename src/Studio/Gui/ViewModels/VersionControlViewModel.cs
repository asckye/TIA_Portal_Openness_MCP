using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;

namespace TiaOpenness.Gui.ViewModels;

/// <summary>Owns workspace mapping, status, diff and synchronization.</summary>
public sealed class VersionControlViewModel : ObservableObject, IDisposable
{
    private readonly IStudioClient _client;
    private readonly IDialogService _dialogs;
    private readonly WorkbenchActivity _activity;
    private readonly Action _ensureBridge;
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
        Action ensureBridge, Func<string?> selectedDeviceId)
    {
        _client = client;
        _dialogs = dialogs;
        _activity = activity;
        _ensureBridge = ensureBridge;
        _selectedDeviceId = selectedDeviceId;
        VcStatusItems.CollectionChanged += (_, _) => Raise(nameof(HasVcItems));
        Loc.Current.LanguageChanged += OnLanguageChanged;
        VcRefresh = new AsyncCommand(VcRefreshAsync);
        VcCreate = new AsyncCommand(VcCreateAsync,
            () => VcSupported && NewWorkspaceName.Length > 0 && NewWorkspaceFolder.Length > 0);
        VcMap = new AsyncCommand(VcMapAsync, () => VcSupported && SelectedWorkspace is not null);
        VcStatus = new AsyncCommand(VcStatusAsync, () => VcSupported && SelectedWorkspace is not null);
        VcPush = new AsyncCommand(VcPushAsync, () => VcSupported && SelectedWorkspace is not null);
        VcPull = new AsyncCommand(VcPullAsync, () => VcSupported && SelectedWorkspace is not null);
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

    private void RaiseVcCommands()
    {
        foreach (var command in new[] { VcCreate, VcMap, VcStatus, VcPush, VcPull })
        {
            command.RaiseCanExecuteChanged();
        }
    }

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

    /// <summary>
    /// Loads the VCI panel. If the project exposes no service, the panel disables itself
    /// rather than offering buttons that can only fail.
    /// </summary>
    public async Task VcRefreshAsync() => await _activity.Guarded("Status.ReadingVc", async () =>
    {
        _ensureBridge();
        VcSupported = await _client.VcSupportedAsync();

        Workspaces.Clear();
        VcStatusItems.Clear();

        if (!VcSupported)
        {
            _activity.SetStatus("Status.VcUnsupported");
            _activity.Append(_activity.Status);
            return;
        }

        foreach (var workspace in await _client.VcListWorkspacesAsync()) Workspaces.Add(workspace);
        SelectedWorkspace ??= Workspaces.FirstOrDefault();

        if (Workspaces.Count == 0)
        {
            _activity.SetStatus("Status.VcNoWorkspace");
            return;
        }

        _activity.SetStatus("Status.VcWorkspaces", Workspaces.Count);

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

        if (report.InSync) _activity.SetStatus("Status.VcInSync", report.Total);
        else _activity.SetStatus("Status.VcDiffer", report.Total, report.Differing);
    }

    private async Task VcCreateAsync() => await _activity.Guarded("Status.CreatingWorkspace", async () =>
    {
        var workspace = await _client.VcCreateWorkspaceAsync(NewWorkspaceName, NewWorkspaceFolder);
        _activity.Append(Loc.Current.T("Log.WorkspaceCreated", workspace.Name, workspace.RootPath));
        await VcRefreshAsync();
        SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Name == workspace.Name);
        _activity.SetStatus("Status.VcCreated");
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
            _activity.SetStatus("Status.VcMapApplied",
                result.Mapped, result.AlreadyMapped, result.Unsupported, result.Failed);
        }

        _activity.Append(_activity.Status);
        if (!result.DryRun) await VcStatusAsync();
    });

    private async Task VcStatusAsync() => await _activity.Guarded("Status.ComparingWorkspace", async () =>
    {
        await LoadVcStatusAsync();
        _activity.Append(_activity.Status);
    });

    private async Task VcPushAsync() => await VcSyncAsync(SyncDirection.ProjectToWorkspace);
    private async Task VcPullAsync()
    {
        if (!VcDryRun && _dialogs.ShowMessage(
                Loc.Current["Dialog.Pull.Text"], Loc.Current["Dialog.Pull.Caption"],
                DialogButtons.OKCancel, DialogIcon.Warning) != DialogResult.OK)
        {
            return;
        }
        await VcSyncAsync(SyncDirection.WorkspaceToProject);
    }

    private async Task VcSyncAsync(SyncDirection direction)
        => await _activity.Guarded("Status.Synchronizing", async () =>
    {
        var result = await _client.VcSyncAsync(SelectedWorkspace?.Name, direction, VcDryRun);

        foreach (var item in result.Items.Where(i => i.Error is not null))
        {
            _activity.Append(Loc.Current.T("Log.Failed", item.Name, item.Error));
        }

        if (result.DryRun)
        {
            _activity.SetStatus("Status.VcSyncDry", result.Synchronized, direction, result.SkippedEqual);
        }
        else
        {
            _activity.SetStatus("Status.VcSyncApplied", result.Synchronized, result.Failed, result.SkippedEqual);
        }

        _activity.Append(_activity.Status);

        if (!result.DryRun)
        {
            _activity.Append(direction == SyncDirection.ProjectToWorkspace
                ? Loc.Current.T("Log.CommitHint", result.RootPath)
                : Loc.Current["Log.PullHint"]);
            await VcStatusAsync();
        }
    }, direction);

    public void BrowseWorkspaceFolder()
    {
        var folder = _dialogs.OpenFolder(Loc.Current["Dialog.Workspace.Title"]);
        if (folder is not null) NewWorkspaceFolder = folder;
    }

    internal void ClearPresentation()
    {
        SelectedVcItem = null;
        SelectedWorkspace = null;
        Workspaces.Clear();
        VcStatusItems.Clear();
        VcDiffLines.Clear();
        VcSupported = false;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Raise(nameof(WorkspaceRootDisplay));

    public void Dispose() => Loc.Current.LanguageChanged -= OnLanguageChanged;
}
