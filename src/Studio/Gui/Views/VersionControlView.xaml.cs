using System;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class VersionControlView : UserControl
{
    public VersionControlView() => InitializeComponent();
    private MainViewModel Model => (MainViewModel)DataContext;
    internal void HideOptions() => Options.Hide();
    internal bool OptionsOpen => Options.IsOpen;
    private void OnWorkspaceAction(object sender, RoutedEventArgs e)
    {
        if (Model.VersionControl.SelectedWorkspace == null) { Options.Show("Workspace"); return; }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Model.VersionControl.SelectedWorkspace.RootPath) { UseShellExecute = true });
        }
        catch (Exception ex) { Model.Activity.Append(ex.Message); }
    }
    private void OnGitTools(object sender, RoutedEventArgs e)
        => Controls.WorkbenchMessageBox.Show(Window.GetWindow(this), Localization.Loc.Current["Pages.GitHint"], Localization.Loc.Current["Pages.GitTools"], MessageBoxButton.OK, MessageBoxImage.Information);
    private void OnWorkspaceOptions(object sender, RoutedEventArgs e) => Options.Show("Workspace");
    private void OnMappedFile(object sender, RoutedEventArgs e) => Model.VersionControl.SelectedVcItem = (TiaOpenness.Contracts.Models.MappedObjectInfo)((Button)sender).Tag;
    private void OnMappedFilter(object sender, RoutedEventArgs e)
    {
        string state = (string)((Button)sender).Tag;
        MappedList.Items.Filter = item => ((TiaOpenness.Contracts.Models.MappedObjectInfo)item).CompareState.ToString() == state;
    }
    private void OnMappedAll(object sender, RoutedEventArgs e)
    {
        MappedList.Items.Filter = null;
        if (Model.VersionControl.VcStatus.CanExecute(null)) Model.VersionControl.VcStatus.Execute(null);
    }
    private void OnPreview(object sender, RoutedEventArgs e) => Model.VersionControl.VcDryRun = true;
    private void OnRun(object sender, RoutedEventArgs e) => Model.VersionControl.VcDryRun = false;

}
