using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.Themes;
using TiaOpenness.Gui.ViewModels;
using Xunit;

namespace TiaOpenness.Gui.Tests;

public sealed partial class UnifiedDesktopTests
{
    internal static void ClickControl(System.Windows.Controls.Primitives.ButtonBase button)
    {
        button.GetType().GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);
        FlushMenu();
    }

    private static void FlushMenu() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    internal static void AssertPage(MainWindow window, bool configuration)
    {
        FlushMenu();
        Assert.Equal(configuration, ((MainViewModel)window.DataContext).IsConfigurationPage);
        Assert.Equal(configuration ? Visibility.Visible : Visibility.Collapsed, ((FrameworkElement)window.FindName("ConfigurationHost")).Visibility);
        Assert.Equal(configuration ? Visibility.Collapsed : Visibility.Visible, ((FrameworkElement)window.FindName("EngineeringPage")).Visibility);
    }

    [Theory]
    [InlineData(AppLanguage.Chinese, AppTheme.Light)]
    [InlineData(AppLanguage.Chinese, AppTheme.Dark)]
    [InlineData(AppLanguage.English, AppTheme.Light)]
    [InlineData(AppLanguage.English, AppTheme.Dark)]
    public void Rail_shortcuts_and_settings_keep_existing_actions_reachable(AppLanguage language, AppTheme theme)
    {
        wpf.RunWithLanguage(language, () =>
        {
            using var trace = new BindingPathTests.BindingTrace();
            var previous = ThemeManager.Current.Theme;
            ThemeManager.Current.Theme = theme;
            var window = new MainWindow(new MainViewModel(new FakeStudioClient(), new FakeDialogService()), false);
            try
            {
                window.ShowConfiguration(false);
                Assert.Null(window.FindName("MenuBar"));
                Assert.Null(window.FindName("SidebarCard"));
                Assert.Null(window.FindName("RailCard"));
                Assert.Equal(38, ((RadioButton)window.FindName("RailOverview")).Height);
                var pages = new[] { "Mcp", "Engineering", "Blocks", "VersionControl", "Calls", "Audit", "Environment", "Log" };
                for (int i = 0; i < pages.Length; i++)
                {
                    var shortcut = window.InputBindings.OfType<KeyBinding>().Single(x => x.Key == Key.D1 + i);
                    Assert.Equal(pages[i], shortcut.CommandParameter);
                    Assert.Same(WorkbenchCommands.Navigate, shortcut.Command);
                    var rail = (RadioButton)window.FindName("Rail" + pages[i]);
                    if (pages[i] is "Blocks" or "VersionControl") { Assert.False(rail.IsEnabled); continue; }
                    WorkbenchCommands.Navigate.Execute(pages[i], window);
                    Assert.True(rail.IsChecked);
                    Assert.True(WorkbenchCommands.Open.CanExecute(null, window));
                }
                WorkbenchCommands.Open.Execute(null, window);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)ProjectPageTestSupport.Find(window, "OptionsOverlay")).Visibility);
                window.OpenSettings();
                Assert.Equal(400, ((Border)window.FindName("DrawerPanel")).Width);
                var settings = (TiaOpenness.Gui.Views.SettingsView)window.FindName("SettingsContent");
                foreach (string name in new[] { "CheckUpdate", "RunUpdate", "OpenReleases", "OpenProjectPage", "OpenConfig", "OpenLog" })
                    Assert.IsType<Button>(settings.FindName(name));
                ClickControl((RadioButton)settings.FindName("Chinese"));
                Assert.Equal(AppLanguage.Chinese, Loc.Current.Language);
                ClickControl((RadioButton)settings.FindName("English"));
                Assert.Equal(AppLanguage.English, Loc.Current.Language);
                ClickControl((RadioButton)settings.FindName("Dark"));
                Assert.Equal(AppTheme.Dark, ThemeManager.Current.Theme);
                ClickControl((RadioButton)settings.FindName("Light"));
                Assert.Equal(AppTheme.Light, ThemeManager.Current.Theme);
                Assert.Same(window.Configuration, ((ContentControl)window.FindName("ConfigurationHost")).Content);
            }
            finally { window.Close(); ThemeManager.Current.Theme = previous; }
            Assert.True(string.IsNullOrWhiteSpace(trace.Text), trace.Text);
        });
    }

    [Fact]
    public void Approval_off_requires_confirmation_and_diagnostics_button_starts_export()
    {
        wpf.Run(() =>
        {
            var diagnostics = new FeaturePageFixtures.Diagnostics();
            var window = new MainWindow(new MainViewModel(new FakeStudioClient(), new FakeDialogService()), false, approvals: new Services.Stubs.ApprovalServiceStub(), diagnostics: diagnostics);
            try
            {
                window.OpenSettings();
                var settings = (TiaOpenness.Gui.Views.SettingsView)window.FindName("SettingsContent");
                Assert.Equal(0, window.Approvals.PendingCount);
                Assert.Equal(120, window.Approvals.TimeoutSeconds);
                ClickControl((RadioButton)settings.FindName("ApprovalOff"));
                Assert.True(window.Approvals.Enabled);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("ConfirmOverlay")).Visibility);
                ClickControl((Button)window.FindName("CancelApprovalOff"));
                Assert.True(window.Approvals.Enabled);
                ClickControl((RadioButton)settings.FindName("ApprovalOff"));
                ClickControl((Button)window.FindName("ConfirmApprovalOff"));
                Assert.False(window.Approvals.Enabled);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("ApprovalOffPill")).Visibility);
                ClickControl((RadioButton)settings.FindName("Timeout300"));
                Assert.Equal(300, window.Approvals.TimeoutSeconds);
                ClickControl((RadioButton)settings.FindName("ApprovalOn"));
                Assert.True(window.Approvals.Enabled);
                ClickControl((Button)window.FindName("ExportDiagnostics"));
                Assert.Equal(DiagnosticState.Running, diagnostics.Progress.State);
                Assert.True(window.Features.DiagnosticRunning);
            }
            finally { window.Close(); }
        });
    }

    private sealed class DialogLifetime(Window window) : IDisposable
    {
        internal Window Window { get; } = window;
        public void Dispose() => Window.Close();
    }

    [Theory]
    [InlineData(MessageBoxButton.OK, 0, MessageBoxResult.OK)]
    [InlineData(MessageBoxButton.OKCancel, 0, MessageBoxResult.OK)]
    [InlineData(MessageBoxButton.OKCancel, 1, MessageBoxResult.Cancel)]
    [InlineData(MessageBoxButton.YesNoCancel, 0, MessageBoxResult.Yes)]
    [InlineData(MessageBoxButton.YesNoCancel, 1, MessageBoxResult.No)]
    [InlineData(MessageBoxButton.YesNoCancel, 2, MessageBoxResult.Cancel)]
    public void Themed_dialog_buttons_preserve_message_box_results(MessageBoxButton buttons, int index, MessageBoxResult expected)
    {
        wpf.Run(() =>
        {
            using var lifetime = new DialogLifetime(new WorkbenchMessageBox("fixture", "fixture", buttons, MessageBoxImage.Warning));
            var dialog = (WorkbenchMessageBox)lifetime.Window;
            dialog.Show(); FlushMenu();
            var choice = DesktopCapture.Descendants<Button>((FrameworkElement)dialog.Content).ElementAt(index);
            typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(choice, null);
            Assert.Equal(expected, dialog.Result);
            Assert.False(dialog.IsVisible);
        });
    }
}
