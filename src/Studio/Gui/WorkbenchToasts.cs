using System;
using System.Linq;
using System.Collections.Generic;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui;

public partial class MainWindow
{
    private readonly ToastQueue _toasts;
    private readonly DispatcherTimer _toastTimer = new();
    private readonly HashSet<ToastNotice> _announced = new(ReferenceEqualityComparer.Instance);
    private IInputElement? _beforeToastFocus;
    private ToastCard[] _toastCards = [];
    private ToastCard NewestCard => _toastCards[^1];
    internal Border Toast => NewestCard.Surface;
    internal Ellipse ToastDot => NewestCard.Dot;
    internal TextBlock ToastTitle => NewestCard.Title;
    internal TextBlock ToastMessage => NewestCard.Message;
    internal Button ToastClose => NewestCard.Close;
    internal Button ToastSecondAction => NewestCard.SecondAction;
    internal Button ToastAction => NewestCard.Action;
    internal bool ToastHostClosed { get; private set; }

    private Grid[] ToastSheets() => [DrawerOverlay, OverlayHost, ApprovalsContent.ConfirmationSheet, .. ControlOptionsPanels()];

    private void InitializeToast()
    {
        var template = (DataTemplate)FindResource("Primer.Workbench.ToastCard");
        _toastCards = [new(template), new(template), new(template)];
        foreach (var card in _toastCards)
        {
            ToastStack.Children.Add(card.Host);
            card.Close.Click += (_, _) => CloseToast(card);
            card.Action.Click += (_, _) => InvokeToastAction(card);
            card.SecondAction.Click += (_, _) => InvokeToastAction(card, second: true);
        }
        RegisterName(nameof(Toast), Toast);
        foreach (var part in new FrameworkElement[] { ToastDot, ToastTitle, ToastMessage, ToastClose, ToastAction }) RegisterName(part.Name, part);
        _toasts.Changed += RenderToast;
        _toasts.ErrorDropped += AnnounceDroppedError;
        ToastStack.PreviewGotKeyboardFocus += RememberToastFocus;
        _toastTimer.Tick += OnToastTick;
        foreach (var sheet in ToastSheets()) OptionsVisibility.AddValueChanged(sheet, OnToastPresentationChanged);
        IsEnabledChanged += OnToastEnabledChanged;
        ToastStack.IsKeyboardFocusWithinChanged += OnToastEnabledChanged;
        ToastStack.MouseEnter += OnToastHover;
        ToastStack.MouseLeave += OnToastHover;
        _model.Activity.Feedback += OnFeatureFeedback;
    }

    private void DisposeToast()
    {
        ToastHostClosed = true;
        _toastTimer.Stop();
        _toastTimer.Tick -= OnToastTick;
        _toasts.Changed -= RenderToast;
        _toasts.ErrorDropped -= AnnounceDroppedError;
        ToastStack.PreviewGotKeyboardFocus -= RememberToastFocus;
        foreach (var sheet in ToastSheets()) OptionsVisibility.RemoveValueChanged(sheet, OnToastPresentationChanged);
        IsEnabledChanged -= OnToastEnabledChanged;
        ToastStack.IsKeyboardFocusWithinChanged -= OnToastEnabledChanged;
        ToastStack.MouseEnter -= OnToastHover;
        ToastStack.MouseLeave -= OnToastHover;
        _model.Activity.Feedback -= OnFeatureFeedback;
    }

    internal void ShowToast(ToastKind kind, LocalizedText text, string source, Action? action = null,
        LocalizedText title = default, string actionKey = "Common.Open", bool actionShowsDetails = false, bool sticky = false,
        Action? secondAction = null, LocalizedText secondActionKey = default, LocalizedText actionLabel = default, Action? onDismiss = null)
        => ShowToast(new ToastNotice(source, kind, text, title, action, actionKey, actionShowsDetails,
            sticky, secondAction, secondActionKey, actionLabel, onDismiss));

    private void ShowToast(ToastNotice notice)
    {
        if (ToastHostClosed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => ShowToast(notice)); return; }
        UpdateToastPresentation();
        _toasts.Show(notice);
    }

    private void UpdateToastPresentation() =>
        _toasts.SetPresentation(ToastBlockingSheetOpen || !IsEnabled || OwnedWindows.OfType<Window>().Any(w => w.IsVisible),
            ToastStack.IsMouseOver || ToastStack.IsKeyboardFocusWithin);

    private void OnToastPresentationChanged(object? sender, EventArgs e) => UpdateToastPresentation();
    private void OnToastEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateToastPresentation();
    private void OnToastHover(object sender, MouseEventArgs e) => UpdateToastPresentation();
    private void OnToastTick(object? sender, EventArgs e) => TickToast();
    internal void TickToast() => _toasts.Tick();

    private void AnnounceDroppedError(ToastNotice notice)
    {
        DroppedErrorAnnouncement.Text = notice.FullText;
        AutomationProperties.SetName(DroppedErrorAnnouncement, notice.FullText);
        AccessibleStatus.Announce(DroppedErrorAnnouncement);
    }

    private void RememberToastFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.OldFocus is DependencyObject previous && !ToastStack.IsAncestorOf(previous)) _beforeToastFocus = e.OldFocus;
    }

    private void CloseToast(ToastCard card)
    {
        if (card.Notice is not { } notice) return;
        bool restoreFocus = card.Host.IsKeyboardFocusWithin;
        var previous = _beforeToastFocus;
        _toasts.Dismiss(notice);
        if (!restoreFocus) return;
        if (_toasts.Visible != null) NewestCard.Close.Focus();
        else previous?.Focus();
    }

    private void RenderToast()
    {
        _toastTimer.Stop();
        var cards = _toasts.Cards.ToArray();
        int offset = _toastCards.Length - cards.Length;
        for (int i = 0; i < _toastCards.Length; i++) _toastCards[i].Display(i < offset ? null : cards[i - offset], ToastStack.Width);
        var crashLogs = _toastCards.FirstOrDefault(card => card.Notice?.Source == "session-lost")?.SecondAction;
        if (OperationsContent.FindName("CrashLogs") != crashLogs)
        {
            if (OperationsContent.FindName("CrashLogs") != null) OperationsContent.UnregisterName("CrashLogs");
            if (crashLogs != null) OperationsContent.RegisterName("CrashLogs", crashLogs);
        }
        if (!_toasts.HasSource("env-alert")) _environmentIssue = null;
        _announced.RemoveWhere(notice => !_toasts.Contains(notice));
        foreach (var card in _toastCards)
            if (card.Notice is { } notice && _announced.Add(notice)) AccessibleStatus.Announce(card.Message);
        if (_toasts.Delay is { } delay)
        {
            _toastTimer.Interval = delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
            _toastTimer.Start();
        }
    }

    private void OnCanInvokeToastAction(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = _toasts.Visible != null && ToastAction.Visibility == Visibility.Visible;

    private void OnToastClick(object sender, RoutedEventArgs e)
    {
        InvokeToastAction(NewestCard);
        e.Handled = true;
    }

    private void InvokeToastAction(ToastCard card, bool second = false)
    {
        if (card.Notice is not { } notice) return;
        bool details = !second && card.ShowDetails;
        // Environment actions keep the finding visible until a check resolves it or the user dismisses it.
        if (notice.OnDismiss == null) _toasts.Remove(notice.Source);
        if (details) { Navigate("Log"); LogContent.ShowMessage(notice.FullText); }
        else (second ? notice.SecondAction ?? notice.Action : notice.Action)?.Invoke();
    }
}
