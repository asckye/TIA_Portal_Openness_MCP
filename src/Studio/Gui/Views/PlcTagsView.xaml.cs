using System.Windows.Controls;
using System.Windows;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class PlcTagsView : UserControl
{
    public PlcTagsView() => InitializeComponent();
    private async void OnRead(object sender, RoutedEventArgs e) => await ((MainViewModel)DataContext).Engineering.ReadTagsAsync();
}
