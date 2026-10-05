using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using TiaMcp.Adapters.Contracts.Candidates;
#if PLC_SAFETY
using EngineeringProject = Siemens.Engineering.ProjectBase;
#else
using EngineeringProject = Siemens.Engineering.Project;
#endif

namespace TiaMcp.Adapters.Native.Session
{
    // Compiled against each exact SDK. The supplied callbacks stay on the caller's owner thread.
    public sealed class SessionCandidateAdapter : ISessionCandidateAdapter, ISessionCandidateBoundary
    {
        private readonly Func<TiaPortal?> portal;
        private readonly Func<EngineeringProject?> project;
        private readonly Func<SessionState> state;
        private readonly Action<TiaPortal, int, DateTimeOffset> attached;
        private readonly Action<EngineeringProject, bool> bound;
        private readonly Action checkThread;
        private readonly Action<int, DateTimeOffset>? reserve;
        private readonly Action? releaseReservation;
        private readonly bool localSessionOpenSupported;
        private SessionRequest? pendingRequest;
        private TiaPortal? expectedPortal;
        private EngineeringProject? expectedProject;
        private SessionState? expectedState;
        private long epoch;
        public bool RequiresReset { get; private set; }
        public long WorkerEpoch { get; set; }

        public SessionCandidateAdapter(Func<TiaPortal?> portal, Func<EngineeringProject?> project, Func<SessionState> state,
            Action<TiaPortal, int, DateTimeOffset> attached, Action<EngineeringProject, bool> bound, Action checkThread,
            Action<int, DateTimeOffset>? reserve = null, Action? releaseReservation = null, bool localSessionOpenSupported = false)
        { this.portal = portal; this.project = project; this.state = state; this.attached = attached; this.bound = bound; this.checkThread = checkThread;
            this.reserve = reserve; this.releaseReservation = releaseReservation; this.localSessionOpenSupported = localSessionOpenSupported; }

        private static DateTimeOffset Start(int pid)
        { using var process = Process.GetProcessById(pid); if (process.HasExited) throw new InvalidOperationException("Selected TIA process exited."); return new DateTimeOffset(process.StartTime.ToUniversalTime()); }

        public SessionObservation Observe()
        {
            checkThread();
            var current = state(); current.Epoch = epoch; current.WorkerEpoch = WorkerEpoch;
            if (expectedState != null && (!ReferenceEquals(expectedPortal, portal()) || !ReferenceEquals(expectedProject, project())
                || current.ProcessId != expectedState.ProcessId || current.ProcessStartUtc != expectedState.ProcessStartUtc
                || current.ProjectFile != expectedState.ProjectFile || current.Ownership != expectedState.Ownership)) current.Ownership = "unknown";
            var rows = new List<SessionProcess>();
            foreach (var process in TiaPortal.GetProcesses())
            {
                if (rows.Count >= 1024) CandidatePrimitives.Fail("precondition", "process-budget");
                var row = new SessionProcess { ProcessId = process.Id };
                try
                {
                    var before = Start(process.Id);
                    string? file = process.ProjectPath?.FullName;
                    row.ProjectFiles = file == null ? Array.Empty<string>() : new[] { CandidatePrimitives.CanonicalProject(file) };
                    if (current.ProcessId == process.Id && portal() != null)
                    {
                        var paths = portal()!.Projects.Select(p => CandidatePrimitives.CanonicalProject(p.Path.FullName)).ToList();
#if PLC_SAFETY
                        paths.AddRange(portal()!.LocalSessions.Select(s => CandidatePrimitives.CanonicalProject(s.Project.Path.FullName)));
#endif
                        row.ProjectFiles = paths.OrderBy(p => p, StringComparer.Ordinal).ToArray();
                    }
                    row.ProcessStartUtc = before; row.Complete = Start(process.Id) == before;
                }
                catch (Exception) /* swallow(env-probe): process exit or access denial leaves the candidate explicitly incomplete */ { row.Complete = false; }
                rows.Add(row);
            }
            return new SessionObservation { Processes = rows.OrderBy(p => p.ProcessId).ToArray(), State = current, UpgradeSupported = true, LocalSessionOpenSupported = localSessionOpenSupported };
        }
        public void VerifyOwnedState()
        {
            checkThread();
            if (RequiresReset) CandidatePrimitives.Fail("precondition", "session-reset-required");
            if (expectedState == null) return;
            var current = state();
            if (!ReferenceEquals(expectedPortal, portal()) || !ReferenceEquals(expectedProject, project())
                || current.ProcessId != expectedState.ProcessId || current.ProcessStartUtc != expectedState.ProcessStartUtc
                || current.ProjectFile != expectedState.ProjectFile || current.Ownership != expectedState.Ownership
                || !current.ProcessId.HasValue || Start(current.ProcessId.Value) != expectedState.ProcessStartUtc)
            { RequiresReset = true; CandidatePrimitives.Fail("identity", "session-binding-ownership-epoch"); }
        }
        void ISessionCandidateAdapter.BeforeAction()
        {
            VerifyOwnedState();
            if (pendingRequest?.Action == "attach") reserve?.Invoke(pendingRequest.ProcessId, pendingRequest.ProcessStartUtc);
        }
        public SessionAttempt Execute(SessionCheck check)
        {
            checkThread(); pendingRequest = check.Request;
            try
            {
                var result = CandidateExecution.Session(this, check);
                if (!result.Issued && check.Request.Action == "attach") releaseReservation?.Invoke();
                return result;
            }
            finally { pendingRequest = null; }
        }
        private void Remember()
        { epoch++; expectedPortal = portal(); expectedProject = project(); expectedState = state(); }
        void ISessionCandidateAdapter.Attach(SessionRequest request)
        {
            checkThread();
            if (portal() != null || project() != null) CandidatePrimitives.Fail("precondition", "detached-session");
            var selected = TiaPortal.GetProcesses().Single(p => p.Id == request.ProcessId);
            if (Start(selected.Id) != request.ProcessStartUtc) CandidatePrimitives.Fail("identity", "process-id-start-time");
            var handle = selected.Attach();
            // Retain the handle even if post-attach observation fails. Never close an unknown project.
            attached(handle, request.ProcessId, request.ProcessStartUtc); Remember();
        }
        void ISessionCandidateAdapter.Bind(SessionRequest request)
        {
            checkThread();
            if (!request.ReuseOpen || project() != null) CandidatePrimitives.Fail("precondition", "explicit-unbound-borrowing");
            var p = portal() ?? throw new InvalidOperationException("No attached portal.");
            var matches = p.Projects.Cast<EngineeringProject>().Where(x => CandidatePrimitives.CanonicalProject(x.Path.FullName) == request.ProjectPath).ToList();
#if PLC_SAFETY
            matches.AddRange(p.LocalSessions.Select(s => s.Project).Where(x => CandidatePrimitives.CanonicalProject(x.Path.FullName) == request.ProjectPath));
#endif
            if (matches.Count != 1) CandidatePrimitives.Fail("identity", "exact-open-project");
            bound(matches.Single(), false); Remember();
        }
        void ISessionCandidateAdapter.Open(SessionRequest request)
        {
            checkThread();
            var p = portal() ?? throw new InvalidOperationException("No attached portal.");
            if (project() != null || p.Projects.Any()) CandidatePrimitives.Fail("precondition", "empty-selected-process");
#if PLC_SAFETY
            if (p.LocalSessions.Any()) CandidatePrimitives.Fail("precondition", "empty-selected-process");
            if (Path.GetExtension(request.ProjectPath).StartsWith(".als", StringComparison.OrdinalIgnoreCase))
            { if (!localSessionOpenSupported) CandidatePrimitives.Fail("precondition", "local-session-execution-gate");
                var session = p.LocalSessions.Open(new FileInfo(request.ProjectPath)); bound(session.Project, true); Remember(); return; }
#endif
            var opened = request.Upgrade == "allow" ? p.Projects.OpenWithUpgrade(new FileInfo(request.CopyPath)) : p.Projects.Open(new FileInfo(request.ProjectPath));
            bound(opened, true); Remember();
        }
        public void MarkUncertain() { RequiresReset = true; }
    }
}
