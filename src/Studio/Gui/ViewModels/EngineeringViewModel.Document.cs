extern alias PlcRendering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using TiaOpenness.Gui.Services;
using PlcDocumentationLogic = PlcRendering::TiaMcpServer.ModelContextProtocol.PlcDocumentationLogic;
using System.Threading.Tasks;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.ViewModels;

public sealed partial class EngineeringViewModel
{
    public sealed record ReadOnlySnapshot(string File, IReadOnlyList<string> Tree, string BlockName, string Kind, string Language,
        string DocumentPath, IReadOnlyList<string> Lines);
    private ReadOnlySnapshot? _snapshot;
    public ReadOnlySnapshot? Snapshot { get => _snapshot; private set => Set(ref _snapshot, value); }
    internal void CaptureSnapshot(string file)
    {
        if (file.Length == 0 || Devices.Count == 0) return;
        var tree = new List<string>();
        foreach (var device in Devices)
        {
            tree.Add(device.Name);
            if (device != SelectedDevice) continue;
            tree.AddRange(Blocks.Select(row => "    " + row.Path));
            tree.AddRange(Screens.Select(screen => "    " + screen));
        }
        Snapshot = new(file, tree, FocusedBlock?.Name ?? "", FocusedBlock?.Kind ?? "", FocusedBlock?.Language ?? "",
            SourcePath, TextLines.ToArray());
    }
    internal void ClearSnapshot() => Snapshot = null;

    private BlockRow? _interfaceBlock;
    private IReadOnlyList<BlockMember> _members = [];
    public IReadOnlyList<BlockMember> Members
    {
        get => _members;
        private set { if (Set(ref _members, value)) Raise(nameof(HasMembers)); }
    }
    public bool HasMembers => Members.Count > 0;
    public IReadOnlyList<ProjectLadderNetwork> Networks { get; private set; } = [];
    public string Xml { get; private set; } = "";
    private string _xmlFile = "";
    public string DocumentComment { get; private set; } = "";
    public string LogicModeLabel => ShowXml ? "XML" : Loc.Current["Project.LadText"];
    private bool ShowXml => Xml.Length != 0 && FocusedBlock?.HasLadderOrFbd == true;
    private IReadOnlyList<string> _xmlLines = [];
    public IReadOnlyList<string> TextLines => ShowXml ? _xmlLines : SourceLines;

    internal async Task ReadInterfaceAsync()
    {
        if (FocusedBlock is not { } row || SelectedDevice == null || _interfaceBlock == row || _activity.Busy) return;
        int request = _sourceRequest;
        await _activity.Guarded("Project.Interface", async () =>
        {
            var members = await _client.ReadInterfaceAsync(SelectedDevice.Id, row.Path);
            if (_sourceRequest != request) return;
            Members = members; _interfaceBlock = row;
        });
    }

    internal async Task OpenXmlAsync(bool export = false)
    {
        if (FocusedBlock is not { } row || _activity.Busy) return;
        var plc = SelectedDevice;
        await _activity.Guarded("Project.OpenXml", async () =>
        {
            string? path = await SelectXmlAsync(export, plc?.Id, null, row.Path);
            if (path == null || FocusedBlock != row) return;
            string xml = await File.ReadAllTextAsync(path);
            var (document, networks) = await Task.Run(() =>
            {
                var document = PlcDocumentationLogic.RenderText(xml, path).Document;
                bool matches = document.Format == "SimaticML" && document.BlockName == row.Name;
                return (matches ? document : null, matches && row.HasLadderOrFbd ? ProjectLadderNetwork.FromXml(xml) : null);
            });
            if (FocusedBlock != row) return;
            if (document == null)
                throw new InvalidDataException(Loc.Current["Project.XmlMismatch"]);
            Xml = xml;
            _xmlLines = xml.ReplaceLineEndings("\n").Split('\n');
            _xmlFile = Path.GetFileName(path);
            if (networks != null) Networks = networks;
            Raise(nameof(Networks)); Raise(nameof(SourcePath)); Raise(nameof(LineCountLabel)); Raise(nameof(SourceStatus));
            DocumentComment = document.Comment;
            Members = document.Members.Select(m => new BlockMember(m.Section, m.Path, m.Datatype, "")).ToArray();
            _interfaceBlock = row;
            Raise(nameof(Xml)); Raise(nameof(LogicModeLabel)); Raise(nameof(TextLines)); Raise(nameof(DocumentComment));
        });
    }

    public bool CanExportXml => SelectedDevice?.Category == "Plc" && _client is McpProjectClient;
    private async Task<string?> SelectXmlAsync(bool export, string? plc, string? unit, string blockPath)
    {
        if (!export || _client is not McpProjectClient host || plc == null)
            return _dialogs.OpenFiles(Loc.Current["Project.OpenXml"], Loc.Current["Project.XmlFilter"])?.FirstOrDefault();
        var folder = _dialogs.OpenFolder(Loc.Current["Project.ExportLocation"]);
        if (folder == null) return null;
        string file = await host.ExportBlockXmlAsync(plc, blockPath, folder, unit, false);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        if (!File.Exists(file) || !Path.GetFullPath(file).StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(file), ".xml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(Loc.Current["Project.MissingExport"]);
        return file;
    }

    public string UnitSourceFile { get; private set; } = "";
    public string UnitSource { get; private set; } = "";
    private void ClearUnitSource()
    {
        UnitSourceFile = UnitSource = "";
        Raise(nameof(UnitSourceFile)); Raise(nameof(UnitSource));
    }
    internal async Task OpenUnitBlockAsync(SoftwareUnitInfo unit, string name)
    {
        if (_activity.Busy || !unit.Blocks.Contains(name, StringComparer.Ordinal)) return;
        SelectedUnit = unit; Document = ProjectDocument.SoftwareUnit;
        var plc = SelectedDevice;
        await _activity.Guarded("Project.OpenXml", async () =>
        {
            string? path = _client is McpProjectClient { SupportsUnitExport: true }
                ? await SelectXmlAsync(true, plc?.Id, unit.Name, Uri.EscapeDataString(name))
                : _dialogs.OpenFiles(Loc.Current.T("Project.UnitExport", unit.Name, name), Loc.Current["Project.XmlFilter"])?.FirstOrDefault();
            if (path == null || SelectedUnit != unit) return;
            string xml = await File.ReadAllTextAsync(path);
            var rendered = await Task.Run(() => PlcDocumentationLogic.RenderText(xml, path));
            if (SelectedUnit != unit || Document != ProjectDocument.SoftwareUnit) return;
            if (rendered.Document.Format != "SimaticML" || rendered.Document.BlockName != name)
                throw new InvalidDataException(Loc.Current["Project.XmlMismatch"]);
            UnitSourceFile = path; UnitSource = xml;
            Raise(nameof(UnitSourceFile)); Raise(nameof(UnitSource));
        });
    }

    private int _sourceRequest;
    private BlockRow? _readBlock;
    private int? _tagCount;
    private LocalizedText _sourceStatus = LocalizedText.Key("Project.FocusBlock");
    public int? TagCount { get => _tagCount; private set => Set(ref _tagCount, value); }
    public IReadOnlyList<string> SourceLines { get; private set; } = [];
    public bool SourceFailed { get; private set; }
    public string SourceErrorTitle => Loc.Current.T("Shell.SourceError", FocusedBlock?.Name ?? "");
    public string SourceStatus => ShowXml ? "" : _sourceStatus.Resolve();
    public string SourcePath => ShowXml ? _xmlFile : FocusedBlock is { } row
        ? string.Join("/", new[] { SelectedDevice?.Name, row.Info.FolderPath, row.FileName }.Where(s => !string.IsNullOrEmpty(s))) : "";
    public string LineCountLabel => TextLines.Count switch
    {
        0 => "", 1 => Loc.Current["Project.OneLine"], _ => Loc.Current.T("Project.Lines", TextLines.Count),
    };
    public int? DiagnosticCount => LastCompile is { } result ? result.ErrorCount + result.WarningCount : null;
    public IEnumerable<CompileMessage> CompilerMessages => LastCompile?.Messages ?? Enumerable.Empty<CompileMessage>();
    public string CompileSummary
    {
        get
        {
            if (LastCompile is not { } result) return Loc.Current["Pages.CompileHint"];
            string state = result.State is "Success" or "Warning" && result.ErrorCount == 0 ? Loc.Current["Project.Compiled"] : result.State;
            string errors = result.ErrorCount == 1 ? Loc.Current["Project.OneError"] : Loc.Current.T("Project.Errors", result.ErrorCount);
            string warnings = result.WarningCount == 1 ? Loc.Current["Project.OneWarning"] : Loc.Current.T("Project.Warnings", result.WarningCount);
            return string.Join(" · ", state, errors, warnings);
        }
    }

    private void ResetSource()
    {
        _sourceRequest++;
        _readBlock = _interfaceBlock = null;
        Xml = _xmlFile = DocumentComment = ""; _xmlLines = [];
        Members = [];
        Raise(nameof(Xml)); Raise(nameof(LogicModeLabel)); Raise(nameof(DocumentComment));
        SetSource([], FocusedBlock == null ? LocalizedText.Key("Project.FocusBlock") : LocalizedText.Key("Project.ReadCode"));
        Raise(nameof(SourcePath)); Raise(nameof(SourceErrorTitle));
    }

    private void SetSource(IReadOnlyList<string> lines, LocalizedText status, bool failed = false)
    {
        SourceFailed = failed;
        Raise(nameof(SourceFailed));
        RetrySource.RaiseCanExecuteChanged();
        SourceLines = lines;
        if (Xml.Length == 0) Networks = FocusedBlock?.HasLadderOrFbd == true ? ProjectLadderNetwork.FromListing(lines) : [];
        Raise(nameof(Networks)); Raise(nameof(TextLines));
        _sourceStatus = status;
        Raise(nameof(SourceLines)); Raise(nameof(SourceStatus)); Raise(nameof(LineCountLabel));
    }

    private async Task ResolveLadderMetadataAsync(string plc, int request)
    {
        var networks = Networks;
        var calls = networks.SelectMany(n => n.Rungs).Where(r => r.IsBox).Select(r => r.Box).Distinct(StringComparer.OrdinalIgnoreCase);
        var interfaces = new Dictionary<string, IReadOnlyList<BlockMember>>(StringComparer.OrdinalIgnoreCase);
        foreach (string call in calls)
        {
            var targets = Blocks.Where(b => string.Equals(b.Name, call, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (targets.Length != 1 || targets[0].Info.IsKnowHowProtected) continue;
            try { interfaces[call] = await _client.ReadInterfaceAsync(plc, targets[0].Path); }
            catch (Exception error) { _activity.Append(error.Message); }
            if (request != _sourceRequest) return;
        }

        IReadOnlyList<TagInfo> tags = [];
        if (networks.SelectMany(n => n.Rungs).SelectMany(r => r.Elements).Any(e => !e.Operand.StartsWith('#')))
        {
            // Only PLC-root tags can resolve symbols in these PLC-root blocks.
            try { tags = await _client.ListPlcTagsAsync(plc, "", includeUnits: false); }
            catch (Exception error) { _activity.Append(error.Message); }
            if (request != _sourceRequest) return;
        }
        string? Section(int _, string block, string pin) => interfaces.TryGetValue(block, out var members)
            ? members.FirstOrDefault(m => string.Equals(m.Name, pin, StringComparison.OrdinalIgnoreCase))?.Section : null;
        Networks = ProjectLadderNetwork.WithMetadata(networks, tags, Section);
        Raise(nameof(Networks));
    }

    public async Task ReadFocusedBlockAsync()
    {
        if (FocusedBlock is not { } row || SelectedDevice is not { } plc || row == _readBlock) return;
        if (row.Info.IsKnowHowProtected || !row.Info.IsConsistent)
        {
            SetSource([], LocalizedText.Key(row.Info.IsKnowHowProtected ? "Block.Protected" : "Block.NeedsCompiling"));
            return;
        }
        if (_activity.Busy) return;
        int request = ++_sourceRequest;
        _readBlock = row;
        SetSource([], LocalizedText.Key("Project.LoadingCode"));
        await _activity.Guarded("Project.LoadingCode", async () =>
        {
            try
            {
                string source = await _client.DescribeBlockLogicAsync(plc.Id, row.Path);
                if (request != _sourceRequest) return;
                if (string.IsNullOrWhiteSpace(source))
                {
                    SetSource([], LocalizedText.Key("Project.NoSource"));
                    _activity.SetStatus("Project.NoSource", kind: null);
                    return;
                }
                string normalized = source.ReplaceLineEndings("\n");
                if (normalized.EndsWith('\n')) normalized = normalized[..^1];
                SetSource(normalized.Split('\n'), LocalizedText.Empty);
                if (Networks.Count != 0) await ResolveLadderMetadataAsync(plc.Id, request);
                if (request != _sourceRequest) return;
                if (SourceLines.Count == 1) _activity.SetStatus("Project.OneLine", kind: null);
                else _activity.SetStatus("Project.Lines", kind: null, SourceLines.Count);
            }
            catch (Exception error)
            {
                if (request != _sourceRequest) return;
                _readBlock = null;
                SetSource([], LocalizedText.Literal(error.Message), failed: true);
                throw;
            }
        });
    }
}
