using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.ControlChannel;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.Settings;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Shared;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class WorkbenchControlTests(WpfContext wpf)
{
    internal const string Project = @"D:\Projects\Line.ap21";
    internal const string CallId = "11111111111111111111111111111111";
    internal static WorkbenchControlRequest Request(WorkbenchControlOperation operation = WorkbenchControlOperation.DisplayPage,
        WorkbenchControlArguments? arguments = null, string session = "2222222222222222") => new()
    {
        RequestId = Guid.NewGuid().ToString("N"), DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(
            operation is WorkbenchControlOperation.ReadState or WorkbenchControlOperation.ReadSelection ? 2 : 5),
        Origin = new() { Host = "foundation", ReleaseKey = "21", HostProcessId = Environment.ProcessId,
            McpSession = session, ClientName = "fixture", BoundProjectFile = Project },
        Operation = operation, Arguments = arguments ?? Arguments(operation),
    };
    internal static WorkbenchControlArguments Arguments(WorkbenchControlOperation operation) => operation switch
    {
        WorkbenchControlOperation.DisplayPage => new WorkbenchDisplayPageArguments { Page = WorkbenchPage.Log },
        WorkbenchControlOperation.DisplayBlock => new WorkbenchDisplayBlockArguments { SoftwarePath = "PLC_1", BlockPath = "Main" },
        WorkbenchControlOperation.DisplayCall => new WorkbenchDisplayCallArguments { RequestId = CallId },
        WorkbenchControlOperation.DisplayLadder => new WorkbenchDisplayLadderArguments { SoftwarePath = "PLC_1", BlockPath = "Main" },
        WorkbenchControlOperation.DisplayAtlas => new WorkbenchDisplayAtlasArguments(),
        WorkbenchControlOperation.ReadState => new WorkbenchReadStateArguments(),
        WorkbenchControlOperation.ReadSelection => new WorkbenchReadSelectionArguments(),
        _ => new WorkbenchPrefillArguments { Form = WorkbenchPrefillForm.BlockFilter, Mode = WorkbenchPrefillMode.Set,
            Fields = new WorkbenchBlockFilterFields { Filter = "Main" } },
    };
    internal static (MainWindow Window, MainViewModel Model, FakeStudioClient Client) Window(bool connected = true)
    {
        var client = new FakeStudioClient { AttachedProject = new() { Name = "Line", Path = Project } };
        var model = new MainViewModel(client, new FakeDialogService()) { SelectedReleaseKey = "21" };
        if (connected) model.Session.Connect.Execute(null);
        var window = new MainWindow(model, false, new ApprovalServiceStub(), new FeaturePageFixtures.Diagnostics());
        var journal = new FeaturePageFixtures.Journal();
        journal.Replace([journal.Calls[0] with { RequestId = CallId, JournalKey = CallId }]);
        window.ConfigureFeaturePages(journal, new FeaturePageFixtures.Audit(), new FeaturePageFixtures.Environment());
        window.Show(); WpfContext.Drain(); client.Calls.Clear();
        // Showing a real window may generate mouse motion. The test clock models an idle human.
        window.ControlGuard = new ControlInputGuard(() => DateTimeOffset.MaxValue);
        return (window, model, client);
    }
    internal static string Scratch(string name) => Path.GetFullPath(Path.Combine("bin-build", "refactor", "P8-20b", name + "-" + Guid.NewGuid().ToString("N")));

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void Each_operation_uses_loaded_window_state_without_native_calls(int operation)
    {
        wpf.Run(() =>
        {
            var (window, model, client) = Window();
            try
            {
                var request = Request((WorkbenchControlOperation)operation);
                var response = window.ControlSurface.Apply(request);
                Assert.Equal(WorkbenchControlStatus.Done, response.Status);
                WorkbenchControlProtocol.Validate(response);
                Assert.Empty(client.Calls);
                Assert.False(model.Engineering.Blocks[0].Selected);
                if (operation is 1 or 3) Assert.Equal("Main", window.ControlSurface.Snapshot.Read(new WorkbenchReadSelectionArguments()).FocusedBlock?.Path);
                if (operation == 7) Assert.Equal("Main", model.Engineering.BlockFilter);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(7)]
    public void Switch_off_refuses_each_UI_operation_and_reads_remain_available(int operation)
    {
        wpf.Run(() =>
        {
            var (window, _, client) = Window(); bool enabled = App.Settings.WorkbenchControlEnabled;
            try
            {
                App.Settings.WorkbenchControlEnabled = false;
                Assert.Equal("workbench-control-disabled", window.ApplyControl(Request((WorkbenchControlOperation)operation)).Refusal?.Condition);
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request(WorkbenchControlOperation.ReadState)).Status);
                Assert.Empty(client.Calls);
            }
            finally { App.Settings.WorkbenchControlEnabled = enabled; window.Close(); }
        });
    }

    [Theory]
    [InlineData("modal")] [InlineData("confirm")] [InlineData("human")] [InlineData("busy")]
    public void Human_first_guards_are_rechecked_on_UI_thread(string guard)
    {
        wpf.Run(() =>
        {
            var (window, model, client) = Window();
            Task? busy = null; var finish = new TaskCompletionSource();
            try
            {
                if (guard == "modal") window.OpenSettings();
                if (guard == "confirm") ((FrameworkElement)window.FindName("ConfirmOverlay")).Visibility = Visibility.Visible;
                if (guard == "human") { window.ControlGuard = new(); window.ControlGuard.HumanInput(); }
                if (guard == "busy") busy = model.Activity.Guarded("Status.ReadingBlocks", () => finish.Task);
                Assert.Equal(guard switch { "human" => "workbench-user-active", "busy" => "workbench-busy", _ => "workbench-modal-open" },
                    window.ApplyControl(Request()).Refusal?.Condition);
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request(WorkbenchControlOperation.ReadSelection)).Status);
                Assert.Empty(client.Calls);
            }
            finally { finish.TrySetResult(); if (busy != null) WpfContext.Complete(busy); window.Close(); }
        });
    }

    [Fact]
    public void Modal_owner_window_refuses_control()
    {
        wpf.Run(() =>
        {
            var (window, _, _) = Window(); var modal = new System.Windows.Window { Owner = window, Width = 200, Height = 100 };
            try { modal.Show(); Assert.Equal("workbench-modal-open", window.ApplyControl(Request()).Refusal?.Condition); }
            finally { modal.Close(); window.Close(); }
        });
    }

    [Theory]
    [InlineData("identity")] [InlineData("missing")] [InlineData("ambiguous")] [InlineData("tree")] [InlineData("no-project")]
    public void Block_resolution_has_exact_identity_and_uniqueness_guards(string condition)
    {
        wpf.Run(() =>
        {
            var (window, model, client) = Window(condition != "no-project");
            try
            {
                var request = Request(WorkbenchControlOperation.DisplayBlock);
                if (condition == "identity") request.Origin.BoundProjectFile = @"D:\Other.ap21";
                if (condition == "missing") ((WorkbenchDisplayBlockArguments)request.Arguments).BlockPath = "main";
                if (condition == "ambiguous") model.Engineering.Blocks.Add(new(new() { Path = "Main", Name = "Duplicate", Kind = BlockKind.OB }));
                if (condition == "tree") model.Engineering.SelectedDevice = null;
                var response = window.ApplyControl(request);
                Assert.Equal(WorkbenchControlStatus.Refused, response.Status);
                Assert.Equal(condition switch { "identity" => WorkbenchControlError.IdentityMismatch, "missing" => WorkbenchControlError.NotFound,
                    "ambiguous" => WorkbenchControlError.TargetAmbiguous, _ => WorkbenchControlError.PreconditionFailed }, response.Refusal?.Code);
                Assert.Empty(client.Calls);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Locating_does_not_change_checks_or_keyboard_focus()
    {
        wpf.Run(() =>
        {
            var (window, model, _) = Window();
            try
            {
                model.Engineering.Blocks[0].Selected = true;
                var focus = Keyboard.FocusedElement;
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request(WorkbenchControlOperation.DisplayBlock)).Status);
                Assert.True(model.Engineering.Blocks[0].Selected); Assert.Same(focus, Keyboard.FocusedElement);
                Assert.True(window.ControlGuard.ClickGuardActive);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Pending_call_is_never_opened()
    {
        wpf.Run(() =>
        {
            var (window, _, client) = Window();
            try
            {
                ((ApprovalServiceStub)window.Approvals).Receive(FeaturePageFixtures.Request(CallId) with { Deadline = DateTimeOffset.UtcNow.AddSeconds(120) });
                Assert.Equal("approval-pending", window.ApplyControl(Request(WorkbenchControlOperation.DisplayCall)).Refusal?.Condition);
                Assert.Null(window.Features.SelectedCall); Assert.Empty(client.Calls);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Prefill_is_owned_validated_and_clear_restores_original_values(int form)
    {
        wpf.Run(() =>
        {
            var (window, model, client) = Window();
            try
            {
                string originalPattern = model.Engineering.NamePattern;
                var arguments = new WorkbenchPrefillArguments { Form = (WorkbenchPrefillForm)form, Mode = WorkbenchPrefillMode.Set,
                    Fields = form switch { 0 => new WorkbenchInspectionRulesFields { NamePattern = "^AI_" },
                        1 => new WorkbenchBlockFilterFields { Filter = "Main" }, _ => new WorkbenchBlockSelectionFields { SoftwarePath = "PLC_1", BlockPaths = ["Main"] } } };
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request(WorkbenchControlOperation.PrefillForm, arguments)).Status);
                Assert.Equal("prefill-pending", window.ApplyControl(Request(WorkbenchControlOperation.PrefillForm, arguments, "3333333333333333")).Refusal?.Condition);
                var clear = new WorkbenchPrefillArguments { Form = arguments.Form, Mode = WorkbenchPrefillMode.Clear,
                    Fields = form switch { 0 => new WorkbenchInspectionRulesFields(), 1 => new WorkbenchBlockFilterFields(), _ => new WorkbenchBlockSelectionFields() } };
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request(WorkbenchControlOperation.PrefillForm, clear)).Status);
                Assert.Equal(originalPattern, model.Engineering.NamePattern); Assert.Equal("", model.Engineering.BlockFilter);
                Assert.False(model.Engineering.Blocks[0].Selected); Assert.Empty(client.Calls);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Invalid_regex_and_missing_selection_are_atomic_refusals()
    {
        wpf.Run(() =>
        {
            var (window, model, client) = Window();
            try
            {
                string original = model.Engineering.NamePattern;
                var regex = new WorkbenchPrefillArguments { Form = WorkbenchPrefillForm.InspectionRules,
                    Fields = new WorkbenchInspectionRulesFields { NamePattern = "[" } };
                Assert.Equal(WorkbenchControlError.InvalidArgument, window.ApplyControl(Request(WorkbenchControlOperation.PrefillForm, regex)).Refusal?.Code);
                var selection = new WorkbenchPrefillArguments { Form = WorkbenchPrefillForm.BlockSelection,
                    Fields = new WorkbenchBlockSelectionFields { SoftwarePath = "PLC_1", BlockPaths = ["Main", "Missing"] } };
                Assert.Equal(WorkbenchControlError.NotFound, window.ApplyControl(Request(WorkbenchControlOperation.PrefillForm, selection)).Refusal?.Code);
                Assert.Equal(original, model.Engineering.NamePattern); Assert.False(model.Engineering.Blocks[0].Selected); Assert.Empty(client.Calls);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Inspection_rule_prefill_is_available_before_connecting()
    {
        wpf.Run(() =>
        {
            var (window, model, client) = Window(false);
            try
            {
                var request = Request(WorkbenchControlOperation.PrefillForm, new WorkbenchPrefillArguments
                    { Form = WorkbenchPrefillForm.InspectionRules, Fields = new WorkbenchInspectionRulesFields { NamePattern = "^FB_" } });
                request.Origin.BoundProjectFile = null;
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(request).Status);
                Assert.Equal("^FB_", model.Engineering.NamePattern); Assert.Empty(client.Calls);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Snapshot_reports_the_most_recently_updated_pending_form()
    {
        wpf.Run(() =>
        {
            var (window, _, _) = Window();
            try
            {
                var rules = Request(WorkbenchControlOperation.PrefillForm, new WorkbenchPrefillArguments
                    { Form = WorkbenchPrefillForm.InspectionRules, Fields = new WorkbenchInspectionRulesFields { NamePattern = "^FB_" } });
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(rules).Status);
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request(WorkbenchControlOperation.PrefillForm)).Status);
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(rules).Status);
                Assert.Equal(WorkbenchPrefillForm.InspectionRules, window.ControlSurface.Snapshot.Read(new WorkbenchReadStateArguments()).Prefill?.Form);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Snapshot_is_frozen_paginated_and_reads_while_dispatcher_is_blocked()
    {
        var (window, model, client) = wpf.Run(() => Window());
        try
        {
            wpf.Run(() => { model.Engineering.Blocks[0].Selected = true; WpfContext.Drain(); });
            var snapshot = window.ControlSurface.Snapshot;
            var data = snapshot.Read(new WorkbenchReadSelectionArguments());
            Assert.Single(data.CheckedBlocks!); data.CheckedBlocks![0].Path = "tampered";
            Assert.Equal("Main", snapshot.Read(new WorkbenchReadSelectionArguments()).CheckedBlocks![0].Path);
            var page = snapshot.Read(new WorkbenchReadSelectionArguments { Offset = 1, Limit = 1 });
            Assert.Empty(page.CheckedBlocks!); Assert.Equal(1, page.Paging?.Total);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            window.Dispatcher.BeginInvoke(new Action(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(2)); }));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));
            try
            {
                using var server = new WorkbenchControlServer(window.ControlSurface, window.Dispatcher, _ => Environment.ProcessPath!);
                var read = server.Execute(Request(WorkbenchControlOperation.ReadState));
                Assert.True(read.IsCompletedSuccessfully); Assert.Equal("Line", read.Result.Data?.Session?.Project?.Name);
                Assert.Empty(client.Calls);
            }
            finally { release.Set(); }
        }
        finally { wpf.Run(window.Close); }
    }

    [Fact]
    public async Task Queue_is_bounded_FIFO_and_per_origin_inflight_is_limited()
    {
        var (window, _, _) = wpf.Run(() => Window());
        var seen = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var surface = new WorkbenchControlSurface(request => { seen.Enqueue(request.RequestId); return window.ApplyControl(request); }, window.ControlSurface.Snapshot);
        using var server = new WorkbenchControlServer(surface, window.Dispatcher, _ => Environment.ProcessPath!);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        window.Dispatcher.BeginInvoke(new Action(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(4)); }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));
        try
        {
            var first = Request(); var pending = server.Execute(first);
            Assert.Equal("workbench-busy", (await server.Execute(Request())).Refusal?.Condition);
            await Task.Delay(50); // The reader is now waiting on the blocked dispatcher.
            var requests = Enumerable.Range(1, 16).Select(i => Request(session: i.ToString("x16"))).ToArray();
            var queued = requests.Select(server.Execute).ToArray();
            Assert.Equal("workbench-busy", (await server.Execute(Request(session: "ffffffffffffffff"))).Refusal?.Condition);
            release.Set();
            Assert.Equal(WorkbenchControlStatus.Done, (await pending).Status);
            Assert.All(await Task.WhenAll(queued), response => Assert.Equal(WorkbenchControlStatus.Done, response.Status));
            Assert.Equal(new[] { first.RequestId }.Concat(requests.Select(request => request.RequestId)), seen);
            Assert.Equal("workbench-busy", (await server.Execute(Request())).Refusal?.Condition);
        }
        finally { release.Set(); wpf.Run(window.Close); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Expired_and_closing_queued_requests_never_apply(bool closing)
    {
        var (window, _, _) = wpf.Run(() => Window()); int applied = 0;
        var surface = new WorkbenchControlSurface(request => { Interlocked.Increment(ref applied); return window.ApplyControl(request); }, window.ControlSurface.Snapshot);
        using var server = new WorkbenchControlServer(surface, window.Dispatcher, _ => Environment.ProcessPath!);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        window.Dispatcher.BeginInvoke(new Action(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(2)); }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));
        try
        {
            var request = Request(); request.DeadlineUtc = DateTimeOffset.UtcNow.AddMilliseconds(150);
            var pending = server.Execute(request);
            if (closing) server.Dispose();
            var response = await pending;
            Assert.Equal(closing ? "workbench-closing" : "workbench-deadline", response.Refusal?.Condition);
            release.Set(); server.Dispose(); await server.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, applied);
        }
        finally { release.Set(); server.Dispose(); wpf.Run(window.Close); }
    }

    [Theory]
    [InlineData("version")] [InlineData("deadline")] [InlineData("future")]
    public async Task Server_refuses_version_and_deadline_before_UI(string condition)
    {
        var (window, _, client) = wpf.Run(() => Window());
        using var server = new WorkbenchControlServer(window.ControlSurface, window.Dispatcher, _ => Environment.ProcessPath!);
        try
        {
            var request = Request();
            if (condition == "version") request.Version = 2;
            else request.DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(condition == "future" ? 30 : -1);
            Assert.Equal(condition switch { "version" => WorkbenchControlError.UnsupportedCapability,
                "future" => WorkbenchControlError.InvalidArgument, _ => WorkbenchControlError.Timeout }, (await server.Execute(request)).Refusal?.Code);
            Assert.Empty(client.Calls);
        }
        finally { wpf.Run(window.Close); }
    }

    [Fact]
    public void Root_click_and_key_guard_blocks_action_for_500ms_without_deciding()
    {
        wpf.Run(() =>
        {
            var (window, _, _) = Window();
            try
            {
                var now = DateTimeOffset.UtcNow; window.ControlGuard = new(() => now);
                var button = (Button)window.FindName("PendingBadge");
                window.ControlGuard.UiChanged();
                Assert.True(window.ControlGuard.ShouldBlock(button));
                var mouse = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent, Source = button };
                button.RaiseEvent(mouse); Assert.True(mouse.Handled);
                Assert.Equal(0, window.Approvals.PendingCount);
                now = now.AddMilliseconds(500); Assert.False(window.ControlGuard.ShouldBlock(button));
                Assert.True(window.ControlGuard.HumanActive);
                now = now.AddMilliseconds(1000); Assert.False(window.ControlGuard.HumanActive);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Actual_decision_button_is_protected_by_root_preview_handlers()
    {
        wpf.Run(() =>
        {
            var (window, _, _) = Window();
            try
            {
                var pending = FeaturePageFixtures.Request(CallId) with { Deadline = DateTimeOffset.UtcNow.AddSeconds(120) };
                ((ApprovalServiceStub)window.Approvals).Receive(pending);
                window.ApplyControl(Request());
                window.Features.OpenApprovals(); WpfContext.Drain(); window.UpdateLayout();
                var button = Descendants<Button>(window).First(item => item.Name == "ApproveRequest");
                var mouse = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseDownEvent, Source = button };
                button.RaiseEvent(mouse); Assert.True(mouse.Handled);
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = button };
                button.RaiseEvent(key); Assert.True(key.Handled);
                Assert.Equal(ApprovalState.Pending, window.Approvals.Requests.Single().State);
            }
            finally { window.Close(); }
        });
    }
    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T result) yield return result;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    [Fact]
    public void Human_can_confirm_or_clear_prefill_and_release_form_ownership()
    {
        wpf.Run(() =>
        {
            var (window, model, _) = Window();
            try
            {
                window.ApplyControl(Request(WorkbenchControlOperation.PrefillForm));
                var hint = (StackPanel)window.FindName("ControlPrefillHint");
                hint.Children.OfType<Button>().Last().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("", model.Engineering.BlockFilter);
                Assert.Equal(Visibility.Collapsed, hint.Visibility);
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request(WorkbenchControlOperation.PrefillForm, session: "4444444444444444")).Status);
                hint.Children.OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WpfContext.Drain();
                Assert.Equal("Main", model.Engineering.BlockFilter);
                Assert.Null(window.ControlSurface.Snapshot.Read(new WorkbenchReadStateArguments()).Prefill);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Surface_and_control_directory_have_no_privileged_dependencies()
    {
        Assert.Equal(new[] { "Apply", "get_Snapshot" }, typeof(IWorkbenchControlSurface).GetMethods().Select(method => method.Name).Order().ToArray());
        Assert.Equal(3, PrefillRegistry.Targets.Count);
        Assert.All(PrefillRegistry.Targets, target => Assert.Equal("none", target.Executes));
        var forbidden = new[] { "IStudioClient", "TiaClient", "BridgeClient", "IApprovalService", "ApprovalSettings", "ConfigurationView" };
        foreach (var type in typeof(WorkbenchControlServer).Assembly.GetTypes().Where(type => type.Namespace == typeof(WorkbenchControlServer).Namespace))
            Assert.DoesNotContain(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                field => forbidden.Contains(field.FieldType.Name));
        var sources = SourceScan.Code.Where(file => file.Name.Contains(".Control.")).ToArray();
        Assert.NotEmpty(sources);
        foreach (string source in sources.Select(file => file.Text))
            foreach (string name in new[] { "Approv", "OnStartServer", "OnStopServer", "UiSettings" }) Assert.DoesNotContain(name, source);
    }

    [Fact]
    public void Switch_defaults_on_and_roundtrips_without_other_settings_changes()
    {
        string path = Scratch("ui") + ".settings";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Assert.True(UiSettings.Load(path).WorkbenchControlEnabled);
        File.WriteAllText(path, "workbenchControlEnabled=broken\n"); Assert.True(UiSettings.Load(path).WorkbenchControlEnabled);
        new UiSettings { WorkbenchControlEnabled = false }.Save(path);
        Assert.False(UiSettings.Load(path).WorkbenchControlEnabled);
    }

    [Theory]
    [InlineData(AppLanguage.English)] [InlineData(AppLanguage.Chinese)]
    public void Status_and_prefill_markers_are_localized(AppLanguage language)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var (window, _, _) = Window();
            try
            {
                window.ApplyControl(Request(WorkbenchControlOperation.PrefillForm));
                Assert.Contains("PrefillWorkbenchForm", ((TextBlock)window.FindName("ControlStatus")).Text);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("ControlPrefillHint")).Visibility);
                Assert.NotEqual("Control.Enabled", Loc.Current["Control.Enabled"]);
            }
            finally { window.Close(); }
        });
    }
}
