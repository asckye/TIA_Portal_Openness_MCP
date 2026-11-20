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
    internal RadioButton TagsTab { get; set; } = null!;
    internal bool OptionsOpen => Options.IsOpen;
    private Action? _environmentRequested;
    internal void SetFeatures(FeaturePagesViewModel? features, Action? environmentRequested)
    {
        EnvironmentBanner.DataContext = features;
        _environmentRequested = environmentRequested;
    }
    private void OnEnvironment(object sender, RoutedEventArgs e) => _environmentRequested?.Invoke();
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
    internal event EventHandler? OpenTransferRequested;
    private void OnTransfer(object sender, RoutedEventArgs e) => OpenTransferRequested?.Invoke(this, EventArgs.Empty);
    private void OnExportMenu(object sender, RoutedEventArgs e)
    {
        var menu = ExportMenuButton.ContextMenu;
        menu.PlacementTarget = ExportMenuButton;
        menu.DataContext = Model;
        SessionItem.Visibility = Model.HasMcpPanel ? Visibility.Visible : Visibility.Collapsed;
        menu.IsOpen = true;
    }
    private void OnSessions(object sender, RoutedEventArgs e)
    {
        Sessions.Visibility = Visibility.Visible;
        Sessions.IsExpanded = true;
    }
    private void OnInspect(object sender, RoutedEventArgs e) => ShowControlInspectionRules();
    private async void OnRender(object sender, RoutedEventArgs e)
    {
        Model.Engineering.Document = ProjectDocument.Render;
        await Model.Engineering.ReadFocusedBlockAsync();
    }
    private void OnInterface(object sender, RoutedEventArgs e) => Model.Engineering.Document = ProjectDocument.Interface;
    private void OnCompileResults(object sender, RoutedEventArgs e) => Model.Engineering.Document = ProjectDocument.Compile;
    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        var engineering = Model.Engineering;
        if (engineering.Document == ProjectDocument.Tags) await engineering.ReadTagsAsync();
        else if (engineering.Refresh.CanExecute(null)) engineering.Refresh.Execute(null);
    }
    private void OnBrowseProject(object sender, RoutedEventArgs e) => Model.Session.BrowseProject();
    private void OnConnect(object sender, RoutedEventArgs e)
    {
        var command = string.IsNullOrWhiteSpace(Model.Session.ProjectPath) ? Model.Session.Connect : Model.Session.OpenProject;
        if (command.CanExecute(null)) command.Execute(null);
    }
}
