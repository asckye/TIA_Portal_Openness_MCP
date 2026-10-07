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
                var model = ProjectPageTestSupport.Model(state.Item3, state.Item4, !state.Item1.StartsWith("vc-empty", StringComparison.Ordinal));
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
                        var footer = rows.Children.OfType<Border>().Single(border => Grid.GetRow(border) == 3);
                        Assert.InRange(card.ActualHeight - footer.TranslatePoint(new Point(0, footer.ActualHeight), card).Y, 1, 3);
                    }
                    WorkbenchRenderTests.SaveRender(host, Path.Combine(output, state.Item1 + ".png"));
                    if (state.Item2 is "Engineering" or "Blocks")
                    {
                        Assert.Equal(300, ((FrameworkElement)window.FindName("EngineeringSideContent")).ActualWidth);
                        var tail = (Controls.LogTailView)((EngineeringSideView)window.FindName("EngineeringSideContent")).FindName("LogTail");
                        if (tail.Items.Count > 0 && tail.ActualHeight >= 24)
                        {
                            var last = (ListBoxItem)tail.ItemContainerGenerator.ContainerFromIndex(tail.Items.Count - 1);
                            Assert.NotNull(last);
                            Assert.InRange(last.TranslatePoint(new Point(), tail).Y, 0, tail.ActualHeight - last.ActualHeight);
                        }
                    }
                }
                finally { window.Close(); ThemeManager.Current.Theme = previous; }
                Assert.True(string.IsNullOrWhiteSpace(trace.Text), trace.Text);
            });
        }
    }
}
