using System.Linq;
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
    public bool HasLadderOrFbd => Language.ToUpperInvariant() is "LAD" or "FBD" or "F-LAD" or "F-FBD";
    public string Subtitle => string.Join(" · ", new[] { Kind + (Info.Number == null ? "" : " " + Number), Info.ProgrammingLanguage }
        .Where(value => !string.IsNullOrWhiteSpace(value)));
    public string FileName => Name + (string.IsNullOrEmpty(Info.ProgrammingLanguage) ? "" : "." + Language.ToLowerInvariant());
    private bool _modified;
    public bool IsModified { get => _modified; internal set => Set(ref _modified, value); }
    private bool _focused;
    public bool IsFocused { get => _focused; internal set => Set(ref _focused, value); }
    public string Author => Info.HeaderAuthor ?? "-";

    /// <summary>Short reason the block cannot be exported, or empty when it can.</summary>
    public string Status =>
        Info.IsKnowHowProtected ? Loc.Current["Block.Protected"]
        : !Info.IsConsistent ? Loc.Current["Block.NeedsCompiling"]
        : string.Empty;

    public bool HasStatus => Info.IsKnowHowProtected || !Info.IsConsistent;

    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    /// <summary>Called when the language changes; <see cref="Status"/> is translated on read.</summary>
    public void RefreshLocalizedText() => Raise(nameof(Status));
}

