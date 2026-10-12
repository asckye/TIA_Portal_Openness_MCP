using System;
using System.ComponentModel;
using System.Collections.Generic;

namespace TiaOpenness.Gui.Services.Stubs;

public interface IApprovalService : INotifyPropertyChanged
{
    public bool Enabled { get; set; }
    public int TimeoutSeconds { get; set; }
    public int PendingCount { get; }
    public IReadOnlyList<ApprovalRequest> Requests { get; }
    public event EventHandler<ApprovalRequest>? NewRequest;
    public bool CanDecide(string requestId);
    public bool Approve(string requestId);
    public bool Deny(string requestId);
    public void Refresh();
}
