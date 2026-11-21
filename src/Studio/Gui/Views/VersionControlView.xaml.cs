using System;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class VersionControlView : UserControl
{
    public VersionControlView()
    {
        InitializeComponent();
        Options.LocalScrim.Source = WorkspaceBackdrop;
    }
    private MainViewModel Model => (MainViewModel)DataContext;
    internal void HideOptions() => Options.Hide();
    internal bool OptionsOpen => Options.IsOpen;
    private void OnWorkspaceAction(object sender, RoutedEventArgs e)
    {
        if (Model.VersionControl.SelectedWorkspace == null) { Options.Show("Workspace"); return; }
        try
        {
            System.Diagnostics.Process
                .Start(new System.Diagnostics.ProcessStartInfo(Model.VersionControl.SelectedWorkspace.RootPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Model.Activity.Append(ex.Message);
            Model.Activity.Notify(ToastKind.Error, Localization.LocalizedText.Literal(ex.Message), "workspace");
        }
    }
    private void OnGitTools(object sender, RoutedEventArgs e)
        => Model.Activity.Notify(ToastKind.Info, Localization.LocalizedText.Key("Pages.GitHint"), "git-guidance");
    private void OnWorkspaceOptions(object sender, RoutedEventArgs e) => Options.Show("Workspace");
    private void OnMappedFile(object sender, RoutedEventArgs e) => Model.VersionControl.SelectedVcItem =
        (TiaOpenness.Contracts.Models.MappedObjectInfo)((Button)sender).Tag;
    private void OnMappedFilter(object sender, RoutedEventArgs e)
    {
        string state = (string)((FrameworkElement)sender).Tag;
        MappedList.Items.Filter = item => ((TiaOpenness.Contracts.Models.MappedObjectInfo)item).CompareState.ToString() == state;
    }
    private void OnMappedAll(object sender, RoutedEventArgs e)
    {
        MappedList.Items.Filter = null;
        Model.VersionControl.VcShowAll = true;
        if (Model.VersionControl.VcStatus.CanExecute(null)) Model.VersionControl.VcStatus.Execute(null);
    }
    private void OnPreview(object sender, RoutedEventArgs e) => Model.VersionControl.VcDryRun = true;
    private void OnRun(object sender, RoutedEventArgs e) => Model.VersionControl.VcDryRun = false;

}
