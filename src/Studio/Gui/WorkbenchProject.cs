using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui;

public partial class MainWindow
{
    private void OnProjectPageInitialized(object sender, EventArgs e)
    {
        ProjectExplorer.OpenPage = OpenTreePage;
        ProjectExplorer.SelectionMarker.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = BlocksContent.SelectionMarker });
        ProjectExplorer.Nodes.SetBinding(AutomationProperties.HelpTextProperty, new Binding
        {
            Path = new PropertyPath(AutomationProperties.HelpTextProperty), Source = BlocksContent.BlocksList,
        });
        ProjectExplorer.SelectionMarker.ClearRequested += (source, _) => OnClearFieldPrefill(source, "BlockSelection");
        LeftPaneToggle.Click += (_, _) =>
        {
            if (ProjectTree.Content == ProjectExplorer && _leftPaneFlyoutOpen && ProjectTree.IsKeyboardFocusWithin) ProjectTree.Focus();
        };

        _model.Engineering.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(EngineeringViewModel.Document)) ShowDocument();
        };
        ProjectOverview.SizeChanged += (_, _) => ShowDocument();
        Root.SizeChanged += (_, resize) =>
        {
            if (Stacked(resize.PreviousSize.Width) != Stacked(resize.NewSize.Width)) ShowDocument();
        };
        ShowDocument();
    }

    private static bool Stacked(double width) => width is > 0 and < 1199.5;
    private void OnProjectContextClick(object sender, RoutedEventArgs e) => ShowDocument();

    private void ShowDocument()
    {
        var document = _model.Engineering.Document;
        bool stacked = Stacked(Root.ActualWidth);
        bool compile = document == ProjectDocument.Compile;
        bool canFold = compile && !stacked && ProjectOverview.ActualWidth is > 0 and < 1232;
        FoldProjectContext.Visibility = canFold ? Visibility.Visible : Visibility.Collapsed;
        if (!canFold) FoldProjectContext.IsChecked = false;
        double height = stacked ? Math.Max(520, ProjectOverview.ActualHeight) : double.NaN;
        ProjectScroll.VerticalScrollBarVisibility = stacked ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        BlockDocument.Visibility = document == ProjectDocument.Tags ? Visibility.Collapsed : Visibility.Visible;
        CompileDocument.Visibility = compile && FoldProjectContext.IsChecked != true ? Visibility.Visible : Visibility.Collapsed;
        PlcTagsContent.Visibility = document == ProjectDocument.Tags ? Visibility.Visible : Visibility.Collapsed;
        BlockDocument.Height = CompileDocument.Height = PlcTagsContent.Height = height;
        Grid.SetRow(BlockDocument, stacked && compile ? 1 : 0);
        Grid.SetColumn(CompileDocument, stacked ? 0 : 1);
        CompileDocument.Width = stacked ? double.NaN : 300;
        CompileDocument.Margin = new Thickness(stacked ? 0 : 16, 0, 0, 0);
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
