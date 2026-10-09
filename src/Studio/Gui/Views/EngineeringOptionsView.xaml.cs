using System;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class EngineeringOptionsView : UserControl
{
    public EngineeringOptionsView() => InitializeComponent();
    private MainViewModel _model => (MainViewModel)DataContext;
    internal void Show(string section)
    {
        foreach (var item in new[] { ProjectOptions, ExportOptions, InspectOptions, WorkspaceOptions })
            item.Visibility = item.Name == section + "Options" ? Visibility.Visible : Visibility.Collapsed;
        string key = section switch { "Project" => "Project.Label", "Export" => "Primer.Transfer", "Inspect" => "Toolbar.Inspect", _ => "Primer.NewWorkspace" };
        OptionsTitle.SetBinding(TextBlock.TextProperty, Localization.TrExtension.CreateBinding(key));
        OptionsOverlay.Visibility = Visibility.Visible;
    }
    internal void Hide() => OptionsOverlay.Visibility = Visibility.Collapsed;
    internal void SetControlPrefillMarker(bool pending)
        => ControlNamePattern.ToolTip = pending ? Localization.Loc.Current["Control.Prefilled"] : null;
    private void OnCloseOptions(object sender, RoutedEventArgs e) => Hide();
    private void OnDeviceSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ViewModels.DeviceNode { Device: not null } node) _model.Engineering.SelectedDevice = node.Device;
    }

    private void OnBrowseProject(object sender, RoutedEventArgs e) => _model.Session.BrowseProject();

    private void OnBrowseOutput(object sender, RoutedEventArgs e) => _model.Engineering.BrowseOutput();

    private void OnBrowseWorkspace(object sender, RoutedEventArgs e) => _model.VersionControl.BrowseWorkspaceFolder();

}
