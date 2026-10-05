using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class ApprovalsDrawer : UserControl
{
    public ApprovalsDrawer() { InitializeComponent(); DataContext = null; }
    private FeaturePagesViewModel Model => (FeaturePagesViewModel)DataContext;
    private void OnApprove(object sender, RoutedEventArgs e) => Model.Decide((ApprovalRow)((Button)sender).DataContext, true);
    private void OnDeny(object sender, RoutedEventArgs e) => Model.Decide((ApprovalRow)((Button)sender).DataContext, false);
    private void OnParameters(object sender, RoutedEventArgs e)
    {
        var row = (ApprovalRow)((Button)sender).DataContext;
        row.JsonOpen = !row.JsonOpen;
    }
}
