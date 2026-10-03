using System;

namespace TiaMcp.PlcWorker
{
    internal sealed class WorkerSessionOutcomeState
    {
        private bool mutationOutcomeUnknown;
        private bool readsBlocked;

        internal void MarkUncertain(bool blockReads = false)
        {
            mutationOutcomeUnknown = true;
            // A later outcome cannot relax an earlier whole-session refusal.
            readsBlocked |= blockReads;
        }

        internal void RequireUsable(bool readOnly)
        {
            if (readsBlocked || (mutationOutcomeUnknown && !readOnly))
                throw new InvalidOperationException("Prior native outcome is unknown; new explicit worker session required. Never replay.");
        }
    }
}
