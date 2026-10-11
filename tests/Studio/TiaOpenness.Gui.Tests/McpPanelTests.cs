using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.ViewModels;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class McpPanelTests(WpfContext wpf)
{
    private static async Task Attach(McpSessionClient client, McpOperatorFixture host)
        => await client.AttachAsync(Assert.Single(await client.DiscoverAsync(host.Host.ReleaseKey)));

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Every_release_discovers_and_attaches_without_launch_and_detaches_without_disconnect(string release)
    {
        await using var host = new McpOperatorFixture(release); await using var client = host.Client();
        Assert.Empty(await client.DiscoverAsync(release == "19" ? "21" : "19"));
        var session = Assert.Single(await client.DiscoverAsync(release));
        Assert.Contains("unverified", session.DisplayName);
        await client.AttachAsync(session);
        Assert.Equal(host.Key, client.SessionKey);
        await client.CallAsync("GetProjectTree", new Dictionary<string, object?>());
        await client.DetachAsync();
        Assert.Null(client.SessionKey); Assert.DoesNotContain(host.Calls, c => c.Name == "DisconnectPortal");
        Assert.Null(host.Start); Assert.Equal(new[] { "list", "attach" }, host.Actions);
    }
    [Theory, InlineData("SaveProject"), InlineData("OpenProject"), InlineData("ExportPlcBlock"), InlineData("ImportPlcBlock"), InlineData("CompilePlcSoftware")]
    public async Task Every_write_previews_then_waits_for_a_separate_approval(string tool)
    {
        await using var host = new McpOperatorFixture { HoldApproval = true }; await using var client = host.Client(); await Attach(client, host);
        using var project = new McpProjectClient(client) { ExpectedProjectFile = @"D:\fixture\Line.ap19" };
        var args = tool switch { "OpenProject" => new Dictionary<string, object?> { ["path"] = @"D:\fixture\Other.ap19" },
            "ExportPlcBlock" => new() { ["softwarePath"] = "devices/Station/PLC_1", ["blockPath"] = "Main", ["exportPath"] = host.Root },
            "ImportPlcBlock" => new() { ["softwarePath"] = "devices/Station/PLC_1", ["groupPath"] = "", ["importPath"] = Path.Combine(host.Root, "Main.xml") },
            "CompilePlcSoftware" => new() { ["softwarePath"] = "devices/Station/PLC_1" }, _ => new() };
        var pending = project.Write(tool, args);
        await host.Pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted); Assert.Equal(0, host.ExecutedWrites);
        Assert.Equal(new[] { true, false }, host.Calls.Select(c => (bool)c.Arguments["dryRun"]!));
        Assert.Equal(new[] { false, true }, host.Calls.Select(c => (bool)c.Arguments["confirm"]!));
        Assert.All(host.Calls, c => Assert.Equal(project.ExpectedProjectFile, (string?)c.Arguments["expectedProjectFile"]));
        host.Approval.SetResult(true); await pending;
        Assert.Equal(1, host.ExecutedWrites); Assert.Contains("\"schemaVersion\": 4", project.LastEnvelope);
    }
    [Theory, InlineData(true), InlineData(false)]
    public async Task Failed_preview_or_stale_apply_is_rendered_without_replaying(bool preview)
    {
        await using var host = new McpOperatorFixture { RejectPreview = preview, RejectApply = !preview };
        await using var client = host.Client(); await Attach(client, host); using var project = new McpProjectClient(client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => project.SaveProjectAsync());
        Assert.Equal(preview ? 1 : 2, host.Calls.Count); Assert.Equal(0, host.ExecutedWrites);
        Assert.Contains(preview ? "PRECONDITION_FAILED" : "PLAN_STALE", project.LastEnvelope);
    }
    [Fact]
    public async Task Identity_failure_precedes_every_handshake_byte()
    {
        await using var host = new McpOperatorFixture();
        await using var client = new McpSessionClient(Directory.GetCurrentDirectory(), host.Root,
            (_, _) => throw new UnauthorizedAccessException("PID/SID/image mismatch"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.AttachAsync(new(host.Host, host.Key, host.Snapshot())));
        Assert.Empty(host.Actions); Assert.Empty(host.Calls);
        // Production checks also refuse this registration: its pipe name cannot match the computed identity.
        await using var production = new McpSessionClient(Directory.GetCurrentDirectory(), host.Root);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => production.AttachAsync(new(host.Host, host.Key, host.Snapshot())));
        Assert.Empty(host.Actions);
    }
    [Theory, InlineData("SESSION_NOT_FOUND"), InlineData("ACCESS_DENIED"), InlineData("UNSUPPORTED_VERSION")]
    public async Task Handshake_refusal_never_initializes_MCP(string code)
    {
        await using var host = new McpOperatorFixture { HandshakeError = code }; await using var client = host.Client();
        await Assert.ThrowsAsync<IOException>(() => client.AttachAsync(new(host.Host, host.Key, host.Snapshot())));
        Assert.Null(client.SessionKey); Assert.Empty(host.Calls);
    }
    [Fact]
    public async Task Wrong_session_identity_in_the_response_is_refused()
    {
        await using var host = new McpOperatorFixture { ReturnedKey = Guid.NewGuid().ToString("N") }; await using var client = host.Client();
        await Assert.ThrowsAsync<InvalidDataException>(() => client.AttachAsync(new(host.Host, host.Key, host.Snapshot())));
        Assert.Empty(host.Calls);
    }
    [Fact]
    public async Task Notifications_merge_by_revision_and_keep_endedBy_without_following_a_new_key()
    {
        await using var host = new McpOperatorFixture(); await using var client = host.Client(); await Attach(client, host);
        var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += (_, s) => { if ((bool?)s["sessionLocked"] == true) seen.TrySetResult(true); };
        await host.Notify(s => { s["sessionLocked"] = true; s["worker"]!["state"] = "poisoned"; s["lane"] = new JsonObject { ["tool"] = "CompilePlcSoftware", ["actor"] = "mcp" }; });
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var old = host.Snapshot(); old["revision"] = 1L; old["sessionLocked"] = false; client.Merge(old);
        Assert.True((bool?)client.State!["sessionLocked"]);
        var other = host.Snapshot(); other["sessionKey"] = Guid.NewGuid().ToString("N"); other["revision"] = 100L; client.Merge(other);
        Assert.Equal(host.Key, client.SessionKey);
        var detached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Detached += (_, _) => detached.TrySetResult(true);
        await host.Notify(s => { s["worker"]!["state"] = "disconnected"; s["endedBy"] = "mcp"; s["endReason"] = "session-ended"; });
        await detached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(client.SessionKey); Assert.Equal("mcp", (string?)client.State!["endedBy"]);
        Assert.DoesNotContain(host.Actions, a => a == "create");
    }
    [Fact]
    public async Task Owned_session_uses_operator_transport_and_releases_only_its_owned_host()
    {
        await using var host = new McpOperatorFixture(); await using var client = host.Client();
        await client.CreateAsync("19");
        Assert.Equal(new[] { "create" }, host.Actions);
        Assert.Contains("--operator-owner", host.Start!.ArgumentList); Assert.Contains("operator", host.Start.ArgumentList);
        Assert.False(host.Start.UseShellExecute); Assert.True(host.Start.CreateNoWindow);
        await client.DetachAsync(); Assert.True(host.Owner!.Disposed); Assert.DoesNotContain(host.Calls, c => c.Name == "DisconnectPortal");
    }
    [Fact]
    public async Task Failed_create_releases_its_started_host()
    {
        await using var host = new McpOperatorFixture { HandshakeError = "CREATE_NOT_ALLOWED" }; await using var client = host.Client();
        await Assert.ThrowsAsync<IOException>(() => client.CreateAsync("19"));
        Assert.True(host.Owner!.Disposed); Assert.Null(client.SessionKey); Assert.Empty(host.Calls);
    }
    [Theory, InlineData(false), InlineData(true)]
    public async Task Owned_session_waits_for_graceful_exit_on_detach_and_dispose(bool dispose)
    {
        await using var host = new McpOperatorFixture();
        var owner = new McpOperatorFixture.FakeOwnedHost(host.Host.Pid) { AutoExit = false };
        await using var client = host.Client(owner); await client.CreateAsync("19");
        var closing = dispose ? client.DisposeAsync().AsTask() : client.DetachAsync();
        await owner.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(client.SessionKey); Assert.False(closing.IsCompleted);
        Assert.False(owner.Disposed); Assert.False(owner.Forced);
        owner.Exited.TrySetResult(true);
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(owner.Disposed); Assert.False(owner.Forced);
        Assert.DoesNotContain(host.Calls, call => call.Name == "DisconnectPortal");
    }
    [Fact]
    public async Task Owned_session_dispose_joins_a_pending_detach()
    {
        await using var host = new McpOperatorFixture();
        var owner = new McpOperatorFixture.FakeOwnedHost(host.Host.Pid) { AutoExit = false };
        await using var client = host.Client(owner); await client.CreateAsync("19");
        var detaching = client.DetachAsync();
        await owner.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposing = client.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.False(disposing.IsCompleted); Assert.False(owner.Disposed); Assert.False(owner.Forced);
        owner.Exited.TrySetResult(true);
        await Task.WhenAll(detaching, disposing).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, owner.WaitCalls); Assert.True(owner.Disposed); Assert.False(owner.Forced);
    }
    [Fact]
    public async Task Owned_session_forces_only_after_the_shutdown_timeout_and_traces_it()
    {
        await using var host = new McpOperatorFixture();
        var owner = new McpOperatorFixture.FakeOwnedHost(host.Host.Pid) { AutoExit = false };
        await using var client = host.Client(owner); await client.CreateAsync("19");
        using var trace = new OwnedHostTrace(); Trace.Listeners.Add(trace);
        try
        {
            var closing = client.DetachAsync();
            await owner.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromSeconds(10.1));
            Assert.False(owner.Forced); Assert.False(owner.Disposed); Assert.False(closing.IsCompleted);
            await closing.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(owner.Forced); Assert.True(owner.Disposed);
            Assert.True(McpSessionClient.OwnedHostExitTimeout > TimeSpan.FromSeconds(10));
            Assert.True(owner.ForcedAfter >= TimeSpan.FromSeconds(19.8), owner.ForcedAfter.ToString());
            Assert.Contains(trace.Lines, line => line.Contains("Forced termination of owned operator host " + owner.Id));
        }
        finally { Trace.Listeners.Remove(trace); }
    }
    private sealed class OwnedHostTrace : TraceListener
    {
        internal readonly List<string> Lines = [];
        public override void Write(string? message) { lock (Lines) Lines.Add(message ?? ""); }
        public override void WriteLine(string? message) => Write(message);
    }
    [Theory, InlineData(true), InlineData(false)]
    public async Task Workbench_write_opens_the_normal_approval_card_and_requires_its_decision(bool approve)
    {
        await using var host = new McpOperatorFixture { HoldApproval = true }; var client = host.Client(); await Attach(client, host);
        var approvals = new ApprovalServiceStub(); MainWindow? window = null; MainViewModel? model = null;
        host.ApprovalRequested = row => wpf.Run(() => approvals.Receive(row));
        wpf.Run(() =>
        {
            model = new MainViewModel(new McpProjectClient(client), new FakeDialogService { Result = DialogResult.OK });
            window = new MainWindow(model, loadExistingConfiguration: false, approvals: approvals); window.Show();
            WpfContext.Complete(model.Engineering.LoadDevicesAsync("Line"));
            Assert.True(model.Engineering.Save.CanExecute(null));
            model.Engineering.Save.Execute(null);
        });
        await host.Pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        wpf.Run(() =>
        {
            WpfContext.Drain();
            Assert.True(model!.Engineering.Save.IsRunning);
            var row = Assert.Single(approvals.Requests); Assert.Equal("workbench", row.Actor); Assert.NotNull(row.OperatorCallId);
            Assert.Equal(ApprovalState.Pending, row.State);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window!.FindName("ApprovalsContent")).Visibility);
            window.Features.Decide(Assert.Single(window.Features.Requests), approve);
            host.Approval.SetResult(approve);
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (wpf.Run(() => model!.Engineering.Save.IsRunning)) await Task.Delay(10, deadline.Token);
        wpf.Run(() => { Assert.Equal(approve ? 1 : 0, host.ExecutedWrites); Assert.Contains(approve ? "\"ok\": true" : "ACCESS_DENIED", model!.LastEnvelope); window!.Close(); });
    }
    [Fact]
    public async Task Inspection_uses_MCP_facts_and_does_not_report_unknown_references_as_dead_code()
    {
        await using var host = new McpOperatorFixture(); await using var client = host.Client(); await Attach(client, host);
        using var project = new McpProjectClient(client);
        var report = await project.InspectAsync("devices/Station/PLC_1", null);
        Assert.Equal(1, report.BlocksScanned); Assert.DoesNotContain(report.Findings, f => f.RuleId == "DEAD-001");
        Assert.Equal(new[] { "ListPlcBlocks", "GetPlcBlockHierarchy", "GetPlcBlockInfo" }, host.Calls.Select(c => c.Name));
    }
    [Fact]
    public async Task Cancel_ends_only_the_panels_pending_call()
    {
        await using var host = new McpOperatorFixture { HoldApproval = true }; await using var client = host.Client(); await Attach(client, host);
        using var project = new McpProjectClient(client);
        var call = project.SaveProjectAsync(); await host.Pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        client.CancelPending(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(host.Key, client.SessionKey); Assert.Equal(0, host.ExecutedWrites);
        Assert.DoesNotContain(host.Calls, c => c.Name == "DisconnectPortal");
    }
    [Theory, InlineData(false), InlineData(true)]
    public async Task Atlas_and_ladder_export_over_MCP_then_render_at_the_host(bool single)
    {
        await using var host = new McpOperatorFixture(); await using var client = host.Client(); await Attach(client, host);
        using var project = new McpProjectClient(client);
        var service = new McpAtlasService(project);
        var result = await service.GenerateAsync(new(@"D:\fixture\Line.ap19", "devices/Station/PLC_1", ["Main"], single), new Progress<AtlasProgress>(), CancellationToken.None);
        Assert.Null(result.Error); Assert.True(File.Exists(result.HtmlPath));
        Assert.Equal(new[] { "ExportPlcBlock", "ExportPlcBlock", single ? "RenderPlcBlock" : "RenderPlcProgramAtlas" }, host.Calls.Select(c => c.Name));
    }
    [Theory, InlineData("Save"), InlineData("Open"), InlineData("Export"), InlineData("Import"), InlineData("Compile"), InlineData("Connect"), InlineData("Disconnect")]
    public async Task Candidate_policy_uses_the_advertised_schema_and_preserves_the_preview_hash(string action)
    {
        await using var host = new McpOperatorFixture { CandidatePolicies = true }; await using var client = host.Client(); await Attach(client, host);
        using var project = new McpProjectClient(client) { ProcessId = 123, ExpectedProjectFile = @"D:\fixture\Line.ap19" };
        switch (action)
        {
            case "Save": await project.SaveProjectAsync(); break;
            case "Open": await project.OpenProjectAsync(@"D:\fixture\Other.ap19"); break;
            case "Export":
                var result = await project.ExportBlocksAsync("devices/Station/PLC_1", ["Main"], host.Root, ExportFormat.SimaticMl);
                Assert.True(File.Exists(Assert.Single(result.Items).FilePath)); break;
            case "Import": await project.ImportBlocksAsync("devices/Station/PLC_1", [Path.Combine(host.Root, "Main.xml")], false); break;
            case "Compile": await project.CompileAsync("devices/Station/PLC_1"); break;
            case "Connect": await project.ConnectAsync(true); break;
            default: await project.DisconnectAsync(); break;
        }
        foreach (var calls in host.Calls.Where(c => c.Arguments["mode"] != null).GroupBy(c => c.Name))
        {
            Assert.Equal(new[] { "preview", "apply" }, calls.Select(c => (string)c.Arguments["mode"]!));
            Assert.All(calls, c => Assert.Null(c.Arguments["dryRun"]));
            Assert.Equal(new string('a', 64), (string?)calls.Last().Arguments["expectedPlanHash"]);
            Assert.True((bool?)calls.Last().Arguments["confirm"]);
        }
        Assert.Equal(action == "Connect" ? 2 : 1, host.ExecutedWrites);
        Assert.Contains(project.Receipts, receipt => receipt.Phase == "preview");
        Assert.Contains(project.Receipts, receipt => receipt.Phase == "apply");
    }
    [Fact]
    public async Task Source_export_uses_only_its_declared_arguments_and_waits_for_approval()
    {
        await using var host = new McpOperatorFixture { HoldApproval = true }; await using var client = host.Client(); await Attach(client, host);
        using var project = new McpProjectClient(client);
        var call = project.ExportBlocksAsync("devices/Station/PLC_1", ["Main"], host.Root, ExportFormat.Source);
        await host.Pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(call.IsCompleted); Assert.Equal(0, host.ExecutedWrites);
        Assert.Equal(new[] { true, false }, host.Calls.Select(c => (bool)c.Arguments["dryRun"]!));
        Assert.All(host.Calls, c => { Assert.Equal("GeneratePlcSourceFromBlocks", c.Name); Assert.Null(c.Arguments["confirm"]); Assert.Null(c.Arguments["expectedProjectFile"]); });
        host.Approval.SetResult(true); await call; Assert.Equal(1, host.ExecutedWrites);
    }
    [Fact]
    public async Task Background_session_end_updates_the_window_and_clears_its_project()
    {
        await using var host = new McpOperatorFixture(); var client = host.Client(); await Attach(client, host);
        await host.Notify(s => s["binding"] = new JsonObject { ["processId"] = 123, ["projectFile"] = @"D:\fixture\Line.ap19", ["bindingEpoch"] = 1 });
        MainWindow? window = null; MainViewModel? model = null;
        wpf.Run(() =>
        {
            model = new MainViewModel(new McpProjectClient(client), new FakeDialogService());
            window = new MainWindow(model, loadExistingConfiguration: false, approvals: new ApprovalServiceStub()); window.Show();
            WpfContext.Complete(model.Session.ApplySharedStateAsync(host.Snapshot()));
            Assert.Contains("fixture AI", model.Panel!.Participants); Assert.Contains("idle", model.Panel.Status);
            Assert.NotEmpty(model.Engineering.Blocks);
        });
        var detached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Detached += (_, _) => detached.TrySetResult(true);
        await host.Notify(s => { s["worker"]!["state"] = "disconnected"; s["endedBy"] = "mcp"; s["endReason"] = "session-ended"; });
        await detached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        wpf.Run(() =>
        {
            WpfContext.Drain(); Assert.False(model!.Panel!.Attached); Assert.Contains("mcp", model.Panel.Ended);
            Assert.False(model.Session.IsConnected); Assert.Empty(model.Session.ProjectName); Assert.Empty(model.Engineering.Blocks);
            Assert.True(model.CanSelectRelease); window!.Close();
        });
    }
    [Theory, InlineData("Save"), InlineData("Export"), InlineData("Import"), InlineData("Compile"), InlineData("Inspect"), InlineData("Connect"), InlineData("OpenProject")]
    public async Task Page_commands_use_the_shared_session_and_preserve_the_V4_result(string action)
    {
        await using var host = new McpOperatorFixture(); var client = host.Client(); await Attach(client, host);
        MainViewModel? model = null; TiaOpenness.Gui.Common.AsyncCommand? command = null;
        wpf.Run(() =>
        {
            model = new MainViewModel(new McpProjectClient(client), new FakeDialogService { Result = action == "Import" ? DialogResult.Yes : DialogResult.OK,
                Files = [Path.Combine(host.Root, "Main.xml")] });
            WpfContext.Complete(model.Session.ApplySharedStateAsync(new JsonObject { ["binding"] = new JsonObject { ["processId"] = 123,
                ["projectFile"] = @"D:\fixture\Line.ap19", ["bindingEpoch"] = 1 }, ["releaseKey"] = "19", ["worker"] = new JsonObject { ["state"] = "idle" } }));
            model.Engineering.OutputDirectory = host.Root; model.Engineering.SelectAll(true);
        });
        wpf.Run(() =>
        {
            command = action switch { "Connect" => model!.Session.Connect, "OpenProject" => model!.Session.OpenProject,
                "Save" => model!.Engineering.Save, "Export" => model!.Engineering.Export, "Import" => model!.Engineering.Import,
                "Compile" => model!.Engineering.Compile, _ => model!.Engineering.Inspect };
            Assert.True(command.CanExecute(null)); command.Execute(null);
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (wpf.Run(() => command!.IsRunning)) await Task.Delay(10, deadline.Token);
        wpf.Run(() =>
        {
            Assert.Contains("\"schemaVersion\": 4", model!.LastEnvelope);
            Assert.DoesNotContain("fixture refusal", model.LastEnvelope);
            Assert.False(model.VersionControl.VcRefresh.CanExecute(null));
            model.Dispose();
        });
    }
    [Fact]
    public async Task Real_window_page_commands_render_envelopes_and_attach_cached_binding()
    {
        await using var host = new McpOperatorFixture(); var client = host.Client();
        await Attach(client, host);
        await host.Notify(s => s["binding"] = new JsonObject { ["processId"] = 123, ["projectFile"] = @"D:\fixture\Line.ap19", ["bindingEpoch"] = 1 });
        MainWindow? window = null; MainViewModel? model = null;
        wpf.Run(() =>
        {
            model = new MainViewModel(new McpProjectClient(client), new FakeDialogService { Result = DialogResult.OK });
            window = new MainWindow(model, loadExistingConfiguration: false, approvals: new ApprovalServiceStub()); window.Show();
            WpfContext.Complete(model.Session.ApplySharedStateAsync(host.Snapshot()));
            Assert.True(model.Session.IsConnected); Assert.NotEmpty(model.Engineering.Blocks);
            model.Engineering.Save.Execute(null);
        });
        await host.Pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (wpf.Run(() => model!.Engineering.Save.IsRunning)) await Task.Delay(10, deadline.Token);
        wpf.Run(() =>
        {
            WpfContext.Drain();
            Assert.Contains("schemaVersion", model!.LastEnvelope);
            Assert.Equal("SaveProject", model.SelectedReceipt!.Tool); Assert.Equal("apply", model.SelectedReceipt.Phase);
            Assert.IsType<ComboBox>(((TiaOpenness.Gui.Views.ProjectOperationsView)window!.FindName("OperationsContent")).FindName("SessionPicker"));
            window.Close();
        });
    }
}
