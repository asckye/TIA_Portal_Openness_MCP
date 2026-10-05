using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Contracts.Rpc;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.ViewModels;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class WorkbenchViewModelTests(WpfContext wpf)
{
    private static void Execute(AsyncCommand command)
    {
        Assert.True(command.CanExecute(null));
        command.Execute(null);
        // The fake replies synchronously, so a completed command must be enabled again.
        Assert.True(command.CanExecute(null));
    }

    private static void Call(FakeStudioClient.Invocation actual, string name, params object?[] arguments)
    {
        Assert.Equal(name, actual.Name);
        Assert.Equivalent(arguments, actual.Arguments, strict: true);
    }

    private static void SelectDevice(MainViewModel model, FakeStudioClient client)
    {
        model.Engineering.SelectedDevice = client.Devices[0];
        client.Calls.Clear();
    }

    [Theory]
    [InlineData(AppLanguage.English, DialogResult.Cancel)]
    [InlineData(AppLanguage.English, DialogResult.OK)]
    [InlineData(AppLanguage.Chinese, DialogResult.Cancel)]
    [InlineData(AppLanguage.Chinese, DialogResult.OK)]
    public void Save_requires_the_existing_confirmation(AppLanguage language, DialogResult result)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var client = new FakeStudioClient();
            var dialogs = new FakeDialogService { Result = result };
            using var model = new MainViewModel(client, dialogs);
            SelectDevice(model, client);
            Execute(model.Engineering.Save);
            Assert.Equal((Loc.Current["Dialog.Save.Text"], Loc.Current["Dialog.Save.Caption"],
                DialogButtons.OKCancel, DialogIcon.Warning), Assert.Single(dialogs.Messages));
            if (result == DialogResult.OK)
            {
                Call(Assert.Single(client.Calls), "Save");
                Assert.Equal(Loc.Current["Status.ProjectSaved"], model.Activity.Status);
            }
            else Assert.Empty(client.Calls);
            Assert.False(model.Busy);
        });
    }

    [Theory]
    [InlineData(AppLanguage.English, DialogResult.Cancel)]
    [InlineData(AppLanguage.English, DialogResult.Yes)]
    [InlineData(AppLanguage.English, DialogResult.No)]
    [InlineData(AppLanguage.Chinese, DialogResult.Cancel)]
    [InlineData(AppLanguage.Chinese, DialogResult.Yes)]
    [InlineData(AppLanguage.Chinese, DialogResult.No)]
    public void Import_preserves_cancel_and_both_overwrite_choices(AppLanguage language, DialogResult result)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var client = new FakeStudioClient();
            string[] files = [@"D:\input\First.xml", @"D:\input\Second.scl"];
            var dialogs = new FakeDialogService { Result = result, Files = files };
            using var model = new MainViewModel(client, dialogs);
            SelectDevice(model, client);
            Execute(model.Engineering.Import);
            Assert.Equal((Loc.Current["Dialog.Import.Title"], Loc.Current["Dialog.Import.Filter"], true), Assert.Single(dialogs.FileRequests));
            Assert.Equal((Loc.Current["Dialog.Import.Text"], Loc.Current["Dialog.Import.Caption"],
                DialogButtons.YesNoCancel, DialogIcon.Question), Assert.Single(dialogs.Messages));
            if (result == DialogResult.Cancel) Assert.Empty(client.Calls);
            else Assert.Collection(client.Calls,
                call => Call(call, "Import", "PLC_1", files, result == DialogResult.Yes),
                call => Call(call, "Blocks", "PLC_1"));
            Assert.False(model.Busy);
        });
    }

    [Fact]
    public void Cancelling_the_import_file_picker_never_asks_or_calls_the_bridge()
    {
        wpf.Run(() =>
        {
            var client = new FakeStudioClient();
            var dialogs = new FakeDialogService();
            using var model = new MainViewModel(client, dialogs);
            SelectDevice(model, client);
            Execute(model.Engineering.Import);
            Assert.Empty(dialogs.Messages);
            Assert.Empty(client.Calls);
        });
    }

    [Theory]
    [InlineData(AppLanguage.English, false, DialogResult.Cancel)]
    [InlineData(AppLanguage.English, false, DialogResult.OK)]
    [InlineData(AppLanguage.English, true, DialogResult.Cancel)]
    [InlineData(AppLanguage.Chinese, false, DialogResult.Cancel)]
    [InlineData(AppLanguage.Chinese, false, DialogResult.OK)]
    [InlineData(AppLanguage.Chinese, true, DialogResult.Cancel)]
    public void Pull_confirms_only_when_applying_and_refreshes_only_after_application(
        AppLanguage language, bool dryRun, DialogResult result)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var client = new FakeStudioClient();
            var dialogs = new FakeDialogService { Result = result };
            using var model = new MainViewModel(client, dialogs);
            Execute(model.VersionControl.VcRefresh);
            client.Calls.Clear();
            model.VersionControl.VcDryRun = dryRun;
            Execute(model.VersionControl.VcPull);
            if (dryRun) Assert.Empty(dialogs.Messages);
            else Assert.Equal((Loc.Current["Dialog.Pull.Text"], Loc.Current["Dialog.Pull.Caption"],
                DialogButtons.OKCancel, DialogIcon.Warning), Assert.Single(dialogs.Messages));
            if (!dryRun && result == DialogResult.Cancel) Assert.Empty(client.Calls);
            else if (dryRun) Call(Assert.Single(client.Calls), "VcSync", "git", SyncDirection.WorkspaceToProject, true);
            else Assert.Collection(client.Calls,
                call => Call(call, "VcSync", "git", SyncDirection.WorkspaceToProject, false),
                call => Call(call, "VcStatus", "git", true));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Session_preserves_attach_and_explicit_open_order(bool attach)
    {
        wpf.Run(() =>
        {
            var client = new FakeStudioClient { AttachedProject = new() { Name = "Attached", Path = @"D:\Attached.ap21" } };
            using var model = new MainViewModel(client, new FakeDialogService());
            model.SelectedReleaseKey = "15.1";
            model.Session.Headless = true;
            model.Session.ProjectPath = @"D:\Requested.ap15_1";
            Execute(attach ? model.Session.Connect : model.Session.OpenProject);
            var expected = new List<string> { "Start", "Connect" };
            if (!attach) expected.Add("OpenProject");
            expected.AddRange(["Devices", "Blocks", "VcSupported", "Workspaces", "VcStatus"]);
            Assert.Equal(expected, client.Calls.Select(c => c.Name));
            Call(client.Calls[0], "Start", false, "15.1");
            Call(client.Calls[1], "Connect", false);
            if (!attach) Call(client.Calls[2], "OpenProject", @"D:\Requested.ap15_1");
            Assert.Equal(attach ? @"D:\Attached.ap21" : @"D:\Requested.ap15_1", model.Session.ProjectPath);
            Assert.Equal(attach ? "Attached" : "Line", model.Session.ProjectName);
            Assert.Same(client.Devices[0], model.Engineering.SelectedDevice);
            Assert.True(model.Session.IsConnected);
            Assert.False(model.CanSelectRelease);
            model.SelectedReleaseKey = "20";
            Assert.Equal("15.1", model.SelectedReleaseKey);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Engineering_keeps_device_selection_export_format_and_refresh_sequence(bool source)
    {
        wpf.Run(() =>
        {
            var client = new FakeStudioClient();
            using var model = new MainViewModel(client, new FakeDialogService());
            var engineering = model.Engineering;
            Assert.False(engineering.Export.CanExecute(null));
            Assert.False(engineering.Save.CanExecute(null));
            SelectDevice(model, client);
            Assert.False(engineering.Export.CanExecute(null));
            engineering.OutputDirectory = @"D:\output";
            engineering.SourceFormat = source;
            engineering.SelectAll(true);
            Execute(engineering.Export);
            Execute(engineering.Compile);
            engineering.NamePattern = "^FB_";
            Execute(engineering.Inspect);
            engineering.NamePattern = " ";
            Execute(engineering.Inspect);
            Assert.Collection(client.Calls,
                call => Call(call, "Export", "PLC_1", new[] { "Main" }, @"D:\output", source ? ExportFormat.Source : ExportFormat.SimaticMl),
                call => Call(call, "Compile", "PLC_1"),
                call => Call(call, "Blocks", "PLC_1"),
                call => Call(call, "Inspect", "PLC_1", "^FB_"),
                call => Call(call, "Inspect", "PLC_1", null));
            engineering.SelectedDevice = null;
            Assert.False(engineering.Compile.CanExecute(null));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Vci_uses_the_current_device_and_preserves_preview_and_apply_calls(bool dryRun)
    {
        wpf.Run(() =>
        {
            var client = new FakeStudioClient();
            using var model = new MainViewModel(client, new FakeDialogService());
            var vc = model.VersionControl;
            Assert.False(vc.VcMap.CanExecute(null));
            Execute(vc.VcRefresh);
            Assert.False(vc.VcCreate.CanExecute(null));
            vc.NewWorkspaceName = "second";
            vc.NewWorkspaceFolder = @"D:\second";
            client.Calls.Clear();
            Execute(vc.VcCreate);
            Assert.Collection(client.Calls,
                call => Call(call, "VcCreate", "second", @"D:\second"),
                call => Call(call, "VcSupported"),
                call => Call(call, "Workspaces"),
                call => Call(call, "VcStatus", "git", true));
            Assert.Equal("second", vc.SelectedWorkspace!.Name);
            model.Engineering.SelectedDevice = new DeviceInfo { Id = "PLC_2", Name = "PLC_2", Category = "Plc" };
            vc.VcDryRun = dryRun;
            vc.VcShowAll = true;
            client.Calls.Clear();
            Execute(vc.VcMap);
            Execute(vc.VcPush);
            if (dryRun) Assert.Collection(client.Calls,
                call => Call(call, "VcMap", "second", "PLC_2", true),
                call => Call(call, "VcSync", "second", SyncDirection.ProjectToWorkspace, true));
            else Assert.Collection(client.Calls,
                call => Call(call, "VcMap", "second", "PLC_2", false),
                call => Call(call, "VcStatus", "second", false),
                call => Call(call, "VcSync", "second", SyncDirection.ProjectToWorkspace, false),
                call => Call(call, "VcStatus", "second", false));
        });
    }

    [Fact]
    public void Vci_capability_and_diff_selection_keep_their_enablement_and_order()
    {
        wpf.Run(() =>
        {
            var client = new FakeStudioClient { VcSupported = false };
            using var model = new MainViewModel(client, new FakeDialogService());
            var vc = model.VersionControl;
            Execute(vc.VcRefresh);
            Assert.Equal(new[] { "Start", "VcSupported" }, client.Calls.Select(c => c.Name));
            Assert.False(vc.VcPull.CanExecute(null));
            client.VcSupported = true;
            client.StatusItems.Add(new() { Name = "Main", FilePath = "Main.xml" });
            client.Calls.Clear();
            Execute(vc.VcRefresh);
            Assert.Equal(new[] { "VcSupported", "Workspaces", "VcStatus", "VcDiff" }, client.Calls.Select(c => c.Name));
            Call(client.Calls[^1], "VcDiff", "git", "Main.xml");
            Assert.True(vc.HasVcDiff);
            Assert.Equal("Main", vc.VcDiffCaption);
        });
    }

    [Theory]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Chinese)]
    public void Browse_routes_dialogs_to_the_owning_child_and_keeps_values_on_cancel(AppLanguage language)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var dialogs = new FakeDialogService { Files = [@"D:\Line.ap21"], Folder = @"D:\folder" };
            var client = new FakeStudioClient();
            using var model = new MainViewModel(client, dialogs);
            model.Session.BrowseProject();
            model.Engineering.BrowseOutput();
            model.VersionControl.BrowseWorkspaceFolder();
            Assert.Equal((Loc.Current["Dialog.OpenProject.Title"], Loc.Current["Dialog.OpenProject.Filter"], false), Assert.Single(dialogs.FileRequests));
            Assert.Equal(new[] { Loc.Current["Dialog.Export.Title"], Loc.Current["Dialog.Workspace.Title"] }, dialogs.FolderRequests);
            dialogs.Files = null;
            dialogs.Folder = null;
            model.Session.BrowseProject();
            model.Engineering.BrowseOutput();
            model.VersionControl.BrowseWorkspaceFolder();
            Assert.Equal(@"D:\Line.ap21", model.Session.ProjectPath);
            Assert.Equal(@"D:\folder", model.Engineering.OutputDirectory);
            Assert.Equal(@"D:\folder", model.VersionControl.NewWorkspaceFolder);
            Assert.Empty(client.Calls);
        });
    }

    [Fact]
    public async Task Shared_activity_keeps_busy_errors_progress_and_release_notifications()
    {
        var client = new FakeStudioClient();
        MainViewModel model = wpf.Run(() => new MainViewModel(client, new FakeDialogService
            { Files = ["bad.xml"], Result = DialogResult.Yes }));
        try
        {
            var gate = new TaskCompletionSource();
            Task operation = wpf.Run(() => model.Activity.Guarded("Status.Importing", () => gate.Task));
            wpf.Run(() =>
            {
                Assert.True(model.Busy);
                Assert.False(model.CanSelectRelease);
                client.EmitProgress(new ProgressPayload { Operation = "Import", Current = 1, Total = 2, Message = "fixture" });
                Assert.Equal(2, model.Activity.ProgressMax);
                Assert.Equal(1, model.Activity.ProgressValue);
                Assert.Equal("Import 1/2: fixture", model.Activity.Status);
            });
            gate.SetResult();
            await operation;
            wpf.Run(() =>
            {
                Assert.False(model.Busy);
                Assert.True(model.CanSelectRelease);
                Assert.Equal(0, model.Activity.ProgressMax);
                SelectDevice(model, client);
                client.ImportError = new InvalidOperationException("fixture failure");
                Execute(model.Engineering.Import);
                Assert.Equal("fixture failure", model.Activity.Status);
                Assert.Contains("fixture failure", model.Activity.Log);
                Assert.False(model.Busy);
                client.EmitLog("reader line");
                Assert.Contains("bridge: reader line", model.Activity.Log);
                Execute(model.Session.Connect);
                client.Exit();
                Assert.False(model.Session.IsConnected);
            });
        }
        finally { wpf.Run(model.Dispose); }
        Assert.True(client.Disposed);
    }

    [Fact]
    public void Language_changes_refresh_children_and_results_without_rewriting_history()
    {
        wpf.RunWithLanguage(AppLanguage.English, () =>
        {
            var client = new FakeStudioClient();
            using var model = new MainViewModel(client, new FakeDialogService());
            using var results = new GlassResults(model);
            SelectDevice(model, client);
            model.Engineering.SelectAll(true);
            Assert.Contains("1 selected", results.BlocksSummary);
            model.Activity.SetStatus("Status.ProjectSaved");
            model.Activity.Append("recorded in English");
            string log = model.Activity.Log;
            Loc.Current.Language = AppLanguage.Chinese;
            Assert.Equal(Loc.Current["Status.ProjectSaved"], model.Activity.Status);
            Assert.Equal(Loc.Current.T("Blocks.Selected", 1, 1), model.Engineering.SelectionSummary);
            Assert.Equal(Loc.Current["Vc.NoWorkspace"], model.VersionControl.WorkspaceRootDisplay);
            Assert.Contains("已选 1", results.BlocksSummary);
            Assert.Equal(log, model.Activity.Log);
        });
    }

    [Fact]
    public async Task Mock_startup_uses_the_real_client_and_composes_all_three_children()
    {
        MainViewModel model = wpf.Run(() => new MainViewModel(new StudioClient(), new FakeDialogService()));
        try
        {
            await wpf.Run(() => model.ApplyStartupAsync(["--mock", "--project", @"D:\fixture\Line.ap21", "--tab", "vc"]));
            wpf.Run(() =>
            {
                Assert.True(model.Session.UseMock);
                Assert.True(model.Session.IsConnected);
                Assert.True(model.IsVcTab);
                Assert.Equal(@"D:\fixture\Line.ap21", model.Session.ProjectPath);
                Assert.NotEmpty(model.Engineering.Devices);
                Assert.NotEmpty(model.Engineering.Blocks);
                Assert.True(model.VersionControl.VcSupported);
                Assert.False(model.Busy);
            });
        }
        finally { wpf.Run(model.Dispose); }
    }
}
