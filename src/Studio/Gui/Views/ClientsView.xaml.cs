using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using TiaMcpConfigurator;

namespace TiaOpenness.Gui.Views;

public partial class ClientsView : UserControl
{
    public ClientsView() => InitializeComponent();

    public static readonly DependencyProperty MonogramProperty = DependencyProperty.RegisterAttached(
        "Monogram", typeof(ClientProfile), typeof(ClientsView), new PropertyMetadata(null, OnMonogramChanged));

    public static ClientProfile? GetMonogram(Border badge) => (ClientProfile?)badge.GetValue(MonogramProperty);
    public static void SetMonogram(Border badge, ClientProfile? value) => badge.SetValue(MonogramProperty, value);

    private static void OnMonogramChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not ClientProfile profile) return;
        var badge = (Border)sender;
        var (letters, hue) = profile.Id switch
        {
            "claude-code" => ("CC", "Claude"),
            "codex" => ("CX", "Codex"),
            "gemini" => ("GM", "Gemini"),
            "cursor" => ("CR", "Cursor"),
            "vscode" => ("VS", "VSCode"),
            "opencode" => ("OC", "OpenCode"),
            "qwen" => ("QW", "Qwen"),
            "kimi" => ("KM", "Kimi"),
            "codebuddy" => ("YB", "Yuanbao"),
            _ => (Initials(profile.DisplayName), "Neutral")
        };
        badge.Tag = letters;
        badge.SetResourceReference(Border.BackgroundProperty, "Primer.client" + hue + "Bg");
        badge.BorderThickness = new Thickness(0);
        badge.CornerRadius = new CornerRadius(8);
        badge.SetResourceReference(TextElement.ForegroundProperty, "Primer.client" + hue + "Fg");
    }

    private static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(words.Take(2).Select(word => StringInfo.GetNextTextElement(word))).ToUpperInvariant();
    }
}

// The tile paints its own selection outline. Keep the row's native focus and selection semantics.
public sealed class ClientChoiceList : ListBox
{
    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        var row = (ListBoxItem)element;
        row.Margin = new Thickness(5);
        TiaOpenness.Gui.Controls.ContrastSelection.SetActive(row, false);
    }
}

public sealed class ClientTileColumnsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width ? width switch { < 570 => 1, < 850 => 2, _ => 3 } : 1;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
