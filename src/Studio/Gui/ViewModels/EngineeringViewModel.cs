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

/// <summary>Owns devices, block selection and engineering operations.</summary>
public sealed class EngineeringViewModel : ObservableObject, IDisposable
{
    private readonly IStudioClient _client;
    private readonly IDialogService _dialogs;
    private readonly WorkbenchActivity _activity;
    private string _outputDirectory = string.Empty;
    private string _blockFilter = string.Empty;
    private string _namePattern = "^(OB|FB|FC|DB|UDT)_";
    private DeviceInfo? _selectedDevice;
    private bool _sourceFormat;
    private bool _suppressDeviceLoad;

    public EngineeringViewModel(IStudioClient client, IDialogService dialogs, WorkbenchActivity activity)
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

        Refresh = new AsyncCommand(RefreshBlocksAsync, () => SelectedDevice is not null);
        Export = new AsyncCommand(ExportAsync, () => SelectedDevice is not null && OutputDirectory.Length > 0);
        Import = new AsyncCommand(ImportAsync, () => SelectedDevice is not null);
        Compile = new AsyncCommand(CompileAsync, () => SelectedDevice is not null);
        Inspect = new AsyncCommand(InspectAsync, () => SelectedDevice is not null);
        Save = new AsyncCommand(SaveAsync, () => SelectedDevice is not null);
    }

    public ObservableCollection<DeviceInfo> Devices { get; } = [];

    public ObservableCollection<BlockRow> Blocks { get; }

    public ICollectionView BlocksView { get; }

    /// <summary>The blocks as TIA shows them: its categories, then the engineer's own folders.</summary>
    public ObservableCollection<BlockNode> BlockTree { get; } = [];

    /// <summary>The devices as TIA shows them: the project, its device groups, then the devices.</summary>
    public ObservableCollection<DeviceNode> DeviceTree { get; } = [];

    public AsyncCommand Refresh { get; }
    public AsyncCommand Export { get; }
    public AsyncCommand Import { get; }
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

    private async Task RefreshBlocksAsync()
    {
        if (SelectedDevice is null) return;

        var deviceId = SelectedDevice.Id;
        LoadedDeviceId = null;
        await _activity.Guarded("Status.ReadingBlocks", async () =>
        {
            var blocks = await _client.ListBlocksAsync(deviceId);
            Blocks.Clear();
            foreach (var block in blocks)
            {
                var row = new BlockRow(block);
                row.PropertyChanged += OnRowChanged;
                Blocks.Add(row);
            }
            LoadedDeviceId = deviceId;
            RebuildTree();
            _activity.SetStatus("Status.BlocksIn", blocks.Count, deviceId);
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
            _activity.SetStatus("Status.ExportedFailed",
                result.Succeeded, result.Requested, result.OutputDirectory, result.Failed);
        }
        else
        {
            _activity.SetStatus("Status.Exported", result.Succeeded, result.Requested, result.OutputDirectory);
        }

        _activity.AppendStatus();
    });

    private async Task ImportAsync() => await _activity.Guarded("Status.Importing", async () =>
    {
        var files = _dialogs.OpenFiles(Loc.Current["Dialog.Import.Title"], Loc.Current["Dialog.Import.Filter"], multiselect: true);
        if (files is null) return;

        var overwrite = _dialogs.ShowMessage(
            Loc.Current["Dialog.Import.Text"], Loc.Current["Dialog.Import.Caption"],
            DialogButtons.YesNoCancel, DialogIcon.Question);
        if (overwrite == DialogResult.Cancel) return;

        var result = await _client.ImportBlocksAsync(
            SelectedDevice!.Id, files, overwrite == DialogResult.Yes);

        foreach (var item in result.Items.Where(i => !i.Succeeded))
        {
            _activity.AppendLocalized("Log.Failed", item.FilePath, item.Error);
        }

        _activity.SetStatus("Status.Imported", result.Succeeded, result.Requested);
        _activity.AppendStatus();
        await RefreshBlocksAsync();
    });

    private async Task CompileAsync() => await _activity.Guarded("Status.Compiling", async () =>
    {
        var result = await _client.CompileAsync(SelectedDevice!.Id);

        foreach (var message in Flatten(result.Messages).Where(m => m.Severity != CompileSeverity.Information))
        {
            _activity.AppendDiagnostic(message.Target, message.Description, message.Severity == CompileSeverity.Error ? WorkbenchActivity.Severity.Error : WorkbenchActivity.Severity.Warning);
        }

        _activity.SetStatus("Status.CompileResult",
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

        _activity.SetStatus("Status.InspectResult", report.Findings.Count, report.BlocksScanned);
        _activity.AppendStatus();
    });

    private async Task SaveAsync() => await _activity.Guarded("Status.SavingProject", async () =>
    {
        if (_dialogs.ShowMessage(
                Loc.Current["Dialog.Save.Text"], Loc.Current["Dialog.Save.Caption"],
                DialogButtons.OKCancel, DialogIcon.Warning) != DialogResult.OK)
        {
            return;
        }

        await _client.SaveProjectAsync();
        _activity.SetStatus("Status.ProjectSaved");
        _activity.AppendStatus();
    });

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

    public void BrowseOutput()
    {
        var folder = _dialogs.OpenFolder(Loc.Current["Dialog.Export.Title"]);
        if (folder is not null) OutputDirectory = folder;
    }

    internal void ClearPresentation()
    {
        LoadedDeviceId = null;
        SelectedDevice = null;
        Devices.Clear();
        foreach (var row in Blocks) row.PropertyChanged -= OnRowChanged;
        Blocks.Clear();
        BlockTree.Clear();
        DeviceTree.Clear();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Raise(nameof(SelectionSummary));
        foreach (var row in Blocks) row.RefreshLocalizedText();
        // The tree carries TIA's category names, which are captured when it is built.
        RebuildTree();
    }

    public void Dispose() => Loc.Current.LanguageChanged -= OnLanguageChanged;
}
