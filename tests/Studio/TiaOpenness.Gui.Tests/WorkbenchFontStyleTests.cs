using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TiaMcpConfigurator;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Themes;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Gui.Views;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class WorkbenchFontStyleTests(WpfContext wpf)
{
    [Fact]
    public void Bundled_font_tokens_use_the_private_folder_and_explicit_fallbacks()
    {
        wpf.Run(() =>
        {
            var resources = new ResourceDictionary();
            Assert.True(WorkbenchFonts.Apply(resources, AppContext.BaseDirectory));
            foreach (var (key, fallback) in new[] { ("Ui.Font", WorkbenchFonts.UiFallback), ("Ui.FontMono", WorkbenchFonts.MonoFallback) })
            {
                var family = Assert.IsType<FontFamily>(resources[key]);
                Assert.Equal("./#Maple Mono CN, " + fallback, family.Source);
                Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Fonts") + Path.DirectorySeparatorChar, family.BaseUri.LocalPath);
                Assert.Equal(family.Source, ((FontFamily)Application.Current.FindResource(key)).Source);
            }
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Fonts", "LICENSE.txt")));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_empty_font_folder_uses_only_the_system_fallbacks(bool emptyFolder)
    {
        wpf.Run(() =>
        {
            string root = Path.Combine(AppContext.BaseDirectory, "font-fallback-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (emptyFolder) Directory.CreateDirectory(Path.Combine(root, "Fonts"));
                var resources = new ResourceDictionary();
                Assert.False(WorkbenchFonts.Apply(resources, root));
                Assert.Equal("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI", ((FontFamily)resources["Ui.Font"]).Source);
                Assert.Equal("Cascadia Mono, Consolas, Microsoft YaHei UI", ((FontFamily)resources["Ui.FontMono"]).Source);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
    }

    [Theory]
    [InlineData(400, "Regular")]
    [InlineData(600, "SemiBold")]
    public void Maple_faces_resolve_to_the_unmodified_private_files_without_synthetic_weight(int weight, string face)
    {
        wpf.Run(() =>
        {
            var folder = new Uri(Path.Combine(AppContext.BaseDirectory, "Fonts") + Path.DirectorySeparatorChar);
            var family = new FontFamily(folder, "./#Maple Mono CN");
            var typeface = new Typeface(family, FontStyles.Normal, FontWeight.FromOpenTypeWeight(weight), FontStretches.Normal);
            Assert.True(typeface.TryGetGlyphTypeface(out var glyph));
            Assert.Equal(Path.Combine(folder.LocalPath, "MapleMono-CN-" + face + ".ttf"), glyph.FontUri.LocalPath, ignoreCase: true);
            Assert.Contains((int)'中', glyph.CharacterToGlyphMap.Keys);
            Assert.Contains((int)'A', glyph.CharacterToGlyphMap.Keys);
            Assert.False(typeface.IsBoldSimulated);
            Assert.False(typeface.IsObliqueSimulated);
        });
    }

    [Fact]
    public void Main_window_and_message_dialog_disable_ligatures_and_inherit_the_ui_font()
    {
        wpf.Run(() =>
        {
            var main = new MainWindow(ProjectPageTestSupport.Model(), false);
            var dialog = new WorkbenchMessageBox("a -> b != c", "Font fixture", MessageBoxButton.OKCancel, MessageBoxImage.None);
            try
            {
                foreach (var window in new Window[] { main, dialog })
                {
                    Assert.False(Typography.GetStandardLigatures(window));
                    Assert.False(Typography.GetContextualLigatures(window));
                    Assert.False(Typography.GetContextualAlternates(window));
                    Assert.Equal(((FontFamily)Application.Current.FindResource("Ui.Font")).Source, window.FontFamily.Source);
                    var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                    content.Measure(new Size(1200, 780));
                    content.Arrange(new Rect(0, 0, 1200, 780));
                    content.UpdateLayout();
                    var texts = Descendants<TextBlock>(content).ToArray();
                    Assert.NotEmpty(texts);
                    foreach (var text in texts)
                    {
                        Assert.False(Typography.GetStandardLigatures(text));
                        Assert.False(Typography.GetContextualLigatures(text));
                        Assert.False(Typography.GetContextualAlternates(text));
                    }
                }
            }
            finally { dialog.Close(); main.Close(); }
        });
    }

    [Fact]
    public void Selected_sider_item_is_semibold_and_keeps_its_bar()
    {
        wpf.Run(() =>
        {
            var item = new RadioButton { Content = "Version control", Style = (Style)Application.Current.FindResource("Primer.NavItem") };
            item.ApplyTemplate();
            item.IsChecked = true;
            Assert.Equal(FontWeights.SemiBold, item.FontWeight);
            Assert.Equal(Visibility.Visible, ((Border)item.Template.FindName("Bar", item)).Visibility);
            item.IsChecked = false;
            Assert.Equal(FontWeights.Normal, item.FontWeight);
            Assert.Equal(Visibility.Hidden, ((Border)item.Template.FindName("Bar", item)).Visibility);
        });
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(1044)]
    [InlineData(1200)]
    [InlineData(1366)]
    public void Approval_choices_have_equal_secondary_cells_and_wrap_plain_labels(int width)
    {
        foreach (var language in new[] { AppLanguage.Chinese, AppLanguage.English })
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            wpf.RunWithLanguage(language, () =>
            {
                var previous = ThemeManager.Current.Theme;
                ThemeManager.Current.Theme = theme;
                using var model = new FeaturePagesViewModel(FeaturePageFixtures.Approvals(), new FeaturePageFixtures.Journal(),
                    new FeaturePageFixtures.Audit(), new FeaturePageFixtures.Environment(), new FeaturePageFixtures.Diagnostics(), () => FeaturePageFixtures.Now);
                var view = new ApprovalsDrawer { DataContext = model };
                var host = new Border { Child = view, Width = Math.Min(width - 32, 480) };
                host.SetResourceReference(TextElement.FontFamilyProperty, "Ui.Font");
                Typography.SetStandardLigatures(host, false);
                Typography.SetContextualLigatures(host, false);
                Typography.SetContextualAlternates(host, false);
                try
                {
                    void Layout()
                    {
                        host.Measure(new Size(host.Width, 780)); host.Arrange(new Rect(0, 0, host.Width, 780)); host.UpdateLayout();
                        WpfContext.Drain(); host.UpdateLayout();
                    }
                    Layout();
                    var approve = Descendants<Button>(host).Single(button => button.Name == "ApproveRequest" && ((ApprovalRow)button.DataContext).Pending);
                    var deny = Descendants<Button>(host).Single(button => button.Name == "DenyRequest" && ((ApprovalRow)button.DataContext).Pending);
                    Assert.Same(approve.Style, deny.Style);
                    Assert.Same(Application.Current.FindResource("Primer.Secondary"), approve.Style.BasedOn);
                    foreach (var button in new[] { deny, approve })
                    {
                        Assert.False(button.IsDefault); Assert.False(button.IsCancel);
                        Assert.Equal("", Ux.GetVariant(button));
                        Assert.Empty(Descendants<AccessText>(button));
                        Assert.DoesNotContain('_', Assert.IsType<string>(button.Content));
                    }
                    double original = approve.ActualHeight;
                    approve.Content = "A_long_label -> != " + string.Join(" ", Enumerable.Repeat("Maple Mono approval", 8));
                    Layout();
                    Assert.Equal(deny.ActualWidth, approve.ActualWidth, precision: 4);
                    Assert.Equal(deny.ActualHeight, approve.ActualHeight, precision: 4);
                    Assert.True(approve.ActualHeight > original);
                    var label = Assert.Single(Descendants<TextBlock>(approve));
                    Assert.Equal(Assert.IsType<string>(approve.Content), label.Text);
                    Assert.Equal(TextWrapping.Wrap, label.TextWrapping);
                    Assert.Empty(Descendants<AccessText>(approve));
                }
                finally { ThemeManager.Current.Theme = previous; }
            });
    }

    [Theory]
    [InlineData(1200, 780)]
    [InlineData(1920, 1080)]
    public void Catalog_client_names_wrap_only_between_words_without_truncation_and_sider_title_fits(int width, int height)
    {
        foreach (var language in new[] { AppLanguage.Chinese, AppLanguage.English })
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        foreach (bool detected in new[] { false, true })
            wpf.RunWithLanguage(language, () =>
            {
                var previous = ThemeManager.Current.Theme;
                ThemeManager.Current.Theme = theme;
                // Take the names from the real catalog, but keep all configuration paths synthetic.
                var clients = ClientProfiles.All().Select((client, i) => new ClientProfile(client.Id, client.Name,
                    @"C:\fixture\" + i + ".json", "layout fixture") { Detected = detected }).ToList();
                var preview = new ConfigurationPreview("21", @"C:\fixture\Portal V21", "127.0.0.1", false,
                    false, true, "", "", clients);
                var window = new MainWindow(ProjectPageTestSupport.Model(), false, FeaturePageFixtures.Approvals(),
                    new FeaturePageFixtures.Diagnostics(), preview);
                try
                {
                    window.ShowConfiguration(false);
                    var host = WorkbenchRenderTests.CreateHost(window, width, height);
                    var list = (ListBox)window.Configuration!.FindName("ClientChoices");
                    foreach (bool selected in new[] { false, true })
                    {
                        if (selected) list.SelectAll(); else list.UnselectAll();
                        Layout(host, width, height);
                        string context = $"{width}x{height} {language} {theme} detected={detected} selected={selected}";
                        var title = (TextBlock)window.FindName("BrandTitle");
                        Assert.Equal(Loc.Current["Shell.Brand"], title.Text);
                        AssertSingleLine(title, context);
                        var version = (Border)window.FindName("BrandVersion");
                        Assert.Equal(Visibility.Visible, version.Visibility);
                        AssertSingleLine((TextBlock)window.FindName("RailVersion"), context);
                        Assert.True(version.TranslatePoint(new Point(0, version.ActualHeight), host).Y <= 56, context);
                        for (int i = 0; i < clients.Count; i++)
                        {
                            var row = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(i));
                            var name = Descendants<TextBlock>(row).Single(text => text.Name == "ClientName");
                            Assert.Equal(clients[i].DisplayName, name.Text);
                            Assert.Equal(clients[i].DisplayName, name.ToolTip);
                            AssertClientName(name, context);
                            Assert.Equal(selected, row.IsSelected);
                        }
                    }
                }
                finally { window.Close(); ThemeManager.Current.Theme = previous; }
            });
    }

    [Theory]
    [InlineData(1024, 640)]
    [InlineData(1044, 780)]
    [InlineData(1200, 780)]
    [InlineData(1366, 860)]
    [InlineData(1920, 1080)]
    public void Font_change_labels_keep_complete_words_across_every_page(int width, int height)
    {
        string? render = Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR");
        string? output = string.IsNullOrWhiteSpace(render) ? null : Path.Combine(Path.GetFullPath(render), "font-layout");
        if (output != null) Directory.CreateDirectory(output);
        foreach (var language in new[] { AppLanguage.Chinese, AppLanguage.English })
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            wpf.RunWithLanguage(language, () =>
            {
                var previous = ThemeManager.Current.Theme;
                ThemeManager.Current.Theme = theme;
                var window = new MainWindow(ProjectPageTestSupport.Model(results: true, release: "21"), false,
                    FeaturePageFixtures.Approvals(), new FeaturePageFixtures.Diagnostics());
                try
                {
                    window.ConfigureFeaturePages(new FeaturePageFixtures.Journal(), new FeaturePageFixtures.Audit(),
                        new FeaturePageFixtures.Environment(), new FeaturePageFixtures.Notification(), () => FeaturePageFixtures.Now);
                    window.ShowConfiguration(false);
                    var host = WorkbenchRenderTests.CreateHost(window, width, height);
                    string suffix = $"{width}x{height}-{language}-{theme}";
                    foreach (string page in new[] { "Mcp", "Engineering", "VersionControl", "Calls", "Audit", "Environment", "Log" })
                    {
                        window.Navigate(page); Layout(host, width, height);
                        string context = page + " " + suffix;
                        AssertSingleLine((TextBlock)window.FindName("BrandTitle"), context);
                        AssertWholeWords(Descendants<TextBlock>((DependencyObject)window.FindName("RailEnvironment"))
                            .Single(text => text.Text == Loc.Current["Shell.Environment"]), context);
                        if (page == "Mcp")
                        {
                            AssertWholeWords((TextBlock)window.Configuration!.FindName("PageSubtitle"), context);
                            var steps = (DependencyObject)window.Configuration!.FindName("SetupSteps");
                            foreach (string key in new[] { "Config.StepInstallationHint", "Config.StepServiceHint", "Config.StepClientsHint" })
                                AssertWholeWords(Descendants<TextBlock>(steps).Single(text => text.Text == Loc.Current[key]), context);
                        }
                        if (page == "Engineering")
                        {
                            var blocks = (DependencyObject)window.FindName("BlocksContent");
                            foreach (var header in Descendants<TextBlock>(blocks).Where(text =>
                                ReferenceEquals(text.Style, Application.Current.FindResource("Primer.TableHeader"))))
                                AssertWholeWords(header, context);
                            foreach (var text in Descendants<TextBlock>(blocks).Where(text => text.DataContext is BlockRow row
                                && (text.Text == row.Name || Grid.GetColumn(text) == 10))) AssertWholeWords(text, context);
                            AssertWholeWords((TextBlock)ProjectPageTestSupport.Find(window, "BlocksSummary"), context);
                        }
                        if (page == "VersionControl")
                            AssertSingleLine((TextBlock)ProjectPageTestSupport.Find(window, "MappedMeta"), context);
                        if (page == "Audit")
                        {
                            var audit = (DependencyObject)window.FindName("AuditContent");
                            AssertWholeWords(Descendants<TextBlock>(audit).Single(text => text.Text == Loc.Current["Audit.FileSize"]), context);
                            foreach (var text in Descendants<TextBlock>(audit).Where(text => text.DataContext is AuditRow row
                                && (text.Text == row.Type || text.Text == row.Actor))) AssertSingleLine(text, context);
                        }
                        if (page == "Calls")
                        {
                            var calls = (UserControl)window.FindName("CallsContent");
                            var statistics = (DependencyObject)calls.FindName("CallStatistics");
                            foreach (string key in new[] { "Calls.StatTotal", "Calls.StatPending", "Calls.StatFailed", "Calls.StatAverage" })
                                AssertWholeWords(Descendants<TextBlock>(statistics).Single(text => text.Text == Loc.Current[key]), context);
                            foreach (var header in Descendants<TextBlock>(calls).Where(text =>
                                ReferenceEquals(text.Style, Application.Current.FindResource("Primer.TableHeader"))))
                                AssertWholeWords(header, context);
                            foreach (var text in Descendants<TextBlock>(calls).Where(text => text.DataContext is CallRow row
                                && text.Text == row.Actor)) AssertSingleLine(text, context);
                        }
                        if (output != null) WorkbenchRenderTests.SaveRender(host,
                            Path.Combine(output, page + "-" + suffix + ".png"), width, height);
                    }
                    window.Navigate("Calls");
                    window.Features.OpenCall(window.Features.Calls.Single(call => call.Tool == "ExportBlock"));
                    Layout(host, width, height);
                    var detail = (DependencyObject)window.FindName("CallDetailContent");
                    AssertSingleLine(Descendants<TextBlock>(detail).Single(text => text.Text == Loc.Current["Calls.Host"]), suffix);
                    if (output != null) WorkbenchRenderTests.SaveRender(host,
                        Path.Combine(output, "CallDetail-" + suffix + ".png"), width, height);
                }
                finally { window.Close(); ThemeManager.Current.Theme = previous; }
            });
    }

    private static void Layout(FrameworkElement host, int width, int height)
    {
        WpfContext.Drain();
        host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout();
        WpfContext.Drain(); host.UpdateLayout();
    }

    private static TextBlock Probe(TextBlock text)
    {
        var probe = new TextBlock
        {
            Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize, FontWeight = text.FontWeight,
            FontStyle = text.FontStyle, FontStretch = text.FontStretch, Padding = text.Padding, Language = text.Language,
            LineHeight = text.LineHeight, LineStackingStrategy = text.LineStackingStrategy,
        };
        Typography.SetStandardLigatures(probe, Typography.GetStandardLigatures(text));
        Typography.SetContextualLigatures(probe, Typography.GetContextualLigatures(text));
        Typography.SetContextualAlternates(probe, Typography.GetContextualAlternates(text));
        return probe;
    }

    private static void AssertSingleLine(TextBlock text, string context)
    {
        Assert.Equal(Visibility.Visible, text.Visibility);
        Assert.False(string.IsNullOrWhiteSpace(text.Text));
        Assert.Equal(TextWrapping.NoWrap, text.TextWrapping);
        var probe = Probe(text);
        TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(text));
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Assert.True(probe.DesiredSize.Width <= text.ActualWidth,
            $"{context}: '{text.Text}' needs {probe.DesiredSize.Width} px, has {text.ActualWidth} px");
        Assert.True(text.DesiredSize.Width - text.Margin.Left - text.Margin.Right <= text.ActualWidth, context + ": " + text.Text);
        Assert.True(probe.DesiredSize.Height <= text.ActualHeight + 0.5, context + ": " + text.Text);
        int lines = 0;
        for (TextPointer? line = text.ContentStart.GetLineStartPosition(0);
            line != null && line.CompareTo(text.ContentEnd) < 0; line = line.GetLineStartPosition(1)) lines++;
        Assert.True(lines == 1, $"{context}: '{text.Text}' has {lines} lines");
    }

    private static void AssertWholeWords(TextBlock text, string context)
    {
        Assert.True(text.ActualWidth > 0, context + ": " + text.Text);
        var probe = Probe(text);
        TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(text));
        // A wrapped label must have enough width for every whole word and enough height for all its lines.
        foreach (string word in text.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Any(character => character > 127)) continue;
            probe.Text = word; probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(probe.DesiredSize.Width <= text.ActualWidth,
                $"{context}: '{word}' in '{text.Text}' needs {probe.DesiredSize.Width} px, has {text.ActualWidth} px");
        }
        probe.Text = text.Text; probe.TextWrapping = text.TextWrapping;
        probe.Measure(new Size(text.ActualWidth, double.PositiveInfinity));
        Assert.True(probe.DesiredSize.Height <= text.ActualHeight + 0.5,
            $"{context}: '{text.Text}' needs {probe.DesiredSize.Height} px height, has {text.ActualHeight} px");
        if (text.TextWrapping == TextWrapping.NoWrap) AssertSingleLine(text, context);
        else Assert.Equal(TextTrimming.None, text.TextTrimming);
    }

    private static void AssertClientName(TextBlock text, string context)
    {
        Assert.Equal(Visibility.Visible, text.Visibility);
        Assert.False(string.IsNullOrWhiteSpace(text.Text));
        Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
        Assert.Equal(TextTrimming.CharacterEllipsis, text.TextTrimming);
        Assert.Equal(18, text.LineHeight);
        Assert.Equal(36, text.MaxHeight);
        Assert.True(text.ActualWidth > 0, context + ": " + text.Text);
        var probe = Probe(text);
        TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(text));
        foreach (string word in text.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            probe.Text = word; probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(probe.DesiredSize.Width <= text.ActualWidth,
                $"{context}: '{word}' in '{text.Text}' needs {probe.DesiredSize.Width} px, has {text.ActualWidth} px");
        }
        // An untrimmed, unconstrained-height copy must fit in the actual name's two visible line boxes.
        probe.Text = text.Text; probe.TextWrapping = TextWrapping.Wrap;
        probe.Measure(new Size(text.ActualWidth, double.PositiveInfinity));
        Assert.True(probe.DesiredSize.Height <= text.ActualHeight + 0.5,
            $"{context}: '{text.Text}' needs {probe.DesiredSize.Height} px height, has {text.ActualHeight} px");
        Assert.InRange(text.ActualHeight, text.LineHeight, text.MaxHeight);
        int lines = 0;
        for (TextPointer? line = text.ContentStart.GetLineStartPosition(0);
            line != null && line.CompareTo(text.ContentEnd) < 0; line = line.GetLineStartPosition(1))
        {
            // TextRange counts text characters, unlike TextPointer offsets which also count element edges.
            string prefix = new TextRange(text.ContentStart, line).Text;
            Assert.True(text.Text.StartsWith(prefix, StringComparison.Ordinal), context + ": " + text.Text);
            int start = prefix.Length;
            while (start < text.Text.Length && char.IsWhiteSpace(text.Text[start])) start++;
            Assert.True(start < text.Text.Length && (start == 0 || char.IsWhiteSpace(text.Text[start - 1])),
                $"{context}: '{text.Text}' line {lines + 1} starts inside a word at character {start}");
            lines++;
        }
        Assert.InRange(lines, 1, 2);
        Assert.Equal(lines, (int)Math.Round(text.ActualHeight / text.LineHeight));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
