using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;

namespace TiaOpenness.Gui.ViewModels;

public enum ProjectDocument { Render, Compile, Tags, Interface }

/// <summary>Owns devices, block selection and engineering operations.</summary>
public sealed partial class EngineeringViewModel : ObservableObject, IDisposable
{
    private readonly IProjectClient _client;
    private readonly IDialogService _dialogs;
    private readonly WorkbenchActivity _activity;
    private string _outputDirectory = string.Empty;
    private string _blockFilter = string.Empty;
    private string _namePattern = "^(OB|FB|FC|DB|UDT)_";
    private DeviceInfo? _selectedDevice;
    private bool _sourceFormat;
    private bool _suppressDeviceLoad;
    private bool _loadDeviceWhenIdle;
    private Task _blockLoad = Task.CompletedTask;
    private DeviceNode? _activeNode;
    private int _readGeneration;
    private int _tagGeneration;
    private IReadOnlyList<TagInfo> _plcTags = [];
    private LocalizedText _tagStatus = LocalizedText.Empty;
    private string _tagTable = "";
    public string TagTable { get => _tagTable; set => Set(ref _tagTable, value); }
    private ProjectDocument _document;
    public ProjectDocument Document { get => _document; set => Set(ref _document, value); }
    private BlockRow? _focusedBlock;
    public BlockRow? FocusedBlock
    {
        get => _focusedBlock;
        set
        {
            if (_focusedBlock == value) return;
            if (_focusedBlock != null) _focusedBlock.IsFocused = false;
            if (value != null) value.IsFocused = true;
            Set(ref _focusedBlock, value);
            ResetSource();
        }
    }
    public IReadOnlyList<TagInfo> PlcTags { get => _plcTags; private set => Set(ref _plcTags, value); }
    public string TagStatus => _tagStatus.Resolve();
    public bool CanReadTags => !_activity.Busy && SelectedDevice?.Category == "Plc";

    public async Task ReadTagsAsync()
    {
        if (SelectedDevice is not { Category: "Plc" } device) return;
        int generation = _tagGeneration;
        await _blockLoad;
        if (generation != _tagGeneration || !CanReadTags) return;
        await _activity.Guarded("Project.LoadingTags", async () =>
        {
            PlcTags = [];
            TagCount = null;
            Set(ref _tagStatus, LocalizedText.Working("Project.LoadingTags"), nameof(TagStatus));
            try
            {
                var tags = await _client.ListPlcTagsAsync(device.Id, TagTable, includeUnits: true);
                if (generation != _tagGeneration) return;
                PlcTags = tags;
                TagCount = tags.Count;
                Set(ref _tagStatus, tags.Count == 0 ? LocalizedText.Key("Project.EmptyTags") : LocalizedText.Empty, nameof(TagStatus));
                _activity.SetStatus("Project.TagsRead", kind: null, tags.Count);
            }
            catch (Exception error)
            {
                if (generation != _tagGeneration) return;
                Set(ref _tagStatus, LocalizedText.Literal(error.Message), nameof(TagStatus));
                throw;
            }
        });
    }

    public EngineeringViewModel(IProjectClient client, IDialogService dialogs, WorkbenchActivity activity)
    {
        _client = client;
        _dialogs = dialogs;
        _activity = activity;
        Blocks = new ObservableCollection<BlockRow>();
        BlocksView = CollectionViewSource.GetDefaultView(Blocks);
        BlocksView.Filter = FilterBlock;
        Devices.CollectionChanged += (_, _) => Raise(nameof(HasDevices));
        Blocks.CollectionChanged += OnBlocksChanged;
        Loc.Current.LanguageChanged += OnLanguageChanged;
        _activity.PropertyChanged += OnActivityChanged;
        ((INotifyCollectionChanged)_activity.Entries).CollectionChanged += OnResultEntry;

        Refresh = new AsyncCommand(RefreshBlocksAsync, () => !_activity.Busy && SelectedDevice is not null);
        Export = Command(ExportAsync, () => !_activity.Busy && SelectedDevice is not null && OutputDirectory.Length > 0);
        Import = Command(ImportAsync, () => !_activity.Busy && SelectedDevice is not null);
        Compile = new AsyncCommand(CompileAsync, () => !_activity.Busy && SelectedDevice is not null);
        Inspect = new AsyncCommand(InspectAsync, () => !_activity.Busy && SelectedDevice is not null);
        Save = new AsyncCommand(SaveAsync, () => !_activity.Busy && SelectedDevice is not null);
    }

    public ObservableCollection<DeviceInfo> Devices { get; } = [];

    public ObservableCollection<BlockRow> Blocks { get; }

    public ICollectionView BlocksView { get; }

    /// <summary>The active PLC’s unfiled blocks, user folders and other software categories.</summary>
    public ObservableCollection<BlockNode> BlockTree { get; } = [];

    /// <summary>The devices as TIA shows them: the project, its device groups, then the devices.</summary>
    public ObservableCollection<DeviceNode> DeviceTree { get; } = [];
    public IEnumerable<DeviceNode> ProjectDevices => DeviceTree.SelectMany(root => root.Children);

    public AsyncCommand Refresh { get; }
    private AsyncCommand Command(Func<Task> execute, Func<bool> enabled) => _client is McpProjectClient
        ? new McpPanelCommand(execute, enabled) : new AsyncCommand(execute, enabled);
    [PrefillTarget(Executes = "mcp")]
    public AsyncCommand Export { get; }
    [PrefillTarget(Executes = "mcp")]
    public AsyncCommand Import { get; }
    private string _importPathsText = "";
    private bool _importOverwrite;
    public string ImportPathsText { get => _importPathsText; set => Set(ref _importPathsText, value); }
    public bool ImportOverwrite { get => _importOverwrite; set => Set(ref _importOverwrite, value); }
    private CompileResult? _lastCompile;
    public CompileResult? LastCompile
    {
        get => _lastCompile;
        private set
        {
            if (!Set(ref _lastCompile, value)) return;
            Raise(nameof(CompileSummary));
            Raise(nameof(DiagnosticCount));
            Raise(nameof(CompilerMessages));
        }
    }

    public AsyncCommand Compile { get; }
    public AsyncCommand Inspect { get; }
    public AsyncCommand Save { get; }

    public string OutputDirectory
    {
        get => _outputDirectory;
        set { if (Set(ref _outputDirectory, value)) Export.RaiseCanExecuteChanged(); }
    }

    public string NamePattern { get => _namePattern; set => Set(ref _namePattern, value); }

    public bool SourceFormat { get => _sourceFormat; set => Set(ref _sourceFormat, value); }

    public bool HasDevices => Devices.Count > 0;

    public bool HasBlocks => Blocks.Count > 0;
    internal string? LoadedDeviceId { get; private set; }

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

    public DeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!Set(ref _selectedDevice, value)) return;
            Raise(nameof(CanReadTags));
            _readGeneration++;
            _tagGeneration++;
            Document = ProjectDocument.Render;
            FocusedBlock = null;
            PlcTags = [];
            Set(ref _tagStatus, LocalizedText.Empty, nameof(TagStatus));
            TagCount = null;
            LastCompile = null;
            TagTable = "";
            LoadedDeviceId = null;
            foreach (var row in Blocks) row.PropertyChanged -= OnRowChanged;
            Blocks.Clear();
            BlockTree.Clear();
            if (_activeNode != null)
            {
                _activeNode.IsActive = false;
                _activeNode.HasSafetyBlocks = false;
                _activeNode.IsExpanded = false;
                _activeNode.ShowBlocks([]);
            }
            _activeNode = DeviceTree.SelectMany(root => root.DeviceNodes()).FirstOrDefault(node => node.Device == value);
            if (_activeNode != null) { _activeNode.IsActive = true; _activeNode.IsExpanded = true; }
            foreach (var command in new[] { Refresh, Export, Import, Compile, Inspect, Save })
            {
                command.RaiseCanExecuteChanged();
            }
            // A device picked in the list loads its blocks. Suppressed while the app selects one
            // itself, where the caller awaits the load instead of racing this fire-and-forget one.
            _loadDeviceWhenIdle = value is not null && !_suppressDeviceLoad && _activity.Busy;
            if (value is not null && !_suppressDeviceLoad && !_loadDeviceWhenIdle) _ = RefreshBlocksAsync();
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

    private void OnBlocksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Raise(nameof(HasBlocks));
        Raise(nameof(SelectionSummary));
    }

    public async Task LoadDevicesAsync(string projectName)
    {
        var devices = await _client.ListDevicesAsync();
        Devices.Clear();
        foreach (var device in devices) Devices.Add(device);

        DeviceTree.Clear();
        foreach (var node in DeviceNode.Build(devices, projectName)) DeviceTree.Add(node);
        Raise(nameof(ProjectDevices));

        _activity.AppendLocalized("Log.DeviceCount", devices.Count);

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
    }

    private Task RefreshBlocksAsync()
    {
        _loadDeviceWhenIdle = false;
        return _blockLoad = ReadBlocksAsync();
    }

    private async Task ReadBlocksAsync()
    {
        if (SelectedDevice is null) return;

        var deviceId = SelectedDevice.Id;
        int request = ++_readGeneration;
        LoadedDeviceId = null;
        await _activity.Guarded("Status.ReadingBlocks", async () =>
        {
            var blocks = await _client.ListBlocksAsync(deviceId);
            if (request != _readGeneration) return;
            string? focusedPath = FocusedBlock?.Path;
            foreach (var row in Blocks) row.PropertyChanged -= OnRowChanged;
            Blocks.Clear();
            foreach (var block in blocks)
            {
                var row = new BlockRow(block);
                row.PropertyChanged += OnRowChanged;
                Blocks.Add(row);
            }
            FocusedBlock = focusedPath == null ? null : Blocks.FirstOrDefault(row => row.Path == focusedPath);
            LoadedDeviceId = deviceId;
            RebuildTree();
            _activity.SetStatus("Status.BlocksIn", kind: null, blocks.Count, deviceId);
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

    private async Task ExportAsync() => await _activity.Guarded("Status.Exporting", async () =>
    {
        var selected = Blocks.Where(b => b.Selected).Select(b => b.Path).ToList();
        var format = SourceFormat ? ExportFormat.Source : ExportFormat.SimaticMl;

        var result = await _client.ExportBlocksAsync(SelectedDevice!.Id, selected, OutputDirectory, format);

        foreach (var item in result.Items.Where(i => !i.Succeeded))
        {
            _activity.AppendLocalized("Log.Failed", item.BlockPath, item.Error);
        }

        if (result.Failed > 0)
        {
            _activity.SetStatus("Status.ExportedFailed", ToastKind.Error,
                result.Succeeded, result.Requested, result.OutputDirectory, result.Failed);
        }
        else
        {
            _activity.SetStatus("Status.Exported", ToastKind.Success, result.Succeeded, result.Requested, result.OutputDirectory);
        }

        _activity.AppendStatus();
    });

    private async Task ImportAsync() => await _activity.Guarded("Status.Importing", async () =>
    {
        var files = string.IsNullOrWhiteSpace(ImportPathsText)
            ? _dialogs.OpenFiles(Loc.Current["Dialog.Import.Title"], Loc.Current["Dialog.Import.Filter"], multiselect: true)
            : ImportPathsText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (files is null) return;

        var overwrite = !string.IsNullOrWhiteSpace(ImportPathsText) ? (ImportOverwrite ? DialogResult.Yes : DialogResult.No) : _dialogs.ShowMessage(
            Loc.Current["Dialog.Import.Text"], Loc.Current["Dialog.Import.Caption"],
            DialogButtons.YesNoCancel, DialogIcon.Question, new(ConfirmationAction.ImportBlocks));
        if (overwrite == DialogResult.Cancel) return;

        var result = await _client.ImportBlocksAsync(
            SelectedDevice!.Id, files, overwrite == DialogResult.Yes);

        foreach (var item in result.Items.Where(i => !i.Succeeded))
        {
            _activity.AppendLocalized("Log.Failed", item.FilePath, item.Error);
        }

        _activity.SetStatus("Status.Imported", result.Succeeded < result.Requested ? ToastKind.Error : ToastKind.Success, result.Succeeded, result.Requested);
        _activity.AppendStatus();
        await RefreshBlocksAsync();
    });

    private async Task CompileAsync() => await _activity.Guarded("Status.Compiling", async () =>
    {
        var result = await _client.CompileAsync(SelectedDevice!.Id);
        LastCompile = result;

        foreach (var message in Flatten(result.Messages).Where(m => m.Severity != CompileSeverity.Information))
        {
            _activity
                .AppendDiagnostic(message.Target, message.Description, message.Severity == CompileSeverity.Error ? WorkbenchActivity.Severity.Error
                    : WorkbenchActivity.Severity.Warning);
        }

        _activity.SetStatus("Status.CompileResult", result.ErrorCount > 0 || result.State == "Error" ? ToastKind.Error
            : result.WarningCount > 0 || result.State == "Warning" ? ToastKind.Warning : ToastKind.Success,
            result.State, result.ErrorCount, result.WarningCount,
            result.Duration.TotalSeconds.ToString("F1", CultureInfo.CurrentCulture));

        _activity.AppendStatus();
        await RefreshBlocksAsync();
    });

    private async Task InspectAsync() => await _activity.Guarded("Status.Inspecting", async () =>
    {
        var report = await _client.InspectAsync(SelectedDevice!.Id,
            string.IsNullOrWhiteSpace(NamePattern) ? null : NamePattern);
        _activity.AppendLocalized("Log.InspectionHeader", report.DeviceId);
        foreach (var group in report.Findings.GroupBy(f => f.RuleId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            _activity.AppendRule(group.Key, group.Count());
            foreach (var finding in group) _activity.Append($"    [{finding.Severity}] {finding.Target}: {finding.Message}");
        }

        var severity = report.Findings.Any(f => f.Severity == Contracts.Models.CheckStatus.Fail) ? ToastKind.Error
            : report.Findings.Any(f => f.Severity == Contracts.Models.CheckStatus.Warn) ? ToastKind.Warning : ToastKind.Success;
        _activity.SetStatus("Status.InspectResult", severity, report.Findings.Count, report.BlocksScanned);
        _activity.AppendStatus();
    });

    private async Task SaveAsync() => await _activity.Guarded("Status.SavingProject", async () =>
    {
        if (_dialogs.ShowMessage(
                Loc.Current["Dialog.Save.Text"], Loc.Current["Dialog.Save.Caption"],
                DialogButtons.OKCancel, DialogIcon.Warning, new(ConfirmationAction.SaveProject)) != DialogResult.OK)
        {
            return;
        }

        await _client.SaveProjectAsync();
        _activity.SetStatus("Status.ProjectSaved", ToastKind.Success);
        _activity.AppendStatus();
    });

    private bool FilterBlock(object item)
    {
        if (BlockFilter.Length == 0) return true;
        return item is BlockRow row
               && (Matches(row.Path) || Matches(row.Name) || Matches(row.Subtitle) || Matches(SelectedDevice?.Name) || Matches(SelectedDevice?.DisplayName));
    }

    /// <summary>
    /// Rebuilds the tree from whatever the filter is showing, so a filtered tree contains the
    /// matching blocks and the folders that lead to them, and nothing else.
    /// </summary>
    private bool Matches(string? value) => value?.Contains(BlockFilter, StringComparison.OrdinalIgnoreCase) == true;

    private void RebuildTree()
    {
        BlockTree.Clear();
        foreach (var group in BlockNode.Build(BlocksView.Cast<BlockRow>()).SelectMany(node => node.ExplorerGroups())) BlockTree.Add(group);
        if (_activeNode != null)
        {
            _activeNode.HasSafetyBlocks = Blocks.Any(row => row.Language.StartsWith("F-", StringComparison.OrdinalIgnoreCase));
            _activeNode.ShowBlocks(BlockTree);
        }
        foreach (var device in ProjectDevices.SelectMany(node => node.DeviceNodes()))
            device.MatchesFilter = BlockFilter.Length == 0 || Matches(device.Name) || Matches(device.Label) || device.IsActive && BlockTree.Count > 0;
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

    public void BrowseOutput()
    {
        var folder = _dialogs.OpenFolder(Loc.Current["Dialog.Export.Title"]);
        if (folder is not null) OutputDirectory = folder;
    }

    internal void ClearPresentation()
    {
        LastCompile = null;
        LoadedDeviceId = null;
        SelectedDevice = null;
        Devices.Clear();
        foreach (var row in Blocks) row.PropertyChanged -= OnRowChanged;
        Blocks.Clear();
        BlockTree.Clear();
        DeviceTree.Clear();
        Raise(nameof(ProjectDevices));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Raise(nameof(TagStatus));
        Raise(nameof(SourceStatus));
        Raise(nameof(LineCountLabel));
        Raise(nameof(CompileSummary));
        Raise(nameof(SelectionSummary));
        foreach (var row in Blocks) row.RefreshLocalizedText();
        // The tree carries TIA's category names, which are captured when it is built.
        RebuildTree();
    }

    private void OnActivityChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(WorkbenchActivity.Busy)) return;
        if (!_activity.Busy && _loadDeviceWhenIdle)
        {
            _loadDeviceWhenIdle = false;
            _ = RefreshBlocksAsync();
        }
        Raise(nameof(CanReadTags));
        foreach (var command in new[] { Refresh, Export, Import, Compile, Inspect, Save }) command.RaiseCanExecuteChanged();
    }
    private void OnResultEntry(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems?.OfType<WorkbenchActivity.Entry>().Any(entry => entry.Key is "Status.CompileResult" or "Status.InspectResult") == true)
            Document = ProjectDocument.Compile;
    }
    public void Dispose()
    {
        _sourceRequest++;
        Loc.Current.LanguageChanged -= OnLanguageChanged;
        _activity.PropertyChanged -= OnActivityChanged;
        ((INotifyCollectionChanged)_activity.Entries).CollectionChanged -= OnResultEntry;
    }
}
