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
        _focus?.Dispose();
        foreach (var item in new[] { ProjectOptions, ExportOptions, InspectOptions, WorkspaceOptions })
            item.Visibility = item.Name == section + "Options" ? Visibility.Visible : Visibility.Collapsed;
        string key = section switch { "Project" => "Project.Label", "Export" => "Primer.Transfer", "Inspect" => "Toolbar.Inspect", _ => "Primer.NewWorkspace" };
        OptionsTitle.SetBinding(TextBlock.TextProperty, Localization.TrExtension.CreateBinding(key));
        OptionsOverlay.Visibility = Visibility.Visible;
        _focus = new Controls.ModalPane(OptionsOverlay, OptionsTitle, Hide);
    }
    internal void Hide() { OptionsOverlay.Visibility = Visibility.Collapsed; _focus?.Dispose(); _focus = null; }
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

    private void OnBrowseProject(object sender, RoutedEventArgs e) => Model.Session.BrowseProject();

    private void OnBrowseOutput(object sender, RoutedEventArgs e) => Model.Engineering.BrowseOutput();

    private void OnBrowseWorkspace(object sender, RoutedEventArgs e) => Model.VersionControl.BrowseWorkspaceFolder();

}
