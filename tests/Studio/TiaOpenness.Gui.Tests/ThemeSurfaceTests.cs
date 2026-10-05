using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using TiaOpenness.Gui.Themes;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class ThemeSurfaceTests(WpfContext wpf)
{
    [Fact]
    public void Colors_live_only_in_the_palettes()
    {
        var literal = new Regex(@"#[\da-fA-F]{3,8}\b|\b(?:Colors|Brushes)\.(?!Transparent\b)\w+|\bColor\.From\w+");
        // Transparent carries no hue and intentionally lets the active palette show through.
        var violations = SourceScan.Markup.Concat(SourceScan.Code)
            .Where(file => !file.Name.Contains(".Themes.Palette.", StringComparison.Ordinal))
            .SelectMany(file => literal.Matches(file.Text).Select(match => file.Name + ": " + match.Value));
        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("Light", "#E7ECF1", "#B8FFFFFF", "#15202B", "#5B6B7A", "#8A98A6", "#3E9D62", "#B8741A", "#C25A5A")]
    [InlineData("Dark", "#0B1420", "#0FFFFFFF", "#E6EBEF", "#8A98A6", "#5F6F7D", "#6FCF97", "#F0B35A", "#F08080")]
    public void Handoff_surface_text_and_semantic_tokens_are_preserved(string theme, params string[] expected)
    {
        wpf.Run(() =>
        {
            var palette = new ResourceDictionary { Source = new Uri(ThemeManager.PackPrefix + "Palette." + theme + ".xaml") };
            string[] keys = ["WindowBackground", "CardBackground", "Label", "SecondaryLabel", "TertiaryLabel", "Green", "Orange", "Red"];
            for (int i = 0; i < keys.Length; i++)
                Assert.Equal((Color)ColorConverter.ConvertFromString(expected[i]), ((SolidColorBrush)palette["Ui." + keys[i]]).Color);
            Assert.Equal(theme == "Dark" ? 0 : .08, ((System.Windows.Media.Effects.DropShadowEffect)palette["Glass.SurfaceShadow"]).Opacity);
        });
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Text_contrasts_with_every_surface_where_it_is_used(string theme)
    {
        wpf.Run(() =>
        {
            var palette = new ResourceDictionary { Source = new Uri(ThemeManager.PackPrefix + "Palette." + theme + ".xaml") };
            Color Token(string key) => ((SolidColorBrush)palette["Ui." + key]).Color;
            var window = Token("WindowBackground");
            void Check(string text, string surface, double minimum = 4.5)
            {
                var background = Composite(Token(surface), window);
                var foreground = Composite(Token(text), background);
                double first = Luminance(foreground), second = Luminance(background);
                double contrast = (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
                Assert.True(contrast >= minimum, $"{theme}: {text} on {surface} = {contrast:F2}, expected {minimum}");
            }
            foreach (string surface in new[] { "WindowBackground", "CardBackground", "FieldBackground" })
                foreach (string text in new[] { "Label", "SecondaryLabel" }) Check(text, surface);
            foreach (string surface in new[] { "CardBackground", "InsetBackground" }) Check("LogText", surface);
            Check("Label", "ChipBackground");
            Check("AccentText", "AccentSoft", 3);
            // The handoff uses lighter semantic colors and faint labels; assert their exact tokens separately.
            foreach (string text in new[] { "Green", "Red", "Orange" }) Check(text, "CardBackground", 2.5);
            Check("TertiaryLabel", "CardBackground", 2);
            Check("Orange", "WarningSoft", 2.5);
            Check("SelectionText", "SelectionFill");
            Check("OnAccent", "Accent");
        });
    }

    internal static Color Composite(Color foreground, Color background)
    {
        double alpha = foreground.A / 255.0;
        return Color.FromRgb((byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)),
            (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
    }

    internal static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            double value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }
}
