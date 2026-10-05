using System;
using System.ComponentModel;
using System.Windows;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui;

public partial class MainWindow
{
    internal FeaturePagesViewModel Features { get; private set; } = null!;
    private IApprovalNotification _approvalNotification = new WindowsApprovalNotification();
    private ApprovalRequest? _approvalToast;
    private LocalizedText _featureFeedback;
    private bool _featuresDisposed;

    private void InitializeFeaturePages()
    {
        ConfigureFeaturePages(new CallJournalService(_model.SelectedReleaseKey), new AuditLogService(), new EnvironmentCheckService());
        Approvals.NewRequest += OnNewApproval;
        _model.PropertyChanged += OnFeatureReleaseChanged;
    }

    internal void ConfigureFeaturePages(ICallJournalService journal, IAuditLogService audit, IEnvironmentCheckService environment,
        IApprovalNotification? notification = null, Func<DateTimeOffset>? now = null)
    {
        if (Features != null)
        {
            Features.DrawerRequested -= OnFeatureDrawer;
            Features.Feedback -= OnFeatureFeedback;
            Features.Dispose();
            (Features.Journal as IDisposable)?.Dispose();
            (Features.Audit as IDisposable)?.Dispose();
        }
        if (notification != null) { _approvalNotification.Dispose(); _approvalNotification = notification; }
        Features = new FeaturePagesViewModel(Approvals, journal, audit, environment, _diagnostics, now) { Release = _model.SelectedReleaseKey };
        DrawerHeading.DataContext = CallsContent.DataContext = AuditContent.DataContext = EnvironmentContent.DataContext = ApprovalsContent.DataContext = CallDetailContent.DataContext = Features;
        Features.DrawerRequested += OnFeatureDrawer;
        Features.Feedback += OnFeatureFeedback;
    }

    private void OnFeatureReleaseChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedReleaseKey))
        {
            Features.Release = _model.SelectedReleaseKey;
            if (Features.Journal is CallJournalService journal) journal.SetRelease(_model.SelectedReleaseKey);
        }
    }

    private void OnFeatureDrawer(object? sender, string drawer) => OpenDrawer(drawer);
    private void OnFeatureFeedback(object? sender, LocalizedText message)
    {
        _approvalToast = null; _featureFeedback = message;
        UpdateFeatureToast(); Toast.Visibility = Visibility.Visible;
    }
    private void OnNewApproval(object? sender, ApprovalRequest request)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnNewApproval(sender, request)); return; }
        if (_featuresDisposed) return;
        _approvalToast = request;
        UpdateFeatureToast(); Toast.Visibility = Visibility.Visible;
        if (WindowState == WindowState.Minimized)
        {
            try { _approvalNotification.Show(ToastTitle.Text, ToastMessage.Text, () => Dispatcher.Invoke(() => { if (_featuresDisposed) return; WindowState = WindowState.Normal; Activate(); OpenDrawer("Approvals"); })); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Approval notification failed: {0}", ex.Message); }
        }
    }
    private void UpdateFeatureToast()
    {
        ToastTitle.Text = _approvalToast == null ? Loc.Current["Shell.Diagnostics"] : Loc.Current.T("Approval.ToastTitle", _approvalToast.Operations.Count);
        ToastMessage.Text = _approvalToast == null ? _featureFeedback.Resolve() : Loc.Current["Approval.ToastHelp"];
    }
    private void DisposeFeaturePages()
    {
        _featuresDisposed = true;
        Approvals.NewRequest -= OnNewApproval;
        _model.PropertyChanged -= OnFeatureReleaseChanged;
        Features.DrawerRequested -= OnFeatureDrawer;
        Features.Feedback -= OnFeatureFeedback;
        DrawerHeading.DataContext = CallsContent.DataContext = AuditContent.DataContext = EnvironmentContent.DataContext = ApprovalsContent.DataContext = CallDetailContent.DataContext = null;
        Features.Dispose(); (Features.Journal as IDisposable)?.Dispose(); (Features.Audit as IDisposable)?.Dispose(); _approvalNotification.Dispose();
    }
}
