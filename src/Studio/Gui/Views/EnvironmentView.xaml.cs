using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public sealed class DecorativeGlyph : TextBlock
{
    protected override System.Windows.Automation.Peers.AutomationPeer? OnCreateAutomationPeer() => null;
}

public partial class EnvironmentView : UserControl
{
    private bool _checked;
    public EnvironmentView()
    {
        InitializeComponent(); DataContext = null;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible || _checked || DataContext is not FeaturePagesViewModel) return;
            Recheck();
        };
    }
    private FeaturePagesViewModel Model => (FeaturePagesViewModel)DataContext;
    internal void Recheck()
    {
        _checked = true;
        Model.Run(Model.Environment.Recheck);
    }
    private void OnRecheck(object sender, RoutedEventArgs e) => Recheck();
    private async void OnFix(object sender, RoutedEventArgs e)
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
        if (row.Check.Issue is { Endpoint: not null } issue && Window.GetWindow(this) is MainWindow shell)
        {
            await shell.ReviewEnvironmentIssue(issue);
            return;
        }
        Model.Run(() => Model.Environment.Fix(row.Check.Id), kind: ToastKind.Info);
    }
}
