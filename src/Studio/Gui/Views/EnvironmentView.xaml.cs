using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class EnvironmentView : UserControl
{
    private bool _checked;
    public EnvironmentView()
    {
        InitializeComponent(); DataContext = null;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible || _checked || DataContext is not FeaturePagesViewModel) return;
            _checked = true; Model.Run(Model.Environment.Recheck);
        };
    }
    private FeaturePagesViewModel Model => (FeaturePagesViewModel)DataContext;
    private void OnRecheck(object sender, RoutedEventArgs e) => Model.Run(Model.Environment.Recheck);
    private void OnGenerate(object sender, RoutedEventArgs e) => Model.Export();
    private void OnFix(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var button = (Button)sender;
        var row = (EnvironmentRow)button.DataContext;
        if (row.Check.Id == "proxy-remote" && Window.GetWindow(this) is MainWindow window)
        {
            WorkbenchCommands.Navigate.Execute(button.CommandParameter, button);
            if (window.CurrentPage == "Mcp") window.Configuration?.RepairRemoteClients();
            return;
        }
        Model.Run(() => Model.Environment.Fix(row.Check.Id), kind: ToastKind.Info);
    }
}
