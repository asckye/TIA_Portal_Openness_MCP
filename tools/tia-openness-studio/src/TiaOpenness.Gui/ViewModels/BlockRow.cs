using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.ViewModels;

/// <summary>A block row with the selection state the export list needs.</summary>
public sealed class BlockRow(BlockInfo info) : ObservableObject
{
    private bool _selected;

    public BlockInfo Info { get; } = info;
    public string Path => Info.Path;
    public string Name => Info.Name;
    public string Kind => Info.Kind.ToString();
    public string Number => Info.Number?.ToString() ?? "-";
    public string Language => Info.ProgrammingLanguage ?? "-";
    public string Author => Info.HeaderAuthor ?? "-";

    /// <summary>Short reason the block cannot be exported, or empty when it can.</summary>
    public string Status =>
        Info.IsKnowHowProtected ? Loc.Current["Block.Protected"]
        : !Info.IsConsistent ? Loc.Current["Block.NeedsCompiling"]
        : string.Empty;

    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    /// <summary>Called when the language changes; <see cref="Status"/> is translated on read.</summary>
    public void RefreshLocalizedText() => Raise(nameof(Status));
}

