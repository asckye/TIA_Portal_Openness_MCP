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

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EngineeringViewModel.FocusedBlock))
        {
            UpdateEditor();
            ControlSelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        if (e.PropertyName == nameof(EngineeringViewModel.Document)) UpdateEditor();
    }
    private void UpdateEditor()
    {
        var doc = Model.Engineering.Document;
        BlockChooser.Visibility = Model.Engineering.FocusedBlock == null ? Visibility.Visible : Visibility.Collapsed;
        Editor.Visibility = doc == ProjectDocument.Compile ? Visibility.Collapsed : Visibility.Visible;
        CodePanel.Visibility = doc == ProjectDocument.Render ? Visibility.Visible : Visibility.Collapsed;
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
    internal void SetAtlasService(IAtlasService service)
    {
        Atlas?.Dispose();
        Atlas = new AtlasPresentation(Model, service);
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
    private void OnOpenAtlas(object sender, RoutedEventArgs e) => Atlas?.Open();
    private void OnCancelAtlas(object sender, RoutedEventArgs e) => Atlas?.Cancel();
    public void Dispose()
    {
        if (DataContext is MainViewModel model) model.Engineering.PropertyChanged -= OnDocumentChanged;
        Atlas?.Dispose();
        Atlas = null;
    }
}
