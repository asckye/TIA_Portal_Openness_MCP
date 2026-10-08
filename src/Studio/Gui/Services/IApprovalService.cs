using System;
using System.ComponentModel;
using System.Collections.Generic;

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
