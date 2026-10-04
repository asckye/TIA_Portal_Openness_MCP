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
using TiaOpenness.Gui.Themes;
using TiaOpenness.Gui.ViewModels;
using Xunit;

namespace TiaOpenness.Gui.Tests;

public sealed partial class UnifiedDesktopTests
{
    internal static void ClickMenu(MenuItem item)
    {
        typeof(MenuItem).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(item, null);
        FlushMenu();
    }

    private static void FlushMenu() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    internal static void AssertPage(MainWindow window, bool configuration)
    {
        FlushMenu();
        var model = (MainViewModel)window.DataContext;
        Assert.Equal(configuration, model.IsConfigurationPage);
        Assert.Equal(!configuration, model.IsEngineeringPage);
        Assert.Equal(configuration, ((MenuItem)window.FindName("ConfigurationMenu")).IsChecked);
        Assert.Equal(!configuration, ((MenuItem)window.FindName("EngineeringMenu")).IsChecked);
        Assert.Equal(configuration ? Visibility.Visible : Visibility.Collapsed, ((FrameworkElement)window.FindName("ConfigurationHost")).Visibility);
        Assert.Equal(configuration ? Visibility.Collapsed : Visibility.Visible, ((FrameworkElement)window.FindName("EngineeringWorkspace")).Visibility);
        var label = (TextBlock)window.FindName("CaptionPage");
        Assert.Equal("· " + Loc.Current[configuration ? "Desktop.Configuration" : "Desktop.Engineering"], label.Text);
        Assert.Same(Application.Current.FindResource("Ui.SecondaryLabel"), label.Foreground);
        Assert.Equal(Loc.Current["App.Title"], window.Title);
    }

    [Theory]
    [InlineData(AppLanguage.English, AppTheme.Light)]
    [InlineData(AppLanguage.English, AppTheme.Dark)]
    [InlineData(AppLanguage.Chinese, AppTheme.Light)]
    [InlineData(AppLanguage.Chinese, AppTheme.Dark)]
    public void Caption_tracks_page_and_language_and_keeps_free_space_draggable(AppLanguage language, AppTheme theme)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var previous = ThemeManager.Current.Theme;
            ThemeManager.Current.Theme = theme;
            var window = new MainWindow(new MainViewModel(new FakeStudioClient(), new FakeDialogService()), false) { ShowInTaskbar = false };
            try
            {
                window.Show(); FlushMenu();
                var root = (FrameworkElement)window.Content;
                var caption = (Grid)window.FindName("CaptionBar");
                Assert.Null(window.FindName("EngineeringTab"));
                Assert.Null(window.FindName("ConfigurationTab"));
                Assert.Empty(DesktopCapture.Descendants<RadioButton>(caption));
                Assert.Equal(4, DesktopCapture.Descendants<Button>(caption).Count());
                Assert.Equal("MCP · " + Loc.Current["Config.Idle"], ((TextBlock)window.FindName("McpStatusText")).Text);
                Assert.Same(WorkbenchCommands.Configuration, ((Button)window.FindName("McpStatus")).Command);
                Assert.Same(window.FindName("ReleasePicker"), Assert.Single(DesktopCapture.Descendants<ComboBox>(caption)));
                foreach (var configuration in new[] { false, true, false })
                {
                    if (configuration) window.ShowConfiguration(false); else window.ShowEngineering();
                    foreach (var current in new[] { language, language == AppLanguage.English ? AppLanguage.Chinese : AppLanguage.English })
                    {
                        Loc.Current.Language = current;
                        AssertPage(window, configuration);
                        var label = (TextBlock)window.FindName("CaptionPage");
                        Assert.False(label.IsHitTestVisible);
                        Assert.True(label.ActualWidth + label.Margin.Left + label.Margin.Right >= label.DesiredSize.Width);
                        var left = (FrameworkElement)caption.Children[0];
                        var right = (FrameworkElement)caption.Children[1];
                        double gapStart = left.TranslatePoint(new Point(left.ActualWidth, 20), root).X;
                        double gapEnd = right.TranslatePoint(new Point(0, 20), root).X;
                        Assert.True(gapEnd - gapStart > 80, "The caption must retain space to drag at minimum window width.");
                        Assert.Equal(root.ActualWidth - 8, right.TranslatePoint(new Point(right.ActualWidth, 0), root).X, 1);
                        var hit = Assert.IsAssignableFrom<DependencyObject>(root.InputHitTest(new Point((gapStart + gapEnd) / 2, 20)));
                        for (var element = hit; element != null; element = VisualTreeHelper.GetParent(element))
                            if (element is IInputElement input) Assert.False(WindowChrome.GetIsHitTestVisibleInChrome(input));
                    }
                }
            }
            finally { window.Close(); ThemeManager.Current.Theme = previous; }
        });
    }

    private static IEnumerable<MenuItem> MenuItems(ItemsControl parent)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var child in MenuItems(item)) yield return child;
        }
    }

    [Theory]
    [InlineData(AppLanguage.English, AppTheme.Light)]
    [InlineData(AppLanguage.English, AppTheme.Dark)]
    [InlineData(AppLanguage.Chinese, AppTheme.Light)]
    [InlineData(AppLanguage.Chinese, AppTheme.Dark)]
    public void One_menu_navigates_and_localizes_the_workbench_without_binding_errors(AppLanguage language, AppTheme theme)
    {
        wpf.RunWithLanguage(language, () =>
        {
            using var trace = new BindingPathTests.BindingTrace();
            var previous = ThemeManager.Current.Theme;
            ThemeManager.Current.Theme = theme;
            var window = new MainWindow(new MainViewModel(new FakeStudioClient(), new FakeDialogService()), false) { ShowActivated = true, ShowInTaskbar = false };
            MenuItem Item(string name) => (MenuItem)window.FindName(name);
            try
            {
                window.Show(); window.Activate(); FlushMenu();
                var root = (FrameworkElement)window.Content;
                var menu = Assert.Single(DesktopCapture.Descendants<Menu>(root));
                Assert.Equal(new[] { "Project", "View", "Plc", "Mcp", "Tools", "Help" }.Select(key => Loc.Current["Menu." + key]), menu.Items.OfType<MenuItem>().Select(item => item.Header));
                Assert.True(WindowChrome.GetIsHitTestVisibleInChrome(menu));
                Assert.False(WindowChrome.GetIsHitTestVisibleInChrome(root));
                Assert.True(menu.IsMainMenu);
                Assert.Equal(new[] { 'P', 'V', 'L', 'M', 'T', 'H' }, DesktopCapture.Descendants<AccessText>(menu).Select(text => char.ToUpperInvariant(text.AccessKey)));
                Assert.All(MenuItems(menu), item => Assert.False(string.IsNullOrWhiteSpace(item.Header?.ToString())));
                Assert.All(MenuItems(menu).Where(item => BindingOperations.IsDataBound(item, MenuItem.CommandProperty)), item => Assert.NotNull(item.Command));
                var model = (MainViewModel)window.DataContext;
                Assert.Same(model.Engineering.Save, Item("SaveMenu").Command);
                OpenMenu(Item("ProjectMenu"));
                Assert.Equal(model.Engineering.Save.CanExecute(null), Item("SaveMenu").IsEnabled);
                CloseMenu(Item("ProjectMenu"));
                model.Session.ProjectPath = @"D:\fixture\Menu.ap21";
                model.Session.OpenProject.Execute(null); FlushMenu();
                Assert.True(Item("SaveMenu").IsEnabled);
                var selectedDevice = model.Engineering.SelectedDevice;
                model.Engineering.SelectedDevice = null; FlushMenu();
                Assert.False(Item("SaveMenu").IsEnabled);
                model.Engineering.SelectedDevice = selectedDevice; FlushMenu();
                Assert.True(Item("SaveMenu").IsEnabled);
                Assert.Equal(new[] { Key.O, Key.S, Key.B, Key.D1, Key.D2 }, window.InputBindings.OfType<KeyBinding>().Select(binding => binding.Key));
                Assert.Same(model.Engineering.Compile, window.InputBindings.OfType<KeyBinding>().Single(binding => binding.Key == Key.B).Command);
                Assert.Same(model.Engineering.Compile, Item("CompileMenu").Command);
                // Project lifecycle, PLC program operations and preferences each live in one menu (Windows / TIA Portal convention).
                string[] Headers(string name) => Item(name).Items.OfType<MenuItem>().Select(item => (string)item.Header).ToArray();
                Assert.Equal(new[] { "Menu.Open", "Menu.Save", "Menu.Exit" }.Select(key => Loc.Current[key]), Headers("ProjectMenu"));
                Assert.Equal(new[] { "Menu.Software", "Menu.Compile", "Menu.Inspect", "Menu.Transfer" }.Select(key => Loc.Current[key]), Headers("PlcMenu"));
                Assert.Equal(new[] { "Menu.Workspace", "Menu.Language", "Menu.Theme" }.Select(key => Loc.Current[key]), Headers("ToolsMenu"));
                Assert.Equal(new[] { "Menu.McpStart", "Menu.McpStop", "Menu.McpNetwork", "Menu.McpTest", "Menu.McpWrite", "Menu.McpPage" }.Select(key => Loc.Current[key]), Headers("McpMenu"));
                Assert.Same(WorkbenchCommands.Configuration, Item("McpPageItem").Command);
                Assert.Equal(5, Headers("ViewMenu").Length);
                foreach (var name in new[] { "ProjectMenu", "PlcMenu", "ToolsMenu" })
                {
                    OpenMenu(Item(name));
                    DesktopCapture.Save(root, $"menu-{name}-{language}-{theme}", PopupChild(Item(name)));
                    CloseMenu(Item(name));
                }
                var open = window.InputBindings.OfType<KeyBinding>().Single(binding => binding.Key == Key.O);
                Assert.Same(WorkbenchCommands.Open, open.Command);
                WorkbenchCommands.Open.Execute(null, window); FlushMenu();
                Assert.True(((FrameworkElement)window.FindName("OptionsOverlay")).IsVisible);
                Assert.True(((FrameworkElement)window.FindName("ProjectOptions")).IsVisible);
                typeof(MainWindow).GetMethod("OnCloseOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
                Assert.All(window.InputBindings.OfType<KeyBinding>(), binding => Assert.Equal(ModifierKeys.Control, binding.Modifiers));
                AssertPage(window, false);
                Assert.Null(window.Configuration);
                Assert.False(Item("RunUpdate").IsEnabled);
                OpenMenu(Item("HelpMenu"));
                var page = Assert.IsType<TiaMcpConfigurator.ConfigurationView>(window.Configuration);
                AssertPage(window, false);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("EngineeringWorkspace")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("ConfigurationHost")).Visibility);
                Assert.Null(page.FindName("MenuBar"));
                Assert.False(Item("RunUpdate").IsEnabled);
                OpenMenu(Item("UpdateMenu"));
                Assert.Equal(6, Item("UpdateMenu").Items.Count);
                DesktopCapture.Save(root, $"menu-update-{language}-{theme}", PopupChild(Item("HelpMenu")), PopupChild(Item("UpdateMenu")));
                CloseMenu(Item("UpdateMenu")); CloseMenu(Item("HelpMenu"));
                // The MCP menu runs the configuration page actions and offers each one exactly when its button does.
                void AssertServiceMenu(bool http)
                {
                    OpenMenu(Item("McpMenu"));
                    Assert.Equal(http && ((Button)page.FindName("StartServer")).IsEnabled, Item("McpStartItem").IsEnabled);
                    Assert.False(Item("McpStopItem").IsEnabled);
                    Assert.Equal(http, Item("McpNetworkItem").IsEnabled);
                    Assert.Equal(http, Item("McpTestItem").IsEnabled);
                    Assert.True(Item("McpWriteItem").IsEnabled);
                    Assert.Equal("MCP · " + Loc.Current[http ? "Config.Idle" : "Config.Local"], ((TextBlock)window.FindName("McpStatusText")).Text);
                    DesktopCapture.Save(root, $"menu-McpMenu-{(http ? "http" : "stdio")}-{language}-{theme}", PopupChild(Item("McpMenu")));
                    CloseMenu(Item("McpMenu"));
                }
                AssertServiceMenu(true);
                ((RadioButton)page.FindName("LocalNav")).IsChecked = true; FlushMenu();
                AssertServiceMenu(false);
                ((RadioButton)page.FindName("RemoteNav")).IsChecked = true; FlushMenu();
                AssertServiceMenu(true);
                OpenMenu(Item("ViewMenu"));
                Assert.True(Item("EngineeringMenu").IsChecked);
                Assert.Equal(language == AppLanguage.Chinese ? "工程操作(_E)" : "_Engineering", Item("EngineeringMenu").Header);
                Assert.Equal(language == AppLanguage.Chinese ? "MCP 与客户端(_C)" : "MCP & _clients", Item("ConfigurationMenu").Header);
                Assert.Equal('E', char.ToUpperInvariant(Assert.Single(DesktopCapture.Descendants<AccessText>(Item("EngineeringMenu"))).AccessKey));
                Assert.Equal('C', char.ToUpperInvariant(Assert.Single(DesktopCapture.Descendants<AccessText>(Item("ConfigurationMenu"))).AccessKey));
                DesktopCapture.Save(root, $"menu-view-{language}-{theme}", PopupChild(Item("ViewMenu")));
                CloseMenu(Item("ViewMenu"));
                // Invoke the real MenuItem click, including WPF's check-toggle and command execution.
                ClickMenu(Item("ConfigurationMenu"));
                AssertPage(window, true);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("ConfigurationHost")).Visibility);
                Assert.True(Item("ConfigurationMenu").IsChecked);
                ClickMenu(Item("ConfigurationMenu")); AssertPage(window, true);
                Assert.Same(page, window.Configuration);
                Assert.True(((Button)page.FindName("SaveBoth")).IsVisible);
                AssertCards(page, root, theme, "InstallationCard", "ServerPane", "ClientPane", "SummaryCard", "ConfigurationLogCard");
                DesktopCapture.Save(root, $"configuration-menu-{language}-{theme}");
                ((RadioButton)page.FindName("LocalNav")).IsChecked = true; FlushMenu();
                AssertCards(page, root, theme, "InstallationCard", "ServerPane", "ClientPane", "SummaryCard", "ConfigurationLogCard");
                DesktopCapture.Save(root, $"configuration-local-{language}-{theme}");
                ((RadioButton)page.FindName("RemoteNav")).IsChecked = true; FlushMenu();

                // Execute the routed commands assigned to Ctrl+1 / Ctrl+2 against the real window.
                foreach (var key in new[] { Key.D1, Key.D1, Key.D2, Key.D2 })
                {
                    var shortcut = window.InputBindings.OfType<KeyBinding>().Single(binding => binding.Key == key);
                    var command = Assert.IsType<RoutedUICommand>(shortcut.Command);
                    Assert.Same(key == Key.D1 ? WorkbenchCommands.Engineering : WorkbenchCommands.Configuration, command);
                    command.Execute(shortcut.CommandParameter, window);
                    AssertPage(window, key == Key.D2);
                }
                ClickMenu(Item("ChineseMenu")); AssertPage(window, true);
                ClickMenu(Item("EnglishMenu")); AssertPage(window, true);
                Loc.Current.Language = language; FlushMenu();
                OpenMenu(Item("ViewMenu"));
                DesktopCapture.Save(root, $"menu-view-configuration-{language}-{theme}", PopupChild(Item("ViewMenu")));
                CloseMenu(Item("ViewMenu"));
                ClickMenu(Item("EngineeringMenu")); AssertPage(window, false);
                ClickMenu(Item("EngineeringMenu")); AssertPage(window, false);

                foreach (var state in new[] { ("BlocksMenu", "blocks", new[] { "SidebarCard", "ProjectCard", "BlocksCard", "CompileCard", "InspectionCard", "BlocksLogCard" }),
                    ("VciMenu", "vci", new[] { "SidebarCard", "WorkspaceCard", "VciStatusCard", "SyncCard", "MappedObjectsCard", "DiffCard" }),
                    ("LogMenu", "log", new[] { "SidebarCard", "LogCard" }) })
                {
                    ClickMenu(Item(state.Item1));
                    Assert.True(Item(state.Item1).IsChecked);
                    Assert.True(Item("EngineeringMenu").IsChecked);
                    AssertPage(window, false);
                    AssertCards(window, root, theme, state.Item3);
                    DesktopCapture.Save(root, $"engineering-{state.Item2}-menu-{language}-{theme}");
                    ClickMenu(Item(state.Item1)); Assert.True(Item(state.Item1).IsChecked);
                }
                ClickMenu(Item("DarkThemeMenu")); Assert.Equal(AppTheme.Dark, ThemeManager.Current.Theme);
                ClickMenu(Item("LightThemeMenu")); Assert.Equal(AppTheme.Light, ThemeManager.Current.Theme);
                ClickMenu(Item("LightThemeMenu")); Assert.True(Item("LightThemeMenu").IsChecked);
                ClickMenu(Item("AutoThemeMenu")); Assert.Equal(AppTheme.Auto, ThemeManager.Current.Theme);
                ClickMenu(Item("ChineseMenu")); Assert.Equal(AppLanguage.Chinese, Loc.Current.Language);
                Assert.Equal("MCP · " + Loc.Current["Config.Idle"], ((TextBlock)window.FindName("McpStatusText")).Text);
                AssertPage(window, false);
                ClickMenu(Item("ChineseMenu")); Assert.True(Item("ChineseMenu").IsChecked);
                ClickMenu(Item("EnglishMenu")); Assert.Equal(AppLanguage.English, Loc.Current.Language);
                AssertPage(window, false);
                Assert.False(Item("ChineseMenu").IsChecked);
                Assert.Same(model, window.DataContext);
                Assert.Same(page, window.Configuration);
                foreach (var top in menu.Items.OfType<MenuItem>()) { OpenMenu(top); CloseMenu(top); }
            }
            finally { foreach (var item in RenderedMenus.Keys.ToArray()) CloseMenu(item); window.Close(); ThemeManager.Current.Theme = previous; FlushMenu(); }
            Assert.True(string.IsNullOrWhiteSpace(trace.Text), trace.Text);
        });
    }

    private static readonly Dictionary<MenuItem, Popup> RenderedMenus = new();

    private static void OpenMenu(MenuItem item)
    {
        item.ApplyTemplate();
        var original = (Popup)item.Template.FindName("PART_Popup", item);
        item.Focus(); item.IsSubmenuOpen = true; FlushMenu();
        if (item.IsSubmenuOpen && original.IsOpen) return;
        // Native mouse capture is denied in the sandbox, including for a stock WPF Menu.
        // Rehost the shipping popup child in a noncapturing popup for deterministic rendering.
        item.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, item));
        var child = original.Child;
        original.Child = null;
        var popup = new Popup { Child = child, DataContext = item.DataContext, PlacementTarget = item,
            Placement = item.Role == MenuItemRole.TopLevelHeader ? PlacementMode.Bottom : PlacementMode.Right,
            AllowsTransparency = true, StaysOpen = true, Focusable = false };
        RenderedMenus.Add(item, popup);
        popup.IsOpen = true;
        FlushMenu();
    }

    private static void CloseMenu(MenuItem item)
    {
        if (!RenderedMenus.Remove(item, out var popup)) { item.IsSubmenuOpen = false; FlushMenu(); return; }
        popup.IsOpen = false;
        var child = popup.Child;
        popup.Child = null;
        ((Popup)item.Template.FindName("PART_Popup", item)).Child = child;
        FlushMenu();
    }

    private static FrameworkElement PopupChild(MenuItem item)
    {
        var popup = RenderedMenus.TryGetValue(item, out var rendered) ? rendered : (Popup)item.Template.FindName("PART_Popup", item);
        Assert.True(popup.IsOpen);
        return Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
    }

    private static void AssertCards(FrameworkElement owner, FrameworkElement root, AppTheme theme, params string[] names)
        => DesktopCapture.AssertCards(root, theme, names.Select(name => Assert.IsType<Border>(owner.FindName(name))).ToArray());

    [Theory]
    [InlineData(AppLanguage.English, AppTheme.Light)]
    [InlineData(AppLanguage.English, AppTheme.Dark)]
    [InlineData(AppLanguage.Chinese, AppTheme.Light)]
    [InlineData(AppLanguage.Chinese, AppTheme.Dark)]
    public void Options_dialogs_dropdowns_and_tooltips_use_the_active_palette(AppLanguage language, AppTheme theme)
    {
        wpf.RunWithLanguage(language, () =>
        {
            using var trace = new BindingPathTests.BindingTrace();
            var previous = ThemeManager.Current.Theme;
            ThemeManager.Current.Theme = theme;
            var window = new MainWindow(new MainViewModel(new FakeStudioClient(), new FakeDialogService()), false) { ShowActivated = true, ShowInTaskbar = false };
            try
            {
                window.Show(); window.Activate(); FlushMenu();
                var root = (FrameworkElement)window.Content;
                var menu = (Menu)window.FindName("MenuBar");
                foreach (string action in new[] { "Open", "Transfer", "Inspect", "Workspace" })
                {
                    ClickMenu(MenuItems(menu).Single(item => Equals(item.Header, Loc.Current["Menu." + action])));
                    var overlay = (FrameworkElement)window.FindName("OptionsOverlay");
                    Assert.True(overlay.IsVisible);
                    AssertCards(window, root, theme, "OptionsPanel");
                    DesktopCapture.Save(root, $"options-{action}-{language}-{theme}");
                    typeof(MainWindow).GetMethod("OnCloseOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
                    Assert.False(overlay.IsVisible);
                }
                var picker = (ComboBox)window.FindName("ReleasePicker");
                picker.IsDropDownOpen = true; FlushMenu();
                var popup = (Popup)picker.Template.FindName("PART_Popup", picker);
                DesktopCapture.Save(root, $"dropdown-{language}-{theme}", (FrameworkElement)popup.Child);
                picker.IsDropDownOpen = false;
                var tip = new ToolTip { Content = Loc.Current["Connection.OpennessVersion"], PlacementTarget = picker, Placement = PlacementMode.Bottom, IsOpen = true };
                FlushMenu();
                tip.HorizontalOffset = picker.ActualWidth - tip.ActualWidth; FlushMenu();
                Assert.Same(Application.Current.FindResource("Ui.Label"), tip.Foreground);
                Assert.True(tip.IsOpen && tip.ActualWidth > 0 && tip.ActualHeight > 0);
                Assert.True(new Rect(root.RenderSize).Contains(new Rect(root.PointFromScreen(tip.PointToScreen(new Point())), tip.RenderSize)));
                DesktopCapture.Save(root, $"tooltip-{language}-{theme}", tip);
                tip.IsOpen = false;
                using var close = new DialogLifetime(new GlassMessageBox(Loc.Current["Config.Ready"], Loc.Current["Config.AboutCaption"], MessageBoxButton.YesNoCancel, MessageBoxImage.Information));
                var dialog = close.Window;
                dialog.Owner = window; dialog.Show(); FlushMenu();
                var card = (Border)dialog.Content;
                DesktopCapture.AssertCards(card, theme, card);
                DesktopCapture.Save(card, $"dialog-{language}-{theme}");
                Assert.Equal(3, DesktopCapture.Descendants<Button>(card).Count());
            }
            finally { foreach (var item in RenderedMenus.Keys.ToArray()) CloseMenu(item); window.Close(); ThemeManager.Current.Theme = previous; FlushMenu(); }
            Assert.True(string.IsNullOrWhiteSpace(trace.Text), trace.Text);
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
            using var lifetime = new DialogLifetime(new GlassMessageBox("fixture", "fixture", buttons, MessageBoxImage.Warning));
            var dialog = (GlassMessageBox)lifetime.Window;
            dialog.Show(); FlushMenu();
            var choice = DesktopCapture.Descendants<Button>((FrameworkElement)dialog.Content).ElementAt(index);
            typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(choice, null);
            Assert.Equal(expected, dialog.Result);
            Assert.False(dialog.IsVisible);
        });
    }
}
