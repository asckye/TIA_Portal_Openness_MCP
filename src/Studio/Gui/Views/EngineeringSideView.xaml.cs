using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class EngineeringSideView : UserControl
{
    public EngineeringSideView() => InitializeComponent();
    private MainViewModel _model => (MainViewModel)DataContext;
    private void OnClearLog(object sender, RoutedEventArgs e) => _model.Activity.ClearLog();
    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange > 0) ((ScrollViewer)sender).ScrollToEnd();
    }

}
