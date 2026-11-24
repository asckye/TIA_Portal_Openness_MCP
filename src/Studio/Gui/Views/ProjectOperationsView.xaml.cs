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
        ProjectToolbar.SizeChanged += (_, e) => ProjectToolbar.Tag = e.NewSize.Width < 850 ? "Compact" : null;
        RegisterName("SessionPicker", Sessions.SessionPicker);
        Options.LocalScrim.Source = ProjectBackdrop;
    }
    internal Controls.ProjectSessionDetails Sessions { get; } = new();
    internal RadioButton TagsTab { get; set; } = null!;
    internal bool OptionsOpen => Options.IsOpen;
    private MainViewModel Model => (MainViewModel)DataContext;
    internal void ShowProjectOptions() => Options.Show("Project");
    internal Func<string>? PendingTransfer { get; set; }
    internal void ShowTransferOptions() => Options.Show(PendingTransfer?.Invoke() ?? "Export");
    internal void SetMcpPrefillMarkers(bool project, bool export, bool import)
    {
        Options.SetMcpPrefillMarkers(project, export, import);
        ProjectPathMarker.SetPending(project);
    }
    internal void ShowControlInspectionRules() => Options.Show("Inspect");
    internal void SetControlPrefillMarker(bool pending) => Options.SetControlPrefillMarker(pending);
    internal void HideOptions() => Options.Hide();
    private void OnProjectOptions(object sender, RoutedEventArgs e) => ShowProjectOptions();
    internal event EventHandler? OpenTransferRequested;
    private void OnTransfer(object sender, RoutedEventArgs e)
    {
        OpenTransferRequested?.Invoke(this, EventArgs.Empty);
        Options.Show("Export");
    }
    internal void ShowDownloadOptions() => Options.Show("Download");
    private void OnImportDrawer(object sender, RoutedEventArgs e) => Options.Show("Import");
    private void OnDownload(object sender, RoutedEventArgs e) => ShowDownloadOptions();
    private async void OnExportXml(object sender, RoutedEventArgs e) => await Model.Engineering.OpenXmlAsync(true);
    private async void OnOpenXml(object sender, RoutedEventArgs e) => await Model.Engineering.OpenXmlAsync();
    private async void OnScan(object sender, RoutedEventArgs e) => await Model.Session.RefreshProcessesAsync();
    private async void OnPick(object sender, RoutedEventArgs e) => await Model.Session.ChooseProcessAsync(true);
    private async void OnReconnect(object sender, RoutedEventArgs e) => await Model.Session.ChooseProcessAsync(attach: true, showEmpty: true);
    private async void OnAttach(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ProjectProcessRow row) await Model.Session.SelectProcessAsync(row.ProcessId, true);
    }
    private void OnExportMenu(object sender, RoutedEventArgs e)
    {
        var menu = ExportMenuButton.ContextMenu;
        menu.PlacementTarget = ExportMenuButton;
        menu.DataContext = Model;
        menu.IsOpen = true;
    }
    private void OnOpenRules(object sender, RoutedEventArgs e) => ShowControlInspectionRules();
    private async void OnRender(object sender, RoutedEventArgs e)
    {
        Model.Engineering.Document = ProjectDocument.Render;
        await Model.Engineering.ReadFocusedBlockAsync();
    }
    private async void OnInterface(object sender, RoutedEventArgs e)
    {
        Model.Engineering.Document = ProjectDocument.Interface;
        await Model.Engineering.ReadInterfaceAsync();
    }
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
