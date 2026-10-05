using System;
using System.Linq;
using System.Threading;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Adapters.Native.Session;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private SessionCandidateAdapter? sessionCandidateAdapter;
        private readonly TiaMcp.Logic.V4.SessionCandidateSession sessionCandidates = new TiaMcp.Logic.V4.SessionCandidateSession();
        internal TiaMcp.Logic.V4.Envelope RunSessionCandidate(string tool, SessionRequest request, string mode, bool confirm, string hash, string expectedProjectFile, bool confirmUpgrade)
        {
            if (sessionCandidateAdapter == null)
                sessionCandidateAdapter = new SessionCandidateAdapter(() => _portal, () => _project,
                    () => new SessionState { ProcessId = _boundProcessId, ProcessStartUtc = _boundProcessId.HasValue ? new DateTimeOffset(new DateTime(_processStartTicks, DateTimeKind.Utc)) : (DateTimeOffset?)null,
                        ProjectFile = _project == null ? null : CandidatePrimitives.CanonicalProject(_project.Path.FullName), Ownership = _project == null ? "none" : _projectOpenedByUs ? "owned" : "borrowed" },
                    (handle, pid, start) => { _portal = handle; _boundProcessId = pid; _processStartTicks = start.UtcDateTime.Ticks; },
                    (value, own) => { _project = value; _projectOpenedByUs = own; _session = _portal!.LocalSessions.SingleOrDefault(s => object.Equals(s.Project, value)); CaptureBinding(); InvalidateHmiSoftwareCache(); ResetHmiReadHealth(); },
                    () => { if (Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA) throw new InvalidOperationException("Session candidates require the owning MTA thread."); },
                    (pid, start) => { _processLease = Reserve(pid, start.UtcDateTime.Ticks); },
                    () => { _processLease?.ReleaseCleanly(); _processLease = null; }, localSessionOpenSupported: true);
            return sessionCandidates.Run(sessionCandidateAdapter, Engineering.TiaMajorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), tool,
                TiaMcp.Logic.V4.Meta.Correlate(ModelContextProtocol.InvocationJournal.CorrelationId), request, mode, confirm, hash, expectedProjectFile, confirmUpgrade);
        }
    }
}
