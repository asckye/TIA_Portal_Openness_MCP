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
    public void Long_page_titles_keep_character_ellipsis_with_the_explicit_style()
    {
        wpf.Run(() =>
        {
            var window = new MainWindow(ProjectPageTestSupport.Model(), false);
            try
            {
                var host = WorkbenchRenderTests.CreateHost(window, 1024, 640);
                var title = (TextBlock)window.FindName("CurrentPageTitle");
                title.Text = new string('W', 128);
                host.UpdateLayout();
                Assert.Equal(TextTrimming.CharacterEllipsis, title.TextTrimming);
                Assert.Equal(TextWrapping.NoWrap, title.TextWrapping);
                Assert.True(title.ActualWidth > 0);
                var fullTitle = new TextBlock { Text = title.Text, FontFamily = title.FontFamily,
                    FontSize = title.FontSize, FontWeight = title.FontWeight };
                fullTitle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.True(fullTitle.DesiredSize.Width > title.ActualWidth);
            }
            finally { window.Close(); }
        });
    }

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
    [InlineData("Light",
        "#F8FAFD #FFFFFF #F3F6FA #EBEBED #DBDCDF #EBEBED #EAEFF6 #7B8797 #EAEFF6 #F2F2F3 #FFFFFF #0F172A #475569 #57657A " +
        "#7B8797 #1D4ED8 #FFFFFF #2563EB #9A5504 #F8EEE6 #0E7A50 #E8F2EC #EFF6FF #1D4ED8 #1D4ED8 #F6F8FC #0F172A #0E7A50 " +
        "#B91C1C #FFFFFF #FFFFFF #FFF4E5 #1D4ED8 #FFFFFF #0F172A #0F172A #2563EB #1D4ED8 #1E40AF #1E40AF #EBEBED #FFFFFF " +
        "#DBDCDF #DC2626 #FCEBEB #DC2626 #DC2626 #B91C1C #991B1B #9A5504 #0E7A50 #EAEFF6 #F3F6FA #8B97A8 #0F172A #FFFFFF " +
        "#0F172A #EAECF0 #EFF1F5 Transparent")]
    [InlineData("Dark",
        "#141B26 #1B2332 #101620 #222E42 #363F4F #2A3242 #232E40 #6D7D94 #232E40 #272F3D #1B2332 #F1F5F9 #94A3B8 #8B9BB2 " +
        "#6D7D94 #7BB4FF #0B0F15 #2563EB #FBBF24 #2D220E #34D399 #0F2920 #132139 #60A5FA #60A5FA #0C1017 #F1F5F9 #6EE7B7 " +
        "#FDA4AF #FFFFFF #0B0F15 #2D220E #60A5FA #0B0F15 #F1F5FA #F1F5FA #2563EB #1D4ED8 #1E40AF #93C5FD #2F3745 #FFFFFF " +
        "#363F4F #FB7185 #362C3C #FB7185 #E11D48 #B91C1C #991B1B #D97706 #10B981 #232E40 #1A2332 #6B7B91 #070A0F #1B2332 " +
        "#F1F5F9 #0D1219 #0F141C Transparent")]
    [InlineData("Light.Petrol",
        "#F8FAFD #FFFFFF #F3F6FA #EBEBED #DBDCDF #EBEBED #EAEFF6 #7B8797 #EAEFF6 #F2F2F3 #FFFFFF #0F172A #475569 #57657A " +
        "#7B8797 #0B656E #FFFFFF #0E7C86 #9A5504 #F8EEE6 #0E7A50 #E8F2EC #EBF7F8 #0B656E #0B656E #F6F8FC #0F172A #0E7A50 " +
        "#B91C1C #FFFFFF #FFFFFF #FFF4E5 #0B656E #FFFFFF #0F172A #0F172A #0E7C86 #0B656E #084F56 #084F56 #EBEBED #FFFFFF " +
        "#DBDCDF #DC2626 #FCEBEB #DC2626 #DC2626 #B91C1C #991B1B #9A5504 #0E7A50 #EAEFF6 #F3F6FA #8B97A8 #0F172A #FFFFFF " +
        "#0F172A #EAECF0 #EFF1F5 Transparent")]
    [InlineData("Dark.Petrol",
        "#141B26 #1B2332 #101620 #222E42 #363F4F #2A3242 #232E40 #6D7D94 #232E40 #272F3D #1B2332 #F1F5F9 #94A3B8 #8B9BB2 " +
        "#6D7D94 #5EEAD4 #0B0F15 #0E7C86 #FBBF24 #2D220E #34D399 #0F2920 #0C2A2C #2DD4BF #2DD4BF #0C1017 #F1F5F9 #6EE7B7 " +
        "#FDA4AF #FFFFFF #0B0F15 #2D220E #2DD4BF #0B0F15 #F1F5FA #F1F5FA #0E7C86 #0B656E #084F56 #5EEAD4 #2F3745 #FFFFFF " +
        "#363F4F #FB7185 #362C3C #FB7185 #E11D48 #B91C1C #991B1B #D97706 #10B981 #232E40 #1A2332 #6B7B91 #070A0F #1B2332 " +
        "#F1F5F9 #0D1219 #0F141C Transparent")]
    public void Handoff_surface_text_and_semantic_tokens_are_preserved(string theme, string colors)
    {
        wpf.Run(() =>
        {
            var palette = new ResourceDictionary { Source = new Uri(ThemeManager.PackPrefix + "Palette." + theme + ".xaml") };
            string[] keys = ["bg", "card", "cardSoft", "cardSel", "cardBorder", "divider", "input", "inputBorder", "pill", "pillHover", "menuBg", "text", "textMuted", "textFaint", "checkBorder", "accent", "onAccent", "primaryBg", "warn", "warnBg", "ok", "okBg", "noteBg", "noteBorder", "noteAccent", "codeBg", "codeText", "diffGreen", "diffRed", "primaryText", "warningText", "countWarningBg", "focusOuter", "focusInner", "selectedText", "hoverText", "Ant.colorPrimary", "Ant.colorPrimaryHover", "Ant.colorPrimaryActive", "Ant.colorLinkHover", "Ant.controlItemBgActiveHover", "Ant.colorWhite", "Ant.colorBorder", "Ant.colorError", "Ant.colorErrorBg", "Ant.colorErrorBorder", "Ant.colorErrorSolid", "Ant.colorErrorSolidHover", "Ant.colorErrorSolidActive", "Ant.colorWarningBorder", "Ant.colorSuccessBorder", "Ant.colorFillSecondary", "Ant.colorBgContainerDisabled", "Ant.colorTextDisabled", "Ant.colorBgMask", "Ant.colorBgSpotlight", "Ant.colorTextLightSolid", "Ant.boxShadowKey", "Ant.boxShadowUmbra", "Ant.boxShadowAmbient"];
            string[] expected = colors.Split(' ');
            bool light = theme.StartsWith("Light", StringComparison.Ordinal);
            Assert.Equal(keys.Length + 50, palette.Count);
            Assert.Equal(light ? "#FFB91C1C" : "#FFFCA5A5",
                ((SolidColorBrush)palette["Primer.destructiveText"]).Color.ToString());
            Assert.Equal(Colors.White, ((SolidColorBrush)palette["Primer.onPrimary"]).Color);
            Assert.Equal(light ? "#FFFFFFFF" : "#FF1B2332", ((SolidColorBrush)palette["Primer.window"]).Color.ToString());
            Assert.Equal(light ? "#FFF3F6FA" : "#FF101620", ((SolidColorBrush)palette["Primer.sidebar"]).Color.ToString());
            Assert.Equal(0, ((SolidColorBrush)palette["Primer.hcOutline"]).Color.A);
            for (int i = 0; i < keys.Length; i++)
                Assert.Equal((Color)ColorConverter.ConvertFromString(expected[i]), ((SolidColorBrush)palette[keys[i].StartsWith("Ant.", StringComparison.Ordinal) ? keys[i] : "Primer." + keys[i]]).Color);
        });
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("Light.Petrol")]
    [InlineData("Dark.Petrol")]
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
    [InlineData("Light.Petrol")]
    [InlineData("Dark.Petrol")]
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
    [InlineData("Light.Petrol")]
    [InlineData("Dark.Petrol")]
    [InlineData("HighContrast")]
    public void Toast_text_and_actions_contrast_with_the_spotlight_surface(string theme)
    {
        wpf.Run(() =>
        {
            var previous = ThemeManager.Current.Theme;
            var previousAccent = ThemeManager.Current.Accent;
            var previousContrast = ThemeManager.Current.HighContrastOverride;
            ThemeManager.Current.HighContrastOverride = theme == "HighContrast";
            ThemeManager.Current.Theme = theme.StartsWith("Dark", StringComparison.Ordinal) ? AppTheme.Dark : AppTheme.Light;
            ThemeManager.Current.Accent = theme.EndsWith(".Petrol", StringComparison.Ordinal) ? AppAccent.Petrol : AppAccent.Cobalt;
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
                foreach (var kind in Enum.GetValues<ViewModels.ToastKind>())
                {
                    window.ShowToast(kind, Localization.LocalizedText.Literal("Message"), "sample");
                    var dot = Composite(((SolidColorBrush)window.ToastDot.Fill).Color, background);
                    double dotLight = Luminance(dot), backLight = Luminance(background);
                    double contrast = (Math.Max(dotLight, backLight) + .05) / (Math.Min(dotLight, backLight) + .05);
                    Assert.True(contrast >= 3, $"{theme} {kind}: dot contrast {contrast:F2} < 3");
                }
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
                ThemeManager.Current.Accent = previousAccent;
            }
        });
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("Light.Petrol")]
    [InlineData("Dark.Petrol")]
    [InlineData("HighContrast")]
    public void Default_actions_selected_navigation_and_info_tags_keep_AA_text(string theme)
    {
        wpf.Run(() =>
        {
            var previous = ThemeManager.Current.Theme;
            var previousAccent = ThemeManager.Current.Accent;
            var previousContrast = ThemeManager.Current.HighContrastOverride;
            ThemeManager.Current.HighContrastOverride = theme == "HighContrast";
            ThemeManager.Current.Theme = theme.StartsWith("Dark", StringComparison.Ordinal) ? AppTheme.Dark : AppTheme.Light;
            ThemeManager.Current.Accent = theme.EndsWith(".Petrol", StringComparison.Ordinal) ? AppAccent.Petrol : AppAccent.Cobalt;
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
                ThemeManager.Current.Accent = previousAccent;
            }
        });
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("Light.Petrol")]
    [InlineData("Dark.Petrol")]
    public void Primary_buttons_and_text_selection_use_the_solid_fill_and_white_text(string theme)
    {
        wpf.Run(() =>
        {
            var previous = ThemeManager.Current.Theme;
            var previousAccent = ThemeManager.Current.Accent;
            var previousContrast = ThemeManager.Current.HighContrastOverride;
            ThemeManager.Current.HighContrastOverride = false;
            ThemeManager.Current.Theme = theme.StartsWith("Dark", StringComparison.Ordinal) ? AppTheme.Dark : AppTheme.Light;
            ThemeManager.Current.Accent = theme.EndsWith(".Petrol", StringComparison.Ordinal) ? AppAccent.Petrol : AppAccent.Cobalt;
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
                ThemeManager.Current.Accent = previousAccent;
            }
        });
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("Light.Petrol")]
    [InlineData("Dark.Petrol")]
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
            var previousAccent = ThemeManager.Current.Accent;
            var previousContrast = ThemeManager.Current.HighContrastOverride;
            ThemeManager.Current.HighContrastOverride = theme == "HighContrast";
            ThemeManager.Current.Theme = theme.StartsWith("Dark", StringComparison.Ordinal) ? AppTheme.Dark : AppTheme.Light;
            ThemeManager.Current.Accent = theme.EndsWith(".Petrol", StringComparison.Ordinal) ? AppAccent.Petrol : AppAccent.Cobalt;
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
                ThemeManager.Current.Accent = previousAccent;
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
