using System.Windows;

namespace TiaOpenness.Gui.Controls;

/// <summary>
/// Attached properties the themed templates read.
///
/// These exist so the whole UI can stay built from stock WPF controls: a field with a grey prompt
/// is a <see cref="System.Windows.Controls.TextBox"/> that happens to carry a
/// <see cref="PlaceholderProperty"/>, not a bespoke control with its own bugs. Subclassing would
/// also have cost every one of these the default styling, keyboard handling and automation peers
/// that come for free.
/// </summary>
public static class Ux
{
    /// <summary>Grey prompt shown inside an empty text field or an unset pop-up button.</summary>
    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.RegisterAttached(
            "Placeholder", typeof(string), typeof(Ux), new PropertyMetadata(string.Empty));

    public static string GetPlaceholder(DependencyObject d) => (string)d.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject d, string value) => d.SetValue(PlaceholderProperty, value);

    /// <summary>
    /// Per-instance corner rounding. The button template reads it with a TemplateBinding, which is
    /// what lets one template serve the 8px buttons and the 7px segments without a second copy.
    /// </summary>
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.RegisterAttached(
            "CornerRadius", typeof(CornerRadius), typeof(Ux), new PropertyMetadata(new CornerRadius(8)));

    public static CornerRadius GetCornerRadius(DependencyObject d) => (CornerRadius)d.GetValue(CornerRadiusProperty);

    public static void SetCornerRadius(DependencyObject d, CornerRadius value) => d.SetValue(CornerRadiusProperty, value);
}
