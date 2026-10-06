using System;
using System.Runtime.CompilerServices;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class PlcSourceCandidateService
    {
        private static readonly ConditionalWeakTable<IEngineeringSession, PlcSourceCandidateService> Sessions = new ConditionalWeakTable<IEngineeringSession, PlcSourceCandidateService>();
        internal static PlcSourceCandidateService For(IEngineeringSession session) => Sessions.GetValue(session, s => new PlcSourceCandidateService(s));
        private readonly IEngineeringSession session;
        private readonly SourceSession sources = new SourceSession();
        private PlcSourceAdapter? adapter;
        private object? boundProject;
        private PlcSoftware? boundSoftware;
        private string? generation;
        private long epoch;
        private PlcSourceCandidateService(IEngineeringSession session) { this.session = session; }
        internal Envelope Run(string tool, SourceRequest request, string mode, bool confirm, string hash, string file)
        {
            string release = session.PortalMajorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), id = Meta.Correlate(InvocationJournal.CorrelationId);
            try
            {
                if (BehaviorCapabilities.Select(typeof(PlcSourceCandidateService).Assembly, release, "P6-SOURCE") != BehaviorPolicy.SafeV4) throw new SourceRejection(new Error("Candidate is not selected.", new UnsupportedCapabilityDetails(release, "P6-SOURCE", "candidate-not-selected")));
                if (sources.RequiresSessionReset) return SourceSession.Result(release, tool, id, null, new Error("The source session must be rebuilt.", new SessionResetRequiredDetails("source-unknown")), Outcome.RejectedBeforeOperation, Execution.NotStarted);
                var project = session.CurrentProject;
                if (project == null || session.CurrentPortal == null) throw new SourceRejection(new Error("No project is bound.", new ProjectNotBoundDetails()));
                var plc = session.ExactPlcForEngineering(request.SoftwarePath, false);
                CandidateIdentity Identity()
                {
                    session.VerifyBinding("plc-source-candidate");
                    if (!object.Equals(project, session.CurrentProject)) CandidatePrimitives.Fail("identity", "project-binding");
                    var binding = session.GetBindingIdentity()["identity"]!.AsObject(); string fresh = binding["generation"]!.GetValue<string>();
                    if (fresh != generation) { generation = fresh; epoch++; }
                    return new CandidateIdentity(binding["processId"]!.GetValue<int>(), DateTimeOffset.Parse(binding["processStartUtc"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture), CandidatePrimitives.CanonicalProject(project!.Path.FullName), epoch);
                }
                PlcSoftware Software()
                { Identity(); var fresh = session.ExactPlcForEngineering(request.SoftwarePath, false); if (!object.Equals(fresh, plc)) CandidatePrimitives.Fail("identity", "plc-binding"); return fresh; }
                if (!object.Equals(boundProject, project) || !object.Equals(boundSoftware, plc)) { adapter = null; boundProject = project; boundSoftware = plc; }
                // The canonical query path is independent of whether this request used an empty path.
                string Path() => session is Portal portal ? portal.SourceCandidatePath(request.SoftwarePath) : request.SoftwarePath;
                adapter ??= new PlcSourceAdapter(Identity, Software, Path, () => session.ExactPlcForEngineering(request.SoftwarePath, true));
                adapter.Identity = Identity; adapter.Software = Software; adapter.SoftwarePath = Path; adapter.RequireOffline = () => session.ExactPlcForEngineering(request.SoftwarePath, true);
                return sources.Run(adapter, release, tool, id, request, mode, confirm, hash, file);
            }
            catch (Exception ex) { return SourceSession.Result(release, tool, id, null, SourceSession.Map(ex, hash), Outcome.RejectedBeforeOperation, Execution.NotStarted); }
        }
    }
}
