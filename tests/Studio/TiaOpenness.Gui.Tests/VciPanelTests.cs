using TiaMcp.Engine.Tests.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.ViewModels;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class VciPanelTests(WpfContext wpf)
{
    [Theory]
    [InlineData("14sp1", "Vc.Unavailable")]
    [InlineData("15.1", "Vc.Unavailable")]
    [InlineData("16", "Vc.ReadOnlyBridge")]
    [InlineData("17", "Vc.ReadOnlyBridge")]
    [InlineData("18", "Vc.ReadOnlyBridge")]
    [InlineData("19", "Vc.ReadOnlyBridge")]
    [InlineData("20", "Vc.ReaderUnavailable")]
    [InlineData("21", "Vc.ReaderUnavailable")]
    public void Fallback_notice_follows_the_bound_release_without_reading_the_project(string release, string expected)
    {
        wpf.Run(() =>
        {
            var session = new ProjectPageInteractionTests.DocumentSession((_, _) => throw new NotSupportedException())
                { Release = release, AdvertiseTools = false };
            using var project = new McpProjectClient(session);
            using var model = new VersionControlViewModel(new McpVersionControlClient(project), new FakeDialogService(), new WorkbenchActivity(), () => null);
            string displayName = TiaMcp.Versioning.TiaVersionCatalog.Get(release).DisplayName;
            if (expected == "Vc.Unavailable")
            {
                Assert.Contains(displayName, model.AccessNotice);
                Assert.Equal(Loc.Current.T(expected, displayName), model.AccessNotice);
            }
            else Assert.Equal(Loc.Current[expected], model.AccessNotice);
            Assert.Empty(session.Calls);
        });
    }

    [Fact]
    public void Background_VCI_reads_take_immutable_snapshots_on_the_owning_dispatcher()
    {
        wpf.Run(() =>
        {
            var session = new ProjectPageInteractionTests.DocumentSession((_, _) => throw new NotSupportedException()) { Release = "17" };
            using var bridge = new ReadOnlyVciBridge(session);
            var first = Task.Run(() => bridge.Snapshot(default));
            WpfContext.Complete(first);
            session.Release = "21"; session.Epoch++;
            var second = Task.Run(() => bridge.Snapshot(default));
            WpfContext.Complete(second);
            Assert.True(first.Result.Readable); Assert.Equal("17", first.Result.Release);
            Assert.False(second.Result.Readable); Assert.NotEqual(first.Result.Lease, second.Result.Lease);
            // Wait for the pool caller to return its task while this UI thread still owns the session.
            var issued = Task.Factory.StartNew(bridge.VcSupportedAsync, default, TaskCreationOptions.None, TaskScheduler.Default);
            var read = issued.WaitAsync(TestDeadlines.Hang).GetAwaiter().GetResult();
            Assert.False(read.IsCompleted);
            Assert.Contains("attached and idle", Assert.Throws<InvalidOperationException>(() => WpfContext.Complete(read)).Message);
        });
    }

    [Fact]
    public void Sync_default_previews_both_directions_and_run_keeps_overwrite_confirmation()
    {
        wpf.Run(() =>
        {
            var client = new FakeProjectClient { AttachedProject = new ProjectInfo { Name = "Line", Path = @"D:\Line.ap21" } };
            var dialogs = new FakeDialogService();
            var model = new MainViewModel(client, dialogs) { SelectedReleaseKey = "21" };
            var window = new MainWindow(model, false);
            try
            {
                model.Session.Connect.Execute(null);
                window.Navigate("VersionControl"); WpfContext.Complete(model.VersionControl.VcRefreshAsync());
                var host = WorkbenchRenderTests.CreateHost(window);
                var view = (Views.VersionControlView)window.FindName("VersionControlContent");
                var buttons = WorkbenchRenderFeaturePagesTests.Descendants<Button>(view).ToArray();
                var preview = buttons.Single(button => Equals(button.Content, Localization.Loc.Current["Pages.PreviewSync"]));
                var run = buttons.Single(button => Equals(button.Content, Localization.Loc.Current["Pages.RunSync"]));
                Assert.True(preview.IsDefault); Assert.False(run.IsDefault);
                client.Calls.Clear();
                UnifiedDesktopTests.ClickControl(preview); WpfContext.Drain();
                var call = Assert.Single(client.Calls, call => call.Name == "VcSync");
                Assert.Equal(SyncDirection.ProjectToWorkspace, call.Arguments[1]); Assert.Equal(true, call.Arguments[2]);
                client.Calls.Clear();
                UnifiedDesktopTests.ClickControl(run); WpfContext.Drain();
                Assert.Equal(false, Assert.Single(client.Calls, call => call.Name == "VcSync").Arguments[2]);
                UnifiedDesktopTests.ClickControl((RadioButton)view.FindName("ToProject")); WpfContext.Drain(); host.UpdateLayout();
                client.Calls.Clear();
                UnifiedDesktopTests.ClickControl(preview); WpfContext.Drain();
                call = Assert.Single(client.Calls, call => call.Name == "VcSync");
                Assert.Equal(SyncDirection.WorkspaceToProject, call.Arguments[1]); Assert.Equal(true, call.Arguments[2]);
                client.Calls.Clear();
                UnifiedDesktopTests.ClickControl(run); WpfContext.Drain();
                Assert.DoesNotContain(client.Calls, call => call.Name == "VcSync");
                Assert.NotEmpty(dialogs.Messages);
                dialogs.Result = DialogResult.OK;
                UnifiedDesktopTests.ClickControl(run); WpfContext.Drain();
                Assert.Equal(false, Assert.Single(client.Calls, call => call.Name == "VcSync").Arguments[2]);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("16", false)][InlineData("17", false)][InlineData("18", false)][InlineData("19", false)]
    [InlineData("16", true)][InlineData("17", true)][InlineData("18", true)][InlineData("19", true)]
    public async Task Registered_older_release_tools_use_MCP_reads_and_approval_gated_writes(string release, bool candidate)
    {
        await using (var reads = new McpOperatorFixture(release) { CandidatePolicies = candidate })
        {
            reads.BindProject(); await using var client = reads.Client();
            await client.AttachAsync(Assert.Single(await client.DiscoverAsync(release)));
            using var project = new McpProjectClient(client);
            var bridge = new ReadFixture(); using var vci = new McpVersionControlClient(project, bridge);
            Assert.True(vci.CanRead); Assert.True(vci.CanWrite); Assert.True(await vci.VcSupportedAsync());
            Assert.Equal("git", Assert.Single(await vci.VcListWorkspacesAsync()).Name);
            Assert.Equal("Main", Assert.Single((await vci.VcStatusAsync("git", false)).Items).Name);
            Assert.Equal(0, bridge.Reads); Assert.Equal(0, bridge.Writes);
        }
        foreach (string tool in new[] { "CreateVersionControlWorkspace", "ConnectProjectToWorkspace", "SynchronizeVersionControlWorkspace" })
        {
            await using var host = new McpOperatorFixture(release) { CandidatePolicies = candidate, HoldApproval = true };
            host.BindProject(); await using var client = host.Client();
            await client.AttachAsync(Assert.Single(await client.DiscoverAsync(release)));
            using var project = new McpProjectClient(client);
            var bridge = new ReadFixture(); using var vci = new McpVersionControlClient(project, bridge);
            ApprovalRequest? request = null; host.ApprovalRequested = r => request = r;
            Task pending = tool switch
            {
                "CreateVersionControlWorkspace" => vci.VcCreateWorkspaceAsync("git", host.Root),
                "ConnectProjectToWorkspace" => vci.VcMapProjectAsync("git", "Station", false),
                _ => vci.VcSyncAsync("git", SyncDirection.WorkspaceToProject, false),
            };
            await host.Pending.Task.WaitAsync(TestDeadlines.Hang);
            Assert.False(pending.IsCompleted); Assert.Equal(0, host.ExecutedWrites);
            Assert.Equal("workbench", request?.Actor); Assert.NotNull(request?.OperatorCallId);
            Assert.Contains(host.Calls, c => c.Name == tool);
            if (!candidate && tool == "CreateVersionControlWorkspace")
            {
                Assert.Equal(new[] { tool, tool }, host.Calls.Select(c => c.Name));
                Assert.Equal(new[] { true, false }, host.Calls.Select(c => (bool)c.Arguments["dryRun"]!));
                Assert.Equal(new string('a', 64), (string?)host.Calls.Last().Arguments["expectedPlanHash"]);
            }
            host.Approval.TrySetResult(true); await pending;
            Assert.Equal(1, host.ExecutedWrites); Assert.Equal(0, bridge.Reads); Assert.Equal(0, bridge.Writes);
        }
    }

    [Theory]
    [InlineData("20", false)] [InlineData("21", false)] [InlineData("20", true)] [InlineData("21", true)]
    public async Task Engine_reads_preserve_V4_receipts_and_populate_the_real_page(string release, bool candidate)
    {
        await using var host = new McpOperatorFixture(release) { CandidatePolicies = candidate };
        host.BindProject(); await using var client = host.Client(); await client.AttachAsync(Assert.Single(await client.DiscoverAsync(release)));
        wpf.Run(() =>
        {
            var project = new McpProjectClient(client);
            var model = new MainViewModel(project, new FakeDialogService());
            var window = new MainWindow(model, false, new ApprovalServiceStub(), new FeaturePageFixtures.Diagnostics());
            try
            {
                WpfContext.Complete(model.Session.ApplySharedStateAsync(host.Snapshot()));
                WpfContext.ShowWindow(window); window.Navigate("VersionControl");
                WpfContext.Complete(model.VersionControl.VcRefreshAsync());
                var projectItem = (RadioButton)window.FindName("RailEngineering");
                Assert.Equal(Visibility.Visible, projectItem.Visibility);
                Assert.True(projectItem.IsChecked);
                Assert.Equal("VersionControl", window.CurrentPage);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("VersionControlContent")).Visibility);
                Assert.Equal("git", Assert.Single(model.VersionControl.Workspaces).Name);
                Assert.Equal("Main", Assert.Single(model.VersionControl.VcStatusItems).Name);
                Assert.True(model.VersionControl.VcPull.CanExecute(null));
                Assert.Contains(project.Receipts, r => r.Tool == "GetVersionControlStatus" && r.Json.Contains("\"schemaVersion\": 4"));
            }
            finally { window.Close(); }
        });
        Assert.Contains(host.Calls, c => c.Name == "ListVersionControlWorkspaces");
        Assert.Null(host.Start);
    }

    public static IEnumerable<object[]> Writes()
    {
        foreach (string release in new[] { "20", "21" })
            foreach (bool candidate in new[] { false, true })
                foreach (string tool in new[] { "CreateVersionControlWorkspace", "ConnectProjectToWorkspace", "SynchronizeVersionControlWorkspace" })
                    yield return [release, candidate, tool];
    }

    [Fact]
    public async Task Workbench_qualifies_stored_equal_in_status_and_sync()
    {
        const string refusal = "The file is deleted";
        await using var host = new McpOperatorFixture("17") { VciRefreshMessage = refusal };
        host.BindProject();
        await using var client = host.Client();
        await client.AttachAsync(Assert.Single(await client.DiscoverAsync("17")));
        wpf.Run(() =>
        {
            using var project = new McpProjectClient(client);
            using var model = new MainViewModel(project, new FakeDialogService());
            WpfContext.Complete(model.Session.ApplySharedStateAsync(host.Snapshot()));
            var vc = model.VersionControl;
            WpfContext.Complete(vc.VcRefreshAsync());
            Assert.True(vc.HasStoredEqual);
            Assert.Empty(vc.VcStatusItems);
            vc.VcShowAll = true;
            WpfContext.Complete(vc.VcRefreshAsync());
            var row = Assert.Single(vc.VcStatusItems);
            Assert.Equal(VcCompareState.Equal, row.CompareState);
            Assert.Equal(refusal, row.RefreshMessage);
            Assert.Contains(refusal, TiaOpenness.Contracts.Rpc.BridgeJson.Serialize(row));
            Assert.DoesNotContain("RefreshMessage", TiaOpenness.Contracts.Rpc.BridgeJson.Serialize(new MappedObjectInfo()));
            Assert.DoesNotContain("RefreshMessage", TiaOpenness.Contracts.Rpc.BridgeJson.Serialize(new SyncItem()));
            Assert.True(vc.HasStoredEqual);
            Assert.Equal(Loc.Current.T("Status.StoredEqual", 1), vc.StoredEqualNotice);
            var converter = new Controls.DisplayValueConverter();
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            Assert.Equal(Loc.Current["Primer.StoredEqual"], converter.Convert(row, typeof(string), "compare", culture));
            Assert.Equal(vc.StoredEqualNotice + " " + refusal, converter.Convert(row, typeof(string), "compareDetail", culture));

            var finished = new TaskCompletionSource();
            vc.PropertyChanged += (_, _) => { if (vc.SyncResult != null) finished.TrySetResult(); };
            vc.VcDryRun = true;
            vc.VcPush.Execute(null);
            WpfContext.Complete(finished.Task.WaitAsync(TestDeadlines.Hang));
            Assert.Contains(vc.StoredEqualNotice, vc.SyncCounts);
            Assert.Equal(1, vc.SyncResult!.UnverifiedEqual);
            Assert.Equal(refusal, Assert.Single(vc.SyncRows).RefreshMessage);
        });
    }
    [Theory, MemberData(nameof(Writes))]
    public async Task Every_engine_write_previews_and_waits_for_normal_workbench_approval(string release, bool candidate, string tool)
    {
        await using var host = new McpOperatorFixture(release) { CandidatePolicies = candidate, HoldApproval = true };
        host.BindProject(); await using var client = host.Client(); await client.AttachAsync(Assert.Single(await client.DiscoverAsync(release)));
        using var project = new McpProjectClient(client);
        using var vci = new McpVersionControlClient(project);
        ApprovalRequest? request = null; host.ApprovalRequested = r => request = r;
        Task pending = tool switch
        {
            "CreateVersionControlWorkspace" => vci.VcCreateWorkspaceAsync("git", host.Root),
            "ConnectProjectToWorkspace" => vci.VcMapProjectAsync("git", "Station", false),
            _ => vci.VcSyncAsync("git", SyncDirection.WorkspaceToProject, false),
        };
        await host.Pending.Task.WaitAsync(TestDeadlines.Hang);
        Assert.False(pending.IsCompleted); Assert.Equal(0, host.ExecutedWrites);
        Assert.Equal("workbench", request?.Actor); Assert.NotNull(request?.OperatorCallId);
        if (candidate)
        {
            Assert.Equal(new[] { "preview", "preview", "apply" }, host.Calls.Select(c => (string?)c.Arguments["mode"]));
            Assert.Equal(new string('a', 64), (string?)host.Calls.Last().Arguments["expectedPlanHash"]);
            Assert.StartsWith("vci/", (string?)host.Calls.Last().Arguments["route"]);
        }
        else if (tool == "CreateVersionControlWorkspace")
        {
            Assert.Equal(new[] { tool, tool }, host.Calls.Select(c => c.Name));
            Assert.Equal(new[] { true, false }, host.Calls.Select(c => (bool)c.Arguments["dryRun"]!));
            Assert.Equal(new string('a', 64), (string?)host.Calls.Last().Arguments["expectedPlanHash"]);
        }
        else Assert.Equal(new[] { true, false }, host.Calls.Select(c => (bool)c.Arguments["dryRun"]!));
        host.Approval.TrySetResult(true); await pending;
        Assert.Equal(1, host.ExecutedWrites);
        Assert.All(project.Receipts, r => Assert.Contains("\"schemaVersion\": 4", r.Json));
    }

    [Theory, InlineData("20"), InlineData("21")]
    public async Task Dry_run_is_only_preview_and_generation_fence_refusal_is_not_retried(string release)
    {
        await using var host = new McpOperatorFixture(release);
        host.BindProject(); await using var client = host.Client(); await client.AttachAsync(Assert.Single(await client.DiscoverAsync(release)));
        using var project = new McpProjectClient(client); using var vci = new McpVersionControlClient(project);
        await vci.VcSyncAsync("git", SyncDirection.ProjectToWorkspace, true);
        Assert.Equal(0, host.ExecutedWrites); Assert.True((bool?)Assert.Single(host.Calls).Arguments["dryRun"]);
        host.Calls.Clear(); host.GenerationRunActive = true;
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => vci.VcSyncAsync("git", SyncDirection.ProjectToWorkspace, false));
        Assert.Contains("PRECONDITION_FAILED", refusal.Message);
        Assert.Contains("generation-run-active", project.LastEnvelope);
        Assert.Equal(2, host.Calls.Count); Assert.Equal(0, host.ExecutedWrites);
    }
    [Theory, InlineData("20"), InlineData("21")]
    public async Task Invalid_generic_creation_preview_is_not_an_apply_permission(string release)
    {
        await using var host = new McpOperatorFixture(release) { MissingCreationPlanHash = true }; host.BindProject();
        await using var client = host.Client(); await client.AttachAsync(Assert.Single(await client.DiscoverAsync(release)));
        using var project = new McpProjectClient(client); using var vci = new McpVersionControlClient(project);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => vci.VcCreateWorkspaceAsync("git", host.Root));
        var preview = Assert.Single(host.Calls);
        Assert.Equal("CreateVersionControlWorkspace", preview.Name);
        Assert.True((bool?)preview.Arguments["dryRun"]);
        Assert.Equal(0, host.ExecutedWrites);
        Assert.False(host.Pending.Task.IsCompleted); Assert.Contains("plan hash", error.Message);
    }

    [Theory, InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19")]
    public async Task Older_real_window_registered_reads_and_writes_are_enabled_and_writes_require_approval(string release)
    {
        await using var host = new McpOperatorFixture(release);
        host.BindProject(); await using var client = host.Client(); await client.AttachAsync(Assert.Single(await client.DiscoverAsync(release)));
        var bridge = new ReadFixture(); Task? pending = null; ApprovalRequest? request = null;
        host.ApprovalRequested = r => request = r;
        wpf.Run(() =>
        {
            var project = new McpProjectClient(client); var vci = new McpVersionControlClient(project, bridge);
            var model = new MainViewModel(project, new FakeDialogService(), versionControl: vci);
            var window = new MainWindow(model, false, new ApprovalServiceStub(), new FeaturePageFixtures.Diagnostics());
            try
            {
                WpfContext.Complete(model.Session.ApplySharedStateAsync(host.Snapshot())); WpfContext.ShowWindow(window); window.Navigate("VersionControl");
                WpfContext.Complete(model.VersionControl.VcRefreshAsync());
                Assert.Single(model.VersionControl.Workspaces); Assert.True(model.VersionControl.VcStatus.CanExecute(null));
                Assert.False(model.VersionControl.VcCreate.CanExecute(null));
                model.VersionControl.NewWorkspaceFolder = host.Root;
                foreach (var command in new[] { model.VersionControl.VcCreate, model.VersionControl.VcMap, model.VersionControl.VcPush, model.VersionControl.VcPull })
                    Assert.True(command.CanExecute(null));
                host.HoldApproval = true;
                pending = vci.VcSyncAsync("git", SyncDirection.WorkspaceToProject, false);
                WpfContext.Complete(host.Pending.Task.WaitAsync(TestDeadlines.Hang));
                Assert.False(pending.IsCompleted); Assert.Equal(0, host.ExecutedWrites);
                Assert.Equal("workbench", request?.Actor); Assert.NotNull(request?.OperatorCallId);
                host.Approval.TrySetResult(true); WpfContext.Complete(pending);
                Assert.Equal(1, host.ExecutedWrites);
                var projectItem = (RadioButton)window.FindName("RailEngineering");
                Assert.Equal(Visibility.Visible, projectItem.Visibility);
                Assert.True(projectItem.IsChecked);
                Assert.Equal("VersionControl", window.CurrentPage);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("VersionControlContent")).Visibility);
            }
            finally { window.Close(); }
        });
        Assert.Equal(0, bridge.Reads); Assert.Equal(0, bridge.Writes);
        Assert.Contains(host.Calls, c => c.Name == "ListVersionControlWorkspaces");
        Assert.Contains(host.Calls, c => c.Name == "GetVersionControlStatus");
        Assert.Contains(host.Calls, c => c.Name == "SynchronizeVersionControlWorkspace");
    }

    [Theory, InlineData("14sp1"), InlineData("15.1")]
    public void Absent_API_disables_VCI_navigation_with_a_reason(string release) => wpf.Run(() =>
    {
        var (window, model, _) = WorkbenchControlTests.Window(connected: false);
        try
        {
            model.SelectedReleaseKey = release; model.Session.Connect.Execute(null); WpfContext.Drain();
            window.Navigate("Engineering");
            var projectItem = (RadioButton)window.FindName("RailEngineering");
            Assert.Equal(Visibility.Visible, projectItem.Visibility);
            Assert.True(projectItem.IsEnabled);
            Assert.False(model.VersionControlPageVisible);
            Assert.Equal(Visibility.Visible, window.Toast.Visibility);
            // Drain the successful connection and project-load results before exercising the navigation notice.
            while (window.Toast.Visibility == Visibility.Visible) UnifiedDesktopTests.ClickControl(window.ToastClose);
            window.Navigate("VersionControl"); Assert.False(model.IsVcTab);
            Assert.Equal("Engineering", window.CurrentPage);
            Assert.True(projectItem.IsChecked);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("VersionControlContent")).Visibility);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("Toast")).Visibility);
            Assert.Equal(Loc.Current["Shell.VersionControlUnavailable"], ((TextBlock)window.FindName("ToastMessage")).Text);
        }
        finally { window.Close(); }
    });

    private sealed class ReadFixture : IVersionControlClient
    {
        public int Reads, Writes;
        public bool CanRead => true;
        public bool CanWrite => false;
        public Task<bool> VcSupportedAsync() { Reads++; return Task.FromResult(true); }
        public Task<List<WorkspaceInfo>> VcListWorkspacesAsync() { Reads++; return Task.FromResult(new List<WorkspaceInfo> { new() { Name = "git", RootPath = "D:\\fixture" } }); }
        public Task<WorkspaceStatusReport> VcStatusAsync(string? name, bool changedOnly) { Reads++; return Task.FromResult(new WorkspaceStatusReport()); }
        public Task<WorkspaceDiff> VcDiffAsync(string? name, string? file) { Reads++; return Task.FromResult(new WorkspaceDiff()); }
        private InvalidOperationException Write() { Writes++; return new("Unexpected bridge write."); }
        public Task<WorkspaceInfo> VcCreateWorkspaceAsync(string name, string folderPath) => throw Write();
        public Task<MappingResult> VcMapProjectAsync(string? name, string? device, bool dryRun) => throw Write();
        public Task<SyncResult> VcSyncAsync(string? name, SyncDirection direction, bool dryRun) => throw Write();
        public void Dispose() { }
    }
}
