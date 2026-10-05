using System;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class ProjectOperationsView : UserControl
{
    public ProjectOperationsView() => InitializeComponent();
    internal event EventHandler? EnvironmentRequested;
    private void OnEnvironment(object sender, RoutedEventArgs e) => EnvironmentRequested?.Invoke(this, EventArgs.Empty);
    private MainViewModel Model => (MainViewModel)DataContext;
    internal void ShowProjectOptions() => Options.Show("Project");
    internal void HideOptions() => Options.Hide();
    private void OnProjectOptions(object sender, RoutedEventArgs e) => ShowProjectOptions();
    private void OnExportOptions(object sender, RoutedEventArgs e) => Options.Show("Export");
    private void OnInspectOptions(object sender, RoutedEventArgs e) => Options.Show("Inspect");
    private void OnBrowseProject(object sender, RoutedEventArgs e) => Model.Session.BrowseProject();
    private void OnConnect(object sender, RoutedEventArgs e)
    {
        var command = string.IsNullOrWhiteSpace(Model.Session.ProjectPath) ? Model.Session.Connect : Model.Session.OpenProject;
        if (command.CanExecute(null)) command.Execute(null);
    }
    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (Model.Engineering.OutputDirectory.Length == 0) Options.Show("Export");
        else if (Model.Engineering.Export.CanExecute(null)) Model.Engineering.Export.Execute(null);
    }
}
