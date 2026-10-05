using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Logic.V4;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class PlcExportCandidateService
    {
        private static readonly ConditionalWeakTable<IEngineeringSession, PlcExportCandidateService> Sessions = new ConditionalWeakTable<IEngineeringSession, PlcExportCandidateService>();
        internal static PlcExportCandidateService For(IEngineeringSession session) => Sessions.GetValue(session, s => new PlcExportCandidateService(s));
        private readonly IEngineeringSession session;
        private readonly PlcExportSession exports = new PlcExportSession();
        private readonly List<KeyValuePair<PlcSoftware, PlcExportAdapter>> adapters = new List<KeyValuePair<PlcSoftware, PlcExportAdapter>>();
        private string? generation;
        private long epoch;
        public PlcExportCandidateService(IEngineeringSession session) { this.session = session; }

        internal Envelope Run(string tool, PlcExportRequest request, string mode, bool confirm, string hash, string projectFile)
        {
            string release = session.PortalMajorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string id = Meta.Correlate(InvocationJournal.CorrelationId);
            try
            {
                if (BehaviorCapabilities.Select(typeof(PlcExportCandidateService).Assembly, release, "P6-EXPORT") != BehaviorPolicy.SafeV4) PlcExportSession.Unsupported(release, "candidate-not-selected");
                if (exports.RequiresSessionReset) PlcExportSession.Refuse("The export session must be rebuilt.", new SessionResetRequiredDetails("plc-export-unknown"));
                var project = session.CurrentProject;
                if (project == null || session.CurrentPortal == null) PlcExportSession.Refuse("No project is bound.", new ProjectNotBoundDetails());
                var software = session.ExactPlcForEngineering(request.SoftwarePath, false);
                CandidateIdentity Identity()
                {
                    session.VerifyBinding("plc-export-candidate");
                    if (!object.Equals(project, session.CurrentProject)) PlcExportSession.IdentityMismatch();
                    var binding = session.GetBindingIdentity()["identity"]!.AsObject();
                    string fresh = binding["generation"]!.GetValue<string>();
                    if (fresh != generation) { generation = fresh; epoch++; }
                    return new CandidateIdentity(binding["processId"]!.GetValue<int>(), DateTimeOffset.Parse(binding["processStartUtc"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
                        DeviceCreationSession.CanonicalProject(project!.Path.FullName), epoch);
                }
                PlcSoftware Software()
                {
                    Identity(); var fresh = session.ExactPlcForEngineering(request.SoftwarePath, false);
                    if (!object.Equals(fresh, software)) PlcExportSession.IdentityMismatch();
                    return fresh;
                }
                PlcExportAdapter? adapter = null;
                foreach (var pair in adapters) if (object.Equals(pair.Key, software)) adapter = pair.Value;
                if (adapter == null)
                {
                    if (adapters.Count >= 128) PlcExportSession.Refuse("The session PLC budget is exhausted.", new LimitExceededDetails("plcs", 128, adapters.Count + 1));
                    adapter = new PlcExportAdapter(release, Identity, Software, () => session.ExactPlcForEngineering(request.SoftwarePath, true), true);
                    adapters.Add(new KeyValuePair<PlcSoftware, PlcExportAdapter>(software, adapter));
                }
                adapter.Identity = Identity; adapter.Software = Software;
                adapter.RequireOffline = () => session.ExactPlcForEngineering(request.SoftwarePath, true);
                return exports.Run(adapter, release, tool, id, request, mode, confirm, hash, projectFile);
            }
            catch (Exception ex)
            {
                var error = ex is PlcExportRejection rejected ? rejected.Error
                    : new Error("The exact PLC export target is unavailable; no export was issued.", new PreconditionFailedDetails("plc-export-target", null));
                return PlcExportSession.Result(release, tool, id, null, error, Outcome.RejectedBeforeOperation, Execution.NotStarted);
            }
        }
    }
}
