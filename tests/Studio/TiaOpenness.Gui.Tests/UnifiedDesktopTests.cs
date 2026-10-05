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
public sealed partial class UnifiedDesktopTests(WpfContext wpf)
{
    [Theory]
    [InlineData(AppLanguage.Chinese, AppTheme.Light, false)]
    [InlineData(AppLanguage.Chinese, AppTheme.Dark, false)]
    [InlineData(AppLanguage.English, AppTheme.Light, false)]
    [InlineData(AppLanguage.English, AppTheme.Dark, false)]
    [InlineData(AppLanguage.Chinese, AppTheme.Light, true)]
    [InlineData(AppLanguage.Chinese, AppTheme.Dark, true)]
    [InlineData(AppLanguage.English, AppTheme.Light, true)]
    [InlineData(AppLanguage.English, AppTheme.Dark, true)]
    public void Engineering_renders_blocks_and_vci(AppLanguage language, AppTheme theme, bool vci)
        => new GlassViewTests(wpf).RenderEngineeringFixture(theme, vci, language);

    [Theory]
    [InlineData(AppLanguage.Chinese, AppTheme.Light)]
    [InlineData(AppLanguage.Chinese, AppTheme.Dark)]
    [InlineData(AppLanguage.English, AppTheme.Light)]
    [InlineData(AppLanguage.English, AppTheme.Dark)]
    public void Engineering_renders_log(AppLanguage language, AppTheme theme)
        => new GlassViewTests(wpf).RenderEngineeringFixture(theme, false, language, true);

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
            NameScope.SetNameScope(host, NameScope.GetNameScope(window));
            try
            {
                var count = Application.Current.Windows.Count;
                window.ShowConfiguration(false);
                AssertPage(window, true);
                var page = window.Configuration!;
                host.Measure(new Size(1200, 780)); host.Arrange(new Rect(0, 0, 1200, 780)); host.UpdateLayout();
                Assert.Equal(count, Application.Current.Windows.Count);
                Assert.Same(window, page.Window);
                Assert.IsType<ConfigurationView>(page);
                Assert.Same(page, ((ContentControl)window.FindName("ConfigurationHost")).Content);
                var address = (TextBox)page.FindName("ServerAddress");
                address.Text = "192.0.2.77";
                window.ShowEngineering();
                AssertPage(window, false);
                window.ShowConfiguration(false);
                AssertPage(window, true);
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
                Assert.Same(expected, page.Background);
                window.Close();
                Loc.Current.Language = AppLanguage.English;
                Assert.Equal("zh-cn", page.Language.IetfLanguageTag);
            }
            finally { window.Close(); ThemeManager.Current.Theme = previousTheme; }
        });
    }

    [Theory]
    [InlineData(AppLanguage.English, AppTheme.Light, AppLanguage.Chinese, AppTheme.Dark)]
    [InlineData(AppLanguage.Chinese, AppTheme.Dark, AppLanguage.English, AppTheme.Light)]
    public void Hosted_configuration_uses_workbench_resources_in_both_directions(
        AppLanguage initialLanguage, AppTheme initialTheme, AppLanguage nextLanguage, AppTheme nextTheme)
    {
        wpf.RunWithLanguage(initialLanguage, () =>
        {
            var previousTheme = ThemeManager.Current.Theme;
            ThemeManager.Current.Theme = initialTheme;
            var window = new MainWindow();
            try
            {
                window.ShowConfiguration(false);
                var page = window.Configuration!;
                var choices = (ListBox)page.FindName("ClientChoices");
                choices.SelectedItems.Clear();
                var fixtures = new[]
                {
                    new ClientProfile("first", "First", @"C:\fixture\first.json", "first hint") { Detected = true, Evidence = "first evidence" },
                    new ClientProfile("second", "Second", @"C:\fixture\second.json", "second hint") { Evidence = "second evidence" },
                };
                choices.ItemsSource = fixtures;
                foreach (var fixture in fixtures) choices.SelectedItems.Add(fixture);
                var address = (TextBox)page.FindName("ServerAddress");
                address.Text = "192.0.2.77";
                var secret = (PasswordBox)page.FindName("Key");
                secret.Password = "language-switch-fixture";
                var log = (TextBox)page.FindName("Log");
                string history = log.Text;
                int entries = history.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length;
                var brush = page.Background;
                bool detected = (bool)typeof(ConfigurationView).GetField("tiaDetected",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(page)!;

                AssertAppearance(initialLanguage);
                Loc.Current.Language = nextLanguage;
                ThemeManager.Current.Theme = nextTheme;
                AssertAppearance(nextLanguage);
                Assert.NotSame(brush, page.Background);
                Loc.Current.Language = initialLanguage;
                ThemeManager.Current.Theme = initialTheme;
                AssertAppearance(initialLanguage);

                choices.SelectedItems.Clear();
                ((RadioButton)page.FindName("LocalNav")).IsChecked = true;
                Loc.Current.Language = nextLanguage;
                FlushBindings();
                Assert.Equal(Loc.Current["Config.ChooseClients"], Text("ClientInstructions"));
                Assert.Equal(Loc.Current["Config.LocalTitle"], Text("PageTitle"));
                Assert.Equal(Loc.Current["Config.NoSelection"], Text("LinkClient"));
                Assert.Equal(Loc.Current["Config.Local"], Text("LinkState"));

                void FlushBindings() => window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                string Text(string name) => ((TextBlock)page.FindName(name)).Text;
                void AssertAppearance(AppLanguage language)
                {
                    FlushBindings();
                    Assert.Equal(language == AppLanguage.Chinese ? "zh-cn" : "en-us", page.Language.IetfLanguageTag);
                    Assert.False(page.Resources.Contains("Ui.WindowBackground"));
                    Assert.Same(Application.Current.FindResource("Ui.WindowBackground"), page.Background);
                    Assert.Same(Application.Current.FindResource("Ui.Font"), page.FontFamily);
                    Assert.Equal(Loc.Current["Config.RemoteTitle"], Text("PageTitle"));
                    Assert.Equal(Loc.Current["Config.Write"], ((Button)page.FindName("SaveClient")).Content);
                    Assert.Equal(Loc.Current.T("Config.Selected", 2), Text("ClientSelection"));
                    Assert.Equal(Loc.Current.T("Config.Selected", 2), Text("LinkClient"));
                    Assert.Equal("First" + Loc.Current["Config.ListSeparator"] + "Second",
                        ((TextBlock)page.FindName("LinkClient")).ToolTip);
                    Assert.Equal(Loc.Current.T("Config.Entries", entries), Text("LogCount"));
                    Assert.Equal(Loc.Current.T("Config.LastTest", Loc.Current["Config.TestNotRun"]), Text("LastTest"));
                    Assert.Equal(detected ? "● " + Loc.Current["Config.Detected"] : Loc.Current["Config.NotDetected"], Text("DetectionSource"));
                    Assert.Equal(Loc.Current.T("Config.ClientInstructionsDetected", "First", "first hint", "first evidence") + "\n" +
                        Loc.Current.T("Config.ClientInstructionsNotDetected", "Second", "second hint", "second evidence"), Text("ClientInstructions"));
                    Assert.Equal(Text("ClientInstructions"), ((TextBlock)page.FindName("ClientSelection")).ToolTip);
                    Assert.Equal("192.0.2.77", address.Text);
                    Assert.Equal("language-switch-fixture", secret.Password);
                    Assert.Equal(history, log.Text);
                }
            }
            finally { window.Close(); ThemeManager.Current.Theme = previousTheme; }
        });
    }

    [Theory]
    [InlineData(AppLanguage.Chinese, "客户端检测：", "就绪。服务与客户端配置在同一页完成，两端使用同一密钥。", "已配置 3 个客户端")]
    [InlineData(AppLanguage.English, "Client detection: ", "Ready. Configure the service and clients on one page, using the same secret on both sides.", "3 clients configured")]
    public void Configuration_localizes_new_messages_and_refreshes_current_status_without_rewriting_logs(
        AppLanguage language, string detection, string ready, string status)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var window = new MainWindow();
            try
            {
                window.ShowConfiguration(false);
                var page = window.Configuration!;
                var log = (TextBox)page.FindName("Log");
                Assert.Contains(detection, log.Text);
                Assert.Contains(ready, log.Text);
                string history = log.Text;
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                typeof(ConfigurationView).GetMethod("SetStatus", flags)!.Invoke(page, new object[] { "Config.ClientsConfigured", new object[] { 3 } });
                typeof(ConfigurationView).GetField("lastTestFailed", flags)!.SetValue(page, true);
                typeof(ConfigurationView).GetField("tiaDetected", flags)!.SetValue(page, true);
                Assert.Equal(status, ((TextBlock)page.FindName("Status")).Text);
                typeof(ConfigurationView).GetMethod("SetLocalizedText", flags)!.Invoke(page, new object[]
                {
                    "UpdateStateItem", MenuItem.HeaderProperty, "Config.UpdateAvailable",
                    new object[] { "9.0", LocalizedText.Key("Config.UpdateSize", "15.4 MB") },
                });

                Loc.Current.Language = language == AppLanguage.Chinese ? AppLanguage.English : AppLanguage.Chinese;
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                Assert.Equal(Loc.Current.T("Config.ClientsConfigured", 3), ((TextBlock)page.FindName("Status")).Text);
                Assert.Equal(Loc.Current.T("Config.LastTest", Loc.Current["Config.TestFailed"]), ((TextBlock)page.FindName("LastTest")).Text);
                Assert.Equal("● " + Loc.Current["Config.Detected"], ((TextBlock)page.FindName("DetectionSource")).Text);
                Assert.Equal(Loc.Current.T("Config.UpdateAvailable", "9.0", Loc.Current.T("Config.UpdateSize", "15.4 MB")),
                    ((MenuItem)window.FindName("UpdateStateItem")).Header);
                var installed = (MenuItem)window.FindName("UpdateInstalledItem");
                string root = MainWindow.FindBundleRoot(AppContext.BaseDirectory);
                string? version = UpdateCheck.Installed(root);
                Assert.Equal(version == null ? Loc.Current["Config.EngineOutsideBundle"] :
                    Loc.Current.T("Config.InstalledEngine", version, UpdateCheck.InstalledPackage(root)), installed.Header);
                Assert.Equal(history, log.Text);
                Loc.Current.Language = language;
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                Assert.Equal(status, ((TextBlock)page.FindName("Status")).Text);
                Assert.Equal(history, log.Text);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(AppLanguage.English, ", ", "Client: failure", "(15.4 MB)", "; ")]
    [InlineData(AppLanguage.Chinese, "、", "Client：failure", "（15.4 MB）", "；")]
    public void Configuration_punctuation_comes_from_the_active_catalogue(
        AppLanguage language, string list, string error, string size, string reasons)
    {
        wpf.RunWithLanguage(language, () =>
        {
            Assert.Equal(list, Loc.Current["Config.ListSeparator"]);
            Assert.Equal(error, Loc.Current.T("Config.ClientSaveError", "Client", "failure"));
            Assert.Equal(size, Loc.Current.T("Config.UpdateSize", "15.4 MB"));
            Assert.Equal(reasons, Loc.Current["Config.ReasonSeparator"]);
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

                typeof(TiaOpenness.Gui.Services.WorkbenchActivity).GetProperty(nameof(MainViewModel.Busy))!.SetValue(model.Activity, true);
                window.Close();
                Assert.False(closed);
                typeof(TiaOpenness.Gui.Services.WorkbenchActivity).GetProperty(nameof(MainViewModel.Busy))!.SetValue(model.Activity, false);
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
                    typeof(TiaOpenness.Gui.Services.WorkbenchActivity).GetProperty(nameof(MainViewModel.Busy))!.SetValue(model.Activity, false);
                    window.Close();
                }
            }
        });
    }
}
