using System;

namespace TiaMcp.PlcWorker
{
    internal sealed class WorkerSessionOutcomeState
    {
        private bool mutationOutcomeUnknown;
        private bool readsBlocked;

        internal bool RequiresReset => readsBlocked || mutationOutcomeUnknown;

        internal void MarkUncertain(bool blockReads = false)
        {
            mutationOutcomeUnknown = true;
            // A later outcome cannot relax an earlier whole-session refusal.
            readsBlocked = true;
        }

        internal void RequireUsable(bool readOnly)
        {
            if (TiaOpenness.Shared.SessionBehavior.RequiresReset(readsBlocked || mutationOutcomeUnknown, true))
                throw new InvalidOperationException(TiaOpenness.Shared.SessionBehavior.Recovery);
        }
    }
}
