using System;
using System.ComponentModel;
using System.Linq;
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
    internal ControlChannel.ControlInputGuard DecisionGuard { get; set; } = new();
    private long decisionVersion;
    private long? mouseVersion, releaseVersion, spaceVersion;

    public ApprovalsDrawer()
    {
        InitializeComponent(); DataContext = null;
        CommandBindings.Add(new CommandBinding(_openHistory, (_, _) => Model.HandledRequests = true));
        DataContextChanged += OnModelChanged;
        Unloaded += OnUnloaded;
        Loaded += OnLoaded;
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) DecisionChanged(); else CloseConfirmation(); };
        foreach (var button in new[] { ApproveRequest, DenyRequest, ConfirmDestructive })
        {
            button.PreviewMouseLeftButtonDown += (_, _) => mouseVersion = PressVersion;
            button.PreviewMouseLeftButtonUp += (_, _) => releaseVersion = mouseVersion;
            // Button releases capture before raising Click on a normal mouse-up.
            button.LostMouseCapture += (_, _) => mouseVersion = null;
            button.PreviewKeyDown += (_, e) => { if (e.Key == Key.Space && !e.IsRepeat) spaceVersion = PressVersion; };
            // The Button class handler raises Click before these release handlers clear the matching press.
            button.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler((_, e) =>
            { if (e.ChangedButton == MouseButton.Left) releaseVersion = mouseVersion = null; }), true);
            button.AddHandler(Keyboard.KeyUpEvent, new KeyEventHandler((_, e) =>
            { if (e.Key == Key.Space) spaceVersion = null; }), true);
            button.LostKeyboardFocus += (_, _) => spaceVersion = null;
        }
    }
    private readonly RoutedCommand _openHistory = new();
    internal RadioButton PendingTab => (RadioButton)AgentTabs.ContentTemplate.FindName(nameof(PendingTab), AgentTabs);
    internal RadioButton HistoryTab => (RadioButton)AgentTabs.ContentTemplate.FindName(nameof(HistoryTab), AgentTabs);
    private void OnHistoryTab(object sender, RoutedEventArgs e)
    {
        Model.HandledRequests = true;
        Model.HistoryDetailOpen = false;
    }
    private void OnHistorySizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (HistoryList.View is not GridView grid || grid.Columns.Count != 6) return;
        double flexible = Math.Max(0, e.NewSize.Width - 92 - 86 - 6 - SystemParameters.VerticalScrollBarWidth);
        grid.Columns[0].Width = 92;
        grid.Columns[1].Width = flexible * .32;
        grid.Columns[2].Width = flexible * .22;
        grid.Columns[3].Width = flexible * .22;
        grid.Columns[4].Width = 86;
        grid.Columns[5].Width = flexible * .24;
    }
    private void OnHistoryReview(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is ApprovalRow { Pending: false } row) FocusRequest(row.Request.Id);
    }
    private void OnHistoryAudit(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.DataContext is not ApprovalRow { AuditDecision: { } record }) return;
        WorkbenchCommands.Navigate.Execute(button.CommandParameter, button);
        if (Window.GetWindow(this) is not MainWindow { CurrentPage: "Audit" } window) return;
        Model.AuditSearch = "";
        if (Model.AuditRows.FirstOrDefault(row => row.Record == record) is not { } found) return;
        Model.SelectedAudit = found;
        window.AuditContent.DetailsToggle.IsChecked = true;
        window.AuditContent.AuditList.ScrollIntoView(found);
    }
    private FeaturePagesViewModel Model => (FeaturePagesViewModel)DataContext;
    private void DecisionChanged() { decisionVersion++; DecisionGuard.UiChanged(); }
    private long PressVersion => GuardedByTime ? -1 : decisionVersion;
    private bool Guarded => GuardedByTime || (releaseVersion ?? mouseVersion ?? decisionVersion) != decisionVersion
        || (spaceVersion ?? decisionVersion) != decisionVersion;
    private bool GuardedByTime => DecisionGuard.ClickGuardActive || (Window.GetWindow(this) as MainWindow)?.ControlGuard.ClickGuardActive == true;
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
        else if (!Model.HandledRequests && _row is { Pending: true })
        { e.Handled = true; if ((_row.Destructive || DenyRequest.IsKeyboardFocusWithin) && !Guarded) Model.Decide(_row, false); }
    }
    private void OnModelChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is FeaturePagesViewModel previous) previous.PropertyChanged -= OnModelPropertyChanged;
        if (e.NewValue is FeaturePagesViewModel model) model.PropertyChanged += OnModelPropertyChanged;
        EmptyRequests.ActionCommand = e.NewValue is FeaturePagesViewModel { HandledRequests: false } ? _openHistory : null;
        SelectRow();
    }
    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FeaturePagesViewModel.Requests) && IsVisible) DecisionChanged();
        if (e.PropertyName == nameof(FeaturePagesViewModel.SelectedRequest)) SelectRow();
        if (e.PropertyName == nameof(FeaturePagesViewModel.HandledRequests))
            EmptyRequests.ActionCommand = Model.HandledRequests ? null : _openHistory;
    }
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RequestList.SelectedItem is ApprovalRow row && DataContext is FeaturePagesViewModel model && row != model.SelectedRequest)
        {
            model.HandledRequests = !row.Pending;
            model.SelectedRequest = row;
            model.HistoryDetailOpen = !row.Pending;
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
        DecisionChanged();
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
        if (!request.Pending && !request.MatchesHistory(Model.HistorySearch.Trim())) Model.HistorySearch = "";
        Model.HandledRequests = !request.Pending;
        Model.SelectedRequest = request;
        Model.HistoryDetailOpen = !request.Pending;
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
