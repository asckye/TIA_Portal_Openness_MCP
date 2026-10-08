using System;
using System.Threading;
using Siemens.Engineering;
using Siemens.Engineering.Multiuser;
using TiaMcp.Adapters;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Worker;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private bool foundationSession;
        private int foundationOwner;
        private SharedSessionLifecycle? sharedLifecycle;
        private Action? sharedDisconnect;
        private bool sharedPortalOwned;
        internal bool NeedsSharedLifecycle => sharedLifecycle != null && !sharedLifecycle.Executing;
        internal T SharedSessionOperation<T>(Func<T> operation) => sharedLifecycle!.Engine(operation);
        internal void UseSharedLifecycle(SharedSessionLifecycle lifecycle, Action disconnect)
        { sharedLifecycle = lifecycle; sharedDisconnect = disconnect; }
        internal void BorrowEngineSession(Action<TiaPortal?, ProjectBase?, object?, PlcRuntimeState, long, PortalProcessLease?, bool> adopt)
        {
            if (foundationOwner != Thread.CurrentThread.ManagedThreadId)
                throw new InvalidOperationException("Shared session transfer requires its owner thread.");
            if (_project != null && _binding == null) throw new InvalidOperationException("Engine project identity was not captured.");
            adopt(_portal, _project, _session, new PlcRuntimeState { ReleaseKey=Engineering.TiaMajorVersion.ToString(),
                IsAttached=_boundProcessId.HasValue, ProcessId=_boundProcessId, ProjectFile=_binding?.ProjectPath,
                OwnsProject=_projectOpenedByUs, IsLocalSession=_session!=null }, _processStartTicks, _processLease, sharedPortalOwned);
        }

        internal void AdoptFoundationSession(TiaPortal? portal, ProjectBase? project, object? session,
            PlcRuntimeState state, long processStartTicks, PortalProcessLease? lease, bool ownedPortal = false)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA
                || foundationOwner != 0 && foundationOwner != Thread.CurrentThread.ManagedThreadId)
                throw new InvalidOperationException("Shared session adoption requires the worker's MTA owner thread.");
            foundationOwner = Thread.CurrentThread.ManagedThreadId;
            foundationSession = true;
            if (!ReferenceEquals(_project, project) || !string.Equals(_binding?.ProjectPath,
                state.ProjectFile == null ? null : ProjectBindingIdentity.CanonicalPath(state.ProjectFile), StringComparison.OrdinalIgnoreCase))
            {
                _softwareContainerCache.Clear(); _plcResolutionCache.Clear();
                InvalidateHmiSoftwareCache(); ResetHmiReadHealth(); HmiExactAccess.InvalidateTokens();
            }
            _portal = portal; _project = project; _session = session as LocalSession;
            sharedPortalOwned = ownedPortal;
            _boundProcessId = state.ProcessId; _processStartTicks = processStartTicks; _processLease = lease;
            _projectOpenedByUs = state.OwnsProject;
            // Reuse the cached path and lease. Read the name once for a new binding;
            // adoption never attaches or selects an implicit project.
            if (project == null || !state.ProcessId.HasValue) _binding = null;
            else if (_binding == null || _binding.ProcessId != state.ProcessId || _binding.StartUtcTicks != processStartTicks
                || !string.Equals(_binding.ProjectPath, ProjectBindingIdentity.CanonicalPath(state.ProjectFile!), StringComparison.OrdinalIgnoreCase))
                _binding = new ProjectBindingIdentity(Engineering.TiaMajorVersion, state.ProcessId.Value, processStartTicks,
                    state.ProjectFile!, InvocationJournal.Native("Binding.FoundationProjectName", () => project.Name));
            _expectedProjectName = _binding?.ProjectName;
            _bindingFault = null;
        }

        internal void ClearFoundationSession(string? fault = null)
        {
            if (foundationOwner != Thread.CurrentThread.ManagedThreadId)
                throw new InvalidOperationException("Shared session clearing requires its owner thread.");
            _portal = null; _project = null; _session = null; _openedProject = null;
            _boundProcessId = null; _processLease = null; _binding = null; _expectedProjectName = null;
            _processStartTicks = 0; sharedPortalOwned = false;
            _bindingFault = fault;
            _softwareContainerCache.Clear(); _plcResolutionCache.Clear();
            InvalidateHmiSoftwareCache(); HmiExactAccess.InvalidateTokens();
        }
    }
}
