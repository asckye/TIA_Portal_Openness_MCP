using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class AiCallsView : UserControl
{
    public AiCallsView() { InitializeComponent(); DataContext = null; DataContextChanged += OnModelChanged; }
    private FeaturePagesViewModel Model => (FeaturePagesViewModel)DataContext;
    private void OnModelChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is FeaturePagesViewModel old) old.PropertyChanged -= OnChanged;
        if (e.NewValue is FeaturePagesViewModel model) model.PropertyChanged += OnChanged;
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Model.FollowLatest && (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(Model.Calls))) CallScroll.ScrollToTop();
    }
    private void OnFollow(object sender, RoutedEventArgs e) => Model.ToggleFollow();
    private void OnApprovals(object sender, RoutedEventArgs e) => Model.OpenApprovals();
    private void OnCall(object sender, RoutedEventArgs e) => Model.OpenCall((CallRow)((Button)sender).DataContext);
    private void OnCopyConnection(object sender, RoutedEventArgs e) => Model.Copy(Model.ConnectionJson);
}
