using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class ApprovalsDrawer : UserControl
{
    private ApprovalRow? _row, _confirming;
    private ModalPane? _confirmationFocus;
    private Window? _owner;
    public ApprovalsDrawer()
    {
        InitializeComponent(); DataContext = null;
        DataContextChanged += OnModelChanged;
        Unloaded += OnUnloaded;
        Loaded += OnLoaded;
        IsVisibleChanged += (_, e) => { if (!(bool)e.NewValue) CloseConfirmation(); };
    }
    private FeaturePagesViewModel Model => (FeaturePagesViewModel)DataContext;
    private bool Guarded => (Window.GetWindow(this) as MainWindow)?.ControlGuard.ClickGuardActive == true;
    private void OnApprove(object sender, RoutedEventArgs e)
    {
        if (Guarded || _row is not { CanDecide: true } row) return;
        if (!row.Destructive) { Model.Decide(row, true); return; }
        _confirming = row;
        ConfirmationSheet.Visibility = Visibility.Visible;
        _confirmationFocus = new ModalPane(ConfirmationSheet, CancelDestructive, CloseConfirmation);
    }
    private void OnDeny(object sender, RoutedEventArgs e) { if (!Guarded && _row is { CanDecide: true } row) Model.Decide(row, false); }
    private void OnConfirmDestructive(object sender, RoutedEventArgs e)
    {
        if (Guarded) return;
        var row = _confirming;
        CloseConfirmation();
        if (row is { CanDecide: true } && ReferenceEquals(row, _row)) Model.Decide(row, true);
    }
    private void OnCancelDestructive(object sender, RoutedEventArgs e) => CloseConfirmation();
    private void CloseConfirmation()
    {
        ConfirmationSheet.Visibility = Visibility.Collapsed;
        _confirmationFocus?.Dispose(); _confirmationFocus = null; _confirming = null;
    }
    private void OnDecisionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { OnEscape(sender, e); return; }
        if (e.Key != Key.Enter && !(e.Key == Key.System && e.SystemKey == Key.Enter)) return;
        // Enter always takes the safe path, even with focus on either approval button.
        if (_confirming != null) { e.Handled = true; CloseConfirmation(); }
        else if (_row is { Pending: true })
        { e.Handled = true; if ((_row.Destructive || DenyRequest.IsKeyboardFocusWithin) && !Guarded) Model.Decide(_row, false); }
    }
    private void OnModelChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is FeaturePagesViewModel previous) previous.PropertyChanged -= OnModelPropertyChanged;
        if (e.NewValue is FeaturePagesViewModel model) model.PropertyChanged += OnModelPropertyChanged;
        SelectRow();
    }
    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(FeaturePagesViewModel.SelectedRequest)) SelectRow(); }
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RequestList.SelectedItem is ApprovalRow row && DataContext is FeaturePagesViewModel model && row != model.SelectedRequest)
        {
            model.HandledRequests = !row.Pending;
            model.SelectedRequest = row;
        }
    }
    private void OnRequestPressed(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject target
            && ItemsControl.ContainerFromElement(RequestList, target) is ListBoxItem { DataContext: ApprovalRow row })
            Model.ClearDecisionForSelection(row);
    }

    private void SelectRow()
    {
        var next = (DataContext as FeaturePagesViewModel)?.SelectedRequest;
        if (ReferenceEquals(next, _row)) return;
        CloseConfirmation();
        if (_row != null) { _row.CountdownThreshold -= OnCountdownThreshold; _row.PropertyChanged -= OnRowChanged; }
        _row = next;
        CountdownCaption.ClearValue(System.Windows.Automation.AutomationProperties.NameProperty);
        System.Windows.Automation.AutomationProperties.SetLiveSetting(CountdownCaption, System.Windows.Automation.AutomationLiveSetting.Off);
        if (_row != null) { _row.CountdownThreshold += OnCountdownThreshold; _row.PropertyChanged += OnRowChanged; }
        DetailScroll?.ScrollToTop();
    }
    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(ApprovalRow.CanDecide) && _confirming is { CanDecide: false }) CloseConfirmation(); }
    private void OnCountdownThreshold(object? sender, int seconds)
    {
        if (!IsVisible || _row?.Pending != true) return;
        System.Windows.Automation.AutomationProperties.SetName(CountdownCaption, Loc.Current.T("Approval.CountdownAnnouncement", seconds));
        AccessibleStatus.Announce(CountdownCaption);
    }
    internal void FocusRequest(string id)
    {
        var request = System.Linq.Enumerable.FirstOrDefault(Model.Requests, r => r.Request.Id == id);
        if (request == null) return;
        Model.HandledRequests = !request.Pending;
        Model.SelectedRequest = request;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { if (IsVisible && _row?.Request.Id == id) RequestTitle.Focus(); }));
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SelectRow();
        _owner = Window.GetWindow(this);
        if (_owner != null) _owner.KeyDown += OnEscape;
    }
    private void OnEscape(object sender, KeyEventArgs e)
    {
        // The shell heading can retain focus. Bubble unhandled Escape after other controls have had their turn.
        if (e.Key != Key.Escape || !IsVisible || !IsEnabled || _confirming != null || _row is not { Destructive: true, CanDecide: true } row) return;
        e.Handled = true;
        if (!Guarded) Model.Decide(row, false);
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_owner != null) _owner.KeyDown -= OnEscape;
        _owner = null;
        CloseConfirmation();
        if (_row != null) { _row.CountdownThreshold -= OnCountdownThreshold; _row.PropertyChanged -= OnRowChanged; _row = null; }
    }
    private void OnPlan(object sender, RoutedEventArgs e)
    {
        var row = (ApprovalRow)((Button)sender).DataContext;
        row.PlanOpen = !row.PlanOpen;
    }
}
