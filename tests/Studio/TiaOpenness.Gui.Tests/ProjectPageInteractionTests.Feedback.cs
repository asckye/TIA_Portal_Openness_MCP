using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaOpenness.Gui.Services;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.ViewModels;
using Xunit;

namespace TiaOpenness.Gui.Tests;

public sealed partial class ProjectPageInteractionTests
{
    [Fact]
    public void Cancelling_a_source_read_releases_the_busy_card_and_the_same_block_can_be_read_again()
    {
        wpf.Run(() =>
        {
            var pending = new TaskCompletionSource<string>();
            FakeProjectClient client = null!;
            var model = ProjectPageTestSupport.Model(configure: fake => { client = fake; fake.LogicHandler = (_, _) => pending.Task; });
            var window = new MainWindow(model, false);
            try
            {
                window.Navigate("Engineering");
                WpfContext.ShowWindow(window);
                var editor = model.Engineering;
                window.BlocksContent.LocateControlBlock(editor.Blocks.First(row => row.Language == "SCL"));
                var read = editor.ReadFocusedBlockAsync();
                WpfContext.Drain();
                Assert.True(window.BusyCard.IsVisible && window.CancelProjectRead.IsVisible);
                Assert.Equal(editor.SourcePath, editor.ReadPath);
                UnifiedDesktopTests.ClickControl(window.CancelProjectRead);
                WpfContext.Complete(read);
                Assert.False(model.Busy);
                Assert.False(editor.SourceFailed);
                Assert.Empty(editor.SourceLines);
                Assert.Equal(Loc.Current["Session.CancelledRead"], editor.SourceStatus);
                const string source = "// retry\nRETURN;\r\n";
                client.LogicHandler = (_, _) => Task.FromResult(source);
                WpfContext.Complete(editor.ReadFocusedBlockAsync());
                string? copied = null;
                window.BlocksContent.CopyText = text => copied = text;
                UnifiedDesktopTests.ClickControl(window.BlocksContent.CopySource);
                Assert.Equal(source, copied);
                Assert.False(window.BusyCard.IsVisible);
            }
            finally { pending.TrySetResult("obsolete source"); window.Close(); }
        });
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    public void Cancel_during_XML_export_waits_for_apply_and_shows_its_result(bool unit, bool fail, bool pausePreview)
    {
        wpf.Run(() =>
        {
            var dir = Directory.CreateTempSubdirectory("project-export-");
            string file = Path.Combine(dir.FullName, "MCPVerify_FC_LAD_v3.xml");
            var pending = new TaskCompletionSource();
            var tokens = new List<CancellationToken>();
            var session = new DocumentSession((tool, args) =>
            {
                if (tool != "ExportPlcBlock") return """{"items":[],"evidence":{"records":[]}}""";
                if (Equals(args["dryRun"], false)) File.Copy(Path.Combine(McpOperatorFixture.RepositoryRoot, "templates", "mcp-full-e2e-verify",
                    "plc", "blocks", Path.GetFileName(file)), file);
                return new JsonObject { ["outputFile"] = file }.ToJsonString();
            });
            session.BeforeResponse = (tool, args, token) =>
            {
                if (tool != "ExportPlcBlock") return Task.CompletedTask;
                tokens.Add(token);
                return Equals(args["dryRun"], pausePreview) ? pending.Task : Task.CompletedTask;
            };
            var model = new MainViewModel(new McpProjectClient(session), new FakeDialogService { Folder = dir.FullName });
            var window = new MainWindow(model, false);
            try
            {
                WpfContext.ShowWindow(window);
                var editor = model.Engineering;
                editor.SelectedDevice = new() { Category = "Plc", Name = "PLC", Id = "Line/PLC" };
                editor.FocusedBlock = new BlockRow(new() { Name = "MCPVerify_FC_LAD_v3", Path = "MCPVerify_FC_LAD_v3", ProgrammingLanguage = "LAD" });
                var export = unit ? editor.OpenUnitBlockAsync(new("Motion", "Line", ["MCPVerify_FC_LAD_v3"]), "MCPVerify_FC_LAD_v3")
                    : editor.OpenXmlAsync(true);
                WpfContext.Drain();
                Assert.True(window.CancelProjectRead.IsVisible);
                UnifiedDesktopTests.ClickControl(window.CancelProjectRead);
                Assert.False(export.IsCompleted);
                Assert.True(model.Busy);
                if (fail) session.FailureCode = "EXPORT_FAILED";
                pending.SetResult();
                WpfContext.Complete(export);
                Assert.Equal(new[] { CancellationToken.None, CancellationToken.None }, tokens);
                var exports = session.Calls.Where(call => call.Tool == "ExportPlcBlock").ToArray();
                Assert.Equal(new object?[] { true, false }, exports.Select(call => call.Args["dryRun"]));
                Assert.All(exports, call => Assert.Equal(unit, call.Args.ContainsKey("unitName")));
                Assert.False(model.Busy);
                Assert.NotEqual(Loc.Current["Session.CancelledRead"], model.Activity.Status);
                if (fail) Assert.Contains("EXPORT_FAILED", model.Activity.Status);
                else
                {
                    Assert.True(File.Exists(file));
                    Assert.Equal(Loc.Current.T("Toast.Exported", file), model.Activity.Status);
                    Assert.Equal(model.Activity.Status, window.ToastMessage.Text);
                }
            }
            finally { pending.TrySetResult(); window.Close(); dir.Delete(true); }
        });
    }

    [Theory]
    [InlineData("RenderPlcBlockDocument")]
    [InlineData("DescribePlcBlockLogic")]
    public async Task MCP_block_reads_that_export_a_temporary_file_do_not_cancel_the_native_step(string tool)
    {
        using var stop = new CancellationTokenSource();
        var session = new DocumentSession((_, _) => """{"readable":"RETURN;","evidence":{"markdown":"| Section | Name | Datatype |"}}""");
        CancellationToken observed = stop.Token;
        session.BeforeResponse = (_, _, token) =>
        {
            observed = token;
            stop.Cancel();
            return Task.CompletedTask;
        };
        using var client = new McpProjectClient(session);
        async Task Read()
        {
            if (tool == "RenderPlcBlockDocument") Assert.Empty(await client.ReadInterfaceAsync("PLC", "Main", stop.Token));
            else Assert.Equal("RETURN;", await client.DescribeBlockLogicAsync("PLC", "Main", stop.Token));
        }
        await Read();
        Assert.Equal(CancellationToken.None, observed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(Read);
        Assert.Equal(tool, Assert.Single(session.Calls).Tool);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mcp_atlas_stops_between_exports_and_finishes_a_started_HTML_write(bool stopDuringRender)
    {
        using var stop = new CancellationTokenSource();
        var session = new DocumentSession((tool, args) =>
        {
            if (tool == "ExportPlcBlock")
            {
                string file = Path.Combine((string)args["exportPath"]!, (string)args["blockPath"]! + ".xml");
                if (Equals(args["dryRun"], false)) File.Copy(Path.Combine(McpOperatorFixture.RepositoryRoot, "templates", "mcp-full-e2e-verify",
                    "plc", "blocks", "MCPVerify_FC_LAD_v3.xml"), file);
                return new JsonObject { ["outputFile"] = file }.ToJsonString();
            }
            Assert.Equal("RenderPlcProgramAtlas", tool);
            File.WriteAllText((string)args["outputPath"]!, "<!doctype html><title>Atlas</title>");
            return "{}";
        });
        var tokens = new List<CancellationToken>();
        session.BeforeResponse = (tool, args, token) =>
        {
            tokens.Add(token);
            if (stopDuringRender ? tool == "RenderPlcProgramAtlas" : tool == "ExportPlcBlock" && Equals(args["dryRun"], false)) stop.Cancel();
            return Task.CompletedTask;
        };
        using var client = new McpProjectClient(session);
        var generate = new McpAtlasService(client).GenerateAsync(new(DocumentSession.Project, "PLC", ["First", "Next"], false),
            new Progress<AtlasProgress>(), stop.Token);
        if (stopDuringRender)
        {
            var result = await generate;
            Assert.True(File.Exists(result.HtmlPath));
            Assert.Null(result.Error);
            Assert.Equal(new[] { "ExportPlcBlock", "ExportPlcBlock", "ExportPlcBlock", "ExportPlcBlock", "RenderPlcProgramAtlas" },
                session.Calls.Select(call => call.Tool));
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generate);
            Assert.Equal(new object?[] { true, false }, session.Calls.Select(call => call.Args["dryRun"]));
            Assert.All(session.Calls, call => Assert.Equal("First", call.Args["blockPath"]));
        }
        Assert.True(stop.IsCancellationRequested);
        Assert.All(tokens, token => Assert.Equal(CancellationToken.None, token));
    }

    private sealed class AtlasAttempts(bool writing = false) : IAtlasService
    {
        internal readonly List<AtlasRequest> Requests = [];
        internal TaskCompletionSource<AtlasResult> Result = new();
        internal IProgress<AtlasProgress> Progress = null!;
        public Task<AtlasResult> GenerateAsync(AtlasRequest request, IProgress<AtlasProgress> progress, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Progress = progress;
            Result = new();
            return writing ? Result.Task : Result.Task.WaitAsync(cancellationToken);
        }
    }

    [Fact]
    public void Cancelling_during_an_atlas_write_preserves_a_renderer_timeout()
    {
        wpf.Run(() =>
        {
            var model = ProjectPageTestSupport.Model();
            var window = new MainWindow(model, false);
            try
            {
                WpfContext.ShowWindow(window);
                var service = new AtlasAttempts(writing: true);
                window.BlocksContent.SetAtlasService(service);
                var atlas = window.BlocksContent.Atlas!;
                var generation = atlas.GenerateAsync(false);
                UnifiedDesktopTests.ClickControl(window.CancelAtlas);
                Assert.True(atlas.Running);
                Assert.False(generation.IsCompleted);
                service.Result.SetException(new OperationCanceledException("renderer timed out"));
                WpfContext.Complete(generation);
                Assert.True(atlas.Failed);
                Assert.Equal("renderer timed out", window.ToastMessage.Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Atlas_progress_cancels_and_a_sticky_failure_retries_only_unprotected_blocks()
    {
        wpf.Run(() =>
        {
            var model = ProjectPageTestSupport.Model(configure: client => client.Blocks[1].IsKnowHowProtected = true);
            TimeSpan now = TimeSpan.Zero;
            var window = new MainWindow(model, false, toastClock: () => now);
            try
            {
                window.Navigate("Engineering");
                WpfContext.ShowWindow(window);
                var service = new AtlasAttempts();
                string? opened = null;
                window.BlocksContent.SetAtlasService(service, path => opened = path);
                var atlas = window.BlocksContent.Atlas!;
                var first = atlas.GenerateAsync(false);
                var protectedPath = model.Engineering.Blocks[1].Path;
                service.Progress.Report(new(1, 9, protectedPath));
                WpfContext.Drain();
                Assert.True(window.AtlasBar.IsVisible && window.CancelAtlas.IsVisible);
                Assert.Equal(protectedPath, atlas.Detail);
                UnifiedDesktopTests.ClickControl(window.CancelAtlas);
                WpfContext.Complete(first);
                Assert.False(atlas.Visible);
                UnifiedDesktopTests.ClickControl(window.OperationsContent.GenerateAtlasItem);
                service.Result.SetResult(new(null, "protected source"));
                WpfContext.Drain();
                now += TimeSpan.FromMinutes(1);
                window.TickToast();
                Assert.Equal("protected source", window.ToastMessage.Text);
                UnifiedDesktopTests.ClickControl(window.ToastAction);
                Assert.True(atlas.Running);
                Assert.Equal(service.Requests[1].BlockPaths.Where(path => path != protectedPath), service.Requests[2].BlockPaths);
                string html = Path.Combine(McpOperatorFixture.RepositoryRoot, "tests", "FoundationHost", "TiaMcp.Engine.Tests",
                    "Fixtures", "PlcRender", "Parts", "BoxParts.xml.html");
                foreach (bool folder in new[] { false, true })
                {
                    if (folder) UnifiedDesktopTests.ClickControl(window.OperationsContent.GenerateAtlasItem);
                    service.Result.SetResult(new(html, null));
                    WpfContext.Drain();
                    UnifiedDesktopTests.ClickControl(folder ? window.ToastSecondAction : window.ToastAction);
                    Assert.Equal(folder ? Path.GetDirectoryName(html) : html, opened);
                }
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Atlas_open_failure_keeps_the_generated_path_and_retries_open_without_exporting(bool folder)
    {
        wpf.Run(() =>
        {
            using var model = ProjectPageTestSupport.Model();
            var service = new AtlasAttempts();
            ToastNotice? toast = null;
            var opened = new List<string>();
            string html = Path.Combine(McpOperatorFixture.RepositoryRoot, "tests", "FoundationHost", "TiaMcp.Engine.Tests",
                "Fixtures", "PlcRender", "Parts", "BoxParts.xml.html");
            using var atlas = new Views.AtlasPresentation(model, service, path =>
            {
                opened.Add(path);
                if (opened.Count == 1) throw new IOException("viewer unavailable");
            }, notice => toast = notice);
            var generate = atlas.GenerateAsync(false);
            service.Result.SetResult(new(html, null));
            WpfContext.Complete(generate);
            atlas.Open();
            Assert.True(atlas.Failed);
            Assert.Equal(html, atlas.Detail);
            Assert.NotNull(toast);
            Assert.True(toast.Sticky);
            Assert.Equal(Loc.Current["Project.AtlasOpenFailed"], atlas.Label);
            Assert.Equal(Loc.Current["Project.OpenFolder"], toast.ActionLabel.Resolve());
            Assert.Equal(Loc.Current["Project.RetryOpenAtlas"], toast.SecondActionKey.Resolve());
            (folder ? toast.Action : toast.SecondAction)!.Invoke();
            Assert.True(atlas.Done);
            Assert.Equal(new[] { html, folder ? Path.GetDirectoryName(html) : html }, opened);
            Assert.Single(service.Requests);
        });
    }

    [Fact]
    public void The_tag_scope_overflow_loads_the_entered_table()
    {
        wpf.Run(() =>
        {
            FakeProjectClient client = null!;
            var model = ProjectPageTestSupport.Model(configure: fake => client = fake);
            var window = new MainWindow(model, false);
            try
            {
                WpfContext.ShowWindow(window);
                window.Navigate("Engineering");
                UnifiedDesktopTests.ClickControl(window.OperationsContent.TagsTab);
                WpfContext.Drain();
                var editor = model.Engineering;
                var tags = (Views.PlcTagsView)window.PlcTagsContent.Content;
                UnifiedDesktopTests.ClickControl(tags.TableOptions);
                WpfContext.Drain();
                Assert.True(tags.TableMenu.IsOpen);
                tags.TablePath.Text = "root/tag-tables/Mixer";
                UnifiedDesktopTests.ClickControl(tags.ReloadTags);
                Assert.Equal("root/tag-tables/Mixer", editor.TagTable);
                tags.TableMenu.IsOpen = false;
                Assert.Contains(client.Calls, call => call.Name == "Tags" && Equals(call.Arguments[1], editor.TagTable));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Detailed_session_controls_open_from_the_status_popover_and_follow_its_lifetime()
    {
        wpf.Run(() =>
        {
            var session = new DocumentSession((_, _) => "{}");
            var model = new MainViewModel(new Services.McpProjectClient(session), new FakeDialogService());
            var window = new MainWindow(model, false);
            try
            {
                WpfContext.ShowWindow(window);
                UnifiedDesktopTests.ClickControl(window.SessionStatusButton);
                var popover = Assert.Single(window.Root.Children.OfType<Controls.SessionPopover>());
                Assert.Null(popover.SessionDetails.Content);
                popover.SessionDetailsLink.RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
                WpfContext.Drain();
                var details = Assert.IsType<Controls.ProjectSessionDetails>(popover.SessionDetails.Content);
                Assert.True(details.SessionPicker.IsVisible);
                Assert.Same(model.Panel!.Sessions, details.SessionPicker.ItemsSource);
                Assert.Same(details.SessionPicker, window.OperationsContent.FindName("SessionPicker"));
                UnifiedDesktopTests.ClickControl(window.SessionStatusButton);
                Assert.Null(popover.SessionDetails.Content);
                Assert.Empty(window.Root.Children.OfType<Controls.SessionPopover>());
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Enabled_rule_labels_and_unit_groups_follow_the_current_pattern_and_filter()
    {
        wpf.Run(() =>
        {
            using var model = ProjectPageTestSupport.Model(configure: client =>
                client.Units = [new("Motion", "Line.Motion", ["Home"]), new("Washing", "Area.Cleaning", ["Rinse"])]);
            var editor = model.Engineering;
            var device = Assert.Single(editor.ProjectDevices);
            Assert.Equal(2, Assert.Single(device.ProjectChildren.OfType<SoftwareUnitGroup>()).Units.Count);
            editor.BlockFilter = "Area.Cleaning";
            Assert.True(device.MatchesFilter);
            Assert.Equal("Washing", Assert.Single(Assert.Single(device.ProjectChildren.OfType<SoftwareUnitGroup>()).Units).Name);
            editor.BlockFilter = "Home";
            Assert.Equal("Motion", Assert.Single(Assert.Single(device.ProjectChildren.OfType<SoftwareUnitGroup>()).Units).Name);
            editor.BlockFilter = "Absent";
            Assert.Empty(device.ProjectChildren.OfType<SoftwareUnitGroup>());
            var expected = new[] { "Project.RuleNaming", "Project.RuleBuild", "Project.RuleProtection", "Project.RuleAuthors" };
            Assert.Equal(expected.Select(key => Loc.Current[key]), editor.EnabledRules);
            editor.NamePattern = "";
            Assert.Equal(expected.Skip(1).Select(key => Loc.Current[key]), editor.EnabledRules);
        });
    }

    [Fact]
    public void Block_review_opens_only_the_matching_pending_request()
    {
        wpf.Run(() =>
        {
            var model = ProjectPageTestSupport.Model(release: "21");
            var approvals = new ApprovalServiceStub();
            var window = new MainWindow(model, false, approvals, new FeaturePageFixtures.Diagnostics());
            try
            {
                WpfContext.ShowWindow(window);
                var button = window.OperationsContent.BlockReviewButton;
                var block = model.Engineering.Blocks.First();
                window.BlocksContent.LocateControlBlock(block);
                Assert.False(button.IsVisible);
                var match = new ApprovalRequest("matching", "MCP", "stdio", "local", "21", model.Session.OpenProjectFile,
                    model.Engineering.SelectedDevice!.Id, [], [], "", 120, DateTimeOffset.UtcNow.AddMinutes(2),
                    ApprovalState.Pending, new JsonObject { ["blockPath"] = block.Path }.ToJsonString());
                approvals.Receive(match with { Id = "other", Plc = "other" });
                Assert.False(button.IsVisible);
                approvals.Receive(match);
                Assert.True(button.IsVisible);
                Assert.Equal(Loc.Current["Project.PendingReview"], button.Content);
                foreach (int width in new[] { 1360, 1044, 1024 })
                {
                    window.Width = window.Root.Width = width;
                    window.UpdateLayout(); WpfContext.Drain();
                    var ops = window.OperationsContent;
                    double left = button.TranslatePoint(new Point(), ops.ProjectToolbar).X;
                    Assert.True(ops.CompileTab.TranslatePoint(new Point(ops.CompileTab.ActualWidth, 0), ops.ProjectToolbar).X <= left);
                    Assert.InRange(left + button.ActualWidth, left + 1, ops.ProjectToolbar.ActualWidth);
                }
                model.Engineering.FocusedBlock = model.Engineering.Blocks[1];
                Assert.False(button.IsVisible);
                model.Engineering.FocusedBlock = block;
                UnifiedDesktopTests.ClickControl(button);
                Assert.Equal("Approvals", window.CurrentPage);
                Assert.Equal(match.Id, window.Features.SelectedRequest!.Request.Id);
                Assert.Equal(2, approvals.PendingCount);
                window.Navigate("Engineering");
                approvals.Transition(match.Id, ApprovalState.TimedOut);
                Assert.False(button.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("21")]
    public void Offline_demo_does_not_display_the_release_unavailable_explanation(string release)
    {
        wpf.Run(() =>
        {
            using var model = new MainViewModel(new FakeProjectClient(), new FakeDialogService(), offlineDemo: true) { SelectedReleaseKey = release };
            Assert.False(model.VersionControlPageVisible);
            Assert.False(model.ShowVersionControlReleaseNote);
        });
    }

    [Theory]
    [InlineData("14sp1", true)]
    [InlineData("15.1", true)]
    [InlineData("21", false)]
    public void The_active_plc_tree_explains_unavailable_version_control(string release, bool unavailable)
    {
        wpf.Run(() =>
        {
            using var model = ProjectPageTestSupport.Model(release: release, configure: client =>
                client.Devices.Insert(0, new() { Name = "Operator panel", Id = "Panel", Category = "Hmi" }));
            var window = new MainWindow(model, false);
            try
            {
                window.Navigate("Engineering");
                WpfContext.ShowWindow(window);
                var tree = window.ProjectExplorer.Nodes;
                var plc = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0);
                Assert.Equal("Plc", Assert.IsType<DeviceNode>(plc.Header).Category);
                Assert.Equal("Hmi", Assert.IsType<DeviceNode>(tree.Items[1]).Category);
                plc.IsExpanded = true;
                WpfContext.Drain();
                tree.UpdateLayout();
                var link = Assert.Single(DesktopCapture.Descendants<TreeViewItem>(plc), item => item.Header is ProjectLink { IsVersionControl: true });
                var text = Assert.Single(DesktopCapture.Descendants<TextBlock>(link), item => item.Text == Loc.Current["Shell.VersionControlUnavailable"]);
                Assert.Equal(unavailable, text.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Unsupported_direction_is_explained_and_cannot_be_selected_by_focus_or_automation()
    {
        wpf.Run(() =>
        {
            var model = ProjectPageTestSupport.Model(release: "21");
            var window = new MainWindow(model, false);
            try
            {
                window.Navigate("VersionControl");
                WpfContext.ShowWindow(window);
                var view = window.VersionControlContent;
                Assert.False(view.ToProject.IsEnabled);
                Assert.False(view.ToProject.Focus());
                Assert.True(ToolTipService.GetShowOnDisabled(view.ToProject));
                Assert.Equal(Loc.Current["Project.UnsupportedDirection"], view.ToProject.ToolTip);
                var peer = new RadioButtonAutomationPeer(view.ToProject);
                var provider = Assert.IsAssignableFrom<ISelectionItemProvider>(peer.GetPattern(PatternInterface.SelectionItem));
                Assert.Throws<ElementNotEnabledException>(provider.Select);
                Assert.True(view.ToWorkspace.IsChecked);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Navigating_to_an_unread_MCP_workspace_twice_does_not_discover_or_read_it()
    {
        wpf.Run(() =>
        {
            var session = new DocumentSession((_, _) => """{"value":""}""");
            var model = new MainViewModel(new McpProjectClient(session), new FakeDialogService());
            var window = new MainWindow(model, false);
            try
            {
                WpfContext.Complete(model.Session.ApplySharedStateAsync(session.State));
                Assert.True(model.Session.IsConnected);
                Assert.False(model.VersionControl.HasStatus);
                session.Calls.Clear();
                for (int visit = 0; visit < 2; visit++)
                {
                    window.Navigate("VersionControl");
                    WpfContext.Drain();
                    Assert.Equal("VersionControl", window.CurrentPage);
                    Assert.Empty(session.Calls);
                    window.Navigate("Engineering");
                }
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Reopening_version_control_keeps_the_cached_status_and_issues_no_client_calls()
    {
        wpf.Run(() =>
        {
            FakeProjectClient client = null!;
            var model = ProjectPageTestSupport.Model(release: "21", configure: fake => client = fake);
            var window = new MainWindow(model, false);
            try
            {
                var vc = model.VersionControl;
                foreach (var equal in vc.VcStatusItems.Where(item => item.CompareState == VcCompareState.Equal).ToArray())
                    vc.VcStatusItems.Remove(equal);
                Assert.True(vc.HasStatus && vc.VcStatusItems.Count < vc.MappedCount);
                Assert.False(vc.VcShowAll);
                client.Calls.Clear();
                for (int visit = 0; visit < 2; visit++)
                {
                    window.Navigate("Engineering");
                    window.Navigate("VersionControl");
                    WpfContext.Drain();
                    Assert.True(model.IsVcTab);
                    Assert.Empty(client.Calls);
                    Assert.False(vc.VcShowAll);
                }
                var all = DesktopCapture.Descendants<RadioButton>(window.VersionControlContent.SyncCard).Single(button => button.Tag == null);
                UnifiedDesktopTests.ClickControl(all);
                Assert.True(vc.VcShowAll);
                Assert.Contains(client.Calls, call => call.Name == "VcStatus" && Equals(false, call.Arguments[1]));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Changing_the_workspace_in_options_refreshes_the_selected_workspace_and_filters_its_files()
    {
        wpf.Run(() =>
        {
            FakeProjectClient client = null!;
            var model = ProjectPageTestSupport.Model(release: "21", configure: fake =>
            {
                client = fake;
                client.Workspaces.Add(new() { Name = "Commissioning", RootPath = @"D:\Commissioning" });
                client.StatusItems.Add(new() { Name = "Missing", FilePath = "Missing.xml", CompareState = VcCompareState.WorkspaceFileMissing });
            });
            var window = new MainWindow(model, false);
            try
            {
                WpfContext.ShowWindow(window);
                window.Navigate("VersionControl");
                var view = window.VersionControlContent;
                UnifiedDesktopTests.ClickControl(view.WorkspaceOptionsButton);
                Assert.True(view.Options.IsOpen);
                client.Calls.Clear();
                view.Options.WorkspacePicker.SelectedIndex = 1;
                WpfContext.Drain();
                Assert.Equal("Commissioning", model.VersionControl.SelectedWorkspace!.Name);
                Assert.Contains(client.Calls, call => call.Name == "VcStatus" && Equals(call.Arguments[0], "Commissioning"));
                view.HideOptions();
                var filters = DesktopCapture.Descendants<RadioButton>(view.SyncCard).ToArray();
                UnifiedDesktopTests.ClickControl(filters.Single(button => Equals(button.Tag, "WorkspaceFileMissing")));
                Assert.Equal("Missing", Assert.Single(view.MappedList.Items.Cast<MappedObjectInfo>()).Name);
                UnifiedDesktopTests.ClickControl(filters.Single(button => Equals(button.Tag, "Unequal")));
                Assert.Equal(4, view.MappedList.Items.Count);
                UnifiedDesktopTests.ClickControl(filters.Single(button => button.Tag == null));
                Assert.Equal(client.StatusItems.Count, view.MappedList.Items.Count);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(AppLanguage.English, "1 to synchronize · 1 to skip")]
    [InlineData(AppLanguage.Chinese, "将同步 1 · 跳过 1")]
    public void Clean_preview_counts_describe_planned_work(AppLanguage language, string counts) => wpf.RunWithLanguage(language, () =>
    {
        using var model = ProjectPageTestSupport.Model(configure: client => client.SyncHandler = (direction, preview) =>
            Task.FromResult(new SyncResult { Direction = direction, DryRun = preview, Synchronized = 1, SkippedEqual = 1 }));
        model.VersionControl.VcPush.Execute(null);
        Assert.Equal(counts, model.VersionControl.SyncCounts);
    });

    [Theory]
    [InlineData(AppLanguage.English, "Sync", "Synchronized", "Skipped", "Failed", "Other result")]
    [InlineData(AppLanguage.Chinese, "同步", "已同步", "已跳过", "失败", "其他结果")]
    public void Preview_dispositions_use_reported_outcomes_and_unambiguous_workspace_state(AppLanguage language,
        string sync, string completed, string skipped, string failed, string other)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var model = ProjectPageTestSupport.Model(configure: client =>
            {
                client.StatusItems.Clear();
                client.StatusItems.Add(new() { Name = "Motion", FilePath = "Motion.xml", CompareState = VcCompareState.Unequal });
                client.StatusItems.Add(new() { Name = "New", FilePath = "New.xml", CompareState = VcCompareState.WorkspaceFileMissing });
                client.SyncHandler = (direction, preview) => Task.FromResult(new SyncResult
                {
                    Direction = direction, DryRun = preview, Synchronized = 2, SkippedEqual = 1, Failed = 1,
                    Items = preview ? [new() { Name = "Motion.xml", Outcome = "planned" }, new() { Name = "New", Outcome = "would sync ProjectToWorkspace" },
                        new() { Name = "Equal", Outcome = "skipped" }, new() { Name = "Unknown", Outcome = "failed", Error = "denied" }]
                        : [new() { Name = "Motion", Outcome = "synchronized" }, new() { Name = "New", Outcome = "completed" },
                            new() { Name = "Equal", Outcome = "skipped-equal" }, new() { Name = "Denied", Outcome = "failed", Error = "denied" },
                            new() { Name = "Unknown", Outcome = "VendorDisposition" }]
                });
            });
            using (model)
            {
                var vc = model.VersionControl;
                Assert.Equal(2, vc.MappedCount);
                vc.VcPush.Execute(null);
                Assert.Equal(new[] { "UPDATE", "CREATE", "SKIP", sync }, vc.SyncRows.Select(row => row.Outcome));
                Assert.Equal("denied", vc.SyncRows.Last().Error);
                Assert.Empty(vc.ElapsedLabel);
                Assert.Equal(language == AppLanguage.English ? "2 to synchronize · 1 to skip · preview errors: 1" : "将同步 2 · 跳过 1 · 预演错误 1", vc.SyncCounts);
                vc.VcDryRun = false;
                vc.VcPush.Execute(null);
                Assert.Equal(new[] { completed, completed, skipped, failed, other }, vc.SyncRows.Select(row => row.Outcome));
                Assert.Equal("VendorDisposition", vc.SyncRows.Last().RawOutcome);
                Assert.Equal("denied", vc.SyncRows.Single(row => row.Name == "Denied").Error);
                Assert.NotEmpty(vc.ElapsedLabel);
                Assert.Equal(Loc.Current.T("Project.SyncCounts", 2, 1, 1), vc.SyncCounts);
            }
        });
    }
}
