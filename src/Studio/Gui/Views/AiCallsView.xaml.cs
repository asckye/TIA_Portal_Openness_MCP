using System;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class AiCallsView : UserControl
{
    private bool _atTop = true, _followQueued;
    public AiCallsView()
    {
        InitializeComponent(); DataContext = null; DataContextChanged += OnModelChanged;
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
    }
    private FeaturePagesViewModel Model => (FeaturePagesViewModel)DataContext;
    internal void LocateControlCall(CallRow row)
    {
        CallList.SelectedItem = row;
        CallList.ScrollIntoView(row);
    }
    private void OnModelChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is FeaturePagesViewModel old) { old.PropertyChanged -= OnChanged; ((INotifyCollectionChanged)old.Calls).CollectionChanged -= OnRowsChanged; }
        if (e.NewValue is FeaturePagesViewModel model) { model.PropertyChanged += OnChanged; ((INotifyCollectionChanged)model.Calls).CollectionChanged += OnRowsChanged; }
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Model.FollowLatest) && Model.FollowLatest && Model.Calls.Count > 0)
            CallList.ScrollIntoView(Model.Calls[0]);
    }
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is ScrollViewer scroll && e.ExtentHeightChange == 0 && e.VerticalChange != 0) _atTop = scroll.VerticalOffset <= 2;
    }
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!Model.FollowLatest || !_atTop || _followQueued || !IsVisible) return;
        _followQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _followQueued = false;
            if (DataContext is FeaturePagesViewModel model && model.FollowLatest && _atTop && model.Calls.Count > 0) CallList.ScrollIntoView(model.Calls[0]);
        }));
    }
    private void OnFollow(object sender, RoutedEventArgs e) => Model.ToggleFollow();
    private void OnApprovals(object sender, RoutedEventArgs e) => Model.OpenApprovals();
    private void OnCall(object sender, RoutedEventArgs e) => Model.OpenCall((CallRow)((Button)sender).DataContext);
    private void OnCopyConnection(object sender, RoutedEventArgs e) => Model.Copy(Model.ConnectionJson);
}
