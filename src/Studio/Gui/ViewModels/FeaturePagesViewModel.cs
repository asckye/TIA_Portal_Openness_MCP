using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
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
    private bool _writeOnly, _failOnly, _releaseOnly, _humanOnly, _aiOnly, _disposed;
    private string _search = "", _release = "21";
    private string? _selectedCall;
    private AuditVerification? _verification;
    private IReadOnlyList<AuditEvent>? _verifiedEvents;
    private long? _auditSelection;
    private readonly ObservableCollection<ApprovalRow> _requests = [];
    private readonly ObservableCollection<CallRow> _calls = [];
    private readonly ObservableCollection<AuditRow> _auditRows = [];
    private readonly Dictionary<string, CallRow> _callCache = new(StringComparer.Ordinal);
    private readonly Dictionary<INotifyPropertyChanged, HashSet<string>> _dirty = [];
    private bool _queued, _callsVisible = true, _auditVisible = true, _drawerVisible = true, _detailVisible, _verifying;
    private Task? _verificationTask;
    private int _snapshotCount;
    private string _connectionJson = "{}", _connectionSource = "";
    public long RefreshCount { get; private set; }
    private EnvironmentGroupRow[] _groups = [];

    public FeaturePagesViewModel(IApprovalService approvals, ICallJournalService journal, IAuditLogService audit,
        IEnvironmentCheckService environment, IDiagnosticBundleService diagnostics, Func<DateTimeOffset>? now = null)
    {
        Approvals = approvals; Journal = journal; Audit = audit; Environment = environment; Diagnostics = diagnostics;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _sources = [approvals, journal, audit, environment, diagnostics];
        foreach (var source in _sources) source.PropertyChanged += OnChanged;
        Loc.Current.LanguageChanged += OnLanguageChanged;
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _groups = Environment.Groups.Select(g => new EnvironmentGroupRow(g)).ToArray();
        RefreshCalls(); RefreshRequests(); RefreshAudit(); RefreshConnection(); UpdateTimer();
    }

    public IApprovalService Approvals { get; }
    public ICallJournalService Journal { get; }
    public IAuditLogService Audit { get; }
    public IEnvironmentCheckService Environment { get; }
    public IDiagnosticBundleService Diagnostics { get; }
    public event EventHandler<string>? DrawerRequested;
    public event EventHandler<LocalizedText>? Feedback;

    public bool WriteOnly { get => _writeOnly; set { if (Set(ref _writeOnly, value)) RefreshCalls(); } }
    public bool FailOnly { get => _failOnly; set { if (Set(ref _failOnly, value)) RefreshCalls(); } }
    public bool ReleaseOnly { get => _releaseOnly; set { if (Set(ref _releaseOnly, value)) RefreshCalls(); } }
    public bool HumanOnly { get => _humanOnly; set { if (Set(ref _humanOnly, value)) { if (value) AiOnly = false; RefreshCalls(); } } }
    public bool AiOnly { get => _aiOnly; set { if (Set(ref _aiOnly, value)) { if (value) HumanOnly = false; RefreshCalls(); } } }
    public string Search { get => _search; set { if (Set(ref _search, value)) RefreshCalls(); } }
    public string Release { get => _release; set { if (Set(ref _release, value)) { RefreshCalls(); Raise(nameof(ReleaseFilter)); } } }
    public string ReleaseFilter => Loc.Current.T("Calls.ReleaseOnly", TiaMcp.Versioning.TiaVersionCatalog.Get(Release).DisplayName);
    public bool FollowLatest => _pausedCalls == null;
    public string FollowLabel => Loc.Current[FollowLatest ? "Calls.Follow" : "Calls.Paused"];
    public IReadOnlyList<CallRow> Calls => _calls;
    public bool CallsEmpty => _calls.Count == 0;
    public string CallsCount => Loc.Current.T("Calls.Count", _snapshotCount, Approvals.PendingCount);
    public string CallEmptyTitle => Loc.Current[_snapshotCount == 0 ? "Calls.EmptyTitle" : "Calls.NoMatches"];
    public CallRow? SelectedCall => _selectedCall != null && _callCache.TryGetValue(_selectedCall, out var row) ? row : null;
    public string Address => string.IsNullOrEmpty(Journal.Connection.Address) ? Loc.Current["Feature.NotConnected"] : Journal.Connection.Address;
    public string Transport => string.IsNullOrEmpty(Journal.Connection.Transport) ? "—" : Journal.Connection.Transport;
    public string ConnectionJson => _connectionJson;
    public string ApprovalSetting => Approvals.Enabled ? Loc.Current.T("Calls.ApprovalOn", Approvals.TimeoutSeconds) : Loc.Current["Shell.ApprovalOff"];
    public IReadOnlyList<ApprovalRow> Requests => _requests;
    public bool RequestsEmpty => Requests.Count == 0;
    public string PendingCount => Loc.Current.T("Feature.Count", Approvals.PendingCount);
    public string PendingSummary => Approvals.Requests.FirstOrDefault(r => r.State == ApprovalState.Pending) is { } request
        ? Loc.Current.T("Calls.PendingSummary", ActorDisplay.Client(request.Actor), request.Operations.Count, Remaining(request)) : Loc.Current["Shell.NoPending"];
    public int Remaining(ApprovalRequest request) => Math.Clamp((int)Math.Ceiling((request.Deadline - _now()).TotalSeconds), 0, request.TimeoutSeconds);
    public void ToggleFollow() { _pausedCalls = FollowLatest ? Journal.Calls.ToArray() : null; RefreshCalls(); Notify(nameof(FollowLatest), nameof(FollowLabel)); }
    public void OpenApprovals() => DrawerRequested?.Invoke(this, "Approvals");
    public void OpenCall(CallRow row) { _selectedCall = row.Record.JournalKey; Raise(nameof(SelectedCall)); DrawerRequested?.Invoke(this, "CallDetail"); }
    public void Decide(ApprovalRow row, bool approve) => Run(() =>
    {
        bool accepted = approve ? Approvals.Approve(row.Request.Id) : Approvals.Deny(row.Request.Id);
        return accepted ? LocalizedText.Empty : LocalizedText.Key("Approval.NoLongerPending");
    });

    public IReadOnlyList<AuditRow> AuditRows => _auditRows;
    public bool AuditEmpty => Audit.Events.Count == 0;
    public string AuditCount => Loc.Current.T("Feature.Count", Audit.TotalCount.ToString("N0"));
    public bool HasVerification => _verification != null;
    public bool VerificationBroken => _verification?.Passed == false;
    public FeatureTone VerificationTone => _verification?.Passed switch { true => FeatureTone.Success, false => FeatureTone.Warning, _ => FeatureTone.Muted };
    public string VerificationText => _verification?.Chains.Count > 0
        ? string.Join(" · ", _verification.Chains.Select(chain => (chain.Passed
            ? Loc.Current.T("Audit.Pass", chain.Count.ToString("N0")) : Loc.Current.T("Audit.Break", chain.BreakIndex))
            + " · " + chain.Chain + (chain.File == null ? "" : " · " + chain.File)))
        : _verification?.Passed switch
        {
            true => Loc.Current.T("Audit.Pass", _verification.Count.ToString("N0")),
            false => Loc.Current.T("Audit.Break", _verification.BreakIndex),
            _ => Loc.Current["Feature.NotConnected"],
        };
    public long? BreakIndex => _verification?.BreakIndex;
    public string Coverage => Audit.Coverage.Resolve();
    public int RetentionSize => Audit.FileSizeMb;
    public int RetentionCopies => Audit.Copies;
    public bool Verifying => _verifying;
    public int VerificationProgress { get; private set; }
    public void Verify() => _ = VerifyAsync();
    public Task VerifyAsync() => _verificationTask is { IsCompleted: false } ? _verificationTask : _verificationTask = VerifyCoreAsync();
    private async Task VerifyCoreAsync()
    {
        _verifying = true; VerificationProgress = 0; Notify(nameof(Verifying), nameof(VerificationProgress));
        try
        {
            _verification = await Task.Run(() => Audit is AuditLogService service
                ? service.Verify(percent => Dispatch(() => { VerificationProgress = percent; Raise(nameof(VerificationProgress)); })) : Audit.Verify());
            _verifiedEvents = Audit.Events;
            _auditSelection = null; RefreshAudit(); NotifyVerification();
        }
        catch (Exception ex) { Feedback?.Invoke(this, LocalizedText.Literal(ex.Message)); }
        finally { _verifying = false; Notify(nameof(Verifying)); }
    }
    public AuditRow? JumpToBreak()
    {
        _auditSelection = BreakIndex;
        RefreshAudit();
        return AuditRows.FirstOrDefault(r => r.Record.Index == BreakIndex);
    }
    public void SetRetention(int value, bool size) => _ = SetRetentionAsync(value, size);
    internal async Task SetRetentionAsync(int value, bool size)
    {
        try { await Task.Run(() => { if (size) Audit.FileSizeMb = value; else Audit.Copies = value; }); }
        catch (Exception ex) { Feedback?.Invoke(this, LocalizedText.Literal(ex.Message)); }
    }

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
    public void Export() => Run(() => { string message = Diagnostics.Export(); return message.Length == 0 ? LocalizedText.Empty : LocalizedText.Key(message); });
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
    public void SetVisibility(bool calls, bool audit, bool drawer, bool detail = false)
    {
        _callsVisible = calls; _auditVisible = audit; _drawerVisible = drawer; _detailVisible = detail;
        if (Journal is CallJournalService journal) journal.SetActive(calls || detail);
        if (Audit is AuditLogService log) log.SetActive(audit);
        if (calls || detail) { RefreshCalls(); RefreshConnection(); }
        if (audit) { RefreshAudit(); Notify(nameof(Coverage), nameof(RetentionSize), nameof(RetentionCopies)); }
        if (drawer) RefreshRequests();
        UpdateTimer();
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Dispatch(() =>
    {
        if (sender is INotifyPropertyChanged source)
        {
            if (!_dirty.TryGetValue(source, out var names)) _dirty[source] = names = [];
            names.Add(e.PropertyName ?? "");
        }
        if (_queued) return;
        _queued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Flush));
    });
    private void Flush()
    {
        _queued = false;
        if (_disposed) return;
        RefreshCount++;
        if (_dirty.ContainsKey(Approvals))
        {
            if (Changed(Approvals, nameof(IApprovalService.Requests)))
            {
                if (_drawerVisible) RefreshRequests();
                if (_callsVisible || _detailVisible) RefreshCalls();
                Notify(nameof(PendingSummary));
            }
            if (Changed(Approvals, nameof(IApprovalService.PendingCount))) Notify(nameof(PendingCount), nameof(CallsCount), nameof(PendingSummary));
            if (Changed(Approvals, nameof(IApprovalService.Enabled), nameof(IApprovalService.TimeoutSeconds))) Raise(nameof(ApprovalSetting));
            UpdateTimer();
        }
        if (_dirty.ContainsKey(Journal) && (_callsVisible || _detailVisible))
        {
            if (Changed(Journal, nameof(ICallJournalService.Calls))) RefreshCalls();
            if (Changed(Journal, nameof(ICallJournalService.Connection))) RefreshConnection();
        }
        if (_dirty.ContainsKey(Audit) && _auditVisible)
        {
            if (Changed(Audit, nameof(IAuditLogService.Events)))
            {
                if (_verification != null && !ReferenceEquals(_verifiedEvents, Audit.Events)) { _verification = null; NotifyVerification(); }
                RefreshAudit();
            }
            if (Changed(Audit, nameof(IAuditLogService.TotalCount))) Raise(nameof(AuditCount));
            if (Changed(Audit, nameof(IAuditLogService.Coverage))) Raise(nameof(Coverage));
            if (Changed(Audit, nameof(IAuditLogService.FileSizeMb), nameof(IAuditLogService.Copies))) Notify(nameof(RetentionSize), nameof(RetentionCopies));
        }
        if (_dirty.ContainsKey(Environment))
        {
            _groups = Environment.Groups.Select(g => new EnvironmentGroupRow(g)).ToArray();
            Notify(nameof(EnvironmentGroups), nameof(EnvironmentSummary), nameof(EnvironmentTone), nameof(LogTail));
        }
        if (_dirty.ContainsKey(Diagnostics)) Notify(nameof(DiagnosticCanStart), nameof(DiagnosticIdle), nameof(DiagnosticRunning),
            nameof(DiagnosticDone), nameof(DiagnosticLabel), nameof(DiagnosticTone), nameof(DiagnosticStep), nameof(DiagnosticPercent), nameof(DiagnosticPath));
        _dirty.Clear();
    }
    private bool Changed(INotifyPropertyChanged source, params string[] names) => _dirty.TryGetValue(source, out var properties)
        && (properties.Contains("") || names.Any(properties.Contains));
    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatch(() =>
    {
        foreach (var row in _callCache.Values) row.RefreshLanguage();
        foreach (var row in _requests) row.RefreshLanguage();
        _groups = Environment.Groups.Select(g => new EnvironmentGroupRow(g)).ToArray();
        RefreshCalls(); RefreshAudit(languageChanged: true);
        Notify(nameof(EnvironmentGroups), nameof(EnvironmentSummary), nameof(EnvironmentTone), nameof(PendingSummary), nameof(ApprovalSetting),
            nameof(PendingCount), nameof(AuditCount), nameof(Coverage), nameof(FollowLabel), nameof(ReleaseFilter));
        NotifyVerification();
    });
    private void Dispatch(Action action)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { if (!_disposed) action(); }));
    }
    private void RefreshCalls()
    {
        var pending = Approvals.Requests.Where(r => r.State == ApprovalState.Pending).ToArray();
        var ids = pending.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var records = (_pausedCalls ?? Journal.Calls).Where(c => !ids.Contains(c.RequestId)).Concat(pending.Select(r => new CallRecord(r.Id,
            r.Deadline.AddSeconds(-r.TimeoutSeconds), r.Host, r.Release,
            r.Operations.Count == 1 ? r.Operations[0].Tool : Loc.Current.T("Calls.PendingOperations", r.Operations.Count),
            true, CallResult.Pending, null, r.Project + " · " + r.Plc, r.ParametersJson,
            LocalizedText.Key("Calls.Pending"), "", LocalizedText.Empty, LocalizedText.Key("Approval.Pending"))
            { Actor = r.Actor })).ToArray();
        int previousSnapshotCount = _snapshotCount, previousCount = _calls.Count;
        var previousSelected = SelectedCall;
        _snapshotCount = records.Length;
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            keep.Add(record.JournalKey);
            if (!_callCache.TryGetValue(record.JournalKey, out var row)) _callCache[record.JournalKey] = new CallRow(record);
            else row.Update(record);
        }
        foreach (string key in _callCache.Keys.Where(k => !keep.Contains(k)).ToArray()) _callCache.Remove(key);
        string search = Search.Trim();
        var next = records.Where(c => (!WriteOnly || c.IsWrite) && (!FailOnly || c.Result is CallResult.Failed or CallResult.Partial or CallResult.Unknown)
                && (!ReleaseOnly || c.Release == Release) && (!HumanOnly || c.Actor == "workbench")
                && (!AiOnly || c.Actor == "mcp") && c.Tool.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Result == CallResult.Pending ? 0 : 1).ThenByDescending(c => c.Time).Select(c => _callCache[c.JournalKey]).ToArray();
        Sync(_calls, next);
        if ((previousCount == 0) != (_calls.Count == 0)) Raise(nameof(CallsEmpty));
        if (previousSnapshotCount != _snapshotCount) Notify(nameof(CallsCount), nameof(CallEmptyTitle));
        if (!ReferenceEquals(previousSelected, SelectedCall)) Raise(nameof(SelectedCall));
    }
    private void RefreshRequests()
    {
        var previous = _requests.ToDictionary(r => r.Request.Id);
        var next = Approvals.Requests.OrderBy(r => r.State == ApprovalState.Pending ? 0 : 1).Select(r =>
        {
            if (previous.TryGetValue(r.Id, out var row)) { row.Update(r); return row; }
            return new ApprovalRow(r, this);
        }).ToArray();
        Sync(_requests, next); Notify(nameof(RequestsEmpty));
    }
    private void RefreshAudit(bool languageChanged = false)
    {
        if (languageChanged) _auditRows.Clear();
        var old = _auditRows.ToDictionary(r => r.Record.Index);
        Sync(_auditRows, Audit.Events.TakeLast(AuditLogService.PageSize).Select(e => old.TryGetValue(e.Index, out var row)
            && row.Record == e && row.Selected == (e.Index == _auditSelection) ? row : new AuditRow(e, e.Index == _auditSelection)).ToArray());
        Notify(nameof(AuditEmpty), nameof(AuditCount));
    }
    private void RefreshConnection()
    {
        if (_connectionSource != Journal.Connection.ConfigurationJson)
        { _connectionSource = Journal.Connection.ConfigurationJson; _connectionJson = FeatureJson.Redact(_connectionSource); }
        Notify(nameof(Address), nameof(Transport), nameof(ConnectionJson));
    }
    private static void Sync<T>(ObservableCollection<T> rows, IReadOnlyList<T> next) where T : class
    {
        var keep = next.ToHashSet();
        for (int i = rows.Count - 1; i >= 0; i--) if (!keep.Contains(rows[i])) rows.RemoveAt(i);
        for (int i = 0; i < next.Count; i++)
        {
            if (i < rows.Count && ReferenceEquals(rows[i], next[i])) continue;
            int existing = rows.IndexOf(next[i]);
            if (existing >= 0) rows.Move(existing, i); else rows.Insert(i, next[i]);
        }
    }
    private void Notify(params string[] names) { foreach (string name in names) Raise(name); }
    private void NotifyVerification() => Notify(nameof(HasVerification), nameof(VerificationBroken), nameof(VerificationTone), nameof(VerificationText), nameof(BreakIndex));
    private void UpdateTimer()
    {
        if (Approvals.PendingCount > 0 && (_callsVisible || _drawerVisible)) _timer.Start(); else _timer.Stop();
    }
    private void OnTick(object? sender, EventArgs e)
    {
        if (_drawerVisible) foreach (var row in _requests.Where(r => r.Pending)) row.RefreshCountdown();
        if (_callsVisible) Raise(nameof(PendingSummary));
    }
    public void Dispose()
    {
        _disposed = true; _timer.Stop(); _timer.Tick -= OnTick;
        foreach (var source in _sources) source.PropertyChanged -= OnChanged;
        Loc.Current.LanguageChanged -= OnLanguageChanged;
    }
}

public sealed class CallRow(CallRecord record) : ObservableObject
{
    private string? _parameters, _resultJson;
    public CallRecord Record { get; private set; } = record;
    internal void Update(CallRecord record)
    {
        if (Record == record) return;
        bool parameters = Record.ParametersJson != record.ParametersJson || Record.ParametersTruncated != record.ParametersTruncated;
        bool result = Record.ResultJson != record.ResultJson || Record.ResultTruncated != record.ResultTruncated;
        Record = record;
        if (parameters) { _parameters = null; Raise(nameof(Parameters)); }
        if (result) { _resultJson = null; Raise(nameof(ResultJson)); }
        foreach (string name in new[] { nameof(Record), nameof(Time), nameof(Host), nameof(Tool), nameof(Duration), nameof(Target), nameof(Pending),
            nameof(Actor), nameof(ReadWrite), nameof(WriteTone), nameof(Tone), nameof(Result), nameof(PreviewNote), nameof(Summary), nameof(Error), nameof(HasError), nameof(Approval) }) Raise(name);
    }
    internal void RefreshLanguage()
    {
        foreach (string name in new[] { nameof(Actor), nameof(Host), nameof(ReadWrite), nameof(Result), nameof(PreviewNote), nameof(Summary), nameof(Error), nameof(Approval) }) Raise(name);
    }
    public string Time => Record.Time.ToLocalTime().ToString("HH:mm:ss");
    public string Actor => ActorDisplay.Name(Record.Actor);
    public string Host => (Record.Host == "engine-worker" ? Loc.Current["Calls.WorkerDispatched"] : Record.Host)
        + (Record.Release.Length == 0 ? "" : " · " + Record.Release);
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
    public string Parameters => _parameters ??= Record.ParametersTruncated ? TiaOpenness.Shared.CallJournalPayload.Bound(Record.ParametersJson) : FeatureJson.Redact(Record.ParametersJson);
    public string ResultJson => _resultJson ??= Record.ResultTruncated ? TiaOpenness.Shared.CallJournalPayload.Bound(Record.ResultJson) : FeatureJson.Redact(Record.ResultJson);
    public string PreviewNote => Loc.Current[Record.ParametersTruncated || Record.ResultTruncated ? "Calls.PreviewTruncated" : "Calls.PreviewRedacted"];
    public string Summary => Record.Outcome.Length == 0 ? Record.Summary.Resolve() : Loc.Current.T("Calls.JournalSummary", Result,
        Loc.Current[Record.Execution switch
        {
            "not-started" => "Calls.Execution.NotStarted", "read-only" => "Calls.Execution.ReadOnly", "completed" => "Calls.Execution.Completed",
            "partial" => "Calls.Execution.Partial", _ => "Calls.Execution.Unknown"
        }], Loc.Current[Record.Completeness switch
        {
            "complete" => "Calls.Completeness.Complete", "partial" => "Calls.Completeness.Partial", "none" => "Calls.Completeness.None", _ => "Calls.Completeness.Unknown"
        }]);
    public string Error => Record.ErrorCode + (EngineResultText.Error(Record.ErrorCode) is { Length: > 0 } localized
        ? "\n" + localized : Record.ErrorMessage.Resolve().Length > 0 ? "\n" + Record.ErrorMessage.Resolve() : "");
    public bool HasError => Error.Length > 0;
    public string Approval => Record.ApprovalRecord.Resolve();
}

public sealed class ApprovalRow(ApprovalRequest request, FeaturePagesViewModel owner) : ObservableObject
{
    private bool _jsonOpen;
    private string? _parameters;
    public ApprovalRequest Request { get; private set; } = request;
    public string Actor => ActorDisplay.Name(Request.Actor);
    public string Client => ActorDisplay.Client(Request.Actor);
    public bool Pending => Request.State == ApprovalState.Pending;
    public bool CanDecide => Pending && owner.Approvals.CanDecide(Request.Id);
    public int Remaining => owner.Remaining(Request);
    public double Percent => 100.0 * Remaining / Request.TimeoutSeconds;
    public FeatureTone CountdownTone => Remaining < 20 ? FeatureTone.Warning : FeatureTone.Accent;
    public bool JsonOpen { get => _jsonOpen; set => Set(ref _jsonOpen, value); }
    public string Parameters => _parameters ??= FeatureJson.Redact(Request.ParametersJson);
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
    internal void Update(ApprovalRequest request)
    {
        if (Request == request) return;
        if (Request.ParametersJson != request.ParametersJson) { _parameters = null; Raise(nameof(Parameters)); }
        Request = request; Raise(nameof(Request)); RefreshLanguage(); RefreshCountdown();
    }
    internal void RefreshLanguage()
    {
        foreach (string name in new[] { nameof(Actor), nameof(Client), nameof(Pending), nameof(CanDecide), nameof(Tone), nameof(State), nameof(Note), nameof(HasNote) }) Raise(name);
    }
    public void RefreshCountdown()
    {
        foreach (string name in new[] { nameof(Remaining), nameof(Percent), nameof(CountdownTone), nameof(CanDecide) }) Raise(name);
    }
}

public sealed record AuditRow(AuditEvent Record, bool Selected)
{
    public string Actor => ActorDisplay.Name(Record.Actor);
    public string Result => Record.Result switch
    {
        "succeeded" => Loc.Current["Calls.Success"], "rejected-before-operation" => Loc.Current["Calls.Rejected"],
        "failed" or "read-failed" => Loc.Current["Approval.Failed"], "partial" => Loc.Current["Approval.Partial"],
        "unknown" => Loc.Current["Approval.Unknown"], "enabled" => Loc.Current["Audit.Enabled"],
        "disabled" => Loc.Current["Audit.Disabled"], "invalid-record" => Loc.Current["Audit.InvalidRecord"], _ => Record.Result,
    };
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

internal static class ActorDisplay
{
    internal static string Name(string? actor) => actor switch
    { "mcp" => Loc.Current["Actor.AI"], "workbench" => Loc.Current["Actor.Human"], _ => Loc.Current["Actor.Unrecorded"] };
    internal static string Client(string? actor) => actor == "workbench" ? Loc.Current["Actor.Workbench"] : Loc.Current["Actor.McpClient"];
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
