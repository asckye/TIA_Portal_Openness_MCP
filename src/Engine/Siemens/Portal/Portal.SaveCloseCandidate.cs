using System;
using System.Threading;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Adapters.Native.Session;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private SaveCloseAdapter? saveCloseAdapter;
        private global::Siemens.Engineering.TiaPortal? saveCloseAttachment;
        private readonly TiaMcp.Logic.V4.SaveCloseSession saveCloseCandidates = new TiaMcp.Logic.V4.SaveCloseSession();
        private bool CanDetachSaveClose()
        {
            // Retain the exact attachment proof when close/copy clears the open candidate's cached binding.
            if (sessionCandidateAdapter?.IsNonOwningAttachment == true) saveCloseAttachment = _portal;
            return (bool?)LastConnectInfo?["startedNew"] == false
                || saveCloseAttachment != null && ReferenceEquals(saveCloseAttachment, _portal);
        }
        internal TiaMcp.Logic.V4.Envelope RunSaveCloseCandidate(string tool, SaveCloseRequest request, string mode, bool confirm, string hash, string expectedProjectFile, bool confirmDiscard)
        {
            if (saveCloseAdapter == null)
                saveCloseAdapter = new SaveCloseAdapter(() => _portal, () => _project,
                    () => new SessionState { ProcessId = _boundProcessId,
                        ProcessStartUtc = _boundProcessId.HasValue ? new DateTimeOffset(new DateTime(_processStartTicks, DateTimeKind.Utc)) : (DateTimeOffset?)null,
                        ProjectFile = _project == null ? null : _binding?.ProjectPath, Ownership = _project == null ? "none" : _projectOpenedByUs ? "owned" : "borrowed" },
                    () => _binding, CanDetachSaveClose,
                    () => { if (Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA) throw new InvalidOperationException("Save/close candidates require the owning MTA thread."); },
                    () => { _project = null; _session = null; _projectOpenedByUs = false; _expectedProjectName = null; _binding = null; sessionCandidateAdapter = null; InvalidateHmiSoftwareCache(); ResetHmiReadHealth(); },
                    () => { CaptureBinding(); sessionCandidateAdapter = null; }, () => { if (!DisconnectPortal()) throw new InvalidOperationException("Disconnect did not acknowledge detachment."); }, local: () => _session);
            var result = saveCloseCandidates.Run(saveCloseAdapter, Engineering.TiaMajorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), tool,
                TiaMcp.Logic.V4.Meta.Correlate(ModelContextProtocol.InvocationJournal.CorrelationId), request, mode, confirm, hash, expectedProjectFile, confirmDiscard);
            if (result.Meta.RequiresSessionReset) _bindingFault = "Save/close outcome is unknown; reset the session before further native access.";
            return result;
        }
    }
}
