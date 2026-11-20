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
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("ApprovalOffPill")).Visibility);
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
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("ApprovalOffPill")).Visibility);
                Assert.Equal(Loc.Current["Shell.ApprovalOnStatus"], ((TextBlock)window.FindName("ApprovalStatusText")).Text);
            }
            finally { window.Close(); }
        });
    }

    private static MainWindow CreateWindow() => new(new MainViewModel(new FakeProjectClient(), new FakeDialogService()),
        false, approvals: new ApprovalServiceStub());
}
