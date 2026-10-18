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
        // The warning-count colour stays exact; its brush is defined in the light palette.
        const string countVariant = "<SolidColorBrush x:Key=\"Primer.countWarningBg\" Color=\"#FFFBE6\"/>";
        var lightPalette = SourceScan.Markup.Single(file => file.Name.EndsWith(".Themes.Palette.Light.xaml", StringComparison.Ordinal));
        Assert.Single(Regex.Matches(lightPalette.Text, Regex.Escape(countVariant)));
        var brushesOutsidePalettes = SourceScan.Markup
            .Where(file => !file.Name.Contains(".Themes.Palette.", StringComparison.Ordinal))
            .SelectMany(file => System.Xml.Linq.XDocument.Parse(file.Text).Descendants()
                .Where(element => element.Name.LocalName.EndsWith("Brush", StringComparison.Ordinal)));
        Assert.Empty(brushesOutsidePalettes);
        // Transparent carries no hue and intentionally lets the active palette show through.
        var violations = SourceScan.Markup.Concat(SourceScan.Code)
            .Where(file => !file.Name.Contains(".Themes.Palette.", StringComparison.Ordinal))
            .SelectMany(file => literal.Matches(file.Text)
                .Select(match => file.Name + ": " + match.Value));
        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("Light", "#F5F5F5 #FFFFFF #FAFAFA #E6F4FF #F0F0F0 #0F050505 #FFFFFF #8C8C8C #FFFFFF #F5F5F5 #FFFFFF #E0000000 #A6000000 #A6000000 #8C8C8C #0958D9 #FFFFFF #0958D9 #874D00 #FFFBE6 #237804 #F6FFED #E6F4FF #91CAFF #0958D9 #F5F5F5 #E0000000 #237804 #CF1322 #FFFFFF #FFFFFF #FFFBE6 #1677FF #FFFFFF #E0000000 #E0000000 #1677FF #003EB3 #002C8C #003EB3 #BAE0FF #FFFFFF #D9D9D9 #D9363E #FFF2F0 #FFCCC7 #D9363E #CF1322 #A8071A #FFE58F #B7EB8F #0F000000 #0A000000 #40000000 #73000000 #D9000000 #FFFFFF #1F000000 #14000000 #0D000000")]
    [InlineData("Dark", "#000000 #141414 #1D1D1D #111A2C #303030 #1FFDFDFD #141414 #737373 #141414 #262626 #1F1F1F #D9FFFFFF #A6FFFFFF #A6FFFFFF #737373 #3C89E8 #141414 #1668DC #D89614 #2B2111 #49AA19 #162312 #111A2C #15325B #3C89E8 #1F1F1F #D9FFFFFF #49AA19 #E86E6B #FFFFFF #141414 #2B2111 #3C89E8 #141414 #D9FFFFFF #D9FFFFFF #1668DC #1554AD #15417E #65A9F3 #112545 #FFFFFF #424242 #E86E6B #2C1618 #5B2526 #AD393A #7E2E2F #5B2526 #594214 #274916 #1FFFFFFF #14FFFFFF #40FFFFFF #73000000 #424242 #FFFFFF #1F000000 #14000000 #0D000000")]
    public void Handoff_surface_text_and_semantic_tokens_are_preserved(string theme, string colors)
    {
        wpf.Run(() =>
        {
            var palette = new ResourceDictionary { Source = new Uri(ThemeManager.PackPrefix + "Palette." + theme + ".xaml") };
            string[] keys = ["bg", "card", "cardSoft", "cardSel", "cardBorder", "divider", "input", "inputBorder", "pill", "pillHover", "menuBg", "text", "textMuted", "textFaint", "checkBorder", "accent", "onAccent", "primaryBg", "warn", "warnBg", "ok", "okBg", "noteBg", "noteBorder", "noteAccent", "codeBg", "codeText", "diffGreen", "diffRed", "primaryText", "warningText", "countWarningBg", "focusOuter", "focusInner", "selectedText", "hoverText", "Ant.colorPrimary", "Ant.colorPrimaryHover", "Ant.colorPrimaryActive", "Ant.colorLinkHover", "Ant.controlItemBgActiveHover", "Ant.colorWhite", "Ant.colorBorder", "Ant.colorError", "Ant.colorErrorBg", "Ant.colorErrorBorder", "Ant.colorErrorSolid", "Ant.colorErrorSolidHover", "Ant.colorErrorSolidActive", "Ant.colorWarningBorder", "Ant.colorSuccessBorder", "Ant.colorFillSecondary", "Ant.colorBgContainerDisabled", "Ant.colorTextDisabled", "Ant.colorBgMask", "Ant.colorBgSpotlight", "Ant.colorTextLightSolid", "Ant.boxShadowKey", "Ant.boxShadowUmbra", "Ant.boxShadowAmbient"];
            string[] expected = colors.Split(' ');
            Assert.Equal(keys.Length, palette.Count);
            for (int i = 0; i < keys.Length; i++)
                Assert.Equal((Color)ColorConverter.ConvertFromString(expected[i]), ((SolidColorBrush)palette[keys[i].StartsWith("Ant.", StringComparison.Ordinal) ? keys[i] : "Primer." + keys[i]]).Color);
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
            foreach (string text in new[] { "ok", "diffRed", "warn" }) Check(text, "card", 4.5);
            Check("textFaint", "card", 4.5);
            Check("warn", "warnBg", 4.5);
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
