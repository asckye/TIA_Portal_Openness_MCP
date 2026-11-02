using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TiaMcpConfigurator;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Themes;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Gui.Services.Stubs;
using Xunit;

namespace TiaOpenness.Gui.Tests;

public sealed class WorkbenchRenderFactAttribute : FactAttribute
{
    public WorkbenchRenderFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR")))
            Skip = "Set TIA_WORKBENCH_RENDER_DIR to render the offline workbench states.";
    }
}

[Collection(WpfCollection.Name)]
public sealed class WorkbenchRenderTests(WpfContext wpf)
{
    [WorkbenchRenderFact]
    public void Render_all_handoff_states_without_an_engine()
    {
        var output = Path.GetFullPath(Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR")!);
        Directory.CreateDirectory(output);
        foreach (var state in States())
        {
            wpf.RunWithLanguage(state.Language, () =>
            {
                using var trace = new BindingPathTests.BindingTrace();
                var previous = ThemeManager.Current.Theme;
                ThemeManager.Current.Theme = state.Theme;
                var preview = new ConfigurationPreview("14sp1", @"C:\Program Files\Siemens\Automation\Portal V14",
                    "192.168.86.131", state.Local, state.Running, state.Running, state.Running ? "render-fixture-secret" : "",
                    "20:50:27   Client scan · 1 detected, 11 not detected\n20:50:27   Ready · configure both sides on this page\n20:50:27   Loaded V14sp1 service config\n20:50:27   Update check · 3.3.0 is latest (api)\n", Clients());
                var approvals = FeaturePageFixtures.Approvals(state.Pending);
                approvals.Enabled = !state.Pending;
                var window = new MainWindow(new MainViewModel(new FakeProjectClient(), new FakeDialogService()), false, approvals: approvals, preview: preview);
                try
                {
                    // Measure the MCP page before navigating to each shell page for the final capture.
                    window.ShowConfiguration(false);
                    ((Expander)window.Configuration!.FindName("ConfigurationActivity")).IsExpanded = true;
                    var host = CreateHost(window);
                    Assert.Equal(!state.Local, ((RadioButton)window.Configuration!.FindName("RemoteNav")).IsChecked);
                    Assert.Equal(state.Local, ((RadioButton)window.Configuration.FindName("LocalNav")).IsChecked);
                    Assert.False(window.Configuration.HasRunningServer);
                    var installationState = (ContentControl)window.Configuration.FindName("DetectionChip");
                    var serviceState = (ContentControl)window.Configuration.FindName("ServiceStateChip");
                    Assert.True(installationState.TranslatePoint(new Point(), host).Y < serviceState.TranslatePoint(new Point(), host).Y);
                    var start = (Button)window.Configuration.FindName(state.Running ? "StopServer" : "StartServer");
                    Assert.True(serviceState.TranslatePoint(new Point(), host).Y + serviceState.ActualHeight < start.TranslatePoint(new Point(), host).Y);
                    foreach (var chip in new[] { installationState, serviceState })
                    {
                        var dot = WorkbenchRenderFeaturePagesTests.Descendants<System.Windows.Shapes.Ellipse>(chip).Single();
                        Assert.Equal(7, dot.ActualWidth); Assert.Equal(7, dot.ActualHeight);
                        var label = (TextBlock)chip.Content;
                        Assert.Equal(12, label.FontSize);
                        Assert.Equal(FontWeights.Normal, label.FontWeight);
                    }
                    foreach (string name in new[] { "LinkClient", "LinkServer", "LinkEndpoint", "Transport" })
                    {
                        var value = (TextBlock)window.Configuration.FindName(name);
                        Assert.Equal(FontWeights.Normal, value.FontWeight);
                        Assert.Equal(TextAlignment.Right, value.TextAlignment);
                    }
                    Assert.Equal(window.Features.Calls.Count == 0 ? Visibility.Collapsed : Visibility.Visible, ((Border)window.FindName("CallsCountBadge")).Visibility);
                    if (state.Page == "Mcp") Assert.Equal(360, ((FrameworkElement)window.Configuration.FindName("ClientPane")).ActualWidth);
                    string serviceStatus = state.Local ? Loc.Current["Pages.ServiceOnDemand"] : Loc.Current[state.Running ? "Config.Running" : "Config.Idle"];
                    Assert.Equal(Loc.Current.T("Shell.McpStatus", serviceStatus), ((TextBlock)window.FindName("McpStatusText")).Text);
                    var release = TiaMcp.Versioning.TiaVersionCatalog.Get(((MainViewModel)window.DataContext).SelectedReleaseKey).DisplayName;
                    Assert.Equal(Loc.Current.T("Shell.ProjectStatus", release, Loc.Current["Blocks.Empty.Title"]), ((TextBlock)window.FindName("SessionStatus")).Text);
                    Assert.Equal(state.Pending ? Visibility.Visible : Visibility.Collapsed, ((FrameworkElement)window.FindName("PendingBadge")).Visibility);
                    Assert.Equal(state.Pending ? Visibility.Visible : Visibility.Collapsed, ((FrameworkElement)window.FindName("ApprovalOffPill")).Visibility);
                    if (state.Name == "mcp-zh-light")
                    {
                        var metrics = new List<string>();
                        metrics.Add($"ClientPane: {((FrameworkElement)window.Configuration.FindName("ClientPane")).ActualWidth}");
                        foreach (string name in new[] { "RailMcp", "RailEngineering", "RailCalls", "RailAudit", "RailEnvironment", "RailLog" })
                        {
                            var element = (FrameworkElement)window.FindName(name);
                            var point = element.TransformToAncestor(host).Transform(new Point());
                            Assert.Equal(9, point.X);
                            Assert.Equal(36, element.ActualHeight);
                            var item = (RadioButton)element;
                            var icon = (Border)item.Template.FindName("GlyphBox", item);
                            Assert.Equal(18, icon.ActualWidth);
                            Assert.Equal(18, icon.ActualHeight);
                            metrics.Add($"{name}: {point.X},{point.Y} {element.ActualWidth}x{element.ActualHeight}");
                        }
                        foreach (string name in new[] { "MinimizeWindowButton", "MaximizeWindowButton", "CloseWindowButton" })
                            Assert.Equal(28, ((FrameworkElement)window.FindName(name)).ActualWidth);
                        var log = (TiaOpenness.Gui.Controls.WorkbenchLogView)window.Configuration.FindName("ActivityLog");
                        log.BringIntoView(); WpfContext.Drain(); host.UpdateLayout(); WpfContext.Drain();
                        metrics.Add($"Log: {log.ActualWidth}, padding {log.Padding}, document padding {log.Document.PagePadding}, page width {log.Document.PageWidth}");
                        Assert.Equal(new Thickness(0), log.Document.PagePadding);
                        var table = (System.Windows.Documents.Table)log.Document.Blocks.FirstBlock;
                        var messageCell = table.RowGroups[0].Rows[0].Cells[1];
                        var message = messageCell.Blocks.FirstBlock;
                        Assert.Equal(90, table.Columns[0].Width.Value);
                        Assert.Equal(12, table.RowGroups[0].Rows[0].Cells[0].Padding.Right);
                        double textHeight = message.ContentEnd.GetCharacterRect(LogicalDirection.Backward).Bottom
                            - message.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
                        const int expectedMessageLines = 2;
                        Assert.Equal(12, log.FontSize);
                        Assert.Equal(17, message.LineHeight);
                        Assert.Equal(new Thickness(0, 9, 14, 9), messageCell.Padding);
                        Assert.Equal(new Thickness(0), message.Margin);
                        int renderedLines = 0;
                        for (TextPointer? line = message.ContentStart.GetLineStartPosition(0);
                            line != null && line.CompareTo(message.ContentEnd) < 0;)
                        {
                            Assert.True(renderedLines <= expectedMessageLines, "Line traversal must terminate within the expected layout.");
                            renderedLines++;
                            var next = line.GetLineStartPosition(1);
                            Assert.True(next == null || next.CompareTo(line) > 0, "Formatted line traversal must advance.");
                            line = next;
                        }
                        Assert.Equal(expectedMessageLines, renderedLines);
                        // Character rectangles exclude the 9 + 9 epx cell padding from the two-line row height.
                        double verticalPadding = messageCell.Padding.Top + messageCell.Padding.Bottom;
                        double maximumRowHeight = message.LineHeight * expectedMessageLines + verticalPadding;
                        double maximumTextHeight = maximumRowHeight - verticalPadding;
                        metrics.Add($"First log message height: {textHeight}, line height: {message.LineHeight}, lines: {renderedLines}, font size: {message.FontSize}, cell padding: {messageCell.Padding}");
                        metrics.Add($"Log height bounds: text {maximumTextHeight}, row {maximumRowHeight}; padded measured height: {textHeight + verticalPadding}");
                        SaveRender(host, Path.Combine(output, "mcp-log-height-inspection.png"));
                        File.WriteAllLines(Path.Combine(output, "log-height-inspection.txt"), metrics);
                        Assert.InRange(textHeight, 10, maximumTextHeight);
                        Assert.InRange(textHeight + verticalPadding, 10 + verticalPadding, maximumRowHeight);
                        metrics.Add($"First log message height: {textHeight}");
                        foreach (string name in new[] { "LinkClient", "LinkServer", "LinkEndpoint", "Transport" })
                        {
                            var row = (FrameworkElement)window.Configuration.FindName(name);
                            var point = row.TransformToAncestor(host).Transform(new Point(0, row.ActualHeight / 2));
                            metrics.Add($"{name} center: {point.Y}");
                        }
                        File.WriteAllLines(Path.Combine(output, "layout.txt"), metrics);
                    }
                    window.Navigate(state.Page); WpfContext.Drain(); host.UpdateLayout(); WpfContext.Drain(); host.UpdateLayout();
                    if (state.Page is "Approvals" or "Settings")
                    {
                        Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName(state.Page == "Approvals" ? "ApprovalsContent" : "SettingsContent")).Visibility);
                        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("DrawerOverlay")).Visibility);
                    }
                    if (state.Page == "Engineering") Assert.False(((MainViewModel)window.DataContext).Session.IsConnected);
                    SaveRender(host, Path.Combine(output, state.Name + ".png"));
                }
                finally { window.Close(); ThemeManager.Current.Theme = previous; }
                Assert.True(string.IsNullOrWhiteSpace(trace.Text), trace.Text);
            });
        }
    }

    [WorkbenchRenderFact]
    public void Sider_collapses_to_its_icons_below_the_lg_breakpoint()
    {
        var output = Path.GetFullPath(Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR")!);
        Directory.CreateDirectory(output);
        foreach (var language in new[] { AppLanguage.Chinese, AppLanguage.English })
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                wpf.RunWithLanguage(language, () =>
                {
                    using var trace = new BindingPathTests.BindingTrace();
                    var previous = ThemeManager.Current.Theme;
                    ThemeManager.Current.Theme = theme;
                    var window = new MainWindow(new MainViewModel(new FakeProjectClient(), new FakeDialogService()), false);
                    try
                    {
                        window.Navigate("Calls");
                        var host = CreateHost(window, 960, 700);
                        var sider = (Border)window.FindName("Sider");
                        Assert.Equal(64, sider.ActualWidth);
                        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("BrandTitle")).Visibility);
                        var toggle = (Button)window.FindName("SiderToggle");
                        Assert.Equal(Loc.Current["Shell.ExpandMenu"], System.Windows.Automation.AutomationProperties.GetName(toggle));
                        foreach (string name in new[] { "RailEngineering", "RailCalls", "RailAudit", "RailEnvironment", "RailLog", "RailMcp", "SettingsButton" })
                        {
                            var item = (FrameworkElement)window.FindName(name);
                            var origin = item.TransformToAncestor(host).Transform(new Point());
                            Assert.True(origin.X >= 0 && origin.X + item.ActualWidth <= sider.ActualWidth, name + " stays inside the collapsed sider");
                            Assert.True(item.ActualHeight >= 24, name + " keeps the target size");
                            Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(item)), name + " keeps its name");
                        }
                        var title = (FrameworkElement)window.FindName("CurrentPageTitle");
                        Assert.True(title.TransformToAncestor(host).Transform(new Point()).X > sider.ActualWidth);
                        string suffix = (language == AppLanguage.Chinese ? "zh" : "en") + "-" + theme.ToString().ToLowerInvariant();
                        SaveRender(host, Path.Combine(output, "sider-collapsed-" + suffix + ".png"), 960, 700);
                        // The trigger expands it below the breakpoint, and the label returns.
                        UnifiedDesktopTests.ClickControl(toggle); host.UpdateLayout(); WpfContext.Drain(); host.UpdateLayout();
                        Assert.Equal(232, sider.ActualWidth);
                        Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("BrandTitle")).Visibility);
                        SaveRender(host, Path.Combine(output, "sider-expanded-" + suffix + ".png"), 960, 700);
                        Resize(1200); Assert.Equal(232, sider.ActualWidth);
                        UnifiedDesktopTests.ClickControl(toggle); host.UpdateLayout();
                        Assert.Equal(64, sider.ActualWidth);
                        Resize(1199); Assert.Equal(64, sider.ActualWidth);
                        Resize(1200); Assert.Equal(232, sider.ActualWidth);
                        void Resize(double width)
                        {
                            host.Width = width; host.Measure(new Size(width, 700)); host.Arrange(new Rect(0, 0, width, 700));
                            host.UpdateLayout(); WpfContext.Drain(); host.UpdateLayout();
                        }

                    }
                    finally { window.Close(); ThemeManager.Current.Theme = previous; }
                    Assert.True(string.IsNullOrWhiteSpace(trace.Text), trace.Text);
                });
    }

    [Fact]
    public void Render_shared_themes_in_both_languages()
    {
        string? render = Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR");
        string? output = string.IsNullOrWhiteSpace(render) ? null : Path.Combine(Path.GetFullPath(render), "shared-themes");
        if (output != null) Directory.CreateDirectory(output);
        foreach (var language in new[] { AppLanguage.Chinese, AppLanguage.English })
            wpf.RunWithLanguage(language, () =>
            {
                using var trace = new BindingPathTests.BindingTrace();
                var previous = ThemeManager.Current.Theme;
                var previousContrast = ThemeManager.Current.HighContrastOverride;
                ThemeManager.Current.HighContrastOverride = false;
                ThemeManager.Current.Theme = AppTheme.Light;
                var window = new MainWindow(ProjectPageTestSupport.Model(results: true, release: "21"), false,
                    FeaturePageFixtures.Approvals(), new FeaturePageFixtures.Diagnostics());
                try
                {
                    window.ShowConfiguration(false);
                    window.Navigate("Engineering");
                    var host = CreateHost(window, 1366, 860);
                    foreach (string theme in new[] { "Light", "Dark", "HighContrast" })
                    {
                        ThemeManager.Current.HighContrastOverride = theme == "HighContrast";
                        ThemeManager.Current.Theme = theme == "Dark" ? AppTheme.Dark : AppTheme.Light;
                        ApplyCurrentPalette(host);
                        WpfContext.Drain(); host.UpdateLayout();
                        Assert.Same(Application.Current.FindResource("Primer.bg"), ((Grid)window.FindName("Root")).Background);
                        double rowHeight = Assert.IsType<double>(Application.Current.FindResource("Ui.RowHeight"));
                        Assert.Equal(36, rowHeight);
                        foreach (string name in new[] { "RailMcp", "RailEngineering", "RailCalls", "RailAudit", "RailEnvironment", "RailLog" })
                        {
                            var element = (FrameworkElement)window.FindName(name);
                            Assert.Equal(rowHeight, element.ActualHeight);
                        }
                        string suffix = (language == AppLanguage.Chinese ? "zh" : "en") + "-" + theme.ToLowerInvariant();
                        if (output != null) SaveRender(host, Path.Combine(output, suffix + ".png"), 1366, 860);
                        Assert.True(string.IsNullOrWhiteSpace(trace.Text), trace.Text);
                    }
                }
                finally
                {
                    window.Close();
                    ThemeManager.Current.HighContrastOverride = previousContrast;
                    ThemeManager.Current.Theme = previous;
                }
            });
    }

    private static System.Collections.Generic.IEnumerable<RenderState> States()
    {
        foreach (var language in new[] { AppLanguage.Chinese, AppLanguage.English })
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                string suffix = (language == AppLanguage.Chinese ? "zh" : "en") + "-" + theme.ToString().ToLowerInvariant();
                yield return new("mcp-" + suffix, language, theme);
                yield return new("mcp-local-" + suffix, language, theme, Local: true);
                yield return new("mcp-running-" + suffix, language, theme, Running: true);
                yield return new("settings-" + suffix, language, theme, Page: "Settings");
                yield return new("approval-badges-" + suffix, language, theme, Pending: true);
                yield return new("approvals-empty-" + suffix, language, theme, Page: "Approvals");
            }
    }

    [Fact]
    public void Shell_pages_status_lines_and_settings_render_at_all_handoff_sizes()
    {
        string? render = Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR");
        string? output = string.IsNullOrWhiteSpace(render) ? null : Path.Combine(Path.GetFullPath(render), "shell");
        if (output != null) Directory.CreateDirectory(output);
        foreach (var language in new[] { AppLanguage.English, AppLanguage.Chinese })
        foreach (var size in new[] { (Width: 1024, Height: 640), (Width: 1200, Height: 780), (Width: 1366, Height: 860) })
        foreach (string theme in new[] { "Light", "Dark", "HighContrast" })
            wpf.RunWithLanguage(language, () =>
            {
                using var trace = new BindingPathTests.BindingTrace();
                var previousTheme = ThemeManager.Current.Theme;
                var previousContrast = ThemeManager.Current.HighContrastOverride;
                ThemeManager.Current.HighContrastOverride = theme == "HighContrast";
                ThemeManager.Current.Theme = theme == "Dark" ? AppTheme.Dark : AppTheme.Light;
                var window = new MainWindow(ProjectPageTestSupport.Model(release: "21"), false,
                    FeaturePageFixtures.Approvals(), new FeaturePageFixtures.Diagnostics());
                try
                {
                    window.ConfigureFeaturePages(new FeaturePageFixtures.Journal(), new FeaturePageFixtures.Audit(),
                        new FeaturePageFixtures.Environment(), new FeaturePageFixtures.Notification(), () => FeaturePageFixtures.Now);
                    var host = CreateHost(window, size.Width, size.Height);
                    var sider = (Border)window.FindName("Sider");
                    Assert.Equal(size.Width < 1200 ? 64 : 232, sider.ActualWidth);
                    foreach (string name in new[] { "RailApprovals", "RailEngineering", "RailVersionControl", "RailCalls", "RailAudit", "RailEnvironment", "RailLog", "RailMcp", "SettingsButton" })
                    {
                        var item = (RadioButton)window.FindName(name);
                        Assert.Equal(36, item.ActualHeight);
                        var surface = (Border)item.Template.FindName("Surface", item);
                        if (item.IsChecked != true) Assert.Equal(0, ((SolidColorBrush)surface.Background).Color.A);
                        else
                        {
                            var bar = (Border)item.Template.FindName("Bar", item);
                            Assert.Equal(3, bar.Width); Assert.Equal(Visibility.Visible, bar.Visibility);
                            Assert.Equal(FontWeights.SemiBold, item.FontWeight);
                            if (theme == "HighContrast") Assert.Equal(SystemColors.HighlightTextColor, ((SolidColorBrush)bar.Background).Color);
                        }
                        Assert.Equal(System.Windows.Automation.AutomationProperties.GetName(item), item.ToolTip);
                        var label = (ContentPresenter)item.Template.FindName("Label", item);
                        if (size.Width < 1200) Assert.Equal(Visibility.Collapsed, label.Visibility);
                        else
                        {
                            foreach (var text in DesktopCapture.Descendants<TextBlock>(label))
                            {
                                Assert.Equal(TextWrapping.NoWrap, text.TextWrapping);
                                var probe = new TextBlock { Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize, FontWeight = text.FontWeight };
                                probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                                Assert.True(probe.DesiredSize.Width <= text.ActualWidth + 1, $"{language}: '{text.Text}' is truncated");
                            }
                        }
                    }
                    foreach (string name in new[] { "McpStatusButton", "SessionStatusButton", "ApprovalStatusButton" })
                    {
                        var button = (Button)window.FindName(name);
                        string fullText = System.Windows.Automation.AutomationProperties.GetName(button);
                        Assert.False(string.IsNullOrWhiteSpace(fullText));
                        Assert.Equal(fullText, button.ToolTip);
                        var text = DesktopCapture.Descendants<TextBlock>(button).Single();
                        Assert.Equal(TextWrapping.NoWrap, text.TextWrapping);
                        Assert.Equal(TextTrimming.CharacterEllipsis, text.TextTrimming);
                        Assert.Equal(size.Width < 1200 ? Visibility.Collapsed : Visibility.Visible, text.Visibility);
                        var dot = DesktopCapture.Descendants<System.Windows.Shapes.Ellipse>(button).Single();
                        Assert.Equal(fullText, System.Windows.Automation.AutomationProperties.GetName(dot));
                        Assert.Equal(fullText, dot.ToolTip);
                        var origin = button.TransformToAncestor(host).Transform(new Point());
                        Assert.InRange(origin.Y + button.ActualHeight, 0, size.Height);
                    }
                    foreach (string page in new[] { "Engineering", "Approvals", "Settings" })
                    {
                        window.Navigate(page); Settle();
                        var title = (TextBlock)window.FindName("CurrentPageTitle");
                        Assert.Equal(28, title.FontSize); Assert.Equal(FontWeights.Bold, title.FontWeight);
                        Assert.Equal(title.Text + " – " + Loc.Current["App.Title"], window.Title);
                        Save(page);
                    }
                    var settings = (Views.SettingsView)window.FindName("SettingsContent");
                    foreach (string section in new[] { "Service", "AI" }) { settings.ShowSection(section); Settle(); Save("Settings-" + section); }
                    UnifiedDesktopTests.ClickControl((RadioButton)settings.FindName("ApprovalOff")); Settle(); Save("ApprovalOffSheet");
                    UnifiedDesktopTests.ClickControl((Button)window.FindName("CancelApprovalOff"));
                    Assert.True(string.IsNullOrWhiteSpace(trace.Text), trace.Text);
                    void Settle() { WpfContext.Drain(); host.UpdateLayout(); WpfContext.Drain(); host.UpdateLayout(); }
                    void Save(string page)
                    {
                        if (output != null) SaveRender(host, Path.Combine(output, $"{page}-{language}-{theme}-{size.Width}x{size.Height}.png"), size.Width, size.Height);
                    }
                }
                finally { window.Close(); ThemeManager.Current.HighContrastOverride = previousContrast; ThemeManager.Current.Theme = previousTheme; }
            });
    }

    internal static Border CreateHost(MainWindow window) => CreateHost(window, 1200, 780);

    // Detached render hosts are outside Application.Windows. Refresh their resources when reusing them across themes.
    internal static void ApplyCurrentPalette(FrameworkElement host)
    {
        var palettes = host.Resources.MergedDictionaries;
        var palette = Application.Current.Resources.MergedDictionaries[0];
        if (palettes.Count == 0) palettes.Add(palette);
        else palettes[0] = palette;
    }

    internal static Border CreateHost(MainWindow window, double width, double height)
    {
        WpfContext.Drain();
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var host = new Border { Child = content, DataContext = window.DataContext, Width = width, Height = height };
        NameScope.SetNameScope(host, NameScope.GetNameScope(window));
        foreach (System.Windows.Input.CommandBinding binding in window.CommandBindings) host.CommandBindings.Add(binding);
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        WpfContext.Drain();
        host.UpdateLayout();
        WpfContext.Drain();
        return host;
    }

    internal static void SaveRender(FrameworkElement host, string path) => SaveRender(host, path, 1200, 780);

    internal static void SaveRender(FrameworkElement host, string path, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed record RenderState(string Name, AppLanguage Language, AppTheme Theme,
        bool Local = false, bool Running = false, bool Pending = false, string Page = "Mcp");

    private static List<ClientProfile> Clients()
    {
        string[] names = ["Claude Code", "Codex", "Gemini CLI", "Qwen", "Kimi", "Yuanbao", "DeepSeek", "GLM", "Grok", "Qwen Agent", "Cursor", "VS Code · Copilot"];
        return names.Select((name, i) => new ClientProfile("fixture-" + i, name,
            i == 0 ? @"~\.claude.json" : @"C:\fixture\" + i + ".json", "render fixture") { Detected = i == 0 }).ToList();
    }
}
