using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TiaOpenness.Shared;

namespace TiaOpenness.Gui.ControlChannel;

internal sealed record PrefillTarget(WorkbenchPrefillForm Form, string[] Fields, string Executes);

internal sealed class PrefillRegistry
{
    internal static IReadOnlyList<PrefillTarget> Targets { get; } = Array.AsReadOnly(new[]
    {
        new PrefillTarget(WorkbenchPrefillForm.InspectionRules, ["namePattern"], "none"),
        new PrefillTarget(WorkbenchPrefillForm.BlockFilter, ["filter"], "none"),
        new PrefillTarget(WorkbenchPrefillForm.BlockSelection, ["softwarePath", "blockPaths"], "none"),
        new PrefillTarget(WorkbenchPrefillForm.ConnectProject, ["projectPath"], "mcp"),
        new PrefillTarget(WorkbenchPrefillForm.ExportBlocks, ["outputDirectory", "format"], "mcp"),
        new PrefillTarget(WorkbenchPrefillForm.ImportBlocks, ["paths", "overwrite"], "mcp"),
    });
    private readonly Dictionary<WorkbenchPrefillForm, WorkbenchPrefillState> _pending = [];
    private readonly List<WorkbenchPrefillForm> _order = [];
    internal WorkbenchPrefillState? Latest => _order.Count == 0 ? null : _pending[_order[^1]];
    internal bool Contains(WorkbenchPrefillForm form) => _pending.ContainsKey(form);
    internal bool CanChange(WorkbenchPrefillForm form, WorkbenchControlOrigin origin)
        => !_pending.TryGetValue(form, out var pending) || ControlResponse.OriginKey(pending.Origin) == ControlResponse.OriginKey(origin);
    internal static bool Valid(WorkbenchPrefillArguments arguments)
    {
        if (arguments.Mode == WorkbenchPrefillMode.Clear) return true;
        if (arguments.Fields is WorkbenchConnectProjectFields connect)
            return AbsolutePath(connect.ProjectPath) && Regex.IsMatch(connect.ProjectPath!, @"\.(ap|als)[0-9]+(?:_[0-9]+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (arguments.Fields is WorkbenchExportBlocksFields export)
            return AbsolutePath(export.OutputDirectory) && export.Format is "SimaticMl" or "Source";
        if (arguments.Fields is WorkbenchImportBlocksFields import)
            return import.Paths is { Length: > 0 and <= 256 } && import.Paths.All(AbsolutePath) && import.Overwrite != null;
        if (arguments.Fields is WorkbenchInspectionRulesFields { NamePattern: { } pattern })
        {
            try { _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException) /* swallow(ui): invalid patterns are rejected before changing any field */ { return false; }
        }
        return true;
    }
    private static bool AbsolutePath(string? path) => path is { Length: > 0 and <= 4096 }
        && !path.Any(c => char.IsControl(c) || "<>\"|?*".Contains(c))
        && (Regex.IsMatch(path, @"^[A-Za-z]:[\\/]") && !path.Substring(2).Contains(':')
            || Regex.IsMatch(path, @"^\\\\[^\\/]+[\\/][^\\/]+(?:[\\/]|$)") && !path.Contains(':'));
    internal void Set(WorkbenchPrefillArguments arguments, WorkbenchControlOrigin origin)
    {
        _order.Remove(arguments.Form);
        if (arguments.Mode == WorkbenchPrefillMode.Clear) _pending.Remove(arguments.Form);
        else
        {
            _pending[arguments.Form] = new() { Form = arguments.Form, Fields = arguments.Fields, Origin = origin };
            _order.Add(arguments.Form);
        }
    }
    internal void ClearByHuman() { _pending.Clear(); _order.Clear(); }
}
