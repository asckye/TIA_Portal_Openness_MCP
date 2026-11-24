using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Themes;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Shared;
using Xunit;

namespace TiaOpenness.Gui.Tests;

public sealed partial class WorkbenchControlTests
{
    [Fact]
    public void Prefill_card_actions_observe_the_mouse_keyboard_and_shortcut_guard()
    {
        wpf.Run(() =>
        {
            var now = TestNow;
            var (window, model, _) = Window(input: new(() => now, () => false));
            try
            {
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request(WorkbenchControlOperation.PrefillForm)).Status);
                var card = window.FindToastCard("ai-prefill")!;
                foreach (var button in new[] { card.Action, card.SecondAction })
                {
                    Assert.True(window.ControlGuard.ShouldBlock(button));
                    foreach (var route in new[] { Mouse.PreviewMouseDownEvent, Mouse.PreviewMouseUpEvent })
                    {
                        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = route };
                        button.RaiseEvent(click);
                        Assert.True(click.Handled);
                    }
                    foreach (var key in new[] { Key.Enter, Key.Space })
                    {
                        var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(button), 0, key)
                            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                        button.RaiseEvent(input);
                        Assert.True(input.Handled);
                    }
                    UnifiedDesktopTests.ClickControl(button);
                }
                Assert.Same(card.Action, window.ToastAction);
                WorkbenchCommands.ToastAction.Execute(null, window);
                Assert.True(card.Surface.IsVisible);
                Assert.NotNull(window.ControlSurface.Snapshot.Read(new WorkbenchReadStateArguments()).Prefill);
                Assert.Equal("Main", model.Engineering.BlockFilter);
                now += TimeSpan.FromMilliseconds(500);
                Assert.False(window.ControlGuard.ShouldBlock(card.Action));
                WorkbenchCommands.ToastAction.Execute(null, window);
                Assert.Null(window.FindToastCard("ai-prefill"));
                Assert.Equal("Main", model.Engineering.BlockFilter);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Control_protocol_has_no_operation_or_display_argument_that_invokes_toast_actions()
    {
        var protocol = WorkbenchControlProtocol.Json;
        Assert.Equal(new[] { "display.page", "display.block", "display.call", "display.ladder", "display.atlas",
                "read.state", "read.selection", "prefill.form" },
            Enum.GetValues<WorkbenchControlOperation>().Select(op => JsonSerializer.SerializeToElement(op, protocol).GetString()));
        var request = JsonSerializer.SerializeToNode(Request(), protocol)!;
        request["operation"] = "toast.action";
        Assert.Throws<JsonException>(() => request.Deserialize<WorkbenchControlRequest>(protocol));
        request["operation"] = "display.page";
        request["arguments"]!["action"] = "ai-prefill";
        Assert.Throws<JsonException>(() => request.Deserialize<WorkbenchControlRequest>(protocol));
    }

    [Fact]
    public void Updated_prefills_replace_the_summary_and_list_each_pending_form()
    {
        wpf.Run(() =>
        {
            var (window, _, _) = Window();
            try
            {
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request(WorkbenchControlOperation.PrefillForm)).Status);
                var first = window.FindToastCard("ai-prefill")!.Notice;
                var rules = new WorkbenchPrefillArguments { Form = WorkbenchPrefillForm.InspectionRules,
                    Fields = new WorkbenchInspectionRulesFields { NamePattern = "FB.*" } };
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request(WorkbenchControlOperation.PrefillForm, rules)).Status);
                window.OperationsContent.HideOptions();
                var updated = window.FindToastCard("ai-prefill")!;
                Assert.NotSame(first, updated.Notice);
                Assert.Contains(Loc.Current["Control.Form.InspectionRules"], updated.Message.Text);
                Assert.Contains(Loc.Current["Control.Form.BlockFilter"], updated.Message.Text);
                Assert.Single(DesktopCapture.Descendants<TextBlock>(window.ToastStack), text => text.IsVisible && text.Text == updated.Title.Text);
                var clear = new WorkbenchPrefillArguments { Form = rules.Form, Mode = WorkbenchPrefillMode.Clear };
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request(WorkbenchControlOperation.PrefillForm, clear)).Status);
                Assert.Equal(Loc.Current["Control.Form.BlockFilter"], window.FindToastCard("ai-prefill")!.Message.Text);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Dismissal_preserves_field_ownership_and_the_field_accepts_or_restores_its_value(bool confirm)
    {
        wpf.Run(() =>
        {
            var now = TestNow;
            var (window, model, _) = Window(input: new(() => now, () => false));
            try
            {
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request(WorkbenchControlOperation.PrefillForm)).Status);
                var marker = window.ProjectExplorer.FilterMarker;
                now += TimeSpan.FromMilliseconds(500);
                UnifiedDesktopTests.ClickControl(window.FindToastCard("ai-prefill")!.Close);
                Assert.Null(window.FindToastCard("ai-prefill"));
                window.Navigate("Log");
                var restored = window.FindToastCard("ai-prefill")!;
                Assert.True(restored.Surface.IsVisible);
                Assert.True(WorkbenchToastTests.PressEscape(window).Handled);
                Assert.Null(window.FindToastCard("ai-prefill"));
                window.Navigate("Engineering");
                Assert.True(window.FindToastCard("ai-prefill")!.Surface.IsVisible);
                UnifiedDesktopTests.ClickControl(window.FindToastCard("ai-prefill")!.Close);
                var emptyForm = new WorkbenchPrefillArguments { Form = WorkbenchPrefillForm.InspectionRules, Mode = WorkbenchPrefillMode.Clear };
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request(WorkbenchControlOperation.PrefillForm, emptyForm)).Status);
                now += TimeSpan.FromMilliseconds(500);
                Assert.Null(window.FindToastCard("ai-prefill"));
                Assert.Equal(Visibility.Visible, marker.Visibility);
                Assert.Equal("Main", model.Engineering.BlockFilter);
                var other = Request(WorkbenchControlOperation.PrefillForm, session: "4444444444444444");
                Assert.Equal("prefill-pending", window.ApplyControl(other).Refusal?.Condition);
                if (confirm)
                {
                    var link = Assert.Single(marker.Children.OfType<TextBlock>().Single().Inlines.OfType<Hyperlink>());
                    link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
                }
                else UnifiedDesktopTests.ClickControl(marker.Children.OfType<Button>().Single());
                WpfContext.Drain();
                Assert.Equal(confirm ? "Main" : "", model.Engineering.BlockFilter);
                Assert.Equal(Visibility.Collapsed, marker.Visibility);
                Assert.Null(window.ControlSurface.Snapshot.Read(new WorkbenchReadStateArguments()).Prefill);
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(other).Status);
                Assert.True(window.FindToastCard("ai-prefill")!.Surface.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Options_close_reshows_a_dismissed_pending_summary_until_the_form_is_cleared()
    {
        wpf.Run(() =>
        {
            var (window, _, _) = Window();
            try
            {
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request(WorkbenchControlOperation.PrefillForm)).Status);
                for (int i = 0; i < 2; i++)
                {
                    Assert.True(WorkbenchToastTests.PressEscape(window).Handled);
                    Assert.Null(window.FindToastCard("ai-prefill"));
                    window.OperationsContent.ShowProjectOptions();
                    window.OperationsContent.HideOptions();
                    Assert.True(window.FindToastCard("ai-prefill")!.Surface.IsVisible);
                }
                var clear = new WorkbenchPrefillArguments { Form = WorkbenchPrefillForm.BlockFilter, Mode = WorkbenchPrefillMode.Clear };
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request(WorkbenchControlOperation.PrefillForm, clear)).Status);
                window.OperationsContent.ShowProjectOptions();
                window.OperationsContent.HideOptions();
                Assert.Null(window.FindToastCard("ai-prefill"));
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Field_actions_refuse_mouse_and_automation_invoke_until_the_guard_expires(bool confirm)
    {
        wpf.Run(() =>
        {
            var now = TestNow;
            var (window, model, _) = Window(input: new(() => now, () => false));
            try
            {
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request(WorkbenchControlOperation.PrefillForm)).Status);
                var marker = window.ProjectExplorer.FilterMarker;
                var link = marker.Children.OfType<TextBlock>().Single().Inlines.OfType<Hyperlink>().Single();
                var button = marker.Children.OfType<Button>().Single();
                AutomationPeer peer = confirm ? new HyperlinkAutomationPeer(link) : new ButtonAutomationPeer(button);
                var invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
                now += TimeSpan.FromMilliseconds(499);
                foreach (var route in new[] { Mouse.PreviewMouseDownEvent, Mouse.PreviewMouseUpEvent })
                {
                    var input = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = route };
                    if (confirm) link.RaiseEvent(input);
                    else button.RaiseEvent(input);
                    Assert.True(input.Handled);
                }
                invoke.Invoke();
                WpfContext.Drain();
                Assert.Equal("Main", model.Engineering.BlockFilter);
                Assert.True(marker.IsVisible);
                Assert.NotNull(window.ControlSurface.Snapshot.Read(new WorkbenchReadStateArguments()).Prefill);
                now += TimeSpan.FromMilliseconds(1);
                invoke.Invoke();
                WpfContext.Drain();
                Assert.Equal(confirm ? "Main" : "", model.Engineering.BlockFilter);
                Assert.False(marker.IsVisible);
                Assert.Null(window.ControlSurface.Snapshot.Read(new WorkbenchReadStateArguments()).Prefill);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(1360, 812)]
    [InlineData(1024, 640)]
    public void Project_page_renders_control_notifications_at_design_sizes(int width, int height)
    {
        wpf.RunWithLanguage(AppLanguage.Chinese, () =>
        {
            var themes = ThemeManager.Current;
            var previous = (themes.Theme, themes.HighContrastOverride);
            themes.Theme = AppTheme.Light;
            themes.HighContrastOverride = false;
            var elapsed = TimeSpan.Zero;
            var (window, _, _) = Window(toastClock: () => elapsed);
            try
            {
                var host = WorkbenchRenderTests.CreateHost(window, width, height);
                WorkbenchRenderTests.ApplyCurrentPalette(host);
                void Capture(string scene, string key)
                {
                    WpfContext.Drain();
                    host.UpdateLayout();
                    var card = window.FindToastCard(key)!;
                    Assert.Equal(Visibility.Visible, card.Surface.Visibility);
                    var origin = card.Surface.TranslatePoint(new Point(), host);
                    Assert.InRange(origin.X, width / 2, width - card.Surface.ActualWidth);
                    Assert.InRange(origin.Y, 0, height - card.Surface.ActualHeight);
                    if (Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR") is { } output)
                    {
                        var folder = Directory.CreateDirectory(Path.Combine(output, "ai-control"));
                        WorkbenchRenderTests.SaveRender(host, Path.Combine(folder.FullName, $"{scene}-light-zh-{width}x{height}.png"), width, height);
                    }
                }
                Assert.Equal(WorkbenchControlStatus.Done,
                    window.ApplyControl(Request(arguments: new WorkbenchDisplayPageArguments { Page = WorkbenchPage.Overview })).Status);
                Capture("activity", "ai-activity");
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request(WorkbenchControlOperation.PrefillForm)).Status);
                elapsed += TimeSpan.FromSeconds(4);
                window.TickToast();
                Assert.Null(window.FindToastCard("ai-activity"));
                Capture("prefill", "ai-prefill");
                var marker = window.BlocksContent.FilterMarker;
                Assert.Equal(Visibility.Visible, marker.Visibility);
                Assert.Equal(24, marker.TranslatePoint(new Point(), window.BlocksContent.BlocksCard).X);
                window.SetControlChannelAvailable(false);
                Assert.True(window.FindToastCard("ai-prefill")!.Notice!.Sticky);
                Assert.True(window.FindToastCard("ai-channel")!.Notice!.Sticky);
                Capture("prefill-and-channel", "ai-prefill");
                UnifiedDesktopTests.ClickControl(window.FindToastCard("ai-prefill")!.Close);
                Capture("channel", "ai-channel");
            }
            finally { window.Close(); (themes.Theme, themes.HighContrastOverride) = previous; }
        });
    }
}
