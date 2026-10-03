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
        bool zh = Loc.Current.IsChinese;
        return (string)parameter switch
        {
            "group" => GroupPath(value.ToString() ?? ""),
            "version" => value.ToString()!.Replace("TIA V", "V"),
            "export" => (bool)value ? (zh ? "导出 · 源文本" : "Export · Source text") : Loc.Current["Glass.ExportXml"],
            "consistent" => (bool)value ? (zh ? "是" : "Yes") : (zh ? "否" : "No"),
            "protected" => (bool)value ? (zh ? "受保护" : "Protected") : "—",
            "compare" => (VcCompareState)value switch
            {
                VcCompareState.Equal => zh ? "相同" : "Identical",
                VcCompareState.Unequal => zh ? "有差异" : "Differs",
                VcCompareState.WorkspaceFileMissing => zh ? "缺失" : "Missing",
                _ => zh ? "未知" : "Unknown",
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
    public GlassResults(MainViewModel model)
    {
        this.model = model;
        model.PropertyChanged += Changed;
        model.VcStatusItems.CollectionChanged += CollectionChanged;
        model.VcDiffLines.CollectionChanged += CollectionChanged;
        model.BlocksView.CollectionChanged += CollectionChanged;
        Loc.Current.LanguageChanged += LanguageChanged;
        Refresh();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string DiffAdded => "+" + model.VcDiffLines.Count(l=>l.Kind==DiffLineKind.Added);
    public string DiffRemoved => "−" + model.VcDiffLines.Count(l=>l.Kind==DiffLineKind.Removed);
    public IReadOnlyList<MappedObjectInfo> OtherMappedFiles => model.VcStatusItems
        .Where(i=>i!=model.SelectedVcItem && i.CompareState!=VcCompareState.Equal).Take(3).ToArray();
    public string BlocksSummary => Loc.Current.IsChinese
        ? $"{model.Blocks.Count} 个程序块 · {model.Blocks.Count(b=>b.Selected)} 已选 · {model.BlocksView.Cast<BlockRow>().Count()} 显示"
        : $"{model.Blocks.Count} blocks · {model.Blocks.Count(b=>b.Selected)} selected · {model.BlocksView.Cast<BlockRow>().Count()} shown";
    public string DifferenceLabel => (Loc.Current.IsChinese ? "差异" : "Differ") + " · " + model.VcStatusItems.Count(i=>i.CompareState==VcCompareState.Unequal);
    public string MissingLabel => (Loc.Current.IsChinese ? "缺失" : "Missing") + " · " + model.VcStatusItems.Count(i=>i.CompareState==VcCompareState.WorkspaceFileMissing);
    public string WorkspaceSummary => model.SelectedWorkspace is null ? model.WorkspaceRootDisplay
        : Loc.Current.IsChinese ? $"{model.WorkspaceRootDisplay} · {model.SelectedWorkspace.MappedObjectCount} 已映射 · {model.VcStatusItems.Count(i=>i.CompareState!=VcCompareState.Equal)} 存在差异"
        : $"{model.WorkspaceRootDisplay} · {model.SelectedWorkspace.MappedObjectCount} mapped · {model.VcStatusItems.Count(i=>i.CompareState!=VcCompareState.Equal)} differ";
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
        if (e.PropertyName is nameof(MainViewModel.Log) or nameof(MainViewModel.ProjectPath)) Refresh();
        else if (e.PropertyName is nameof(MainViewModel.SelectionSummary) or nameof(MainViewModel.WorkspaceRootDisplay) or nameof(MainViewModel.SelectedVcItem))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
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
        "NAMING-001" => Loc.Current.IsChinese ? "命名" : "Naming",
        "DOC-001" => Loc.Current.IsChinese ? "作者" : "Author",
        "BUILD-001" => Loc.Current.IsChinese ? "一致性" : "Consistency",
        "PROT-001" => Loc.Current.IsChinese ? "专有技术" : "Know-how",
        "DEAD-001" => Loc.Current.IsChinese ? "未使用" : "Unused",
        _ => id,
    };

    private void Refresh()
    {
        string notRun = Loc.Current.IsChinese ? "未执行" : "Not run";
        Errors = Warnings = Mapped = Unsupported = Failed = "—";
        InspectionTime = "";
        CompileState = InspectionSummary = MappingSummary = SyncSummary = notRun;
        var diagnostics = new List<Diagnostic>();
        var pendingDiagnostics = new List<Diagnostic>();
        var rules = new List<string>();
        bool inspection = false;
        foreach (string raw in model.Log.Split('\n'))
        {
            string line = Regex.Replace(raw.TrimEnd('\r'), @"^\d{2}:\d{2}:\d{2}\s+", "");
            var compile = Outcome(line, "Status.CompileResult");
            if (compile.Success)
            {
                Errors = compile.Groups["p1"].Value;
                Warnings = compile.Groups["p2"].Value;
                CompileState = Errors != "0" ? (Loc.Current.IsChinese ? "错误" : "Errors")
                    : Warnings != "0" ? (Loc.Current.IsChinese ? "警告" : "Warnings")
                    : (Loc.Current.IsChinese ? "已完成" : "Completed");
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
                InspectionSummary = Loc.Current.IsChinese
                    ? $"已检查 {inspected.Groups["p1"].Value} 个程序块，发现 {inspected.Groups["p0"].Value} 处问题。"
                    : $"Checked {inspected.Groups["p1"].Value} blocks, found {inspected.Groups["p0"].Value} issues.";
                if (!rules.Any(r => r.StartsWith(Loc.Current.IsChinese ? "专有技术" : "Know-how", StringComparison.Ordinal)))
                    rules.Add((Loc.Current.IsChinese ? "专有技术" : "Know-how") + " · 0");
                inspection = false;
            }
            var rule = Regex.Match(line, @"^([^\s]+) \((\d+)\)$");
            if (inspection && rule.Success) rules.Add(RuleLabel(rule.Groups[1].Value) + " · " + rule.Groups[2].Value);
            var mapped = Outcome(line, "Status.VcMapApplied");
            var preview = Outcome(line, "Status.VcMapDry");
            if (mapped.Success || preview.Success)
            {
                var result = mapped.Success ? mapped : preview;
                MappingSummary = line;
                Mapped = result.Groups["p0"].Value;
                Unsupported = result.Groups["p2"].Value;
                Failed = mapped.Success ? result.Groups["p3"].Value : "—";
            }
            var sync = Outcome(line, "Status.VcSyncDry");
            if (sync.Success) SyncSummary = Loc.Current.IsChinese
                ? $"预览：{sync.Groups["p0"].Value} 个对象需要同步，{sync.Groups["p2"].Value} 个已相同。"
                : $"Preview: {sync.Groups["p0"].Value} objects expected to sync · {sync.Groups["p2"].Value} already identical.";
            if (Outcome(line, "Status.VcSyncApplied").Success) SyncSummary = line;
        }
        Diagnostics = diagnostics;
        Rules = rules;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void Dispose()
    {
        model.PropertyChanged -= Changed;
        model.VcStatusItems.CollectionChanged -= CollectionChanged;
        model.VcDiffLines.CollectionChanged -= CollectionChanged;
        model.BlocksView.CollectionChanged -= CollectionChanged;
        Loc.Current.LanguageChanged -= LanguageChanged;
    }
}
