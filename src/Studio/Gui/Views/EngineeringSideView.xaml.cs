using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class EngineeringSideView : UserControl
{
    public EngineeringSideView() => InitializeComponent();
    private MainViewModel Model => (MainViewModel)DataContext;
    private void OnClearLog(object sender, RoutedEventArgs e) => Model.Activity.ClearLog();
}
