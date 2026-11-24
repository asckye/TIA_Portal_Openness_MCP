using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

// The checked state drives inspector visibility. Assistive technology invokes the single visible action.
public sealed class AuditDetailsCloseButton : ToggleButton
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ClosePeer(this);

    private sealed class ClosePeer(AuditDetailsCloseButton button) : ButtonBaseAutomationPeer(button), IInvokeProvider
    {
        protected override string GetClassNameCore() => "Button";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.Invoke ? this : base.GetPattern(pattern);

        public void Invoke()
        {
            if (!IsEnabled()) throw new ElementNotEnabledException();
            button.Dispatcher.InvokeAsync(() => { if (button.IsEnabled && button.IsChecked == true) button.OnClick(); }, DispatcherPriority.Input);
        }
    }
}

public partial class AuditLogView : UserControl
{
    public AuditLogView() { InitializeComponent(); DataContext = null; }
    private FeaturePagesViewModel Model => (FeaturePagesViewModel)DataContext;
    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The shell's content starts after the 60 epx activity rail, before the page margins.
        var windowWidth = ResponsiveLayout.GetWindowWidth(this);
        AuditInspector.Width = (windowWidth > 0 ? windowWidth - 60 : ActualWidth) >= 1200 ? 320 : 280;
    }
    private void OnRowPressed(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject target && ItemsControl.ContainerFromElement(AuditList, target) is ListBoxItem)
            DetailsToggle.IsChecked = true;
    }
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DetailsToggle != null && e.AddedItems.Count > 0) DetailsToggle.IsChecked = true;
    }
    private void OnVerify(object sender, RoutedEventArgs e) => Model.Verify();
    private void OnJump(object sender, RoutedEventArgs e) => ShowBreak();
    internal void ShowBreak()
    {
        var row = Model.JumpToBreak();
        if (row == null) return;
        DetailsToggle.IsChecked = true;
        AuditList.ScrollIntoView(row);
    }
    private async void OnOlder(object sender, RoutedEventArgs e) { if (Model.Audit is Services.AuditLogService audit) await audit.OlderAsync(); }
    private async void OnLatest(object sender, RoutedEventArgs e) { if (Model.Audit is Services.AuditLogService audit) await audit.LatestAsync(); }
}
