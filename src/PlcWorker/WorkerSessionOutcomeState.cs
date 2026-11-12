using System;

namespace TiaMcp.PlcWorker
{
    internal sealed class WorkerSessionOutcomeState
    {
        private bool mutationOutcomeUnknown;
        internal bool RequiresReset => mutationOutcomeUnknown;
        internal long Revision { get; private set; }

        internal void MarkUncertain(bool blockReads = false, bool mutation = true)
        {
            mutationOutcomeUnknown = true;
            if (mutation) Revision++;
        }

    }
}
