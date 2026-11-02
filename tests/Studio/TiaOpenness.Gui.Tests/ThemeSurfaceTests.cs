using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
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
        const string countVariant = "<SolidColorBrush x:Key=\"Primer.countWarningBg\" Color=\"#FFF4E5\"/>";
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
    [InlineData("Light", "#F2F2F7 #FFFFFF #F2F2F7 #E3E3E8 #D1D1D6 #D1D1D6 #F2F2F7 #86868B #E5E5EA #E3E3E8 #FFFFFF #1D1D1F #636366 #636366 #86868B #0066CC #FFFFFF #0071E3 #B25000 #FFF4E5 #1A7431 #E9F7EE #E8F1FC #0066CC #0066CC #F2F2F7 #1D1D1F #1A7431 #D70015 #FFFFFF #FFFFFF #FFF4E5 #0071E3 #FFFFFF #1D1D1F #1D1D1F #0071E3 #0063C7 #0055AB #0055AB #E3E3E8 #FFFFFF #D1D1D6 #D70015 #FDECEC #D70015 #D70015 #BD0012 #A30010 #994500 #16632A #E5E5EA #F2F2F7 #8E8E93 #40000000 #F01E1E1E #FFFFFF #1F000000 #38000000 Transparent")]
    [InlineData("Dark", "#000000 #1C1C1E #2C2C2E #3A3A3C #38383A #38383A #2C2C2E #8E8E93 #3A3A3C #3A3A3C #1C1C1E #F5F5F7 #AEAEB2 #AEAEB2 #8E8E93 #409CFF #000000 #0071E3 #FFB340 #3A2A12 #30D158 #12301B #0A2540 #409CFF #409CFF #2C2C2E #F5F5F7 #30D158 #FF6961 #FFFFFF #1E1E1E #3A2A12 #409CFF #1E1E1E #F5F5F7 #F5F5F7 #0071E3 #0063C7 #0055AB #4095F0 #3A3A3C #FFFFFF #38383A #FF6961 #3B1517 #FF6961 #D70015 #BD0012 #A30010 #994500 #16632A #3A3A3C #2C2C2E #8E8E93 #80000000 #F53A3A3C #FFFFFF #1FFFFFFF #99000000 Transparent")]
    public void Handoff_surface_text_and_semantic_tokens_are_preserved(string theme, string colors)
    {
        wpf.Run(() =>
        {
            var palette = new ResourceDictionary { Source = new Uri(ThemeManager.PackPrefix + "Palette." + theme + ".xaml") };
            string[] keys = ["bg", "card", "cardSoft", "cardSel", "cardBorder", "divider", "input", "inputBorder", "pill", "pillHover", "menuBg", "text", "textMuted", "textFaint", "checkBorder", "accent", "onAccent", "primaryBg", "warn", "warnBg", "ok", "okBg", "noteBg", "noteBorder", "noteAccent", "codeBg", "codeText", "diffGreen", "diffRed", "primaryText", "warningText", "countWarningBg", "focusOuter", "focusInner", "selectedText", "hoverText", "Ant.colorPrimary", "Ant.colorPrimaryHover", "Ant.colorPrimaryActive", "Ant.colorLinkHover", "Ant.controlItemBgActiveHover", "Ant.colorWhite", "Ant.colorBorder", "Ant.colorError", "Ant.colorErrorBg", "Ant.colorErrorBorder", "Ant.colorErrorSolid", "Ant.colorErrorSolidHover", "Ant.colorErrorSolidActive", "Ant.colorWarningBorder", "Ant.colorSuccessBorder", "Ant.colorFillSecondary", "Ant.colorBgContainerDisabled", "Ant.colorTextDisabled", "Ant.colorBgMask", "Ant.colorBgSpotlight", "Ant.colorTextLightSolid", "Ant.boxShadowKey", "Ant.boxShadowUmbra", "Ant.boxShadowAmbient"];
            string[] expected = colors.Split(' ');
            Assert.Equal(keys.Length + 4, palette.Count);
            Assert.Equal(Colors.White, ((SolidColorBrush)palette["Primer.onPrimary"]).Color);
            Assert.Equal(theme == "Light" ? "#FFFFFFFF" : "#FF1E1E1E", ((SolidColorBrush)palette["Primer.window"]).Color.ToString());
            Assert.Equal(theme == "Light" ? "#FFEBEBED" : "#FF2A2A2C", ((SolidColorBrush)palette["Primer.sidebar"]).Color.ToString());
            Assert.Equal(0, ((SolidColorBrush)palette["Primer.hcOutline"]).Color.A);
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

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    public void Control_boundaries_and_focus_contrast_on_all_shared_grounds(string theme)
    {
        wpf.Run(() =>
        {
            var palette = new ResourceDictionary { Source = new Uri(ThemeManager.PackPrefix + "Palette." + theme + ".xaml") };
            Color Token(string key) => ((SolidColorBrush)palette["Primer." + key]).Color;
            foreach (string surface in new[] { "window", "bg", "sidebar", "card" })
            foreach (string boundary in new[] { "inputBorder", "focusOuter" })
            {
                var background = Composite(Token(surface), Token("window"));
                var foreground = Composite(Token(boundary), background);
                double first = Luminance(foreground), second = Luminance(background);
                double ratio = (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
                Assert.True(ratio >= 3, $"{theme}: {boundary} on {surface} = {ratio:F2}, expected 3");
            }
            if (theme != "HighContrast") Assert.Equal(0, Token("hcOutline").A);
            else Assert.Equal(SystemColors.WindowTextColor, Token("hcOutline"));
        });
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    public void Toast_text_and_actions_contrast_with_the_spotlight_surface(string theme)
    {
        wpf.Run(() =>
        {
            var previous = ThemeManager.Current.Theme;
            var previousContrast = ThemeManager.Current.HighContrastOverride;
            ThemeManager.Current.HighContrastOverride = theme == "HighContrast";
            ThemeManager.Current.Theme = theme == "Dark" ? AppTheme.Dark : AppTheme.Light;
            var window = new MainWindow(ProjectPageTestSupport.Model(), false);
            try
            {
                var toast = (Border)window.FindName("Toast");
                toast.Visibility = Visibility.Visible;
                ((TextBlock)window.FindName("ToastTitle")).Text = "Title";
                ((TextBlock)window.FindName("ToastMessage")).Text = "Message";
                toast.Measure(new Size(320, double.PositiveInfinity));
                toast.Arrange(new Rect(0, 0, 320, 200));
                var ground = ((SolidColorBrush)Application.Current.FindResource("Primer.window")).Color;
                var background = Composite(((SolidColorBrush)toast.Background).Color, ground);
                var foregrounds = new[] { "ToastTitle", "ToastMessage" }
                    .Select(name => ((TextBlock)window.FindName(name)).Foreground)
                    .Concat(DesktopCapture.Descendants<Button>(toast).Select(button => button.Foreground)).ToArray();
                Assert.True(foregrounds.Length >= 4);
                foreach (var brush in foregrounds)
                {
                    var foreground = Composite(((SolidColorBrush)brush).Color, background);
                    double first = Luminance(foreground), second = Luminance(background);
                    double ratio = (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
                    Assert.True(ratio >= 4.5, $"{theme}: toast contrast {ratio:F2} < 4.5");
                }
            }
            finally
            {
                window.Close();
                ThemeManager.Current.HighContrastOverride = previousContrast;
                ThemeManager.Current.Theme = previous;
            }
        });
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    public void Default_actions_selected_navigation_and_info_tags_keep_AA_text(string theme)
    {
        wpf.Run(() =>
        {
            var previous = ThemeManager.Current.Theme;
            var previousContrast = ThemeManager.Current.HighContrastOverride;
            ThemeManager.Current.HighContrastOverride = theme == "HighContrast";
            ThemeManager.Current.Theme = theme == "Dark" ? AppTheme.Dark : AppTheme.Light;
            try
            {
                var ground = ((SolidColorBrush)Application.Current.FindResource("Primer.window")).Color;
                void Check(Brush foregroundBrush, Brush backgroundBrush, string role)
                {
                    var background = Composite(((SolidColorBrush)backgroundBrush).Color, ground);
                    var foreground = Composite(((SolidColorBrush)foregroundBrush).Color, background);
                    double first = Luminance(foreground), second = Luminance(background);
                    double ratio = (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
                    Assert.True(ratio >= 4.5, $"{theme}: {role} contrast {ratio:F2} < 4.5");
                }
                foreach (string key in new[] { "Run", "Primary", "WarningAction", "Secondary", "OutlinedAccent", "Danger" })
                {
                    var button = new Button { Content = "Action", Style = (Style)Application.Current.FindResource("Primer." + key) };
                    button.ApplyTemplate();
                    Check(button.Foreground, button.Background, key);
                }
                var navigation = new RadioButton { Content = "Page", IsChecked = true,
                    Style = (Style)Application.Current.FindResource("Primer.NavItem") };
                navigation.ApplyTemplate();
                var surface = (Border)navigation.Template.FindName("Surface", navigation);
                Check(navigation.Foreground, surface.Background, "selected navigation");
                var blockText = new TextBlock { Text = "Block", Style = (Style)Application.Current.FindResource("Primer.Text") };
                var block = new ListBoxItem { DataContext = new { Selected = true }, Content = blockText,
                    Style = (Style)Application.Current.FindResource("Primer.BlockItem") };
                block.ApplyTemplate();
                block.Measure(new Size(160, 40)); block.Arrange(new Rect(0, 0, 160, 40));
                Check(blockText.Foreground, block.Background, "checked block row");
                if (theme == "HighContrast")
                {
                    Assert.Equal(SystemColors.HighlightColor, ((SolidColorBrush)block.Background).Color);
                    Assert.Equal(SystemColors.HighlightTextColor, ((SolidColorBrush)blockText.Foreground).Color);
                }
                var label = new TextBlock { Text = "Info", Tag = ViewModels.FeatureTone.Accent,
                    Style = (Style)Application.Current.FindResource("Primer.ToneText") };
                var tag = new Border { Tag = ViewModels.FeatureTone.Accent, Child = label,
                    Style = (Style)Application.Current.FindResource("Primer.ToneChip") };
                tag.Measure(new Size(160, 40)); tag.Arrange(new Rect(0, 0, 160, 40));
                Check(label.Foreground, tag.Background, "info tag");
            }
            finally
            {
                ThemeManager.Current.HighContrastOverride = previousContrast;
                ThemeManager.Current.Theme = previous;
            }
        });
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Primary_buttons_and_text_selection_use_the_solid_fill_and_white_text(string theme)
    {
        wpf.Run(() =>
        {
            var previous = ThemeManager.Current.Theme;
            var previousContrast = ThemeManager.Current.HighContrastOverride;
            ThemeManager.Current.HighContrastOverride = false;
            ThemeManager.Current.Theme = theme == "Dark" ? AppTheme.Dark : AppTheme.Light;
            try
            {
                Color Token(string key) => ((SolidColorBrush)Application.Current.FindResource("Primer." + key)).Color;
                void Check(Brush foregroundBrush, Brush backgroundBrush)
                {
                    var foreground = ((SolidColorBrush)foregroundBrush).Color;
                    var background = ((SolidColorBrush)backgroundBrush).Color;
                    Assert.Equal(Token("onPrimary"), foreground);
                    Assert.Equal(Token("primaryBg"), background);
                    Assert.NotEqual(Token("accent"), background);
                    double first = Luminance(foreground), second = Luminance(background);
                    double ratio = (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
                    Assert.True(ratio >= 4.5, $"{theme}: solid primary contrast {ratio:F2} < 4.5");
                }
                Assert.Equal(Colors.White, Token("onPrimary"));
                foreach (string key in new[] { "Primary", "Run" })
                {
                    var button = new Button { Content = "Action", Style = (Style)Application.Current.FindResource("Primer." + key) };
                    button.ApplyTemplate();
                    Check(button.Foreground, button.Background);
                }
                foreach (string key in new[] { "Input", "MonoInput" })
                {
                    var input = new TextBox { Style = (Style)Application.Current.FindResource("Primer." + key) };
                    input.ApplyTemplate();
                    Check(input.SelectionTextBrush, input.SelectionBrush);
                }
            }
            finally
            {
                ThemeManager.Current.HighContrastOverride = previousContrast;
                ThemeManager.Current.Theme = previous;
            }
        });
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    public void Primary_and_accent_foreground_brushes_meet_AA_in_every_palette(string theme)
    {
        wpf.Run(() =>
        {
            var palette = new ResourceDictionary { Source = new Uri(ThemeManager.PackPrefix + "Palette." + theme + ".xaml") };
            Color Token(string key) => ((SolidColorBrush)palette[key]).Color;
            (string Foreground, string Background)[] pairs =
            [
                ("Primer.onAccent", "Primer.accent"),
                ("Primer.onPrimary", "Primer.primaryBg"),
                ("Primer.primaryText", "Primer.primaryBg"),
                ("Ant.colorWhite", "Primer.primaryBg"),
                ("Ant.colorWhite", "Ant.colorPrimary"),
                ("Primer.onPrimary", "Ant.colorPrimaryHover"),
                ("Primer.onPrimary", "Ant.colorPrimaryActive"),
                ("Primer.onPrimary", "Ant.colorErrorSolid"),
                ("Primer.onPrimary", "Ant.colorErrorSolidHover"),
                ("Primer.onPrimary", "Ant.colorErrorSolidActive"),
            ];
            foreach (var pair in pairs)
            {
                var background = Composite(Token(pair.Background), Token("Primer.bg"));
                var foreground = Composite(Token(pair.Foreground), background);
                double first = Luminance(foreground), second = Luminance(background);
                double ratio = (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
                Assert.True(ratio >= 4.5, $"{theme}: {pair.Foreground}/{pair.Background} {ratio:F2} < 4.5");
            }

            var previous = ThemeManager.Current.Theme;
            var previousContrast = ThemeManager.Current.HighContrastOverride;
            ThemeManager.Current.HighContrastOverride = theme == "HighContrast";
            ThemeManager.Current.Theme = theme == "Dark" ? AppTheme.Dark : AppTheme.Light;
            try
            {
                var onPrimary = ((SolidColorBrush)Application.Current.FindResource("Primer.onPrimary")).Color;
                var primaryBg = ((SolidColorBrush)Application.Current.FindResource("Primer.primaryBg")).Color;
                foreach (string key in new[] { "Primary", "Run" })
                {
                    var button = new Button { Style = (Style)Application.Current.FindResource("Primer." + key) };
                    button.ApplyTemplate();
                    Assert.Equal(onPrimary, ((SolidColorBrush)button.Foreground).Color);
                    Assert.Equal(primaryBg, ((SolidColorBrush)button.Background).Color);
                }
                foreach (string key in new[] { "Input", "MonoInput" })
                {
                    var input = new TextBox { Style = (Style)Application.Current.FindResource("Primer." + key) };
                    input.ApplyTemplate();
                    Assert.Equal(onPrimary, ((SolidColorBrush)input.SelectionTextBrush).Color);
                    Assert.Equal(primaryBg, ((SolidColorBrush)input.SelectionBrush).Color);
                    Assert.Equal(1, input.SelectionOpacity);
                }
                var secret = new PasswordBox { Style = (Style)Application.Current.FindResource("Primer.SecretInput") };
                secret.ApplyTemplate();
                Assert.Equal(onPrimary, ((SolidColorBrush)secret.SelectionTextBrush).Color);
                Assert.Equal(primaryBg, ((SolidColorBrush)secret.SelectionBrush).Color);
                Assert.Equal(1, secret.SelectionOpacity);
            }
            finally
            {
                ThemeManager.Current.HighContrastOverride = previousContrast;
                ThemeManager.Current.Theme = previous;
            }
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
