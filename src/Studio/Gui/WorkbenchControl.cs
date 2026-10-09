using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TiaOpenness.Gui.ControlChannel;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Shared;

namespace TiaOpenness.Gui;

public partial class MainWindow
{
    internal WorkbenchControlSurface ControlSurface { get; private set; } = null!;
    internal ControlInputGuard ControlGuard { get; set; } = new();
    private WorkbenchControlServer? _controlServer;
    private readonly PrefillRegistry _prefills = new();
    private readonly Dictionary<WorkbenchPrefillForm, Action> _prefillRestore = [];
    private readonly HashSet<BlockRow> _controlRows = [];
    private readonly DispatcherTimer _controlHintTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _controlQueued;
    private volatile bool _controlClosing;
    private bool _controlUnavailable;
    private string? _controlProjectPath;
    private WorkbenchControlRequest? _lastControl;
    private DateTimeOffset _lastControlUtc;
    private INotifyPropertyChanged? _controlFeatures, _controlJournal;
    private string? _controlDetailRequestId;

    private void InitializeControl()
    {
        _controlProjectPath = HasProject ? _model.Session.ProjectPath : null;
        ControlSurface = new WorkbenchControlSurface(ApplyControl, CaptureControlSnapshot());
        foreach (var source in new INotifyPropertyChanged[] { _model, _model.Session, _model.Engineering, _model.Activity })
            source.PropertyChanged += OnControlChanged;
        HookControlFeatures();
        _model.Engineering.Blocks.CollectionChanged += OnControlBlocksChanged;
        BlocksContent.ControlSelectionChanged += OnControlSelectionChanged;
        ObserveControlRows();
        SettingsContent.ControlEnabledChanged += OnControlEnabledChanged;
        PreviewMouseDown += OnControlMouseDown;
        PreviewMouseUp += OnControlMouseUp;
        PreviewMouseMove += OnControlMouseMove;
        PreviewMouseWheel += OnControlMouseWheel;
        PreviewKeyDown += OnControlKeyDown;
        _controlHintTimer.Tick += OnControlHintTick;
        Loc.Current.LanguageChanged += OnControlLanguageChanged;
        if (!_loadExistingConfiguration) return;
        try
        {
            string root = BundleLayout.RequireWorkbenchRoot(AppContext.BaseDirectory);
            _controlServer = new WorkbenchControlServer(ControlSurface, Dispatcher,
                release => BundleLayout.WorkbenchEnginePath(root, release, AppContext.BaseDirectory));
            _controlServer.Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _controlServer?.Dispose(); _controlUnavailable = true;
            System.Diagnostics.Trace.TraceWarning("Workbench control unavailable: {0}", ex.GetType().Name);
            UpdateControlHint();
        }
    }
    private void HookControlFeatures()
    {
        if (_controlFeatures != null) _controlFeatures.PropertyChanged -= OnControlChanged;
        if (_controlJournal != null) _controlJournal.PropertyChanged -= OnControlChanged;
        _controlFeatures = Features; _controlJournal = Features.Journal;
        _controlFeatures.PropertyChanged += OnControlChanged;
        _controlJournal.PropertyChanged += OnControlChanged;
    }
    private void OnControlChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender == _model.Session && e.PropertyName == nameof(SessionViewModel.ProjectName))
        {
            _controlProjectPath = HasProject ? _model.Session.ProjectPath : null;
            ConfirmControlPrefill();
        }
        if (sender == _controlFeatures && e.PropertyName == nameof(FeaturePagesViewModel.SelectedCall)) UpdateControlDetail();
        QueueControlSnapshot();
    }
    private async void UpdateControlDetail()
    {
        string? id = _controlDetailRequestId = Features.SelectedCall?.Record.RequestId;
        CallDetailContent.SetControlActivity("");
        if (id == null) return;
        var activity = await System.Threading.Tasks.Task.Run(() => WorkbenchControlLog.ReadActivity(id));
        if (!_controlClosing && _controlDetailRequestId == id && activity != null)
            CallDetailContent.SetControlActivity(Loc.Current.T("Control.Record", activity.Operation, activity.Status));
    }
    private void OnControlBlocksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var row in _controlRows) row.PropertyChanged -= OnControlChanged;
            _controlRows.Clear(); ObserveControlRows();
        }
        else
        {
            if (e.OldItems != null)
                foreach (BlockRow row in e.OldItems)
                    if (_controlRows.Remove(row)) row.PropertyChanged -= OnControlChanged;
            if (e.NewItems != null)
                foreach (BlockRow row in e.NewItems)
                    if (_controlRows.Add(row)) row.PropertyChanged += OnControlChanged;
        }
        QueueControlSnapshot();
    }
    private void OnControlSelectionChanged(object? sender, EventArgs e) => QueueControlSnapshot();
    private void ObserveControlRows()
    {
        foreach (var row in _model.Engineering.Blocks)
            if (_controlRows.Add(row)) row.PropertyChanged += OnControlChanged;
    }
    private void QueueControlSnapshot()
    {
        if (ControlSurface == null || _controlClosing || Interlocked.Exchange(ref _controlQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _controlQueued, 0);
            if (!_controlClosing) ControlSurface.Publish(CaptureControlSnapshot());
        }));
    }
    private WorkbenchPage? ControlPage => _page switch
    {
        "Blocks" => WorkbenchPage.Blocks, "VersionControl" => WorkbenchPage.VersionControl,
        "Calls" => WorkbenchPage.Calls, "Audit" => WorkbenchPage.Audit, "Environment" => WorkbenchPage.Environment,
        "Log" => WorkbenchPage.Log, "Engineering" => WorkbenchPage.Overview, _ => null,
    };
    private static string ShellPage(WorkbenchPage page) => page == WorkbenchPage.Overview ? "Engineering" : page.ToString();
    private static WorkbenchBlock ControlBlock(BlockRow row)
        => new() { Path = row.Path, Name = row.Name, Kind = row.Kind, Number = row.Info.Number ?? 0 };
    private WorkbenchDevice? ControlDevice => _model.Engineering.SelectedDevice is { } device
        ? new() { Id = device.Id, DisplayName = device.DisplayName ?? device.Name } : null;
    private WorkbenchSnapshot CaptureControlSnapshot()
    {
        var info = ControlResponse.Info; info.ControlEnabled = App.Settings.WorkbenchControlEnabled;
        return new WorkbenchSnapshot(new()
        {
            Workbench = info, Page = ControlPage, Release = _model.SelectedReleaseKey, Device = ControlDevice,
            Session = new() { Source = WorkbenchSessionSource.WorkbenchBridge, Connected = _model.Session.IsConnected,
                Project = HasProject ? new() { Name = _model.Session.ProjectName, Path = _controlProjectPath ?? _model.Session.ProjectPath } : null },
            Busy = _model.Busy, Prefill = _prefills.Latest,
        }, new()
        {
            Page = ControlPage, Device = ControlDevice,
            FocusedBlock = BlocksContent.ControlFocusedBlock is { } row ? ControlBlock(row) : null,
            CheckedBlocks = _model.Engineering.Blocks.Where(row => row.Selected).Select(ControlBlock).ToArray(),
            SelectedCall = Features.SelectedCall is { } call ? new() { RequestId = call.Record.RequestId,
                Tool = call.Record.Tool, Result = call.Record.Result.ToString().ToLowerInvariant() } : null,
        });
    }
    internal WorkbenchControlResponse ApplyControl(WorkbenchControlRequest request)
    {
        if (_controlClosing) return ControlResponse.Refuse(request, "workbench-closing");
        if (request.Operation is WorkbenchControlOperation.ReadState or WorkbenchControlOperation.ReadSelection)
            return ControlResponse.Done(request, ControlSurface.Snapshot.Read(request.Arguments));
        if (!App.Settings.WorkbenchControlEnabled) return ControlResponse.Refuse(request, "workbench-control-disabled");
        if (!IsEnabled || _drawer is "Settings" or "Approvals" || ConfirmOverlay.Visibility == Visibility.Visible
            || OwnedWindows.OfType<Window>().Any(window => window.IsVisible))
            return ControlResponse.Refuse(request, "workbench-modal-open");
        if (ControlGuard.HumanActive) return ControlResponse.Refuse(request, "workbench-user-active");
        if (_model.Busy) return ControlResponse.Refuse(request, "workbench-busy");
        WorkbenchControlResponse response;
        switch (request.Arguments)
        {
            case WorkbenchDisplayPageArguments page:
                if (page.Page is WorkbenchPage.Blocks or WorkbenchPage.VersionControl && !HasProject)
                    return ControlResponse.Refuse(request, "workbench-no-project");
                var previous = ControlPage;
                Navigate(ShellPage(page.Page));
                response = ControlResponse.Done(request, new() { Page = page.Page, PreviousPage = previous });
                break;
            case WorkbenchDisplayBlockArguments block:
                response = LocateControlBlock(request, block.SoftwarePath, block.BlockPath);
                break;
            case WorkbenchDisplayLadderArguments ladder:
                if (ladder.RenderRequestId != null && ladder.Artifact == null)
                    return ControlResponse.Refuse(request, "render-artifact-missing", WorkbenchControlError.NotFound, "render-artifact");
                response = LocateControlBlock(request, ladder.SoftwarePath, ladder.BlockPath);
                if (response.Data != null)
                {
                    response.Data.Opened = ladder.Artifact != null;
                    response.Data.NextStep = ladder.Artifact == null ? "user-click-view-ladder" : null;
                }
                break;
            case WorkbenchDisplayAtlasArguments atlas:
                if (ControlProjectRefusal(request) is { } refusal) return refusal;
                if (atlas.RenderRequestId != null && atlas.Artifact == null)
                    return ControlResponse.Refuse(request, "render-artifact-missing", WorkbenchControlError.NotFound, "render-artifact");
                Navigate("Blocks");
                response = ControlResponse.Done(request, new() { Page = WorkbenchPage.Blocks,
                    Opened = atlas.Artifact != null, NextStep = atlas.Artifact == null ? "user-click-generate-atlas" : null });
                break;
            case WorkbenchDisplayCallArguments call:
                if (Approvals.Requests.Any(row => row.Id == call.RequestId && row.State == ApprovalState.Pending)
                    || Features.Journal.Calls.Any(row => row.RequestId == call.RequestId && row.Result == CallResult.Pending))
                    return ControlResponse.Refuse(request, "approval-pending");
                var matches = Features.Journal.Calls.Where(row => row.RequestId == call.RequestId).ToArray();
                if (matches.Length != 1) return ControlResponse.Refuse(request, "call-not-found",
                    matches.Length == 0 ? WorkbenchControlError.NotFound : WorkbenchControlError.TargetAmbiguous, "call");
                // Clear presentation filters, then open only the ordinary detail drawer.
                Features.WriteOnly = Features.FailOnly = Features.ReleaseOnly = false; Features.Search = "";
                if (!Features.FollowLatest) Features.ToggleFollow();
                Navigate("Calls");
                var target = Features.Calls.First(row => row.Record.JournalKey == matches[0].JournalKey);
                CallsContent.LocateControlCall(target);
                Features.OpenCall(target);
                response = ControlResponse.Done(request, new() { Page = WorkbenchPage.Calls, RequestId = call.RequestId,
                    Tool = matches[0].Tool, Result = matches[0].Result.ToString().ToLowerInvariant() });
                break;
            case WorkbenchPrefillArguments prefill:
                response = ApplyControlPrefill(request, prefill);
                break;
            default: return ControlResponse.Refuse(request, "workbench-operation", WorkbenchControlError.InvalidArgument);
        }
        if (response.Status == WorkbenchControlStatus.Done)
        {
            ControlGuard.UiChanged(); _lastControl = request; _lastControlUtc = DateTimeOffset.UtcNow;
            _controlHintTimer.Start(); UpdateControlHint();
            ControlSurface.Publish(CaptureControlSnapshot());
        }
        return response;
    }
    private WorkbenchControlResponse? ControlProjectRefusal(WorkbenchControlRequest request)
    {
        if (!HasProject) return ControlResponse.Refuse(request, "workbench-no-project");
        string? expected = request.Origin.BoundProjectFile;
        try
        {
            if (expected != null && Path.IsPathFullyQualified(expected) && _controlProjectPath != null
                && string.Equals(Path.GetFullPath(expected), Path.GetFullPath(_controlProjectPath), StringComparison.OrdinalIgnoreCase)) return null;
        }
        catch (ArgumentException) /* swallow(ui): malformed cached project paths produce the identity refusal below */ { }
        var response = ControlResponse.Refuse(request, "workbench-project-mismatch", WorkbenchControlError.IdentityMismatch, "project");
        response.Refusal!.Expected = expected; response.Refusal.Actual = _controlProjectPath;
        return response;
    }
    private WorkbenchControlResponse? ResolveControlBlocks(WorkbenchControlRequest request, string software, string[] paths, out BlockRow[] rows)
    {
        rows = [];
        if (ControlProjectRefusal(request) is { } refusal) return refusal;
        var devices = _model.Engineering.Devices.Where(device => device.Id == software || device.Name == software || device.DisplayName == software).ToArray();
        if (devices.Length != 1) return ControlResponse.Refuse(request, "software-not-found",
            devices.Length == 0 ? WorkbenchControlError.NotFound : WorkbenchControlError.TargetAmbiguous, "software",
            devices.Select(device => device.Id).Distinct().Take(16).ToArray());
        if (devices[0] != _model.Engineering.SelectedDevice || _model.Engineering.LoadedDeviceId != devices[0].Id)
            return ControlResponse.Refuse(request, "workbench-tree-not-loaded");
        var found = new List<BlockRow>();
        var byPath = _model.Engineering.Blocks.ToLookup(row => row.Path, StringComparer.Ordinal);
        foreach (string path in paths)
        {
            var matches = byPath[path].ToArray();
            if (matches.Length != 1) return ControlResponse.Refuse(request, "block-not-found",
                matches.Length == 0 ? WorkbenchControlError.NotFound : WorkbenchControlError.TargetAmbiguous, "block",
                matches.Select(row => row.Path).Distinct().Take(16).ToArray());
            found.Add(matches[0]);
        }
        rows = found.ToArray(); return null;
    }
    private WorkbenchControlResponse LocateControlBlock(WorkbenchControlRequest request, string software, string path)
    {
        if (ResolveControlBlocks(request, software, [path], out var rows) is { } refusal) return refusal;
        Navigate("Blocks");
        _model.Engineering.BlockFilter = "";
        BlocksContent.LocateControlBlock(rows[0]);
        return ControlResponse.Done(request, new() { Page = WorkbenchPage.Blocks, Device = ControlDevice,
            Block = ControlBlock(rows[0]), Focused = true });
    }
    private WorkbenchControlResponse ApplyControlPrefill(WorkbenchControlRequest request, WorkbenchPrefillArguments arguments)
    {
        if (!_prefills.CanChange(arguments.Form, request.Origin)) return ControlResponse.Refuse(request, "prefill-pending");
        if (arguments.Form != WorkbenchPrefillForm.InspectionRules && !HasProject)
            return ControlResponse.Refuse(request, "workbench-no-project");
        if (!PrefillRegistry.Valid(arguments)) return ControlResponse.Refuse(request, "prefill-field-invalid", WorkbenchControlError.InvalidArgument, "namePattern");
        var engineering = _model.Engineering;
        BlockRow[] selection = [];
        if (arguments.Mode == WorkbenchPrefillMode.Set && arguments.Fields is WorkbenchBlockSelectionFields fields
            && ResolveControlBlocks(request, fields.SoftwarePath ?? "", fields.BlockPaths ?? [], out selection) is { } selectionRefusal)
            return selectionRefusal;
        if (arguments.Mode == WorkbenchPrefillMode.Clear)
        {
            if (_prefillRestore.Remove(arguments.Form, out var restore)) restore();
        }
        else
        {
            if (!_prefillRestore.ContainsKey(arguments.Form))
            {
                string filter = engineering.BlockFilter, pattern = engineering.NamePattern;
                _prefillRestore[arguments.Form] = arguments.Form switch
                {
                    WorkbenchPrefillForm.InspectionRules => () => engineering.NamePattern = pattern,
                    WorkbenchPrefillForm.BlockFilter => () => engineering.BlockFilter = filter,
                    _ => CaptureControlSelection(),
                };
            }
            switch (arguments.Fields)
            {
                case WorkbenchInspectionRulesFields rules: engineering.NamePattern = rules.NamePattern ?? ""; break;
                case WorkbenchBlockFilterFields filter: engineering.BlockFilter = filter.Filter ?? ""; break;
                case WorkbenchBlockSelectionFields:
                    var selected = selection.ToHashSet();
                    foreach (var row in engineering.Blocks) row.Selected = selected.Contains(row);
                    break;
            }
        }
        _prefills.Set(arguments, request.Origin);
        Navigate(arguments.Form == WorkbenchPrefillForm.InspectionRules ? "Engineering" : "Blocks");
        if (arguments.Form == WorkbenchPrefillForm.InspectionRules && arguments.Mode == WorkbenchPrefillMode.Set)
            OperationsContent.ShowControlInspectionRules();
        UpdateControlPrefill();
        return ControlResponse.Done(request, new() { Form = arguments.Form, Page = ControlPage,
            Applied = arguments.Mode == WorkbenchPrefillMode.Clear ? [] : PrefillRegistry.Targets.Single(target => target.Form == arguments.Form).Fields,
            AwaitingConfirmation = arguments.Mode == WorkbenchPrefillMode.Set });
    }
    private void UpdateControlPrefill()
    {
        ControlPrefillHint.Visibility = _prefills.Latest != null ? Visibility.Visible : Visibility.Collapsed;
        ControlPrefillHint.ToolTip = _prefills.Latest is { } pending
            ? pending.Form + " · " + string.Join(", ", PrefillRegistry.Targets.Single(target => target.Form == pending.Form).Fields) : null;
        BlocksContent.SetControlPrefillMarker(_prefills.Contains(WorkbenchPrefillForm.BlockFilter), _prefills.Contains(WorkbenchPrefillForm.BlockSelection));
        OperationsContent.SetControlPrefillMarker(_prefills.Contains(WorkbenchPrefillForm.InspectionRules));
    }
    private Action CaptureControlSelection()
    {
        var selected = _model.Engineering.Blocks.ToDictionary(row => row, row => row.Selected);
        return () => { foreach (var row in _model.Engineering.Blocks) row.Selected = selected.TryGetValue(row, out bool value) && value; };
    }
    private void OnClearControlPrefill(object sender, RoutedEventArgs e)
    {
        foreach (var restore in _prefillRestore.Values) restore();
        ConfirmControlPrefill();
    }
    private void OnConfirmControlPrefill(object sender, RoutedEventArgs e) => ConfirmControlPrefill();
    private void ConfirmControlPrefill() { _prefillRestore.Clear(); _prefills.ClearByHuman(); UpdateControlPrefill(); QueueControlSnapshot(); }
    private void OnControlEnabledChanged(object? sender, EventArgs e) => QueueControlSnapshot();
    private void OnControlMouseDown(object sender, MouseButtonEventArgs e) { if (ControlGuard.ShouldBlock(e.OriginalSource as DependencyObject)) e.Handled = true; ControlGuard.HumanInput(); }
    private void OnControlMouseUp(object sender, MouseButtonEventArgs e) { if (ControlGuard.ShouldBlock(e.OriginalSource as DependencyObject)) e.Handled = true; ControlGuard.HumanInput(); }
    private void OnControlMouseMove(object sender, MouseEventArgs e) => ControlGuard.HumanInput();
    private void OnControlMouseWheel(object sender, MouseWheelEventArgs e) => ControlGuard.HumanInput();
    private void OnControlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && (ControlGuard.ShouldBlock(e.OriginalSource as DependencyObject) || ControlGuard.ClickGuardActive)) e.Handled = true;
        ControlGuard.HumanInput();
    }
    private void OnControlHintTick(object? sender, EventArgs e) => UpdateControlHint();
    private void OnControlLanguageChanged(object? sender, EventArgs e) { UpdateControlHint(); UpdateControlPrefill(); UpdateControlDetail(); }
    private void UpdateControlHint()
    {
        ControlStatus.Text = _controlUnavailable ? Loc.Current["Control.Unavailable"] : _lastControl == null ? ""
            : Loc.Current.T("Control.Activity", ControlTool(_lastControl.Operation), _lastControl.Origin.ClientName,
                Math.Max(0, (int)(DateTimeOffset.UtcNow - _lastControlUtc).TotalSeconds));
    }
    private static string ControlTool(WorkbenchControlOperation operation) => operation switch
    {
        WorkbenchControlOperation.DisplayPage => "ShowWorkbenchPage", WorkbenchControlOperation.DisplayBlock => "ShowWorkbenchBlock",
        WorkbenchControlOperation.DisplayCall => "ShowWorkbenchCall", WorkbenchControlOperation.DisplayLadder => "ShowWorkbenchLadder",
        WorkbenchControlOperation.DisplayAtlas => "ShowWorkbenchAtlas", _ => "PrefillWorkbenchForm",
    };
    private void DisposeControl()
    {
        _controlClosing = true; _controlServer?.Dispose(); _controlHintTimer.Stop();
        _controlHintTimer.Tick -= OnControlHintTick;
        Loc.Current.LanguageChanged -= OnControlLanguageChanged;
        foreach (var source in new INotifyPropertyChanged[] { _model, _model.Session, _model.Engineering, _model.Activity })
            source.PropertyChanged -= OnControlChanged;
        if (_controlFeatures != null) _controlFeatures.PropertyChanged -= OnControlChanged;
        if (_controlJournal != null) _controlJournal.PropertyChanged -= OnControlChanged;
        foreach (var row in _controlRows) row.PropertyChanged -= OnControlChanged;
        _controlRows.Clear();
        _model.Engineering.Blocks.CollectionChanged -= OnControlBlocksChanged;
        BlocksContent.ControlSelectionChanged -= OnControlSelectionChanged;
        SettingsContent.ControlEnabledChanged -= OnControlEnabledChanged;
        PreviewMouseDown -= OnControlMouseDown; PreviewMouseUp -= OnControlMouseUp;
        PreviewMouseMove -= OnControlMouseMove; PreviewMouseWheel -= OnControlMouseWheel; PreviewKeyDown -= OnControlKeyDown;
    }
}
