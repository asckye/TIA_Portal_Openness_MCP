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
        foreach (var state in new[]
        {
            new RenderState("mcp-zh-light", AppLanguage.Chinese, AppTheme.Light),
            new RenderState("mcp-zh-light-local", AppLanguage.Chinese, AppTheme.Light, Local: true),
            new RenderState("mcp-zh-light-running", AppLanguage.Chinese, AppTheme.Light, Running: true),
            new RenderState("mcp-zh-dark", AppLanguage.Chinese, AppTheme.Dark),
            new RenderState("mcp-en-light", AppLanguage.English, AppTheme.Light),
            new RenderState("settings-zh-light", AppLanguage.Chinese, AppTheme.Light, Drawer: "Settings"),
            new RenderState("settings-zh-dark", AppLanguage.Chinese, AppTheme.Dark, Drawer: "Settings"),
            new RenderState("engineering-zh-light", AppLanguage.Chinese, AppTheme.Light, Page: "Engineering"),
            new RenderState("approval-badges-zh-light", AppLanguage.Chinese, AppTheme.Light, Pending: true),
            new RenderState("approvals-empty-zh-light", AppLanguage.Chinese, AppTheme.Light, Drawer: "Approvals"),
        })
        {
            wpf.RunWithLanguage(state.Language, () =>
            {
                using var trace = new BindingPathTests.BindingTrace();
                var previous = ThemeManager.Current.Theme;
                ThemeManager.Current.Theme = state.Theme;
                var preview = new ConfigurationPreview("14sp1", @"C:\Program Files\Siemens\Automation\Portal V14",
                    "192.168.86.131", state.Local, state.Running, state.Running, state.Running ? "render-fixture-secret" : "",
                    "20:50:27   Client scan · 1 detected, 11 not detected\n20:50:27   Ready · configure both sides on this page\n20:50:27   Loaded V14sp1 service config\n20:50:27   Update check · 3.3.0 is latest (api)\n", Clients());
                var approvals = new ApprovalServiceStub(state.Pending ? 1 : 0) { Enabled = !state.Pending };
                var window = new MainWindow(new MainViewModel(new FakeStudioClient(), new FakeDialogService()), false, approvals: approvals, preview: preview);
                try
                {
                    window.ShowConfiguration(false);
                    window.Navigate(state.Page);
                    if (state.Drawer == "Settings") window.OpenSettings();
                    if (state.Drawer == "Approvals")
                        UnifiedDesktopTests.ClickControl((Button)window.FindName("PendingBadge"));
                    var content = (FrameworkElement)window.Content;
                    window.Content = null;
                    var host = new Border { Child = content, DataContext = window.DataContext, Width = 1200, Height = 780 };
                    NameScope.SetNameScope(host, NameScope.GetNameScope(window));
                    host.Measure(new Size(1200, 780));
                    host.Arrange(new Rect(0, 0, 1200, 780));
                    host.UpdateLayout();
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                    host.UpdateLayout();
                    Assert.Equal(!state.Local, ((RadioButton)window.Configuration!.FindName("RemoteNav")).IsChecked);
                    Assert.Equal(state.Local, ((RadioButton)window.Configuration.FindName("LocalNav")).IsChecked);
                    Assert.False(window.Configuration.HasRunningServer);
                    if (state.Page == "Mcp") Assert.Equal(660, ((FrameworkElement)window.Configuration.FindName("ClientPane")).ActualWidth);
                    Assert.Equal(state.Pending ? Visibility.Visible : Visibility.Collapsed, ((FrameworkElement)window.FindName("PendingBadge")).Visibility);
                    Assert.Equal(state.Pending ? Visibility.Visible : Visibility.Collapsed, ((FrameworkElement)window.FindName("ApprovalOffPill")).Visibility);
                    if (state.Drawer == "Approvals") Assert.Equal(440, ((Border)window.FindName("DrawerPanel")).Width);
                    if (state.Page == "Engineering") Assert.False(((MainViewModel)window.DataContext).Session.IsConnected);
                    if (state.Name == "mcp-zh-light")
                    {
                        var metrics = new List<string>();
                        double[] railCenters = [107, 145, 179, 211, 246, 283, 320, 357];
                        int railIndex = 0;
                        foreach (string name in new[] { "RailMcp", "RailEngineering", "RailBlocks", "RailVersionControl", "RailCalls", "RailAudit", "RailEnvironment", "RailLog", "ExportDiagnostics", "SettingsButton" })
                        {
                            var element = (FrameworkElement)window.FindName(name);
                            var point = element.TransformToAncestor(host).Transform(new Point());
                            metrics.Add($"{name}: {point.X},{point.Y} {element.ActualWidth}x{element.ActualHeight}");
                            if (name.StartsWith("Rail", StringComparison.Ordinal)) Assert.Equal(railCenters[railIndex++], point.Y + element.ActualHeight / 2);
                        }
                        foreach (string name in new[] { "MinimizeWindowButton", "MaximizeWindowButton", "CloseWindowButton" })
                            Assert.Equal(28, ((FrameworkElement)window.FindName(name)).ActualWidth);
                        var log = (TiaDesktop.Glass.GlassLogView)window.Configuration.FindName("ActivityLog");
                        metrics.Add($"Log: {log.ActualWidth}, padding {log.Padding}, document padding {log.Document.PagePadding}, page width {log.Document.PageWidth}");
                        Assert.Equal(new Thickness(0), log.Document.PagePadding);
                        var table = (System.Windows.Documents.Table)log.Document.Blocks.FirstBlock;
                        var message = table.RowGroups[0].Rows[0].Cells[1].Blocks.FirstBlock;
                        Assert.Equal(72, table.Columns[0].Width.Value);
                        Assert.Equal(12, table.RowGroups[0].Rows[0].Cells[0].Padding.Right);
                        double textHeight = message.ContentEnd.GetCharacterRect(LogicalDirection.Backward).Bottom
                            - message.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
                        Assert.InRange(textHeight, 25, 40);
                        metrics.Add($"First log message height: {textHeight}");
                        foreach (string name in new[] { "LinkClient", "LinkServer", "LinkEndpoint", "Transport" })
                        {
                            var row = (FrameworkElement)window.Configuration.FindName(name);
                            var point = row.TransformToAncestor(host).Transform(new Point(0, row.ActualHeight / 2));
                            metrics.Add($"{name} center: {point.Y}");
                        }
                        File.WriteAllLines(Path.Combine(output, "layout.txt"), metrics);
                    }
                    var bitmap = new RenderTargetBitmap(1200, 780, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(host);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, state.Name + ".png"));
                    encoder.Save(stream);
                }
                finally { window.Close(); ThemeManager.Current.Theme = previous; }
                Assert.True(string.IsNullOrWhiteSpace(trace.Text), trace.Text);
            });
        }
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
