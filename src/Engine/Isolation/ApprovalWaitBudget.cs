using System;
using System.Diagnostics;

namespace TiaMcpServer.Isolation
{
    // Only trusted child-originated progress can pause the native-call deadline.
    internal sealed class ApprovalWaitBudget
    {
        private readonly object sync = new object();
        private readonly Func<TimeSpan> now;
        private TimeSpan total, began, limit;
        private bool waiting;
        private int requests;
        internal ApprovalWaitBudget(Func<TimeSpan>? now = null)
        { var clock = Stopwatch.StartNew(); this.now = now ?? (() => clock.Elapsed); }
        internal void Signal(string phase, int seconds)
        {
            lock (sync)
            {
                if (phase == "begin" && !waiting && seconds >= 1 && seconds <= 3600 && requests < 50)
                { began = now(); limit = TimeSpan.FromSeconds(seconds + 1); waiting = true; requests++; }
                else if (phase == "end" && waiting) { total += Active(); waiting = false; }
            }
        }
        private TimeSpan Active() { var duration = now() - began; return duration < limit ? duration : limit; }
        internal TimeSpan Excluded { get { lock (sync) return total + (waiting ? Active() : TimeSpan.Zero); } }
    }
}
