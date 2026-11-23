extern alias PlcRendering;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Threading.Tasks;
using PlcRendering::TiaMcp.Logic.V4;
using TiaMcp.Engine.Tests.Shared;
using TiaOpenness.Gui.Services;
using TiaOpenness.Shared;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Views;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class SafetySettingsViewTests(WpfContext wpf)
{
    [Theory]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Chinese)]
    public void Approval_hint_shows_each_host_category_in_its_confirmation_group(AppLanguage language)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var view = new SettingsView();
            var approvals = new Services.Stubs.ApprovalServiceStub();
            try
            {
                view.Initialize(approvals);
                foreach (bool enabled in new[] { true, false })
                {
                    approvals.Enabled = enabled;
                    WpfContext.Drain();
                    Assert.Equal(Visibility.Visible, view.ApprovalRuleHint.Visibility);
                    Assert.Equal(TextWrapping.Wrap, view.ApprovalRuleHint.TextWrapping);
                    string hint = view.ApprovalRuleHint.Text;
                    Assert.Equal(hint, AutomationProperties.GetHelpText(view.ApprovalOff));
                    Assert.Equal(hint, AutomationProperties.GetHelpText(view.ApprovalOn));
                    string immediate = language == AppLanguage.Chinese ? "切换后立即生效。" : "Changes immediately. ";
                    Assert.StartsWith(immediate, hint);
                    var groups = hint[immediate.Length..].Split('\n');
                    Assert.Equal(2, groups.Length);
                    List<string>[] expected = [[], []];
                    foreach (var operation in Enum.GetValues<WriteApprovalOperation>())
                    {
                        var (en, zh) = Strings.ApprovalOperationName(operation);
                        Assert.False(string.IsNullOrWhiteSpace(en) || string.IsNullOrWhiteSpace(zh));
                        Assert.NotEqual(en, zh);
                        string name = language == AppLanguage.Chinese ? zh : en;
                        int group = operation.AlwaysConfirmed() ? 1 : 0;
                        expected[group].Add(name);
                        Assert.Contains(name, groups[group]);
                        Assert.DoesNotContain(name, groups[1 - group]);
                    }
                    if (language == AppLanguage.Chinese)
                    {
                        Assert.Equal($"关闭后，{string.Join("、", expected[0])}无需人工确认。", groups[0]);
                        Assert.Equal($"{string.Join("、", expected[1])}仍须人工确认。", groups[1]);
                    }
                    else
                    {
                        static string EnglishList(List<string> names) => names.Count < 3 ? string.Join(" and ", names)
                            : string.Join(", ", names.SkipLast(1)) + ", and " + names[^1];
                        Assert.Equal($"Turning this off removes confirmation for {EnglishList(expected[0])}.", groups[0]);
                        Assert.Equal($"You still confirm {EnglishList(expected[1])}.", groups[1]);
                    }
                }
            }
            finally { view.Dispose(); }
        });
    }

    [Fact]
    public void Settings_has_no_native_cross_reference_toggle()
    {
        wpf.Run(() =>
        {
            var view = new SettingsView();
            Assert.NotNull(view.FindName("ControlEnabled"));
            Assert.Null(view.FindName("NativeCrossReferencesEnabled"));
            view.Dispose();
        });
    }

    [Fact]
    public void Safety_labels_and_lists_use_the_project_Primer_styles()
    {
        wpf.Run(() =>
        {
            var view = new SettingsView();
            foreach (string name in new[] { "PlanLifetimeLabel", "TargetAddressLabel" })
                Assert.Same(view.FindResource("Primer.FormLabel"), ((Label)view.FindName(name)).Style);
            Assert.Same(view.FindResource("Primer.List"), ((ListBox)view.FindName("OnlineTargets")).Style);
            view.Dispose();
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Problem_files_lock_the_editor_and_preserve_entries_until_replacement_is_confirmed(bool unreadable)
    {
        wpf.Run(() =>
        {
            using var fixture = new SafetySettingsServiceTests.Fixture();
            fixture.Service.SaveTargets(new[] { "192.0.2.10" });
            var view = new SettingsView();
            view.SetSafetyService(fixture.Service);
            WpfContext.Complete(view.LoadSafetyAsync());
            File.WriteAllText(fixture.TargetsPath, "invalid original entries");
            using var held = unreadable ? new FileStream(fixture.TargetsPath, FileMode.Open, FileAccess.Read, FileShare.None) : null;
            WpfContext.Complete(view.LoadSafetyAsync());
            Assert.False(((StackPanel)view.FindName("TargetEditor")).IsEnabled);
            Assert.True(((Button)view.FindName("ReloadSafety")).IsEnabled);
            Assert.Equal(Visibility.Visible, ((Button)view.FindName("ReplaceTargetFile")).Visibility);
            Assert.Contains(fixture.TargetsPath, ((TextBlock)view.FindName("SafetyStatus")).Text, StringComparison.Ordinal);
            var entries = (ListBox)view.FindName("OnlineTargets");
            Assert.Equal("192.0.2.10", Assert.Single(entries.Items));
            view.SafetyConfirmationForTests = key => { Assert.Equal("Settings.ConfirmReplaceTargets", key); return false; };
            Assert.False(view.ConfirmTargetFileReplacement());
            Assert.Single(entries.Items);
            view.SafetyConfirmationForTests = _ => true;
            Assert.True(view.ConfirmTargetFileReplacement());
            Assert.Empty(entries.Items);
            Assert.True(((StackPanel)view.FindName("TargetEditor")).IsEnabled);
            view.Dispose();
        });
    }

    [Fact]
    public void Concurrent_changes_prompt_reload_and_leave_the_file_untouched()
    {
        wpf.Run(() =>
        {
            using var fixture = new SafetySettingsServiceTests.Fixture();
            fixture.Service.SaveTargets(new[] { "192.0.2.10" });
            var view = new SettingsView();
            view.SetSafetyService(fixture.Service);
            WpfContext.Complete(view.LoadSafetyAsync());
            File.WriteAllText(fixture.TargetsPath, "target=198.51.100.10\n");
            string? error = null;
            view.SafetyErrorForTests = key => error = key;
            WpfContext.Complete(view.SaveTargetsAsync());
            Assert.Equal("Settings.TargetsChanged", error);
            Assert.Equal("target=198.51.100.10\n", File.ReadAllText(fixture.TargetsPath));
            Assert.False(((StackPanel)view.FindName("TargetEditor")).IsEnabled);
            Assert.True(((Button)view.FindName("ReloadSafety")).IsEnabled);
            WpfContext.Complete(view.LoadSafetyAsync());
            Assert.True(((StackPanel)view.FindName("TargetEditor")).IsEnabled);
            view.Dispose();
        });
    }

    [Theory]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Chinese)]
    public void Editors_load_targets_and_keep_accessible_names(AppLanguage language)
    {
        wpf.RunWithLanguage(language, () =>
        {
            using var fixture = new SafetySettingsServiceTests.Fixture();
            fixture.Service.SaveTargets(new[] { "192.0.2.10" });
            var view = new SettingsView();
            view.SetSafetyService(fixture.Service);
            WpfContext.Complete(view.LoadSafetyAsync());
            var targets = (ListBox)view.FindName("OnlineTargets");
            Assert.Equal("192.0.2.10", Assert.Single(targets.Items));
            foreach (string name in new[] { "OnlineTargets", "OnlineTargetInput", "OnlinePlanLifetime" })
            {
                var control = (Control)view.FindName(name);
                Assert.True(control.Focusable);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)), name);
            }
            view.Dispose();
        });
    }

    [Fact]
    public void Presets_and_custom_seconds_round_trip_through_the_approval_settings_file()
    {
        wpf.Run(() =>
        {
            using var fixture = new SafetySettingsServiceTests.Fixture();
            string path = Path.Combine(fixture.Root, "approval.settings");
            new ApprovalSettings(true, 75, 420).Save(path, fixture.Audit);
            using var approvals = new ApprovalService(path, "settings-" + Guid.NewGuid().ToString("N"), fixture.Audit);
            var view = new SettingsView();
            view.Initialize(approvals);
            try
            {
                Assert.True(view.TimeoutCustom.IsChecked);
                Assert.True(view.PlanCustom.IsChecked);
                Assert.Equal("75", view.CustomTimeout.Text);
                Assert.Equal("420", view.OnlinePlanLifetime.Text);
                void Change(System.Windows.Controls.Primitives.ButtonBase action, string property)
                {
                    var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void Saved(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                    { if (e.PropertyName == property) changed.TrySetResult(); }
                    approvals.PropertyChanged += Saved;
                    try
                    {
                        UnifiedDesktopTests.ClickControl(action);
                        WpfContext.Complete(changed.Task.WaitAsync(TestDeadlines.Hang));
                        WpfContext.Drain();
                    }
                    finally { approvals.PropertyChanged -= Saved; }
                }
                Change(view.Timeout60, nameof(approvals.TimeoutSeconds));
                Change(view.Plan30, nameof(approvals.OnlinePlanLifetimeSeconds));
                Assert.True(view.Timeout60.IsChecked); Assert.True(view.Plan30.IsChecked);
                Assert.False(view.TimeoutCustom.IsChecked); Assert.False(view.PlanCustom.IsChecked);
                var presets = ApprovalSettings.Load(path);
                Assert.Equal(60, presets.TimeoutSeconds); Assert.Equal(1800, presets.OnlinePlanLifetimeSeconds);
                UnifiedDesktopTests.ClickControl(view.TimeoutCustom);
                UnifiedDesktopTests.ClickControl(view.PlanCustom);
                string? error = null;
                view.SafetyErrorForTests = key => error = key;
                view.CustomTimeout.Text = "3601";
                UnifiedDesktopTests.ClickControl(view.ApplyTimeout);
                Assert.Equal("Settings.TimeoutInvalid", error);
                Assert.Equal(60, ApprovalSettings.Load(path).TimeoutSeconds);
                view.CustomTimeout.Text = "3599";
                Change(view.ApplyTimeout, nameof(approvals.TimeoutSeconds));
                UnifiedDesktopTests.ClickControl(view.PlanCustom);
                view.OnlinePlanLifetime.Text = "86400";
                Change(view.ApplyPlanLifetime, nameof(approvals.OnlinePlanLifetimeSeconds));
                var saved = ApprovalSettings.Load(path);
                Assert.Equal(3599, saved.TimeoutSeconds); Assert.Equal(86400, saved.OnlinePlanLifetimeSeconds);
                Assert.True(saved.Enabled); Assert.True(fixture.Audit.Verify().Passed);
            }
            finally { view.Dispose(); }
        });
    }

    [Fact]
    public void The_write_approval_switch_uses_its_whole_track_and_Space_changes_the_setting_once()
    {
        wpf.Run(() =>
        {
            using var fixture = new SafetySettingsServiceTests.Fixture();
            var approvals = FeaturePageFixtures.Approvals(false);
            var view = new SettingsView();
            view.SetSafetyService(fixture.Service);
            view.Initialize(approvals);
            var window = new Window { Content = view, Width = 1024, Height = 640 };
            try
            {
                WpfContext.ShowWindow(window);
                var track = (Grid)view.ApprovalOn.Parent;
                void CheckHitTargets(RadioButton expected)
                {
                    foreach (double x in new[] { 4d, track.ActualWidth - 4 })
                    {
                        var hit = Assert.IsAssignableFrom<DependencyObject>(track.InputHitTest(new Point(x, track.ActualHeight / 2)));
                        while (hit is not RadioButton) hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
                        Assert.Same(expected, hit);
                    }
                }
                Assert.True(approvals.Enabled);
                CheckHitTargets(view.ApprovalOff);
                var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                approvals.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(approvals.Enabled)) changed.TrySetResult(); };
                view.ApprovalOn.Focus();
                var key = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(view),
                    0, System.Windows.Input.Key.Space) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                view.ApprovalOn.RaiseEvent(key);
                Assert.True(key.Handled);
                WpfContext.Complete(changed.Task.WaitAsync(TestDeadlines.Hang));
                WpfContext.Drain(); view.UpdateLayout();
                Assert.False(approvals.Enabled);
                CheckHitTargets(view.ApprovalOn);
            }
            finally { window.Close(); view.Dispose(); }
        });
    }

    [Theory]
    [InlineData("configured", "deny")]
    [InlineData("configured", "remove")]
    [InlineData("configured", "cancel")]
    [InlineData("unconfigured", "deny")]
    [InlineData("empty", "remove")]
    public void Empty_target_confirmation_persists_the_selected_policy_after_reopening(string initial, string choice)
    {
        wpf.Run(() =>
        {
            using var fixture = new SafetySettingsServiceTests.Fixture();
            if (initial == "configured") fixture.Service.SaveTargets(["192.0.2.5"]);
            if (initial == "empty") fixture.Service.SaveTargets([]);
            var view = new SettingsView();
            view.SetSafetyService(fixture.Service);
            var window = new Window { Content = view };
            try
            {
                WpfContext.ShowWindow(window);
                WpfContext.Complete(view.LoadSafetyAsync());
                view.UpdateLayout();
                if (initial == "configured")
                {
                    var remove = Assert.Single(WorkbenchRenderFeaturePagesTests.Descendants<Button>(view.OnlineTargets));
                    UnifiedDesktopTests.ClickControl(remove);
                }
                Assert.Equal(Visibility.Visible, view.SaveTargetChanges.Visibility);
                string? body = null;
                bool offered = false;
                window.Dispatcher.InvokeAsync(() =>
                {
                    var dialog = window.OwnedWindows.OfType<FeatureConfirmation>().Single();
                    body = dialog.Body.Text;
                    offered = dialog.AlternativeAction.IsVisible && dialog.CancelAction.IsDefault
                        && Equals(dialog.AlternativeAction.Content, Loc.Current["Settings.UnconfigureTargets"])
                        && Equals(dialog.ApplyAction.Content, Loc.Current["Settings.DenyTargets"]);
                    UnifiedDesktopTests.ClickControl(choice switch
                    {
                        "deny" => dialog.ApplyAction, "remove" => dialog.AlternativeAction, _ => dialog.CancelAction,
                    });
                }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                view.SafetyEditors.IsEnabledChanged += (_, _) => { if (view.SafetyEditors.IsEnabled) saved.TrySetResult(); };
                UnifiedDesktopTests.ClickControl(view.SaveTargetChanges);
                Assert.True(offered);
                Assert.Equal(Loc.Current["Settings.EmptyTargetsChoice"], body);
                if (choice != "cancel") WpfContext.Complete(saved.Task.WaitAsync(TestDeadlines.Hang));
                Assert.Equal(choice != "remove", File.Exists(fixture.TargetsPath));
                if (choice == "deny") Assert.Empty(File.ReadAllBytes(fixture.TargetsPath));
                var reopened = new SettingsView();
                try
                {
                    reopened.SetSafetyService(new SafetySettingsService(fixture.TargetsPath, fixture.Audit));
                    WpfContext.Complete(reopened.LoadSafetyAsync());
                    Assert.Equal(Loc.Current[choice switch
                    {
                        "deny" => "Settings.TargetsEmpty", "remove" => "Settings.TargetsUnconfigured", _ => "Settings.TargetsConfigured",
                    }], reopened.SafetyStatus.Text);
                    Assert.Equal(choice == "cancel" ? 1 : 0, reopened.OnlineTargets.Items.Count);
                    Assert.Equal(initial == "unconfigured" || choice == "cancel" ? 1 : 2, fixture.Audit.Read().Count);
                }
                finally { reopened.Dispose(); }
            }
            finally { window.Close(); view.Dispose(); }
        });
    }

    [Fact]
    public void A_saved_empty_list_displays_the_deny_all_policy_on_open()
    {
        wpf.Run(() =>
        {
            using var fixture = new SafetySettingsServiceTests.Fixture();
            fixture.Service.SaveTargets([]);
            var view = new SettingsView();
            try
            {
                view.SetSafetyService(fixture.Service);
                WpfContext.Complete(view.LoadSafetyAsync());
                Assert.Empty(view.OnlineTargets.Items);
                Assert.Equal(Loc.Current["Settings.TargetsEmpty"], view.SafetyStatus.Text);
                Assert.True(view.TargetEditor.IsEnabled);
            }
            finally { view.Dispose(); }
        });
    }

    [Fact]
    public void Cancelling_a_new_target_keeps_the_editor_and_disk_unchanged()
    {
        wpf.Run(() =>
        {
            using var fixture = new SafetySettingsServiceTests.Fixture();
            var view = new SettingsView();
            view.SetSafetyService(fixture.Service);
            int confirmations = 0;
            view.SafetyConfirmationForTests = key => { Assert.Equal("Settings.ApplySafety", key); confirmations++; return false; };
            var window = new Window { Content = view, Width = 1024, Height = 640 };
            try
            {
                WpfContext.ShowWindow(window);
                WpfContext.Complete(view.LoadSafetyAsync());
                view.OnlineTargetInput.Text = "192.0.2.10";
                UnifiedDesktopTests.ClickControl(view.AddTarget);
                Assert.Equal(1, confirmations);
                Assert.Empty(view.OnlineTargets.Items);
                Assert.Equal("192.0.2.10", view.OnlineTargetInput.Text);
                Assert.Equal(Loc.Current["Settings.TargetsUnconfigured"], view.SafetyStatus.Text);
                Assert.False(File.Exists(fixture.TargetsPath));
                Assert.Empty(fixture.Audit.Read());
            }
            finally { window.Close(); view.Dispose(); }
        });
    }

    [Fact]
    public void Adding_and_removing_the_last_chip_distinguishes_unconfigured_from_saved_empty()
    {
        wpf.Run(() =>
        {
            using var fixture = new SafetySettingsServiceTests.Fixture();
            var view = new SettingsView();
            view.SetSafetyService(fixture.Service);
            view.SafetyConfirmationForTests = key => { Assert.Equal("Settings.ApplySafety", key); return true; };
            string? saveError = null;
            view.SafetyErrorForTests = key => saveError = key;
            var window = new Window { Content = view, Width = 1024, Height = 640 };
            try
            {
                WpfContext.ShowWindow(window);
                WpfContext.Complete(view.LoadSafetyAsync());
                Assert.Equal(Loc.Current["Settings.TargetsUnconfigured"], view.SafetyStatus.Text);
                Assert.False(File.Exists(fixture.TargetsPath));
                void Save(Button action)
                {
                    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void BusyChanged(object sender, DependencyPropertyChangedEventArgs e)
                    { if (view.SafetyEditors.IsEnabled) done.TrySetResult(); }
                    view.SafetyEditors.IsEnabledChanged += BusyChanged;
                    try
                    {
                        UnifiedDesktopTests.ClickControl(action);
                        WpfContext.Complete(done.Task.WaitAsync(TestDeadlines.Hang));
                        Assert.Null(saveError);
                        WpfContext.Drain(); view.UpdateLayout();
                    }
                    finally { view.SafetyEditors.IsEnabledChanged -= BusyChanged; }
                }
                view.OnlineTargetInput.Text = "192.0.2.10";
                Save(view.AddTarget);
                Assert.Equal("192.0.2.10", Assert.Single(fixture.Service.LoadTargets()!.Entries));
                Assert.Equal("", view.OnlineTargetInput.Text);
                var remove = WorkbenchRenderFeaturePagesTests.Descendants<Button>(view.OnlineTargets)
                    .Single(button => Equals(button.DataContext, "192.0.2.10"));
                UnifiedDesktopTests.ClickControl(remove);
                Assert.Empty(view.OnlineTargets.Items);
                Assert.Equal(Loc.Current["Settings.TargetsUnsaved"], view.SafetyStatus.Text);
                Assert.Single(fixture.Service.LoadTargets()!.Entries);
                Save(view.SaveTargetChanges);
                WpfContext.Complete(view.LoadSafetyAsync());
                Assert.Empty(fixture.Service.LoadTargets()!.Entries);
                Assert.Equal(Loc.Current["Settings.TargetsEmpty"], view.SafetyStatus.Text);
                Assert.True(fixture.Audit.Verify().Passed);
            }
            finally { window.Close(); view.Dispose(); }
        });
    }
}
