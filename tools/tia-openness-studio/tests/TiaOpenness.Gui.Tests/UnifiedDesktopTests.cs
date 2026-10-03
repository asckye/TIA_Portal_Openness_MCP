using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TiaMcpConfigurator;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Themes;
using TiaOpenness.Gui.ViewModels;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class UnifiedDesktopTests(WpfContext wpf)
{
    [Theory]
    [InlineData(AppLanguage.Chinese, AppTheme.Light)]
    [InlineData(AppLanguage.Chinese, AppTheme.Dark)]
    [InlineData(AppLanguage.English, AppTheme.Light)]
    [InlineData(AppLanguage.English, AppTheme.Dark)]
    public void Configuration_is_an_embedded_page_and_survives_navigation(AppLanguage language, AppTheme theme)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var previousTheme = ThemeManager.Current.Theme;
            ThemeManager.Current.Theme = theme;
            var window = new MainWindow();
            var content = (FrameworkElement)window.Content;
            window.Content = null;
            var host = new Border { Child = content, DataContext = window.DataContext };
            try
            {
                var count = Application.Current.Windows.Count;
                window.ShowConfiguration(false);
                var page = window.Configuration!;
                host.Measure(new Size(1200, 780)); host.Arrange(new Rect(0, 0, 1200, 780)); host.UpdateLayout();
                Assert.Equal(count, Application.Current.Windows.Count);
                Assert.Same(window, page.Window);
                Assert.IsType<ConfigurationView>(page);
                Assert.Same(page, ((ContentControl)window.FindName("ConfigurationHost")).Content);
                var address = (TextBox)page.FindName("ServerAddress");
                address.Text = "192.0.2.77";
                window.ShowEngineering();
                window.ShowConfiguration(false);
                Assert.Same(page, window.Configuration);
                Assert.Equal("192.0.2.77", address.Text);
                Assert.Null(page.FindName("Close"));

                var model = (MainViewModel)window.DataContext;
                model.SelectedReleaseKey = "15.1";
                Assert.Equal("15.1", page.SelectedReleaseKey);
                model.ServiceOwnsRelease = true;
                model.SelectedReleaseKey = "21";
                Assert.Equal("15.1", model.SelectedReleaseKey);
                Assert.False(((ComboBox)window.FindName("ReleasePicker")).IsEnabled);
                model.ServiceOwnsRelease = false;
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                host.Measure(new Size(1200, 780)); host.Arrange(new Rect(0, 0, 1200, 780)); host.UpdateLayout();
                Assert.True(page.ActualWidth > 1000);
                var bitmap = new RenderTargetBitmap(1200, 780, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host);
                var output = Environment.GetEnvironmentVariable("TIA_GLASS_SCREENSHOTS") ??
                    Path.Combine(MainWindow.FindBundleRoot(AppContext.BaseDirectory), "bin-build", "unified-desktop");
                Directory.CreateDirectory(output);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, $"configuration-{language}-{theme}.png"));
                encoder.Save(stream);
            }
            finally { window.Close(); ThemeManager.Current.Theme = previousTheme; }
        });
    }

    [Fact]
    public void Language_and_theme_changes_reach_the_existing_configuration_page()
    {
        wpf.RunWithLanguage(AppLanguage.English, () =>
        {
            var previousTheme = ThemeManager.Current.Theme;
            var window = new MainWindow();
            try
            {
                window.ShowConfiguration(false);
                Loc.Current.Language = AppLanguage.Chinese;
                ThemeManager.Current.Theme = AppTheme.Dark;
                var page = window.Configuration!;
                Assert.Equal("zh-cn", page.Language.IetfLanguageTag);
                var expected = (SolidColorBrush)Application.Current.FindResource("Ui.WindowBackground");
                var actual = (SolidColorBrush)page.Resources["Ui.WindowBackground"];
                Assert.Equal(expected.Color, actual.Color);
                window.Close();
                Loc.Current.Language = AppLanguage.English;
                Assert.Equal("zh-cn", page.Language.IetfLanguageTag);
            }
            finally { window.Close(); ThemeManager.Current.Theme = previousTheme; }
        });
    }

    [Fact]
    public void Bundle_lookup_does_not_depend_on_the_current_working_directory()
    {
        var root = MainWindow.FindBundleRoot(AppContext.BaseDirectory);
        Assert.Equal(root, MainWindow.FindBundleRoot(Path.Combine(root, "runtime", "studio")));
        Assert.Throws<DirectoryNotFoundException>(() => MainWindow.FindBundleRoot(Path.GetPathRoot(root)!));
    }

    [Fact]
    public void Launch_arguments_select_one_exact_release_for_both_pages()
    {
        Assert.Equal("15.1", MainWindow.InitialReleaseKey(new[] { "--openness-version", "15.1" }));
        Assert.Equal("20", MainWindow.InitialReleaseKey(new[] { "--openness-version", "20.0.0.0" }));
        Assert.Throws<ArgumentException>(() => MainWindow.InitialReleaseKey(new[] { "--openness-version", "22" }));
        Assert.Throws<ArgumentException>(() => MainWindow.InitialReleaseKey(new[] { "--openness-version" }));
    }

    [Fact]
    public void Busy_operations_keep_the_window_and_configuration_alive_until_finished()
    {
        wpf.Run(() =>
        {
            var window = new MainWindow();
            var model = (MainViewModel)window.DataContext;
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            window.ShowConfiguration(false);
            var page = window.Configuration!;
            var setBusy = page.GetType().GetMethod("SetBusy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            try
            {
                setBusy.Invoke(page, new object[] { true });
                window.ShowEngineering();
                Assert.False(model.CanSelectRelease);
                window.Close();
                Assert.False(closed);
                setBusy.Invoke(page, new object[] { false });
                Assert.True(model.CanSelectRelease);

                typeof(MainViewModel).GetProperty(nameof(MainViewModel.Busy))!.SetValue(model, true);
                window.Close();
                Assert.False(closed);
                typeof(MainViewModel).GetProperty(nameof(MainViewModel.Busy))!.SetValue(model, false);
                page.GetType().GetMethod("Append", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(page, new object[] { "operation completed after cancelled close" });
                Assert.Contains("operation completed after cancelled close", ((TextBox)page.FindName("Log")).Text);
                window.Close();
                Assert.True(closed);
            }
            finally
            {
                if (!closed)
                {
                    setBusy.Invoke(page, new object[] { false });
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.Busy))!.SetValue(model, false);
                    window.Close();
                }
            }
        });
    }
}
