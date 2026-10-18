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
using Stubs = TiaOpenness.Gui.Services.Stubs;

namespace TiaOpenness.Gui.Tests;

public sealed partial class UnifiedDesktopTests
{
    internal static void ClickControl(System.Windows.Controls.Primitives.ButtonBase button)
    {
        button.GetType().GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);
        FlushMenu();
    }

    private static void FlushMenu() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void ClickApprovalControl(ButtonBase button, Stubs.IApprovalService approvals, string property)
    {
        var changed = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == property) changed.TrySetResult(true); }
        approvals.PropertyChanged += OnChanged;
        try
        {
            ClickControl(button);
            WpfContext.Complete(changed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { approvals.PropertyChanged -= OnChanged; }
    }

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
            var window = new MainWindow(new MainViewModel(new FakeProjectClient(), new FakeDialogService()), false);
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
            var window = new MainWindow(new MainViewModel(new FakeProjectClient(), new FakeDialogService()), false, approvals: new Services.Stubs.ApprovalServiceStub(), diagnostics: diagnostics);
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
                ClickApprovalControl((Button)window.FindName("ConfirmApprovalOff"), window.Approvals, nameof(window.Approvals.Enabled));
                Assert.False(window.Approvals.Enabled);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("ApprovalOffPill")).Visibility);
                ClickApprovalControl((RadioButton)settings.FindName("Timeout300"), window.Approvals, nameof(window.Approvals.TimeoutSeconds));
                Assert.Equal(300, window.Approvals.TimeoutSeconds);
                ClickApprovalControl((RadioButton)settings.FindName("ApprovalOn"), window.Approvals, nameof(window.Approvals.Enabled));
                Assert.True(window.Approvals.Enabled);
                ClickControl((Button)window.FindName("ExportDiagnostics"));
                Assert.Equal(DiagnosticState.Running, diagnostics.Progress.State);
                Assert.True(window.Features.DiagnosticRunning);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Settings_radio_selection_survives_a_later_unshown_window()
    {
        wpf.Run(() =>
        {
            var earlierApproval = new Stubs.ApprovalServiceStub { TimeoutSeconds = 300 };
            var earlier = new MainWindow(new MainViewModel(new FakeProjectClient(), new FakeDialogService()), false, approvals: earlierApproval);
            MainWindow? later = null;
            try
            {
                var first = (TiaOpenness.Gui.Views.SettingsView)earlier.FindName("SettingsContent");
                Assert.True(((RadioButton)first.FindName("Timeout300")).IsChecked);
                later = new MainWindow(new MainViewModel(new FakeProjectClient(), new FakeDialogService()), false, approvals: new Stubs.ApprovalServiceStub());
                var second = (TiaOpenness.Gui.Views.SettingsView)later.FindName("SettingsContent");
                Assert.True(((RadioButton)second.FindName("Timeout120")).IsChecked);
                Assert.True(((RadioButton)first.FindName("Timeout300")).IsChecked);
                ClickApprovalControl((RadioButton)second.FindName("Timeout300"), later.Approvals, nameof(later.Approvals.TimeoutSeconds));
                Assert.Equal(300, later.Approvals.TimeoutSeconds);
                Assert.True(((RadioButton)first.FindName("Timeout300")).IsChecked);
                Assert.True(((RadioButton)second.FindName("Timeout300")).IsChecked);
            }
            finally { later?.Close(); earlier.Close(); }
        });
    }

    [Fact]
    public void Dispatcher_idle_does_not_complete_an_approval_save()
    {
        wpf.Run(() =>
        {
            using var approvals = new DelayedApprovalService();
            var window = new MainWindow(new MainViewModel(new FakeProjectClient(), new FakeDialogService()), false, approvals: approvals);
            try
            {
                var settings = (TiaOpenness.Gui.Views.SettingsView)window.FindName("SettingsContent");
                ClickControl((RadioButton)settings.FindName("Timeout300"));
                Assert.True(approvals.Entered.Wait(TimeSpan.FromSeconds(10)));
                Assert.Equal(120, approvals.TimeoutSeconds);
                approvals.Release.Set();
                WpfContext.Complete(approvals.Saved.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(300, approvals.TimeoutSeconds);
            }
            finally { approvals.Release.Set(); window.Close(); }
        });
    }

    private sealed class DelayedApprovalService : Stubs.IApprovalService, IDisposable
    {
        private readonly Stubs.ApprovalServiceStub inner = new();
        internal readonly System.Threading.ManualResetEventSlim Entered = new(), Release = new();
        internal readonly System.Threading.Tasks.TaskCompletionSource<bool> Saved = new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Enabled { get => inner.Enabled; set => inner.Enabled = value; }
        public int TimeoutSeconds
        {
            get => inner.TimeoutSeconds;
            set
            {
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fixture save was not released.");
                inner.TimeoutSeconds = value; Saved.TrySetResult(true);
            }
        }
        public int PendingCount => inner.PendingCount;
        public IReadOnlyList<ApprovalRequest> Requests => inner.Requests;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add => inner.PropertyChanged += value; remove => inner.PropertyChanged -= value; }
        public event EventHandler<ApprovalRequest>? NewRequest { add => inner.NewRequest += value; remove => inner.NewRequest -= value; }
        public bool CanDecide(string requestId) => inner.CanDecide(requestId);
        public bool Approve(string requestId) => inner.Approve(requestId);
        public bool Deny(string requestId) => inner.Deny(requestId);
        public void Refresh() => inner.Refresh();
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }

    private sealed class DialogLifetime(Window window) : IDisposable
    {
        internal Window Window { get; } = window;
        public void Dispose() => Window.Close();
    }

    [Theory]
    [InlineData(MessageBoxButton.OK, 0, MessageBoxResult.OK)]
    [InlineData(MessageBoxButton.OKCancel, 0, MessageBoxResult.Cancel)]
    [InlineData(MessageBoxButton.OKCancel, 1, MessageBoxResult.OK)]
    [InlineData(MessageBoxButton.YesNoCancel, 0, MessageBoxResult.Cancel)]
    [InlineData(MessageBoxButton.YesNoCancel, 1, MessageBoxResult.No)]
    [InlineData(MessageBoxButton.YesNoCancel, 2, MessageBoxResult.Yes)]
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
