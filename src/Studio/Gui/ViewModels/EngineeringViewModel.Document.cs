using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.ViewModels;

public sealed partial class EngineeringViewModel
{
    private int _sourceRequest;
    private BlockRow? _readBlock;
    private int? _tagCount;
    private LocalizedText _sourceStatus = LocalizedText.Key("Project.FocusBlock");
    public int? TagCount { get => _tagCount; private set => Set(ref _tagCount, value); }
    public IReadOnlyList<string> SourceLines { get; private set; } = [];
    public string SourceStatus => _sourceStatus.Resolve();
    public string SourcePath => FocusedBlock is { } row
        ? string.Join("/", new[] { SelectedDevice?.Name, row.Info.FolderPath, row.FileName }.Where(s => !string.IsNullOrEmpty(s))) : "";
    public string LineCountLabel => SourceLines.Count switch
    {
        0 => "", 1 => Loc.Current["Project.OneLine"], _ => Loc.Current.T("Project.Lines", SourceLines.Count),
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
        _readBlock = null;
        SetSource([], FocusedBlock == null ? LocalizedText.Key("Project.FocusBlock") : LocalizedText.Key("Project.ReadCode"));
        Raise(nameof(SourcePath));
    }

    private void SetSource(IReadOnlyList<string> lines, LocalizedText status)
    {
        SourceLines = lines;
        _sourceStatus = status;
        Raise(nameof(SourceLines)); Raise(nameof(SourceStatus)); Raise(nameof(LineCountLabel));
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
                    _activity.SetStatus("Project.NoSource");
                    return;
                }
                string normalized = source.ReplaceLineEndings("\n");
                if (normalized.EndsWith('\n')) normalized = normalized[..^1];
                SetSource(normalized.Split('\n'), LocalizedText.Empty);
                if (SourceLines.Count == 1) _activity.SetStatus("Project.OneLine");
                else _activity.SetStatus("Project.Lines", SourceLines.Count);
            }
            catch (Exception error)
            {
                if (request != _sourceRequest) return;
                _readBlock = null;
                SetSource([], LocalizedText.Literal(error.Message));
                throw;
            }
        });
    }
}
