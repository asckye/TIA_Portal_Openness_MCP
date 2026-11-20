using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace TiaOpenness.Gui.Views;

public partial class ClientsView : UserControl
{
    public ClientsView() => InitializeComponent();
}

public sealed class ClientTileColumnsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width ? width switch { <= 940 => 1, < 1024 => 2, _ => 3 } : 1;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class ClientMonogramConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string name) return "";
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => "",
            1 => StringInfo.GetNextTextElement(words[0]).ToUpperInvariant(),
            _ => (StringInfo.GetNextTextElement(words[0]) + StringInfo.GetNextTextElement(words[1])).ToUpperInvariant()
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
