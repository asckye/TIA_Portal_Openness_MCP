using System;
using System.Linq;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Adapters.Native.Session;

namespace TiaMcp.PlcFoundation
{
    public sealed partial class PlcFoundationEngine
    {
        private SessionCandidateAdapter? sessionCandidateAdapter;
        private DateTimeOffset? sessionCandidateStart;
        public SessionCandidateReply SessionCandidate(SessionCandidateCall candidate, string mode = "preview", long bindingEpoch = 0)
        {
            var reply = new SessionCandidateReply();
            try
            {
                Check();
                if (sessionCandidateAdapter == null)
                    sessionCandidateAdapter = new SessionCandidateAdapter(() => portal, () => project,
                        () => new SessionState { ProcessId = lifecycle.ProcessId, ProcessStartUtc = sessionCandidateStart,
                            ProjectFile = project == null ? null : CandidatePrimitives.CanonicalProject(project.Path.FullName),
                            Ownership = project == null ? "none" : lifecycle.OwnsProject ? "owned" : "borrowed" },
                        (handle, pid, start) => { lifecycle.RequireAttach(pid); portal = handle; lifecycle.Attached(pid); ownsPortal = false; sessionCandidateStart = start; },
                        (value, own) => { lifecycle.RequireUnbound(); project = value;
                            bool local = value.Path.Extension.StartsWith(".als", StringComparison.OrdinalIgnoreCase);
#if PLC_SAFETY
                            localSession = local ? portal!.LocalSessions.Single(s => object.Equals(s.Project, value)) : null;
#endif
                            lifecycle.Bound(value.Path.FullName, own, local); }, () => Check());
                sessionCandidateAdapter.WorkerEpoch = bindingEpoch;
                if (candidate.Action == "observe") reply.Observation = sessionCandidateAdapter.Observe();
                else if (candidate.Action == "execute" && mode == "apply" && candidate.Check != null)
                { reply.Attempt = sessionCandidateAdapter.Execute(candidate.Check); reply.RequiresSessionReset = reply.Attempt.RequiresSessionReset; }
                else CandidatePrimitives.Invalid("candidate.action");
            }
            catch (Exception error)
            { reply.Fault = error is CandidateObservationException observed ? observed.Fault : new CandidateFault { Kind = "preflight", Subject = "session-observation" }; }
            return reply;
        }
    }
}
