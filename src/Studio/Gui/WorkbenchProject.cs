using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui;

public partial class MainWindow
{
    private Panel? _shellBannerSlot;
    private void PlaceSafetyBanner()
    {
        if (_shellBannerSlot == null || SafetyBanner.Parent is not Panel current) return;
        Panel slot = EngineeringPage.Visibility != Visibility.Visible ? _shellBannerSlot
            : VersionControlContent.Visibility == Visibility.Visible ? VersionControlContent.BannerSlot
            : _model.Engineering.Document == ProjectDocument.Tags ? ((Views.PlcTagsView)PlcTagsContent.Content).BannerSlot : OperationsContent.BannerSlot;
        if (current == slot) return;
        current.Children.Remove(SafetyBanner);
        slot.Children.Add(SafetyBanner);
    }

    private void OnProjectPageInitialized(object sender, EventArgs e)
    {
        _shellBannerSlot = (Panel)SafetyBanner.Parent;
        EngineeringPage.IsVisibleChanged += (_, _) => PlaceSafetyBanner();
        VersionControlContent.IsVisibleChanged += (_, _) => PlaceSafetyBanner();
        OperationsContent.TagsTab = ((Views.PlcTagsView)PlcTagsContent.Content).TagsTab;
        ProjectExplorer.OpenPage = OpenTreePage;
        BlocksContent.SelectionMarker.Target = ProjectExplorer.Nodes;
        ProjectExplorer.SelectionMarker.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = BlocksContent.SelectionMarker });
        ProjectExplorer.SelectionMarker.ClearRequested += (source, _) => OnClearFieldPrefill(source, "BlockSelection");
        ProjectExplorer.FilterClearRequested += (source, _) => OnClearFieldPrefill(source, "BlockFilter");
        BlocksContent.FilterPendingChanged += ProjectExplorer.SetFilterPending;
        OperationsContent.GenerateAtlasItem.Click += BlocksContent.GenerateAtlas;
        OperationsContent.GenerateAtlasItem.SetBinding(IsEnabledProperty, new Binding("Atlas.CanGenerate") { Source = BlocksContent });
        OperationsContent.OpenTransferRequested += (_, _) => BlocksContent.OpenTransferOptions();
        LeftPaneToggle.Click += (_, _) =>
        {
            if (ProjectTree.Content == ProjectExplorer && _leftPaneFlyoutOpen && ProjectTree.IsKeyboardFocusWithin) ProjectTree.Focus();
        };

        _model.Engineering.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(EngineeringViewModel.Document) or nameof(EngineeringViewModel.FocusedBlock)) ShowDocument();
        };
        Root.SizeChanged += (_, _) => ShowDocument();
        ShowDocument();
    }

    private void OnProjectContextClick(object sender, RoutedEventArgs e) => ShowDocument();

    private void ShowDocument()
    {
        PlaceSafetyBanner();
        var document = _model.Engineering.Document;
        bool tags = document == ProjectDocument.Tags;
        bool compile = document == ProjectDocument.Compile;
        bool foldable = compile && Root.ActualWidth >= 1200 && ProjectOverview.ActualWidth < 1232;
        FoldProjectContext.Visibility = foldable ? Visibility.Visible : Visibility.Collapsed;
        if (!foldable) FoldProjectContext.IsChecked = false;
        // Keep the fold shortcut in the content margin, clear of the Save button.
        FoldProjectContext.Margin = new Thickness(0, 47, 1, 0);
        BlockDocument.Visibility = tags ? Visibility.Collapsed : Visibility.Visible;
        CompileDocument.Visibility = compile && FoldProjectContext.IsChecked != true ? Visibility.Visible : Visibility.Collapsed;
        PlcTagsContent.Visibility = tags ? Visibility.Visible : Visibility.Collapsed;
        OperationsContent.ProjectToolbar.Visibility = tags ? Visibility.Collapsed : Visibility.Visible;
        OperationsContent.BlockIdentity.Visibility = Root.ActualWidth >= 1280 && _model.Engineering.FocusedBlock != null
            ? Visibility.Visible : Visibility.Collapsed;
        DocumentHost.RowDefinitions[0].Height = compile ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        DocumentHost.RowDefinitions[1].Height = compile ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        Grid.SetRow(CompileDocument, 1);
        Grid.SetColumn(CompileDocument, 0);
        Grid.SetColumnSpan(CompileDocument, 2);
        CompileDocument.Width = double.NaN;
        CompileDocument.Margin = new Thickness(28, 16, 28, 20);
        CompileDocument.MaxWidth = 920;
    }

    private bool OpenTreePage(string page, bool keepTreeOpen)
    {
        if (_page is not ("Engineering" or "VersionControl") || ShellModalOpen) return false;
        string target = page == "Blocks" ? "Engineering" : page;
        if (_page != target || (_leftPaneFlyoutOpen && !keepTreeOpen))
        {
            bool retainFlyout = keepTreeOpen && _leftPaneFlyoutOpen;
            WorkbenchCommands.Navigate.Execute(page, ProjectTree);
            if (retainFlyout && _page == target)
            {
                _leftPaneFlyoutOpen = true;
                UpdateLeftPane();
                Root.UpdateLayout();
            }
        }
        return _page == target;
    }
}
