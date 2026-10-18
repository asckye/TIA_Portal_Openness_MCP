using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using TiaOpenness.Gui.Themes;

namespace TiaOpenness.Gui.Controls;

/// <summary>Selected rows keep system highlight/text pairs even inside coloured data templates.</summary>
public static class ContrastSelection
{
    public static readonly DependencyProperty ActiveProperty = DependencyProperty.RegisterAttached(
        "Active", typeof(bool), typeof(ContrastSelection), new PropertyMetadata(false, Changed));
    public static bool GetActive(DependencyObject element) => (bool)element.GetValue(ActiveProperty);
    public static void SetActive(DependencyObject element, bool value) => element.SetValue(ActiveProperty, value);
    private static readonly string[] TextKeys = ["text", "textMuted", "textFaint", "accent", "ok", "warn", "diffRed", "diffGreen", "codeText", "noteAccent"];
    private static readonly string[] SurfaceKeys = ["card", "cardSel", "cardSoft", "pill", "pillHover", "input", "codeBg", "warnBg", "okBg", "noteBg"];
    // Ant tokens drawn inside rows: primary graphics swap to the selection text colour and the
    // glyphs drawn on them to the selection colour, so a checked box stays visible on the highlight.
    private static readonly string[] AntTextKeys = ["Ant.colorPrimary"];
    private static readonly string[] AntSurfaceKeys = ["Ant.colorWhite"];
    private static readonly DependencyProperty SavedProperty = DependencyProperty.RegisterAttached(
        "Saved", typeof(Dictionary<string, object?>), typeof(ContrastSelection));

    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Control control) return;
        if (e.NewValue is true)
        {
            var saved = new Dictionary<string, object?>();
            var resources = TextKeys.Select(key => ("Primer." + key, true)).Concat(SurfaceKeys.Append("onAccent")
                .Select(key => ("Primer." + key, false))).Concat(AntTextKeys.Select(key => (key, true)))
                .Concat(AntSurfaceKeys.Select(key => (key, false)));
            foreach (var (resource, text) in resources)
            {
                saved[resource] = control.Resources.Contains(resource) ? control.Resources[resource] : null;
                var brush = new SolidColorBrush();
                BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty, new Binding(text
                    ? nameof(ThemeManager.SelectionTextColor) : nameof(ThemeManager.SelectionColor))
                    { Source = ThemeManager.Current, Mode = BindingMode.OneWay });
                control.Resources[resource] = brush;
            }
            control.SetValue(SavedProperty, saved);
        }
        else if (control.GetValue(SavedProperty) is Dictionary<string, object?> saved)
        {
            foreach (var (key, value) in saved)
                if (value == null) control.Resources.Remove(key); else control.Resources[key] = value;
            control.ClearValue(SavedProperty);
        }
    }
}
