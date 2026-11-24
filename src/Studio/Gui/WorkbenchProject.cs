using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.Services.Stubs;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui;

public partial class MainWindow
{
    internal static bool MatchesBlockReview(ApprovalRequest request, string release, string file, string plc, string path)
    {
        if (request.State != ApprovalState.Pending || request.Actor != "mcp" || request.Release != release || file.Length == 0
            || !string.Equals(request.Project, file, StringComparison.OrdinalIgnoreCase) || request.Plc != plc) return false;
        try
        {
            return JsonNode.Parse(request.ParametersJson) is JsonObject values && values["blockPath"] is JsonValue value
                && value.TryGetValue<string>(out var target) && target == path;
        }
        catch (JsonException error)
        {
            System.Diagnostics.Trace.TraceWarning("Invalid target JSON for request {0} at byte {1}.", request.Id, error.BytePositionInLine);
            return false;
        }
    }
    private ApprovalRequest? BlockReview() => _model.Session.IsConnected && _model.Engineering.FocusedBlock is { } block
        && _model.Engineering.SelectedDevice is { } plc ? Approvals?.Requests.FirstOrDefault(request => MatchesBlockReview(request,
            _model.SelectedReleaseKey, _model.Session.OpenProjectFile, plc.Id, block.Path)) : null;
    private void UpdateProjectReview()
        => OperationsContent.BlockReviewButton.Visibility = BlockReview() == null ? Visibility.Collapsed : Visibility.Visible;
    private void OnProjectReviewsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (Dispatcher.CheckAccess()) UpdateProjectReview();
        else Dispatcher.BeginInvoke(UpdateProjectReview);
    }

    // Preserve automation lookups when a project command moves to another namescope.
    private void RegisterProjectControls()
    {
        OperationsContent.RegisterName("TagsTab", OperationsContent.TagsTab);
        OperationsContent.RegisterName("RunEnvironmentItem", OperationsContent.Options.RunEnvironmentItem);
        EngineeringSideContent.RegisterName("CompileButton", OperationsContent.ToolbarCompile);
        BlocksContent.RegisterName("ViewLadderAtlas", OperationsContent.ViewLadderAtlas);
        BlocksContent.RegisterName("AtlasFooter", AtlasFooter);
        BlocksContent.RegisterName("AtlasBar", AtlasBar);
        VersionControlContent.RegisterName("GitToolsButton", VersionControlContent.Options.GitToolsButton);
    }

    private void OnProjectPageInitialized(object sender, EventArgs e)
    {
        OperationsContent.BlockReviewButton.Click += (_, _) =>
        {
            if (BlockReview() is not { } request) return;
            Navigate("Approvals");
            ApprovalsContent.FocusRequest(request.Id);
        };
        BlocksContent.AtlasNotice += ShowToast;
        BlocksContent.ClearAtlasNotice += () => _toasts.Remove("atlas");
        OperationsContent.PendingTransfer = () => _prefills.Latest?.Form == TiaOpenness.Shared.WorkbenchPrefillForm.ImportBlocks ? "Import" : "Export";
        OperationsContent.TagsTab = ((Views.PlcTagsView)PlcTagsContent.Content).TagsTab;
        ProjectExplorer.OptionsRequested += (_, _) => OperationsContent.ShowProjectOptions();
        ProjectExplorer.OpenPage = OpenTreePage;
        var openImport = new Common.AsyncCommand(() =>
        {
            OperationsContent.Options.Show("Import"); return System.Threading.Tasks.Task.CompletedTask;
        }, () => _model.Engineering.Import.CanExecute(null));
        BlocksContent.EmptyBlocks.ActionCommand = openImport;
        _model.Engineering.Import.CanExecuteChanged += (_, _) => openImport.RaiseCanExecuteChanged();
        OperationsContent.ViewLadderAtlas.Click += async (_, _) =>
        {
            if (BlocksContent.Atlas is { } atlas) await atlas.GenerateAsync(true);
        };
        OperationsContent.ViewLadderAtlas.SetBinding(IsEnabledProperty, new Binding("Atlas.CanViewBlock") { Source = BlocksContent });
        BlocksContent.SelectionMarker.Target = ProjectExplorer.Nodes;
        ProjectExplorer.SelectionMarker.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = BlocksContent.SelectionMarker });
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
            if (change.PropertyName is nameof(EngineeringViewModel.Document) or nameof(EngineeringViewModel.FocusedBlock)
                or nameof(EngineeringViewModel.EmptyBlockList) or nameof(EngineeringViewModel.EmptyDeviceList)) ShowDocument();
        };
        _model.Session.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(SessionViewModel.IsConnected) or nameof(SessionViewModel.ProjectName)
                or nameof(SessionViewModel.LostConnection)) ShowDocument();
        };
        ProjectOverview.SizeChanged += (_, _) => ShowDocument();
        Root.SizeChanged += (_, _) => ShowDocument();
        ShowDocument();
    }

    private void ShowDocument()
    {
        UpdateProjectReview();
        var document = _model.Engineering.Document;
        bool tags = document == ProjectDocument.Tags;
        bool compile = document == ProjectDocument.Compile;
        BlockDocument.Visibility = tags || compile ? Visibility.Collapsed : Visibility.Visible;
        CompileDocument.Visibility = compile ? Visibility.Visible : Visibility.Collapsed;
        PlcTagsContent.Visibility = tags ? Visibility.Visible : Visibility.Collapsed;
        bool empty = HasProject && document == ProjectDocument.Render && (_model.Engineering.EmptyBlockList || _model.Engineering.EmptyDeviceList);
        OperationsContent.EmptyToolbar.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        OperationsContent.ProjectToolbar.Visibility = _model.Session.LostConnection ||
            HasProject && !empty && !tags && document is not (ProjectDocument.Hmi or ProjectDocument.SoftwareUnit)
            ? Visibility.Visible : Visibility.Collapsed;
        bool lost = _model.Session.LostConnection;
        if (HasProject || lost) ProjectTree.ClearValue(VisibilityProperty);
        else ProjectTree.Visibility = Visibility.Collapsed;
        OperationsContent.BlockIdentity.Visibility = Root.ActualWidth >= 1280 && _model.Engineering.FocusedBlock != null
            ? Visibility.Visible : Visibility.Collapsed;
        DocumentHost.RowDefinitions[0].Height = compile ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        DocumentHost.RowDefinitions[1].Height = compile ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
    }

    private void OnCancelAtlas(object sender, RoutedEventArgs e) => BlocksContent.Atlas?.Cancel();
    private void OnCancelProjectRead(object sender, RoutedEventArgs e) => _model.Engineering.CancelDocumentRead();

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
