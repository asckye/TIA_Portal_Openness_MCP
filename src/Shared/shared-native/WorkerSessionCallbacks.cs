#if WORKER_SESSION_LIFECYCLE
using System;
using System.Collections.Generic;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Adapters.Session;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;

namespace TiaMcp.Adapters
{
    public sealed class WorkerSessionIdentity
    {
        public int ProcessId { get; }
        public long StartUtcTicks { get; }
        public string ProjectPath { get; }
        public string ProjectName { get; }
        public string Generation { get; }
        public Dictionary<string, object?> Identity { get; }
        public Action<int, int, long, string, string> Verify { get; }

        public WorkerSessionIdentity(int pid, long ticks, string path, string name, string generation,
            Dictionary<string, object?> identity, Action<int, int, long, string, string> verify)
        {
            ProcessId = pid; StartUtcTicks = ticks; ProjectPath = path; ProjectName = name;
            Generation = generation; Identity = identity; Verify = verify;
        }
    }

    public sealed partial class PlcFoundationEngine
    {
        private WorkerSessionIdentity? _workerBinding;
        private Func<int, int, long, string, string, WorkerSessionIdentity>? _workerCreateIdentity;
        private Func<object?, PlcSeedHmiReply>? _workerSeedReply;
        private object? _workerProject;
        private string? _workerBindingFault, _workerDeviceGeneration;
        private long _workerDeviceEpoch;
        private Func<long>? _workerProcessStart;
        private Func<bool>? _workerProcessReserved;
        public string? WorkerSessionBindingFault => _workerBindingFault;
        internal bool WorkerSessionBindingConfigured => _workerProcessStart != null;

        public void ConfigureWorkerSessionCallbacks(Func<long> processStart, Func<bool> processReserved,
            Func<int, int, long, string, string, WorkerSessionIdentity> createIdentity,
            Func<object?, PlcSeedHmiReply> seedReply)
        {
            Check();
            _workerCreateIdentity = createIdentity;
            _workerSeedReply = seedReply;
            _workerProcessStart = processStart;
            _workerProcessReserved = processReserved;
            ProjectCachedBinding = WorkerBindingSnapshot;
            ProjectExpectedProjectName = () => _workerBinding?.ProjectName;
            EngineeringProjectMissing = () =>
            {
                Check();
                if (project == null) return true;
                VerifyWorkerBinding("Project access");
                return false;
            };
            ProjectValidationTree = () => EngineeringProjectMissing!() ? string.Empty : ReadProjectTree();
            AcquireEngineeringEditAccess = () => SessionCallbackPrimitives.EditAccess(portal != null,
                () => VerifyWorkerBinding("Write"), () => HasEngineeringTransaction,
                () => portal!.ExclusiveAccess("MCP: verifying a precise HMI operation"));
            SharedFamilyMutationIdentity = expected =>
                MutationIdentityPolicy.RequireSameProject(expected, ReadState().ProjectFile);
            SharedHardwareCandidateIdentity = path =>
            {
                VerifyWorkerBinding("device-creation-candidate");
                return SessionCallbackPrimitives.HardwareIdentity(_workerBinding!.ProcessId,
                    new DateTimeOffset(new DateTime(_workerBinding.StartUtcTicks, DateTimeKind.Utc)),
                    _workerBinding.Generation, () => {
                        string current = path();
                        MutationIdentityPolicy.RequireSameProject(_workerBinding.ProjectPath, current);
                        return current;
                    },
                    ref _workerDeviceGeneration, ref _workerDeviceEpoch);
            };
            HmiBindingIdentity = WorkerBindingSnapshot;
        }

        public PlcSeedHmiReply ImportProjectLifecycleSeedHmi(SeedHmiRequest request)
            => SessionCallbackPrimitives.SeedImport(request, ExecuteHmi, _workerSeedReply!);

        private Dictionary<string, object?> WorkerBindingSnapshot()
        {
            Check(true);
            return SessionCallbackPrimitives.Snapshot(_workerBinding?.Identity, _workerBindingFault, _workerProcessReserved!());
        }

        public void RefreshWorkerSessionBinding()
        {
            Check(true);
            if (_workerBindingFault != null) return;
            if (project == null || !lifecycle.ProcessId.HasValue)
            {
                _workerBinding = null;
                _workerProject = null;
                return;
            }
            string path = lifecycle.ProjectFile!;
            long ticks = _workerProcessStart!();
            bool samePath = string.Equals(_workerBinding?.ProjectPath,
                System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
            if (!ReferenceEquals(_workerProject, project) || !samePath)
            {
                sessionCandidateAdapter = null; saveCloseAdapter = null;
            }
            if (_workerBinding == null || _workerBinding.ProcessId != lifecycle.ProcessId
                || _workerBinding.StartUtcTicks != ticks || !samePath)
            {
                _workerBinding = _workerCreateIdentity!(TiaMcp.Versioning.TiaVersionCatalog.Get(ReleaseKey).MajorVersion,
                    lifecycle.ProcessId.Value, ticks, path,
                    InvocationJournal.Native("Binding.FoundationProjectName", () => project.Name));
            }
            _workerProject = project;
        }

        private void CaptureWorkerSessionBinding()
        {
            _workerBinding = null;
            _workerBindingFault = null;
            if (project == null) return;
            _workerBinding = SessionCallbackPrimitives.Capture(lifecycle.ProcessId, _workerProcessStart!(),
                _workerProcessReserved!(), () => _workerCreateIdentity!(TiaMcp.Versioning.TiaVersionCatalog.Get(ReleaseKey).MajorVersion,
                    lifecycle.ProcessId!.Value, _workerProcessStart!(), project.Path.FullName, project.Name));
            _workerProject = project;
            sessionCandidateAdapter = null; saveCloseAdapter = null;
        }

        public void LockWorkerSessionBinding(string reason)
        {
            Check(true);
            _workerBindingFault ??= reason;
            _workerBinding = null;
        }

        public void ClearWorkerSessionBinding()
        {
            Check(true);
            _workerBinding = null;
            _workerProject = null;
            _workerBindingFault = null;
            sessionCandidateAdapter = null; saveCloseAdapter = null;
        }

        private void VerifyWorkerBinding(string operation)
        {
            Check();
            sessionCandidateAdapter?.VerifyOwnedState();
            if (_workerBindingFault != null)
                throw new PortalException(PortalErrorCode.InvalidState, _workerBindingFault);
            if (project == null) return;
            try
            {
                SessionCallbackPrimitives.Verify(_workerBinding != null, TiaMcp.Versioning.TiaVersionCatalog.Get(ReleaseKey).MajorVersion,
                    lifecycle.ProcessId, _workerProcessReserved!(), () => project.Path.FullName, () => project.Name,
                    (major, pid, ticks, path, name) => _workerBinding!.Verify(major, pid, ticks, path, name));
            }
            catch (Exception error)
            {
                _workerBindingFault = operation + " refused: " + error.GetBaseException().Message
                    + " Explicitly reconnect; automatic recovery is disabled.";
                throw new PortalException(PortalErrorCode.InvalidState, _workerBindingFault, inner: error);
            }
        }
    }
}
#endif
