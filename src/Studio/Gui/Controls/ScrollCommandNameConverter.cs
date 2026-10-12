using System;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.Controls;

public sealed class ScrollCommandNameConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.FirstOrDefault() is RoutedCommand command ? command.Name switch
        {
            "LineUp" or "PageUp" => Loc.Current["Access.ScrollUp"],
            "LineDown" or "PageDown" => Loc.Current["Access.ScrollDown"],
            "LineLeft" or "PageLeft" => Loc.Current["Access.ScrollLeft"],
            "LineRight" or "PageRight" => Loc.Current["Access.ScrollRight"],
            _ => Loc.Current["Access.Scroll"],
        } : Loc.Current["Access.Scroll"];
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
