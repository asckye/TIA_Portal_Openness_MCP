using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private ProjectBindingIdentity? _binding;
        private string? _bindingFault;
        private PortalProcessLease? _processLease;
        private static long ProcessStart(int pid)
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) throw new InvalidOperationException("TIA process exited.");
            return process.StartTime.ToUniversalTime().Ticks;
        }
        private static PortalProcessLease Reserve(int pid, long ticks) => PortalProcessLease.Acquire(
            TiaOpenness.Shared.DataLocations.Current.LeasesDirectory, pid, ticks);
        private void CaptureBinding()
        {
            HmiExactAccess.InvalidateTokens();
            _binding = null; _bindingFault = null;
            if (_project == null) return;
            if (_boundProcessId == null || _processLease == null) throw new InvalidOperationException("No reserved TIA process.");
            if (ProcessStart(_boundProcessId.Value) != _processStartTicks) throw new InvalidOperationException("TIA process changed before project binding.");
            _binding = InvocationJournal.Native("Binding.Capture", () => new ProjectBindingIdentity(
                Engineering.TiaMajorVersion, _boundProcessId.Value, _processStartTicks, _project.Path.FullName, _project.Name));
            _expectedProjectName = _binding.ProjectName;
        }
        public JsonObject GetBindingIdentity() => new JsonObject {
            ["identity"] = _binding?.ToJson(), ["fault"] = _bindingFault,
            ["instanceReserved"] = _processLease != null, ["verifiedLive"] = false,
            ["note"] = "Cached binding only. Project access verifies PID/start time/full path/name; no implicit rebind."
        };
        internal void VerifyBinding(string operation)
        {
            sessionCandidateAdapter?.VerifyOwnedState();
            if (_bindingFault != null) throw new PortalException(PortalErrorCode.InvalidState, _bindingFault +
                " The old project binding cannot be reused, even after TIA restarts. GetOpennessWorkerStatus: if enabled=true, explicitly RestartOpennessWorker(confirmRestart=true); " +
                "otherwise restart this MCP service. Then ListPortalProcessProjects and ConnectProject using the new PID/start time/full path. Do not replay the failed write.");
            if (_project == null) return;
            if (System.Threading.Thread.CurrentThread.GetApartmentState() != System.Threading.ApartmentState.MTA)
                throw new PortalException(PortalErrorCode.InvalidState, "Background Openness calls require MTA; no native call attempted.");
            try
            {
                if (_binding == null || _boundProcessId == null || _processLease == null)
                    throw new InvalidOperationException("Exact project binding is unavailable.");
                _binding.Verify(Engineering.TiaMajorVersion, _boundProcessId.Value, ProcessStart(_boundProcessId.Value),
                    InvocationJournal.Native("Binding.ProjectPath", () => _project.Path.FullName),
                    InvocationJournal.Native("Binding.ProjectName", () => _project.Name));
            }
            catch (Exception ex)
            {
                _bindingFault = operation + " refused: " + ex.GetBaseException().Message + " Explicitly reconnect; automatic recovery is disabled.";
                _ = PortalFailureClassifier.IsPortalProcessLost(ex);
                throw new PortalException(PortalErrorCode.InvalidState, _bindingFault, inner: ex);
            }
        }
        // Process metadata selection happens before Attach; no probing other projects.
        public bool ConnectToProject(int processId, string processStartUtc, string projectPath)
        {
            if (!DateTimeOffset.TryParse(processStartUtc, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var expectedStart))
                throw new ArgumentException("processStartUtc must be the ISO timestamp from ListPortalProcessProjects.");
            string path = ProjectBindingIdentity.CanonicalPath(projectPath);
            var process = InvocationJournal.Native("TiaPortal.GetProcesses", () => TiaPortal.GetProcesses().SingleOrDefault(p => p.Id == processId))
                ?? throw new PortalException(PortalErrorCode.NotFound, "Requested TIA PID is no longer available.");
            if (ProcessStart(processId) != expectedStart.UtcDateTime.Ticks || process.ProjectPath == null ||
                !string.Equals(ProjectBindingIdentity.CanonicalPath(process.ProjectPath.FullName), path, StringComparison.OrdinalIgnoreCase))
                throw new PortalException(PortalErrorCode.InvalidState, "TIA PID/start time/project path changed before attachment.");
            ConnectSelectedProcess(process, path);
            return true;
        }
        private void ConnectSelectedProcess(TiaPortalProcess process, string? expectedPath)
        {
            if (_projectOpenedByUs) throw new PortalException(PortalErrorCode.InvalidState, "Save/close/disconnect the MCP-owned project explicitly before replacing its connection.");
            if (_portal != null) DisconnectPortal();
            int pid = process.Id;
            long ticks = ProcessStart(pid);
            var lease = Reserve(pid, ticks);
            // A timeout cannot cancel Attach. The MTA worker owns the lease until it
            // returns and detaches. A dead worker leaves ACTIVE for this TIA identity.
            TiaPortal? attached = TimedAttachment.Run(() => {
                try { return InvocationJournal.Native("TiaPortal.Attach", () => process.Attach()); }
                catch (Exception ex) { if (IsSecurityRefusal(ex)) lease.ReleaseCleanly(); else lease.Dispose(); throw; }
            }, late => { try { late.Dispose(); lease.ReleaseCleanly(); } finally { lease.Dispose(); } }, ConnectLogic.AttachTimeoutMsPerProcess);
            if (attached == null)
            {
                var timeout = new PortalException(PortalErrorCode.InvalidState, "Attach timed out. Answer the TIA Portal Openness access prompt (Yes / Yes to all), then inspect the session state and retry.");
                bool matching = false;
                try { matching = ProcessStart(pid) == ticks; }
                catch (Exception) /* swallow(env-probe): a vanished or inaccessible process cannot establish a confirmation-dialog reason */ { }
                var reason = TiaMcp.Adapters.Contracts.Candidates.SessionPrimitives.TimeoutReason(true, matching);
                if (reason != null) timeout.Data["sessionReason"] = reason;
                throw timeout;
            }
            _portal = attached; _processLease = lease; _boundProcessId = pid; _processStartTicks = ticks;
            try
            {
                if (ProcessStart(pid) != ticks) throw new InvalidOperationException("TIA process identity changed during Attach.");
                var sessions = InvocationJournal.Native("Binding.EnumerateSessions", () => _portal.LocalSessions.ToList());
                var projects = InvocationJournal.Native("Binding.EnumerateProjects", () =>
                    _portal.Projects.Cast<ProjectBase>().Concat(sessions.Select(s => s.Project)).ToList());
                var matches = expectedPath == null ? projects : projects.Where(p => string.Equals(p.Path.FullName, expectedPath, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count > 1 || (expectedPath != null && matches.Count != 1))
                    throw new InvalidOperationException("The selected TIA instance no longer contains exactly the requested project.");
                _project = matches.SingleOrDefault();
                _session = _project == null ? null : sessions.FirstOrDefault(s => string.Equals(s.Project.Path.FullName, _project.Path.FullName, StringComparison.OrdinalIgnoreCase));
                _projectOpenedByUs = false;
                CaptureBinding();
                InvalidateHmiSoftwareCache(); ResetHmiReadHealth();
                LastConnectInfo = new JsonObject { ["boundProcessId"] = pid, ["startedNew"] = false, ["binding"] = GetBindingIdentity() };
            }
            catch { DisconnectPortal(); throw; }
        }
    }
}
