using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.Themes;
using TiaOpenness.Gui.ViewModels;
using Xunit;
using static TiaOpenness.Gui.Tests.WorkbenchRenderFeaturePagesTests;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class ProjectStateRenderTests(WpfContext wpf)
{
    [Fact]
    public void Failed_crash_rescan_reports_the_failure_without_opening_the_picker()
    {
        wpf.Run(() =>
        {
            var client = Fixture();
            var dialogs = new FakeDialogService();
            var model = new MainViewModel(client, dialogs);
            var window = new MainWindow(model, false, new Services.Stubs.ApprovalServiceStub());
            try
            {
                model.Session.ProcessId = 4204;
                model.Session.Connect.Execute(null);
                client.Exit(); WpfContext.Drain();
                client.ProcessScanError = new System.IO.IOException("fixture scan failed");
                UnifiedDesktopTests.ClickControl(window.ToastAction);
                Assert.Empty(dialogs.ProcessRequests);
                Assert.Equal("fixture scan failed", window.ToastMessage.Text);
                Assert.Same(window.FindResource("Ant.colorError"), window.ToastDot.Fill);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Crash_alert_identifies_the_process_and_rescans_before_showing_the_picker(bool hasProcesses)
    {
        wpf.Run(() =>
        {
            var client = Fixture();
            var dialogs = new FakeDialogService();
            var model = new MainViewModel(client, dialogs);
            var window = new MainWindow(model, false, new Services.Stubs.ApprovalServiceStub());
            try
            {
                model.Session.ProcessId = 4204;
                model.Session.Connect.Execute(null);
                client.Exit();
                WpfContext.Drain();
                Assert.Contains("PID 4204", window.ToastTitle.Text);
                client.Calls.Clear();
                if (!hasProcesses) client.Processes = [];
                UnifiedDesktopTests.ClickControl(window.ToastAction);
                var choices = Assert.Single(dialogs.ProcessRequests);
                Assert.Equal(hasProcesses ? new[] { 4204, 4316 } : Array.Empty<int>(), choices.Select(choice => choice.ProcessId));
                Assert.DoesNotContain(client.Calls, call => call.Name == "Connect");
                Assert.True(model.Session.LostConnection);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(1360, 812, AppTheme.Light, AppLanguage.Chinese, false)]
    [InlineData(1360, 812, AppTheme.Light, AppLanguage.English, false)]
    [InlineData(1360, 812, AppTheme.Dark, AppLanguage.Chinese, false)]
    [InlineData(1360, 812, AppTheme.Light, AppLanguage.Chinese, true)]
    [InlineData(1024, 640, AppTheme.Light, AppLanguage.Chinese, false)]
    [InlineData(1366, 860, AppTheme.Light, AppLanguage.Chinese, false)]
    [InlineData(1920, 1080, AppTheme.Light, AppLanguage.Chinese, false)]
    public void Project_states_follow_session_documents_and_command_results(int width, int height, AppTheme theme, AppLanguage language, bool contrast)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var themes = ThemeManager.Current;
            var previous = (themes.Theme, themes.HighContrastOverride, themes.Accent, App.Settings.LeftPaneOpen);
            (themes.Theme, themes.HighContrastOverride, themes.Accent, App.Settings.LeftPaneOpen) = (theme, contrast, AppAccent.Cobalt, true);
            var dialogs = new FakeDialogService();
            var client = Fixture();
            var model = new MainViewModel(client, dialogs) { SelectedReleaseKey = "21" };
            var window = new MainWindow(model, false, new Services.Stubs.ApprovalServiceStub());
            try
            {
                window.Navigate("Engineering");
                var forms = window.OperationsContent;
                WpfContext.ShowWindow(window);
                WpfContext.Drain(); window.UpdateLayout();
                Assert.Contains(WorkbenchRenderFeaturePagesTests.Descendants<TextBlock>(forms.FileRelease), label => label.Text == "V21 (MTA)");
                Assert.Contains(WorkbenchRenderFeaturePagesTests.Descendants<TextBlock>(forms.ProjectPathInput),
                    label => label.IsVisible && label.Text == model.Session.ProjectFilePlaceholder);
                UnifiedDesktopTests.ClickControl(forms.GoToMcp);
                WpfContext.Drain();
                Assert.True(window.RailMcp.IsChecked);
                window.Navigate("Engineering");
                window.Hide();
                var host = WorkbenchRenderTests.CreateHost(window, width, height);
                WorkbenchRenderTests.ApplyCurrentPalette(host);
                void Capture(string view, bool keepNotifications = false)
                {
                    Assert.Equal(Visibility.Visible, window.EngineeringPage.Visibility);
                    WpfContext.Drain(); host.UpdateLayout();
                    while (!keepNotifications && window.Toast.Visibility == Visibility.Visible)
                    {
                        UnifiedDesktopTests.ClickControl(window.ToastClose);
                        WpfContext.Drain();
                    }
                    SaveState(host, view, width, height, theme, language, contrast);
                }

                WpfContext.Complete(model.Session.RefreshProcessesAsync());
                Assert.Equal(2, model.Session.Processes.Count);
                Assert.DoesNotContain(client.Calls, c => c.Name == "Connect");
                Assert.Contains("MTA", model.Session.Processes[0].Details);
                Assert.Contains(Loc.Current["Project.WithUi"], model.Session.Processes[0].Details);
                Assert.Contains(Loc.Current["Project.LeaseAvailable"], model.Session.Processes[0].Details);
                Assert.Contains(Loc.Current["Project.LeaseHeld"], model.Session.Processes[1].Details);
                Assert.Equal(Visibility.Collapsed, forms.FileReleaseLabel.Visibility);
                Assert.Equal(Visibility.Collapsed, forms.AccessibleLabel4.Visibility);
                Assert.Contains(".ap21", (string)forms.ProjectPathInput.Tag);
                Assert.Equal(Loc.Current["Project.GoToMcp"], forms.GoToMcp.Content);
                Assert.Equal("Mcp", forms.GoToMcp.CommandParameter);
                Capture("state-disconnected");
                AssertCenteredColumn(forms.ConnectionForms, window.ProjectOverview, 820);
                window.Navigate("Mcp"); host.UpdateLayout();
                var config = window.Configuration!;
                RemoteConfigurationTests.CheckPageColumns(config.Panes, config.ActivityCard, host, width, 348);
                window.Navigate("Clients"); host.UpdateLayout();
                RemoteConfigurationTests.CheckPageColumns(config.Clients.ClientsScroll, config.Clients.ClientActionsBand, host, width, 392);
                window.Navigate("Engineering");
                var attachedProject = client.AttachedProject;
                client.AttachedProject = null;
                model.Session.Connect.Execute(null);
                Assert.True(model.Session.ShowConnectionForm);
                Assert.Equal(Loc.Current["Project.NoOpenFile"], model.Session.ConnectionLabel);
                model.Session.Disconnect.Execute(null);
                Assert.Equal(Loc.Current["Project.Disconnected"], model.Session.ConnectionLabel);
                client.AttachedProject = attachedProject;
                OpenProject(model, window);
                var editor = model.Engineering;
                var ops = window.OperationsContent;
                var blocks = window.BlocksContent;
                void OpenBlock(string name)
                {
                    blocks.LocateControlBlock(editor.Blocks.Single(r => r.Name == name));
                    UnifiedDesktopTests.ClickControl(ops.RenderTab);
                    WpfContext.Drain();
                }

                OpenBlock("FB_MixerRun");
                Assert.Contains(editor.SourceLines, line => line.Contains("#Running", StringComparison.Ordinal));
                Capture("project-code");
                AssertCenteredColumn(blocks.BlocksCard, window.BlockDocument, 920);
                foreach (var document in new[] { ProjectDocument.Hmi, ProjectDocument.SoftwareUnit })
                {
                    editor.Document = document;
                    host.UpdateLayout();
                    AssertCenteredColumn(blocks.NodeColumn, Viewport(blocks.NodeScroll), 900);
                }
                editor.Document = ProjectDocument.Tags; host.UpdateLayout();
                var tags = (Views.PlcTagsView)window.PlcTagsContent.Content;
                AssertCenteredColumn(tags.TagsColumn, tags, 920);
                editor.Document = ProjectDocument.Render;
                editor.FocusedBlock!.Selected = true;
                editor.OutputDirectory = Path.Combine(McpOperatorFixture.RepositoryRoot, "bin-build", "refactor", "UI-0036GB1", "exports");
                editor.ImportPathsText = Path.Combine(McpOperatorFixture.RepositoryRoot, "templates", "mcp-full-e2e-verify", "plc", "blocks",
                    "MCPVerify_FC_LAD_v3.xml");
                editor.ImportOverwrite = true;
                foreach (var (section, suffix) in new[] { ("Project", "project"), ("Export", "export"),
                    ("Import", "import"), ("Download", "download"), ("Inspect", "rules") })
                {
                    ops.ShowProjectOptions();
                    var tab = ops.Options.OptionTabs.Children.OfType<RadioButton>().Single(b => Equals(b.Tag, section));
                    UnifiedDesktopTests.ClickControl(tab);
                    Assert.Equal(section, ops.Options.CurrentSection);
                    Assert.True(((StackPanel)ops.Options.FindName(section + "Options")).Visibility == Visibility.Visible);
                    if (section != "Download") Capture("drawer-" + suffix);
                    ops.HideOptions();
                }
                UnifiedDesktopTests.ClickControl(ops.InterfaceTab);
                Assert.Equal(3, editor.Members.Count);
                Capture("project-iface");
                editor.Compile.Execute(null);
                Assert.Equal(CompileSeverity.Warning, Assert.Single(editor.CompilerMessages).Severity);
                Capture("project-diag");
                AssertCenteredColumn(blocks.BlocksCard, window.BlockDocument, 920);
                AssertCenteredColumn(window.CompileDocument, window.DocumentHost, 920);
                OpenBlock("Main");
                Assert.True(blocks.LadderPanel.Visibility == Visibility.Visible);
                Assert.Equal(4, Assert.Single(editor.Networks[0].Rungs).Elements.Count);
                var call = Assert.Single(editor.Networks[1].Rungs);
                Assert.Equal("Mixer_DB", call.Instance); Assert.Equal("FB_MixerRun", call.Box);
                Assert.Equal(new[] { "Start", "Stop" }, call.Inputs.Select(pin => pin.Name));
                Assert.Equal("Output", Assert.Single(call.Outputs).Parameter);
                Assert.Equal("%I0.0", editor.Networks[0].Rungs[0].Elements[0].Address);
                Assert.True(blocks.ViewLadderAtlas.IsEnabled);
                Capture("project-lad-visual");
                var toolbar = Assert.Single(WorkbenchRenderFeaturePagesTests.Descendants<Controls.PageHeadPanel>(blocks.LadderToolbar));
                Assert.Equal(width == 1024, toolbar.IsWrapped);
                UnifiedDesktopTests.ClickControl(blocks.XmlTab);
                Assert.True(blocks.CodePanel.Visibility == Visibility.Visible);
                Assert.Contains("Output", editor.TextLines[1]);
                OpenBlock("MCPVerify_FC_LAD_v3");
                dialogs.Files = [Path.Combine(McpOperatorFixture.RepositoryRoot, "templates", "mcp-full-e2e-verify", "plc", "blocks",
                    "MCPVerify_FC_LAD_v3.xml")];
                WpfContext.Complete(editor.OpenXmlAsync());
                Assert.StartsWith("<?xml", editor.TextLines[0]);
                Assert.Contains(editor.Members, member => member.Name == "CmpA");
                Assert.Equal("MCPVerify_FC_LAD_v3.xml", editor.SourcePath);
                Capture("project-lad-xml");
                UnifiedDesktopTests.ClickControl(ops.TagsTab);
                Assert.Equal(3, editor.TagCount);
                Capture("project-tags");
                editor.SelectedDevice = client.Devices.Single(d => d.Category == "Hmi");
                Assert.Equal(ProjectDocument.Hmi, editor.Document);
                Assert.Equal(2, editor.Screens.Count);
                Capture("project-hmi");
                editor.SelectedDevice = client.Devices[0];
                editor.SelectedUnit = Assert.Single(client.Units);
                editor.Document = ProjectDocument.SoftwareUnit;
                Capture("project-su");
                OpenBlock("FB_MixerRun");
                foreach (string state in new[] { "running", "done", "failed" })
                {
                    blocks.SetAtlasService(new WorkbenchRenderProjectPagesTests.AtlasFixture(state));
                    var atlas = blocks.Atlas!;
                    var work = atlas.GenerateAsync(false);
                    WpfContext.Drain();
                    Assert.True(atlas.Visible);
                    Capture("project-atlas-" + state);
                    if (state == "running") { atlas.Cancel(); WpfContext.Complete(work); }
                }
                blocks.Atlas!.Dismiss();
                var busy = new TaskCompletionSource();
                var operation = model.Activity.Guarded("Status.Compiling", () => busy.Task);
                Assert.False(ops.SaveButton.IsEnabled);
                Assert.False(ops.ToolbarCompile.IsEnabled);
                Assert.False(editor.Compile.CanExecute(null));
                Capture("state-busy");
                busy.SetResult(); WpfContext.Complete(operation);

                window.Navigate("VersionControl");
                model.VersionControl.VcStatus.Execute(null);
                model.VersionControl.SelectedVcItem = Assert.Single(model.VersionControl.VcStatusItems);
                WpfContext.Drain();
                Assert.NotEmpty(model.VersionControl.VcDiffLines);
                Assert.True(model.VersionControl.ShowWorkspace);
                if (contrast)
                {
                    var list = window.VersionControlContent.MappedList;
                    var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem);
                    var file = WorkbenchRenderFeaturePagesTests.Descendants<TextBlock>(row)
                        .Single(text => text.Text == model.VersionControl.SelectedVcItem!.FilePath);
                    byte[] visible = RowPixels(row);
                    file.Opacity = 0;
                    Assert.False(visible.SequenceEqual(RowPixels(row)), "The selected mapped file name must be visible in high contrast.");
                    file.ClearValue(UIElement.OpacityProperty);
                }
                Capture("project-vci-normal");
                AssertCenteredColumn(window.VersionControlContent.VciColumn, Viewport(window.VersionControlContent.VciScroll), 920);
                var vc = model.VersionControl;
                vc.VcPush.Execute(null);
                Assert.True(vc.SyncResult?.DryRun);
                Capture("project-vci-preview");
                AssertCenteredColumn(window.VersionControlContent.VciColumn, Viewport(window.VersionControlContent.VciScroll), 900);
                var sync = new TaskCompletionSource<SyncResult>();
                client.SyncHandler = (_, _) => sync.Task;
                vc.VcDryRun = false;
                vc.VcPush.Execute(null);
                Assert.True(vc.Synchronizing);
                Capture("project-vci-syncing");
                AssertCenteredColumn(window.VersionControlContent.VciColumn, Viewport(window.VersionControlContent.VciScroll), 740);
                sync.SetResult(Sync(false)); WpfContext.Drain();
                Assert.Equal(2, vc.SyncResult?.Items.Count);
                Capture("project-vci-done");
                AssertCenteredColumn(window.VersionControlContent.VciColumn, Viewport(window.VersionControlContent.VciScroll), 780);
                client.Workspaces.Clear(); WpfContext.Complete(vc.VcRefreshAsync());
                Assert.False(vc.HasWorkspace);
                Capture("project-vci-no-ws");
                AssertCenteredColumn(window.VersionControlContent.VciColumn, Viewport(window.VersionControlContent.VciScroll), 640);

                window.Navigate("Engineering");
                var originalLogic = client.LogicHandler!;
                client.LogicHandler = (_, _) => Task.FromException<string>(new IOException("Block read timed out."));
                OpenBlock("Main"); Assert.True(editor.SourceFailed); Capture("state-error");
                var errorColumn = (Grid)blocks.SourceError.Template.FindName("ErrorColumn", blocks.SourceError);
                AssertCenteredColumn(errorColumn, blocks.BlockBackdrop, 720);
                var cachedBlocks = client.Blocks.ToArray();
                client.Blocks.Clear(); editor.Refresh.Execute(null);
                Assert.True(editor.EmptyBlockList);
                var cachedDevices = client.Devices.ToArray();
                client.Devices.Clear(); WpfContext.Complete(editor.LoadDevicesAsync(model.Session.ProjectName));
                Assert.True(editor.EmptyDeviceList); Assert.False(editor.Import.CanExecute(null)); Capture("state-empty");
                client.Devices.AddRange(cachedDevices);
                UnifiedDesktopTests.ClickControl(window.ProjectExplorer.ReloadEmptyTree); WpfContext.Drain();
                Assert.True(editor.HasDevices); Assert.False(editor.EmptyDeviceList);
                client.Blocks.AddRange(cachedBlocks); editor.Refresh.Execute(null);
                client.LogicHandler = async (plc, path) => "// Last source read from the attached project.\n" + await originalLogic(plc, path);
                OpenBlock("FB_MixerRun");
                client.Exit(); WpfContext.Drain();
                Assert.True(model.Session.LostConnection);
                Assert.Contains(editor.Snapshot!.Lines, line => line.Contains("Last source", StringComparison.Ordinal));
                Assert.False(editor.Compile.CanExecute(null));
                Capture("state-crashed", keepNotifications: true);
                Assert.Equal(Visibility.Visible, window.Toast.Visibility);
                AssertCenteredColumn(ops.SnapshotColumn, window.ProjectOverview, 920);
                Assert.Contains(model.SelectedReleaseKey, window.ToastTitle.Text);
                Assert.Equal(Loc.Current["Project.RescanProcesses"], window.ToastAction.Content);
                Assert.Equal(Loc.Current["Project.ViewLogs"], window.ToastSecondAction.Content);
                Assert.Same(window.FindResource("Ant.colorError"), window.Toast.BorderBrush);
                Assert.Same(window.FindResource("Ant.colorErrorBg"), window.SessionStatusButton.Background);
                Assert.Contains(Loc.Current["Shell.SessionLost"], window.SessionStatus.Text);
                UnifiedDesktopTests.ClickControl(window.ToastSecondAction); WpfContext.Drain();
                Assert.True(window.RailLog.IsChecked);
            }
            finally
            {
                window.Close();
                (themes.Theme, themes.HighContrastOverride, themes.Accent, App.Settings.LeftPaneOpen) = previous;
            }
        });
    }

    private static byte[] RowPixels(FrameworkElement row)
    {
        int width = (int)Math.Ceiling(row.ActualWidth), height = (int)Math.Ceiling(row.ActualHeight);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(row);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static void SaveState(FrameworkElement host, string view, int width, int height, AppTheme theme, AppLanguage language, bool contrast)
    {
        string? output = Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR");
        if (output == null) return;
        string palette = contrast ? "hc" : theme.ToString().ToLowerInvariant();
        string variant = palette + (language == AppLanguage.Chinese ? "-zh" : "-en") + (width == 1360 ? "" : $"-{width}x{height}");
        string dir = Directory.CreateDirectory(Path.Combine(output, "states")).FullName;
        WorkbenchRenderTests.SaveRender(host, Path.Combine(dir, view + "-" + variant + ".png"), width, height);
    }

    [Theory]
    [InlineData(1360, 812, AppTheme.Light, AppLanguage.Chinese, false)]
    [InlineData(1360, 812, AppTheme.Light, AppLanguage.English, false)]
    [InlineData(1360, 812, AppTheme.Dark, AppLanguage.Chinese, false)]
    [InlineData(1360, 812, AppTheme.Light, AppLanguage.Chinese, true)]
    [InlineData(1024, 640, AppTheme.Light, AppLanguage.Chinese, false)]
    public void Download_drawer_reviews_compile_save_readiness_plan_and_operator_confirmation(int width, int height, AppTheme theme,
        AppLanguage language, bool contrast)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var themes = ThemeManager.Current;
            var previous = (themes.Theme, themes.HighContrastOverride, themes.Accent, App.Settings.LeftPaneOpen);
            (themes.Theme, themes.HighContrastOverride, themes.Accent, App.Settings.LeftPaneOpen) = (theme, contrast, AppAccent.Cobalt, true);
            var session = new ProjectPageInteractionTests.DocumentSession((tool, args) => tool switch {
                "GetProjectTree" => """{"value":"PlcSoftware: PLC [softwarePath=Line/PLC]"}""",
                "ListPlcBlocks" => """{"items":[{"name":"Mixer","path":"Line/Mixer","typeName":"FB","isConsistent":true,"programmingLanguage":"SCL"}]}""",
                "ListPlcSoftwareUnits" => """{"evidence":{"records":[]}}""",
                "ListTransferRoutes" => """{"modes":[{"pcInterfaces":[{"name":"Fixture Ethernet"}]}]}""",
                "DescribePlcBlockLogic" => """{"readable":"#Ready := #Enable;"}""",
                "CompilePlcSoftware" => """{"state":"Success","errorCount":0,"warningCount":0}""",
                "SaveProject" => "{}",
                "CheckDownloadReadiness" => """{"ready":true,"issues":[]}""",
                "DownloadPlc" when Equals(args["dryRun"], true) => new JsonObject { ["planHash"] = new string('b', 64),
                    ["targetIdentity"] = new JsonObject { ["projectFile"] = ProjectPageInteractionTests.DocumentSession.Project } }.ToJsonString(),
                "DownloadPlc" => """{"downloadState":"Success"}""",
                _ => throw new InvalidOperationException(tool) });
            var dialogs = new FakeDialogService();
            var model = new MainViewModel(new McpProjectClient(session), dialogs);
            var window = new MainWindow(model, false, new Services.Stubs.ApprovalServiceStub());
            try
            {
                WpfContext.Complete(model.Session.ApplySharedStateAsync(session.State));
                window.Navigate("Engineering");
                var host = WorkbenchRenderTests.CreateHost(window, width, height);
                WorkbenchRenderTests.ApplyCurrentPalette(host);
                var editor = model.Engineering;
                window.BlocksContent.LocateControlBlock(Assert.Single(editor.Blocks));
                UnifiedDesktopTests.ClickControl(window.OperationsContent.RenderTab);
                window.OperationsContent.ShowDownloadOptions();
                editor.DownloadAddress = "192.0.2.4";
                WpfContext.Complete(editor.ReadDownloadAdaptersAsync());
                editor.DownloadAdapter = Assert.Single(editor.DownloadAdapters);
                Assert.True(editor.CanPreviewDownload);
                WpfContext.Drain(); host.UpdateLayout();
                Assert.Equal(Visibility.Visible, window.EngineeringPage.Visibility);
                Assert.Equal(Visibility.Visible, window.OperationsContent.Visibility);
                Assert.Equal(Visibility.Visible, window.OperationsContent.Options.DownloadOptions.Visibility);
                SaveState(host, "drawer-download", width, height, theme, language, contrast);

                int start = session.Calls.Count;
                WpfContext.Complete(editor.PreviewDownloadAsync());
                Assert.Equal(start, session.Calls.Count);
                dialogs.Result = DialogResult.OK;
                WpfContext.Complete(editor.PreviewDownloadAsync());
                Assert.True(editor.HasDownloadPlan);
                Assert.Equal(new[] { "CompilePlcSoftware", "CompilePlcSoftware", "SaveProject", "SaveProject", "CheckDownloadReadiness", "DownloadPlc" },
                    session.Calls.Skip(start).Select(c => c.Tool));
                Assert.True((bool)session.Calls.Last().Args["dryRun"]!);
                dialogs.Result = DialogResult.Cancel;
                WpfContext.Complete(editor.SubmitDownloadAsync(() => throw new InvalidOperationException("Unexpected close.")));
                Assert.Equal(start + 6, session.Calls.Count);
                editor.DownloadAddress = "192.0.2.5"; Assert.False(editor.HasDownloadPlan);
                dialogs.Result = DialogResult.OK;
                WpfContext.Complete(editor.PreviewDownloadAsync());
                WpfContext.Complete(editor.SubmitDownloadAsync(window.OperationsContent.HideOptions));
                Assert.False(window.OperationsContent.OptionsOpen);
                var applied = Assert.Single(session.Calls, call => call.Tool == "DownloadPlc" && Equals(call.Args["dryRun"], false));
                Assert.Equal("192.0.2.5", applied.Args["targetIpAddress"]);
            }
            finally
            {
                window.Close();
                (themes.Theme, themes.HighContrastOverride, themes.Accent, App.Settings.LeftPaneOpen) = previous;
            }
        });
    }

    private static void OpenProject(MainViewModel model, MainWindow window)
    {
        model.Session.Connect.Execute(null);
        WpfContext.Drain();
        window.Navigate("Engineering");
    }

    private static SyncResult Sync(bool preview) => new()
    {
        DryRun = preview, Direction = SyncDirection.ProjectToWorkspace, Synchronized = 1, SkippedEqual = 1,
        Items = [new() { Name = "Mixing/FB_MixerRun.xml", Outcome = preview ? "planned" : "completed" },
            new() { Name = "Main.xml", Outcome = "skipped" }]
    };

    private static FakeProjectClient Fixture()
    {
        var client = new FakeProjectClient
        {
            AttachedProject = new() { Name = "Mixing_Cell", Path = @"D:\Fixture\Mixing_Cell.ap21" },
            Processes = [new() { ProcessId = 4204, ProjectPath = @"D:\Fixture\Mixing_Cell.ap21", Release = "21", UiMode = "WithUserInterface" },
                new() { ProcessId = 4316, Release = "21", UiMode = "WithoutUserInterface" }],
            Units = [new("MixerMotion", "Cell.Motion", ["FB_Home"])], Screens = ["Overview", "Batch settings"],
            Members = [new("Input", "Start", "Bool", "Start mixing"), new("Input", "Stop", "Bool", "Stop mixing"),
                new("Output", "Running", "Bool", "Drive enable")],
            LogicHandler = (_, path) => Task.FromResult(path == "Main"
                ? "── 程序段 1 · Mixer enable  [LAD]\n   Output ( )  ⇐  Start · /Stop · Healthy\n\n"
                    + "── 程序段 2  [LAD]\n   当 [Ready] 时: CALL \"FB_MixerRun\"[Mixer_DB](Start=Start, Stop=Stop, Running=Output)"
                : "── 程序段 1 · Mixer control  [SCL]\n   // Run the mixer until the stop input is set.\n   IF #Stop THEN\n"
                    + "       #Running := FALSE;\n       #Elapsed := T#0s;\n   ELSIF #Start THEN\n       #Running := TRUE;\n   END_IF;\n\n"
                    + "   IF #Running THEN\n       #Elapsed := #Elapsed + #Cycle;\n       IF #Elapsed >= #Duration THEN\n"
                    + "           #Running := FALSE;\n           #Done := TRUE;\n       END_IF;\n   END_IF;"),
            CompileResult = new() { State = "Warning", WarningCount = 1, Messages = [new() { Severity = CompileSeverity.Warning,
                Target = "Mixing/FB_MixerRun", Description = "Unused temporary variable." }] },
            DiffFixture = new() { Available = true, Lines = [
                new() { Kind = DiffLineKind.Context, Text = "  IF #Stop THEN" },
                new() { Kind = DiffLineKind.Removed, Text = "-    #Running := #Start;" },
                new() { Kind = DiffLineKind.Added, Text = "+    #Running := FALSE;" },
                new() { Kind = DiffLineKind.Added, Text = "+    #Elapsed := T#0s;" },
                new() { Kind = DiffLineKind.Context, Text = "  ELSIF #Start THEN" }] },
            SyncHandler = (_, preview) => Task.FromResult(Sync(preview))
        };
        client.ProcessLeases[4204] = ProjectLeaseState.Available;
        client.ProcessLeases[4316] = ProjectLeaseState.Held;
        client.HmiTagNames.AddRange(["Start", "Stop", "Running"]); client.AlarmNames.Add("Motor trip");
        client.Devices[0].TypeName = "CPU 1512SP-1 PN";
        client.Devices.Add(new() { Id = "HMI_Cell", Name = "HMI_Cell", Category = "Hmi", TypeName = "Comfort Panel" });
        client.Blocks.Add(new() { Name = "MCPVerify_FC_LAD_v3", Path = "Verification/MCPVerify_FC_LAD_v3",
            FolderPath = "Verification", Kind = BlockKind.FC, ProgrammingLanguage = "LAD", IsConsistent = true });
        client.Blocks[0].ProgrammingLanguage = "LAD";
        client.Blocks.Add(new() { Name = "FB_MixerRun", Path = "Mixing/FB_MixerRun", FolderPath = "Mixing",
            Kind = BlockKind.FB, Number = 24, ProgrammingLanguage = "SCL", IsConsistent = true });
        client.StatusItems.Add(new() { Name = "FB_MixerRun", FilePath = "Mixing/FB_MixerRun.xml", CompareState = VcCompareState.Unequal });
        foreach (var (name, address) in new[] { ("Start", "%I0.0"), ("Stop", "%I0.1"), ("Running", "%Q0.0") })
            client.Tags.Add(new() { Name = name, DataType = "Bool", LogicalAddress = address, TableName = "root/tag-tables/Mixer" });
        return client;
    }
}
