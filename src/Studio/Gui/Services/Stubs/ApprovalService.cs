using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using TiaOpenness.Gui.Common;

namespace TiaOpenness.Gui.Services.Stubs;

public interface IApprovalService : INotifyPropertyChanged
{
    bool Enabled { get; set; }
    int TimeoutSeconds { get; set; }
    int PendingCount { get; }
    IReadOnlyList<ApprovalRequest> Requests { get; }
    event EventHandler<ApprovalRequest>? NewRequest;
    bool CanDecide(string requestId);
    bool Approve(string requestId);
    bool Deny(string requestId);
    void Refresh();
}

// Stub until P6-44: settings are memory-only; no host approval or execution is connected.
public sealed class ApprovalServiceStub : ObservableObject, IApprovalService
{
    private readonly List<ApprovalRequest> _requests = [];
    private readonly Func<DateTimeOffset> _now;
    public ApprovalServiceStub() : this(() => DateTimeOffset.UtcNow) { }
    internal ApprovalServiceStub(Func<DateTimeOffset> now) { _now = now; }
    private bool _enabled = true;
    private int _timeoutSeconds = 120;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public int PendingCount => _requests.Count(r => r.State == ApprovalState.Pending);
    public IReadOnlyList<ApprovalRequest> Requests => _requests.AsReadOnly();
    public event EventHandler<ApprovalRequest>? NewRequest;
    public int TimeoutSeconds
    {
        get => _timeoutSeconds;
        set
        {
            if (value is not (60 or 120 or 300)) throw new ArgumentOutOfRangeException(nameof(value));
            Set(ref _timeoutSeconds, value);
        }
    }

    // Transport integration will supply requests and execution outcomes in P6-44.
    public void Receive(ApprovalRequest request)
    {
        if (_requests.Any(r => r.Id == request.Id)) throw new ArgumentException("Duplicate request ID.", nameof(request));
        if (request.TimeoutSeconds <= 0 || request.State != ApprovalState.Pending) throw new ArgumentException("Expected a pending request with a timeout.", nameof(request));
        _requests.Add(request);
        Refresh();
        Changed();
        if (_requests[^1].State == ApprovalState.Pending) NewRequest?.Invoke(this, request);
    }

    public bool CanDecide(string requestId) => !_requests.Any(r => r.State is ApprovalState.Approved or ApprovalState.Executing)
        && _requests.FirstOrDefault(r => r.State == ApprovalState.Pending)?.Id == requestId;

    public bool Approve(string requestId) => Decide(requestId, ApprovalState.Approved);
    public bool Deny(string requestId) => Decide(requestId, ApprovalState.Rejected);
    private bool Decide(string requestId, ApprovalState state)
    {
        Refresh();
        if (!CanDecide(requestId)) return false;
        return Transition(requestId, state);
    }

    public bool Transition(string requestId, ApprovalState state)
    {
        int index = _requests.FindIndex(r => r.Id == requestId);
        if (index < 0) return false;
        bool valid = _requests[index].State switch
        {
            ApprovalState.Pending => state is ApprovalState.Approved or ApprovalState.Rejected or ApprovalState.TimedOut or ApprovalState.Disconnected,
            ApprovalState.Approved => state is ApprovalState.Executing or ApprovalState.Unknown,
            ApprovalState.Executing => state is ApprovalState.Completed or ApprovalState.Failed or ApprovalState.Partial or ApprovalState.Unknown,
            _ => false,
        };
        if (!valid) return false;
        _requests[index] = _requests[index] with { State = state };
        Changed();
        return true;
    }

    public void Refresh()
    {
        foreach (var request in _requests.Where(r => r.State == ApprovalState.Pending && r.Deadline <= _now()).ToArray())
            Transition(request.Id, ApprovalState.TimedOut);
    }

    public void Disconnect()
    {
        foreach (var request in _requests.ToArray())
        {
            if (request.State == ApprovalState.Pending) Transition(request.Id, ApprovalState.Disconnected);
            else if (request.State is ApprovalState.Approved or ApprovalState.Executing) Transition(request.Id, ApprovalState.Unknown);
        }
    }

    private void Changed() { Raise(nameof(Requests)); Raise(nameof(PendingCount)); }
}
