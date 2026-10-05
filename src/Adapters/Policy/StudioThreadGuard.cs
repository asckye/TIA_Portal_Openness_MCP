using System;
using System.Threading;

namespace TiaMcp.Adapters
{
    // The bridge owns the STA. Reject cross-thread access and synchronous callback
    // reentry before invoking a session operation; never dispatch to another apartment.
    internal sealed class StudioThreadGuard
    {
        private readonly int ownerThread;
        private bool entered;

        internal StudioThreadGuard()
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Studio operations require an owning STA thread.");
            ownerThread = Thread.CurrentThread.ManagedThreadId;
        }

        internal T Run<T>(Func<T> operation)
        {
            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                throw new InvalidOperationException("Use the owning STA thread; cross-thread native access is refused.");
            if (entered)
                throw new InvalidOperationException("Studio adapter operations cannot be reentered.");
            entered = true;
            try { return operation(); }
            finally { entered = false; }
        }

        internal void Run(Action operation)
        {
            Run(() => { operation(); return true; });
        }
    }
}
