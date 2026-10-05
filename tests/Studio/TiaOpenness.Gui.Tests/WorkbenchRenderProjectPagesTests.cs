using System;
using System.IO;
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
        foreach (var state in new[]
        {
            ("ops-zh-light", "Engineering", false, false, AppLanguage.Chinese, AppTheme.Light, ""),
            ("ops-zh-light-connected", "Engineering", true, false, AppLanguage.Chinese, AppTheme.Light, ""),
            ("blocks-zh-light", "Blocks", true, false, AppLanguage.Chinese, AppTheme.Light, ""),
            ("atlas-running-zh-light", "Blocks", true, false, AppLanguage.Chinese, AppTheme.Light, "running"),
            ("atlas-done-zh-light", "Blocks", true, false, AppLanguage.Chinese, AppTheme.Light, "done"),
            ("atlas-failed-zh-light", "Blocks", true, false, AppLanguage.Chinese, AppTheme.Light, "failed"),
            ("vc-zh-light", "VersionControl", true, false, AppLanguage.Chinese, AppTheme.Light, ""),
            ("log-zh-light", "Log", true, false, AppLanguage.Chinese, AppTheme.Light, ""),
            ("log-zh-dark", "Log", true, false, AppLanguage.Chinese, AppTheme.Dark, ""),
            ("ops-zh-dark-connected", "Engineering", true, false, AppLanguage.Chinese, AppTheme.Dark, ""),
            ("blocks-zh-dark", "Blocks", true, false, AppLanguage.Chinese, AppTheme.Dark, ""),
            ("vc-zh-dark", "VersionControl", true, false, AppLanguage.Chinese, AppTheme.Dark, ""),
            ("ops-results-zh-light", "Engineering", true, true, AppLanguage.Chinese, AppTheme.Light, ""),
            ("blocks-results-zh-dark", "Blocks", true, true, AppLanguage.Chinese, AppTheme.Dark, ""),
            ("vc-empty-zh-light", "VersionControl", true, false, AppLanguage.Chinese, AppTheme.Light, ""),
            ("blocks-en-light", "Blocks", true, false, AppLanguage.English, AppTheme.Light, ""),
        })
        {
            wpf.RunWithLanguage(state.Item5, () =>
            {
                using var trace = new BindingPathTests.BindingTrace();
                var previous = ThemeManager.Current.Theme;
                ThemeManager.Current.Theme = state.Item6;
                var model = ProjectPageTestSupport.Model(state.Item3, state.Item4, state.Item1 != "vc-empty-zh-light");
                if (state.Item2 == "Log")
                    typeof(Services.WorkbenchActivity).GetProperty("Log")!.SetValue(model.Activity, model.Activity.Log
                        + "20:45:00  [INFO] 工程已加载 · Project loaded\n"
                        + "20:45:01  Warning: 程序块需要重新编译\n"
                        + "20:45:02  [ERROR] 导入失败 · Import failed\n"
                        + "20:45:03  [DEBUG] 读取程序块列表 · Reading blocks\n");
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
                    WorkbenchRenderTests.SaveRender(host, Path.Combine(output, state.Item1 + ".png"));
                    if (state.Item2 is "Engineering" or "Blocks")
                        Assert.Equal(284, ((FrameworkElement)window.FindName("EngineeringSideContent")).ActualWidth);
                }
                finally { window.Close(); ThemeManager.Current.Theme = previous; }
                Assert.True(string.IsNullOrWhiteSpace(trace.Text), trace.Text);
            });
        }
    }
}
