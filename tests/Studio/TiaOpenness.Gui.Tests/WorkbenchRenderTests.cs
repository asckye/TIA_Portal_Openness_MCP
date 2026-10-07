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
                var window = new MainWindow(new MainViewModel(new FakeStudioClient(), new FakeDialogService()), false, approvals: approvals, preview: preview);
                try
                {
                    window.ShowConfiguration(false);
                    window.Navigate(state.Page);
                    if (state.Drawer == "Settings") window.OpenSettings();
                    if (state.Drawer == "Approvals")
                        UnifiedDesktopTests.ClickControl((Button)window.FindName("PendingBadge"));
                    var host = CreateHost(window);
                    Assert.Equal(!state.Local, ((RadioButton)window.Configuration!.FindName("RemoteNav")).IsChecked);
                    Assert.Equal(state.Local, ((RadioButton)window.Configuration.FindName("LocalNav")).IsChecked);
                    Assert.False(window.Configuration.HasRunningServer);
                    var installationState = (ContentControl)window.Configuration.FindName("DetectionChip");
                    var serviceState = (ContentControl)window.Configuration.FindName("ServiceStateChip");
                    Assert.InRange(Math.Abs(installationState.TranslatePoint(new Point(), host).Y - serviceState.TranslatePoint(new Point(), host).Y), 0, 1);
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
                    if (state.Page == "Mcp") Assert.Equal(830, ((FrameworkElement)window.Configuration.FindName("ClientPane")).ActualWidth);
                    Assert.Equal(state.Pending ? Visibility.Visible : Visibility.Collapsed, ((FrameworkElement)window.FindName("PendingBadge")).Visibility);
                    Assert.Equal(state.Pending ? Visibility.Visible : Visibility.Collapsed, ((FrameworkElement)window.FindName("ApprovalOffPill")).Visibility);
                    if (state.Drawer == "Approvals") Assert.Equal(440, ((Border)window.FindName("DrawerPanel")).Width);
                    if (state.Page == "Engineering") Assert.False(((MainViewModel)window.DataContext).Session.IsConnected);
                    if (state.Name == "mcp-zh-light")
                    {
                        var metrics = new List<string>();
                        foreach (string name in new[] { "RailMcp", "RailEngineering", "RailCalls", "RailAudit", "RailEnvironment", "RailLog" })
                        {
                            var element = (FrameworkElement)window.FindName(name);
                            var point = element.TransformToAncestor(host).Transform(new Point());
                            Assert.Equal(40, element.ActualHeight);
                            Assert.Equal(45, point.Y);
                            metrics.Add($"{name}: {point.X},{point.Y} {element.ActualWidth}x{element.ActualHeight}");
                        }
                        foreach (string name in new[] { "MinimizeWindowButton", "MaximizeWindowButton", "CloseWindowButton" })
                            Assert.Equal(28, ((FrameworkElement)window.FindName(name)).ActualWidth);
                        var log = (TiaOpenness.Gui.Controls.WorkbenchLogView)window.Configuration.FindName("ActivityLog");
                        metrics.Add($"Log: {log.ActualWidth}, padding {log.Padding}, document padding {log.Document.PagePadding}, page width {log.Document.PageWidth}");
                        Assert.Equal(new Thickness(0), log.Document.PagePadding);
                        var table = (System.Windows.Documents.Table)log.Document.Blocks.FirstBlock;
                        var message = table.RowGroups[0].Rows[0].Cells[1].Blocks.FirstBlock;
                        Assert.Equal(90, table.Columns[0].Width.Value);
                        Assert.Equal(12, table.RowGroups[0].Rows[0].Cells[0].Padding.Right);
                        double textHeight = message.ContentEnd.GetCharacterRect(LogicalDirection.Backward).Bottom
                            - message.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
                        Assert.InRange(textHeight, 10, 45);
                        metrics.Add($"First log message height: {textHeight}");
                        foreach (string name in new[] { "LinkClient", "LinkServer", "LinkEndpoint", "Transport" })
                        {
                            var row = (FrameworkElement)window.Configuration.FindName(name);
                            var point = row.TransformToAncestor(host).Transform(new Point(0, row.ActualHeight / 2));
                            metrics.Add($"{name} center: {point.Y}");
                        }
                        File.WriteAllLines(Path.Combine(output, "layout.txt"), metrics);
                    }
                    SaveRender(host, Path.Combine(output, state.Name + ".png"));
                }
                finally { window.Close(); ThemeManager.Current.Theme = previous; }
                Assert.True(string.IsNullOrWhiteSpace(trace.Text), trace.Text);
            });
        }
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
                yield return new("settings-" + suffix, language, theme, Drawer: "Settings");
                yield return new("approval-badges-" + suffix, language, theme, Pending: true);
                yield return new("approvals-empty-" + suffix, language, theme, Drawer: "Approvals");
            }
    }

    internal static Border CreateHost(MainWindow window)
    {
        WpfContext.Drain();
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var host = new Border { Child = content, DataContext = window.DataContext, Width = 1200, Height = 780 };
        NameScope.SetNameScope(host, NameScope.GetNameScope(window));
        foreach (System.Windows.Input.CommandBinding binding in window.CommandBindings) host.CommandBindings.Add(binding);
        host.Measure(new Size(1200, 780));
        host.Arrange(new Rect(0, 0, 1200, 780));
        host.UpdateLayout();
        WpfContext.Drain();
        host.UpdateLayout();
        WpfContext.Drain();
        return host;
    }

    internal static void SaveRender(FrameworkElement host, string path)
    {
        var bitmap = new RenderTargetBitmap(1200, 780, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed record RenderState(string Name, AppLanguage Language, AppTheme Theme,
        bool Local = false, bool Running = false, string? Drawer = null, bool Pending = false, string Page = "Mcp");

    private static List<ClientProfile> Clients()
    {
        string[] names = ["Claude Code", "Codex", "Gemini CLI", "Qwen", "Kimi", "Yuanbao", "DeepSeek", "GLM", "Grok", "Qwen Agent", "Cursor", "VS Code · Copilot"];
        return names.Select((name, i) => new ClientProfile("fixture-" + i, name,
            i == 0 ? @"~\.claude.json" : @"C:\fixture\" + i + ".json", "render fixture") { Detected = i == 0 }).ToList();
    }
}
