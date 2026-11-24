using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Gui.Views;
using TiaOpenness.Shared;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed partial class ProjectPageInteractionTests(WpfContext wpf)
{
    private sealed class TagSession(bool units, bool reject = false, string? reply = null, string? previewReply = null,
        string? release = null) : IMcpSessionClient
    {
        internal List<(string Tool, IReadOnlyDictionary<string, object?> Args)> Calls { get; } = [];
        public string? SessionKey => "fixture";
        public JsonObject? State => null;
        public JsonObject? ToolSchema(string tool) => units ? JsonNode.Parse("""{"properties":{"includeUnits":{"type":"boolean"}}}""")!.AsObject() : null;
        public event EventHandler<JsonObject>? StateChanged { add { } remove { } }
        public event EventHandler? Detached { add { } remove { } }
        public Task<JsonObject> CallAsync(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken token = default)
        {
            Calls.Add((tool, new Dictionary<string, object?>(arguments)));
            string json = reply ?? (tool switch
            {
                "ListPlcTagTables" => """{"ok":true,"data":{"items":["Inputs / local"]}}""",
                "ListPlcTags" when reject => """{"ok":false,"error":{"code":"INVALID_ARGUMENT","message":"Unknown table."}}""",
                "ListPlcTags" when units => """
                    {"ok":true,"data":{"items":[
                      {"name":"Ready/A","path":"units/Mix/tag-tables/Inputs/Ready%2FA","kind":"tag","dataType":"Bool","value":"%I0.0"},
                      {"name":"Speed","path":"root/tag-tables/Inputs/Speed","kind":"tag","dataType":"Real","value":""}]}}
                    """,
                "ListPlcTags" => """
                    {"ok":true,"data":{"items":[{"name":"Ready/A","path":"Inputs %2F local/Ready%2FA","kind":"tag","dataType":"Bool","value":"%I0.0"}]}}
                    """,
                _ => throw new InvalidOperationException(tool),
            });
            bool preview = tool == "CompilePlcSoftware" && Equals(arguments["dryRun"], true);
            if (preview) json = previewReply ?? """{"ok":true,"data":{"dryRun":true,"wouldCompile":true}}""";
            var envelope = JsonNode.Parse(json)!.AsObject();
            envelope["schemaVersion"] = 4;
            bool success = (bool)envelope["ok"]!;
            envelope["meta"] = new JsonObject { ["timestamp"] = "2026-11-20T00:00:00.0000000Z", ["releaseKey"] = release ?? (units ? "21" : "14sp1"),
                ["tool"] = tool, ["requestId"] = "request-1", ["outcome"] = success ? "succeeded" : "rejected-before-operation",
                ["execution"] = success ? "read-only" : "not-started", ["behaviorPolicy"] = "not-applicable",
                ["completeness"] = success ? "complete" : "none", ["paging"] = null, ["warnings"] = new JsonArray() };
            if (success) envelope["error"] = null;
            else if (reply == null)
            {
                envelope["data"] = null;
                envelope["error"]!["details"] = JsonNode.Parse(
                    """{"parameter":"table","parameters":["table"],"allowedValues":[],"expected":"exact table path"}""");
            }
            if (reply != null && tool == "CompilePlcSoftware")
            {
                bool completedWithErrors = (string?)envelope["error"]?["code"] == "COMPILE_ERRORS";
                if (success || completedWithErrors)
                {
                    envelope["meta"]!["execution"] = preview ? "read-only" : "completed";
                    envelope["meta"]!["outcome"] = success ? "succeeded" : "failed";
                }
            }
            _ = TiaOpenness.Core.EngineToolResult.Read(envelope.ToJsonString());
            return Task.FromResult(envelope);
        }
        public void CancelPending() { }
        public Task<IReadOnlyList<OperatorSession>> DiscoverAsync(string releaseKey, CancellationToken token = default) => throw new NotSupportedException();
        public Task CreateAsync(string releaseKey, CancellationToken token = default) => throw new NotSupportedException();
        public Task AttachAsync(OperatorSession session, CancellationToken token = default) => throw new NotSupportedException();
        public Task DetachAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Plc_tags_read_declaration_items_and_only_send_supported_arguments(bool units)
    {
        const string plc = "devices/Station/PLC_1";
        await using var session = new TagSession(units);
        using var client = new McpProjectClient(session);
        var tags = await client.ListPlcTagsAsync(plc, "", true);
        Assert.Equal(units ? new[] { "ListPlcTags" } : new[] { "ListPlcTagTables", "ListPlcTags" }, session.Calls.Select(call => call.Tool));
        var arguments = session.Calls.Last().Args;
        Assert.Equal(plc, arguments["plc"]);
        Assert.Equal(units ? "" : "Inputs %2F local", arguments["table"]);
        Assert.Equal(units, arguments.ContainsKey("includeUnits"));
        if (units) Assert.Equal(true, arguments["includeUnits"]);
        else Assert.Equal(plc, session.Calls[0].Args["softwarePath"]);
        Assert.Equal(units ? 2 : 1, tags.Count);
        Assert.Equal(("Ready/A", "Bool", "%I0.0"), (tags[0].Name, tags[0].DataType, tags[0].LogicalAddress));
        Assert.Equal(units ? "units/Mix/tag-tables/Inputs" : "Inputs %2F local", tags[0].TableName);
        if (units) Assert.Equal("", tags[1].LogicalAddress);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Plc_tag_error_keeps_the_host_error_and_requested_table(bool includeUnits)
    {
        await using var session = new TagSession(true, reject: true);
        using var client = new McpProjectClient(session);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListPlcTagsAsync("PLC_1", "Missing", includeUnits));
        Assert.Contains("INVALID_ARGUMENT", error.Message);
        Assert.Contains("Unknown table.", error.Message);
        var call = Assert.Single(session.Calls);
        Assert.Equal("ListPlcTags", call.Tool);
        Assert.Equal("Missing", call.Args["table"]);
        Assert.Equal(includeUnits, call.Args["includeUnits"]);
    }

    [Theory]
    [InlineData("21", true)]
    [InlineData("14sp1", false)]
    [InlineData("15.1", false)]
    public async Task Table_cells_show_decoded_names_and_expose_the_original_path(string release, bool units)
    {
        const string scopedRows = """
            {"ok":true,"data":{"items":[
              {"name":"Ready/A","path":"units/Mix%2FA/tag-tables/Signals/In%2FOut/Ready%2FA","kind":"tag","dataType":"Bool","value":"%I0.0"},
              {"name":"Speed","path":"root/tag-tables/Inputs/Speed","kind":"tag","dataType":"Real","value":""}]}}
            """;
        await using var session = new TagSession(units, reply: units ? scopedRows : null, release: release);
        using var client = new McpProjectClient(session);
        var tags = await client.ListPlcTagsAsync("devices/Station/PLC_1", "", true);
        wpf.Run(() =>
        {
            var view = new PlcTagsView();
            var grid = view.TagRows;
            grid.ItemsSource = tags;
            view.Measure(new Size(1000, 400));
            view.Arrange(new Rect(0, 0, 1000, 400));
            view.UpdateLayout();
            var expected = units ? new[] { "Mix/A / In/Out", "Inputs" } : new[] { "Inputs / local" };
            var paths = units ? new[] { "units/Mix%2FA/tag-tables/Signals/In%2FOut", "root/tag-tables/Inputs" } : new[] { "Inputs %2F local" };
            Assert.Equal(paths, tags.Select(tag => tag.TableName));
            Assert.Equal(expected.Length, tags.Count);
            for (int i = 0; i < tags.Count; i++)
            {
                var text = Assert.IsType<TextBlock>(grid.Columns[3].GetCellContent(tags[i]));
                Assert.Equal(expected[i], text.Text);
                var cell = Assert.IsType<DataGridCell>(text.Parent);
                Assert.Equal(tags[i].TableName, cell.ToolTip);
                Assert.Equal(tags[i].TableName, System.Windows.Automation.AutomationProperties.GetHelpText(cell));
            }
        });
    }

    [Fact]
    public void Tag_panel_clears_stale_rows_on_error_and_discards_reads_after_device_change()
    {
        wpf.Run(() =>
        {
            var client = new FakeProjectClient();
            using var model = new MainViewModel(client, new FakeDialogService());
            var engineering = model.Engineering;
            engineering.SelectedDevice = client.Devices[0];
            Assert.Null(engineering.TagCount);
            client.Tags.Add(new() { Name = "Ready", DataType = "Bool", LogicalAddress = "%I0.0", TableName = "Inputs" });
            WpfContext.Complete(engineering.ReadTagsAsync());
            Assert.Single(engineering.PlcTags);
            client.TagHandler = _ => Task.FromException<List<TagInfo>>(new InvalidOperationException("Unknown table."));
            WpfContext.Complete(engineering.ReadTagsAsync());
            Assert.Empty(engineering.PlcTags);
            Assert.Equal("Unknown table.", model.Activity.Status);
            Assert.Equal("Unknown table.", engineering.TagStatus);
            Assert.Null(engineering.TagCount);
            var late = new TaskCompletionSource<List<TagInfo>>();
            client.TagHandler = _ => late.Task;
            client.Calls.Clear();
            var read = engineering.ReadTagsAsync();
            engineering.SelectedDevice = new() { Name = "Next", Id = "PLC_2", Category = "Plc" };
            Assert.Equal("Tags", Assert.Single(client.Calls).Name);
            late.SetResult(client.Tags);
            WpfContext.Complete(read);
            Assert.Empty(engineering.PlcTags);
            Assert.Empty(engineering.TagStatus);
            Assert.Equal(new[] { "Tags", "Blocks" }, client.Calls.Select(call => call.Name));
            Assert.Equal("PLC_2", client.Calls[1].Arguments[0]);
        });
    }

    [Fact]
    public void Expanding_a_plc_loads_only_its_blocks_and_tree_nodes_open_the_existing_documents()
    {
        wpf.Run(() =>
        {
            var client = new FakeProjectClient { AttachedProject = new ProjectInfo { Name = "Line", Path = @"D:\Line.ap21" } };
            client.Devices.Add(new() { Id = "PLC_2", Name = "PLC_2", Category = "Plc" });
            client.Blocks[0].FolderPath = "Motion";
            client.Tags.Add(new() { Name = "Ready", TableName = "Inputs", DataType = "Bool", LogicalAddress = "%I0.0" });
            var model = new MainViewModel(client, new FakeDialogService());
            var window = new MainWindow(model, false);
            try
            {
                model.SelectedReleaseKey = "21";
                model.Session.Connect.Execute(null);
                var host = WorkbenchRenderTests.CreateHost(window, 1366, 860);
                var tree = ((ProjectTreeView)window.ProjectTree.Content).Nodes;
                Assert.Equal("PLC_1", Assert.Single(client.Calls, call => call.Name == "Blocks").Arguments[0]);
                var second = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(1);
                ClickNode((UIElement)second.Template.FindName("Switcher", second));
                WpfContext.Drain();
                host.UpdateLayout();
                Assert.Same(client.Devices[1], model.Engineering.SelectedDevice);
                Assert.Equal(new[] { "PLC_1", "PLC_2" }, client.Calls.Where(call => call.Name == "Blocks").Select(call => call.Arguments[0]));
                var nodes = WorkbenchRenderFeaturePagesTests.Descendants<TreeViewItem>(second).ToArray();
                var block = nodes.Single(item => item.Header is BlockNode { Row: not null });
                var row = model.Engineering.Blocks.Single();
                Assert.Contains(nodes, item => item.Header is BlockNode { Name: "Motion", IsFolder: true });
                ClickNode(block);
                Assert.False(row.Selected);
                Assert.Same(row, window.BlocksContent.ControlFocusedBlock);
                var check = WorkbenchRenderFeaturePagesTests.Descendants<CheckBox>(block).Single();
                UnifiedDesktopTests.ClickControl(check);
                Assert.True(row.Selected);
                ClickNode(nodes.Single(item => item.Header is ProjectLink { IsVersionControl: false }));
                WpfContext.Drain();
                host.UpdateLayout();
                Assert.Equal(ProjectDocument.Tags, model.Engineering.Document);
                Assert.Equal(new object?[] { "PLC_2", "", true }, Assert.Single(client.Calls, call => call.Name == "Tags").Arguments);
                var table = ((PlcTagsView)window.PlcTagsContent.Content).TagRows;
                Assert.True(table.IsReadOnly);
                Assert.Same(client.Tags[0], Assert.Single(table.Items.Cast<TagInfo>()));
                ClickNode(nodes.Single(item => item.Header is ProjectLink { IsVersionControl: true }));
                Assert.Equal("VersionControl", window.CurrentPage);
                Assert.True(window.RailEngineering.IsChecked);
                ClickNode(block);
                Assert.Equal("Engineering", window.CurrentPage);
                Assert.Equal(ProjectDocument.Render, model.Engineering.Document);
                Assert.True(row.Selected);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Tree_badges_use_block_language_and_current_workspace_differences()
    {
        wpf.Run(() =>
        {
            var model = ProjectPageTestSupport.Model(release: "21");
            var window = new MainWindow(model, false);
            try
            {
                var host = WorkbenchRenderTests.CreateHost(window, 1366, 860);
                WpfContext.Drain(); host.UpdateLayout();
                var nodes = WorkbenchRenderFeaturePagesTests.Descendants<TreeViewItem>((ProjectTreeView)window.ProjectTree.Content)
                    .Select(item => item.Header).OfType<BlockNode>().Where(node => node.Row != null).ToArray();
                Assert.Equal("F", nodes.Single(node => node.Name == "Safety_Door_FB").Kind);
                Assert.Equal(4, nodes.Count(node => node.IsModified));
                var main = nodes.Single(node => node.Name == "Main");
                Assert.False(main.IsModified);
                model.VersionControl.VcStatusItems.Clear();
                WpfContext.Drain();
                Assert.DoesNotContain(nodes, node => node.IsModified);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Block_selection_prefill_from_tags_reveals_the_rows_and_can_be_cleared_in_the_tree()
    {
        wpf.Run(() =>
        {
            var now = WorkbenchControlTests.TestNow;
            var (window, model, client) = WorkbenchControlTests.Window(input: new(() => now, () => false));
            try
            {
                model.Engineering.Document = ProjectDocument.Tags;
                var prefill = new WorkbenchPrefillArguments { Form = WorkbenchPrefillForm.BlockSelection,
                    Fields = new WorkbenchBlockSelectionFields { SoftwarePath = "PLC_1", BlockPaths = ["Main"] } };
                var result = window.ApplyControl(WorkbenchControlTests.Request(WorkbenchControlOperation.PrefillForm, prefill));
                Assert.Equal(WorkbenchControlStatus.Done, result.Status);
                WpfContext.Drain();
                Assert.Equal(ProjectDocument.Render, model.Engineering.Document);
                Assert.True(model.Engineering.Blocks[0].Selected);
                var marker = ((ProjectTreeView)window.ProjectTree.Content).SelectionMarker;
                Assert.Equal(Visibility.Visible, marker.Visibility);
                UnifiedDesktopTests.ClickControl(marker.Children.OfType<Button>().Single());
                Assert.True(model.Engineering.Blocks[0].Selected);
                now += TimeSpan.FromMilliseconds(500);
                UnifiedDesktopTests.ClickControl(marker.Children.OfType<Button>().Single());
                WpfContext.Drain();
                Assert.Equal(Visibility.Collapsed, marker.Visibility);
                Assert.False(model.Engineering.Blocks[0].Selected);
                Assert.Empty(client.Calls);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(AppLanguage.English, "Inspect", "Open rules…")]
    [InlineData(AppLanguage.Chinese, "审查", "打开规则…")]
    public void Rules_menu_opens_the_drawer_with_a_separate_inspect_command(AppLanguage language, string inspect, string openRules)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var model = ProjectPageTestSupport.Model();
            var window = new MainWindow(model, false);
            try
            {
                WpfContext.ShowWindow(window);
                var page = window.OperationsContent;
                UnifiedDesktopTests.ClickControl(page.ExportMenuButton);
                var menu = page.ExportMenuButton.ContextMenu;
                Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), entry => Equals(entry.Header, openRules));
                menu.IsOpen = false;
                UnifiedDesktopTests.ClickControl(page.RulesButton);
                WpfContext.Drain();
                Assert.Equal("Inspect", page.Options.CurrentSection);
                var run = Assert.Single(DesktopCapture.Descendants<Button>(page.Options), b => b.IsVisible && b.Command == model.Engineering.Inspect);
                Assert.Equal(inspect, run.Content);
                Assert.Equal(inspect, System.Windows.Automation.AutomationProperties.GetName(run));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Project_toolbar_save_is_the_only_accent_fill_and_runs_the_existing_confirmation()
    {
        wpf.Run(() =>
        {
            var client = new FakeProjectClient { AttachedProject = new ProjectInfo { Name = "Line", Path = @"D:\Line.ap21" } };
            var dialogs = new FakeDialogService();
            var model = new MainViewModel(client, dialogs);
            var window = new MainWindow(model, false);
            try
            {
                model.Session.Connect.Execute(null);
                window.Width = 1366; window.Height = 860;
                // The desktop can cap a native window's width. Keep the single-row layout at its design width.
                window.Root.Width = window.Width;
                WpfContext.ShowWindow(window); WpfContext.Drain();
                var host = window;
                var bar = (Border)ProjectPageTestSupport.Find(window, "ProjectToolbar");
                var allButtons = WorkbenchRenderFeaturePagesTests.Descendants<Button>(bar).ToList();
                Assert.Equal(8, allButtons.Count);
                Assert.Equal(new[] { window.OperationsContent.Reconnect, window.OperationsContent.BlockReviewButton },
                    allButtons.Where(button => !button.IsVisible));
                var buttons = allButtons.Where(button => button.IsVisible).ToList();
                Assert.Equal(6, buttons.Count);
                Assert.Contains(window.OperationsContent.DownloadButton, buttons);
                Assert.Equal(44, bar.ActualHeight);
                Assert.All(buttons, button => Assert.Equal(28, button.ActualHeight));
                UnifiedDesktopTests.ClickControl(window.OperationsContent.ExportMenuButton);
                var menu = window.OperationsContent.ExportMenuButton.ContextMenu;
                Assert.True(menu.IsOpen);
                foreach (string key in new[] { "Project.OpenConnect", "Common.Reload", "Pages.Import",
                    "Project.GenerateViewAtlas", "Project.ExportXml", "Project.OpenXml", "Primer.Transfer" })
                    Assert.Contains(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, Loc.Current[key]));
                menu.IsOpen = false;
                var save = Assert.Single(buttons, button => Equals(button.Background, host.FindResource("Primer.primaryBg")));
                Assert.Same(model.Engineering.Save, save.Command);
                client.Calls.Clear();
                UnifiedDesktopTests.ClickControl(save);
                Assert.Equal(DialogButtons.OKCancel, Assert.Single(dialogs.Messages).Buttons);
                Assert.Empty(client.Calls);
                dialogs.Result = DialogResult.OK;
                UnifiedDesktopTests.ClickControl(save);
                Assert.Equal("Save", Assert.Single(client.Calls).Name);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(1366)]
    public void Long_compile_results_keep_inspection_actions_reachable(int width)
    {
        wpf.Run(() =>
        {
            var model = ProjectPageTestSupport.Model(results: true);
            for (int i = 0; i < 50; i++) model.Activity.AppendDiagnostic("Block_" + i, "Compile warning with local diagnostic evidence", Services.WorkbenchActivity.Severity.Warning);
            model.Activity.AppendLocalized("Status.CompileResult", "Warning", 0, 50, "1.4");
            var window = new MainWindow(model, false);
            try
            {
                window.Navigate("Engineering");
                var host = WorkbenchRenderTests.CreateHost(window, width, 640);
                var side = (EngineeringSideView)window.FindName("EngineeringSideContent");
                var scroll = (ScrollViewer)side.FindName("ResultScroll");
                var inspect = (Button)side.FindName("InspectButton");
                inspect.BringIntoView(); WpfContext.Drain(); host.UpdateLayout(); WpfContext.Drain();
                Assert.True(inspect.IsEnabled);
                Assert.InRange(inspect.TranslatePoint(new Point(), scroll).Y, 0, scroll.ActualHeight);
                Assert.InRange(inspect.TranslatePoint(new Point(0, inspect.ActualHeight), scroll).Y, 0, scroll.ActualHeight);
                Assert.Equal(double.NaN, ((Border)side.FindName("CompileCard")).Height);
                Assert.Equal(double.NaN, ((Border)side.FindName("InspectionCard")).Height);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Environment_warning_navigates_to_the_existing_shell_page()
    {
        wpf.Run(() =>
        {
            var window = new MainWindow(ProjectPageTestSupport.Model(), false);
            try
            {
                window.ConfigureFeaturePages(new FeaturePageFixtures.Journal(), new FeaturePageFixtures.Audit(), new FeaturePageFixtures.Environment());
                WpfContext.Drain();
                WpfContext.ShowWindow(window);
                var menuButton = window.OperationsContent.ExportMenuButton;
                UnifiedDesktopTests.ClickControl(menuButton);
                Assert.True(menuButton.ContextMenu.IsOpen);
                string[] expected = ["Primer.Transfer", "Pages.Import", "Project.GenerateViewAtlas", "Project.ExportXml", "Project.OpenXml",
                    "Project.OpenConnect", "Common.Reload", "Pages.ViewLadder", "Pages.Options", "Pages.Disconnect"];
                Assert.Equal(expected.Select(key => Loc.Current[key]), menuButton.ContextMenu.Items.OfType<MenuItem>().Select(item => item.Header));
                menuButton.ContextMenu.IsOpen = false;
                Assert.True(window.Features.EnvironmentNeedsAction);
                Assert.False(window.RailEnvironment.IsChecked);
                var options = window.OperationsContent.Options;
                options.Show("Project");
                options.AdvancedProjectTools.IsExpanded = true;
                WpfContext.Drain();
                var item = options.RunEnvironmentItem;
                Assert.Equal(Visibility.Visible, item.Visibility);
                Assert.Equal(window.Features.EnvironmentAttention, item.ToolTip);
                Assert.True(item.IsEnabled);
                UnifiedDesktopTests.ClickControl(item);
                Assert.True(((RadioButton)window.FindName("RailEnvironment")).IsChecked);
            }
            finally { window.Close(); }
        });
    }

    private sealed class AtlasProbe : IAtlasService
    {
        public AtlasRequest? Request { get; private set; }
        public IProgress<AtlasProgress>? Progress { get; private set; }
        public CancellationToken Token { get; private set; }
        public TaskCompletionSource<AtlasResult> Completion { get; } = new();
        public Task<AtlasResult> GenerateAsync(AtlasRequest request, IProgress<AtlasProgress> progress, CancellationToken cancellationToken)
        {
            Request = request;
            Progress = progress;
            Token = cancellationToken;
            return Completion.Task;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"D:\fixture\Line.ap21")]
    public void Connect_button_chooses_the_existing_attach_or_open_command(string path)
    {
        wpf.Run(() =>
        {
            var client = new FakeProjectClient();
            var model = new MainViewModel(client, new FakeDialogService());
            var window = new MainWindow(model, false);
            try
            {
                model.Session.ProjectPath = path;
                UnifiedDesktopTests.ClickControl((Button)ProjectPageTestSupport.Find(window, "ConnectButton"));
                Assert.True(model.Session.IsConnected);
                Assert.Equal("Start", client.Calls[0].Name);
                Assert.Equal("Connect", client.Calls[1].Name);
                Assert.Equal(path.Length > 0, client.Calls.Any(call => call.Name == "OpenProject"));
                if (path.Length > 0) Assert.Equal(path, client.Calls.Single(call => call.Name == "OpenProject").Arguments[0]);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Disconnect_forwards_once_clears_project_presentations_and_preserves_log_history()
    {
        wpf.Run(() =>
        {
            var client = new FakeProjectClient { AttachedProject = new ProjectInfo { Name = "Line", Path = @"D:\Line.ap21" } };
            var model = new MainViewModel(client, new FakeDialogService());
            var window = new MainWindow(model, false);
            try
            {
                model.Session.Connect.Execute(null);
                model.Engineering.Compile.Execute(null); WpfContext.Drain();
                var results = (ResultPresentation)((FrameworkElement)window.FindName("Root")).Tag;
                Assert.True(results.HasCompile);
                window.Navigate("Blocks");
                string history = model.Activity.Log;
                client.Calls.Clear();
                model.Session.Disconnect.Execute(null);
                Assert.Equal("Disconnect", Assert.Single(client.Calls).Name);
                Assert.False(model.Session.IsConnected);
                Assert.Empty(model.Session.ProjectName);
                Assert.Empty(model.Engineering.Blocks);
                Assert.Null(model.Engineering.SelectedDevice);
                Assert.Empty(model.VersionControl.Workspaces);
                Assert.Null(model.VersionControl.SelectedWorkspace);
                Assert.False(results.HasCompile);
                Assert.Empty(results.Diagnostics);
                Assert.StartsWith(history, model.Activity.Log);
                Assert.False(model.Engineering.Compile.CanExecute(null));
                Assert.False(model.Session.Disconnect.CanExecute(null));
                Assert.True(((RadioButton)window.FindName("RailEngineering")).IsChecked);
                model.Session.Connect.Execute(null);
                Assert.True(model.Session.IsConnected);
                Assert.False(results.HasCompile);
                model.Activity.ClearLog();
                model.Engineering.Compile.Execute(null); WpfContext.Drain();
                Assert.True(results.HasCompile);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Rendering_the_focused_unchecked_block_preserves_the_transfer_selection()
    {
        wpf.Run(() =>
        {
            using var model = ProjectPageTestSupport.Model();
            var service = new AtlasProbe();
            using var atlas = new AtlasPresentation(model, service);
            var focused = model.Engineering.Blocks[2];
            var selection = model.Engineering.Blocks.Where(row => row.Selected).ToArray();
            Assert.False(focused.Selected);
            model.Engineering.FocusedBlock = focused;
            var pending = atlas.GenerateAsync(true);
            Assert.Equal(focused.Path, Assert.Single(service.Request!.BlockPaths));
            Assert.Equal(selection, model.Engineering.Blocks.Where(row => row.Selected));
            service.Completion.SetResult(new AtlasResult(null, "fixture failure"));
            WpfContext.Complete(pending);
        });
    }

    [Fact]
    public void Atlas_uses_selected_block_or_whole_plc_and_displays_progress_without_native_calls()
    {
        wpf.Run(() =>
        {
            using var model = ProjectPageTestSupport.Model();
            var service = new AtlasProbe();
            using var atlas = new AtlasPresentation(model, service);
            Assert.False(atlas.Visible);
            var pending = atlas.GenerateAsync(false);
            Assert.True(atlas.Running);
            Assert.False(atlas.CanGenerate);
            Assert.Equal(9, service.Request!.BlockPaths.Count);
            Assert.False(service.Request.SingleBlock);
            service.Progress!.Report(new AtlasProgress(3, 9, "Scale_Analog_FC"));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Assert.Equal(100.0 / 3, atlas.Percent, 5);
            Assert.Equal("Scale_Analog_FC", atlas.Detail);
            service.Completion.SetResult(new AtlasResult(null, "fixture failure"));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Assert.True(pending.IsCompletedSuccessfully);
            Assert.True(atlas.Failed);
            Assert.Equal("fixture failure", atlas.Detail);
            Assert.True(atlas.CanGenerate);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generated_html_opens_through_the_browser_boundary(bool singleBlock)
    {
        wpf.Run(() =>
        {
            using var model = ProjectPageTestSupport.Model();
            var service = new AtlasProbe();
            string? opened = null;
            using var atlas = new AtlasPresentation(model, service, path => opened = path);
            string html = Path.Combine(AppContext.BaseDirectory, "atlas-" + Guid.NewGuid().ToString("N") + ".html");
            try
            {
                File.WriteAllText(html, "<!doctype html><title>Test atlas</title>");
                _ = atlas.GenerateAsync(singleBlock);
                Assert.Equal(singleBlock, service.Request!.SingleBlock);
                Assert.Equal(singleBlock ? 1 : 9, service.Request.BlockPaths.Count);
                if (singleBlock) Assert.Equal(model.Engineering.Blocks[0].Path, service.Request.BlockPaths[0]);
                service.Completion.SetResult(new AtlasResult(html, null));
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Assert.True(atlas.Done);
                if (!singleBlock) { Assert.Null(opened); atlas.Open(); }
                Assert.Equal(html, opened);
                File.Delete(html);
                atlas.Open();
                Assert.True(atlas.Failed);
            }
            finally { if (File.Exists(html)) File.Delete(html); }
        });
    }

    [Fact]
    public void Disconnect_discards_late_atlas_results()
    {
        wpf.Run(() =>
        {
            using var model = ProjectPageTestSupport.Model();
            var service = new AtlasProbe();
            using var atlas = new AtlasPresentation(model, service);
            _ = atlas.GenerateAsync(false);
            model.Session.Disconnect.Execute(null);
            Assert.True(service.Token.IsCancellationRequested);
            service.Completion.SetResult(new AtlasResult(@"D:\obsolete.html", null));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Assert.False(atlas.Visible);
            Assert.False(atlas.CanGenerate);
            atlas.Dispose();
        });
    }
}
