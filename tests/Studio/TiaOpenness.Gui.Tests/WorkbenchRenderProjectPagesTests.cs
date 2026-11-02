using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.Themes;
using TiaOpenness.Gui.Views;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class WorkbenchRenderProjectPagesTests(WpfContext wpf)
{
    [Fact]
    public void Render_redesigned_pages_at_both_review_sizes_in_all_languages_and_themes()
    {
        string? output = Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR");
        if (output != null) Directory.CreateDirectory(Path.Combine(output, "UI-2d"));
        foreach (var language in new[] { AppLanguage.Chinese, AppLanguage.English })
        foreach (var size in new[] { (Width: 1024, Height: 640), (Width: 1366, Height: 860) })
            wpf.RunWithLanguage(language, () =>
            {
                var previous = ThemeManager.Current.Theme;
                var contrast = ThemeManager.Current.HighContrastOverride;
                var preview = new TiaMcpConfigurator.ConfigurationPreview("21", @"C:\fixture\Portal V21", "127.0.0.1", false, false, true, "fixture-private-key", "09:41:02  Ready\n",
                    new[] { "Claude Code", "Codex", "Gemini CLI", "VS Code · Copilot" }.Select((name, i) =>
                        new TiaMcpConfigurator.ClientProfile("fixture-" + i, name, @"C:\fixture\client-" + i + ".json", "Offline client fixture") { Detected = i < 2 }).ToList());
                var diagnostics = new FeaturePageFixtures.Diagnostics(); diagnostics.Complete();
                var window = new MainWindow(ProjectPageTestSupport.Model(results: true, release: "21"), false, FeaturePageFixtures.Approvals(), diagnostics, preview);
                try
                {
                    window.ConfigureFeaturePages(new FeaturePageFixtures.Journal(), new FeaturePageFixtures.Audit(), new FeaturePageFixtures.Environment());
                    window.ShowConfiguration(false);
                    var host = WorkbenchRenderTests.CreateHost(window, size.Width, size.Height);
                    foreach (string theme in new[] { "light", "dark", "high-contrast" })
                    {
                        ThemeManager.Current.HighContrastOverride = theme == "high-contrast";
                        ThemeManager.Current.Theme = theme == "dark" ? AppTheme.Dark : AppTheme.Light;
                        WorkbenchRenderTests.ApplyCurrentPalette(host);
                        foreach (string page in new[] { "Engineering", "Environment", "Log", "Mcp", "VersionControl", "Calls", "Audit" })
                        {
                            using var trace = new BindingPathTests.BindingTrace();
                            window.Navigate(page);
                            WpfContext.Drain(); host.UpdateLayout(); WpfContext.Drain(); host.UpdateLayout();
                            Assert.True(string.IsNullOrWhiteSpace(trace.Text), page + ": " + trace.Text);
                            Assert.DoesNotContain(WorkbenchRenderFeaturePagesTests.Descendants<TextBlock>(host), text => text.Text.Contains("fixture-private-key", StringComparison.Ordinal));
                            Assert.Equal(36d, Application.Current.FindResource("Ui.RowHeight"));
                            if (output != null)
                            {
                                string file = $"{page}-{size.Width}x{size.Height}-{(language == AppLanguage.Chinese ? "zh" : "en")}-{theme}.png";
                                WorkbenchRenderTests.SaveRender(host, Path.Combine(output, "UI-2d", file), size.Width, size.Height);
                            }
                        }
                    }
                }
                finally { window.Close(); ThemeManager.Current.HighContrastOverride = contrast; ThemeManager.Current.Theme = previous; }
            });
    }

    private sealed class AtlasFixture(string state) : IAtlasService
    {
        public Task<AtlasResult> GenerateAsync(AtlasRequest request, IProgress<AtlasProgress> progress, CancellationToken cancellationToken)
        {
            progress.Report(new AtlasProgress(3, 9, "Scale_Analog_FC"));
            if (state == "running") return Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith<AtlasResult>(
                _ => throw new OperationCanceledException(cancellationToken), TaskScheduler.Default);
            return Task.FromResult(state == "done" ? new AtlasResult(@"D:\Projects\Line04\atlas\index.html", null)
                : new AtlasResult(null, "atlas generation is not connected yet (P6-48)"));
        }
    }

    [WorkbenchRenderFact]
    public void Render_project_pages_and_atlas_states_without_native_services()
    {
        string output = Path.GetFullPath(Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR")!);
        Directory.CreateDirectory(output);
        foreach (var language in new[] { AppLanguage.Chinese, AppLanguage.English })
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        foreach (var fixture in new[]
        {
            ("ops", "Engineering", false, false, ""),
            ("ops-connected", "Engineering", true, false, ""),
            ("ops-results", "Engineering", true, true, ""),
            ("blocks", "Blocks", true, false, ""),
            ("blocks-results", "Blocks", true, true, ""),
            ("atlas-running", "Blocks", true, false, "running"),
            ("atlas-done", "Blocks", true, false, "done"),
            ("atlas-failed", "Blocks", true, false, "failed"),
            ("vc", "VersionControl", true, false, ""),
            ("vc-empty", "VersionControl", true, false, ""),
            ("log", "Log", true, false, ""),
        })
        {
            var state = (fixture.Item1 + "-" + (language == AppLanguage.Chinese ? "zh" : "en") + "-" + theme.ToString().ToLowerInvariant(), fixture.Item2, fixture.Item3, fixture.Item4, language, theme, fixture.Item5);
            wpf.RunWithLanguage(state.Item5, () =>
            {
                using var trace = new BindingPathTests.BindingTrace();
                var previous = ThemeManager.Current.Theme;
                ThemeManager.Current.Theme = state.Item6;
                var model = ProjectPageTestSupport.Model(state.Item3, state.Item4, !state.Item1.StartsWith("vc-empty", StringComparison.Ordinal),
                    release: state.Item2 == "VersionControl" ? "21" : "14sp1");
                if (state.Item2 == "Log")
                {
                    model.Activity.Append("[INFO] 工程已加载 · Project loaded", Services.WorkbenchActivity.Severity.Info);
                    model.Activity.Append("Warning: 程序块需要重新编译", Services.WorkbenchActivity.Severity.Warning);
                    model.Activity.Append("[ERROR] 导入失败 · Import failed", Services.WorkbenchActivity.Severity.Error);
                    model.Activity.Append("[DEBUG] 读取程序块列表 · Reading blocks", Services.WorkbenchActivity.Severity.Debug);
                }
                var window = new MainWindow(model, false, preview: new TiaMcpConfigurator.ConfigurationPreview("14sp1", @"C:\Program Files\Siemens\Automation\Portal V14", "192.168.86.131", false, false, false, "", "", [] ));
                try
                {
                    window.ShowConfiguration(false);
                    window.Navigate(state.Item2);
                    var host = WorkbenchRenderTests.CreateHost(window);
                    if (state.Item7.Length > 0)
                    {
                        var blocks = (BlocksView)window.FindName("BlocksContent");
                        blocks.SetAtlasService(new AtlasFixture(state.Item7));
                        _ = blocks.Atlas!.GenerateAsync(false);
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        host.UpdateLayout();
                        Assert.True(blocks.Atlas.Visible);
                        var bar = (Border)blocks.FindName("AtlasBar");
                        Assert.Equal(Visibility.Visible, bar.Visibility);
                        Assert.True(bar.ActualHeight > 0);
                    }
                    else if (state.Item2 == "Blocks")
                    {
                        var blocks = (BlocksView)window.FindName("BlocksContent");
                        Assert.Equal(Visibility.Collapsed, ((Border)blocks.FindName("AtlasFooter")).Visibility);
                        var card = (Border)blocks.FindName("BlocksCard");
                        var rows = (Grid)card.Child;
                        var header = rows.Children.OfType<Border>().Single(border => Grid.GetRow(border) == 0);
                        var selection = rows.Children.OfType<Border>().Single(border => Grid.GetRow(border) == 1);
                        Assert.InRange(selection.TranslatePoint(new Point(), card).Y - header.TranslatePoint(new Point(0, header.ActualHeight), card).Y, 8, 16);
                    }
                    WorkbenchRenderTests.SaveRender(host, Path.Combine(output, state.Item1 + ".png"));
                    if (state.Item2 is "Engineering" or "Blocks")
                    {
                        Assert.Equal(300, ((FrameworkElement)window.FindName("EngineeringSideContent")).ActualWidth);
                        var side = (EngineeringSideView)window.FindName("EngineeringSideContent");
                        Assert.Null(side.FindName("EnvironmentCard"));
                        Assert.Null(side.FindName("BlocksLogCard"));
                        Assert.True(((Button)side.FindName("CompileButton")).IsDefault);
                    }
                }
                finally { window.Close(); ThemeManager.Current.Theme = previous; }
                Assert.True(string.IsNullOrWhiteSpace(trace.Text), trace.Text);
            });
        }
    }
}
