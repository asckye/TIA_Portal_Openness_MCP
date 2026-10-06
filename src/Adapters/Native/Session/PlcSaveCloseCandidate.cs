using System;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Adapters.Native.Session;

namespace TiaMcp.PlcFoundation
{
    public sealed partial class PlcFoundationEngine
    {
        private SaveCloseAdapter? saveCloseAdapter;
        public SaveCloseReply SaveCloseCandidate(SaveCloseCall candidate, string mode = "preview", long bindingEpoch = 0)
        {
            var reply = new SaveCloseReply();
            try
            {
                Check(true);
                if (candidate.Check?.Request.Action == "save-copy") CandidatePrimitives.Invalid("unadvertised-save-copy");
                if (saveCloseAdapter == null)
                    saveCloseAdapter = new SaveCloseAdapter(() => portal, () => project,
                        () => new SessionState { ProcessId = lifecycle.ProcessId, ProcessStartUtc = lifecycle.ProcessId.HasValue ? sessionCandidateStart : null,
                            ProjectFile = lifecycle.ProjectFile, Ownership = project == null ? "none" : lifecycle.OwnsProject ? "owned" : "borrowed" },
                        () => project, () => ownsPortal == false, () => Check(true),
                        () => { project = null; lifecycle.Unbound(); sessionCandidateAdapter = null;
#if PLC_SAFETY
                            localSession = null;
#endif
                        }, () => { throw new NotSupportedException("Foundation does not advertise SaveProjectCopy."); }, () => Disconnect(), worker: true
#if PLC_SAFETY
                        , local: () => localSession
#endif
                        );
                saveCloseAdapter.WorkerEpoch = bindingEpoch;
                if (candidate.Action == "observe") reply.Observation = saveCloseAdapter.Observe();
                else if (candidate.Action == "execute" && mode == "apply" && candidate.Check != null)
                { reply.Attempt = saveCloseAdapter.Execute(candidate.Check); reply.RequiresSessionReset = reply.Attempt.RequiresSessionReset; }
                else CandidatePrimitives.Invalid("candidate.action");
            }
            catch (Exception error)
            { reply.Fault = error is CandidateObservationException observed ? observed.Fault : new CandidateFault { Kind = "preflight", Subject = "save-close-observation" }; }
            return reply;
        }
    }
}
