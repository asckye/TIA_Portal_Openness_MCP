using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.Themes;
using TiaOpenness.Gui.Views;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class WorkbenchRenderProjectPagesTests(WpfContext wpf)
{
    [Theory]
    [InlineData(1366, 860, AppTheme.Light, AppLanguage.Chinese)]
    [InlineData(1366, 860, AppTheme.Light, AppLanguage.English)]
    [InlineData(1366, 860, AppTheme.Dark, AppLanguage.Chinese)]
    [InlineData(1024, 640, AppTheme.Light, AppLanguage.Chinese)]
    public void Project_documents_render_the_selected_fixture_at_design_size(int width, int height, AppTheme theme, AppLanguage language)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var themes = ThemeManager.Current;
            var previous = (themes.Theme, themes.Accent, themes.HighContrastOverride, App.Settings.LeftPaneOpen);
            themes.Theme = theme; themes.Accent = AppAccent.Cobalt; themes.HighContrastOverride = false; App.Settings.LeftPaneOpen = true;
            var model = ProjectPageTestSupport.Model(release: "21", configure: client =>
            {
                client.AttachedProject = new() { Name = "Conveyor_Line_04", Path = @"D:\Projects\Line04\Conveyor_Line_04.ap21" };
                client.Devices[0].TypeName = "CPU 1512SP-1 PN";
                client.StatusItems.RemoveAll(row => row.CompareState != VcCompareState.Unequal);
                client.Blocks.RemoveAll(block => block.FolderPath is "Drives" or "HMI");
                client.LogicHandler = (_, _) => Task.FromResult("""
                    ── 程序段 1 · Conveyor  [SCL]
                       // Handle the operator start and stop inputs.
                       IF #StopCmd THEN
                           #Running := FALSE;
                           #Speed := 0;
                       ELSIF #StartCmd THEN
                           #Running := TRUE;
                           #Speed := #RequestedSpeed;
                       END_IF;
                    """);
                client.CompileResult = new() { State = "Warning", WarningCount = 1,
                    Messages = [new() { Severity = CompileSeverity.Warning, Target = "Conveyor/Conveyor_Ctrl_FB",
                        Description = "Unused temporary variable." }] };
                client.Tags.Add(new() { Name = "ConveyorSpeed", DataType = "Real", LogicalAddress = "%QD4", TableName = "root/tag-tables/Drives" });
            });
            var approvals = FeaturePageFixtures.Approvals(false);
            var window = new MainWindow(model, false, approvals);
            try
            {
                window.ShowConfiguration(false);
                window.Navigate("Engineering");
                var host = WorkbenchRenderTests.CreateHost(window, width, height);
                WorkbenchRenderTests.ApplyCurrentPalette(host);
                model.Engineering.Compile.Execute(null);
                window.BlocksContent.LocateControlBlock(model.Engineering.Blocks.Single(row => row.Name == "Conveyor_Ctrl_FB"));
                void Capture(string view)
                {
                    WpfContext.Drain(); host.UpdateLayout();
                    string? output = Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR");
                    if (output == null) return;
                    var dir = Directory.CreateDirectory(Path.Combine(output, "project"));
                    string variant = $"{theme.ToString().ToLowerInvariant()}-{(Loc.Current.IsChinese ? "zh" : "en")}-{width}x{height}";
                    WorkbenchRenderTests.SaveRender(host, Path.Combine(dir.FullName, $"project-{view}-{variant}.png"), width, height);
                }
                UnifiedDesktopTests.ClickControl(window.OperationsContent.RenderTab);
                Assert.Equal(9, model.Engineering.SourceLines.Count);
                Assert.Equal(width >= 1280, window.OperationsContent.BlockIdentity.Visibility == Visibility.Visible);
                Assert.Equal(44, window.OperationsContent.ProjectToolbar.ActualHeight);
                Assert.DoesNotContain(WorkbenchRenderFeaturePagesTests.Descendants<TextBlock>(window.OperationsContent.ProjectToolbar),
                    text => text.Text.Contains("System.Windows.", StringComparison.Ordinal));
                Assert.True(model.Engineering.FocusedBlock!.IsModified);
                if (width >= 1280) AssertTreeGeometry(window.ProjectExplorer.Nodes);
                Capture("code");
                var block = model.Engineering.FocusedBlock!;
                approvals.Receive(FeaturePageFixtures.Request("block-review") with
                {
                    Release = model.SelectedReleaseKey, Project = model.Session.OpenProjectFile, Plc = model.Engineering.SelectedDevice!.Id,
                    ParametersJson = new System.Text.Json.Nodes.JsonObject { ["blockPath"] = block.Path }.ToJsonString(),
                });
                WpfContext.Drain(); host.UpdateLayout();
                Assert.Equal(Visibility.Visible, window.OperationsContent.BlockReviewButton.Visibility);
                Assert.True(window.OperationsContent.BlockReviewButton.ActualWidth > 0);
                Capture("pending-review");
                approvals.Deny("block-review");
                UnifiedDesktopTests.ClickControl(window.OperationsContent.InterfaceTab);
                Assert.Equal(ProjectDocument.Interface, model.Engineering.Document);
                Capture("interface");
                window.BlocksContent.ShowCompileTab();
                Assert.Equal(CompileSeverity.Warning, Assert.Single(model.Engineering.CompilerMessages).Severity);
                Capture("compile");
                UnifiedDesktopTests.ClickControl(window.OperationsContent.TagsTab);
                Assert.Equal(2, model.Engineering.TagCount);
                Capture("tags");
                var tags = (PlcTagsView)window.PlcTagsContent.Content;
                tags.TagSearch.Text = "%QD";
                Assert.Equal("ConveyorSpeed", Assert.Single(tags.TagRows.Items.Cast<TagInfo>()).Name);
                Assert.True(tags.ExportCsv.IsEnabled);
                tags.TagSearch.Text = "";
                Assert.Equal(2, tags.TagRows.Items.Count);
                window.Navigate("VersionControl");
                Assert.True(model.VersionControl.HasStatus);
                Capture("vci");
            }
            finally
            {
                window.Close();
                (themes.Theme, themes.Accent, themes.HighContrastOverride, App.Settings.LeftPaneOpen) = previous;
            }
        });
    }

    private static void AssertTreeGeometry(TreeView tree)
    {
        Assert.Equal(new Thickness(8), tree.Padding);
        var items = WorkbenchRenderFeaturePagesTests.Descendants<TreeViewItem>(tree)
            .Where(item => item.Visibility == Visibility.Visible && item.ActualHeight > 0).ToArray();
        Assert.Contains(items, item => item.Header is BlockNode { Row: not null });
        Assert.Contains(items, item => item.Header is ProjectLink);
        Assert.Single(items, item => item.Header is BlockNode { IsActive: true });
        foreach (var item in items)
        {
            var surface = (Border)item.Template.FindName("Surface", item);
            double rowHeight = item.Header switch
            {
                BlockNode { IsFolder: false } => 45,
                DeviceNode => 43.875,
                _ => 28,
            };
            Assert.Equal(rowHeight, surface.ActualHeight);
            var branch = (Border)item.Template.FindName("Branch", item);
            Assert.Equal(new Thickness(11, item.Header is DeviceNode ? 4 : 2, 0, 0), branch.Margin);
            Assert.Equal(new Thickness(1, 0, 0, 0), branch.BorderThickness);
            Assert.Equal(new Thickness(9, 0, 0, 0), branch.Padding);
            if (item.Header is DeviceNode or BlockNode { IsFolder: true })
            {
                bool device = item.Header is DeviceNode;
                Assert.Equal(device ? new Thickness(8, 6, 8, 6) : new Thickness(6, 0, 6, 0), surface.Padding);
                var switcher = (FrameworkElement)item.Template.FindName("Switcher", item);
                Assert.Equal(device ? 11 : 10, switcher.ActualWidth);
                Assert.Equal(6, switcher.Margin.Right);
                if (item.IsExpanded && item.HasItems)
                {
                    var first = (TreeViewItem)item.ItemContainerGenerator.ContainerFromIndex(0);
                    Assert.Equal(new Point(21, surface.ActualHeight + branch.Margin.Top), first.TranslatePoint(new Point(), item));
                }
            }
            if (item.Header is BlockNode { IsFolder: false })
            {
                var choice = WorkbenchRenderFeaturePagesTests.Descendants<CheckBox>(surface).Single();
                Assert.Equal(new Thickness(12, 6, 8, 6), choice.Padding);
                var labels = WorkbenchRenderFeaturePagesTests.Descendants<TextBlock>(choice)
                    .Where(label => label.LineStackingStrategy == LineStackingStrategy.BlockLineHeight).ToArray();
                Assert.Equal(new double[] { 18, 15 }, labels.Select(label => label.LineHeight));
                Assert.Equal(18, labels[1].TranslatePoint(new Point(), labels[0]).Y);
                if (item.Header is BlockNode { IsActive: true })
                {
                    var card = (Border)choice.Template.FindName("Row", choice);
                    Assert.Equal(new Size(203, 45), card.RenderSize);
                    var bar = (Border)choice.Template.FindName("ActiveBar", choice);
                    Assert.Equal(new Size(3, 16), bar.RenderSize);
                    Assert.Equal(new Point(3, 14.5), bar.TranslatePoint(new Point(), card));
                }
            }
        }
        var conveyor = items.Where(item => item.Header is BlockNode { Row.Info.FolderPath: "Conveyor" }).ToArray();
        Assert.Equal(2, conveyor.Length);
        Assert.Equal(47, Math.Abs(conveyor[0].TranslatePoint(new Point(), conveyor[1]).Y));
    }

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
                        window.Navigate("Engineering");
                        using var projectTrace = new BindingPathTests.BindingTrace();
                        foreach (string name in new[] { "RenderTab", "InterfaceTab", "TagsTab", "CompileTab" })
                        {
                            var tab = (RadioButton)ProjectPageTestSupport.Find(window, name);
                            UnifiedDesktopTests.ClickControl(tab);
                            host.UpdateLayout(); WpfContext.Drain(); host.UpdateLayout();
                            Assert.True(tab.IsChecked);
                            if (name == "TagsTab")
                            {
                                Assert.Equal(Visibility.Visible, window.PlcTagsContent.Visibility);
                                Assert.Single(((PlcTagsView)window.PlcTagsContent.Content).TagRows.Items);
                            }
                            if (name == "CompileTab")
                                Assert.InRange(window.EngineeringSideContent.TranslatePoint(new Point(), host).Y, 0, size.Height - 44);
                            if (output != null)
                            {
                                string file = $"{name}-{size.Width}x{size.Height}-{(language == AppLanguage.Chinese ? "zh" : "en")}-{theme}.png";
                                WorkbenchRenderTests.SaveRender(host, Path.Combine(output, "UI-2d", file), size.Width, size.Height);
                            }
                        }
                        Assert.True(string.IsNullOrWhiteSpace(projectTrace.Text), projectTrace.Text);
                    }
                }
                finally { window.Close(); ThemeManager.Current.HighContrastOverride = contrast; ThemeManager.Current.Theme = previous; }
            });
    }

    internal sealed class AtlasFixture(string state) : IAtlasService
    {
        public Task<AtlasResult> GenerateAsync(AtlasRequest request, IProgress<AtlasProgress> progress, CancellationToken cancellationToken)
        {
            int index = Math.Min(2, request.BlockPaths.Count - 1);
            progress.Report(new AtlasProgress(index, request.BlockPaths.Count, request.BlockPaths[index]));
            if (state == "running") return Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith<AtlasResult>(
                _ => throw new OperationCanceledException(cancellationToken), TaskScheduler.Default);
            return Task.FromResult(state == "done" ? new AtlasResult(@"D:\Projects\Line04\atlas\index.html", null)
                : new AtlasResult(null, Loc.Current.IsChinese ? "无法导出受保护的程序块。" : "A protected block could not be exported."));
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
                        var bar = state.Item7 == "running" ? window.BusyCard : window.Toast;
                        if (state.Item7 == "failed")
                        {
                            Assert.Equal(Loc.Current["Project.RetryAtlas"], window.ToastAction.Content);
                            Assert.Contains(blocks.Atlas.Detail, window.ToastMessage.Text);
                            Assert.Same(window.FindResource("Ant.colorError"), window.ToastDot.Fill);
                        }
                        if (state.Item7 == "done")
                        {
                            Assert.Equal(Loc.Current["Pages.AtlasDone"], window.ToastTitle.Text);
                            Assert.Equal(blocks.Atlas.Detail, window.ToastMessage.Text);
                            Assert.Equal(Loc.Current["Project.OpenAtlas"], window.ToastAction.Content);
                            Assert.Equal(Loc.Current["Project.OpenFolder"], window.ToastSecondAction.Content);
                            Assert.Same(window.FindResource("Primer.ok"), window.ToastDot.Fill);
                        }
                        Assert.Equal(Visibility.Visible, bar.Visibility);
                        Assert.True(bar.ActualHeight > 0);
                    }
                    else if (state.Item2 == "Blocks")
                    {
                        var blocks = (BlocksView)window.FindName("BlocksContent");
                        Assert.Equal(Visibility.Collapsed, ((Border)blocks.FindName("AtlasFooter")).Visibility);
                        var card = (Border)blocks.FindName("BlocksCard");
                        var rows = (Grid)card.Child;
                        var selection = rows.Children.OfType<StackPanel>().Single(panel => Grid.GetRow(panel) == 1);
                        Assert.Equal(selection.TranslatePoint(new Point(0, selection.ActualHeight), card).Y,
                            blocks.Editor.TranslatePoint(new Point(), card).Y);
                        Assert.Equal(new Thickness(0), card.Margin);
                        Assert.Equal(window.BlockDocument.ActualWidth, card.ActualWidth);
                        Assert.Equal(double.PositiveInfinity, card.MaxWidth);
                    }
                    WorkbenchRenderTests.SaveRender(host, Path.Combine(output, state.Item1 + ".png"));
                    if (state.Item2 is "Engineering" or "Blocks")
                    {
                        var side = (EngineeringSideView)window.FindName("EngineeringSideContent");
                        var tab = (RadioButton)ProjectPageTestSupport.Find(window, "CompileTab");
                        if (state.Item3) UnifiedDesktopTests.ClickControl(tab);
                        else
                        {
                            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)side.Parent).Visibility);
                            Assert.Equal(0, side.ActualWidth);
                            // Also measure the results document against the original geometry with an empty project.
                            model.Engineering.Document = ViewModels.ProjectDocument.Compile;
                        }
                        host.UpdateLayout();
                        Assert.True(tab.IsChecked);
                        Assert.Equal(window.ProjectOverview.ActualWidth, side.ActualWidth);
                        Assert.Null(side.FindName("EnvironmentCard"));
                        Assert.Null(side.FindName("BlocksLogCard"));
                        Assert.Same(model.Engineering.Compile, window.OperationsContent.ToolbarCompile.Command);
                        Assert.True(((Button)side.FindName("CompileButton")).IsDefault);
                    }
                }
                finally { window.Close(); ThemeManager.Current.Theme = previous; }
                Assert.True(string.IsNullOrWhiteSpace(trace.Text), trace.Text);
            });
        }
    }
}
