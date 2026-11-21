using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public sealed partial class BlocksView : UserControl, IDisposable
{
    public static readonly DependencyProperty AtlasProperty = DependencyProperty.Register(
        nameof(Atlas), typeof(AtlasPresentation), typeof(BlocksView));
    public AtlasPresentation? Atlas { get => (AtlasPresentation?)GetValue(AtlasProperty); private set => SetValue(AtlasProperty, value); }
    private MainViewModel Model => (MainViewModel)DataContext;
    public BlocksView()
    {
        InitializeComponent();
        Options.LocalScrim.Source = BlockBackdrop;
        Options.ClearPrefillRequested += (_, form) => ClearPrefillRequested?.Invoke(this, form);
        DataContextChanged += (_, change) =>
        {
            if (change.OldValue is MainViewModel old) old.Engineering.PropertyChanged -= OnDocumentChanged;
            Atlas?.Dispose();
            Atlas = DataContext is MainViewModel model ? new AtlasPresentation(model, model.AtlasService) : null;
            if (DataContext is MainViewModel current)
            {
                current.Engineering.PropertyChanged += OnDocumentChanged;
                UpdateEditor();
            }
        };
    }

    internal event EventHandler? DownloadRequested;
    private async void OnOpenUnitMember(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is Services.UnitBlockInfo member) await Model.Engineering.OpenUnitBlockAsync(member.Unit, member.Name);
    }
    private void OnDownload(object sender, RoutedEventArgs e) => DownloadRequested?.Invoke(this, EventArgs.Empty);
    private async void OnViewLadderAtlas(object sender, RoutedEventArgs e)
    {
        if (Atlas != null) await Atlas.GenerateAsync(singleBlock: true);
    }
    private void OnLadderMode(object sender, RoutedEventArgs e) => UpdateEditor();
    private async void OnExportXml(object sender, RoutedEventArgs e) => await Model.Engineering.OpenXmlAsync(export: true);
    private async void OnOpenXml(object sender, RoutedEventArgs e) => await Model.Engineering.OpenXmlAsync();

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EngineeringViewModel.FocusedBlock))
        {
            UpdateEditor();
            ControlSelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        if (e.PropertyName is nameof(EngineeringViewModel.Document) or nameof(EngineeringViewModel.EmptyBlockList)
            or nameof(EngineeringViewModel.EmptyDeviceList) or nameof(EngineeringViewModel.SourceFailed) or nameof(EngineeringViewModel.Xml)
            or nameof(EngineeringViewModel.Screens) or nameof(EngineeringViewModel.SelectedUnit)) UpdateEditor();
    }
    private void UpdateEditor()
    {
        var doc = Model.Engineering.Document;
        bool hmi = doc == ProjectDocument.Hmi;
        bool node = hmi || doc == ProjectDocument.SoftwareUnit;
        NodeDocument.Visibility = node ? Visibility.Visible : Visibility.Collapsed;
        BlocksCard.Visibility = node ? Visibility.Collapsed : Visibility.Visible;
        if (node)
        {
            NodeKind.Text = hmi ? "HMI" : "SU";
            NodeBadge.SetResourceReference(Border.BackgroundProperty, hmi ? "Primer.blkHmBg" : "Primer.blkSuBg");
            NodeKind.SetResourceReference(TextBlock.ForegroundProperty, hmi ? "Primer.blkHmFg" : "Primer.blkSuFg");
            HmiMetrics.Visibility = HmiReadOnlyBadge.Visibility = hmi ? Visibility.Visible : Visibility.Collapsed;
            UnitMembers.Visibility = hmi ? Visibility.Collapsed : Visibility.Visible;
            NodeSubtitle.Text = hmi ? Model.Engineering.SelectedDevice?.TypeName : Model.Engineering.SelectedUnit?.NamespacePreset;
            NodeTitle.Text = hmi ? Model.Engineering.SelectedDevice?.Name : Model.Engineering.SelectedUnit?.Name;
            NodeHeading.SetBinding(TextBlock.TextProperty, hmi ? Localization.TrExtension.CreateBinding("Project.HmiScreens")
                : Localization.TrExtension.CreateBinding("Project.SoftwareUnit"));
            NodeKind.ToolTip = hmi ? NodeKind.Text : Localization.Loc.Current["Project.SoftwareUnit"];
            NodeDescription.Text = hmi ? Model.Engineering.HmiScreens == null ? Localization.Loc.Current["Project.HmiUnknown"]
                : Model.Engineering.HmiScreens == 0 ? Localization.Loc.Current["Project.NoScreens"] : ""
                : Localization.Loc.Current["Project.UnitBlocks"];
            NodeItems.ItemsSource = hmi ? Model.Engineering.Screens : null;
        }
        bool ladder = doc == ProjectDocument.Render && Model.Engineering.FocusedBlock?.HasLadderOrFbd == true;
        LadderToolbar.Visibility = ladder ? Visibility.Visible : Visibility.Collapsed;
        LadderPanel.Visibility = ladder && VisualTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        bool empty = (Model.Engineering.EmptyBlockList || Model.Engineering.EmptyDeviceList) && doc == ProjectDocument.Render;
        bool error = Model.Engineering.SourceFailed && Model.Engineering.Xml.Length == 0 && doc == ProjectDocument.Render;
        EmptyBlocks.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        SourceError.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
        BlockChooser.Visibility = !empty && Model.Engineering.FocusedBlock == null ? Visibility.Visible : Visibility.Collapsed;
        BlocksHeaderCard.Visibility = empty || error ? Visibility.Collapsed : Visibility.Visible;
        Editor.Visibility = doc == ProjectDocument.Compile || empty || error ? Visibility.Collapsed : Visibility.Visible;
        CodePanel.Visibility = doc == ProjectDocument.Render && !empty && !error && LadderPanel.Visibility != Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
        InterfacePanel.Visibility = doc == ProjectDocument.Interface ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnChooseBlock(object sender, MouseButtonEventArgs e) => await OpenChosenAsync();
    private async void OnChooserKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await OpenChosenAsync();
    }
    private async Task OpenChosenAsync()
    {
        if (BlockChooser.SelectedItem is not BlockRow row) return;
        LocateControlBlock(row);
        await Model.Engineering.ReadFocusedBlockAsync();
    }
    internal void SetAtlasService(IAtlasService service, Action<string>? open = null)
    {
        Atlas?.Dispose();
        Atlas = new AtlasPresentation(Model, service, open);
    }
    internal void ShowCompileTab() => Model.Engineering.Document = ProjectDocument.Compile;
    internal void HideOptions() => Options.Hide();
    internal bool OptionsOpen => Options.IsOpen;
    internal event EventHandler? ControlSelectionChanged;
    internal event EventHandler<string>? ClearPrefillRequested;
    internal BlockRow? ControlFocusedBlock => (DataContext as MainViewModel)?.Engineering.FocusedBlock;
    internal void LocateControlBlock(BlockRow row)
    {
        Model.Engineering.Document = ProjectDocument.Render;
        Model.Engineering.FocusedBlock = row;
    }
    internal event Action<bool>? FilterPendingChanged;
    internal void SetControlPrefillMarker(bool filter, bool selection)
    {
        if ((filter && FilterMarker.Visibility != Visibility.Visible) || (selection && SelectionMarker.Visibility != Visibility.Visible))
            Model.Engineering.Document = ProjectDocument.Render;
        FilterMarker.SetPending(filter);
        FilterPendingChanged?.Invoke(filter);
        SelectionMarker.SetPending(selection);
    }
    private void OnClearPrefill(object? sender, EventArgs e)
        => ClearPrefillRequested?.Invoke(this, (string)((FrameworkElement)sender!).Tag);
    internal event EventHandler? TransferOptionsRequested;
    internal void OpenTransferOptions() => TransferOptionsRequested?.Invoke(this, EventArgs.Empty);
    internal async void GenerateAtlas(object? sender, RoutedEventArgs e)
    {
        if (Atlas != null) await Atlas.GenerateAsync(false);
    }
    private void OnAtlasFolder(object sender, RoutedEventArgs e) => Atlas?.Open(true);
    private void OnDismissAtlas(object sender, RoutedEventArgs e) => Atlas?.Dismiss();
    private async void OnRetryAtlas(object sender, RoutedEventArgs e)
    {
        if (Atlas != null) await Atlas.GenerateAsync(false, skipProtected: true);
    }
    private void OnOpenAtlas(object sender, RoutedEventArgs e) => Atlas?.Open();
    private void OnCancelAtlas(object sender, RoutedEventArgs e) => Atlas?.Cancel();
    public void Dispose()
    {
        if (DataContext is MainViewModel model) model.Engineering.PropertyChanged -= OnDocumentChanged;
        Atlas?.Dispose();
        Atlas = null;
    }
}
