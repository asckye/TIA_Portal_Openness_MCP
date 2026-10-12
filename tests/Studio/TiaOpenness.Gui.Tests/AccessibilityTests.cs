using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Themes;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class AccessibilityTests(WpfContext wpf)
{
    [Fact]
    public void Every_page_control_has_an_explicit_name_and_every_input_has_a_label()
    {
        var interactive = new HashSet<string> { "Button", "ToggleButton", "RadioButton", "CheckBox", "TextBox", "PasswordBox",
            "ComboBox", "ListBox", "TreeView", "DataGrid", "Expander", "LogTailView", "WorkbenchLogView" };
        var failures = new List<string>();
        int controls = 0, inputs = 0;
        foreach (var file in SourceScan.Markup.Where(f => f.Name.Contains(".Views.", StringComparison.Ordinal)
            || f.Name.Contains(".Configuration.", StringComparison.Ordinal) || f.Name.EndsWith(".MainWindow.xaml", StringComparison.Ordinal)))
        foreach (var element in XDocument.Parse(file.Text).Descendants().Where(e => interactive.Contains(e.Name.LocalName)))
        {
            controls++;
            if (string.IsNullOrWhiteSpace((string?)element.Attribute("AutomationProperties.Name")))
                failures.Add(file.Name + ": unnamed " + element.Name.LocalName);
            if (element.Name.LocalName is "TextBox" or "PasswordBox" or "ComboBox")
            {
                inputs++;
                if (element.Attribute("AutomationProperties.LabeledBy") == null) failures.Add(file.Name + ": unlabelled input");
            }
        }
        Assert.True(controls >= 150); Assert.True(inputs >= 23);
        Assert.Empty(failures);
    }

    [Fact]
    public void High_contrast_uses_dynamic_system_colours_and_overrides_explicit_appearance()
    {
        var source = SourceScan.Markup.Single(f => f.Name.EndsWith(".Palette.HighContrast.xaml", StringComparison.Ordinal)).Text;
        var brushes = XDocument.Parse(source).Root!.Elements().ToArray();
        Assert.True(brushes.Length >= 29);
        var lightSource = SourceScan.Markup.Single(f => f.Name.EndsWith(".Palette.Light.xaml", StringComparison.Ordinal)).Text;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        Assert.Equal(XDocument.Parse(lightSource).Root!.Elements().Select(b => (string)b.Attribute(x + "Key")!).Order(),
            brushes.Select(b => (string)b.Attribute(x + "Key")!).Order());
        Assert.All(brushes, b => Assert.StartsWith("{DynamicResource {x:Static SystemColors.", (string)b.Attribute("Color")!));
        Assert.DoesNotContain("#", source);
        var templates = SourceScan.Markup.Single(f => f.Name.EndsWith(".Themes.Primer.xaml", StringComparison.Ordinal)).Text;
        Assert.DoesNotMatch(@"#[\da-fA-F]{3,8}\b|\b(?:Colors|Brushes)\.(?!Transparent\b)\w+|\bColor\.From\w+", templates);
        wpf.Run(() =>
        {
            var manager = ThemeManager.Current;
            var previous = manager.Theme;
            try
            {
                manager.HighContrastOverride = true;
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    manager.Theme = theme;
                    Assert.Equal(SystemColors.WindowColor, ((SolidColorBrush)Application.Current.FindResource("Primer.bg")).Color);
                    Assert.Equal(SystemColors.WindowTextColor, ((SolidColorBrush)Application.Current.FindResource("Primer.textMuted")).Color);
                }
            }
            finally { manager.HighContrastOverride = null; manager.Theme = previous; }
        });
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    public void Boundaries_focus_badges_and_action_text_meet_AA(string theme)
    {
        wpf.Run(() =>
        {
            var palette = new ResourceDictionary { Source = new Uri(ThemeManager.PackPrefix + "Palette." + theme + ".xaml") };
            Color Token(string key) => ((SolidColorBrush)palette["Primer." + key]).Color;
            void Check(string foreground, string background, double minimum)
            {
                var bg = ThemeSurfaceTests.Composite(Token(background), Token("bg"));
                var fg = ThemeSurfaceTests.Composite(Token(foreground), bg);
                double first = ThemeSurfaceTests.Luminance(fg), second = ThemeSurfaceTests.Luminance(bg);
                double ratio = (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
                Assert.True(ratio >= minimum, $"{theme}: {foreground}/{background} {ratio:F2} < {minimum}");
            }
            foreach (string surface in new[] { "bg", "card", "input", "cardSel", "pill", "pillHover" })
                foreach (string text in new[] { "text", "textMuted" }) Check(text, surface, 4.5);
            Check("primaryText", "primaryBg", 4.5); Check("warningText", "warn", 4.5);
            Check("warn", "countWarningBg", 4.5); Check("onAccent", "accent", 4.5);
            Check("inputBorder", "input", 3); Check("checkBorder", "card", 3);
            Check("focusOuter", "focusInner", 3); Check("accent", "cardSel", 3);
        });
    }

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void Action_hover_templates_keep_contrasting_text_and_pressed_buttons_do_not_fade(AppTheme theme)
    {
        wpf.Run(() =>
        {
            var previous = ThemeManager.Current.Theme;
            ThemeManager.Current.Theme = theme;
            try
            {
                foreach (string style in new[] { "Primer.Run", "Primer.Primary", "Primer.WarningAction", "Primer.Secondary", "Primer.OutlinedAccent" })
                {
                    var button = new Button { Content = "fixture", Width = 160, Height = 32 };
                    button.SetResourceReference(FrameworkElement.StyleProperty, style);
                    button.ApplyTemplate();
                    var hover = (Border)button.Template.FindName("Hover", button);
                    var fg = ((SolidColorBrush)button.Foreground).Color;
                    var bg = ((SolidColorBrush)hover.Background).Color;
                    double first = ThemeSurfaceTests.Luminance(fg), second = ThemeSurfaceTests.Luminance(bg);
                    Assert.True((Math.Max(first, second) + .05) / (Math.Min(first, second) + .05) >= 4.5, style);
                }
                var source = SourceScan.Markup.Single(f => f.Name.EndsWith(".Themes.Primer.xaml", StringComparison.Ordinal)).Text;
                var pressed = XDocument.Parse(source).Descendants().Where(e => e.Name.LocalName == "Trigger"
                    && (string?)e.Attribute("Property") == "IsPressed");
                Assert.DoesNotContain(pressed.SelectMany(e => e.Elements()), e => (string?)e.Attribute("Property") == "Opacity");
            }
            finally { ThemeManager.Current.Theme = previous; }
        });
    }

    [Theory]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Chinese)]
    public void Real_page_and_dialog_automation_peers_have_meaningful_names(AppLanguage language)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var window = Window();
            var unnamed = new List<string>();
            int checkedControls = 0;
            try
            {
                window.Show();
                foreach (string page in Pages)
                {
                    Navigate(window, page);
                    foreach (var element in DesktopCapture.Descendants<UIElement>(window).Where(e => e.IsVisible
                        && e is ButtonBase or TextBox or PasswordBox or ComboBox or ListBoxItem or TreeViewItem or Expander))
                    {
                        var peer = UIElementAutomationPeer.CreatePeerForElement(element);
                        string name = peer?.GetName() ?? "";
                        checkedControls++;
                        if (string.IsNullOrWhiteSpace(name) || name.Contains("TiaOpenness.", StringComparison.Ordinal))
                            unnamed.Add(page + ": " + element.GetType().Name + " " + (element as FrameworkElement)?.Name);
                    }
                }
                window.OpenSettings(); WpfContext.Drain();
                Assert.True(AutomationProperties.GetIsDialog((UIElement)window.FindName("DrawerOverlay")));
                Assert.Same(window.FindName("DrawerTitle"), Keyboard.FocusedElement);
                using var dialog = new DialogScope(new WorkbenchMessageBox("fixture", "fixture", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning));
                dialog.Window.Show(); WpfContext.Drain();
                Assert.All(DesktopCapture.Descendants<Button>(dialog.Window), button => Assert.False(string.IsNullOrWhiteSpace(
                    UIElementAutomationPeer.CreatePeerForElement(button)!.GetName())));
                Assert.True(checkedControls >= 60);
                Assert.True(unnamed.Count == 0, string.Join(Environment.NewLine, unnamed));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Drawer_and_confirmation_trap_Tab_accept_Escape_and_restore_focus()
    {
        wpf.Run(() =>
        {
            var window = Window();
            try
            {
                window.Show();
                var trigger = (Button)window.FindName("SettingsButton"); trigger.Focus();
                window.OpenSettings(); WpfContext.Drain();
                var drawer = (Grid)window.FindName("DrawerOverlay");
                Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(drawer));
                Assert.False(((FrameworkElement)window.FindName("EngineeringPage")).IsEnabled);
                var settings = (Views.SettingsView)window.FindName("SettingsContent");
                UnifiedDesktopTests.ClickControl((RadioButton)settings.FindName("ApprovalOff")); WpfContext.Drain();
                var cancel = (Button)window.FindName("CancelApprovalOff");
                Assert.True(cancel.IsDefault); Assert.True(cancel.IsCancel); Assert.Same(cancel, Keyboard.FocusedElement);
                cancel.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                Assert.Same(window.FindName("ConfirmApprovalOff"), Keyboard.FocusedElement);
                ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                Assert.Same(cancel, Keyboard.FocusedElement);
                Escape(cancel); WpfContext.Drain();
                Assert.Equal(Visibility.Collapsed, ((Grid)window.FindName("ConfirmOverlay")).Visibility);
                Assert.True(window.Approvals.Enabled);
                Escape((UIElement)window.FindName("DrawerTitle")); WpfContext.Drain();
                Assert.Equal(Visibility.Collapsed, drawer.Visibility); Assert.Same(trigger, Keyboard.FocusedElement);
                window.Navigate("Engineering");
                var operations = (Views.ProjectOperationsView)window.FindName("OperationsContent");
                operations.ShowProjectOptions(); WpfContext.Drain();
                var options = (Views.EngineeringOptionsView)operations.FindName("Options");
                var title = (UIElement)options.FindName("OptionsTitle");
                Assert.Same(title, Keyboard.FocusedElement);
                Escape(title); WpfContext.Drain();
                Assert.False(options.IsOpen);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(MessageBoxButton.OKCancel)]
    [InlineData(MessageBoxButton.YesNoCancel)]
    [InlineData(MessageBoxButton.YesNo)]
    public void Risky_confirmation_defaults_to_the_safe_choice(MessageBoxButton buttons)
    {
        wpf.Run(() =>
        {
            using var dialog = new DialogScope(new WorkbenchMessageBox("fixture", "fixture", buttons, MessageBoxImage.Warning));
            dialog.Window.Show(); WpfContext.Drain();
            var safe = Assert.Single(DesktopCapture.Descendants<Button>(dialog.Window), b => b.IsDefault);
            Assert.True(safe.IsCancel); Assert.Same(safe, Keyboard.FocusedElement);
            Assert.DoesNotContain("_", UIElementAutomationPeer.CreatePeerForElement(safe)!.GetName());
            Escape(safe);
            Assert.Equal(buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel, dialog.Window.Result);
        });
    }

    [Theory]
    [InlineData(Services.ConfirmationAction.SaveProject, "Dialog.Save.Action")]
    [InlineData(Services.ConfirmationAction.EndSharedSession, "Dialog.Disconnect.Action")]
    [InlineData(Services.ConfirmationAction.OverwriteProject, "Dialog.Pull.Action")]
    [InlineData(Services.ConfirmationAction.ImportBlocks, "Dialog.Import.Action")]
    public void Risk_actions_have_specific_verbs_and_session_detach_is_an_alternative(Services.ConfirmationAction action, string key)
    {
        wpf.Run(() =>
        {
            var buttons = action == Services.ConfirmationAction.ImportBlocks ? MessageBoxButton.YesNoCancel : MessageBoxButton.OKCancel;
            using var dialog = new DialogScope(new WorkbenchMessageBox("fixture", "fixture", buttons, MessageBoxImage.Warning, new(action)));
            dialog.Window.Show(); WpfContext.Drain();
            var controls = DesktopCapture.Descendants<Button>(dialog.Window).ToArray();
            Assert.Contains(controls, b => Equals(b.Content, Loc.Current[key]));
            Assert.True(Assert.Single(controls, b => b.IsDefault).IsCancel);
            if (action == Services.ConfirmationAction.EndSharedSession)
                Assert.Contains(controls, b => Equals(b.Content, Loc.Current["Dialog.Detach.Action"]));
        });
    }

    [Fact]
    public void AI_switch_guard_blocks_access_keys_and_busy_without_a_total_is_indeterminate()
    {
        wpf.Run(() =>
        {
            var window = Window();
            try
            {
                window.Show(); window.ControlGuard.UiChanged();
                var button = (UIElement)window.FindName("SettingsButton");
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(button), 0, Key.System)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                button.RaiseEvent(key); Assert.True(key.Handled);
                using var activity = new Services.WorkbenchActivity();
                var completion = new System.Threading.Tasks.TaskCompletionSource();
                var work = activity.Guarded("Status.Connecting", () => completion.Task);
                Assert.True(activity.Busy); Assert.True(activity.ProgressIndeterminate);
                completion.SetResult(); WpfContext.Complete(work);
                Assert.False(activity.ProgressIndeterminate);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Prefill_annotation_is_visible_announced_and_clearable_by_keyboard()
    {
        wpf.Run(() =>
        {
            var input = new TextBox();
            var marker = new PrefillMarker { Target = input };
            bool announced = false, cleared = false;
            void Announcement(FrameworkElement element) => announced = true;
            AccessibleStatus.Announced += Announcement;
            marker.ClearRequested += (_, _) => { cleared = true; marker.SetPending(false); };
            try
            {
                marker.SetPending(true);
                Assert.Equal(Visibility.Visible, marker.Visibility);
                Assert.Equal(Loc.Current["Control.Prefilled"], AutomationProperties.GetHelpText(input));
                Assert.True(announced);
                UnifiedDesktopTests.ClickControl((Button)marker.Children[1]);
                Assert.True(cleared); Assert.Equal(Visibility.Collapsed, marker.Visibility);
                Assert.Empty(AutomationProperties.GetHelpText(input));
            }
            finally { AccessibleStatus.Announced -= Announcement; }
        });
    }

    [Theory]
    [InlineData(AppLanguage.English, 1)]
    [InlineData(AppLanguage.Chinese, 1)]
    [InlineData(AppLanguage.English, 2)]
    [InlineData(AppLanguage.Chinese, 2)]
    public void Minimum_window_keeps_navigation_and_commands_available_at_double_text_size(AppLanguage language, int scale)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var previousTheme = ThemeManager.Current.Theme;
            var window = Window();
            try
            {
                Assert.True(window.MinWidth <= 1024); Assert.True(window.MinHeight <= 640);
                foreach (string key in new[] { "Caption", "Control", "SmallTitle", "Section", "Title", "Subtitle", "Heading" })
                    window.Resources["Ui.FontSize." + key] = (double)Application.Current.FindResource("Ui.FontSize." + key) * scale;
                window.Width = 1024; window.Height = 640; window.Show();
                foreach (string theme in new[] { "Light", "Dark", "HighContrast" })
                {
                    ThemeManager.Current.HighContrastOverride = theme == "HighContrast";
                    ThemeManager.Current.Theme = theme == "Dark" ? AppTheme.Dark : AppTheme.Light;
                    foreach (string page in Pages)
                    {
                        Navigate(window, page);
                        var root = (FrameworkElement)window.Content;
                        var title = (TextBlock)window.FindName("CurrentPageTitle");
                        Assert.True(title.IsVisible && title.ActualHeight > 0);
                        var point = ((FrameworkElement)window.FindName("StatusInformation")).TranslatePoint(new Point(), root);
                        Assert.InRange(point.Y, 0, root.ActualHeight);
                        Assert.DoesNotContain(DesktopCapture.Descendants<Button>(root).Where(b => b.IsVisible),
                            b => b.ActualWidth <= 0 || b.ActualHeight < 24);
                        foreach (var button in DesktopCapture.Descendants<ButtonBase>(root).Where(b => b.IsVisible && !InsideScroll(b)))
                        {
                            var origin = button.TranslatePoint(new Point(), root);
                            Assert.True(origin.X >= 0 && origin.Y >= 0 && origin.X + button.ActualWidth <= root.ActualWidth + 1
                                && origin.Y + button.ActualHeight <= root.ActualHeight + 1,
                                $"{page} text{scale}: {button.Name} clipped at {origin}, {button.RenderSize}, root {root.RenderSize}");
                        }
                        DesktopCapture.Save(root, $"accessibility-{page}-{language}-{theme}-1024x640-text{scale}");
                    }
                }
            }
            finally { window.Close(); ThemeManager.Current.HighContrastOverride = null; ThemeManager.Current.Theme = previousTheme; }
        });
    }

    private static readonly string[] Pages = ["Mcp", "Engineering", "Blocks", "VersionControl", "Calls", "Audit", "Environment", "Log"];
    private static MainWindow Window()
    {
        var window = new MainWindow(ProjectPageTestSupport.Model(release: "21"), false, FeaturePageFixtures.Approvals(), new FeaturePageFixtures.Diagnostics());
        window.ConfigureFeaturePages(new FeaturePageFixtures.Journal(), new FeaturePageFixtures.Audit(), new FeaturePageFixtures.Environment(),
            new FeaturePageFixtures.Notification(), () => FeaturePageFixtures.Now);
        window.ShowConfiguration(false);
        return window;
    }
    private static void Navigate(MainWindow window, string page)
    {
        window.Navigate(page); WpfContext.Drain(); window.UpdateLayout();
        string title = page switch
        {
            "Mcp" => "Desktop.Configuration", "Engineering" => "Shell.Engineering", "Blocks" => "Tree.ProgramBlocks",
            "VersionControl" => "Shell.VersionControl", "Calls" => "Shell.Calls", "Audit" => "Shell.Audit",
            "Environment" => "Shell.Environment", _ => "Tab.Log",
        };
        Assert.Equal(Loc.Current[title], ((TextBlock)window.FindName("CurrentPageTitle")).Text);
    }
    private static void Escape(UIElement element) => element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
        PresentationSource.FromVisual(element), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    private static bool InsideScroll(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer) return true;
        return false;
    }
    private sealed class DialogScope(WorkbenchMessageBox window) : IDisposable
    {
        internal WorkbenchMessageBox Window => window;
        public void Dispose() => window.Close();
    }
}
