using System;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class VersionControlView : UserControl
{
    public VersionControlView() => InitializeComponent();
    private MainViewModel _model => (MainViewModel)DataContext;
    internal void HideOptions() => Options.Hide();
    private void OnWorkspaceAction(object sender, RoutedEventArgs e)
    {
        if (_model.VersionControl.SelectedWorkspace == null) { Options.Show("Workspace"); return; }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_model.VersionControl.SelectedWorkspace.RootPath) { UseShellExecute = true });
        }
        catch (Exception ex) { _model.Activity.Append(ex.Message); }
    }
    private void OnGitTools(object sender, RoutedEventArgs e)
        => Controls.WorkbenchMessageBox.Show(Window.GetWindow(this), Localization.Loc.Current["Pages.GitHint"], Localization.Loc.Current["Pages.GitTools"], MessageBoxButton.OK, MessageBoxImage.Information);
    private void OnWorkspaceOptions(object sender, RoutedEventArgs e) => Options.Show("Workspace");
    private void OnMappedFile(object sender, RoutedEventArgs e) => _model.VersionControl.SelectedVcItem = (TiaOpenness.Contracts.Models.MappedObjectInfo)((Button)sender).Tag;
    private void OnMappedFilter(object sender, RoutedEventArgs e)
    {
        string state = (string)((Button)sender).Tag;
        MappedList.Items.Filter = item => ((TiaOpenness.Contracts.Models.MappedObjectInfo)item).CompareState.ToString() == state;
    }
    private void OnMappedAll(object sender, RoutedEventArgs e)
    {
        MappedList.Items.Filter = null;
        if (_model.VersionControl.VcStatus.CanExecute(null)) _model.VersionControl.VcStatus.Execute(null);
    }
    private void OnPreview(object sender, RoutedEventArgs e) => _model.VersionControl.VcDryRun = true;
    private void OnRun(object sender, RoutedEventArgs e) => _model.VersionControl.VcDryRun = false;

}
