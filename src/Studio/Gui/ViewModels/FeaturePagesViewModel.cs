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

public enum ToastKind { Success, Approval, Warning }
public readonly record struct FeatureFeedback(LocalizedText Message, ToastKind Kind = ToastKind.Success);

public sealed class FeaturePagesViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly INotifyPropertyChanged[] _sources;
    private readonly DispatcherTimer _timer;
    private readonly Func<DateTimeOffset> _now;
    private IReadOnlyList<CallRecord>? _pausedCalls;
    private bool _writeOnly, _failOnly, _releaseOnly, _humanOnly, _aiOnly, _disposed;
    private string _search = "", _release = TiaOpenness.Core.Environment.WorkbenchReleaseSelection.DefaultKey();
    private string? _selectedCall;
    private AuditVerification? _verification;
    private IReadOnlyList<AuditEvent>? _verifiedEvents;
    private (string Chain, long Index)? _auditBreak;
    private string _auditSearch = "";
    private AuditRow? _selectedAudit;
    private readonly ObservableCollection<ApprovalRow> _requests = [];
    private readonly ObservableCollection<ApprovalRow> _visibleRequests = [];
    private ApprovalRow? _selectedRequest;
    private bool _handledRequests;
    private readonly ObservableCollection<CallRow> _calls = [];
    private readonly ObservableCollection<AuditRow> _auditRows = [];
    private readonly Dictionary<string, CallRow> _callCache = new(StringComparer.Ordinal);
    private readonly Dictionary<INotifyPropertyChanged, HashSet<string>> _dirty = [];
    private bool _queued, _callsVisible = true, _auditVisible = true, _drawerVisible = true, _detailVisible, _verifying;
    private Task? _verificationTask;
    private int _snapshotCount;
    private (int Pending, int Failed, long? AverageMs) _callStats;
    private bool _allCalls = true;
    private string _connectionJson = "{}", _connectionSource = "";
    public long RefreshCount { get; private set; }
    private EnvironmentGroupRow[] _groups = [];
    private EnvironmentRow? _selectedEnvironment;

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
        RefreshEnvironmentSelection();
        RefreshCalls(); RefreshRequests(); RefreshAudit(); RefreshConnection(); UpdateTimer();
    }

    public IApprovalService Approvals { get; }
    public ICallJournalService Journal { get; }
    public IAuditLogService Audit { get; }
    public IEnvironmentCheckService Environment { get; }
    public IDiagnosticBundleService Diagnostics { get; }
    public event EventHandler<string>? DrawerRequested;
    public event EventHandler<FeatureFeedback>? Feedback;

    public bool OnApprovalsPage => _drawerVisible;
    public bool OnCallsPage => _callsVisible;
    public string ApprovalsTabLabel => Loc.Current.T("Agent.ApprovalsCount", Approvals.PendingCount);
    public string CallsTabLabel => Loc.Current.T("Agent.CallsCount", _snapshotCount);

    public bool WriteOnly { get => _writeOnly; set { if (Set(ref _writeOnly, value)) RefreshCalls(); } }
    public bool FailOnly { get => _failOnly; set { if (Set(ref _failOnly, value)) RefreshCalls(); } }
    public bool ReleaseOnly { get => _releaseOnly; set { if (Set(ref _releaseOnly, value)) { RefreshCalls(); Raise(nameof(ReleaseChoice)); } } }
    /// <summary>The release select: 0 shows every release, 1 only <see cref="Release"/> (the <see cref="ReleaseOnly"/> filter).</summary>
    public int ReleaseChoice { get => ReleaseOnly ? 1 : 0; set => ReleaseOnly = value == 1; }
    /// <summary>True while none of the write, failure and actor filters is set; the first segment of the filter bar.</summary>
    public bool AllCalls => _allCalls;
    public void ClearCallFilters() { WriteOnly = FailOnly = HumanOnly = AiOnly = false; Raise(nameof(AllCalls)); }
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
    // Statistic cards over the same snapshot the count line uses: the journal plus pending approvals, before filters.
    public int WriteCount { get; private set; }
    public int HumanCount { get; private set; }
    public int AiCount { get; private set; }
    public int CallStatTotal => _snapshotCount;
    public int CallStatPending => _callStats.Pending;
    public int CallStatFailed => _callStats.Failed;
    public string CallStatAverage => _callStats.AverageMs is { } ms ? ms.ToString("N0") : "—";
    public CallRow? SelectedCall => _selectedCall != null && _callCache.TryGetValue(_selectedCall, out var row) ? row : null;
    public string Address => string.IsNullOrEmpty(Journal.Connection.Address) ? Loc.Current["Feature.NotConnected"] : Journal.Connection.Address;
    public string Transport => string.IsNullOrEmpty(Journal.Connection.Transport) ? "—" : Journal.Connection.Transport;
    public string ConnectionJson => _connectionJson;
    public FeatureTone ApprovalPolicyTone => Approvals.Enabled ? FeatureTone.Success : FeatureTone.Warning;
    public string ApprovalPolicy => Approvals.Enabled ? Loc.Current["Agent.Manual"] : Loc.Current["Agent.Automatic"];
    public string ApprovalSetting => Approvals.Enabled ? Loc.Current.T("Calls.ApprovalOn", Approvals.TimeoutSeconds) : Loc.Current["Shell.ApprovalOff"];
    public IReadOnlyList<ApprovalRow> Requests => _requests;
    public IReadOnlyList<ApprovalRow> VisibleRequests => _visibleRequests;
    public ApprovalRow? SelectedRequest { get => _selectedRequest; set => Set(ref _selectedRequest, value); }
    public bool HandledRequests { get => _handledRequests; set { if (Set(ref _handledRequests, value)) FilterRequests(); } }
    public string WaitingLabel => Loc.Current.T("Approval.Waiting", Approvals.PendingCount);
    public bool RequestsEmpty => VisibleRequests.Count == 0;
    public string RequestsEmptyTitle => Loc.Current[HandledRequests ? "Approval.NoHistory" : "Shell.NoPending"];
    public string PendingCount => Loc.Current.T("Feature.Count", Approvals.PendingCount);
    public string PendingSummary => Approvals.Requests.FirstOrDefault(r => r.State == ApprovalState.Pending) is { } request
        ? Loc.Current.T("Calls.PendingSummary", ActorDisplay.Client(request.Actor), request.Operations.Count, Remaining(request)) : Loc
            .Current["Shell.NoPending"];
    public int Remaining(ApprovalRequest request) => Math.Clamp((int)Math.Ceiling((request.Deadline - _now()).TotalSeconds), 0, request.TimeoutSeconds);
    public void ToggleFollow() {
        _pausedCalls = FollowLatest ? Journal.Calls.ToArray() : null; RefreshCalls(); Notify(nameof(FollowLatest), nameof(FollowLabel)); }
    public void OpenApprovals() => DrawerRequested?.Invoke(this, "Approvals");
    public void OpenCall(CallRow row) { _selectedCall = row.Record.JournalKey; Raise(nameof(SelectedCall)); DrawerRequested?.Invoke(this, "CallDetail"); }
    public void Decide(ApprovalRow row, bool approve) => RunFeedback(() =>
    {
        bool accepted = approve ? Approvals.Approve(row.Request.Id) : Approvals.Deny(row.Request.Id);
        if (!accepted) return new(LocalizedText.Key("Approval.NoLongerPending"), ToastKind.Warning);
        return new(approve ? LocalizedText.Key("Approval.ApprovedToast") : LocalizedText.Key("Approval.RejectedToast", row.Summary));
    });

    public IReadOnlyList<AuditRow> AuditRows => _auditRows;
    public string AuditSearch { get => _auditSearch; set { if (Set(ref _auditSearch, value)) RefreshAudit(); } }
    public AuditRow? SelectedAudit
    {
        get => _selectedAudit;
        set { if (!ReferenceEquals(_selectedAudit, value)) { _selectedAudit = value; Raise(nameof(SelectedAudit)); } }
    }
    public bool AuditEmpty => _auditRows.Count == 0;
    public string AuditEmptyText => Loc.Current[Audit.Events.Count == 0 ? "Shell.AuditEmpty" : "Audit.NoMatches"];
    public string AuditCount => Loc.Current.T("Feature.Count", Audit.TotalCount.ToString("N0"));
    public bool LatestAuditPage => Audit is not AuditLogService log || log.IsLatest;
    public bool OlderAuditPage => !LatestAuditPage;
    public bool CanPageOlder => Audit is AuditLogService { HasOlder: true };
    public string AuditHead => Audit.Events.LastOrDefault() is { } last ? $"SHA-256 · #{last.Index}" : "SHA-256";
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
            _auditBreak = null; RefreshAudit(); NotifyVerification();
        }
        catch (Exception ex) { Feedback?.Invoke(this, new(LocalizedText.Literal(ex.Message), ToastKind.Warning)); }
        finally { _verifying = false; Notify(nameof(Verifying)); }
    }
    public AuditRow? JumpToBreak()
    {
        var broken = Audit.Events.TakeLast(AuditLogService.PageSize)
            .FirstOrDefault(e => e.Index == BreakIndex && (_verification?.Chain == null || e.Chain == _verification.Chain));
        if (broken == null) return null;
        _auditBreak = (broken.Chain, broken.Index);
        if (_auditSearch.Length != 0) { _auditSearch = ""; Raise(nameof(AuditSearch)); }
        RefreshAudit();
        SelectedAudit = AuditRows.First(r => r.Selected);
        return SelectedAudit;
    }
    public void SetRetention(int value, bool size) => _ = SetRetentionAsync(value, size);
    internal async Task SetRetentionAsync(int value, bool size)
    {
        try { await Task.Run(() => { if (size) Audit.FileSizeMb = value; else Audit.Copies = value; }); }
        catch (Exception ex) { Feedback?.Invoke(this, new(LocalizedText.Literal(ex.Message), ToastKind.Warning)); }
    }

    public IReadOnlyList<EnvironmentGroupRow> EnvironmentGroups => _groups;
    public bool HasEnvironmentStats => _groups.Any(g => g.Group.Rows.Count > 0);
    public IReadOnlyList<EnvironmentGroupRow> EnvironmentStats => _groups.Where(g => g.Group.Rows.Count > 0).ToArray();
    public IReadOnlyList<EnvironmentRow> EnvironmentRows => _groups.SelectMany(g => g.Rows).ToArray();
    public IReadOnlyList<EnvironmentRow> AttentionChecks => _groups.SelectMany(g => g.Rows).Where(r => r.Check.Status != CheckStatus.Pass).ToArray();
    public IReadOnlyList<EnvironmentRow> PassedChecks => _groups.SelectMany(g => g.Rows).Where(r => r.Check.Status == CheckStatus.Pass).ToArray();
    public EnvironmentRow? SelectedEnvironment { get => _selectedEnvironment; set { if (value != null) Set(ref _selectedEnvironment, value); } }
    private void RefreshEnvironmentSelection()
    {
        string? id = _selectedEnvironment?.Check.Id;
        _selectedEnvironment = _groups.SelectMany(g => g.Rows).FirstOrDefault(r => r.Check.Id == id)
            ?? AttentionChecks.FirstOrDefault(row => row.Warning) ?? AttentionChecks.FirstOrDefault() ?? PassedChecks.FirstOrDefault();
        Notify(nameof(HasEnvironmentStats), nameof(EnvironmentStats), nameof(EnvironmentRows), nameof(AttentionChecks), nameof(PassedChecks),
            nameof(SelectedEnvironment));
    }
    public int EnvironmentCheckCount => _groups.Sum(g => g.Group.Rows.Count);
    public int EnvironmentPassedCount => _groups.Sum(g => g.Group.Rows.Count(r => r.Status == CheckStatus.Pass));
    public int EnvironmentPendingCount => _groups.Sum(g => g.Group.Rows.Count(r => r.Status is CheckStatus.Unchecked or CheckStatus.Checking));
    public string EnvironmentSummary => CheckSummary(Environment.Groups.SelectMany(g => g.Rows).ToArray());
    public FeatureTone EnvironmentTone => CheckTone(Environment.Groups.SelectMany(g => g.Rows).ToArray());
    /// <summary>Shows the warning Alert above the groups while any check warns or fails.</summary>
    public bool EnvironmentNeedsAction => EnvironmentAttentionCount > 0;
    /// <summary>The menu badge: checks that warn or fail.</summary>
    public int EnvironmentAttentionCount => Environment.Groups.SelectMany(g => g.Rows).Count(r => r.Status is CheckStatus.Warn or CheckStatus.Fail);
    /// <summary>The Alert description: each check that needs action with its result.</summary>
    public string EnvironmentAttention => string.Join(Loc.Current.IsChinese ? "；" : "; ", Environment.Groups.SelectMany(g => g.Rows)
        .Where(r => r.Status is CheckStatus.Warn or CheckStatus.Fail).Select(r => r.Name.Resolve() + " · " + r.Result.Resolve()));
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
    public void Export() =>
        Run(() => { string message = Diagnostics.Export(); return message.Length == 0 ? LocalizedText.Empty : LocalizedText.Key(message); });
    public void Run(Func<LocalizedText> action) => RunFeedback(() => new(action()));
    private void RunFeedback(Func<FeatureFeedback> action)
    {
        try { var result = action(); if (result.Message.Resolve().Length > 0) Feedback?.Invoke(this, result); }
        catch (Exception ex) { Feedback?.Invoke(this, new(LocalizedText.Literal(ex.Message), ToastKind.Warning)); }
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
        Notify(nameof(OnApprovalsPage), nameof(OnCallsPage));
        if (Journal is CallJournalService journal) journal.SetActive(calls || detail || drawer);
        if (Audit is AuditLogService log) log.SetActive(audit);
        if (calls || detail) { RefreshCalls(); RefreshConnection(); }
        else if (drawer) RefreshAgentCount();
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
                else if (_drawerVisible) RefreshAgentCount();
                Notify(nameof(PendingSummary));
            }
            if (Changed(Approvals, nameof(IApprovalService.PendingCount)))
                Notify(nameof(PendingCount), nameof(CallsCount), nameof(PendingSummary), nameof(WaitingLabel), nameof(ApprovalsTabLabel));
            if (Changed(Approvals, nameof(IApprovalService.Enabled), nameof(IApprovalService.TimeoutSeconds)))
                Notify(nameof(ApprovalSetting), nameof(ApprovalPolicy), nameof(ApprovalPolicyTone));
            UpdateTimer();
        }
        if (_dirty.ContainsKey(Journal) && (_callsVisible || _detailVisible))
        {
            if (Changed(Journal, nameof(ICallJournalService.Calls))) RefreshCalls();
            if (Changed(Journal, nameof(ICallJournalService.Connection))) RefreshConnection();
        }
        else if (_drawerVisible && Changed(Journal, nameof(ICallJournalService.Calls))) RefreshAgentCount();
        if (_dirty.ContainsKey(Audit))
        {
            if (_auditVisible && Changed(Audit, nameof(IAuditLogService.Events)))
            {
                if (_verification != null && !ReferenceEquals(_verifiedEvents, Audit.Events))
                {
                    _verification = null;
                    _auditBreak = null;
                    NotifyVerification();
                }
                RefreshAudit();
            }
            if (Changed(Audit, nameof(IAuditLogService.TotalCount))) Raise(nameof(AuditCount));
            if (Changed(Audit, nameof(IAuditLogService.Coverage))) Raise(nameof(Coverage));
            if (Changed(Audit, nameof(IAuditLogService.FileSizeMb), nameof(IAuditLogService.Copies))) Notify(nameof(RetentionSize), nameof(RetentionCopies));
        }
        if (_dirty.ContainsKey(Environment))
        {
            _groups = Environment.Groups.Select(g => new EnvironmentGroupRow(g)).ToArray();
            RefreshEnvironmentSelection();
            Notify(nameof(EnvironmentGroups), nameof(EnvironmentSummary), nameof(EnvironmentTone), nameof(EnvironmentNeedsAction), nameof(EnvironmentAttention),
                nameof(EnvironmentAttentionCount), nameof(EnvironmentCheckCount), nameof(EnvironmentPassedCount),
                nameof(EnvironmentPendingCount), nameof(LogTail));
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
        RefreshEnvironmentSelection();
        RefreshCalls(); RefreshAudit(languageChanged: true);
        Notify(nameof(EnvironmentGroups), nameof(EnvironmentSummary), nameof(EnvironmentTone), nameof(EnvironmentNeedsAction), nameof(EnvironmentAttention),
            nameof(EnvironmentAttentionCount), nameof(PendingSummary), nameof(ApprovalSetting), nameof(ApprovalPolicy),
            nameof(PendingCount), nameof(WaitingLabel), nameof(RequestsEmptyTitle), nameof(ApprovalsTabLabel),
            nameof(CallsTabLabel), nameof(AuditCount), nameof(Coverage), nameof(FollowLabel), nameof(ReleaseFilter));
        NotifyVerification();
    });
    private void Dispatch(Action action)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { if (!_disposed) action(); }));
    }
    // The Approvals tab needs the count, but does not materialize the hidden Calls table.
    private void RefreshAgentCount()
    {
        var pending = Approvals.Requests.Where(r => r.State == ApprovalState.Pending).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        SetSnapshotCount(pending.Count + (_pausedCalls ?? Journal.Calls).Count(c => !pending.Contains(c.RequestId)));
    }
    private void SetSnapshotCount(int count)
    {
        if (_snapshotCount == count) return;
        _snapshotCount = count;
        Notify(nameof(CallsCount), nameof(CallEmptyTitle), nameof(CallStatTotal), nameof(CallsTabLabel));
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
        int previousCount = _calls.Count;
        var previousSelected = SelectedCall;
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
                && (!AiOnly || c.Actor == "mcp")
                && (c.Tool.Contains(search, StringComparison.OrdinalIgnoreCase) || c.Target.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.Result == CallResult.Pending ? 0 : 1).ThenByDescending(c => c.Time).Select(c => _callCache[c.JournalKey]).ToArray();
        Sync(_calls, next);
        if ((previousCount == 0) != (_calls.Count == 0)) Raise(nameof(CallsEmpty));
        SetSnapshotCount(records.Length);
        WriteCount = records.Count(c => c.IsWrite);
        HumanCount = records.Count(c => c.Actor == "workbench");
        AiCount = records.Count(c => c.Actor == "mcp");
        Notify(nameof(WriteCount), nameof(HumanCount), nameof(AiCount));
        var timed = records.Where(c => c.DurationMs != null).ToArray();
        var stats =
            (records.Count(c => c.Result == CallResult.Pending), records.Count(c => c.Result is CallResult.Failed or CallResult.Partial or CallResult.Unknown),
            timed.Length == 0 ? (long?)null : (long)Math.Round(timed.Average(c => (double)c.DurationMs!.Value)));
        if (stats != _callStats) { _callStats = stats; Notify(nameof(CallStatPending), nameof(CallStatFailed), nameof(CallStatAverage)); }
        bool all = !(WriteOnly || FailOnly || HumanOnly || AiOnly);
        if (all != _allCalls) { _allCalls = all; Raise(nameof(AllCalls)); }
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
        foreach (var group in next.GroupBy(row => row.Pending))
        {
            var first = group.First();
            foreach (var row in group) row.SetGroupHeader(ReferenceEquals(row, first), group.Count());
        }
        Sync(_requests, next); FilterRequests();
    }
    private void FilterRequests()
    {
        var selected = SelectedRequest;
        Sync(_visibleRequests, _requests.Where(r => r.Pending != HandledRequests).ToArray());
        SelectedRequest = selected != null && _visibleRequests.Contains(selected) ? selected : _visibleRequests.FirstOrDefault();
        Notify(nameof(RequestsEmpty), nameof(RequestsEmptyTitle), nameof(WaitingLabel));
    }
    private void RefreshAudit(bool languageChanged = false)
    {
        var selection = SelectedAudit?.Record;
        if (languageChanged) _auditRows.Clear();
        var page = Audit.Events.TakeLast(AuditLogService.PageSize).ToArray();
        // Resolve the actual predecessor before filtering. The link claimed by a record is not proof that its predecessor is loaded.
        var byIndex = page.ToDictionary(e => (e.Chain, e.Index));
        var old = _auditRows.ToDictionary(r => (r.Record.Chain, r.Record.Index));
        var query = AuditSearch.Trim();
        var next = new List<AuditRow>(page.Length);
        foreach (var entry in page.OrderByDescending(e => e.Index))
        {
            var key = (entry.Chain, entry.Index);
            var previous = byIndex.GetValueOrDefault((entry.Chain, entry.Index - 1));
            string previousHash = string.IsNullOrEmpty(previous?.Hash) ? "—" : previous.Hash;
            bool isBreak = key == _auditBreak;
            if (!old.TryGetValue(key, out var row) || row.Record != entry || row.Selected != isBreak || row.PreviousHash != previousHash)
                row = new AuditRow(entry, isBreak) { PreviousHash = previousHash };
            if (row.Matches(query)) next.Add(row);
        }
        Sync(_auditRows, next);
        SelectedAudit = next.FirstOrDefault(r => r.Record.Chain == selection?.Chain && r.Record.Index == selection?.Index) ?? next.FirstOrDefault();
        Notify(nameof(AuditEmpty), nameof(AuditEmptyText), nameof(AuditCount), nameof(AuditHead),
            nameof(LatestAuditPage), nameof(OlderAuditPage), nameof(CanPageOlder));
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
    private void NotifyVerification() =>
        Notify(nameof(HasVerification), nameof(VerificationBroken), nameof(VerificationTone), nameof(VerificationText), nameof(BreakIndex));
    private void UpdateTimer()
    {
        if (Approvals.PendingCount > 0 && (_callsVisible || _drawerVisible)) _timer.Start(); else _timer.Stop();
    }
    internal void RefreshApprovalCountdowns()
    {
        Approvals.Refresh();
        if (_drawerVisible) foreach (var row in _requests.Where(r => r.Pending)) row.RefreshCountdown();
        if (_callsVisible) Raise(nameof(PendingSummary));
    }
    private void OnTick(object? sender, EventArgs e) => RefreshApprovalCountdowns();
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
            nameof(Actor), nameof(ReadWrite), nameof(WriteTone), nameof(Tone), nameof(Result), nameof(PreviewNote), nameof(Summary), nameof(Error),
                nameof(HasError), nameof(Approval) }) Raise(name);
    }
    internal void RefreshLanguage()
    {
        foreach (string name in new[] {
                nameof(Actor), nameof(Host), nameof(ReadWrite), nameof(Result), nameof(PreviewNote), nameof(Summary), nameof(Error), nameof(Approval) })
            Raise(name);
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
    public string Parameters => _parameters ??= Record.ParametersTruncated ? TiaOpenness.Shared.CallJournalPayload.Bound(Record.ParametersJson) : FeatureJson
        .Redact(Record.ParametersJson);
    public string ResultJson => _resultJson ??= Record.ResultTruncated ? TiaOpenness.Shared.CallJournalPayload.Bound(Record.ResultJson) : FeatureJson
        .Redact(Record.ResultJson);
    public string PreviewNote => Loc.Current[Record.ParametersTruncated || Record.ResultTruncated ? "Calls.PreviewTruncated" : "Calls.PreviewRedacted"];
    public string Summary => Record.Outcome.Length == 0 ? Record.Summary.Resolve() : Loc.Current.T("Calls.JournalSummary", Result,
        Loc.Current[Record.Execution switch
        {
            "not-started" => "Calls.Execution.NotStarted", "read-only" => "Calls.Execution.ReadOnly", "completed" => "Calls.Execution.Completed",
            "partial" => "Calls.Execution.Partial", _ => "Calls.Execution.Unknown"
        }], Loc.Current[Record.Completeness switch
        {
            "complete" => "Calls.Completeness.Complete", "partial" => "Calls.Completeness.Partial", "none" => "Calls.Completeness.None", _ =>
                "Calls.Completeness.Unknown"
        }]);
    public string Error => Record.ErrorCode + (EngineResultText.Error(Record.ErrorCode) is { Length: > 0 } localized
        ? "\n" + localized : Record.ErrorMessage.Resolve().Length > 0 ? "\n" + Record.ErrorMessage.Resolve() : "");
    public bool HasError => Error.Length > 0;
    public string Approval => Record.ApprovalRecord.Resolve();
}

public sealed class ApprovalRow(ApprovalRequest request, FeaturePagesViewModel owner) : ObservableObject
{
    private bool _jsonOpen;
    private bool _planOpen;
    private string? _parameters;
    private int _lastRemaining = owner.Remaining(request);
    private bool _announced30, _announced10;
    public ApprovalRequest Request { get; private set; } = request;
    public string Actor => ActorDisplay.Name(Request.Actor);
    public string Client => ActorDisplay.Client(Request.Actor);
    public string UnverifiedClient => Request.Actor == "workbench" ? Client
        : Loc.Current.T("Approval.UnverifiedClient", string.IsNullOrWhiteSpace(Request.Client) ? Client : Request.Client);
    public bool Destructive => Request.Risk == ApprovalRisk.Destructive;
    public string Risk => Loc.Current[Destructive ? "Approval.RiskDestructive" : "Approval.RiskWrite"];
    private bool _startsGroup;
    private int _groupCount;
    public bool StartsGroup => _startsGroup;
    public int GroupCount => _groupCount;
    public string GroupTitle => Loc.Current[Pending ? "Approval.FeedPending" : "Approval.FeedRecent"];
    internal void SetGroupHeader(bool startsGroup, int count)
    {
        Set(ref _startsGroup, startsGroup, nameof(StartsGroup));
        Set(ref _groupCount, count, nameof(GroupCount));
    }
    public string Timer => $"{Remaining / 60:00}:{Remaining % 60:00}";
    public string TimerLabel => Pending ? Loc.Current.T("Approval.Timer", Timer) : State;
    public string FeedTime => Pending ? Timer : Request.Deadline.AddSeconds(-Request.TimeoutSeconds).ToLocalTime().ToString("HH:mm");
    public string FeedKind => Pending ? State + " · " + Risk : State;
    public string FirstObject => Request.Operations.FirstOrDefault()?.Object ?? "";
    public string ObjectPreview => FirstObject.Length <= 64 ? FirstObject : FirstObject[..63] + "…";
    public string Target => JoinFields(Request.Project, Request.Plc);
    public string Change => Request.Operations.FirstOrDefault() is { } op
        ? Request.Operations.Count > 1 ? Loc.Current.T("Approval.MoreActions", op.Action.Resolve(), Request.Operations.Count - 1) : op.Action.Resolve()
        : "";
    public string Safeguard => JoinFields(Request.Risks.Select(r => r.Resolve()).ToArray());
    public string DenyLabel => Loc.Current[Destructive ? "Approval.DenyDefault" : "Approval.Deny"];
    public string DecisionHint => Loc.Current[Destructive ? "Approval.DestructiveHint" : "Approval.WriteHint"];
    private static string JoinFields(params string[] values) => string.Join(" · ", values.Where(v => !string.IsNullOrWhiteSpace(v)));

    public string Summary => Request.Operations.Count == 1
        ? Loc.Current.T("Approval.Summary", Request.Operations[0].Action.Resolve()
            + (Request.Operations[0].Object.Length == 0 ? "" : " · " + Request.Operations[0].Object), Request.Project.Length == 0 ? "—" : Request.Project)
        : Loc.Current.T("Approval.BatchSummary", Request.Operations.Count, Request.Project.Length == 0 ? "—" : Request.Project,
            string.Join(" / ", Request.Operations.Select(op => op.Action.Resolve()).Distinct(StringComparer.Ordinal).Take(3)
                .Select(text => text.Length > 64 ? text[..64] + "…" : text)) + (Request.Operations.Count > 3 ? "…" : ""));
    public string AccessibleSummary => Summary + " · " + Risk + " · " + UnverifiedClient;
    public string ApproveLabel => Loc.Current[!Destructive
        ? Request.Operations.Count > 0 && Request.Operations.All(op => op.Tool.StartsWith("Import", StringComparison.Ordinal))
            ? "Approval.ApproveImport" : "Approval.Approve"
        : Request.RiskAction switch
        { "delete" => "Approval.ApproveDelete", "online" => "Approval.ApproveOnline", _ => "Approval.ApproveDestructive" }];
    private bool PhysicalDownload => Request.Operations.Count == 1 && Request.Operations[0].Tool == "DownloadPlc"
        && Field(TargetParameters?["onlineTarget"] as System.Text.Json.Nodes.JsonObject, "kind") == "physical";
    public string ConfirmationTitle => Loc.Current[PhysicalDownload ? "Approval.ConfirmDownloadTitle"
        : Request.RiskAction == "delete" ? "Approval.ConfirmDeleteTitle" : "Approval.ConfirmTitle"];
    public string ConfirmationAction => Loc.Current[PhysicalDownload ? "Approval.ConfirmDownloadAction"
        : Request.RiskAction == "delete" ? "Approval.ConfirmDeleteAction" : "Approval.ConfirmAction"];
    public string ConfirmationAccessible => Loc.Current[PhysicalDownload ? "Approval.ConfirmDownloadAccessible"
        : Request.RiskAction == "delete" ? "Approval.ConfirmDeleteAccessible" : "Approval.ConfirmAccessible"];
    private System.Text.Json.Nodes.JsonObject? TargetParameters
    {
        get
        {
            try { return System.Text.Json.Nodes.JsonNode.Parse(Parameters) as System.Text.Json.Nodes.JsonObject; }
            catch (System.Text.Json.JsonException) /* swallow(ui): a truncated or redacted payload has no structured fields. */ { return null; }
        }
    }
    private static string Field(System.Text.Json.Nodes.JsonObject? obj, string key) =>
        obj?[key] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    public string ConfirmationConsequence
    {
        get
        {
            var target = TargetParameters?["onlineTarget"] as System.Text.Json.Nodes.JsonObject;
            if (Field(target, "kind") == "physical" && Field(target, "targetIpAddress") is { Length: > 0 } address)
                return Loc.Current.T("Approval.PhysicalConsequence", address);
            return Loc.Current.T(Request.RiskAction == "delete" ? "Approval.DeleteConsequence" : "Approval.OnlineConsequence", Request.Project);
        }
    }
    public IReadOnlyList<KeyValuePair<string, string>> DestructiveFields
    {
        get
        {
            if (!Destructive) return [];
            var args = TargetParameters;
            var target = args?["onlineTarget"] as System.Text.Json.Nodes.JsonObject;
            var rows = new List<KeyValuePair<string, string>>();
            Add(Loc.Current["Approval.TargetKind"], Field(target, "kind"));
            Add(Loc.Current["Approval.TargetAddress"], Field(target, "targetIpAddress") is { Length: > 0 } address ? address : Field(args, "targetIpAddress"));
            Add(Loc.Current["Approval.Adapter"], Field(target, "pgPcInterface") is { Length: > 0 } adapter ? adapter : Field(args, "pgPcInterface"));
            Add(Loc.Current["Approval.PlanHash"], Field(args, "expectedPlanHash"));
            return rows;
            void Add(string label, string value)
            {
                if (value.Length > 0) rows.Add(new(label, value));
            }
        }
    }
    public string Context => JoinFields(Actor, Request.Transport, Request.Host, Request.Release);
    public event EventHandler<int>? CountdownThreshold;
    public bool Pending => Request.State == ApprovalState.Pending;
    public bool CanDecide => Pending && owner.Approvals.CanDecide(Request.Id);
    public int Remaining => owner.Remaining(Request);
    public string RemainingText => Loc.Current.T("Common.Seconds", Remaining);
    public FeatureTone CountdownTone => Remaining < 30 ? FeatureTone.Warning : FeatureTone.Accent;
    public bool JsonOpen { get => _jsonOpen; set => Set(ref _jsonOpen, value); }
    public string Parameters => _parameters ??= FeatureJson.Redact(Request.ParametersJson);
    public bool IsGenerationPlan => Request.GenerationPlan != null;
    public bool IsToolRequest => !IsGenerationPlan;
    public bool PlanOpen { get => _planOpen; set => Set(ref _planOpen, value); }
    public string CompletePlan => Request.GenerationPlan ?? "";
    public IEnumerable<GenerationDifferenceRow> GenerationDifferences
    {
        get
        {
            if (Request.GenerationPlan == null) return Array.Empty<GenerationDifferenceRow>();
            var plan = System.Text.Json.Nodes.JsonNode.Parse(Request.GenerationPlan)!;
            return new[] {
                ("conflicts", Loc.Current["Approval.Conflicts"]), ("skipped", Loc.Current["Approval.Skipped"]),
                    ("unavailable", Loc.Current["Approval.Unavailable"])
            }
                .Select(kind => new GenerationDifferenceRow(kind.Item2, plan[kind.Item1]!.AsArray()
                    .Select(row => new GenerationDifferenceRow(((string?)row?["key"] ?? (string?)row?["phase"]) + ": " + (string?)row?["reason"]
                        + (row?["detail"] == null ? "" : " · " + (string?)row["detail"]), Array.Empty<GenerationDifferenceRow>())).ToArray())).ToArray();
        }
    }
    public IEnumerable<GenerationStepRow> GenerationSteps
    {
        get
        {
            if (Request.GenerationPlan == null) return Array.Empty<GenerationStepRow>();
            var plan = System.Text.Json.Nodes.JsonNode.Parse(Request.GenerationPlan)!;
            return plan["steps"]!.AsArray().Where(step => Request.Operations.Any(op => op.Object == (string?)step!["key"] && op.Tool == (string?)step["tool"]))
                .Select(step => new GenerationStepRow((string)step!["id"]!, (string)step["phase"]!, (string)step["key"]!, (string)step["tool"]!)).ToArray();
        }
    }
    public string Metadata => JoinFields(UnverifiedClient, Request.Id, string.Join(" / ", Request.Operations.Select(op => op.Tool).Distinct()));
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
        bool changed = Request.ParametersJson != request.ParametersJson;
        Request = request;
        if (changed) { _parameters = null; Raise(nameof(Parameters)); }
        Raise(nameof(Request)); RefreshLanguage(); RefreshCountdown();
    }
    internal void RefreshLanguage()
    {
        foreach (string name in new[] {
                nameof(Actor), nameof(Client), nameof(UnverifiedClient), nameof(Risk), nameof(Destructive), nameof(GroupTitle),
                    nameof(FeedKind), nameof(Summary),
                    nameof(AccessibleSummary), nameof(ApproveLabel), nameof(ConfirmationTitle), nameof(ConfirmationAction), nameof(ConfirmationAccessible),
                    nameof(ConfirmationConsequence), nameof(Context), nameof(Metadata), nameof(Target), nameof(FirstObject), nameof(ObjectPreview),
                    nameof(Change), nameof(Safeguard), nameof(DecisionHint), nameof(DenyLabel), nameof(DestructiveFields), nameof(TimerLabel),
                    nameof(Pending), nameof(CanDecide), nameof(Tone), nameof(State), nameof(Note),
                    nameof(HasNote), nameof(IsGenerationPlan), nameof(IsToolRequest), nameof(GenerationDifferences), nameof(GenerationSteps),
                    nameof(CompletePlan)
            })
            Raise(name);
    }
    public void RefreshCountdown()
    {
        int remaining = Remaining;
        if (Pending && remaining > 0)
        {
            if (!_announced10 && _lastRemaining > 10 && remaining <= 10)
            { _announced30 = _announced10 = true; CountdownThreshold?.Invoke(this, 10); }
            else if (!_announced30 && _lastRemaining > 30 && remaining <= 30)
            { _announced30 = true; CountdownThreshold?.Invoke(this, 30); }
        }
        _lastRemaining = remaining;
        foreach (string name in new[] {
                nameof(Remaining), nameof(RemainingText), nameof(Timer), nameof(TimerLabel), nameof(FeedTime), nameof(CountdownTone), nameof(CanDecide) })
            Raise(name);
    }
}

public sealed record AuditRow(AuditEvent Record, bool Selected)
{
    public string PreviousHash { get; init; } = "—";
    public string Hash => string.IsNullOrEmpty(Record.Hash) ? "—" : Record.Hash;
    public string ShortHash => Hash[..Math.Min(8, Hash.Length)];
    public DateTimeOffset LocalTime => Record.Time.ToLocalTime();
    public string Sequence => "#" + Record.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal bool Matches(string query) => query.Length == 0 || Sequence.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Record.ToolObject.Contains(query, StringComparison.OrdinalIgnoreCase) || Record.Hash.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Type.Contains(query, StringComparison.CurrentCultureIgnoreCase) || Result.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Actor.Contains(query, StringComparison.CurrentCultureIgnoreCase);

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

public sealed record GenerationDifferenceRow(string Text, IReadOnlyList<GenerationDifferenceRow> Children);
public sealed record GenerationStepRow(string Id, string Phase, string Key, string Tool);

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
    public FeatureTone Tone => FeaturePagesViewModel.CheckTone(Group.Rows);
    public IReadOnlyList<EnvironmentRow> Rows => Group.Rows.Select(r => new EnvironmentRow(r) { Category = Name }).ToArray();
}

public sealed record EnvironmentRow(EnvironmentCheck Check)
{
    public string Category { get; init; } = "";
    public string Name => Check.Name.Resolve();
    public string Result => Check.Result.Resolve();
    public bool Passed => Check.Status == CheckStatus.Pass;
    public string Status => Loc.Current[Check.Status switch
    {
        CheckStatus.Pass => "Env.Passed", CheckStatus.Warn => "Env.StatusWarning", CheckStatus.Fail => "Env.Failed",
        CheckStatus.Checking => "Env.Checking", _ => "Env.Unchecked",
    }];
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
