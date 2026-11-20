using System;
using System.Collections.Generic;
using System.Windows.Data;

namespace TiaOpenness.Gui.ViewModels;

internal sealed record CommandPaletteItem(string Title, string Detail, Action Execute);

internal sealed class CommandPaletteViewModel(List<CommandPaletteItem> items)
{
    public ListCollectionView Results { get; } = new(items);

    internal void Search(string query)
    {
        string term = query.Trim();
        Results.Filter = term.Length == 0 ? null : value => value is CommandPaletteItem item
            && (item.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || item.Detail.Contains(term, StringComparison.CurrentCultureIgnoreCase));
    }
}
