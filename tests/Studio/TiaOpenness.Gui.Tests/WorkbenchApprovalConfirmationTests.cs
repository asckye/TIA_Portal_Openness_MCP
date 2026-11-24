using TiaMcp.Engine.Tests.Shared;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.ViewModels;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class WorkbenchApprovalConfirmationTests(WpfContext wpf)
{
    [Fact]
    public void Occupied_channel_has_read_only_controls_and_status_chip()
    {
        wpf.Run(() =>
        {
            string name = "Approval.Gui.Tests." + System.Guid.NewGuid().ToString("N");
            string settings = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name + ".settings");
            using var owner = new Services.ApprovalService(settings, name);
            using var other = new Services.ApprovalService(settings, name);
            var window = new MainWindow(new MainViewModel(new FakeProjectClient(), new FakeDialogService()), false, approvals: other);
            try
            {
                window.Navigate("Approvals");
                Assert.True(owner.ChannelAvailable);
                Assert.False(other.ChannelAvailable);
                Assert.False(window.ApprovalStatusButton.IsEnabled);
                Assert.Equal(Visibility.Collapsed, window.ApprovalsContent.DecisionArea.Visibility);
                Assert.False(window.SettingsContent.AiPanel.IsEnabled);
                Assert.Equal(Loc.Current["Shell.ApprovalUnavailable"], window.ApprovalStatusText.Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Status_chip_allows_local_click_and_space_and_refuses_the_control_guard()
    {
        wpf.Run(() =>
        {
            var now = WorkbenchControlTests.TestNow;
            var guard = new ControlChannel.ControlInputGuard(() => now, () => false);
            var (window, _, _) = WorkbenchControlTests.Window(input: guard);
            try
            {
                var chip = window.ApprovalStatusButton;
                Assert.Equal(TiaOpenness.Shared.WorkbenchControlStatus.Done, window.ApplyControl(WorkbenchControlTests.Request()).Status);
                Assert.True(guard.ShouldBlock(chip));
                UnifiedDesktopTests.ClickControl(chip);
                Assert.True(window.Approvals.Enabled);
                now += System.TimeSpan.FromSeconds(1);
                void Toggle(System.Action press, bool manual, string tone)
                {
                    var saved = new System.Threading.Tasks.TaskCompletionSource();
                    void OnEnabled(object sender, DependencyPropertyChangedEventArgs e)
                    {
                        if (chip.IsEnabled && window.Approvals.Enabled == manual) saved.TrySetResult();
                    }
                    chip.IsEnabledChanged += OnEnabled;
                    try { press(); WpfContext.Complete(saved.Task.WaitAsync(TestDeadlines.Hang)); }
                    finally { chip.IsEnabledChanged -= OnEnabled; }
                    Assert.Equal(manual, window.Approvals.Enabled);
                    Assert.Same(window.FindResource(tone), window.ToastDot.Fill);
                    Assert.Equal("Log", window.CurrentPage);
                }
                Toggle(() => UnifiedDesktopTests.ClickControl(chip), false, "Primer.warn");
                chip.Focus();
                Toggle(() => WpfContext.PressSpace(chip), true, "Primer.ok");
                Assert.Same(chip, System.Windows.Input.Keyboard.FocusedElement);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Chinese)]
    public void Write_approval_toggles_directly_and_updates_status_indicators(AppLanguage language)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var window = CreateWindow();
            try
            {
                window.OpenSettings();
                var settings = (Views.SettingsView)window.FindName("SettingsContent");
                settings.ShowSection("AI");
                Assert.True(window.Approvals.Enabled);

                var disabled = new System.Threading.Tasks.TaskCompletionSource();
                window.Approvals.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(IApprovalService.Enabled) && !window.Approvals.Enabled) disabled.TrySetResult();
                };
                UnifiedDesktopTests.ClickControl((RadioButton)settings.FindName("ApprovalOff"));
                WpfContext.Complete(disabled.Task.WaitAsync(TestDeadlines.Hang));
                WpfContext.Drain();
                Assert.False(window.Approvals.Enabled);
                Assert.Null(window.FindName("ApprovalOffPill"));
                Assert.Equal(Loc.Current["Shell.ApprovalOffStatus"], ((TextBlock)window.FindName("ApprovalStatusText")).Text);

                var enabled = new System.Threading.Tasks.TaskCompletionSource();
                window.Approvals.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(IApprovalService.Enabled) && window.Approvals.Enabled) enabled.TrySetResult();
                };
                UnifiedDesktopTests.ClickControl((RadioButton)settings.FindName("ApprovalOn"));
                WpfContext.Complete(enabled.Task.WaitAsync(TestDeadlines.Hang));
                WpfContext.Drain();
                Assert.True(window.Approvals.Enabled);
                Assert.Equal(Loc.Current["Shell.ApprovalOnStatus"], window.ApprovalStatusText.Text);
                Assert.Equal(Loc.Current["Shell.ApprovalOnStatus"], ((TextBlock)window.FindName("ApprovalStatusText")).Text);
            }
            finally { window.Close(); }
        });
    }

    private static MainWindow CreateWindow() => new(new MainViewModel(new FakeProjectClient(), new FakeDialogService()),
        false, approvals: new ApprovalServiceStub());
}
