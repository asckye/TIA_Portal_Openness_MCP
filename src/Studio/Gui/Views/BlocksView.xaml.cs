using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class BlocksView : UserControl, IDisposable
{
    public static readonly DependencyProperty AtlasProperty = DependencyProperty.Register(
        nameof(Atlas), typeof(AtlasPresentation), typeof(BlocksView));
    public AtlasPresentation? Atlas { get => (AtlasPresentation?)GetValue(AtlasProperty); private set => SetValue(AtlasProperty, value); }
    private MainViewModel Model => (MainViewModel)DataContext;

    public BlocksView()
    {
        InitializeComponent();
        BlocksList.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnBlockRowClick));
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
    private void OnExportOptions(object sender, RoutedEventArgs e) => Options.Show("Export");
    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (Model.Engineering.OutputDirectory.Length == 0) Options.Show("Export");
        else if (Model.Engineering.Export.CanExecute(null)) Model.Engineering.Export.Execute(null);
    }
    private void OnSelectAll(object sender, RoutedEventArgs e) => Model.Engineering.SelectAll(true);
    private void OnSelectNone(object sender, RoutedEventArgs e) => Model.Engineering.SelectAll(false);
    private void OnBlockRowClick(object sender, MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(BlocksList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item == null) return;
        for (var source = e.OriginalSource as DependencyObject; source != null && source != item; source = VisualTreeHelper.GetParent(source))
            if (source is CheckBox) return;
        var row = (BlockRow)item.DataContext;
        row.Selected = !row.Selected;
    }
    private async void OnViewLadder(object sender, RoutedEventArgs e) { if (Atlas != null) await Atlas.GenerateAsync(true); }
    private async void OnGenerateAtlas(object sender, RoutedEventArgs e) { if (Atlas != null) await Atlas.GenerateAsync(false); }
    private void OnOpenAtlas(object sender, RoutedEventArgs e) => Atlas?.Open();
    public void Dispose()
    {
        Atlas?.Dispose();
        Atlas = null;
    }
}
