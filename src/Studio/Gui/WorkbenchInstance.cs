using System;
using System.Threading;

namespace TiaOpenness.Gui;

internal sealed class WorkbenchInstance : IDisposable
{
    private readonly EventWaitHandle signal;
    private readonly RegisteredWaitHandle? activation;
    internal bool IsFirst { get; }

    internal WorkbenchInstance(string scope, Action activate)
    {
        var name = @"Global\" + scope + "." + TiaOpenness.Shared.ApprovalPipe.CurrentSid + ".Activate";
        signal = new EventWaitHandle(false, EventResetMode.AutoReset, name, out var first);
        IsFirst = first;
        if (first) activation = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => activate(), null, Timeout.Infinite, false);
        else signal.Set();
    }

    public void Dispose() { activation?.Unregister(null); signal.Dispose(); }
}
