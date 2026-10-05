using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Threading;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.Services.Stubs;

namespace TiaOpenness.Gui.ViewModels;

public enum FeatureTone { Normal, Muted, Accent, Warning, Success, Unchecked }

public sealed class FeaturePagesViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly INotifyPropertyChanged[] _sources;
    private readonly DispatcherTimer _timer;
    private readonly Func<DateTimeOffset> _now;
    private IReadOnlyList<CallRecord>? _pausedCalls;
    private bool _writeOnly, _failOnly, _releaseOnly, _disposed;
    private string _search = "", _release = "21";
    private string? _selectedCall;
    private AuditVerification? _verification;
    private long? _auditSelection;
    private ApprovalRow[] _requests = [];
    private EnvironmentGroupRow[] _groups = [];

    public FeaturePagesViewModel(IApprovalService approvals, ICallJournalService journal, IAuditLogService audit,
        IEnvironmentCheckService environment, IDiagnosticBundleService diagnostics, Func<DateTimeOffset>? now = null)
    {
        Approvals = approvals; Journal = journal; Audit = audit; Environment = environment; Diagnostics = diagnostics;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _sources = [approvals, journal, audit, environment, diagnostics];
        foreach (var source in _sources) source.PropertyChanged += OnChanged;
        Loc.Current.LanguageChanged += OnLanguageChanged;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnTick, _dispatcher);
        _groups = Environment.Groups.Select(g => new EnvironmentGroupRow(g)).ToArray();
        Refresh();
    }

    public IApprovalService Approvals { get; }
    public ICallJournalService Journal { get; }
    public IAuditLogService Audit { get; }
    public IEnvironmentCheckService Environment { get; }
    public IDiagnosticBundleService Diagnostics { get; }
    public event EventHandler<string>? DrawerRequested;
    public event EventHandler<LocalizedText>? Feedback;

    public bool WriteOnly { get => _writeOnly; set { if (Set(ref _writeOnly, value)) Raise(nameof(Calls)); Raise(nameof(CallsEmpty)); } }
    public bool FailOnly { get => _failOnly; set { if (Set(ref _failOnly, value)) Raise(nameof(Calls)); Raise(nameof(CallsEmpty)); } }
    public bool ReleaseOnly { get => _releaseOnly; set { if (Set(ref _releaseOnly, value)) Raise(nameof(Calls)); Raise(nameof(CallsEmpty)); } }
    public string Search { get => _search; set { if (Set(ref _search, value)) Raise(nameof(Calls)); Raise(nameof(CallsEmpty)); } }
    public string Release { get => _release; set { if (Set(ref _release, value)) Raise(null); } }
    public string ReleaseFilter => Loc.Current.T("Calls.ReleaseOnly", TiaMcp.Versioning.TiaVersionCatalog.Get(Release).DisplayName);
    public bool FollowLatest => _pausedCalls == null;
    public string FollowLabel => Loc.Current[FollowLatest ? "Calls.Follow" : "Calls.Paused"];
    private IReadOnlyList<CallRecord> Snapshot => _pausedCalls ?? Journal.Calls;
    public IReadOnlyList<CallRow> Calls => Snapshot
        .Where(c => !WriteOnly || c.IsWrite)
        .Where(c => !FailOnly || c.Result is CallResult.Failed or CallResult.Partial or CallResult.Unknown)
        .Where(c => !ReleaseOnly || c.Release == Release)
        .Where(c => c.Tool.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase))
        .OrderBy(c => c.Result == CallResult.Pending ? 0 : 1).ThenByDescending(c => c.Time)
        .Select(c => new CallRow(c)).ToArray();
    public bool CallsEmpty => Calls.Count == 0;
    public string CallsCount => Loc.Current.T("Calls.Count", Snapshot.Count, Approvals.PendingCount);
    public string CallEmptyTitle => Loc.Current[Snapshot.Count == 0 ? "Calls.EmptyTitle" : "Calls.NoMatches"];
    public CallRow? SelectedCall => Snapshot.FirstOrDefault(c => c.RequestId == _selectedCall) is { } call ? new(call) : null;
    public string Address => string.IsNullOrEmpty(Journal.Connection.Address) ? Loc.Current["Feature.NotConnected"] : Journal.Connection.Address;
    public string Transport => string.IsNullOrEmpty(Journal.Connection.Transport) ? "—" : Journal.Connection.Transport;
    public string ConnectionJson => FeatureJson.Redact(Journal.Connection.ConfigurationJson);
    public string ApprovalSetting => Approvals.Enabled ? Loc.Current.T("Calls.ApprovalOn", Approvals.TimeoutSeconds) : Loc.Current["Shell.ApprovalOff"];
    public IReadOnlyList<ApprovalRow> Requests => _requests;
    public bool RequestsEmpty => Requests.Count == 0;
    public string PendingCount => Loc.Current.T("Feature.Count", Approvals.PendingCount);
    public string PendingSummary => Approvals.Requests.FirstOrDefault(r => r.State == ApprovalState.Pending) is { } request
        ? Loc.Current.T("Calls.PendingSummary", request.Client, request.Operations.Count, Remaining(request)) : Loc.Current["Shell.NoPending"];
    public int Remaining(ApprovalRequest request) => Math.Clamp((int)Math.Ceiling((request.Deadline - _now()).TotalSeconds), 0, request.TimeoutSeconds);
    public void ToggleFollow() { _pausedCalls = FollowLatest ? Journal.Calls.ToArray() : null; Raise(null); }
    public void OpenApprovals() => DrawerRequested?.Invoke(this, "Approvals");
    public void OpenCall(CallRow row) { _selectedCall = row.Record.RequestId; Raise(nameof(SelectedCall)); DrawerRequested?.Invoke(this, "CallDetail"); }
    public void Decide(ApprovalRow row, bool approve) => Run(() =>
    {
        bool accepted = approve ? Approvals.Approve(row.Request.Id) : Approvals.Deny(row.Request.Id);
        return accepted ? LocalizedText.Empty : LocalizedText.Key("Approval.NoLongerPending");
    });

    public IReadOnlyList<AuditRow> AuditRows => Audit.Events.Select(e => new AuditRow(e, e.Index == _auditSelection)).ToArray();
    public bool AuditEmpty => Audit.Events.Count == 0;
    public string AuditCount => Loc.Current.T("Feature.Count", Audit.TotalCount.ToString("N0"));
    public bool HasVerification => _verification != null;
    public bool VerificationBroken => _verification?.Passed == false;
    public FeatureTone VerificationTone => _verification?.Passed switch { true => FeatureTone.Success, false => FeatureTone.Warning, _ => FeatureTone.Muted };
    public string VerificationText => _verification?.Passed switch
    {
        true => Loc.Current.T("Audit.Pass", _verification.Count.ToString("N0")),
        false => Loc.Current.T("Audit.Break", _verification.BreakIndex),
        _ => Loc.Current["Feature.NotConnected"],
    };
    public long? BreakIndex => _verification?.BreakIndex;
    public string Coverage => Audit.Coverage.Resolve();
    public void Verify() => Run(() => { _verification = Audit.Verify(); _auditSelection = null; Raise(null); return LocalizedText.Empty; });
    public AuditRow? JumpToBreak()
    {
        _auditSelection = BreakIndex;
        Raise(nameof(AuditRows));
        return AuditRows.FirstOrDefault(r => r.Record.Index == BreakIndex);
    }
    public void SetRetention(int value, bool size) => Run(() => { if (size) Audit.FileSizeMb = value; else Audit.Copies = value; return LocalizedText.Empty; });

    public IReadOnlyList<EnvironmentGroupRow> EnvironmentGroups => _groups;
    public string EnvironmentSummary => CheckSummary(Environment.Groups.SelectMany(g => g.Rows).ToArray());
    public FeatureTone EnvironmentTone => CheckTone(Environment.Groups.SelectMany(g => g.Rows).ToArray());
    public string LogTail => string.Join("\n", Environment.LogTail);
    public bool DiagnosticCanStart => !DiagnosticRunning;
    public bool DiagnosticIdle => Diagnostics.Progress.State == DiagnosticState.Idle;
    public bool DiagnosticRunning => Diagnostics.Progress.State == DiagnosticState.Running;
    public bool DiagnosticDone => Diagnostics.Progress.State == DiagnosticState.Done;
    public string DiagnosticLabel => Loc.Current[DiagnosticDone ? "Approval.Completed" : DiagnosticRunning ? "Env.Checking" : "Env.Unchecked"];
    public FeatureTone DiagnosticTone => DiagnosticDone ? FeatureTone.Success : DiagnosticRunning ? FeatureTone.Accent : FeatureTone.Muted;
    public string DiagnosticStep => Diagnostics.Progress.Step.Resolve();
    public int DiagnosticPercent => Math.Clamp(Diagnostics.Progress.Percent, 0, 100);
    public string DiagnosticPath => Diagnostics.Progress.Path;
    internal Action<string> CopyText { get; set; } = System.Windows.Clipboard.SetText;
    public void Copy(string text) => Run(() => { CopyText(text); return LocalizedText.Empty; });
    public void Export() => Run(() => LocalizedText.Key(Diagnostics.Export()));
    public void Run(Func<LocalizedText> action)
    {
        try { var result = action(); if (result.Resolve().Length > 0) Feedback?.Invoke(this, result); }
        catch (Exception ex) { Feedback?.Invoke(this, LocalizedText.Literal(ex.Message)); }
    }
    internal static FeatureTone CheckTone(IReadOnlyList<EnvironmentCheck> rows) => rows.Any(r => r.Status == CheckStatus.Checking) ? FeatureTone.Accent
        : rows.Any(r => r.Status is CheckStatus.Warn or CheckStatus.Fail) ? FeatureTone.Warning
        : rows.Count == 0 || rows.Any(r => r.Status == CheckStatus.Unchecked) ? FeatureTone.Unchecked : FeatureTone.Success;
    internal static string CheckSummary(IReadOnlyList<EnvironmentCheck> rows) => CheckTone(rows) switch
    {
        FeatureTone.Accent => Loc.Current["Env.Checking"],
        FeatureTone.Warning => Loc.Current.T("Env.NeedsAction", rows.Count(r => r.Status is CheckStatus.Warn or CheckStatus.Fail)),
        FeatureTone.Success => Loc.Current["Env.AllPass"],
        _ => Loc.Current["Env.Unchecked"],
    };
    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Dispatch(() =>
    {
        if (ReferenceEquals(sender, Environment)) _groups = Environment.Groups.Select(g => new EnvironmentGroupRow(g)).ToArray();
        if (ReferenceEquals(sender, Audit) && e.PropertyName is nameof(IAuditLogService.Events) or nameof(IAuditLogService.TotalCount)) _verification = null;
        Refresh();
    });
    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatch(() => { _groups = Environment.Groups.Select(g => new EnvironmentGroupRow(g)).ToArray(); Refresh(); });
    private void Dispatch(Action action)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(() => { if (!_disposed) action(); });
    }
    private void Refresh()
    {
        var previous = _requests.ToDictionary(r => r.Request.Id);
        _requests = Approvals.Requests.OrderBy(r => r.State == ApprovalState.Pending ? 0 : 1)
            .Select(r => new ApprovalRow(r, this) { JsonOpen = previous.TryGetValue(r.Id, out var old) && old.JsonOpen }).ToArray();
        Raise(null);
    }
    private void OnTick(object? sender, EventArgs e)
    {
        Approvals.Refresh();
        foreach (var row in _requests) row.Refresh();
        Raise(nameof(PendingSummary));
    }
    public void Dispose()
    {
        _disposed = true; _timer.Stop(); _timer.Tick -= OnTick;
        foreach (var source in _sources) source.PropertyChanged -= OnChanged;
        Loc.Current.LanguageChanged -= OnLanguageChanged;
    }
}

public sealed record CallRow(CallRecord Record)
{
    public string Time => Record.Time.ToLocalTime().ToString("HH:mm:ss");
    public string Host => Record.Host;
    public string Tool => Record.Tool;
    public string ReadWrite => Loc.Current[Record.IsWrite ? "Calls.Write" : "Calls.Read"];
    public FeatureTone WriteTone => Record.IsWrite ? FeatureTone.Warning : FeatureTone.Muted;
    public bool Pending => Record.Result == CallResult.Pending;
    public FeatureTone Tone => Record.Result switch
    {
        CallResult.Success => FeatureTone.Success, CallResult.Rejected => FeatureTone.Muted,
        CallResult.Approved or CallResult.Executing => FeatureTone.Accent, _ => FeatureTone.Warning,
    };
    public string Result => Loc.Current[Record.Result switch
    {
        CallResult.Success => "Calls.Success", CallResult.Rejected => "Calls.Rejected", CallResult.Pending => "Calls.Pending",
        CallResult.Failed => "Approval.Failed", CallResult.Partial => "Approval.Partial", CallResult.Unknown => "Approval.Unknown",
        CallResult.Approved => "Approval.Approved", _ => "Approval.Executing",
    }];
    public string Duration => Record.DurationMs is { } ms ? ms.ToString("N0") + " ms" : "—";
    public string Target => Record.Target;
    public string Parameters => FeatureJson.Redact(Record.ParametersJson);
    public string Summary => Record.Summary.Resolve();
    public string Error => Record.ErrorCode + (Record.ErrorMessage.Resolve().Length > 0 ? "\n" + Record.ErrorMessage.Resolve() : "");
    public bool HasError => Error.Length > 0;
    public string Approval => Record.ApprovalRecord.Resolve();
}

public sealed class ApprovalRow(ApprovalRequest request, FeaturePagesViewModel owner) : ObservableObject
{
    private bool _jsonOpen;
    public ApprovalRequest Request { get; } = request;
    public bool Pending => Request.State == ApprovalState.Pending;
    public bool CanDecide => Pending && owner.Approvals.CanDecide(Request.Id);
    public int Remaining => owner.Remaining(Request);
    public double Percent => 100.0 * Remaining / Request.TimeoutSeconds;
    public FeatureTone CountdownTone => Remaining < 20 ? FeatureTone.Warning : FeatureTone.Accent;
    public bool JsonOpen { get => _jsonOpen; set => Set(ref _jsonOpen, value); }
    public string Parameters => FeatureJson.Redact(Request.ParametersJson);
    public string Metadata => "#" + Request.PlanHash + " · " + Request.Id;
    public FeatureTone Tone => Request.State switch
    {
        ApprovalState.Approved or ApprovalState.Executing => FeatureTone.Accent,
        ApprovalState.Completed => FeatureTone.Success,
        ApprovalState.Rejected or ApprovalState.TimedOut or ApprovalState.Disconnected => FeatureTone.Muted,
        _ => FeatureTone.Warning,
    };
    public string State => Loc.Current[Request.State switch
    {
        ApprovalState.Pending => "Approval.Pending", ApprovalState.Approved => "Approval.Approved",
        ApprovalState.Executing => "Approval.Executing", ApprovalState.Completed => "Approval.Completed",
        ApprovalState.Failed => "Approval.Failed", ApprovalState.Partial => "Approval.Partial", ApprovalState.Unknown => "Approval.Unknown",
        ApprovalState.Rejected => "Approval.Rejected", ApprovalState.TimedOut => "Approval.TimedOut", _ => "Approval.Disconnected",
    }];
    public string Note => Request.State switch
    {
        ApprovalState.Rejected or ApprovalState.TimedOut or ApprovalState.Disconnected => Loc.Current["Approval.NoneExecuted"],
        ApprovalState.Unknown or ApprovalState.Failed or ApprovalState.Partial => Loc.Current["Approval.VerifyProject"],
        _ => "",
    };
    public bool HasNote => Note.Length > 0;
    public void Refresh() => Raise(null);
}

public sealed record AuditRow(AuditEvent Record, bool Selected)
{
    public string Time => Record.Time.ToLocalTime().ToString("HH:mm:ss");
    public string Type => Loc.Current[Record.Type switch
    {
        AuditEventType.Request => "Audit.Request", AuditEventType.Approve => "Audit.Approve", AuditEventType.Reject => "Audit.Reject",
        AuditEventType.Timeout => "Audit.Timeout", AuditEventType.Start => "Audit.Start", AuditEventType.End => "Audit.End", _ => "Audit.Toggle",
    }];
    public FeatureTone Tone => Record.Type switch
    {
        AuditEventType.Request => FeatureTone.Warning, AuditEventType.Approve => FeatureTone.Success,
        AuditEventType.Reject or AuditEventType.Timeout => FeatureTone.Muted, _ => FeatureTone.Normal,
    };
}

public sealed record EnvironmentGroupRow(EnvironmentGroup Group)
{
    public string Name => Group.Name.Resolve();
    public string Summary => FeaturePagesViewModel.CheckSummary(Group.Rows);
    public IReadOnlyList<EnvironmentRow> Rows => Group.Rows.Select(r => new EnvironmentRow(r)).ToArray();
}

public sealed record EnvironmentRow(EnvironmentCheck Check)
{
    public string Name => Check.Name.Resolve();
    public string Result => Check.Result.Resolve();
    public string Fix => Check.FixAction.Resolve();
    public string Detail => Check.Detail.Resolve();
    public bool HasFix => Fix.Length > 0 && Check.Status != CheckStatus.Checking;
    public FeatureTone ResultTone => Warning ? FeatureTone.Warning : FeatureTone.Muted;
    public bool Warning => Check.Status is CheckStatus.Warn or CheckStatus.Fail;
    public FeatureTone Tone => Check.Status switch
    {
        CheckStatus.Pass => FeatureTone.Success, CheckStatus.Checking => FeatureTone.Accent,
        CheckStatus.Unchecked => FeatureTone.Unchecked, _ => FeatureTone.Warning,
    };
}
