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

    [Theory]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Chinese)]
    public void Visible_page_access_keys_are_unique_and_match_the_accessible_labels(AppLanguage language)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var window = Window();
            try
            {
                WpfContext.ShowWindow(window);
                string[] pages = ["Engineering", "VersionControl", "Mcp", "Clients", "Calls", "Approvals", "Audit", "Environment", "Log", "Settings"];
                var names = new List<string>();
                int total = 0;
                foreach (string page in pages)
                {
                    window.Navigate(page);
                    if (page == "Engineering")
                        ((ViewModels.MainViewModel)window.DataContext).Engineering.Document = ViewModels.ProjectDocument.Compile;
                    if (page == "Environment")
                    {
                        window.UpdateLayout();
                        DesktopCapture.Descendants<Expander>(window.EnvironmentContent)
                            .Single(e => Equals(e.Header, Loc.Current["Env.FirstConnection"])).IsExpanded = true;
                    }
                    if (page == "Settings")
                    {
                        window.SettingsContent.ShowSection("Service");
                        window.SettingsContent.RetentionDetails.IsExpanded = true;
                    }
                    WpfContext.Drain();
                    window.UpdateLayout();
                    Assert.Equal(page == "Settings" ? "Log" : page, window.CurrentPage);
                    Assert.Equal(page == "Settings", window.SettingsOpen);
                    if (window.SettingsOpen)
                        Assert.All(DesktopCapture.Descendants<ButtonBase>(window.ShellFrame).Where(button => button.IsVisible),
                            button => Assert.False(button.IsEnabled));
                    var keys = RenderedAccessKeys(page == "Settings" ? window.SettingsContent : window, page, language);
                    total += keys.Count;
                    string[] expected = page switch
                    {
                        "Engineering" => ["Pages.CompileSoftware", "Pages.Rules", "Pages.Inspect"],
                        "VersionControl" => ["Pages.PreviewSync", "Pages.RunSync"],
                        "Mcp" => ["Pages.SaveBoth"],
                        "Audit" => ["Audit.Verify"],
                        "Environment" => ["Env.Recheck", "Env.DiagnosticsLink"],
                        "Log" => ["Log.StorageLink", "Pages.ClearLog", "Env.Generate"],
                        "Settings" => ["Audit.OpenFolder"],
                        _ => [],
                    };
                    AssertRenderedCaptions(keys, expected);
                    foreach (var button in DesktopCapture.Descendants<ButtonBase>(window).Where(button => button.IsVisible))
                    {
                        if (button.Content is not string caption || !caption.Any(char.IsLetter)) continue;
                        // A template can render just an icon. Compare the caption only when it is also visible.
                        var texts = DesktopCapture.Descendants<TextBlock>(button);
                        if (!texts.Any(text => text.IsVisible && text.Text == caption)) continue;
                        string name = UIElementAutomationPeer.CreatePeerForElement(button)!.GetName();
                        string words = caption.TrimEnd('…', '.', ' ', '→');
                        if (!name.Contains(words, StringComparison.OrdinalIgnoreCase))
                            names.Add($"{page}: {caption} / {name}");
                    }
                    if (page == "VersionControl")
                    {
                        var files = DesktopCapture.Descendants<Button>(window.VersionControlContent)
                            .Where(button => button.IsVisible && button.Tag is Contracts.Models.MappedObjectInfo).ToArray();
                        Assert.Contains(files, button => ((Contracts.Models.MappedObjectInfo)button.Tag).FilePath.Contains('_'));
                        foreach (var button in files)
                        {
                            var path = ((Contracts.Models.MappedObjectInfo)button.Tag).FilePath;
                            Assert.Equal(path, Assert.Single(DesktopCapture.Descendants<TextBlock>(button)).Text);
                            Assert.Equal(path, UIElementAutomationPeer.CreatePeerForElement(button)!.GetName());
                        }
                    }
                    if (page == "Log")
                    {
                        UnifiedDesktopTests.ClickControl(window.LogContent.MoreActions);
                        Assert.True(window.LogContent.MoreActions.ContextMenu.IsOpen);
                        Assert.Equal(Loc.Current["Pages.CopyLog"], window.LogContent.CopyLogButton.Header);
                        CheckSurface(window.LogContent.MoreActions.ContextMenu, "Log actions", "Pages.CopyLog");
                        window.LogContent.MoreActions.ContextMenu.IsOpen = false;
                        foreach (var button in new[] { window.LogContent.StorageSettingsButton, window.LogContent.ClearLogButton,
                            window.LogContent.GenerateButton })
                        {
                            var label = Assert.Single(DesktopCapture.Descendants<AccessText>(button));
                            Assert.Contains(char.ToUpperInvariant(label.AccessKey), keys.Keys);
                            Assert.Equal(DisplayedLabel(label), AutomationProperties.GetName(button));
                        }
                    }
                }
                Assert.True(total >= 13, $"Rendered {total} access keys across pages ({language}).");
                Assert.Empty(names);
                window.Navigate("Engineering");
                var project = window.OperationsContent;
                UnifiedDesktopTests.ClickControl(project.ExportMenuButton);
                var transfer = project.ExportMenuButton.ContextMenu;
                try { CheckSurface(transfer, "Transfer menu", "Pages.Import", "Pages.GenerateAtlas"); }
                finally { transfer.IsOpen = false; }
                var tree = window.ProjectExplorer.Nodes;
                var menu = tree.ContextMenu;
                menu.PlacementTarget = tree;
                menu.IsOpen = true;
                try { CheckSurface(menu, "Project tree menu", "Pages.ClearSelection"); }
                finally { menu.IsOpen = false; }
                foreach (string section in new[] { "Project", "Inspect" })
                {
                    project.Options.Show(section);
                    string[] fields = section == "Project" ? ["Access.FieldConfigVersion", "Access.FieldSessionProcessId"]
                        : ["Access.FieldPrimerNamingPattern"];
                    CheckSurface(project.Options, section + " options", fields);
                }
                project.Options.Hide();

                void CheckSurface(FrameworkElement surface, string name, params string[] captions)
                {
                    WpfContext.Drain();
                    surface.UpdateLayout();
                    AssertRenderedCaptions(RenderedAccessKeys(surface, name, language), captions);
                }
            }
            finally { window.Close(); }
        });
    }

    private static Dictionary<char, string> RenderedAccessKeys(FrameworkElement surface, string page, AppLanguage language)
    {
        var keys = new Dictionary<char, string>();
        var owners = new HashSet<Control>();
        foreach (var label in DesktopCapture.Descendants<AccessText>(surface).Where(label => label.IsVisible && label.AccessKey != '\0'))
        {
            DependencyObject owner = label;
            while (owner is not Control) owner = VisualTreeHelper.GetParent(owner);
            var control = (Control)owner;
            owners.Add(control);
            var key = char.ToUpperInvariant(label.AccessKey);
            string name = UIElementAutomationPeer.CreatePeerForElement(control)!.GetName();
            string visible = DisplayedLabel(label);
            Assert.Equal(visible, name);
            Assert.True(keys.TryAdd(key, visible), $"{page} ({language}), Alt+{key}: {keys[key]} / {visible}");
        }
        // A localized caption can declare a mnemonic even if its template renders only plain text.
        foreach (var control in DesktopCapture.Descendants<Control>(surface).Where(control => control.IsVisible))
        {
            var property = control switch
            {
                MenuItem => HeaderedItemsControl.HeaderProperty,
                Label or ButtonBase => ContentControl.ContentProperty,
                _ => null,
            };
            if (property == null || control.GetBindingExpression(property)?.ParentBinding.Source != Loc.Current) continue;
            if (control.GetValue(property) is string caption && caption.Contains('_'))
                Assert.True(owners.Contains(control), $"{page} ({language}) did not render the access key in {caption}.");
        }
        return keys;
    }

    private static void AssertRenderedCaptions(Dictionary<char, string> keys, params string[] expected)
    {
        foreach (string resource in expected)
        {
            string caption = Loc.Current[resource];
            int marker = caption.IndexOf('_');
            Assert.True(marker >= 0, resource);
            Assert.True(keys.TryGetValue(char.ToUpperInvariant(caption[marker + 1]), out string? visible),
                $"Missing {resource} ({caption}). Rendered: {string.Join(", ", keys.Values)}");
            Assert.Equal(caption.Remove(marker, 1), visible);
        }
    }

    private static string DisplayedLabel(AccessText label)
    {
        string caption = label.Text;
        int marker = caption.IndexOf("_" + label.AccessKey, StringComparison.Ordinal);
        Assert.True(marker >= 0, caption);
        return caption.Remove(marker, 1).Replace("__", "_");
    }

    [Fact]
    public void Every_text_size_uses_a_design_ramp_resource()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var sizes = SourceScan.Markup.SelectMany(file => XDocument.Parse(file.Text).Descendants())
            .Where(element => ((string?)element.Attribute(x + "Key"))?.StartsWith("Ui.FontSize.", StringComparison.Ordinal) == true).ToArray();
        Assert.NotEmpty(sizes);
        double[] ramp = [9, 9.5, 10, 10.5, 11, 11.5, 12, 12.5, 13, 14, 14.5, 15.5, 18];
        Assert.Equal(ramp, sizes.Select(size => (double)size).Distinct().Order());
        foreach (var file in SourceScan.Markup)
        {
            var attributes = XDocument.Parse(file.Text).Descendants().SelectMany(element => element.Attributes()
                .Where(a => a.Name.LocalName.EndsWith("FontSize", StringComparison.Ordinal)
                    || (a.Name == "Value" && ((string?)element.Attribute("Property"))?.EndsWith("FontSize", StringComparison.Ordinal) == true)));
            Assert.All(attributes, a => Assert.Matches(@"^\{(?:Dynamic|Static)Resource Ui\.FontSize\.[\w.]+\}$", a.Value));
        }
        foreach (var file in SourceScan.Code) Assert.DoesNotMatch(@"\bFontSize\s*=\s*\d", file.Text);
    }

    [Fact]
    public void High_contrast_uses_the_Night_Sky_design_and_overrides_explicit_appearance()
    {
        var source = SourceScan.Markup.Single(f => f.Name.EndsWith(".Palette.HighContrast.xaml", StringComparison.Ordinal)).Text;
        var brushes = XDocument.Parse(source).Root!.Elements().ToArray();
        Assert.True(brushes.Length >= 29);
        var lightSource = SourceScan.Markup.Single(f => f.Name.EndsWith(".Palette.Light.xaml", StringComparison.Ordinal)).Text;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        Assert.Equal(XDocument.Parse(lightSource).Root!.Elements().Select(b => (string)b.Attribute(x + "Key")!).Order(),
            brushes.Select(b => (string)b.Attribute(x + "Key")!).Order());
        Assert.All(brushes, b =>
        {
            string key = ((string)b.Attribute(x + "Key")!).Replace("Primer.", "");
            var expected = key == "brandGradient" ? Colors.Yellow : WorkbenchDesignColors.Get("HighContrast", key);
            Assert.Equal(expected, (Color)ColorConverter.ConvertFromString((string)b.Attribute("Color")!));
        });
        Assert.DoesNotContain("SystemColors", source);
        var templates = string.Join(Environment.NewLine, SourceScan.Vocabulary.Select(file => file.Text));
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
                    Assert.Equal(Colors.Black, ((SolidColorBrush)Application.Current.FindResource("Primer.bg")).Color);
                    Assert.Equal(Colors.White, ((SolidColorBrush)Application.Current.FindResource("Primer.textMuted")).Color);
                }
            }
            finally { manager.HighContrastOverride = null; manager.Theme = previous; }
        });
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    [InlineData("Light.Petrol")]
    [InlineData("Dark.Petrol")]
    public void Boundaries_focus_badges_and_action_text_meet_AA(string theme)
    {
        wpf.Run(() =>
        {
            var palette = new ResourceDictionary { Source = new Uri(ThemeManager.PackPrefix + "Palette." + theme + ".xaml") };
            Color Token(string key) => ((SolidColorBrush)palette[key.StartsWith("Ant.") ? key : "Primer." + key]).Color;
            void Check(string foreground, string background, double minimum)
            {
                var bg = ThemeSurfaceTests.Composite(Token(background), Token("bg"));
                var fg = ThemeSurfaceTests.Composite(Token(foreground), bg);
                double first = ThemeSurfaceTests.Luminance(fg), second = ThemeSurfaceTests.Luminance(bg);
                double ratio = (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
                WorkbenchDesignColors.AssertContrast(theme, foreground, background, "bg", ratio, minimum);
            }
            foreach (string surface in new[] { "bg", "card", "input", "cardSel", "pill", "pillHover" })
                foreach (string text in new[] { "text", "textMuted" }) Check(text, surface, 4.5);
            foreach (string surface in new[] { "window", "bg", "card", "cardSoft", "input", "cardSel", "pill", "pillHover", "menuBg", "sidebar",
                "titlebar", "rail", "statusBar", "codeBg", "accentSoft", "okBg", "warnBg", "noteBg", "Ant.colorErrorBg", "countWarningBg" })
                Check("textFaint", surface, 4.5);
            Check("accent", "accentSoft", 4.5); Check("accent", "rail", 4.5);
            foreach (string kind in new[] { "Ob", "Fb", "Fc", "Db", "Fs" }) Check("blk" + kind + "Fg", "blk" + kind + "Bg", 4.5);
            foreach (string client in new[] { "Claude", "Codex", "Gemini", "Cursor", "VSCode", "OpenCode", "Qwen", "Kimi", "Yuanbao", "Neutral" })
                Check("client" + client + "Fg", "client" + client + "Bg", 4.5);
            foreach (string syntax in new[] { "Keyword", "Comment", "String", "Number", "Variable" }) Check("syntax" + syntax, "codeBg", 4.5);
            Check("diffGreen", "diffAddBg", 4.5); Check("diffRed", "diffDelBg", 4.5);
            Check("ok", "okBg", 4.5); Check("warn", "warnBg", 4.5); Check("destructiveText", "Ant.colorErrorBg", 4.5);
            Check("primaryText", "primaryBg", 4.5); Check("warningText", "warn", 4.5);
            Check("destructiveText", "pill", 4.5); Check("destructiveText", "pillHover", 4.5);
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
                    var surface = ThemeSurfaceTests.Composite(((SolidColorBrush)button.Background).Color,
                        ((SolidColorBrush)button.FindResource("Primer.bg")).Color);
                    var bg = ThemeSurfaceTests.Composite(((SolidColorBrush)hover.Background).Color, surface);
                    double first = ThemeSurfaceTests.Luminance(fg), second = ThemeSurfaceTests.Luminance(bg);
                    var ratio = (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
                    if (style == "Primer.WarningAction")
                        WorkbenchDesignColors.AssertContrast(theme.ToString(), "onDanger", "Ant.colorErrorSolidHover", "bg", ratio, 4.5);
                    else Assert.True(ratio >= 4.5, style);
                }
                var pressed = SourceScan.Vocabulary.SelectMany(file => XDocument.Parse(file.Text).Descendants())
                    .Where(e => e.Name.LocalName == "Trigger"
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
            window.Width = 1200; // Exercise the modal call drawer below the docking breakpoint.
            var unnamed = new List<string>();
            int checkedControls = 0;
            try
            {
                WpfContext.ShowWindow(window);
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
                Assert.Equal(Visibility.Visible, window.SettingsOverlay.Visibility);
                Assert.Same(window.SettingsContent.AppearanceSection, Keyboard.FocusedElement);
                window.Navigate("Calls");
                window.Features.OpenCall(window.Features.Calls[0]); WpfContext.Drain();
                Assert.True(AutomationProperties.GetIsDialog((UIElement)window.FindName("DrawerOverlay")));
                Assert.Same(window.FindName("DrawerTitle"), Keyboard.FocusedElement);
                using var dialog = new DialogScope(new WorkbenchMessageBox("fixture", "fixture", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning));
                WpfContext.ShowWindow(dialog.Window); WpfContext.Drain();
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
            window.Width = 1200; // Exercise the modal call drawer below the docking breakpoint.
            try
            {
                WpfContext.ShowWindow(window);
                window.OpenSettings(); WpfContext.Drain();
                var settings = (Views.SettingsView)window.FindName("SettingsContent");
                Assert.True(settings.IsEnabled);
                Assert.Equal(Visibility.Collapsed, ((Grid)window.FindName("DrawerOverlay")).Visibility);
                window.Navigate("Calls");
                var callTrigger = (RadioButton)window.FindName("RailAgent"); callTrigger.Focus();
                window.Features.OpenCall(window.Features.Calls[0]); WpfContext.Drain();
                var drawer = (Grid)window.FindName("DrawerOverlay");
                Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(drawer));
                Assert.False(((FrameworkElement)window.FindName("CallsContent")).IsEnabled);
                Escape((UIElement)window.FindName("DrawerTitle")); WpfContext.Drain();
                Assert.Equal(Visibility.Collapsed, drawer.Visibility); Assert.Same(callTrigger, Keyboard.FocusedElement);
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
            WpfContext.ShowWindow(dialog.Window); WpfContext.Drain();
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
            WpfContext.ShowWindow(dialog.Window); WpfContext.Drain();
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
                WpfContext.ShowWindow(window); window.ControlGuard.UiChanged();
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
                window.Width = 1024; window.Height = 640; WpfContext.ShowWindow(window);
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

    private static readonly string[] Pages = ["Approvals", "Mcp", "Engineering", "Blocks", "VersionControl", "Calls", "Audit", "Environment", "Log", "Settings"];
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
        string previousTitle = window.CurrentPageTitle.Text, previousPage = window.CurrentPage;
        window.Navigate(page); WpfContext.Drain(); window.UpdateLayout();
        if (page == "Settings")
        {
            Assert.True(window.SettingsOpen);
            Assert.Equal(previousPage, window.CurrentPage);
            Assert.Equal(previousTitle, window.CurrentPageTitle.Text);
            return;
        }
        string title = page switch
        {
            "Approvals" => "Shell.Approvals",
            "Mcp" => "Desktop.Configuration", "Engineering" => "Shell.Engineering", "Blocks" => "Shell.Engineering",
            "VersionControl" => "Shell.Engineering", "Calls" => "Shell.Approvals", "Audit" => "Shell.Audit",
            "Environment" => "Shell.Environment", _ => "Shell.Logs",
        };
        var expected = Loc.Current[title];
        if (page is "Engineering" or "Blocks" or "VersionControl") expected += " · Conveyor_Line_04.ap14";
        Assert.Equal(expected, ((TextBlock)window.FindName("CurrentPageTitle")).Text);
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
