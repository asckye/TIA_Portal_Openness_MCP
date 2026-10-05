using System;
using System.ComponentModel;
using TiaOpenness.Gui.Common;

namespace TiaOpenness.Gui.Services.Stubs;

public interface IApprovalService : INotifyPropertyChanged
{
    bool Enabled { get; set; }
    int TimeoutSeconds { get; set; }
    int PendingCount { get; }
}

// Stub until P6-44: settings are memory-only; no host approval or execution is connected.
public sealed class ApprovalServiceStub : ObservableObject, IApprovalService
{
    public ApprovalServiceStub() { }
    internal ApprovalServiceStub(int pendingCount) { PendingCount = pendingCount; }
    private bool _enabled = true;
    private int _timeoutSeconds = 120;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public int PendingCount { get; }
    public int TimeoutSeconds
    {
        get => _timeoutSeconds;
        set
        {
            if (value is not (60 or 120 or 300)) throw new ArgumentOutOfRangeException(nameof(value));
            Set(ref _timeoutSeconds, value);
        }
    }
}
