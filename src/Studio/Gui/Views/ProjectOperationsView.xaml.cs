using System;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class ProjectOperationsView : UserControl
{
    public ProjectOperationsView()
    {
        InitializeComponent();
        Options.ClearPrefillRequested += (_, form) => ClearPrefillRequested?.Invoke(this, form);
    }
    internal bool OptionsOpen => Options.IsOpen;
    internal event EventHandler<string>? ClearPrefillRequested;
    private MainViewModel Model => (MainViewModel)DataContext;
    internal void ShowProjectOptions() => Options.Show("Project");
    internal void ShowTransferOptions() => Options.Show("Export");
    internal void SetMcpPrefillMarkers(bool project, bool export, bool import)
    {
        Options.SetMcpPrefillMarkers(project, export, import);
        ProjectPathMarker.SetPending(project);
    }
    private void OnClearProjectPrefill(object? sender, EventArgs e) => ClearPrefillRequested?.Invoke(this, "ConnectProject");
    internal void ShowControlInspectionRules() => Options.Show("Inspect");
    internal void SetControlPrefillMarker(bool pending) => Options.SetControlPrefillMarker(pending);
    internal void HideOptions() => Options.Hide();
    private void OnProjectOptions(object sender, RoutedEventArgs e) => ShowProjectOptions();
    private void OnBrowseProject(object sender, RoutedEventArgs e) => Model.Session.BrowseProject();
    private void OnConnect(object sender, RoutedEventArgs e)
    {
        var command = string.IsNullOrWhiteSpace(Model.Session.ProjectPath) ? Model.Session.Connect : Model.Session.OpenProject;
        if (command.CanExecute(null)) command.Execute(null);
    }
}
