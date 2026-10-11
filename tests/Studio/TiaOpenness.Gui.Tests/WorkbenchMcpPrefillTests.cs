using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.ControlChannel;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Shared;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class WorkbenchMcpPrefillTests(WpfContext wpf)
{
    private static WorkbenchPrefillArguments Form(int form, bool clear = false) => new()
    {
        Form = (WorkbenchPrefillForm)form, Mode = clear ? WorkbenchPrefillMode.Clear : WorkbenchPrefillMode.Set,
        Fields = form switch
        {
            3 => new WorkbenchConnectProjectFields { ProjectPath = clear ? null : @"D:\Projects\Other.als21" },
            4 => new WorkbenchExportBlocksFields { OutputDirectory = clear ? null : @"D:\Exports", Format = clear ? null : "Source" },
            _ => new WorkbenchImportBlocksFields { Paths = clear ? null : [@"D:\Imports\Main.xml"], Overwrite = clear ? null : true },
        },
    };
    [Theory, InlineData(3), InlineData(4), InlineData(5)]
    public async Task Real_window_prefill_preserves_ownership_markers_and_clear_without_executing(int form)
    {
        await using var host = new McpOperatorFixture("21"); host.BindProject(); await using var client = host.Client();
        await client.AttachAsync(Assert.Single(await client.DiscoverAsync("21")));
        wpf.Run(() =>
        {
            var model = new MainViewModel(new McpProjectClient(client), new FakeDialogService());
            var window = new MainWindow(model, false, new ApprovalServiceStub(), new FeaturePageFixtures.Diagnostics());
            try
            {
                WpfContext.Complete(model.Session.ApplySharedStateAsync(host.Snapshot())); window.Show();
                window.ControlGuard = new(() => WorkbenchControlTests.TestNow, () => false, () => new Point(0, 0));
                host.Calls.Clear();
                var request = WorkbenchControlTests.Request(WorkbenchControlOperation.PrefillForm, Form(form));
                var response = window.ApplyControl(request); WpfContext.Drain();
                Assert.Equal(WorkbenchControlStatus.Done, response.Status); Assert.True(response.Data?.AwaitingConfirmation);
                Assert.Equal(Form(form).Form, window.ControlSurface.Snapshot.Read(new WorkbenchReadStateArguments()).Prefill?.Form);
                Assert.Equal(Visibility.Visible, ((StackPanel)window.FindName("ControlPrefillHint")).Visibility);
                Assert.Equal("prefill-pending", window.ApplyControl(WorkbenchControlTests.Request(WorkbenchControlOperation.PrefillForm, Form(form), "3333333333333333")).Refusal?.Condition);
                Assert.Empty(host.Calls); Assert.Equal(0, host.ExecutedWrites);
                if (form == 3) Assert.Equal(@"D:\Projects\Other.als21", model.Session.ProjectPath);
                if (form == 4) { Assert.Equal(@"D:\Exports", model.Engineering.OutputDirectory); Assert.True(model.Engineering.SourceFormat); }
                if (form == 5) { Assert.Equal(@"D:\Imports\Main.xml", model.Engineering.ImportPathsText); Assert.True(model.Engineering.ImportOverwrite); }
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(WorkbenchControlTests.Request(WorkbenchControlOperation.PrefillForm, Form(form, true))).Status);
                WpfContext.Drain(); Assert.Null(window.ControlSurface.Snapshot.Read(new WorkbenchReadStateArguments()).Prefill);
                Assert.Equal(Visibility.Collapsed, ((StackPanel)window.FindName("ControlPrefillHint")).Visibility);
                if (form == 3) Assert.Equal((string?)host.Snapshot()["binding"]?["projectFile"], model.Session.ProjectPath);
                if (form == 5) { Assert.Equal("", model.Engineering.ImportPathsText); Assert.False(model.Engineering.ImportOverwrite); }
                Assert.Empty(host.Calls);
            }
            finally { window.Close(); }
        });
    }
    public static IEnumerable<object[]> InvalidFields()
    {
        foreach (string path in new[] { "relative.ap21", @"D:\a.txt", @"D:Line.ap21", @"D:\a.ap", @"D:\a.ap21:stream", @"D:\a?.ap21" })
            yield return [3, path];
        foreach (string path in new[] { "relative", @"D:folder", @"D:\bad?" }) yield return [4, path];
        foreach (string path in new[] { "relative.xml", @"D:Main.xml", @"D:\Main.xml:stream" }) yield return [5, path];
    }
    [Theory, MemberData(nameof(InvalidFields))]
    public async Task Invalid_path_formats_are_refused_without_existence_probes(int form, string path)
    {
        var arguments = Form(form);
        if (arguments.Fields is WorkbenchConnectProjectFields connect) connect.ProjectPath = path;
        if (arguments.Fields is WorkbenchExportBlocksFields export) export.OutputDirectory = path;
        if (arguments.Fields is WorkbenchImportBlocksFields import) import.Paths = [path];
        Assert.False(PrefillRegistry.Valid(arguments));
        await using var host = new McpOperatorFixture("21"); host.BindProject(); await using var client = host.Client();
        await client.AttachAsync(Assert.Single(await client.DiscoverAsync("21")));
        wpf.Run(() =>
        {
            var model = new MainViewModel(new McpProjectClient(client), new FakeDialogService());
            var window = new MainWindow(model, false, new ApprovalServiceStub(), new FeaturePageFixtures.Diagnostics());
            try
            {
                WpfContext.Complete(model.Session.ApplySharedStateAsync(host.Snapshot())); window.Show();
                window.ControlGuard = new(() => WorkbenchControlTests.TestNow, () => false, () => new Point(0, 0));
                host.Calls.Clear();
                Assert.Equal("prefill-field-invalid", window.ApplyControl(WorkbenchControlTests.Request(WorkbenchControlOperation.PrefillForm, arguments)).Refusal?.Condition);
                Assert.Null(window.ControlSurface.Snapshot.Read(new WorkbenchReadStateArguments()).Prefill);
                Assert.Empty(host.Calls); Assert.Equal(0, host.ExecutedWrites);
            }
            finally { window.Close(); }
        });
    }
    [Theory, InlineData(@"Z:\NotPresent\Line.ap14"), InlineData(@"Z:\NotPresent\Line.als15"), InlineData(@"\\server\share\Line.ap21"),
        InlineData(@"Z:\NotPresent\Line.ap15_1"), InlineData(@"Z:\NotPresent\Line.als15_1")]
    public void Project_validation_accepts_absolute_project_or_local_session_format_only(string path)
        => Assert.True(PrefillRegistry.Valid(new() { Form = WorkbenchPrefillForm.ConnectProject, Fields = new WorkbenchConnectProjectFields { ProjectPath = path } }));

    [Fact]
    public async Task Registered_execution_commands_are_attributed_MCP_panel_commands()
    {
        await using var host = new McpOperatorFixture(); host.BindProject(); await using var client = host.Client();
        wpf.Run(() =>
        {
            using var model = new MainViewModel(new McpProjectClient(client), new FakeDialogService());
            foreach (var pair in new[] { (typeof(SessionViewModel), nameof(SessionViewModel.OpenProject), (object)model.Session.OpenProject),
                (typeof(EngineeringViewModel), nameof(EngineeringViewModel.Export), (object)model.Engineering.Export),
                (typeof(EngineeringViewModel), nameof(EngineeringViewModel.Import), (object)model.Engineering.Import) })
            {
                Assert.Equal("mcp", pair.Item1.GetProperty(pair.Item2)!.GetCustomAttribute<PrefillTargetAttribute>()!.Executes);
                Assert.IsType<McpPanelCommand>(pair.Item3);
            }
            Assert.All(PrefillRegistry.Targets, target => Assert.Contains(target.Executes, new[] { "none", "mcp" }));
        });
    }
    [Theory, InlineData(3), InlineData(4), InlineData(5)]
    public async Task Human_execution_of_prefilled_MCP_forms_previews_and_requires_the_normal_approval(int form)
    {
        await using var host = new McpOperatorFixture("21") { HoldApproval = true }; host.BindProject();
        await using var client = host.Client(); await client.AttachAsync(Assert.Single(await client.DiscoverAsync("21")));
        var approvals = new ApprovalServiceStub(); MainWindow? window = null; MainViewModel? model = null; AsyncCommand? command = null;
        host.ApprovalRequested = row => wpf.Run(() => approvals.Receive(row));
        try
        {
            wpf.Run(() =>
            {
                model = new MainViewModel(new McpProjectClient(client), new FakeDialogService());
                window = new MainWindow(model, false, approvals, new FeaturePageFixtures.Diagnostics());
                WpfContext.Complete(model.Session.ApplySharedStateAsync(host.Snapshot())); window.Show();
                window.ControlGuard = new(() => WorkbenchControlTests.TestNow, () => false, () => new Point(0, 0));
                var arguments = Form(form);
                if (arguments.Fields is WorkbenchExportBlocksFields export) { export.OutputDirectory = host.Root; export.Format = "SimaticMl"; }
                if (arguments.Fields is WorkbenchImportBlocksFields import) import.Paths = [Path.Combine(host.Root, "Main.xml")];
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(WorkbenchControlTests.Request(WorkbenchControlOperation.PrefillForm, arguments)).Status);
                host.Calls.Clear(); Assert.Equal(0, host.ExecutedWrites);
                command = form == 3 ? model.Session.OpenProject : form == 4 ? model.Engineering.Export : model.Engineering.Import;
                Assert.True(command.CanExecute(null)); command.Execute(null);
            });
            await host.Pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            wpf.Run(() =>
            {
                Assert.True(command!.IsRunning); Assert.Equal(0, host.ExecutedWrites);
                Assert.Equal("workbench", Assert.Single(approvals.Requests).Actor);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window!.FindName("ApprovalsContent")).Visibility);
                var calls = host.Calls.Where(c => c.Name == (form == 3 ? "OpenProject" : form == 4 ? "ExportPlcBlock" : "ImportPlcBlock")).ToArray();
                Assert.Equal(new[] { true, false }, calls.Select(c => (bool)c.Arguments["dryRun"]!));
                if (form == 3) Assert.All(calls, c => Assert.Equal(@"D:\Projects\Other.als21", (string?)c.Arguments["path"]));
                if (form == 4) Assert.All(calls, c => Assert.Equal(host.Root, (string?)c.Arguments["exportPath"]));
                if (form == 5) Assert.All(calls, c => { Assert.Equal(Path.Combine(host.Root, "Main.xml"), (string?)c.Arguments["importPath"]); Assert.True((bool?)c.Arguments["overwrite"]); });
                window.Features.Decide(Assert.Single(window.Features.Requests), true); host.Approval.SetResult(true);
            });
            for (int i = 0; i < 500 && wpf.Run(() => command!.IsRunning); i++) await Task.Delay(10);
            Assert.False(wpf.Run(() => command!.IsRunning)); Assert.Equal(1, host.ExecutedWrites);
            Assert.Contains("\"schemaVersion\": 4", wpf.Run(() => model!.LastEnvelope));
        }
        finally { if (window != null) wpf.Run(() => window.Close()); }
    }
    [Theory, InlineData(3), InlineData(4), InlineData(5)]
    public void Bridge_forms_are_refused_without_any_FakeStudioClient_calls(int form) => wpf.Run(() =>
    {
        var (window, _, studioClient) = WorkbenchControlTests.Window();
        try
        {
            Assert.IsAssignableFrom<IStudioClient>(studioClient);
            Assert.Equal("shared-mcp-session-required", window.ApplyControl(WorkbenchControlTests.Request(WorkbenchControlOperation.PrefillForm, Form(form))).Refusal?.Condition);
            Assert.Empty(studioClient.Calls);
        }
        finally { window.Close(); }
    });
}
