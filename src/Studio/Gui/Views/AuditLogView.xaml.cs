using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

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
    private void OnCloseInspector(object sender, RoutedEventArgs e) => DetailsToggle.IsChecked = false;
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
