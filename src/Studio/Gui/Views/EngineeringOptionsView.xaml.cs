using System;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class EngineeringOptionsView : UserControl
{
    public EngineeringOptionsView() => InitializeComponent();
    private Controls.ModalPane? _focus;
    internal bool IsOpen => OptionsOverlay.Visibility == Visibility.Visible;
    internal event EventHandler<string>? ClearPrefillRequested;
    private MainViewModel Model => (MainViewModel)DataContext;
    internal void Show(string section)
    {
        SelectSection(section);
        if (IsOpen) return;
        OptionsOverlay.Visibility = Visibility.Visible;
        _focus = new Controls.ModalPane(OptionsOverlay, OptionsTitle, Hide);
    }
    internal string? CurrentSection { get; private set; }
    private void OnSection(object sender, RoutedEventArgs e) => SelectSection((string)((RadioButton)sender).Tag);
    private void SelectSection(string section)
    {
        if (CurrentSection == "Download" && section != CurrentSection) Model.Engineering.ClearDownloadPlan();
        CurrentSection = section;
        foreach (var item in new[] { ProjectOptions, ExportOptions, ImportOptions, DownloadOptions, InspectOptions, WorkspaceOptions })
            item.Visibility = item.Name == section + "Options" ? Visibility.Visible : Visibility.Collapsed;
        foreach (RadioButton tab in OptionTabs.Children) tab.IsChecked = Equals(tab.Tag, section);
        TabBar.Visibility = section == "Workspace" ? Visibility.Collapsed : Visibility.Visible;
        OptionsTitle.SetBinding(TextBlock.TextProperty, section == "Workspace"
            ? Localization.TrExtension.CreateBinding("Primer.NewWorkspace") : Localization.TrExtension.CreateBinding("Project.OptionsTitle"));
    }
    internal void Hide()
    {
        if (CurrentSection == "Download") Model.Engineering.ClearDownloadPlan();
        OptionsOverlay.Visibility = Visibility.Collapsed;
        _focus?.Dispose();
        _focus = null;
    }
    internal void SetControlPrefillMarker(bool pending)
        => PatternMarker.SetPending(pending);
    internal void SetMcpPrefillMarkers(bool project, bool export, bool import)
    {
        ProjectMarker.SetPending(project);
        OutputMarker.SetPending(export); FormatMarker.SetPending(export);
        ImportMarker.SetPending(import); OverwriteMarker.SetPending(import);
    }
    private void OnClearPrefill(object? sender, EventArgs e)
        => ClearPrefillRequested?.Invoke(this, (string)((FrameworkElement)sender!).Tag);
    private void OnCloseOptions(object sender, RoutedEventArgs e) => Hide();
    private void OnDeviceSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ViewModels.DeviceNode { Device: not null } node) Model.Engineering.SelectedDevice = node.Device;
    }

    private async void OnPickProcess(object sender, RoutedEventArgs e) => await Model.Session.ChooseProcessAsync(false);

    private void OnCheckEnvironment(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window) window.OpenEnvironmentCheck();
    }

    private void OnOpenConnection(object sender, RoutedEventArgs e)
    {
        var command = string.IsNullOrWhiteSpace(Model.Session.ProjectPath) ? Model.Session.Connect : Model.Session.OpenProject;
        if (command.CanExecute(null)) command.Execute(null);
    }
    private async void OnReloadTree(object sender, RoutedEventArgs e) => await Model.RefreshDeviceTreeAsync();
    private void OnBrowseProject(object sender, RoutedEventArgs e) => Model.Session.BrowseProject();

    private void OnScope(object sender, RoutedEventArgs e) => Model.Engineering.AllSoftware = Equals(((RadioButton)sender).Tag, "software");
    private async void OnReadAdapters(object sender, RoutedEventArgs e) => await Model.Engineering.ReadDownloadAdaptersAsync();
    private async void OnPreflight(object sender, RoutedEventArgs e) => await Model.Engineering.PreviewDownloadAsync();
    private async void OnSubmitDownload(object sender, RoutedEventArgs e) => await Model.Engineering.SubmitDownloadAsync(Hide);
    private void OnResetRules(object sender, RoutedEventArgs e) => Model.Engineering.NamePattern = EngineeringViewModel.DefaultNamePattern;

    private void OnBrowseOutput(object sender, RoutedEventArgs e) => Model.Engineering.BrowseOutput();

    private void OnBrowseWorkspace(object sender, RoutedEventArgs e) => Model.VersionControl.BrowseWorkspaceFolder();

}
