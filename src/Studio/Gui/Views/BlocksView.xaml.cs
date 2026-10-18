using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public sealed partial class BlocksView : UserControl, IDisposable
{
    public static readonly DependencyProperty AtlasProperty = DependencyProperty.Register(
        nameof(Atlas), typeof(AtlasPresentation), typeof(BlocksView));
    public AtlasPresentation? Atlas { get => (AtlasPresentation?)GetValue(AtlasProperty); private set => SetValue(AtlasProperty, value); }
    private MainViewModel Model => (MainViewModel)DataContext;
    private ListBoxItem? _pressedRow;

    public BlocksView()
    {
        InitializeComponent();
        BlocksList.SelectionChanged += (_, _) => ControlSelectionChanged?.Invoke(this, EventArgs.Empty);
        BlocksList.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnBlockRowClick));
        BlocksList.PreviewMouseLeftButtonDown += (_, e) =>
            _pressedRow = ItemsControl.ContainerFromElement(BlocksList, e.OriginalSource as DependencyObject) as ListBoxItem;
        BlocksList.PreviewKeyDown += OnBlockKey;
        Options.ClearPrefillRequested += (_, form) => ClearPrefillRequested?.Invoke(this, form);
        DataContextChanged += (_, _) =>
        {
            Atlas?.Dispose();
            Atlas = DataContext is MainViewModel model ? new AtlasPresentation(model, model.AtlasService) : null;
        };
    }

    internal void SetAtlasService(IAtlasService service)
    {
        Atlas?.Dispose();
        Atlas = new AtlasPresentation(Model, service);
    }
    internal void HideOptions() => Options.Hide();
    internal bool OptionsOpen => Options.IsOpen;
    internal event EventHandler? ControlSelectionChanged;
    internal event EventHandler<string>? ClearPrefillRequested;
    internal BlockRow? ControlFocusedBlock => BlocksList.SelectedItem as BlockRow;
    internal void LocateControlBlock(BlockRow row)
    {
        BlocksList.SelectedItem = row;
        BlocksList.ScrollIntoView(row);
    }
    internal void SetControlPrefillMarker(bool filter, bool selection)
    {
        FilterMarker.SetPending(filter);
        SelectionMarker.SetPending(selection);
    }
    private void OnClearPrefill(object? sender, EventArgs e)
        => ClearPrefillRequested?.Invoke(this, (string)((FrameworkElement)sender!).Tag);
    private void OnBlockKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || e.OriginalSource is CheckBox || BlocksList.SelectedItem is not BlockRow row) return;
        row.Selected = !row.Selected; e.Handled = true;
    }
    // The import/export options open in the 项目 page's options pane, which covers the whole page.
    internal event EventHandler? TransferOptionsRequested;
    private void OnExportOptions(object sender, RoutedEventArgs e) => TransferOptionsRequested?.Invoke(this, EventArgs.Empty);
    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (Model.Engineering.OutputDirectory.Length == 0) TransferOptionsRequested?.Invoke(this, EventArgs.Empty);
        else if (Model.Engineering.Export.CanExecute(null)) Model.Engineering.Export.Execute(null);
    }
    private void OnSelectAll(object sender, RoutedEventArgs e) => Model.Engineering.SelectAll(true);
    private void OnSelectNone(object sender, RoutedEventArgs e) => Model.Engineering.SelectAll(false);
    private void OnBlockRowClick(object sender, MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(BlocksList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item == null || item != _pressedRow) { _pressedRow = null; return; }
        _pressedRow = null;
        for (var source = e.OriginalSource as DependencyObject; source != null && source != item; source = VisualTreeHelper.GetParent(source))
            if (source is CheckBox) return;
        var row = (BlockRow)item.DataContext;
        row.Selected = !row.Selected;
    }
    private async void OnViewLadder(object sender, RoutedEventArgs e) { if (Atlas != null) await Atlas.GenerateAsync(true); }
    private async void OnGenerateAtlas(object sender, RoutedEventArgs e) { if (Atlas != null) await Atlas.GenerateAsync(false); }
    private void OnOpenAtlas(object sender, RoutedEventArgs e) => Atlas?.Open();
    private void OnCancelAtlas(object sender, RoutedEventArgs e) => Atlas?.Cancel();
    public void Dispose()
    {
        Atlas?.Dispose();
        Atlas = null;
    }
}
