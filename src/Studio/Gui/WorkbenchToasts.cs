using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui;

// Measure the untrimmed line before WPF rounds a trimmed TextBlock down to the last whole glyph.
internal sealed class ToastPanel : DockPanel
{
    protected override Size MeasureOverride(Size available)
    {
        var natural = base.MeasureOverride(new Size(double.PositiveInfinity, available.Height));
        if (natural.Width <= available.Width) return natural;
        return new Size(available.Width, base.MeasureOverride(available).Height);
    }
}

public partial class MainWindow
{
    private readonly ToastQueue _toasts;
    private readonly DispatcherTimer _toastTimer = new();
    private ToastNotice? _announced;
    private bool _showToastDetails;
    internal bool ToastHostClosed { get; private set; }

    private Grid[] ToastSheets() => [DrawerOverlay, OverlayHost, ApprovalsContent.ConfirmationSheet, .. ControlOptionsPanels()];

    private void InitializeToast()
    {
        _toasts.Changed += RenderToast;
        Root.SizeChanged += OnToastSizeChanged;
        _toastTimer.Tick += OnToastTick;
        foreach (var sheet in ToastSheets()) OptionsVisibility.AddValueChanged(sheet, OnToastPresentationChanged);
        IsEnabledChanged += OnToastEnabledChanged;
        Toast.MouseEnter += OnToastHover;
        Toast.MouseLeave += OnToastHover;
        _model.Activity.Feedback += OnFeatureFeedback;
    }

    private void DisposeToast()
    {
        ToastHostClosed = true;
        _toastTimer.Stop();
        _toastTimer.Tick -= OnToastTick;
        _toasts.Changed -= RenderToast;
        Root.SizeChanged -= OnToastSizeChanged;
        foreach (var sheet in ToastSheets()) OptionsVisibility.RemoveValueChanged(sheet, OnToastPresentationChanged);
        IsEnabledChanged -= OnToastEnabledChanged;
        Toast.MouseEnter -= OnToastHover;
        Toast.MouseLeave -= OnToastHover;
        _model.Activity.Feedback -= OnFeatureFeedback;
    }

    internal void ShowToast(ToastKind kind, LocalizedText text, string source, Action? action = null,
        LocalizedText title = default, string actionKey = "Common.Open", bool actionShowsDetails = false)
    {
        if (ToastHostClosed) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowToast(kind, text, source, action, title, actionKey, actionShowsDetails));
            return;
        }
        UpdateToastPresentation();
        _toasts.Show(new(source, kind, text, title, action, actionKey, actionShowsDetails));
    }

    private void UpdateToastPresentation() =>
        _toasts.SetPresentation(ShellModalOpen || !IsEnabled || OwnedWindows.OfType<Window>().Any(w => w.IsVisible), Toast.IsMouseOver);

    private void OnToastPresentationChanged(object? sender, EventArgs e) => UpdateToastPresentation();
    private void OnToastEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateToastPresentation();
    private void OnToastHover(object sender, MouseEventArgs e) => UpdateToastPresentation();
    private void OnToastTick(object? sender, EventArgs e) => TickToast();
    internal void TickToast() => _toasts.Tick();

    private void OnToastSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && _toasts.Visible is { } notice) UpdateToastAction(notice);
    }

    private void UpdateToastAction(ToastNotice notice)
    {
        // Measure the full single line with its original action before deciding whether it needs a Details link.
        ToastAction.Visibility = notice.Action != null ? Visibility.Visible : Visibility.Collapsed;
        ToastAction.Content = Loc.Current[notice.ActionKey];
        ToastLine.InvalidateMeasure();
        ToastLine.Measure(new Size(double.PositiveInfinity, Toast.Height));
        double width = Math.Min(Toast.MaxWidth, Root.ActualWidth > 0 ? Root.ActualWidth - Toast.Margin.Left - Toast.Margin.Right : Toast.MaxWidth);
        double inset = Toast.Padding.Left + Toast.Padding.Right + Toast.BorderThickness.Left + Toast.BorderThickness.Right;
        string full = notice.FullText;
        bool details = notice.Kind == ToastKind.Error || full != full.ReplaceLineEndings(" ") || ToastLine.DesiredSize.Width + inset > width;
        _showToastDetails = details && !notice.ActionShowsDetails;
        bool showAction = details || notice.Action != null;
        ToastAction.Content = details ? Loc.Current["Toast.Details"] : Loc.Current[notice.ActionKey];
        ToastAction.Visibility = showAction ? Visibility.Visible : Visibility.Collapsed;
        string help = showAction ? Loc.Current.T("Toast.ActionShortcut", ToastAction.Content) : "";
        AutomationProperties.SetHelpText(Toast, help);
        AutomationProperties.SetHelpText(ToastMessage, help);
    }

    private void RenderToast()
    {
        _toastTimer.Stop();
        if (_toasts.Visible is not { } notice)
        {
            _announced = null;
            Toast.Visibility = Visibility.Collapsed;
            return;
        }
        string title = notice.Title.Resolve(), message = notice.Text.Resolve();
        string full = notice.FullText;
        ToastTitle.Text = title.ReplaceLineEndings(" ");
        ToastTitle.Visibility = title.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ToastMessage.Text = message.ReplaceLineEndings(" ");
        Toast.ToolTip = full;
        AutomationProperties.SetName(Toast, full);
        AutomationProperties.SetName(ToastMessage, full);
        AutomationProperties.SetLiveSetting(ToastMessage, notice.Kind == ToastKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        ToastDot.SetResourceReference(Shape.FillProperty, notice.Kind switch
        {
            ToastKind.Success => "Primer.ok",
            ToastKind.Warning => "Primer.warn",
            ToastKind.Error => "Ant.colorError",
            _ => "Primer.accent"
        });
        Toast.Visibility = Visibility.Visible;
        UpdateToastAction(notice);
        if (!ReferenceEquals(notice, _announced))
        {
            _announced = notice;
            AccessibleStatus.Announce(ToastMessage);
        }
        if (_toasts.Delay is { } delay)
        {
            _toastTimer.Interval = delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
            _toastTimer.Start();
        }
    }

    private void OnDismissToast(object sender, RoutedEventArgs e) { _toasts.Dismiss(); e.Handled = true; }
    private void OnCanInvokeToastAction(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = _toasts.Visible != null && ToastAction.Visibility == Visibility.Visible;

    private void OnToastClick(object sender, RoutedEventArgs e)
    {
        if (_toasts.Visible is not { } notice || ToastAction.Visibility != Visibility.Visible) return;
        bool showDetails = _showToastDetails;
        _toasts.Dismiss();
        if (showDetails) { Navigate("Log"); LogContent.ShowMessage(notice.FullText); }
        else notice.Action?.Invoke();
        e.Handled = true;
    }
}
