using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using TiaOpenness.Shared;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.ViewModels;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class WorkbenchToastTests(WpfContext wpf)
{
    [Fact]
    public void Crash_dialog_uses_the_ui_owner_and_coalesces_concurrent_and_nested_errors()
    {
        wpf.Run(() =>
        {
            var previous = Application.Current.MainWindow;
            var owner = new Window();
            try
            {
                WpfContext.ShowWindow(owner); Application.Current.MainWindow = owner;
                var messages = new List<string>();
                void Show(Window window, string message)
                {
                    Assert.Same(owner, window);
                    Assert.True(owner.Dispatcher.CheckAccess());
                    messages.Add(message);
                    void Unwanted(Window _, string extra) => throw new InvalidOperationException("Unexpected dialog for " + extra);
                    App.PresentUiError("nested", true, Unwanted);
                    var concurrent = Task.Run(() => App.PresentUiError("concurrent", true, Unwanted));
                    WpfContext.Complete(concurrent.WaitAsync(TiaMcp.Engine.Tests.Shared.TestDeadlines.Hang));
                }
                WpfContext.Complete(Task.Run(() => App.PresentUiError("first", true, Show)));
                App.PresentUiError("next", true, Show);
                Assert.Equal(new[] { "first", "next" }, messages);
            }
            finally { Application.Current.MainWindow = previous; owner.Close(); }
        });
    }

    [Fact]
    public void Fatal_dialog_can_open_while_a_nonfatal_dialog_is_displayed()
    {
        wpf.Run(() =>
        {
            var previous = Application.Current.MainWindow;
            var owner = new Window();
            try
            {
                WpfContext.ShowWindow(owner); Application.Current.MainWindow = owner;
                var messages = new List<string>();
                App.PresentUiError("nonfatal", showDialog: (_, message) =>
                {
                    messages.Add(message);
                    App.PresentUiError("fatal", true, (_, fatal) => messages.Add(fatal));
                    App.PresentUiError("duplicate", showDialog: (_, duplicate) => messages.Add(duplicate));
                });
                Assert.Equal(new[] { "nonfatal", "fatal" }, messages);
            }
            finally { Application.Current.MainWindow = previous; owner.Close(); }
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Fatal_worker_logs_first_and_returns_when_dispatch_or_dialog_is_blocked(bool dialogStarted)
    {
        string path = Path.Combine(McpOperatorFixture.RepositoryRoot, "bin-build", Guid.NewGuid() + ".crash.log");
        using var returned = new System.Threading.ManualResetEventSlim();
        int shown = 0;
        bool loggedBeforeDialog = false, workerReturnedDuringDialog = false;
        try
        {
            wpf.Run(() =>
            {
                void Show(Window _, string message)
                {
                    shown++;
                    loggedBeforeDialog = File.ReadAllText(path).Contains("blocked-dispatcher-fixture") && message.Contains("blocked-dispatcher-fixture");
                    workerReturnedDuringDialog = returned.Wait(TimeSpan.FromSeconds(10));
                }
                var worker = Task.Run(() =>
                {
                    try { App.ReportFatalException(new IOException("blocked-dispatcher-fixture"), path, Show); }
                    finally { returned.Set(); }
                });
                if (dialogStarted) WpfContext.Complete(worker);
                else Assert.True(returned.Wait(TimeSpan.FromSeconds(10))); // Block the dispatcher just as a UI waiting on the crashing worker would.
                WpfContext.Complete(worker);
                Assert.Contains("blocked-dispatcher-fixture", File.ReadAllText(path));
                WpfContext.Drain();
                Assert.Equal(dialogStarted ? 1 : 0, shown);
                Assert.Equal(dialogStarted, loggedBeforeDialog);
                Assert.Equal(dialogStarted, workerReturnedDuringDialog);
            });
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(ToastKind.Success, 4)]
    [InlineData(ToastKind.Info, 4)]
    [InlineData(ToastKind.Info, 4, true)]
    [InlineData(ToastKind.Warning, 4)]
    [InlineData(ToastKind.Error, 8)]
    public void Only_visible_unpaused_time_counts(ToastKind kind, int seconds, bool actionable = false)
    {
        TimeSpan now = TimeSpan.Zero;
        var queue = new ToastQueue(() => now);
        var notice = new ToastNotice("first", kind, LocalizedText.Literal("first"), default, actionable ? () => { } : null, "");
        queue.SetPresentation(true, false);
        queue.Show(notice);
        now += TimeSpan.FromHours(1); queue.Tick();
        Assert.Null(queue.Visible);
        Assert.Null(queue.Delay);
        queue.SetPresentation(false, false);
        Assert.Same(notice, queue.Visible);
        Assert.Equal(TimeSpan.FromSeconds(seconds), queue.Delay);
        now += TimeSpan.FromSeconds(1);
        queue.SetPresentation(false, true);
        now += TimeSpan.FromMinutes(1); queue.Tick();
        Assert.Same(notice, queue.Visible);
        queue.SetPresentation(false, false);
        Assert.Equal(TimeSpan.FromSeconds(seconds - 1), queue.Delay);
        now += TimeSpan.FromSeconds(seconds - 1) - TimeSpan.FromTicks(1);
        queue.Tick(); Assert.Same(notice, queue.Visible);
        now += TimeSpan.FromTicks(1);
        queue.Tick(); Assert.Null(queue.Visible);
    }

    [Fact]
    public void Replaces_visible_and_queued_sources_and_preserves_other_sources_in_order()
    {
        TimeSpan now = TimeSpan.Zero;
        var queue = new ToastQueue(() => now);
        var first = new ToastNotice("a", ToastKind.Info, LocalizedText.Literal("a"), default, null, "");
        var second = first with { Source = "b" };
        var third = first with { Source = "c" };
        var replacement = second with { Kind = ToastKind.Error };
        queue.Show(first); queue.Show(second); queue.Show(third);
        Assert.Equal(new[] { first, second, third }, queue.Cards);
        queue.Show(replacement);
        Assert.Equal(new[] { first, third, replacement }, queue.Cards);
        now += TimeSpan.FromSeconds(3);
        var active = first with { Kind = ToastKind.Warning };
        queue.Show(active);
        Assert.Same(active, queue.Visible);
        Assert.Equal(TimeSpan.FromSeconds(1), queue.Delay);
        queue.Dismiss(); Assert.Same(replacement, queue.Visible);
        queue.Dismiss(); Assert.Same(third, queue.Visible);
        queue.Dismiss(); Assert.Null(queue.Visible);
        queue.Show(active);
        Assert.Equal(TimeSpan.FromSeconds(4), queue.Delay);
        now += TimeSpan.FromSeconds(4) - TimeSpan.FromTicks(1);
        queue.Tick(); Assert.Same(active, queue.Visible);
        now += TimeSpan.FromTicks(1);
        queue.Tick(); Assert.Null(queue.Visible);
    }

    [Fact]
    public void Capacity_evicts_the_oldest_transient_and_sticky_cards_have_no_timer()
    {
        var now = TimeSpan.Zero;
        var queue = new ToastQueue(() => now);
        var sticky = new ToastNotice("sticky", ToastKind.Warning, LocalizedText.Literal("warning"), default, null, "", Sticky: true);
        queue.Show(sticky);
        Assert.Null(queue.Delay);
        foreach (var key in new[] { "a", "b", "c" }) queue.Show(sticky with { Source = key, Sticky = false });
        Assert.Equal(new[] { "sticky", "b", "c" }, queue.Cards.Select(card => card.Source));
        now += TimeSpan.FromDays(1);
        queue.Tick();
        Assert.Same(sticky, Assert.Single(queue.Cards));
        Assert.Null(queue.Delay);
    }

    [Fact]
    public void Broken_audit_toast_survives_the_clock_and_locates_the_broken_record()
    {
        wpf.Run(() =>
        {
            var elapsed = TimeSpan.Zero;
            var window = new MainWindow(ProjectPageTestSupport.Model(), false, toastClock: () => elapsed);
            try
            {
                window.ConfigureFeaturePages(new FeaturePageFixtures.Journal(), new FeaturePageFixtures.Audit { Broken = true },
                    new FeaturePageFixtures.Environment());
                window.Navigate("Log");
                WpfContext.Complete(window.Features.VerifyAsync());
                elapsed += TimeSpan.FromHours(1);
                window.TickToast();
                Assert.Equal(Visibility.Visible, window.Toast.Visibility);
                Assert.Contains("#532", (string)window.ToastAction.Content);
                UnifiedDesktopTests.ClickControl(window.ToastAction);
                Assert.Equal("Audit", window.CurrentPage);
                Assert.Equal(532, window.Features.SelectedAudit!.Record.Index);
                Assert.True(window.AuditContent.DetailsToggle.IsChecked);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Sticky_notices_announce_once_and_a_dropped_error_is_still_announced()
    {
        wpf.Run(() =>
        {
            var (window, _, _) = WorkbenchControlTests.Window();
            var heard = new List<string>();
            void Record(FrameworkElement source) => heard.Add(AutomationProperties.GetName(source));
            AccessibleStatus.Announced += Record;
            try
            {
                foreach (string key in new[] { "first", "second", "third" })
                    window.ShowToast(ToastKind.Warning, LocalizedText.Literal(key), key, sticky: true);
                window.ShowToast(ToastKind.Error, LocalizedText.Literal("dropped error"), "fourth");
                Assert.Equal("third", window.ToastMessage.Text);
                Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(window.DroppedErrorAnnouncement));
                Assert.True(PressEscape(window).Handled);
                Assert.Equal("second", window.ToastMessage.Text);
                Assert.Equal(new[] { "first", "second", "third", "dropped error" }, heard);
            }
            finally { AccessibleStatus.Announced -= Record; window.Close(); }
        });
    }

    [Theory]
    [InlineData("session-lost")]
    [InlineData("ai-prefill")]
    [InlineData("ai-channel")]
    [InlineData("env-alert")]
    public void Each_persistent_condition_returns_when_another_card_closes(string first)
    {
        wpf.Run(() =>
        {
            var (window, model, client) = WorkbenchControlTests.Window();
            var environment = new FeaturePageFixtures.Environment();
            window.ConfigureFeaturePages(new FeaturePageFixtures.Journal(), new FeaturePageFixtures.Audit(), environment);
            var heard = new List<string>();
            void Record(FrameworkElement source) => heard.Add(AutomationProperties.GetName(source));
            AccessibleStatus.Announced += Record;
            try
            {
                void Show(string source)
                {
                    switch (source)
                    {
                        case "ai-prefill":
                            var rules = new WorkbenchPrefillArguments { Form = WorkbenchPrefillForm.InspectionRules,
                                Fields = new WorkbenchInspectionRulesFields { NamePattern = "FB.*" } };
                            var response = window.ApplyControl(WorkbenchControlTests.Request(WorkbenchControlOperation.PrefillForm, rules));
                            Assert.Equal(WorkbenchControlStatus.Done, response.Status);
                            window.OperationsContent.HideOptions();
                            break;
                        case "ai-channel": window.SetControlChannelAvailable(false); break;
                        case "session-lost":
                            client.Exit();
                            Assert.True(model.Session.LostConnection);
                            break;
                        case "env-alert":
                            var issue = EnvironmentCheckCatalogue.Row(new("membership", Core.Environment.EnvironmentFindingStatus.Fail, "Fail", "Fixture"));
                            environment.Replace([new(LocalizedText.Key("Shell.Environment"), [issue])]);
                            break;
                    }
                    WpfContext.Drain();
                    Assert.True(window.FindToastCard(source)!.Surface.IsVisible);
                }
                Show(first);
                var notice = window.FindToastCard(first)!.Notice!;
                var others = new[] { "session-lost", "ai-prefill", "ai-channel", "env-alert" }.Where(source => source != first).ToArray();
                foreach (var source in others) Show(source);
                Assert.Null(window.FindToastCard(first));
                Assert.All(others, source => Assert.True(window.FindToastCard(source)!.Surface.IsVisible));
                UnifiedDesktopTests.ClickControl(window.FindToastCard(others[^1])!.Close);
                Assert.Same(notice, window.FindToastCard(first)!.Notice);
                Assert.True(window.FindToastCard(first)!.Surface.IsVisible);
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(window.DisplacedStickyAnnouncement));
                Assert.Single(heard, text => text == Loc.Current.T("Toast.StickyDisplaced", notice.FullText));
                Assert.Single(heard, text => text == notice.FullText);
            }
            finally { AccessibleStatus.Announced -= Record; window.Close(); }
        });
    }

    [Fact]
    public void Resolving_a_condition_while_its_sticky_card_is_displaced_cancels_its_return()
    {
        var queue = new ToastQueue(() => TimeSpan.Zero);
        var notice = new ToastNotice("ai-channel", ToastKind.Warning, LocalizedText.Literal("unavailable"), default, null, "", Sticky: true);
        queue.Show(notice);
        foreach (string source in new[] { "env-alert", "session-lost", "ai-prefill" }) queue.Show(notice with { Source = source });
        Assert.True(queue.Contains(notice));
        Assert.DoesNotContain(notice, queue.Cards);
        queue.Remove("ai-channel");
        queue.Dismiss();
        Assert.False(queue.Contains(notice));
        Assert.Equal(new[] { "env-alert", "session-lost" }, queue.Cards.Select(card => card.Source));
    }

    [Fact]
    public void Sticky_description_scrolls_and_keyboard_dismissal_restores_focus()
    {
        wpf.Run(() =>
        {
            var (window, _, _) = WorkbenchControlTests.Window();
            try
            {
                window.CurrentPageTitle.Focus();
                window.ShowToast(ToastKind.Warning, LocalizedText.Literal("first"), "first", sticky: true);
                window.ShowToast(ToastKind.Warning, LocalizedText.Literal(new string('W', 1700)), "long", sticky: true);
                window.UpdateLayout();
                var description = Assert.Single(DesktopCapture.Descendants<ScrollViewer>(window.Toast));
                Assert.Equal(Visibility.Visible, description.ComputedVerticalScrollBarVisibility);
                Assert.True(description.ScrollableHeight > 0);
                Assert.Equal(180, window.Toast.ActualHeight);
                Assert.True(window.ToastClose.Focus());
                WpfContext.PressSpace(window.ToastClose);
                Assert.Equal("first", window.ToastMessage.Text);
                Assert.Same(window.ToastClose, Keyboard.FocusedElement);
                Assert.True(PressEscape(window.ToastClose).Handled);
                Assert.Same(window.CurrentPageTitle, Keyboard.FocusedElement);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Escape_closes_newest_and_each_action_invokes_its_own_handler()
    {
        wpf.Run(() =>
        {
            var (window, _, _) = WorkbenchControlTests.Window();
            try
            {
                var calls = new List<int>();
                void Show() => window.ShowToast(ToastKind.Warning, LocalizedText.Literal("two actions"), "two",
                    () => calls.Add(1), sticky: true, secondAction: () => calls.Add(2), secondActionKey: LocalizedText.Key("Project.ViewLogs"));
                Show();
                window.ShowToast(ToastKind.Info, LocalizedText.Literal("newest"), "one");
                Assert.True(PressEscape(window).Handled);
                Assert.Equal("two actions", window.ToastMessage.Text);
                Assert.True(window.ToastSecondAction.IsVisible);
                UnifiedDesktopTests.ClickControl(window.ToastSecondAction);
                Show();
                UnifiedDesktopTests.ClickControl(window.ToastAction);
                Assert.Equal(new[] { 2, 1 }, calls);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Toast_announces_full_text_and_dismisses_without_stealing_focus_or_blocking_control()
    {
        wpf.Run(() =>
        {
            var elapsed = TimeSpan.Zero;
            var guard = new ControlChannel.ControlInputGuard(() => WorkbenchControlTests.TestNow + elapsed, () => false);
            var (window, _, _) = WorkbenchControlTests.Window(input: guard, toastClock: () => elapsed);
            var announcements = new List<(string Text, AutomationLiveSetting Priority)>();
            void Heard(FrameworkElement source)
            {
                if (source == window.ToastMessage)
                    announcements.Add((AutomationProperties.GetName(source), AutomationProperties.GetLiveSetting(source)));
            }
            AccessibleStatus.Announced += Heard;
            try
            {
                window.CurrentPageTitle.Focus();
                var focus = Keyboard.FocusedElement;
                Assert.Same(window.CurrentPageTitle, focus);
                window.OverlayHost.Visibility = Visibility.Visible;
                string full = "first line\n" + new string('W', 1600);
                window.ShowToast(ToastKind.Error, LocalizedText.Literal(full), "fixture");
                Assert.Equal(Visibility.Collapsed, window.Toast.Visibility);
                Assert.Empty(announcements);
                window.OverlayHost.Visibility = Visibility.Collapsed;
                window.UpdateLayout();
                Assert.Equal((full, AutomationLiveSetting.Assertive), Assert.Single(announcements));
                Assert.Equal(full, window.Toast.ToolTip);
                Assert.Equal(full, AutomationProperties.GetName(window.Toast));
                Assert.Equal(TextWrapping.Wrap, window.ToastMessage.TextWrapping);
                Assert.Equal(TextTrimming.None, window.ToastMessage.TextTrimming);
                Assert.Equal(336, window.Toast.ActualWidth);
                var description = Assert.Single(DesktopCapture.Descendants<ScrollViewer>(window.Toast));
                Assert.Equal(64, description.ViewportHeight);
                Assert.InRange(window.Toast.ActualHeight, 65, 180);
                Assert.Equal(Loc.Current["Toast.Details"], window.ToastAction.Content);
                Assert.Same(focus, Keyboard.FocusedElement);
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(WorkbenchControlTests.Request(WorkbenchControlOperation.ReadState)).Status);
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(WorkbenchControlTests.Request()).Status);
                elapsed += TimeSpan.FromMilliseconds(500);
                Assert.True(window.FindToastCard("ai-activity")!.Surface.IsVisible);
                UnifiedDesktopTests.ClickControl(window.FindToastCard("fixture")!.Action);
                Assert.Equal("Log", window.CurrentPage);
                Assert.Equal(full, window.LogContent.MessageText.Text);
                Assert.Equal(Visibility.Visible, window.LogContent.MessageDetails.Visibility);
                elapsed = TimeSpan.FromMilliseconds(3999);
                window.TickToast();
                Assert.True(window.FindToastCard("ai-activity")!.Surface.IsVisible);
                elapsed += TimeSpan.FromMilliseconds(1);
                window.TickToast();
                Assert.Null(window.FindToastCard("ai-activity"));
                focus = Keyboard.FocusedElement;
                window.ShowToast(ToastKind.Info, LocalizedText.Literal("info"), "fixture");
                Assert.Equal(("info", AutomationLiveSetting.Polite), announcements.Last());
                window.UpdateLayout();
                Assert.Equal(336, window.Toast.ActualWidth);
                Assert.Equal(Visibility.Collapsed, window.ToastAction.Visibility);
                var escape = PressEscape(window);
                Assert.True(escape.Handled);
                Assert.Equal(Visibility.Collapsed, window.Toast.Visibility);
                Assert.Same(focus, Keyboard.FocusedElement);
                window.ShowToast(ToastKind.Success, LocalizedText.Literal("done"), "fixture");
                UnifiedDesktopTests.ClickControl(window.ToastClose);
                Assert.Equal(Visibility.Collapsed, window.Toast.Visibility);
                Assert.Same(focus, Keyboard.FocusedElement);
                window.ShowToast(ToastKind.Info, LocalizedText.Literal("short body"), "fixture", title: LocalizedText.Literal(new string('M', 20)));
                window.UpdateLayout();
                Assert.True(window.ToastTitle.ActualWidth > 168);
                Assert.Equal(336, window.Toast.ActualWidth);
                Assert.True(window.ToastMessage.ActualWidth > 0);
            }
            finally { AccessibleStatus.Announced -= Heard; window.Close(); }
        });
    }

    internal static KeyEventArgs PressEscape(UIElement target)
    {
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(key);
        if (!key.Handled) { key.RoutedEvent = Keyboard.KeyDownEvent; target.RaiseEvent(key); }
        return key;
    }

    [Fact]
    public void Environment_completion_above_settings_consumes_one_escape_before_focus_returns_to_log()
    {
        wpf.Run(() =>
        {
            var (window, _, _) = WorkbenchControlTests.Window(toastClock: () => TimeSpan.Zero);
            try
            {
                var environment = Assert.IsType<FeaturePageFixtures.Environment>(window.Features.Environment);
                window.Navigate("Environment");
                Assert.Equal(1, environment.RecheckCount);
                window.Navigate("Log");
                Assert.True(window.RailLog.Focus());
                window.OpenSettings();
                WpfContext.Drain();
                environment.Complete(environment.Groups);
                WpfContext.Drain();
                Assert.Equal("environment", window.NewestCard.Notice?.Source);
                var section = window.SettingsContent.AppearanceSection;
                Assert.Same(section, Keyboard.FocusedElement);
                Assert.True(PressEscape(section).Handled);
                Assert.Null(window.NewestCard.Notice);
                Assert.True(window.SettingsOpen);
                Assert.False(window.RailLog.IsChecked);
                Assert.Same(section, Keyboard.FocusedElement);
                Assert.True(PressEscape(section).Handled);
                Assert.False(window.SettingsOpen);
                Assert.True(window.RailLog.IsChecked);
                Assert.Same(window.RailLog, Keyboard.FocusedElement);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Failed_approval_save_shows_an_interactive_error_above_settings_immediately()
    {
        wpf.Run(() =>
        {
            TimeSpan now = TimeSpan.Zero;
            var window = new MainWindow(ProjectPageTestSupport.Model(), false, FeaturePageFixtures.Approvals(false), toastClock: () => now)
                { Width = 1024, Height = 640 };
            try
            {
                WpfContext.ShowWindow(window); WpfContext.Drain();
                window.ShowToast(ToastKind.Info, LocalizedText.Literal("temporary"), "timer");
                window.OpenSettings(); WpfContext.Drain();
                Assert.True(window.Toast.IsVisible);
                now += TimeSpan.FromSeconds(5); window.TickToast();
                Assert.Equal(Visibility.Collapsed, window.Toast.Visibility);

                bool invoked = false;
                window.ShowToast(ToastKind.Info, LocalizedText.Literal("previous"), "previous", () => invoked = true);
                WpfContext.Complete(window.SettingsContent.SaveApproval(() => throw new IOException("Save failed")));
                Assert.True(window.SettingsOpen);
                Assert.True(window.Toast.IsVisible);
                Assert.Equal("Save failed", window.ToastMessage.Text);
                Assert.Same(window.FindResource("Ant.colorError"), window.ToastDot.Fill);
                window.UpdateLayout();
                void AssertClickable(Button button)
                {
                    Assert.True(button.IsEnabled);
                    var middle = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), window.Root);
                    var hit = Assert.IsAssignableFrom<DependencyObject>(window.Root.InputHitTest(middle));
                    while (hit is FrameworkContentElement content) hit = ContentOperations.GetParent(content) ?? content.Parent;
                    Assert.True(ReferenceEquals(hit, button) || button.IsAncestorOf(hit));
                }
                AssertClickable(window.ToastClose);
                if (Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR") is { Length: > 0 } output)
                {
                    Directory.CreateDirectory(output);
                    WorkbenchRenderTests.SaveRender(window.Root, Path.Combine(output, "settings-failed-save-1024x640.png"), 1024, 640);
                }
                Assert.True(PressEscape(window.SettingsContent.AppearanceSection).Handled);
                Assert.Equal("previous", window.ToastMessage.Text);
                Assert.True(window.SettingsOpen);
                window.UpdateLayout();
                AssertClickable(window.ToastAction);
                UnifiedDesktopTests.ClickControl(window.ToastAction);
                Assert.True(invoked);
                Assert.True(window.SettingsOpen);
                Assert.Equal(Visibility.Collapsed, window.Toast.Visibility);
                Assert.True(PressEscape(window.SettingsContent.AppearanceSection).Handled);
                Assert.False(window.SettingsOpen);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Escape_closes_the_open_combo_before_dismissing_a_visible_toast()
    {
        wpf.Run(() =>
        {
            var (window, _, _) = WorkbenchControlTests.Window();
            try
            {
                window.Navigate("Calls");
                UnifiedDesktopTests.ClickControl(window.CallsContent.FilterToggle);
                var combo = window.CallsContent.ReleaseFilter;
                Assert.True(combo.IsVisible);
                window.UpdateLayout();
                combo.Focus();
                combo.IsDropDownOpen = true;
                WpfContext.Drain();
                window.ShowToast(ToastKind.Info, LocalizedText.Literal("fixture"), "combo");
                Assert.True(combo.IsDropDownOpen);
                Assert.True(PressEscape(combo).Handled);
                Assert.False(combo.IsDropDownOpen);
                Assert.Equal(Visibility.Visible, window.Toast.Visibility);
                Assert.True(PressEscape(combo).Handled);
                WpfContext.Drain();
                Assert.False(window.CallsContent.FilterPopup.IsOpen);
                Assert.Same(window.CallsContent.FilterToggle, Keyboard.FocusedElement);
                Assert.Equal(Visibility.Visible, window.Toast.Visibility);
                Assert.True(PressEscape(window.CallsContent.FilterToggle).Handled);
                Assert.Equal(Visibility.Collapsed, window.Toast.Visibility);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("multiline")]
    [InlineData("truncated")]
    [InlineData("truncated-with-action")]
    [InlineData("action")]
    public void Shortcut_opens_full_text_or_invokes_the_visible_action_from_the_focused_control(string site)
    {
        wpf.Run(() =>
        {
            var (window, _, _) = WorkbenchControlTests.Window();
            try
            {
                window.CurrentPageTitle.Focus();
                var focus = Keyboard.FocusedElement;
                string full = site == "multiline" ? "First\nSecond" : site == "action" ? "Short action" : new string('W', 800);
                IInputElement? atInvocation = null;
                Action? action = site is "action" or "truncated-with-action" ? () => atInvocation = Keyboard.FocusedElement : null;
                window.ShowToast(ToastKind.Info, LocalizedText.Literal(full), "hint", action);
                window.UpdateLayout();
                Assert.Equal(Visibility.Visible, window.ToastAction.Visibility);
                Assert.Equal(Loc.Current[site == "action" ? "Common.Open" : "Toast.Details"], window.ToastAction.Content);
                Assert.Contains("Ctrl+Shift+J", AutomationProperties.GetHelpText(window.ToastMessage));
                Assert.Equal(full, AutomationProperties.GetName(window.ToastMessage));
                var binding = window.InputBindings.OfType<KeyBinding>()
                    .Single(b => b.Key == Key.J && b.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift));
                var command = Assert.IsType<RoutedUICommand>(binding.Command);
                Assert.Same(focus, Keyboard.FocusedElement);
                Assert.True(command.CanExecute(null, focus));
                command.Execute(null, focus);
                if (site == "action") Assert.Same(focus, atInvocation);
                else
                {
                    Assert.Null(atInvocation);
                    Assert.Equal("Log", window.CurrentPage);
                    Assert.Equal(full, window.LogContent.MessageText.Text);
                }
                Assert.Equal(Visibility.Collapsed, window.Toast.Visibility);
                Assert.False(command.CanExecute(null, window));
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("Save", "Primer.ok")]
    [InlineData("Inspect", "Primer.ok")]
    [InlineData("InspectWarning", "Primer.warn")]
    [InlineData("InspectError", "Ant.colorError")]
    [InlineData("Compile", "Primer.ok")]
    [InlineData("CompileWarning", "Primer.warn")]
    [InlineData("CompileError", "Ant.colorError")]
    [InlineData("Export", "Primer.ok")]
    [InlineData("PartialExport", "Ant.colorError")]
    [InlineData("ReadTagsError", "Ant.colorError")]
    [InlineData("ReadBlockError", "Ant.colorError")]
    [InlineData("Import", "Primer.ok")]
    [InlineData("ImportError", "Ant.colorError")]
    [InlineData("Copy", "Primer.ok")]
    [InlineData("CopyError", "Ant.colorError")]
    public void Operations_publish_their_outcome_in_the_shell(string operation, string color)
    {
        wpf.Run(() =>
        {
            var client = new FakeProjectClient();
            var dialogs = new FakeDialogService { Result = DialogResult.OK, Files = ["block.xml"] };
            var model = new MainViewModel(client, dialogs);
            var engineering = model.Engineering;
            engineering.SelectedDevice = client.Devices[0];
            engineering.OutputDirectory = @"D:\fixture";
            var window = new MainWindow(model, false);
            try
            {
                string expected;
                switch (operation)
                {
                    case "Copy":
                    case "CopyError":
                        window.Features.CopyText = _ => { if (operation == "CopyError") throw new InvalidOperationException("Clipboard busy"); };
                        window.Features.Copy("fixture");
                        expected = operation == "Copy" ? Loc.Current["Toast.Copied"] : "Clipboard busy";
                        break;
                    case "Compile": case "CompileError": case "CompileWarning":
                        client.CompileResult.ErrorCount = operation == "CompileError" ? 1 : 0;
                        client.CompileResult.WarningCount = operation == "CompileWarning" ? 1 : 0;
                        engineering.Compile.Execute(null);
                        expected = Loc.Current.T("Status.CompileResult", client.CompileResult.State, client.CompileResult.ErrorCount,
                            client.CompileResult.WarningCount, client.CompileResult.Duration.TotalSeconds.ToString("F1"));
                        break;
                    case "Import": case "ImportError":
                        if (operation == "ImportError") client.ImportError = new InvalidOperationException("Import failed");
                        engineering.Import.Execute(null);
                        expected = operation == "Import" ? Loc.Current.T("Status.Imported", 0, 0) : "Import failed";
                        break;
                    case "ReadTagsError":
                        client.TagHandler = _ => Task.FromException<List<TiaOpenness.Contracts.Models.TagInfo>>(new IOException("Tag read failed"));
                        WpfContext.Complete(engineering.ReadTagsAsync());
                        expected = "Tag read failed"; Assert.Equal(expected, engineering.TagStatus); break;
                    case "ReadBlockError":
                        WpfContext.Complete(engineering.LoadDevicesAsync("Fixture"));
                        while (window.Toast.Visibility == Visibility.Visible) UnifiedDesktopTests.ClickControl(window.ToastClose);
                        engineering.FocusedBlock = engineering.Blocks.Single();
                        client.LogicHandler = (_, _) => Task.FromException<string>(new IOException("Source read failed"));
                        WpfContext.Complete(engineering.ReadFocusedBlockAsync());
                        expected = "Source read failed"; Assert.Equal(expected, engineering.SourceStatus); break;
                    case "PartialExport":
                        client.ExportHandler = (_, _, folder, _) => new() { Requested = 3, Succeeded = 2, Failed = 1, OutputDirectory = folder };
                        engineering.Export.Execute(null);
                        expected = Loc.Current.T("Status.ExportedFailed", 2, 3, engineering.OutputDirectory, 1); break;
                    case "Export":
                        engineering.Export.Execute(null);
                        expected = Loc.Current.T("Status.Exported", 0, 0, engineering.OutputDirectory);
                        break;
                    case "Inspect": case "InspectWarning": case "InspectError":
                        if (operation != "Inspect") client.Findings.Add(new()
                        {
                            RuleId = "Fixture", Severity = operation == "InspectWarning" ? Contracts.Models.CheckStatus.Warn : Contracts.Models.CheckStatus.Fail
                        });
                        engineering.Inspect.Execute(null);
                        expected = Loc.Current.T("Status.InspectResult", client.Findings.Count, 0); break;
                    default:
                        engineering.Save.Execute(null); expected = Loc.Current["Status.ProjectSaved"]; break;
                }
                Assert.Equal(Visibility.Visible, window.Toast.Visibility);
                Assert.Equal(expected, window.ToastMessage.Text);
                Assert.Same(window.FindResource(color), window.ToastDot.Fill);
                var options = window.OperationsContent.Options;
                Assert.DoesNotContain(DesktopCapture.Descendants<TextBlock>(options), text =>
                    System.Windows.Data.BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path.Path == "Activity.Status");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ProjectDocument.Render, "Engineering.SourceStatus")]
    [InlineData(ProjectDocument.Tags, "Engineering.TagStatus")]
    public void Failed_reads_leave_the_reason_in_the_empty_panel_after_the_toast_expires(ProjectDocument document, string binding)
    {
        wpf.Run(() =>
        {
            const string failure = "Read interrupted.";
            var model = ProjectPageTestSupport.Model(configure: client =>
            {
                client.LogicHandler = (_, _) => Task.FromException<string>(new IOException(failure));
                client.TagHandler = _ => Task.FromException<List<TiaOpenness.Contracts.Models.TagInfo>>(new IOException(failure));
            });
            var elapsed = TimeSpan.Zero;
            var window = new MainWindow(model, false, toastClock: () => elapsed);
            try
            {
                WpfContext.ShowWindow(window);
                window.Navigate("Engineering");
                var editor = model.Engineering;
                editor.FocusedBlock = editor.Blocks[1];
                editor.Document = document;
                if (document == ProjectDocument.Tags) WpfContext.Complete(editor.ReadTagsAsync());
                else WpfContext.Complete(editor.ReadFocusedBlockAsync());
                window.UpdateLayout();
                TextBlock panel;
                if (document == ProjectDocument.Render)
                {
                    var state = window.BlocksContent.SourceError;
                    Assert.Equal(binding, System.Windows.Data.BindingOperations.GetBinding(state, WorkbenchState.DescriptionProperty)?.Path.Path);
                    panel = DesktopCapture.Descendants<TextBlock>(state).Single(text => text.IsVisible && text.Text == failure);
                }
                else panel = DesktopCapture.Descendants<TextBlock>(window).Single(text =>
                    text.IsVisible && System.Windows.Data.BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path.Path == binding);
                Assert.Equal(failure, panel.Text);
                Assert.Equal(Visibility.Visible, window.Toast.Visibility);
                Assert.Equal(failure, window.ToastMessage.Text);
                elapsed += TimeSpan.FromSeconds(8);
                window.TickToast();
                Assert.Equal(Visibility.Collapsed, window.Toast.Visibility);
                Assert.True(panel.IsVisible);
                Assert.Equal(failure, panel.Text);
                Assert.Empty(editor.SourceLines);
                Assert.Empty(editor.PlcTags);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("", "Project.NoSource", 0)]
    [InlineData("one", "Project.OneLine", 1)]
    [InlineData("one\ntwo", "Project.Lines", 2)]
    public void Reads_update_the_page_and_activity_without_emitting_success_toasts(string source, string key, int lines)
    {
        wpf.Run(() =>
        {
            var client = new FakeProjectClient { LogicHandler = (_, _) => Task.FromResult(source) };
            var model = new MainViewModel(client, new FakeDialogService());
            var window = new MainWindow(model, false);
            try
            {
                var feedback = new List<FeatureFeedback>();
                model.Activity.Feedback += (_, message) => feedback.Add(message);
                WpfContext.Complete(model.Engineering.LoadDevicesAsync("Fixture"));
                Assert.Equal(Loc.Current.T("Status.BlocksIn", 1, client.Devices[0].Id), model.Activity.Status);
                WpfContext.Complete(model.Engineering.ReadTagsAsync());
                Assert.Equal(Loc.Current.T("Project.TagsRead", 0), model.Activity.Status);
                model.Engineering.FocusedBlock = Assert.Single(model.Engineering.Blocks);
                WpfContext.Complete(model.Engineering.ReadFocusedBlockAsync());
                Assert.Equal(lines, model.Engineering.SourceLines.Count);
                Assert.Equal(Loc.Current.T(key, lines), model.Activity.Status);
                Assert.Empty(feedback);
                Assert.Equal(Visibility.Collapsed, window.Toast.Visibility);
                model.Activity.SetStatus("Status.NotConnected");
                Assert.Equal(ToastKind.Info, Assert.Single(feedback).Kind);
                Assert.Same(window.FindResource("Primer.accent"), window.ToastDot.Fill);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Automatic_workspace_reads_update_status_without_a_toast()
    {
        wpf.Run(() =>
        {
            var client = new FakeProjectClient();
            using var model = new MainViewModel(client, new FakeDialogService());
            var feedback = new List<FeatureFeedback>();
            model.Activity.Feedback += (_, message) => feedback.Add(message);
            var vc = model.VersionControl;
            WpfContext.Complete(vc.RefreshWorkspaceAsync(requested: false));
            Assert.Equal(Loc.Current.T("Status.VcInSync", client.StatusItems.Count), model.Activity.Status);
            client.DifferingItems = 1;
            WpfContext.Complete(vc.RefreshWorkspaceAsync(requested: false));
            Assert.Equal(Loc.Current.T("Status.VcDiffer", client.StatusItems.Count, 1), model.Activity.Status);
            client.Workspaces.Clear();
            WpfContext.Complete(vc.RefreshWorkspaceAsync(requested: false));
            Assert.Equal(Loc.Current["Status.VcNoWorkspace"], model.Activity.Status);
            client.VcSupported = false;
            WpfContext.Complete(vc.RefreshWorkspaceAsync(requested: false));
            Assert.Equal(Loc.Current["Status.VcUnsupported"], model.Activity.Status);
            Assert.Empty(feedback);
        });
    }

    [Theory]
    [InlineData("unsupported", "Primer.warn")]
    [InlineData("no-workspace", "Primer.accent")]
    [InlineData("different", "Primer.accent")]
    [InlineData("map-error", "Ant.colorError")]
    [InlineData("sync-error", "Ant.colorError")]
    public void Workspace_operations_show_the_result_of_the_requested_command(string site, string color)
    {
        wpf.Run(() =>
        {
            var client = new FakeProjectClient();
            var model = new MainViewModel(client, new FakeDialogService());
            var vc = model.VersionControl;
            WpfContext.Complete(vc.VcRefreshAsync());
            var window = new MainWindow(model, false);
            try
            {
                string expected;
                switch (site)
                {
                    case "unsupported": case "no-workspace":
                        client.VcSupported = site != "unsupported";
                        client.Workspaces.Clear();
                        WpfContext.Complete(vc.VcRefreshAsync());
                        expected = Loc.Current[site == "unsupported" ? "Status.VcUnsupported" : "Status.VcNoWorkspace"];
                        break;
                    case "different":
                        client.DifferingItems = 1;
                        vc.VcStatus.Execute(null);
                        expected = Loc.Current.T("Status.VcDiffer", client.StatusItems.Count, 1); break;
                    case "map-error":
                        client.FailedMappings = 1; vc.VcDryRun = false;
                        vc.VcMap.Execute(null);
                        expected = Loc.Current.T("Status.VcMapApplied", 0, 0, 0, 1); break;
                    default:
                        client.FailedSynchronizations = 1; vc.VcDryRun = false;
                        vc.VcPush.Execute(null);
                        expected = Loc.Current.T("Status.VcSyncApplied", 0, 1, 0); break;
                }
                Assert.Equal(Visibility.Visible, window.Toast.Visibility);
                Assert.Equal(expected, window.ToastMessage.Text);
                Assert.Same(window.FindResource(color), window.ToastDot.Fill);
            }
            finally { window.Close(); }
        });
    }

    // Exercise the configuration completion boundary without launching a server, updater or elevated firewall helper.
    [Theory]
    [InlineData("Config.Saved", "ConfigurationView.xaml.Clients.cs", "Primer.ok")]
    [InlineData("Config.ClientsConfigured", "ConfigurationView.xaml.Clients.cs", "Primer.ok")]
    [InlineData("Config.Testing", "ConfigurationView.xaml.Clients.cs", "Primer.accent")]
    [InlineData("Config.ConfiguringNetwork", "ConfigurationView.xaml.Server.cs", "Primer.accent")]
    [InlineData("Config.NetworkConfigured", "ConfigurationView.xaml.Server.cs", "Primer.ok")]
    [InlineData("Config.ServiceStopped", "ConfigurationView.xaml.Server.cs", "Primer.ok")]
    [InlineData("Config.ServiceRunning", "ConfigurationView.xaml.Server.cs", "Primer.ok")]
    [InlineData("Hosts.Restarted", "ConfigurationHosts.cs", "Primer.ok")]
    [InlineData("Hosts.Stopped", "ConfigurationHosts.cs", "Primer.ok")]
    public void Configuration_completion_uses_the_shell_without_an_inline_slot(string key, string file, string color)
    {
        wpf.Run(() =>
        {
            var window = new MainWindow(ProjectPageTestSupport.Model(connected: false), false,
                preview: new TiaMcpConfigurator.ConfigurationPreview("21", @"C:\fixture", "127.0.0.1", false, false, true, "", "", []));
            try
            {
                window.ShowConfiguration(false);
                var config = window.Configuration!;
                var producer = SourceScan.Code.Single(source => source.Name.EndsWith(file, StringComparison.Ordinal));
                Assert.Contains("\"" + key + "\"", producer.Text);
                config.SetStatus(key, 3);
                Assert.Equal(Visibility.Visible, window.Toast.Visibility);
                Assert.Equal(Loc.Current.T(key, 3), window.ToastMessage.Text);
                Assert.Same(window.FindResource(color), window.ToastDot.Fill);
                Assert.Null(config.FindName("Status"));
                Assert.Null(config.Clients.FindName("ClientStatus"));
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("update", "Primer.accent")]
    [InlineData("current", "Primer.ok")]
    [InlineData("update-error", "Ant.colorError")]
    [InlineData("detected", "Primer.ok")]
    [InlineData("not-detected", "Primer.warn")]
    public void Explicit_update_and_detection_requests_report_the_result(string site, string color)
    {
        wpf.Run(() =>
        {
            var preview = new TiaMcpConfigurator.ConfigurationPreview("21", @"C:\fixture", "127.0.0.1", false, false, true, "", "", []);
            var window = new MainWindow(ProjectPageTestSupport.Model(connected: false), false, preview: preview);
            try
            {
                window.ShowConfiguration(false);
                var config = window.Configuration!;
                string expected;
                if (site is "detected" or "not-detected")
                {
                    bool found = site == "detected";
                    string folder = preview.InstallPath;
                    config.PresentDetection(new(found ? folder : null!, "fixture"), true);
                    expected = found ? Loc.Current.T("Config.TiaDetected", config.SelectedReleaseKey, "fixture", folder)
                        : Loc.Current.T("Config.TiaNotDetected", "fixture", config.SelectedReleaseKey);
                }
                else
                {
                    var info = new TiaMcpConfigurator.UpdateInfo
                    {
                        Installed = "1.0.0", Latest = site == "update" ? "1.0.1" : "1.0.0", Tag = "v1.0.0", Source = "fixture"
                    };
                    WpfContext.Complete(config.CheckUpdateAsync(true, info.Installed,
                        () => site == "update-error" ? throw new IOException("Unavailable") : info));
                    expected = site switch
                    {
                        "update" => Loc.Current.T("Config.UpdateAvailable", info.Latest, ""),
                        "update-error" => Loc.Current.T("Config.UpdateCheckFailed", "Unavailable"),
                        _ => Loc.Current.T("Config.UpToDate", info.Tag)
                    };
                }
                Assert.Equal(Visibility.Visible, window.Toast.Visibility);
                Assert.Same(window.FindResource(color), window.ToastDot.Fill);
                Assert.Equal(expected, window.Toast.ToolTip);
                Assert.Null(config.FindName("Status"));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Dispatcher_error_is_readable_in_full_from_the_active_shell()
    {
        wpf.Run(() =>
        {
            var shell = Application.Current.MainWindow;
            var window = new MainWindow(ProjectPageTestSupport.Model(connected: false), false);
            try
            {
                Application.Current.MainWindow = window;
                const string details = "Fixture error\n\nCrash log detail";
                App.PresentUiError(details);
                Assert.Equal(Visibility.Visible, window.Toast.Visibility);
                Assert.Equal(details, AutomationProperties.GetName(window.ToastMessage));
                Assert.Same(window.FindResource("Ant.colorError"), window.ToastDot.Fill);
                UnifiedDesktopTests.ClickControl(window.ToastAction);
                Assert.Equal(details, window.LogContent.MessageText.Text);
            }
            finally { Application.Current.MainWindow = shell; window.Close(); }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Configuration_loading_failures_are_shown_by_both_navigation_commands(bool configurationShortcut)
    {
        wpf.Run(() =>
        {
            var preview = new TiaMcpConfigurator.ConfigurationPreview("21", @"C:\fixture", "127.0.0.1", false, false, true, "", null!, []);
            var window = new MainWindow(ProjectPageTestSupport.Model(connected: false), false, preview: preview);
            try
            {
                if (configurationShortcut) WorkbenchCommands.Configuration.Execute(null, window);
                else WorkbenchCommands.Navigate.Execute("Mcp", window);
                Assert.Equal("Engineering", window.CurrentPage);
                Assert.Equal(Visibility.Visible, window.Toast.Visibility);
                Assert.Same(window.FindResource("Ant.colorError"), window.ToastDot.Fill);
                Assert.False(string.IsNullOrWhiteSpace(window.ToastMessage.Text));
                Assert.Equal(Loc.Current["Toast.Details"], window.ToastAction.Content);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("guidance", "Primer.accent")]
    [InlineData("config-log", "Primer.ok")]
    [InlineData("bad-port", "Ant.colorError")]
    [InlineData("config-busy", "Primer.warn")]
    public void Client_guidance_failed_submission_and_busy_close_use_shell_feedback(string site, string color)
    {
        wpf.Run(() =>
        {
            var preview = new TiaMcpConfigurator.ConfigurationPreview("21", @"C:\fixture", "127.0.0.1", false, false, true, "", "",
                [new("codex", "Codex", @"C:\fixture\codex.toml", "Fixture one"), new("vscode", "VS Code", @"C:\fixture\vscode.json", "Fixture two")]);
            var window = new MainWindow(ProjectPageTestSupport.Model(connected: false), false, preview: preview);
            try
            {
                window.ShowConfiguration(false);
                WpfContext.ShowWindow(window);
                var config = window.Configuration!;
                if (site == "guidance")
                {
                    window.Navigate("Clients");
                    config.Clients.ClientChoices.SelectAll();
                    config.Clients.ClientSelection.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                        { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                    Assert.Equal(config.Clients.ClientSelection.ToolTip, window.Toast.ToolTip);
                }
                else if (site == "config-log")
                {
                    UnifiedDesktopTests.ClickControl(config.ClearLog);
                    Assert.Equal(Loc.Current["Toast.Cleared"], window.ToastMessage.Text);
                    Assert.Empty(config.ActivityLogText);
                }
                else if (site == "bad-port")
                {
                    config.ServerPort.Text = "not-a-port";
                    UnifiedDesktopTests.ClickControl(config.TestClient);
                    Assert.Equal(Loc.Current["Toast.Details"], window.ToastAction.Content);
                    Assert.Contains("not-a-port", window.ToastMessage.Text);
                    Assert.Equal(Visibility.Visible, config.EndpointError.Visibility);
                    Assert.Equal(Loc.Current["Config.InvalidEndpoint"], AutomationProperties.GetHelpText(config.ServerPort));
                    config.ServerPort.Text = "8765";
                    Assert.Equal(Visibility.Collapsed, config.EndpointError.Visibility);
                }
                else
                {
                    var setBusy = typeof(TiaMcpConfigurator.ConfigurationView).GetMethod("SetBusy",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    setBusy.Invoke(config, [true]);
                    try
                    {
                        window.Close();
                        Assert.True(window.IsVisible);
                        Assert.Equal(Loc.Current["Config.WaitBeforeClosing"], window.ToastMessage.Text);
                    }
                    finally { setBusy.Invoke(config, [false]); }
                }
                Assert.Equal(Visibility.Visible, window.Toast.Visibility);
                Assert.Same(window.FindResource(color), window.ToastDot.Fill);
                Assert.Null(config.FindName("Status"));
                Assert.Null(config.Clients.FindName("ClientStatus"));
                if (site == "guidance")
                {
                    var full = Assert.IsType<string>(window.Toast.ToolTip);
                    Assert.Contains("\n", full);
                    Assert.Equal(Loc.Current["Toast.Details"], window.ToastAction.Content);
                    UnifiedDesktopTests.ClickControl(window.ToastAction);
                    Assert.Equal(full, window.LogContent.MessageText.Text);
                }
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Workbench_pages_have_no_informational_message_boxes()
    {
        var pages = SourceScan.Code.Where(source => !source.Name.EndsWith(".App.xaml.cs", StringComparison.Ordinal)
            && !source.Name.EndsWith(".WorkbenchMessageBox.cs", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(pages);
        foreach (var source in pages)
            Assert.DoesNotMatch(@"MessageBoxButton\s*\.\s*OK\b", source.Text);
    }

    [Theory]
    [InlineData("verify", "Primer.ok")]
    [InlineData("broken-audit", "Ant.colorError")]
    [InlineData("export-diagnostics", "Primer.ok")]
    [InlineData("failed-diagnostics", "Ant.colorError")]
    [InlineData("log-copy", "Primer.ok")]
    [InlineData("clear-log", "Primer.ok")]
    [InlineData("git-help", "Primer.accent")]
    [InlineData("environment-ok", "Primer.ok")]
    [InlineData("environment-warning", "Primer.warn")]
    [InlineData("environment-failed", "Ant.colorError")]
    [InlineData("folder-missing", "Ant.colorError")]
    [InlineData("csv", "Primer.ok")]
    [InlineData("csv-error", "Ant.colorError")]
    [InlineData("retention", "Primer.ok")]
    [InlineData("save-settings", "Primer.ok")]
    [InlineData("save-settings-error", "Ant.colorError")]
    [InlineData("timeout", "Ant.colorError")]
    [InlineData("lifetime", "Ant.colorError")]
    [InlineData("target", "Ant.colorError")]
    public void Page_actions_publish_feedback_and_leave_validation_with_its_field(string site, string color)
    {
        wpf.Run(() =>
        {
            var diagnostics = new FeaturePageFixtures.Diagnostics();
            var audit = new FeaturePageFixtures.Audit { Broken = site == "broken-audit" };
            var environment = new FeaturePageFixtures.Environment();
            var window = new MainWindow(ProjectPageTestSupport.Model(release: "21"), false, FeaturePageFixtures.Approvals(false), diagnostics);
            var language = Loc.Current.Language;
            try
            {
                window.ConfigureFeaturePages(new FeaturePageFixtures.Journal(), audit, environment);
                WpfContext.ShowWindow(window);
                string expected;
                switch (site)
                {
                    case "verify": case "broken-audit":
                        WpfContext.Complete(window.Features.VerifyAsync());
                        expected = window.Features.VerificationText;
                        Assert.Null(window.AuditContent.FindName("VerificationBanner"));
                        break;
                    case "export-diagnostics": case "failed-diagnostics":
                        window.Features.Export();
                        if (site == "export-diagnostics") diagnostics.Complete();
                        else diagnostics.Progress = new(DiagnosticState.Idle, LocalizedText.Literal("Archive failed"));
                        WpfContext.Drain();
                        expected = site == "export-diagnostics" ? Loc.Current.T("Toast.Exported", diagnostics.Progress.Path) : "Archive failed";
                        break;
                    case "log-copy":
                        window.Navigate("Log"); window.UpdateLayout();
                        window.Features.CopyText = _ => { };
                        UnifiedDesktopTests.ClickControl(window.LogContent.MoreActions);
                        Assert.True(window.LogContent.MoreActions.ContextMenu.IsOpen);
                        window.LogContent.CopyLogButton.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                        window.LogContent.MoreActions.ContextMenu.IsOpen = false;
                        expected = Loc.Current["Toast.Copied"];
                        break;
                    case "clear-log":
                        window.LogContent.ConfirmClear = () => true;
                        UnifiedDesktopTests.ClickControl(window.LogContent.ClearLogButton);
                        expected = Loc.Current["Toast.Cleared"];
                        break;
                    case "environment-ok": case "environment-warning": case "environment-failed":
                        var group = environment.Groups[0];
                        var result = site switch { "environment-ok" => CheckStatus.Pass, "environment-warning" => CheckStatus.Warn, _ => CheckStatus.Fail };
                        environment.Complete([group with { Rows = [group.Rows[0] with { Status = result }] }]);
                        WpfContext.Drain();
                        expected = window.Features.EnvironmentSummary; break;
                    case "save-settings": case "save-settings-error":
                        window.Navigate("Settings");
                        WpfContext.Complete(window.SettingsContent.SaveApproval(() =>
                        {
                            if (site == "save-settings-error") throw new IOException("Settings unavailable");
                        }));
                        expected = site == "save-settings" ? Loc.Current["Toast.Saved"] : "Settings unavailable"; break;
                    case "folder-missing":
                        window.Navigate("Settings");
                        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
                        window.SettingsContent.ConfigPath.Text = missing;
                        UnifiedDesktopTests.ClickControl(window.SettingsContent.OpenConfig);
                        expected = Loc.Current.T("Settings.PathMissing", missing); break;
                    case "retention":
                        WpfContext.Complete(window.Features.SetRetentionAsync(20, true));
                        Assert.Equal(20, audit.FileSizeMb);
                        expected = Loc.Current["Toast.Saved"]; break;
                    case "csv": case "csv-error":
                        var tags = (TiaOpenness.Gui.Views.PlcTagsView)window.PlcTagsContent.Content;
                        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
                        if (site == "csv-error") file = Path.Combine(file, "missing", "tags.csv");
                        try
                        {
                            tags.SaveCsv(file);
                            if (site == "csv")
                            {
                                Assert.StartsWith("Name,DataType,LogicalAddress,Table,Comment", File.ReadAllText(file));
                                expected = Loc.Current.T("Toast.Exported", file);
                            }
                            else
                            {
                                expected = window.ToastMessage.Text;
                                Assert.Contains(file, expected);
                                Assert.Equal(Loc.Current["Toast.Details"], window.ToastAction.Content);
                            }
                        }
                        finally { if (File.Exists(file)) File.Delete(file); }
                        break;
                    case "git-help":
                        Loc.Current.Language = AppLanguage.English;
                        ((MainViewModel)window.DataContext).SelectedReleaseKey = "21";
                        window.Navigate("VersionControl"); window.UpdateLayout();
                        UnifiedDesktopTests.ClickControl(window.VersionControlContent.GitToolsButton);
                        expected = Loc.Current["Pages.GitHint"];
                        break;
                    default:
                        window.Navigate("Settings");
                        var settings = window.SettingsContent;
                        (TextBox input, Button submit, TextBlock label, string key) = site switch
                        {
                            "timeout" => (settings.CustomTimeout, settings.ApplyTimeout, settings.CustomTimeoutError, "Settings.TimeoutInvalid"),
                            "lifetime" =>
                                (settings.OnlinePlanLifetime, settings.ApplyPlanLifetime, settings.OnlinePlanLifetimeError, "Settings.PlanLifetimeInvalid"),
                            _ => (settings.OnlineTargetInput, settings.AddTarget, settings.OnlineTargetInputError, "Settings.TargetInvalid")
                        };
                        input.Text = "invalid";
                        UnifiedDesktopTests.ClickControl(submit);
                        expected = Loc.Current[key];
                        Assert.Equal(expected, label.Text);
                        Assert.Equal(Visibility.Visible, label.Visibility);
                        Assert.Equal(expected, AutomationProperties.GetHelpText(input));
                        input.Text = "";
                        Assert.Equal(Visibility.Collapsed, label.Visibility);
                        break;
                }
                if (window.SettingsOpen)
                {
                    Assert.True(window.Toast.IsVisible);
                    Assert.True(window.ToastClose.IsEnabled);
                }
                Assert.Equal(Visibility.Visible, window.Toast.Visibility);
                Assert.Same(window.FindResource(color), window.ToastDot.Fill);
                Assert.Equal(expected, window.Toast.ToolTip);
                if (site == "git-help")
                {
                    Assert.Equal(Visibility.Visible, window.ToastAction.Visibility);
                    UnifiedDesktopTests.ClickControl(window.ToastAction);
                    Assert.Equal(expected, window.LogContent.MessageText.Text);
                }
            }
            finally { window.Close(); Loc.Current.Language = language; }
        });
    }

    [Fact]
    public void Docked_inspector_reaches_the_right_edge_of_the_calls_page_gutter()
    {
        wpf.Run(() =>
        {
            var (window, _, _) = WorkbenchControlTests.Window();
            try
            {
                var host = WorkbenchRenderTests.CreateHost(window, 1366, 780);
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(WorkbenchControlTests.Request(WorkbenchControlOperation.DisplayCall)).Status);
                WpfContext.Drain(); host.UpdateLayout();
                Assert.Same(window.DockedInspector, window.DrawerPanel.Parent);
                double right = window.CallsPage.TranslatePoint(new Point(window.CallsPage.ActualWidth, 0), host).X;
                Assert.Equal(right + 16, window.DrawerPanel.TranslatePoint(new Point(window.DrawerPanel.ActualWidth, 0), host).X);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(CallResult.Success, "Primer.ok")]
    [InlineData(CallResult.Rejected, "Primer.warn")]
    [InlineData(CallResult.Failed, "Ant.colorError")]
    [InlineData(CallResult.Partial, "Ant.colorError")]
    [InlineData(CallResult.Unknown, "Ant.colorError")]
    public void New_AI_completion_opens_the_exact_call_from_another_page(CallResult result, string color)
    {
        wpf.Run(() =>
        {
            var journal = new FeaturePageFixtures.Journal();
            var window = new MainWindow(ProjectPageTestSupport.Model(), false);
            try
            {
                window
                    .ConfigureFeaturePages(journal, new FeaturePageFixtures.Audit(), new FeaturePageFixtures.Environment(), now: () => FeaturePageFixtures.Now);
                window.Navigate("Log");
                window.Features.Search = "keep-my-filter";
                Assert.Equal(Visibility.Collapsed, window.Toast.Visibility);
                var call = journal.Calls[0] with
                {
                    Actor = "mcp", JournalKey = "new-ai", RequestId = "new-ai", Result = result,
                    Time = FeaturePageFixtures.Now.AddMinutes(-1), Updated = FeaturePageFixtures.Now
                };
                journal.Replace([call, .. journal.Calls]);
                WpfContext.Drain();
                Assert.Equal(Visibility.Visible, window.Toast.Visibility);
                Assert.Same(window.FindResource(color), window.ToastDot.Fill);
                Assert.Contains(call.Tool, window.ToastMessage.Text);
                UnifiedDesktopTests.ClickControl(window.ToastAction);
                Assert.Equal("Calls", window.CurrentPage);
                Assert.Equal(call.JournalKey, window.Features.SelectedCall?.Record.JournalKey);
                Assert.Equal("keep-my-filter", window.Features.Search);
                Assert.Equal(Visibility.Collapsed, window.Toast.Visibility);
            }
            finally { window.Close(); }
        });
    }
}
