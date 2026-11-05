using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.ViewModels;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class WorkbenchApprovalConfirmationTests(WpfContext wpf)
{
    [Theory]
    [InlineData(AppLanguage.English, "Turn off approval")]
    [InlineData(AppLanguage.Chinese, "关闭审批")]
    public void Only_the_complete_localized_word_enables_and_submits(AppLanguage language, string word)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var window = CreateWindow();
            try
            {
                Open(window);
                var submit = (Button)window.FindName("ConfirmApprovalOff");
                Assert.False(submit.IsEnabled);
                var input = Assert.IsType<TextBox>(window.FindName("ApprovalOffConfirmation"));
                var label = Assert.IsType<TextBlock>(window.FindName("ApprovalOffConfirmationLabel"));
                Assert.Contains(word, label.Text);
                Assert.Equal(label.Text, AutomationProperties.GetName(input));
                Assert.Same(Application.Current.FindResource("Primer.Input"), input.Style);
                foreach (string invalid in new[] { "", "wrong", word[..^1], word + "!", word.Replace(" ", "  ", StringComparison.Ordinal) + "x" })
                {
                    input.Text = invalid;
                    Assert.False(submit.IsEnabled);
                    UnifiedDesktopTests.ClickControl(submit);
                    Assert.True(window.Approvals.Enabled);
                    Assert.Equal(Visibility.Visible, ((Grid)window.FindName("ConfirmOverlay")).Visibility);
                }
                input.Text = "  " + word.ToUpperInvariant() + "  ";
                Assert.True(submit.IsEnabled);
                var saved = new System.Threading.Tasks.TaskCompletionSource();
                window.Approvals.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(IApprovalService.Enabled)) saved.TrySetResult();
                };
                UnifiedDesktopTests.ClickControl(submit);
                WpfContext.Complete(saved.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.False(window.Approvals.Enabled);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(AppLanguage.English, "Turn off approval")]
    [InlineData(AppLanguage.Chinese, "关闭审批")]
    public void Enter_is_inert_and_cancel_or_escape_preserves_approval_and_reopening_clears_input(AppLanguage language, string word)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var window = CreateWindow();
            try
            {
                window.Show(); Open(window);
                var cancel = (Button)window.FindName("CancelApprovalOff");
                var submit = (Button)window.FindName("ConfirmApprovalOff");
                var input = Assert.IsType<TextBox>(window.FindName("ApprovalOffConfirmation"));
                Assert.Same(cancel, Keyboard.FocusedElement);
                Assert.True(cancel.IsDefault); Assert.True(cancel.IsCancel); Assert.False(submit.IsDefault);
                foreach (string text in new[] { "", "wrong", word })
                {
                    input.Text = text; input.Focus();
                    var enter = PressKey(input, Key.Enter);
                    Assert.True(enter.Handled);
                    Assert.True(window.Approvals.Enabled);
                    Assert.Equal(Visibility.Visible, ((Grid)window.FindName("ConfirmOverlay")).Visibility);
                }
                UnifiedDesktopTests.ClickControl(cancel);
                Assert.True(window.Approvals.Enabled);
                Assert.Equal(Visibility.Collapsed, ((Grid)window.FindName("ConfirmOverlay")).Visibility);
                Open(window);
                Assert.Equal("", input.Text); Assert.False(submit.IsEnabled);
                input.Text = word;
                Assert.True(PressKey(input, Key.Escape).Handled);
                Assert.True(window.Approvals.Enabled);
                Assert.Equal(Visibility.Collapsed, ((Grid)window.FindName("ConfirmOverlay")).Visibility);
                Open(window);
                Assert.Equal("", input.Text); Assert.False(submit.IsEnabled);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Turning_approval_on_does_not_require_confirmation()
    {
        wpf.Run(() =>
        {
            var window = CreateWindow();
            try
            {
                window.Approvals.Enabled = false;
                var settings = (Views.SettingsView)window.FindName("SettingsContent");
                var saved = new System.Threading.Tasks.TaskCompletionSource();
                window.Approvals.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(IApprovalService.Enabled)) saved.TrySetResult();
                };
                UnifiedDesktopTests.ClickControl((RadioButton)settings.FindName("ApprovalOn"));
                WpfContext.Complete(saved.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.True(window.Approvals.Enabled);
                Assert.Equal(Visibility.Collapsed, ((Grid)window.FindName("ConfirmOverlay")).Visibility);
            }
            finally { window.Close(); }
        });
    }

    private static MainWindow CreateWindow() => new(new MainViewModel(new FakeProjectClient(), new FakeDialogService()),
        false, approvals: new ApprovalServiceStub());

    private static void Open(MainWindow window)
    {
        window.OpenSettings();
        var settings = (Views.SettingsView)window.FindName("SettingsContent");
        settings.ShowSection("AI");
        UnifiedDesktopTests.ClickControl((RadioButton)settings.FindName("ApprovalOff"));
        WpfContext.Drain();
    }

    private static KeyEventArgs PressKey(UIElement element, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element), 0, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        element.RaiseEvent(args); WpfContext.Drain();
        return args;
    }
}
