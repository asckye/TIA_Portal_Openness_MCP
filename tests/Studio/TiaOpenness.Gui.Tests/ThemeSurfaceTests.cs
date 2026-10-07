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
    public void Colors_live_in_the_palettes_and_the_vocabulary_warning_count_variant()
    {
        var literal = new Regex(@"#[\da-fA-F]{3,8}\b|\b(?:Colors|Brushes)\.(?!Transparent\b)\w+|\bColor\.From\w+");
        // The authoritative count-badge vocabulary specifies peach separately from the 29 palette tokens.
        const string countVariant = "<SolidColorBrush x:Key=\"Primer.CountWarningLight\" Color=\"#FFF1E5\"/>";
        var vocabulary = SourceScan.Markup.Single(file => file.Name.EndsWith(".Themes.Primer.xaml", StringComparison.Ordinal));
        Assert.Single(Regex.Matches(vocabulary.Text, Regex.Escape(countVariant)));
        // Transparent carries no hue and intentionally lets the active palette show through.
        var violations = SourceScan.Markup.Concat(SourceScan.Code)
            .Where(file => !file.Name.Contains(".Themes.Palette.", StringComparison.Ordinal))
            .SelectMany(file => literal.Matches(file.Name == vocabulary.Name ? file.Text.Replace(countVariant, "") : file.Text)
                .Select(match => file.Name + ": " + match.Value));
        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("Light", "#F6F8FA #FFFFFF #F6F8FA #F0F8FA #D0D7DE #D8DEE4 #FFFFFF #D0D7DE #F6F8FA #EAEEF2 #FFFFFF #1F2328 #656D76 #8C959F #8C959F #0B7A99 #FFFFFF #1F883D #9A6700 #FFF8C5 #1A7F37 #DAFBE1 #DDF4FF #6654AEFF #0969DA #F6F8FA #1F2328 #1A7F37 #CF222E")]
    [InlineData("Dark", "#0D1117 #161B22 #0D1117 #1A2730 #30363D #21262D #0D1117 #30363D #21262D #30363D #161B22 #E6EDF3 #8D96A0 #6E7681 #6E7681 #4FC3E0 #0D1117 #238636 #D29922 #26D29922 #3FB950 #263FB950 #26388BFD #66388BFD #58A6FF #0D1117 #E6EDF3 #3FB950 #F85149")]
    public void Handoff_surface_text_and_semantic_tokens_are_preserved(string theme, string colors)
    {
        wpf.Run(() =>
        {
            var palette = new ResourceDictionary { Source = new Uri(ThemeManager.PackPrefix + "Palette." + theme + ".xaml") };
            string[] keys = ["bg", "card", "cardSoft", "cardSel", "cardBorder", "divider", "input", "inputBorder", "pill", "pillHover", "menuBg", "text", "textMuted", "textFaint", "checkBorder", "accent", "onAccent", "primaryBg", "warn", "warnBg", "ok", "okBg", "noteBg", "noteBorder", "noteAccent", "codeBg", "codeText", "diffGreen", "diffRed"];
            string[] expected = colors.Split(' ');
            Assert.Equal(keys.Length, palette.Count);
            for (int i = 0; i < keys.Length; i++)
                Assert.Equal((Color)ColorConverter.ConvertFromString(expected[i]), ((SolidColorBrush)palette["Primer." + keys[i]]).Color);
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
            Color Token(string key) => ((SolidColorBrush)palette["Primer." + key]).Color;
            var window = Token("bg");
            void Check(string text, string surface, double minimum = 4.5)
            {
                var background = Composite(Token(surface), window);
                var foreground = Composite(Token(text), background);
                double first = Luminance(foreground), second = Luminance(background);
                double contrast = (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
                Assert.True(contrast >= minimum, $"{theme}: {text} on {surface} = {contrast:F2}, expected {minimum}");
            }
            foreach (string surface in new[] { "bg", "card", "input" })
                foreach (string text in new[] { "text", "textMuted" }) Check(text, surface);
            Check("codeText", "codeBg");
            Check("text", "pill");
            Check("accent", "cardSel", 3);
            // The handoff uses lighter semantic colors and faint labels; assert their exact tokens separately.
            foreach (string text in new[] { "ok", "diffRed", "warn" }) Check(text, "card", 2.5);
            Check("textFaint", "card", 2);
            Check("warn", "warnBg", 2.5);
            Check("text", "cardSel");
            Check("onAccent", "accent");
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
