using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using TiaOpenness.Gui.ControlChannel;
using TiaOpenness.Shared;
using Xunit;
using static TiaOpenness.Gui.Tests.WorkbenchControlTests;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class WorkbenchControlLoadTests(WpfContext wpf)
{
    [Fact]
    public void Default_width_docks_the_AI_call_detail_without_a_modal_blocker_or_focus_trap()
    {
        wpf.Run(() =>
        {
            var (window, _, _) = Window();
            try
            {
                Assert.Equal(1360, window.Width);
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request(WorkbenchControlOperation.DisplayCall)).Status);
                WpfContext.Drain();
                Assert.Same(window.DockedInspector, window.DrawerPanel.Parent);
                Assert.Null(window.ApplyControl(Request(WorkbenchControlOperation.ReadState)).Data!.Workbench!.BlockedBy);
                Assert.False(window.DrawerPanel.IsKeyboardFocusWithin);
                Assert.True(window.RailLog.Focus());
                Assert.Same(window.RailLog, System.Windows.Input.Keyboard.FocusedElement);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Frozen_state_tracks_an_owned_dialog_even_when_it_opens_without_activation()
    {
        wpf.Run(() =>
        {
            var (window, _, _) = Window();
            var dialog = new Window { Owner = window, Width = 240, Height = 120 };
            try
            {
                string? Blocker() => window.ControlSurface.Snapshot.Read(new WorkbenchReadStateArguments()).Workbench!.BlockedBy;
                WpfContext.ShowWindow(dialog);
                WpfContext.Drain();
                Assert.Equal("dialog", Blocker());
                Assert.Equal("workbench-modal-open", window.ApplyControl(Request()).Refusal?.Condition);
                dialog.Hide();
                WpfContext.Drain();
                Assert.Null(Blocker());
                dialog.Show();
                WpfContext.Drain();
                Assert.Equal("dialog", Blocker());
                dialog.Close();
                WpfContext.Drain();
                Assert.Null(Blocker());
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Frozen_state_tracks_busy_work_and_the_control_switch_in_gate_priority_order()
    {
        wpf.Run(() =>
        {
            var (window, model, _) = Window();
            bool enabled = App.Settings.WorkbenchControlEnabled;
            var finish = new TaskCompletionSource();
            var work = model.Activity.Guarded("Status.ReadingBlocks", () => finish.Task);
            try
            {
                void Check(string reason, string condition)
                {
                    WpfContext.Drain();
                    var state = window.ApplyControl(Request(WorkbenchControlOperation.ReadState)).Data!;
                    Assert.Equal(reason, state.Workbench!.BlockedBy);
                    Assert.Equal(condition, window.ApplyControl(Request()).Refusal?.Condition);
                }
                Check("busy", "workbench-busy");
                window.Navigate("Settings");
                Check("settings", "workbench-modal-open");
                App.Settings.WorkbenchControlEnabled = false;
                window.Navigate("Log");
                Check("control-disabled", "workbench-control-disabled");
            }
            finally
            {
                App.Settings.WorkbenchControlEnabled = enabled;
                finish.SetResult(); WpfContext.Complete(work);
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData("Settings", "settings")]
    [InlineData("Approvals", "approvals")]
    [InlineData("CallDetail", "call-drawer")]
    public void Frozen_state_identifies_the_same_protected_surface_as_the_control_gate(string page, string reason)
    {
        wpf.Run(() =>
        {
            var (window, _, _) = Window();
            try
            {
                if (page == "CallDetail") window.Features.OpenCall(window.Features.Calls[0]);
                else window.Navigate(page);
                WpfContext.Drain();
                var state = window.ApplyControl(Request(WorkbenchControlOperation.ReadState));
                Assert.Equal(WorkbenchControlStatus.Done, state.Status);
                Assert.Equal(reason, state.Data!.Workbench!.BlockedBy);
                Assert.Equal("workbench-modal-open", window.ApplyControl(Request()).Refusal?.Condition);
                window.Navigate("Log");
                WpfContext.Drain();
                Assert.Null(window.ControlSurface.Snapshot.Read(new WorkbenchReadStateArguments()).Workbench!.BlockedBy);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void A_control_opened_call_drawer_closes_on_navigation_and_a_later_human_drawer_blocks()
    {
        wpf.Run(() =>
        {
            var (window, _, client) = Window();
            try
            {
                window.Width = 1200; // Call details must open as a modal for this permission check.
                window.UpdateLayout();
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request(WorkbenchControlOperation.DisplayCall)).Status);
                Assert.Equal(Visibility.Visible, window.DrawerOverlay.Visibility);
                Assert.Null(window.ApplyControl(Request(WorkbenchControlOperation.ReadState)).Data!.Workbench!.BlockedBy);
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request()).Status);
                Assert.Equal(Visibility.Collapsed, window.DrawerOverlay.Visibility);
                Assert.Equal("Log", window.CurrentPage);
                window.Navigate("Calls");
                window.Features.OpenCall(window.Features.Calls[0]);
                Assert.Equal("workbench-modal-open", window.ApplyControl(Request()).Refusal?.Condition);
                Assert.Empty(client.Calls);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Continuous_control_queue_applies_requests_and_keeps_reads_available()
    {
        var (window, _, client) = wpf.Run(() => Window());
        using var server = new WorkbenchControlServer(window.ControlSurface, window.Dispatcher, _ => Environment.ProcessPath!, now: () => TestNow);
        int completed = 0;
        var sampling = Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                await window.Dispatcher.InvokeAsync(() => System.Threading.Interlocked.Increment(ref completed), DispatcherPriority.Background);
                await Task.Delay(5);
            }
        });
        try
        {
            for (int i = 0; i < 100; i++)
            {
                var request = Request(session: i.ToString("x16"), now: TestNow);
                request.Arguments = new WorkbenchDisplayPageArguments { Page = i % 2 == 0 ? WorkbenchPage.Log : WorkbenchPage.Environment };
                Assert.Equal(WorkbenchControlStatus.Done, (await server.Execute(request)).Status);
                Assert.Equal(WorkbenchControlStatus.Done, (await server.Execute(Request(WorkbenchControlOperation.ReadState, now: TestNow))).Status);
                await Task.Delay(5);
            }
            await sampling;
            Assert.Equal(100, completed);
            Assert.Empty(client.Calls);
        }
        finally { await sampling; wpf.Run(window.Close); }
    }
}
