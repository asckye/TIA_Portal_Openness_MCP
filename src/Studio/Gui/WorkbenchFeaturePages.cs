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
    private bool _featuresDisposed;

    private void InitializeFeaturePages()
    {
        ConfigureFeaturePages(new CallJournalService(_model.SelectedReleaseKey), new AuditLogService(),
                new EnvironmentCheckService(!_model.OfflineDemo, () => _model.SelectedReleaseKey));
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
            Features.CallCompleted -= OnCallCompleted;
            Features.Dispose();
            (Features.Journal as IDisposable)?.Dispose();
            (Features.Audit as IDisposable)?.Dispose();
        }
        if (notification != null) { _approvalNotification.Dispose(); _approvalNotification = notification; }
        Features = new FeaturePagesViewModel(Approvals, journal, audit, environment, _diagnostics, now) { Release = _model.SelectedReleaseKey };
        DrawerHeading.DataContext = CallsContent.DataContext = AuditContent.DataContext = EnvironmentContent.DataContext = ApprovalsContent.DataContext =
            CallDetailContent.DataContext = Features;
        OperationsContent.SetFeatures(Features, () => Navigate("Environment"));
        LogContent.SetFeatures(Features);
        SettingsContent.SetFeatures(Features);
        Features.DrawerRequested += OnFeatureDrawer;
        Features.Feedback += OnFeatureFeedback;
        Features.CallCompleted += OnCallCompleted;
        Features.SetVisibility(_page == "Calls", _page == "Audit", _page == "Approvals", _drawer == "CallDetail");
        if (ControlSurface != null) { HookControlFeatures(); QueueControlSnapshot(); }
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
    private void OnFeatureFeedback(object? sender, FeatureFeedback feedback)
        => ShowToast(feedback.Kind, feedback.Message, feedback.Source);

    private void OnCallCompleted(CallRecord call)
    {
        var row = new CallRow(call);
        var kind = call.Result switch
        {
            CallResult.Success => ToastKind.Success,
            CallResult.Rejected => ToastKind.Warning,
            _ => ToastKind.Error
        };
        var text = LocalizedText.Literal(call.Tool + " · " + row.Result + (row.HasError ? " · " + row.Error : ""));
        ShowToast(kind, text, "ai-calls", () =>
        {
            Navigate("Calls");
            if (!Features.OpenCompletedCall(call)) { Navigate("Log"); LogContent.ShowMessage(text.Resolve()); }
        }, actionShowsDetails: true);
    }

    private void OnNewApproval(object? sender, ApprovalRequest request)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnNewApproval(sender, request)); return; }
        if (_featuresDisposed) return;
        var title = LocalizedText.Key("Approval.ToastTitle", request.Operations.Count);
        var help = LocalizedText.Key("Approval.ToastHelp");
        if (request.Actor == "workbench" && request.OperatorCallId != null && _model.Panel?.Attached == true)
        { OpenDrawer("Approvals"); ApprovalsContent.FocusRequest(request.Id); }
        ShowToast(ToastKind.Approval, help, "approval", () => { Navigate("Approvals"); ApprovalsContent.FocusRequest(request.Id); }, title);
        if (WindowState == WindowState.Minimized)
        {
            try {
                _approvalNotification
                    .Show(title.Resolve(), help.Resolve(), () => Dispatcher
                        .Invoke(() => {
                            if (_featuresDisposed) return; WindowState = WindowState.Normal; Activate(); OpenDrawer("Approvals"); ApprovalsContent
                                .FocusRequest(request.Id);
                        }));
            }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Approval notification failed: {0}", ex.Message); }
        }
    }
    private void DisposeFeaturePages()
    {
        _featuresDisposed = true;
        Approvals.NewRequest -= OnNewApproval;
        _model.PropertyChanged -= OnFeatureReleaseChanged;
        Features.DrawerRequested -= OnFeatureDrawer;
        Features.Feedback -= OnFeatureFeedback;
        Features.CallCompleted -= OnCallCompleted;
        DrawerHeading.DataContext = CallsContent.DataContext = AuditContent.DataContext = EnvironmentContent.DataContext = ApprovalsContent.DataContext =
            CallDetailContent.DataContext = null;
        OperationsContent.SetFeatures(null, null);
        LogContent.SetFeatures(null);
        SettingsContent.SetFeatures(null);
        Features.Dispose(); (Features.Journal as IDisposable)?.Dispose(); (Features.Audit as IDisposable)?.Dispose(); _approvalNotification.Dispose();
    }
}
