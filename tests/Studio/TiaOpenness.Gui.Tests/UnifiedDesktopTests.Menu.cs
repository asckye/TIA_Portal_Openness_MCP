using TiaMcp.Engine.Tests.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
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

    internal static void ClickApprovalControl(ButtonBase button, Stubs.IApprovalService approvals, string property)
    {
        var changed = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == property) changed.TrySetResult(true); }
        approvals.PropertyChanged += OnChanged;
        try
        {
            ClickControl(button);
            WpfContext.Complete(changed.Task.WaitAsync(TestDeadlines.Hang));
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
            var id = Guid.NewGuid().ToString("N");
            string root = Directory.CreateDirectory(Path.Combine(McpOperatorFixture.RepositoryRoot, "bin-build", "approval-chip", id)).FullName;
            string policy = Path.Combine(root, "approval.settings");
            var approvals = new ApprovalService(policy, "tia-gui-chip-" + id, new TiaOpenness.Shared.AuditLog(Path.Combine(root, "audit")));
            var window = new MainWindow(new MainViewModel(new FakeProjectClient(), new FakeDialogService()), false, approvals, toastClock: () => TimeSpan.Zero);
            try
            {
                window.ShowConfiguration(false);
                Assert.Null(window.FindName("MenuBar"));
                Assert.Null(window.FindName("SidebarCard"));
                Assert.Null(window.FindName("RailCard"));
                Assert.Equal(42, ((RadioButton)window.FindName("RailEngineering")).Height);
                var pages = new[] { "Engineering", "Agent", "Mcp", "Clients", "Environment", "Audit", "Log" };
                for (int i = 0; i < pages.Length; i++)
                {
                    var shortcut = window.InputBindings.OfType<KeyBinding>().Single(x => x.Key == Key.D1 + i);
                    Assert.Equal(pages[i], shortcut.CommandParameter);
                    Assert.Same(WorkbenchCommands.Navigate, shortcut.Command);
                    var rail = (RadioButton)window.FindName("Rail" + pages[i]);
                    Assert.Equal(ModifierKeys.Control, shortcut.Modifiers);
                    var accelerator = System.Windows.Automation.AutomationProperties.GetAcceleratorKey(rail);
                    Assert.Equal("Ctrl+" + (i + 1), accelerator);
                    Assert.Contains(accelerator, (string)rail.ToolTip);
                    Assert.Equal(i + 1, System.Windows.Automation.AutomationProperties.GetPositionInSet(rail));
                    Assert.Equal(8, System.Windows.Automation.AutomationProperties.GetSizeOfSet(rail));
                    WorkbenchCommands.Navigate.Execute(pages[i], window);
                    Assert.True(rail.IsChecked);
                    Assert.True(WorkbenchCommands.Open.CanExecute(null, window));
                }
                Assert.DoesNotContain(window.InputBindings.OfType<KeyBinding>(), binding => binding.Key == Key.D8);
                Assert.True(WorkbenchCommands.Navigate.CanExecute("VersionControl", window));
                WorkbenchCommands.Navigate.Execute("VersionControl", window);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("Toast")).Visibility);
                Assert.Equal(Loc.Current["Shell.NoProject"], ((TextBlock)window.FindName("ToastMessage")).Text);
                Assert.False(((MainViewModel)window.DataContext).IsVcTab);
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("VersionControlContent")).Visibility);
                WorkbenchCommands.Open.Execute(null, window);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)ProjectPageTestSupport.Find(window, "OptionsOverlay")).Visibility);
                window.OpenSettings();
                Assert.Equal(Visibility.Collapsed, ((Grid)window.FindName("DrawerOverlay")).Visibility);
                var settings = (TiaOpenness.Gui.Views.SettingsView)window.FindName("SettingsContent");
                Assert.Equal(Visibility.Visible, window.SettingsOverlay.Visibility);
                Assert.Equal(4, ((ListBox)settings.FindName("SectionList")).Items.Count);
                Assert.Equal(new[] { settings.AppearanceSection, settings.AiSection, settings.ServiceSection, settings.AboutSection },
                    settings.SectionList.Items.Cast<ListBoxItem>());
                Assert.Same(settings.AppearanceSection, settings.SectionList.SelectedItem);
                var sectionList = (ListBox)settings.FindName("SectionList");
                Assert.Equal(204, ((Border)sectionList.Parent).Width);
                foreach (string name in new[] { "CheckUpdate", "RunUpdate", "OpenReleases", "OpenProjectPage", "OpenConfig", "OpenLog", "OpenData" })
                    Assert.IsType<Button>(settings.FindName(name));
                ClickControl((RadioButton)settings.FindName("Chinese"));
                Assert.Equal(AppLanguage.Chinese, Loc.Current.Language);
                ClickControl((RadioButton)settings.FindName("English"));
                Assert.Equal(AppLanguage.English, Loc.Current.Language);
                ClickControl((RadioButton)settings.FindName("Dark"));
                Assert.Equal(AppTheme.Dark, ThemeManager.Current.Theme);
                var darkShell = ((SolidColorBrush)window.Root.Background).Color;
                ClickControl((RadioButton)settings.FindName("Light"));
                Assert.NotEqual(darkShell, ((SolidColorBrush)window.Root.Background).Color);
                Assert.Equal(AppTheme.Light, ThemeManager.Current.Theme);
                var previousAccent = ThemeManager.Current.Accent;
                try
                {
                    ClickControl((RadioButton)settings.FindName("AccentPetrol"));
                    Assert.Equal(AppAccent.Petrol, ThemeManager.Current.Accent);
                    window.SettingsButton.ApplyTemplate();
                    var glyph = (System.Windows.Shapes.Path)window.SettingsButton.Template.FindName("Glyph", window.SettingsButton);
                    var petrolRail = ((SolidColorBrush)glyph.Stroke).Color;
                    ClickControl((RadioButton)settings.FindName("AccentCobalt"));
                    Assert.NotEqual(petrolRail, ((SolidColorBrush)glyph.Stroke).Color);
                    Assert.Equal(AppAccent.Cobalt, ThemeManager.Current.Accent);
                }
                finally { ThemeManager.Current.Accent = previousAccent; }
                ClickControl(settings.CloseSettingsButton);
                ClickControl((Button)window.FindName("QuickThemeButton"));
                Assert.Equal(AppTheme.Dark, ThemeManager.Current.Theme);
                ClickControl((Button)window.FindName("QuickThemeButton"));
                Assert.Equal(AppTheme.Light, ThemeManager.Current.Theme);
                Assert.Same(window.Configuration, ((ContentControl)window.FindName("ConfigurationHost")).Content);
                var settingsShortcut = window.InputBindings.OfType<KeyBinding>().Single(binding => binding.Key == Key.OemComma);
                Assert.Equal(ModifierKeys.Control, settingsShortcut.Modifiers);
                Assert.Equal("Settings", settingsShortcut.CommandParameter);
                ClickControl(window.SessionStatusButton);
                var service = DesktopCapture.Descendants<SessionPopover>(window.Root).Single().SwitchVersion;
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(window.RailMcp));
                Assert.Equal(AccessibleStatus.GetText(window.RailMcp), service.ToolTip);
                Assert.Equal(AutomationProperties.GetHelpText(window.RailMcp), service.ToolTip);
                ClickControl(service); AssertPage(window, true);
                ClickControl((Button)window.FindName("SessionStatusButton")); AssertPage(window, true);
                bool approvalEnabled = window.Approvals.Enabled, controlEnabled = App.Settings.WorkbenchControlEnabled;
                var selectedSection = sectionList.SelectedItem;
                Assert.True(window.ApprovalStatusButton.IsEnabled);
                ClickApprovalControl(window.ApprovalStatusButton, window.Approvals, nameof(window.Approvals.Enabled));
                Assert.Equal(!approvalEnabled, window.Approvals.Enabled);
                Assert.Equal(!approvalEnabled, TiaOpenness.Shared.ApprovalSettings.Load(policy).Enabled);
                Assert.Equal(controlEnabled, App.Settings.WorkbenchControlEnabled);
                var settingsRail = (RadioButton)window.FindName("SettingsButton");
                Assert.False(settingsRail.IsChecked);
                Assert.Equal(8, System.Windows.Automation.AutomationProperties.GetPositionInSet(settingsRail));
                Assert.Equal(8, System.Windows.Automation.AutomationProperties.GetSizeOfSet(settingsRail));
                Assert.Equal("Ctrl+,", System.Windows.Automation.AutomationProperties.GetAcceleratorKey(settingsRail));
                Assert.Contains("Ctrl+,", (string)settingsRail.ToolTip);
                Assert.False(((FrameworkElement)settings.FindName("AiPanel")).IsVisible);
                Assert.Same(selectedSection, sectionList.SelectedItem);
                Assert.True(window.RailMcp.IsChecked);

            }
            finally
            {
                window.Close();
                ThemeManager.Current.Theme = previous;
                Directory.Delete(root, recursive: true);
            }
            Assert.True(string.IsNullOrWhiteSpace(trace.Text), trace.Text);
        });
    }

    [Fact]
    public void Visible_overlay_handles_the_window_control_shortcuts_on_preview_key_down()
    {
        wpf.Run(() =>
        {
            var window = new MainWindow(ProjectPageTestSupport.Model(), false);
            try
            {
                WpfContext.ShowWindow(window);
                var overlay = (Grid)window.FindName("OverlayHost");
                overlay.Visibility = Visibility.Visible;
                WpfContext.WithControlPressed(() =>
                {
                    Assert.True(Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                    bool Handled(Key key)
                    {
                        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(overlay), 0, key)
                            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                        overlay.RaiseEvent(preview);
                        return preview.Handled;
                    }
                    foreach (var key in new[] { Key.D1, Key.D2, Key.D3, Key.D4, Key.D5, Key.D6, Key.D7, Key.O, Key.S, Key.B, Key.OemComma, Key.K })
                        Assert.True(Handled(key), key.ToString());
                    Assert.False(Handled(Key.D8));
                    overlay.Visibility = Visibility.Collapsed;
                    Assert.False(Handled(Key.D1));
                });
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("Engineering")]
    [InlineData("Settings")]
    public void Narrow_left_pane_overlays_the_page_and_returns_keyboard_focus(string page)
    {
        wpf.Run(() =>
        {
            bool saved = App.Settings.LeftPaneOpen;
            App.Settings.LeftPaneOpen = false;
            var window = new MainWindow(ProjectPageTestSupport.Model(), false, toastClock: () => TimeSpan.Zero) { Width = 1024, Height = 640 };
            try
            {
                WpfContext.ShowWindow(window);
                if (page == "Settings")
                {
                    CheckNarrowSettingsModal(window);
                    return;
                }
                window.Navigate(page); WpfContext.Drain();
                var toggle = (Button)window.FindName("LeftPaneToggle");
                var tree = (ContentControl)window.FindName("ProjectTree");
                FrameworkElement pane = tree;
                var detail = window.ProjectOverview;
                double width = detail.ActualWidth;
                Assert.True(toggle.IsEnabled);
                Assert.Equal(Visibility.Collapsed, pane.Visibility);
                var search = (Button)window.FindName("CommandPaletteButton");
                Assert.True(search.Focus());
                Assert.True(search.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.Same(toggle, Keyboard.FocusedElement);
                WpfContext.PressSpace(toggle);
                Assert.Equal(Visibility.Visible, pane.Visibility);
                Assert.True(pane.IsKeyboardFocusWithin);
                Assert.Equal(214, pane.ActualWidth);
                Assert.Equal(width - 214, detail.ActualWidth);
                Assert.Equal(-214, pane.TranslatePoint(new Point(), detail).X);
                Assert.True(Panel.GetZIndex(pane) > Panel.GetZIndex(detail));
                Assert.Equal(Visibility.Collapsed, ((Grid)window.FindName("OverlayHost")).Visibility);
                if (Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR") is { Length: > 0 } output)
                {
                    System.IO.Directory.CreateDirectory(output);
                    WorkbenchRenderTests.SaveRender((FrameworkElement)window.Content, System.IO.Path.Combine(output, $"flyout-{page}-1024x640.png"), 1024, 640);
                }
                Assert.Same(tree, Keyboard.FocusedElement);
                window.ShowToast(ViewModels.ToastKind.Info, LocalizedText.Literal("fixture"), "flyout");
                var escape = WorkbenchToastTests.PressEscape((UIElement)Keyboard.FocusedElement);
                Assert.Equal(Visibility.Visible, window.Toast.Visibility);
                Assert.True(escape.Handled);
                Assert.Equal(Visibility.Collapsed, pane.Visibility);
                Assert.Same(toggle, Keyboard.FocusedElement);
                tree.Content = new Button { Content = "PLC_1" };
                WpfContext.PressSpace(toggle);
                Assert.True(pane.IsKeyboardFocusWithin);
                Assert.Same(tree.Content, Keyboard.FocusedElement);
                toggle.Focus(); WpfContext.PressSpace(toggle);
                Assert.Equal(Visibility.Collapsed, pane.Visibility);
                Assert.Same(toggle, Keyboard.FocusedElement);
                Assert.False(App.Settings.LeftPaneOpen);
            }
            finally { window.Close(); App.Settings.LeftPaneOpen = saved; }
        });
    }

    private static void CheckNarrowSettingsModal(MainWindow window)
    {
        window.Navigate("Engineering");
        Assert.True(window.LeftPaneToggle.IsEnabled);
        var trigger = window.CurrentPageTitle;
        Assert.True(trigger.Focus());
        string breadcrumb = trigger.Text;
        var settings = window.SettingsContent;

        void Shortcut()
        {
            WpfContext.WithControlPressed(() =>
            {
                var target = (UIElement)Keyboard.FocusedElement;
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, Key.OemComma)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                target.RaiseEvent(key);
                Assert.True(key.Handled);
            });
            WpfContext.Drain();
        }

        void Open()
        {
            Shortcut();
            Assert.True(window.SettingsOpen);
            Assert.Equal("Engineering", window.CurrentPage);
            Assert.Equal(breadcrumb, trigger.Text);
            Assert.True(window.SettingsButton.IsChecked);
            Assert.True(settings.SectionList.IsKeyboardFocusWithin);
            Assert.Same(settings.AppearanceSection, Keyboard.FocusedElement);
            var nav = (Border)settings.SectionList.Parent;
            Assert.Equal(Visibility.Visible, nav.Visibility);
            Assert.Equal(204, nav.ActualWidth);
            Assert.Equal(((Grid)settings.Content).ActualWidth - 204, settings.DetailScroll.ActualWidth);
            Assert.Equal(-204, nav.TranslatePoint(new Point(), settings.DetailScroll).X);
            Assert.Same(window.SettingsScrim, window.Root.InputHitTest(new Point(4, 4)));
            Assert.Equal(Visibility.Collapsed, window.OverlayHost.Visibility);
            WorkbenchShellDesignTests.AssertSettingsBounds(window, window.Root.ActualWidth * 0.94, window.Root.ActualHeight * 0.88);
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(window.SettingsOverlay));
            Assert.False(window.ShellFrame.IsEnabled);
            Assert.False(window.LeftPaneToggle.IsEnabled);
            Assert.True(AutomationProperties.GetIsDialog(window.SettingsOverlay));
            Assert.False(App.Settings.LeftPaneOpen);
        }

        void Closed()
        {
            Assert.Equal(Visibility.Collapsed, window.SettingsOverlay.Visibility);
            Assert.True(window.ShellFrame.IsEnabled);
            Assert.Same(trigger, Keyboard.FocusedElement);
            Assert.True(window.RailEngineering.IsChecked);
            Assert.True(window.LeftPaneToggle.IsEnabled);
            Assert.Equal(breadcrumb, trigger.Text);
            Assert.False(App.Settings.LeftPaneOpen);
        }

        Open();
        if (Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            WorkbenchRenderTests.SaveRender((FrameworkElement)window.Content, Path.Combine(output, "settings-modal-1024x640.png"), 1024, 640);
        }
        foreach (var direction in new[] { FocusNavigationDirection.Previous, FocusNavigationDirection.Next })
            for (int step = 0; step < 15; step++)
            {
                Assert.True(((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(direction)));
                Assert.True(window.SettingsOverlay.IsKeyboardFocusWithin);
            }
        window.ShowToast(ViewModels.ToastKind.Info, LocalizedText.Literal("fixture"), "modal");
        Assert.Equal(Visibility.Visible, window.Toast.Visibility);
        Assert.True(WorkbenchToastTests.PressEscape((UIElement)Keyboard.FocusedElement).Handled);
        Assert.Equal(Visibility.Collapsed, window.Toast.Visibility);
        Assert.True(window.SettingsOpen);
        Assert.True(window.SettingsOverlay.IsKeyboardFocusWithin);
        Assert.True(WorkbenchToastTests.PressEscape((UIElement)Keyboard.FocusedElement).Handled);
        Closed();
        Open(); Shortcut(); Closed();
        Open(); ClickControl(settings.CloseSettingsButton); Closed();
        Open();
        window.SettingsScrim.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        Closed();

        WpfContext.PressSpace(window.LeftPaneToggle);
        Assert.True(window.ProjectTree.IsKeyboardFocusWithin);
        Shortcut();
        Assert.True(window.SettingsOpen);
        Assert.True(App.Settings.LeftPaneOpen);
        Shortcut();
        Assert.True(window.ProjectTree.IsKeyboardFocusWithin);
        Assert.True(WorkbenchToastTests.PressEscape((UIElement)Keyboard.FocusedElement).Handled);
        Assert.False(App.Settings.LeftPaneOpen);
        Assert.Same(window.LeftPaneToggle, Keyboard.FocusedElement);
    }

    [Theory]
    [InlineData("toolbar")]
    [InlineData("heading")]
    [InlineData("command")]
    public void Mouse_opened_settings_restores_a_tab_stop(string origin)
    {
        wpf.Run(() =>
        {
            var (window, _, _) = WorkbenchControlTests.Window();
            try
            {
                window.Navigate("Log");
                UIElement before = origin switch
                {
                    "toolbar" => window.QuickThemeButton,
                    "command" => window.CommandPaletteButton,
                    _ => window.CurrentPageTitle,
                };
                Assert.Equal(origin != "heading", KeyboardNavigation.GetIsTabStop(before));
                Assert.True(before.Focus());
                window.SettingsButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent });
                window.SettingsButton.Focus();
                ClickControl(window.SettingsButton);
                Assert.True(window.SettingsOpen);
                ClickControl(window.SettingsContent.CloseSettingsButton);
                if (origin == "command")
                    // CommandManager can re-enable the palette button after focus has already returned to the rail.
                    Assert.Contains(Keyboard.FocusedElement, new IInputElement[] { before, window.RailLog });
                else Assert.Same(origin == "toolbar" ? before : window.RailLog, Keyboard.FocusedElement);
                Assert.True(KeyboardNavigation.GetIsTabStop((DependencyObject)Keyboard.FocusedElement));
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("Service")]
    [InlineData("AI")]
    [InlineData("About")]
    public void Opening_settings_focuses_the_requested_section_in_a_short_wide_window(string section)
    {
        wpf.Run(() =>
        {
            var (window, _, _) = WorkbenchControlTests.Window();
            try
            {
                window.Width = 1200; window.Height = 640;
                window.Navigate("Log"); WpfContext.Drain();
                if (section == "Service") ClickControl(window.LogContent.StorageSettingsButton);
                else
                {
                    window.OpenSettings();
                    window.SettingsContent.ShowSection(section);
                }
                WpfContext.Drain(); window.UpdateLayout();
                var selected = Assert.IsType<ListBoxItem>(window.SettingsContent.SectionList.SelectedItem);
                Assert.Equal(section, selected.Tag);
                Assert.Same(selected, Keyboard.FocusedElement);
                Assert.Equal("Log", window.CurrentPage);
                WorkbenchShellDesignTests.AssertSettingsBounds(window, 840, window.Root.ActualHeight * 0.88);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Command_palette_shortcut_is_available_only_outside_modal_surfaces()
    {
        wpf.Run(() =>
        {
            var window = new MainWindow(ProjectPageTestSupport.Model(), false, FeaturePageFixtures.Approvals());
            try
            {
                WpfContext.ShowWindow(window); WpfContext.Drain();
                var binding = Assert.Single(window.InputBindings.OfType<KeyBinding>(), key => key.Key == Key.K);
                Assert.Same(WorkbenchCommands.CommandPalette, binding.Command);
                Assert.Equal(ModifierKeys.Control, binding.Modifiers);
                Assert.True(WorkbenchCommands.CommandPalette.CanExecute(null, window));
                var overlay = (Grid)window.FindName("OverlayHost");
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                overlay.Visibility = Visibility.Visible;
                Assert.False(WorkbenchCommands.CommandPalette.CanExecute(null, window));
                overlay.Visibility = Visibility.Collapsed;
                var operations = (Views.ProjectOperationsView)window.FindName("OperationsContent");
                operations.ShowProjectOptions(); WpfContext.Drain();
                Assert.False(WorkbenchCommands.CommandPalette.CanExecute(null, window));
                operations.HideOptions();
                window.Navigate("Approvals");
                var sheet = (FrameworkElement)((Views.ApprovalsDrawer)window.FindName("ApprovalsContent")).FindName("ConfirmationSheet");
                sheet.Visibility = Visibility.Visible;
                Assert.False(WorkbenchCommands.CommandPalette.CanExecute(null, window));
                sheet.Visibility = Visibility.Collapsed;
                Assert.True(WorkbenchCommands.CommandPalette.CanExecute(null, window));
                WorkbenchCommands.CommandPalette.Execute(null, window);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Closing_settings_restores_rail_focus_after_its_ancestor_is_reenabled()
    {
        wpf.Run(() =>
        {
            var (window, _, _) = WorkbenchControlTests.Window();
            try
            {
                window.Navigate("Log");
                Assert.True(window.RailLog.Focus());
                window.Root.IsEnabled = false;
                Assert.False(window.ShellFrame.IsEnabled);
                window.OpenSettings();
                window.Root.IsEnabled = true;
                WpfContext.Drain();
                Assert.Same(window.SettingsContent.AppearanceSection, Keyboard.FocusedElement);
                Assert.False(window.RailLog.IsEnabled);
                Assert.True(WorkbenchToastTests.PressEscape(window.SettingsContent.AppearanceSection).Handled);
                Assert.False(window.SettingsOpen);
                Assert.True(window.RailLog.IsEnabled);
                Assert.Same(window.RailLog, Keyboard.FocusedElement);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Losing_the_project_navigates_behind_settings_until_the_user_leaves()
    {
        wpf.Run(() =>
        {
            var (window, _, client) = WorkbenchControlTests.Window();
            try
            {
                window.Navigate("VersionControl");
                Assert.Equal("VersionControl", window.CurrentPage);
                window.OpenSettings();
                WpfContext.Drain();
                var focus = Keyboard.FocusedElement;
                Assert.True(window.SettingsContent.IsKeyboardFocusWithin);
                client.Exit();
                WpfContext.Drain();
                Assert.Equal("Engineering", window.CurrentPage);
                Assert.True(window.SettingsOpen);
                Assert.Same(focus, Keyboard.FocusedElement);
                window.Navigate("Log");
                Assert.False(window.SettingsOpen);
                Assert.Equal("Log", window.CurrentPage);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Modal_isolation_coerces_sibling_control_types_even_when_shown_later(bool collapsed)
    {
        wpf.Run(() =>
        {
            var root = new Grid();
            UIElement[] siblings = [new Button(), new RadioButton(), new ListBoxItem(), new StackPanel()];
            foreach (var sibling in siblings)
            {
                sibling.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
                root.Children.Add(sibling);
            }
            var pane = new Button();
            root.Children.Add(pane);
            using (new ModalPane(pane, pane, () => { }))
            {
                foreach (var sibling in siblings) sibling.Visibility = Visibility.Visible;
                Assert.All(siblings, sibling => Assert.False(sibling.IsEnabled));
                Assert.True(pane.IsEnabled);
            }
            Assert.All(siblings, sibling => Assert.True(sibling.IsEnabled));
        });
    }

    [Fact]
    public void Settings_preserves_a_background_siblings_enabled_binding()
    {
        wpf.Run(() =>
        {
            var (window, _, _) = WorkbenchControlTests.Window();
            try
            {
                var source = new CheckBox { IsChecked = false };
                var sibling = window.DroppedErrorAnnouncement;
                var binding = sibling.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(CheckBox.IsChecked)) { Source = source });
                Assert.False(sibling.IsEnabled);
                window.OpenSettings();
                WpfContext.Drain();
                source.IsChecked = true;
                Assert.False(sibling.IsEnabled);
                source.IsChecked = false;
                Assert.True(WorkbenchToastTests.PressEscape(window.SettingsContent.AppearanceSection).Handled);
                Assert.False(window.SettingsOpen);
                Assert.False(sibling.IsEnabled);
                Assert.Same(binding, sibling.GetBindingExpression(UIElement.IsEnabledProperty));
                source.IsChecked = true;
                Assert.True(sibling.IsEnabled);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Rail_arrow_keys_cover_the_order_and_agent_restores_its_subpage_unless_approval_is_pending()
    {
        wpf.Run(() =>
        {
            var approvals = FeaturePageFixtures.Approvals(false);
            var window = new MainWindow(ProjectPageTestSupport.Model(), false, approvals);
            try
            {
                window.ConfigureFeaturePages(new FeaturePageFixtures.Journal(), new FeaturePageFixtures.Audit(), new FeaturePageFixtures.Environment());
                WpfContext.ShowWindow(window); WpfContext.Drain();
                var items = new[] { "RailEngineering", "RailAgent", "RailMcp", "RailClients", "RailEnvironment", "RailAudit", "RailLog", "SettingsButton" }
                    .Select(name => (RadioButton)window.FindName(name)).ToArray();
                Assert.Equal(KeyboardNavigationMode.Once, KeyboardNavigation.GetTabNavigation((Grid)window.FindName("SiderMenu")));
                items[0].Focus();
                for (int i = 0; i < items.Length; i++)
                {
                    var active = i == items.Length - 1 ? window.RailLog : items[i];
                    Assert.True(active.IsChecked);
                    Assert.Same(active, Assert.Single(items, item => item.IsTabStop));
                    items[i].RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(items[i]), 0, Key.Down)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    if (window.SettingsOpen)
                    {
                        WpfContext.Drain();
                        Assert.True(window.SettingsButton.IsChecked);
                        Assert.Same(window.SettingsButton, Assert.Single(items, item => item.IsTabStop));
                        Assert.Same(window.SettingsContent.AppearanceSection, Keyboard.FocusedElement);
                        var beforeEscape = window.NewestCard.Notice;
                        Assert.True(WorkbenchToastTests.PressEscape(window.SettingsContent.AppearanceSection).Handled);
                        var afterEscape = window.NewestCard.Notice;
                        static string Describe(ToastNotice? toast) => toast == null ? "none" : $"{toast.Source} ({toast.FullText})";
                        string state = $"SettingsOpen={window.SettingsOpen}, toast before Escape={Describe(beforeEscape)}, " +
                            $"toast after Escape={Describe(afterEscape)}, {items[i].Name}.IsEnabled={items[i].IsEnabled}";
                        Assert.False(window.SettingsOpen, state);
                        Assert.True(beforeEscape == null && afterEscape == null, state);
                        Assert.True(items[i].IsEnabled && items[i].IsKeyboardFocused, $"{state}, focused={Keyboard.FocusedElement}");
                        Assert.Same(items[i], Keyboard.FocusedElement);
                        items[^1].Focus();
                    }
                    else Assert.Same(items[(i + 1) % items.Length], Keyboard.FocusedElement);
                    if (i == 0) Assert.Equal("Calls", window.CurrentPage);
                }
                window.Navigate("Approvals"); window.Navigate("Mcp"); window.Navigate("Agent");
                Assert.Equal("Approvals", window.CurrentPage); Assert.True(items[1].IsChecked);
                window.Navigate("Calls"); window.Navigate("Mcp");
                var badge = (Button)window.FindName("PendingBadge");
                bool announced = false;
                void OnAnnounced(FrameworkElement element) { if (element == badge) announced = true; }
                AccessibleStatus.Announced += OnAnnounced;
                try { approvals.Receive(FeaturePageFixtures.Request()); WpfContext.Drain(); }
                finally { AccessibleStatus.Announced -= OnAnnounced; }
                Assert.True(announced);
                Assert.Equal(Visibility.Visible, badge.Visibility);
                Assert.Contains(Loc.Current.T("Shell.Pending", 1), System.Windows.Automation.AutomationProperties.GetName(items[1]));
                ClickControl(items[1]);
                Assert.Equal("Approvals", window.CurrentPage); Assert.True(items[1].IsChecked);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Approval_toggles_directly_and_diagnostics_export_remains_available()
    {
        wpf.Run(() =>
        {
            var diagnostics = new FeaturePageFixtures.Diagnostics();
            var window = new MainWindow(new MainViewModel(new FakeProjectClient(), new FakeDialogService()), false, approvals: new Services.Stubs.ApprovalServiceStub(), diagnostics: diagnostics);
            try
            {
                window.OpenSettings();
                var settings = (TiaOpenness.Gui.Views.SettingsView)window.FindName("SettingsContent");
                settings.ShowSection("AI");
                Assert.Equal(0, window.Approvals.PendingCount);
                Assert.Equal(120, window.Approvals.TimeoutSeconds);
                ClickApprovalControl((RadioButton)settings.FindName("ApprovalOff"), window.Approvals, nameof(window.Approvals.Enabled));
                WpfContext.Drain();
                Assert.False(window.Approvals.Enabled);
                Assert.Equal(Loc.Current["Shell.ApprovalOffStatus"], window.ApprovalStatusText.Text);
                ClickApprovalControl((RadioButton)settings.FindName("Timeout300"), window.Approvals, nameof(window.Approvals.TimeoutSeconds));
                Assert.Equal(300, window.Approvals.TimeoutSeconds);
                ClickApprovalControl((RadioButton)settings.FindName("ApprovalOn"), window.Approvals, nameof(window.Approvals.Enabled));
                Assert.True(window.Approvals.Enabled);
                Assert.Null(window.FindName("ExportDiagnostics"));
                window.Features.Export();
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
                Assert.True(approvals.Entered.Wait(TestDeadlines.Hang));
                Assert.Equal(120, approvals.TimeoutSeconds);
                approvals.Release.Set();
                WpfContext.Complete(approvals.Saved.Task.WaitAsync(TestDeadlines.Hang));
                Assert.Equal(300, approvals.TimeoutSeconds);
            }
            finally { approvals.Release.Set(); window.Close(); }
        });
    }

    private sealed class DelayedApprovalService : Stubs.IApprovalService, IDisposable
    {
        private readonly Stubs.ApprovalServiceStub inner = new();
        public int OnlinePlanLifetimeSeconds { get => inner.OnlinePlanLifetimeSeconds; set => inner.OnlinePlanLifetimeSeconds = value; }
        internal readonly System.Threading.ManualResetEventSlim Entered = new(), Release = new();
        internal readonly System.Threading.Tasks.TaskCompletionSource<bool> Saved = new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Enabled { get => inner.Enabled; set => inner.Enabled = value; }
        public int TimeoutSeconds
        {
            get => inner.TimeoutSeconds;
            set
            {
                Entered.Set();
                if (!Release.Wait(TestDeadlines.Hang)) throw new TimeoutException("Fixture save was not released.");
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
            WpfContext.ShowWindow(dialog); FlushMenu();
            var choice = DesktopCapture.Descendants<Button>((FrameworkElement)dialog.Content).ElementAt(index);
            typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(choice, null);
            Assert.Equal(expected, dialog.Result);
            Assert.False(dialog.IsVisible);
        });
    }
}
