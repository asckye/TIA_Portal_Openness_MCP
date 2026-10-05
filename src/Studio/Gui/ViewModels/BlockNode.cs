using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.ViewModels;

/// <summary>
/// One row of the block tree: either a folder, or a block that carries the same
/// <see cref="BlockRow"/> the export works from.
///
/// The tree mirrors the TIA project tree — the categories, then the block folders as the engineer
/// made them — rather than the slash-separated paths the bridge speaks in. Those paths are how the
/// protocol addresses a block; they are not how anyone thinks about their program.
///
/// Folders own no selection state of their own. Ticking one ticks the blocks underneath, and a
/// folder's own tick box only reports what its blocks add up to, so the tree and the export list
/// can never disagree about what was selected.
/// </summary>
public sealed class BlockNode : ObservableObject
{
    private bool _isExpanded = true;

    private BlockNode(string name, BlockRow? row)
    {
        Name = name;
        Row = row;
    }

    public string Name { get; }

    /// <summary>The block this row stands for, or null when it is a folder.</summary>
    public BlockRow? Row { get; }

    public ObservableCollection<BlockNode> Children { get; } = [];

    public bool IsFolder => Row is null;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>
    /// Tri-state: null when only some of a folder's blocks are selected. Setting it pushes the
    /// value down to every block underneath.
    /// </summary>
    public bool? IsSelected
    {
        get
        {
            if (Row is not null) return Row.Selected;

            var leaves = Leaves().ToList();
            if (leaves.Count == 0) return false;
            if (leaves.All(l => l.Selected)) return true;
            return leaves.Any(l => l.Selected) ? null : false;
        }
        set
        {
            var selected = value ?? false;
            if (Row is not null)
            {
                Row.Selected = selected;
                return;
            }
            foreach (var leaf in Leaves()) leaf.Selected = selected;
        }
    }

    // ---- what a row shows -------------------------------------------------

    /// <summary>
    /// For a folder, how many blocks it holds, so a collapsed one still says how much is inside.
    /// For a block, its kind and number. A bare count reads the same in both languages and does not
    /// repeat the word the folder is already named after.
    /// </summary>
    public string Detail => IsFolder
        ? "(" + Leaves().Count() + ")"
        : Row!.Kind + (Row.Number == "-" ? string.Empty : " " + Row.Number);

    public string Language => Row?.Language ?? string.Empty;
    public string Author => Row?.Author ?? string.Empty;
    public string Status => Row?.Status ?? string.Empty;

    /// <summary>Re-reads everything derived from the blocks underneath.</summary>
    public void Refresh()
    {
        foreach (var child in Children) child.Refresh();
        Raise(nameof(IsSelected));
        Raise(nameof(Detail));
        Raise(nameof(Status));
    }

    private IEnumerable<BlockRow> Leaves()
    {
        if (Row is not null)
        {
            yield return Row;
            yield break;
        }
        foreach (var leaf in Children.SelectMany(c => c.Leaves())) yield return leaf;
    }

    // ---- building ---------------------------------------------------------

    /// <summary>
    /// Builds the tree TIA shows: the categories it splits a PLC's software into, then the folders
    /// the engineer made inside them.
    ///
    /// Splitting by category first is not decoration. Block paths and data-type paths are both
    /// relative to their own group, so a UDT named <c>Axis</c> at the root and a block named
    /// <c>Axis</c> at the root have the same path; only the category tells them apart.
    /// </summary>
    public static IReadOnlyList<BlockNode> Build(IEnumerable<BlockRow> rows)
    {
        var categories = new List<BlockNode>();

        foreach (var group in rows.GroupBy(r => CategoryOf(r.Info.Kind)).OrderBy(g => g.Key))
        {
            var category = new BlockNode(CategoryLabel(group.Key), null);
            foreach (var row in group) Place(category, row);
            Sort(category);
            categories.Add(category);
        }

        return categories;
    }

    /// <summary>Which part of the TIA tree a block belongs under.</summary>
    private static int CategoryOf(BlockKind kind) => kind switch
    {
        BlockKind.UDT => 1,
        BlockKind.TagTable => 2,
        BlockKind.HmiScreen => 3,
        _ => 0,
    };

    /// <summary>
    /// Named here rather than through a key variable so the catalogue's own consistency test can
    /// see that these entries are used — it looks for keys written at the call site.
    /// </summary>
    private static string CategoryLabel(int category) => category switch
    {
        1 => Loc.Current["Tree.DataTypes"],
        2 => Loc.Current["Tree.Tags"],
        3 => Loc.Current["Tree.HmiScreens"],
        _ => Loc.Current["Tree.ProgramBlocks"],
    };

    /// <summary>Walks the block's folder path, creating folders as needed, and hangs it at the end.</summary>
    private static void Place(BlockNode category, BlockRow row)
    {
        var parent = category;

        // The folder comes from the block's own folder path, never from splitting its full path:
        // real block names contain slashes ("FB4 select / request from panel"), and splitting one
        // of those invents a folder that does not exist.
        var segments = row.Info.FolderPath is { Length: > 0 } folder
            ? folder.Split('/')
            : [];

        for (var i = 0; i < segments.Length; i++)
        {
            var name = segments[i];
            var existing = parent.Children.FirstOrDefault(c => c.IsFolder && c.Name == name);
            if (existing is null)
            {
                existing = new BlockNode(name, null);
                parent.Children.Add(existing);
            }
            parent = existing;
        }

        parent.Children.Add(new BlockNode(row.Name, row));
    }

    /// <summary>Folders before blocks, each alphabetical — the order TIA uses.</summary>
    private static void Sort(BlockNode node)
    {
        var ordered = node.Children
            .OrderBy(c => c.IsFolder ? 0 : 1)
            .ThenBy(c => c.Name, System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        node.Children.Clear();
        foreach (var child in ordered)
        {
            node.Children.Add(child);
            Sort(child);
        }
    }
}
