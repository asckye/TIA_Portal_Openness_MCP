using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class PlcImportCandidateService
    {
        private static readonly ConditionalWeakTable<IEngineeringSession, PlcImportCandidateService> Sessions = new ConditionalWeakTable<IEngineeringSession, PlcImportCandidateService>();
        internal static PlcImportCandidateService For(IEngineeringSession session) => Sessions.GetValue(session, s => new PlcImportCandidateService(s));
        private readonly IEngineeringSession session;
        private readonly PlcImportSession imports = new PlcImportSession();
        private readonly List<KeyValuePair<PlcSoftware, PlcImportAdapter>> adapters = new List<KeyValuePair<PlcSoftware, PlcImportAdapter>>();
        private string? generation;
        private long epoch;
        public PlcImportCandidateService(IEngineeringSession session) { this.session = session; }

        internal Envelope Run(string tool, PlcImportRequest request, string mode, bool confirm, string hash, string projectFile)
        {
            string release = session.PortalMajorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string id = Meta.Correlate(InvocationJournal.CorrelationId);
            try
            {
                if (BehaviorCapabilities.Select(typeof(PlcImportCandidateService).Assembly, release, "P6-IMPORT") != BehaviorPolicy.SafeV4) PlcImportSession.Unsupported(release, "candidate-not-selected");
                if (imports.RequiresSessionReset) PlcImportSession.Refuse("The import session must be rebuilt.", new SessionResetRequiredDetails("plc-import-unknown"));
                var project = session.CurrentProject;
                if (project == null || session.CurrentPortal == null) PlcImportSession.Refuse("No project is bound.", new ProjectNotBoundDetails());
                var software = session.ExactPlcForEngineering(request.SoftwarePath, false);
                PlanIdentity Identity()
                {
                    session.VerifyBinding("plc-import-candidate");
                    if (!object.Equals(project, session.CurrentProject)) PlcImportSession.IdentityMismatch();
                    var binding = session.GetBindingIdentity()["identity"]!.AsObject();
                    string fresh = binding["generation"]!.GetValue<string>();
                    if (fresh != generation) { generation = fresh; epoch++; }
                    return new PlanIdentity(binding["processId"]!.GetValue<int>(), DateTimeOffset.Parse(binding["processStartUtc"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
                        DeviceCreationSession.CanonicalProject(project!.Path.FullName), epoch, null, Array.Empty<PlanFile>());
                }
                PlcSoftware Software()
                {
                    Identity(); var fresh = session.ExactPlcForEngineering(request.SoftwarePath, false);
                    if (!object.Equals(fresh, software)) PlcImportSession.IdentityMismatch();
                    return fresh;
                }
                PlcImportAdapter? adapter = null;
                foreach (var pair in adapters) if (object.Equals(pair.Key, software)) adapter = pair.Value;
                if (adapter == null)
                {
                    if (adapters.Count >= 128) PlcImportSession.Refuse("The session PLC budget is exhausted.", new LimitExceededDetails("plcs", 128, adapters.Count + 1));
                    adapter = new PlcImportAdapter(release, Identity, Software, () => session.ExactPlcForEngineering(request.SoftwarePath, true), true);
                    adapters.Add(new KeyValuePair<PlcSoftware, PlcImportAdapter>(software, adapter));
                }
                adapter.Identity = Identity; adapter.Software = Software;
                adapter.RequireOffline = () => session.ExactPlcForEngineering(request.SoftwarePath, true);
                return imports.Run(adapter, release, tool, id, request, mode, confirm, hash, projectFile);
            }
            catch (Exception ex)
            {
                var error = ex is PlcImportRejection rejected ? rejected.Error
                    : new Error("The exact PLC import target is unavailable; no import was issued.", new PreconditionFailedDetails("plc-import-target", null));
                return PlcImportSession.Result(release, tool, id, null, error, Outcome.RejectedBeforeOperation, Execution.NotStarted);
            }
        }
    }
}
