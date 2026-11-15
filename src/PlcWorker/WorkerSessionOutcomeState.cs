using System;

namespace TiaMcp.PlcWorker
{
    internal sealed class WorkerSessionOutcomeState
    {
        private bool mutationOutcomeUnknown;
        internal bool RequiresReset => mutationOutcomeUnknown;
        internal long Revision { get; private set; }

        internal void MarkUncertain(bool mutation = true)
        {
            if (!mutation) return;
            mutationOutcomeUnknown = true;
            Revision++;
        }

    }
}
