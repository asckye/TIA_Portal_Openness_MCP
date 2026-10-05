using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class CallDetailDrawer : UserControl
{
    public CallDetailDrawer() { InitializeComponent(); DataContext = null; }
    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var model = (FeaturePagesViewModel)DataContext;
        if (model.SelectedCall is { } call) model.Copy(call.Parameters);
    }
}
