using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Gui.Services;

namespace TiaOpenness.Gui.Controls;

public enum LogLevel { Default, Info, Warning, Error, Debug }

public sealed class EnumIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => System.Convert.ToInt32(value);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Enum.ToObject(targetType, value);
}

public sealed class GlassValueConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => Convert(values[0], targetType, parameter, culture);
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // A view losing its data context during teardown can still receive a
        // language notification before WPF removes the multi-binding.
        if (value == null || value == System.Windows.DependencyProperty.UnsetValue || value == Binding.DoNothing) return Binding.DoNothing;
        return (string)parameter switch
        {
            "group" => GroupPath(value.ToString() ?? ""),
            "version" => value.ToString()!.Replace("TIA V", "V"),
            "export" => (bool)value ? Loc.Current["Glass.ExportSource"] : Loc.Current["Glass.ExportXml"],
            "consistent" => (bool)value ? Loc.Current["Glass.Yes"] : Loc.Current["Glass.No"],
            "protected" => (bool)value ? Loc.Current["Glass.Protected"] : "—",
            "compare" => (VcCompareState)value switch
            {
                VcCompareState.Equal => Loc.Current["Glass.Identical"],
                VcCompareState.Unequal => Loc.Current["Glass.Differs"],
                VcCompareState.WorkspaceFileMissing => Loc.Current["Glass.Missing"],
                _ => Loc.Current["Glass.Unknown"],
            },
            _ => value,
        };
    }
    private static string GroupPath(string path)
    {
        int separator = path.LastIndexOfAny(new[] { '/', '\\' });
        return separator < 0 ? path : path[..separator];
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// View-only projection of the existing, timestamped operation log. Never invokes an operation
/// or treats an absent result as success. The VM and bridge remain the source of all outcomes.
/// </summary>
public sealed class GlassResults : INotifyPropertyChanged, IDisposable
{
    private readonly MainViewModel model;
    private int _resultLogStart;
    private readonly ObservableCollection<LogRow> _logRows = [];
    private readonly Dictionary<WorkbenchActivity.Entry, List<LogRow>> _entryRows = new();
    private readonly Dictionary<string, object?> _resultValues = new();
    private static readonly System.Reflection.PropertyInfo[] ResultProperties = new[]
    {
        nameof(LogCount), nameof(HasCompile), nameof(HasInspection), nameof(CompileCardHeight), nameof(InspectionCardHeight), nameof(CompileBadge),
        nameof(InspectionDisplay), nameof(CompileOperationState), nameof(InspectionOperationState), nameof(CompileSummary), nameof(Errors), nameof(Warnings),
        nameof(CompileState), nameof(InspectionTime), nameof(InspectionSummary), nameof(Mapped), nameof(Unsupported), nameof(Failed),
        nameof(MappingSummary), nameof(SyncSummary), nameof(Diagnostics), nameof(Rules)
    }.Select(name => typeof(GlassResults).GetProperty(name)!).ToArray();
    private static readonly string[] PresentationProperties = typeof(GlassResults).GetProperties().Select(p => p.Name).Where(name => name != nameof(LogRows)).ToArray();
    public GlassResults(MainViewModel model)
    {
        this.model = model;
        model.Activity.PropertyChanged += Changed;
        ((INotifyCollectionChanged)model.Activity.Entries).CollectionChanged += LogChanged;
        foreach (var entry in model.Activity.Entries) AddLog(entry);
        model.Session.PropertyChanged += Changed;
        model.Engineering.PropertyChanged += Changed;
        model.VersionControl.PropertyChanged += Changed;
        model.VersionControl.VcStatusItems.CollectionChanged += CollectionChanged;
        model.VersionControl.VcDiffLines.CollectionChanged += CollectionChanged;
        model.Engineering.BlocksView.CollectionChanged += CollectionChanged;
        model.Engineering.Compile.CanExecuteChanged += OperationChanged;
        model.Engineering.Inspect.CanExecuteChanged += OperationChanged;
        Loc.Current.LanguageChanged += LanguageChanged;
        Refresh();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool HasProject => model.Session.IsConnected && model.Session.ProjectName.Length > 0;
    public bool CanConnect => !model.Busy;
    public string MappedMeta => Loc.Current.T("Pages.MappedMeta", model.VersionControl.SelectedWorkspace?.MappedObjectCount ?? 0, model.VersionControl.VcStatusItems.Count(i => i.CompareState != VcCompareState.Equal));
    public bool CanOperate => HasProject && !model.Busy;
    public double OperationOpacity => HasProject ? 1 : .5;
    public bool HasSelection => CanOperate && model.Engineering.Blocks.Any(b => b.Selected);
    public bool HasWorkspace => model.VersionControl.SelectedWorkspace != null;
    public double WorkspaceOpacity => HasWorkspace ? 1 : .45;
    public double CompileCardHeight => HasCompile ? 289 : Loc.Current.Language == AppLanguage.Chinese ? 89 : 104;
    public double InspectionCardHeight => HasInspection ? 148 : 85;
    public string CompileBadge => HasCompile ? CompileState : Loc.Current["Pages.NotCompiled"];
    public string InspectionDisplay => HasInspection ? InspectionSummary : Loc.Current["Pages.NotInspected"];
    public sealed record LogRow(string Time, string Message, LogLevel Level = LogLevel.Default);
    public IReadOnlyList<LogRow> LogRows => _logRows;
    public IReadOnlyList<LogRow> LogTail => LogRows.TakeLast(12).ToArray();
    public bool HasCompile => Errors != "—";
    public bool HasInspection => InspectionTime.Length > 0;
    public string ProjectKind => model.Session.UseMock ? Loc.Current["Toolbar.Mock"] : Loc.Current["Pages.RealProject"];
    public string BlocksSubtitle => model.Session.ProjectName + " · " + model.Engineering.SelectedDevice?.Name + " · Software";
    public string SelectedSummary => HasProject ? Loc.Current.T("Pages.Selected", model.Engineering.Blocks.Count(b => b.Selected)) : Loc.Current["Pages.NotRun"];
    public string ReadyState => HasProject ? Loc.Current["Pages.Ready"] : Loc.Current["Pages.NeedConnection"];
    public string CompileOperationState => OperationState(model.Engineering.Compile.IsRunning, HasCompile);
    public string InspectionOperationState => OperationState(model.Engineering.Inspect.IsRunning, HasInspection);
    public string EnvironmentState => Loc.Current["Pages.Ready"];
    public string EnvironmentSummary => Loc.Current["Pages.NotRun"];
    public string CompileSummary => HasCompile ? model.Engineering.SelectedDevice?.Name + " · " + Errors + " " + Loc.Current["Glass.Errors"] + " · " + Warnings + " " + Loc.Current["Glass.Warnings"] : Loc.Current["Pages.NotRun"];
    public string LogCount => Loc.Current.T("Pages.LogCount", model.Activity.Log.Split('\n').Count(line => !string.IsNullOrWhiteSpace(line)));
    private string OperationState(bool running, bool completed) => !HasProject ? Loc.Current["Pages.NeedConnection"]
        : running ? Loc.Current["Pages.Running"]
        : completed ? Loc.Current["Pages.Done"] : Loc.Current["Pages.Ready"];
    public string DiffAdded => "+" + model.VersionControl.VcDiffLines.Count(l=>l.Kind==DiffLineKind.Added);
    public string DiffRemoved => "−" + model.VersionControl.VcDiffLines.Count(l=>l.Kind==DiffLineKind.Removed);
    public string DiffFileName => model.VersionControl.SelectedVcItem?.FilePath ?? model.VersionControl.VcDiffCaption;
    public IReadOnlyList<MappedObjectInfo> OtherMappedFiles => model.VersionControl.VcStatusItems
        .Where(i=>i!=model.VersionControl.SelectedVcItem && i.CompareState!=VcCompareState.Equal).Take(3).ToArray();
    public string BlocksSummary => Loc.Current.T("Glass.BlocksSummary", model.Engineering.Blocks.Count,
        model.Engineering.Blocks.Count(b=>b.Selected), model.Engineering.BlocksView.Cast<BlockRow>().Count());
    public string DifferenceLabel => Loc.Current["Glass.Differ"] + " · " + model.VersionControl.VcStatusItems.Count(i=>i.CompareState==VcCompareState.Unequal);
    public string MissingLabel => Loc.Current["Glass.Missing"] + " · " + model.VersionControl.VcStatusItems.Count(i=>i.CompareState==VcCompareState.WorkspaceFileMissing);
    public string WorkspaceSummary => model.VersionControl.SelectedWorkspace is null ? model.VersionControl.WorkspaceRootDisplay
        : Loc.Current.T("Glass.WorkspaceSummary", model.VersionControl.WorkspaceRootDisplay,
            model.VersionControl.SelectedWorkspace.MappedObjectCount, model.VersionControl.VcStatusItems.Count(i=>i.CompareState!=VcCompareState.Equal));
    public string Errors { get; private set; } = "—";
    public string Warnings { get; private set; } = "—";
    public string CompileState { get; private set; } = "";
    public string InspectionTime { get; private set; } = "";
    public string InspectionSummary { get; private set; } = "";
    public string Mapped { get; private set; } = "—";
    public string Unsupported { get; private set; } = "—";
    public string Failed { get; private set; } = "—";
    public string MappingSummary { get; private set; } = "";
    public string SyncSummary { get; private set; } = "";
    public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = [];
    public IReadOnlyList<string> Rules { get; private set; } = [];
    public sealed record Diagnostic(string Name, string Message);

    private void AddLog(WorkbenchActivity.Entry entry)
    {
        var rows = entry.Message.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line))
            .Select((line, index) => new LogRow(index == 0 ? entry.Time : "", line.TrimEnd('\r'), (LogLevel)entry.Level)).ToList();
        _entryRows[entry] = rows;
        foreach (var row in rows) _logRows.Add(row);
    }
    private void LogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset) { _logRows.Clear(); _entryRows.Clear(); _resultLogStart = 0; }
        if (e.OldItems != null) foreach (WorkbenchActivity.Entry entry in e.OldItems)
        {
            if (_entryRows.Remove(entry, out var rows)) foreach (var row in rows) _logRows.Remove(row);
            _resultLogStart = Math.Max(0, _resultLogStart - 1);
        }
        if (e.NewItems != null) foreach (WorkbenchActivity.Entry entry in e.NewItems) AddLog(entry);
    }
    private void NotifyPresentation()
    {
        foreach (string name in PresentationProperties) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (sender == model.Activity && e.PropertyName != nameof(WorkbenchActivity.Log)) return;
        if (sender == model.Session && e.PropertyName == nameof(SessionViewModel.IsConnected) && !model.Session.IsConnected)
        {
            _resultLogStart = model.Activity.Entries.Count;
            Refresh();
        }
        else if (e.PropertyName is nameof(WorkbenchActivity.Log) or nameof(SessionViewModel.ProjectPath)) Refresh();
        else NotifyPresentation();
    }
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => NotifyPresentation();
    private void OperationChanged(object? sender, EventArgs e) => NotifyPresentation();
    private void LanguageChanged(object? sender, EventArgs e) => Refresh();

    private static string RuleLabel(string id) => id switch
    {
        "NAMING-001" => Loc.Current["Glass.RuleNaming"],
        "DOC-001" => Loc.Current["Glass.RuleAuthor"],
        "BUILD-001" => Loc.Current["Glass.RuleConsistency"],
        "PROT-001" => Loc.Current["Glass.RuleKnowHow"],
        "DEAD-001" => Loc.Current["Glass.RuleUnused"],
        _ => id,
    };

    private void Refresh()
    {
        string notRun = Loc.Current["Glass.NotRun"];
        Errors = Warnings = Mapped = Unsupported = Failed = "—";
        InspectionTime = "";
        CompileState = InspectionSummary = MappingSummary = SyncSummary = notRun;
        var diagnostics = new List<Diagnostic>();
        var pendingDiagnostics = new List<Diagnostic>();
        var rules = new List<string>();
        bool inspection = false;
        bool protectionRule = false;
        if (model.Activity.Entries.Count < _resultLogStart) _resultLogStart = 0;
        foreach (var entry in model.Activity.Entries.Skip(_resultLogStart))
        {
            string Arg(int index) => Convert.ToString(entry.Arguments[index], CultureInfo.CurrentCulture) ?? "";
            switch (entry.Key)
            {
                case "Status.CompileResult":
                    Errors = Arg(1); Warnings = Arg(2);
                    CompileState = Convert.ToInt32(entry.Arguments[1], CultureInfo.InvariantCulture) != 0 ? Loc.Current["Glass.CompileErrors"]
                        : Convert.ToInt32(entry.Arguments[2], CultureInfo.InvariantCulture) != 0 ? Loc.Current["Glass.CompileWarnings"] : Loc.Current["Glass.CompileCompleted"];
                    diagnostics = [.. pendingDiagnostics];
                    pendingDiagnostics.Clear();
                    break;
                case "compile-diagnostic":
                    pendingDiagnostics.Add(new(Arg(0), Arg(1)));
                    break;
                case "Log.InspectionHeader":
                    inspection = true; protectionRule = false; rules.Clear();
                    break;
                case "inspection-rule" when inspection:
                    protectionRule |= Arg(0) == "PROT-001";
                    rules.Add(RuleLabel(Arg(0)) + " · " + Arg(1));
                    break;
                case "Status.InspectResult":
                    InspectionTime = entry.Time;
                    InspectionSummary = Loc.Current.T("Glass.InspectionSummary", Arg(1), Arg(0));
                    if (!protectionRule) rules.Add(Loc.Current["Glass.RuleKnowHow"] + " · 0");
                    inspection = false;
                    break;
                case "Status.VcMapApplied":
                case "Status.VcMapDry":
                    MappingSummary = Loc.Current.T(entry.Key, entry.Arguments);
                    Mapped = Arg(0); Unsupported = Arg(2);
                    Failed = entry.Key == "Status.VcMapApplied" ? Arg(3) : "—";
                    break;
                case "Status.VcSyncDry":
                    SyncSummary = Loc.Current.T("Glass.SyncPreview", Arg(0), Arg(2));
                    break;
                case "Status.VcSyncApplied":
                    SyncSummary = Loc.Current.T(entry.Key, entry.Arguments);
                    break;
            }
        }
        if (!Diagnostics.SequenceEqual(diagnostics)) Diagnostics = diagnostics;
        if (!Rules.SequenceEqual(rules)) Rules = rules;
        foreach (var property in ResultProperties)
        {
            object? value = property.GetValue(this);
            if (_resultValues.TryGetValue(property.Name, out var previous) && Equals(previous, value)) continue;
            _resultValues[property.Name] = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property.Name));
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LogTail)));
    }

    public void Dispose()
    {
        model.Activity.PropertyChanged -= Changed;
        ((INotifyCollectionChanged)model.Activity.Entries).CollectionChanged -= LogChanged;
        model.Session.PropertyChanged -= Changed;
        model.Engineering.PropertyChanged -= Changed;
        model.VersionControl.PropertyChanged -= Changed;
        model.VersionControl.VcStatusItems.CollectionChanged -= CollectionChanged;
        model.VersionControl.VcDiffLines.CollectionChanged -= CollectionChanged;
        model.Engineering.BlocksView.CollectionChanged -= CollectionChanged;
        model.Engineering.Compile.CanExecuteChanged -= OperationChanged;
        model.Engineering.Inspect.CanExecuteChanged -= OperationChanged;
        Loc.Current.LanguageChanged -= LanguageChanged;
    }
}
