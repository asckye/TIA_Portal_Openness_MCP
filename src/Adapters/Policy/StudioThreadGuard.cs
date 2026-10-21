using System;
using System.Threading;

namespace TiaMcp.Adapters
{
    // The bridge owns one native thread: STA for V14 SP1-V19, MTA for V20/V21 as in the PlcWorker (API audit 2026-10-19, F3;
    // threading-and-concurrency skill: background Openness threads must be MTA). Reject cross-thread access and synchronous
    // callback reentry before invoking a session operation; never dispatch to another apartment.
    internal sealed class StudioThreadGuard
    {
        private readonly int ownerThread;
        private readonly ApartmentState apartment;
        private bool entered;

        internal StudioThreadGuard(string releaseKey)
        {
            apartment = PlcLifecyclePolicy.OwnerApartment(releaseKey);
            if (Thread.CurrentThread.GetApartmentState() != apartment)
                throw new InvalidOperationException("Studio operations require an owning " + apartment + " thread.");
            ownerThread = Thread.CurrentThread.ManagedThreadId;
        }

        internal T Run<T>(Func<T> operation)
        {
            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                throw new InvalidOperationException("Use the owning " + apartment + " thread; cross-thread native access is refused.");
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
