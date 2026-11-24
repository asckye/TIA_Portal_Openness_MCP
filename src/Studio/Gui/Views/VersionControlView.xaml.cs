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
        SizeChanged += (_, e) => SyncStrip.Tag = e.NewSize.Width < 820 ? "Compact" : null;
        Options.LocalScrim.Source = WorkspaceBackdrop;
    }
    private MainViewModel Model => (MainViewModel)DataContext;
    internal void HideOptions() => Options.Hide();
    internal bool OptionsOpen => Options.IsOpen;
    private void OnBackToWorkspace(object sender, RoutedEventArgs e) => Model.VersionControl.CloseSyncResult();
    private void OnBrowseWorkspace(object sender, RoutedEventArgs e) => Model.VersionControl.BrowseWorkspaceFolder();
    internal Button GitToolsButton => Options.GitToolsButton;
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
        var vc = Model.VersionControl;
        vc.VcShowAll = true;
        if (vc.VcStatus.CanExecute(null)) vc.VcStatus.Execute(null);
    }
    private void OnDirection(object sender, RoutedEventArgs e) => Model.VersionControl.CloseSyncResult();
    private void OnApplyPreview(object sender, RoutedEventArgs e)
    {
        var vc = Model.VersionControl;
        if (vc.SyncResult is not { DryRun: true } preview) return;
        var command = preview.Direction == TiaOpenness.Contracts.Models.SyncDirection.ProjectToWorkspace ? vc.VcPush : vc.VcPull;
        if (!command.CanExecute(null)) return;
        vc.VcDryRun = false;
        command.Execute(null);
    }
    private void OnPreview(object sender, RoutedEventArgs e) => Model.VersionControl.VcDryRun = true;
    private void OnRun(object sender, RoutedEventArgs e) => Model.VersionControl.VcDryRun = false;

}
