using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

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
    /// what lets one template serve the 6px buttons and the 4px small buttons without a second copy.
    /// </summary>
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.RegisterAttached(
            "CornerRadius", typeof(CornerRadius), typeof(Ux), new PropertyMetadata(new CornerRadius(8)));

    public static CornerRadius GetCornerRadius(DependencyObject d) => (CornerRadius)d.GetValue(CornerRadiusProperty);

    public static void SetCornerRadius(DependencyObject d, CornerRadius value) => d.SetValue(CornerRadiusProperty, value);

    /// <summary>
    /// The Ant button type the shared button template draws: an empty value is the default
    /// button, otherwise Primary, Outlined, Danger, DangerSolid, Dashed, Text, Link, Segment, Tag
    /// or Badge. It is separate from <c>Tag</c> because segments and filters carry data there.
    /// </summary>
    public static readonly DependencyProperty VariantProperty =
        DependencyProperty.RegisterAttached(
            "Variant", typeof(string), typeof(Ux), new PropertyMetadata(string.Empty));

    public static string GetVariant(DependencyObject d) => (string)d.GetValue(VariantProperty);

    public static void SetVariant(DependencyObject d, string value) => d.SetValue(VariantProperty, value);

    /// <summary>A glyph from Icons.xaml that a menu item or icon button draws beside or instead of its text.</summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached(
            "Icon", typeof(Geometry), typeof(Ux), new PropertyMetadata(null));

    public static Geometry? GetIcon(DependencyObject d) => (Geometry?)d.GetValue(IconProperty);

    public static void SetIcon(DependencyObject d, Geometry? value) => d.SetValue(IconProperty, value);

    /// <summary>
    /// Set on the collapsed side menu and inherited by its items, which then show only their icon
    /// (Ant Sider collapsedWidth).
    /// </summary>
    public static readonly DependencyProperty CompactProperty =
        DependencyProperty.RegisterAttached(
            "Compact", typeof(bool), typeof(Ux), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetCompact(DependencyObject d) => (bool)d.GetValue(CompactProperty);

    public static void SetCompact(DependencyObject d, bool value) => d.SetValue(CompactProperty, value);

    /// <summary>
    /// Makes a <see cref="Decorator"/> (a badge) report through <see cref="ChildHiddenProperty"/>
    /// whether its child is collapsed. <c>Decorator.Child</c> is not a dependency property, so a
    /// style binding to <c>Child.Visibility</c> resolves before the child is assigned and never
    /// updates; this binds to the child itself once it exists.
    /// </summary>
    public static readonly DependencyProperty TracksChildVisibilityProperty =
        DependencyProperty.RegisterAttached(
            "TracksChildVisibility", typeof(bool), typeof(Ux), new PropertyMetadata(false, OnTracksChildVisibility));

    public static bool GetTracksChildVisibility(DependencyObject d) => (bool)d.GetValue(TracksChildVisibilityProperty);

    public static void SetTracksChildVisibility(DependencyObject d, bool value) => d.SetValue(TracksChildVisibilityProperty, value);

    public static readonly DependencyProperty ChildHiddenProperty =
        DependencyProperty.RegisterAttached(
            "ChildHidden", typeof(bool), typeof(Ux), new PropertyMetadata(false));

    public static bool GetChildHidden(DependencyObject d) => (bool)d.GetValue(ChildHiddenProperty);

    public static void SetChildHidden(DependencyObject d, bool value) => d.SetValue(ChildHiddenProperty, value);

    private static void OnTracksChildVisibility(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Decorator decorator || e.NewValue is not true) return;
        BindChild(decorator);
        // Style setters run while the XAML is still being read, before the child is assigned.
        decorator.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => BindChild(decorator)));
    }

    private static void BindChild(Decorator decorator)
    {
        if (decorator.Child is not UIElement child || BindingOperations.IsDataBound(decorator, ChildHiddenProperty)) return;
        BindingOperations.SetBinding(decorator, ChildHiddenProperty,
            new Binding(nameof(UIElement.Visibility)) { Source = child, Converter = CollapsedConverter.Instance });
    }

    private sealed class CollapsedConverter : IValueConverter
    {
        internal static readonly CollapsedConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
