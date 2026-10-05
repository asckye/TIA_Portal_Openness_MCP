using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Data;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Gui.Services;

namespace TiaOpenness.Gui.Controls;

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
    public GlassResults(MainViewModel model)
    {
        this.model = model;
        model.Activity.PropertyChanged += Changed;
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
    public sealed record LogRow(string Time, string Message);
    public IReadOnlyList<LogRow> LogRows => model.Activity.Log.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => line.TrimEnd('\r')).Select(line => line.Length > 8 && line[2] == ':' && line[5] == ':'
            ? new LogRow(line[..8], line[8..].TrimStart()) : new LogRow("", line)).ToArray();
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

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (sender == model.Session && e.PropertyName == nameof(SessionViewModel.IsConnected) && !model.Session.IsConnected)
        {
            _resultLogStart = model.Activity.Log.Length;
            Refresh();
        }
        else if (e.PropertyName is nameof(WorkbenchActivity.Log) or nameof(SessionViewModel.ProjectPath)) Refresh();
        else PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    private void OperationChanged(object? sender, EventArgs e) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    private void LanguageChanged(object? sender, EventArgs e) => Refresh();

    private static Match Outcome(string line, string key)
    {
        foreach (var table in new[] { Strings.English, Strings.Chinese })
        {
            string pattern = Regex.Escape(table[key]);
            pattern = Regex.Replace(pattern, @"\\\{(\d+)}", m => "(?<p" + m.Groups[1].Value + ">.*?)");
            var match = Regex.Match(line, "^" + pattern + "$", RegexOptions.CultureInvariant);
            if (match.Success) return match;
        }
        return Match.Empty;
    }

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
        if (model.Activity.Log.Length < _resultLogStart) _resultLogStart = 0;
        foreach (string raw in model.Activity.Log[_resultLogStart..].Split('\n'))
        {
            string line = Regex.Replace(raw.TrimEnd('\r'), @"^\d{2}:\d{2}:\d{2}\s+", "");
            var compile = Outcome(line, "Status.CompileResult");
            if (compile.Success)
            {
                Errors = compile.Groups["p1"].Value;
                Warnings = compile.Groups["p2"].Value;
                CompileState = Errors != "0" ? Loc.Current["Glass.CompileErrors"]
                    : Warnings != "0" ? Loc.Current["Glass.CompileWarnings"]
                    : Loc.Current["Glass.CompileCompleted"];
                diagnostics = [.. pendingDiagnostics];
                pendingDiagnostics.Clear();
            }
            var diagnostic = Regex.Match(line, @"^(?:Warning|Error): (.*?) - (.*)$");
            if (diagnostic.Success) pendingDiagnostics.Add(new(diagnostic.Groups[1].Value, diagnostic.Groups[2].Value));
            if (Outcome(line, "Log.InspectionHeader").Success) { inspection = true; rules.Clear(); }
            var inspected = Outcome(line, "Status.InspectResult");
            if (inspected.Success)
            {
                InspectionTime = Regex.IsMatch(raw, @"^\d{2}:\d{2}:\d{2}") ? raw[..8] : "";
                InspectionSummary = Loc.Current.T("Glass.InspectionSummary", inspected.Groups["p1"].Value, inspected.Groups["p0"].Value);
                if (!rules.Any(r => r.StartsWith(Loc.Current["Glass.RuleKnowHow"], StringComparison.Ordinal)))
                    rules.Add(Loc.Current["Glass.RuleKnowHow"] + " · 0");
                inspection = false;
            }
            var rule = Regex.Match(line, @"^([^\s]+) \((\d+)\)$");
            if (inspection && rule.Success) rules.Add(RuleLabel(rule.Groups[1].Value) + " · " + rule.Groups[2].Value);
            var mapped = Outcome(line, "Status.VcMapApplied");
            var preview = Outcome(line, "Status.VcMapDry");
            if (mapped.Success || preview.Success)
            {
                var result = mapped.Success ? mapped : preview;
                MappingSummary = mapped.Success
                    ? Loc.Current.T("Status.VcMapApplied", result.Groups["p0"].Value, result.Groups["p1"].Value, result.Groups["p2"].Value, result.Groups["p3"].Value)
                    : Loc.Current.T("Status.VcMapDry", result.Groups["p0"].Value, result.Groups["p1"].Value, result.Groups["p2"].Value);
                Mapped = result.Groups["p0"].Value;
                Unsupported = result.Groups["p2"].Value;
                Failed = mapped.Success ? result.Groups["p3"].Value : "—";
            }
            var sync = Outcome(line, "Status.VcSyncDry");
            if (sync.Success) SyncSummary = Loc.Current.T("Glass.SyncPreview", sync.Groups["p0"].Value, sync.Groups["p2"].Value);
            var synced = Outcome(line, "Status.VcSyncApplied");
            if (synced.Success) SyncSummary = Loc.Current.T("Status.VcSyncApplied", synced.Groups["p0"].Value, synced.Groups["p1"].Value, synced.Groups["p2"].Value);
        }
        Diagnostics = diagnostics;
        Rules = rules;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void Dispose()
    {
        model.Activity.PropertyChanged -= Changed;
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
