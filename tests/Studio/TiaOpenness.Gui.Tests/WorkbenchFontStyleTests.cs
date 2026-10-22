using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
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
