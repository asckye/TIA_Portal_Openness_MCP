using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using TiaMcp.Adapters.Contracts.Candidates;
#if PROJECT_BASE
using EngineeringProject = Siemens.Engineering.ProjectBase;
#else
using EngineeringProject = Siemens.Engineering.Project;
#endif
#if PROJECT_MULTIUSER
using Siemens.Engineering.Multiuser;
#endif

namespace TiaMcp.Adapters.Native.Session
{
    public sealed class SaveCloseAdapter : ISaveCloseAdapter, ISaveCloseBoundary
    {
        private readonly Func<TiaPortal?> portal;
        private readonly Func<EngineeringProject?> project;
        private readonly Func<SessionState> binding;
        private readonly Func<object?> generation;
        private readonly Func<bool> canDisconnect;
        private readonly Action checkThread;
        private readonly Action clearProject;
        private readonly Action savedCopy;
        private readonly Action detach;
        private readonly bool worker;
        private object? lastGeneration;
        private EngineeringProject? lastProject;
        private TiaPortal? lastPortal;
        private bool captured;
        private bool closed;
        private bool detached;
        private int? lastProcessId;
        private DateTimeOffset? lastProcessStart;
        private long epoch;
        public long WorkerEpoch { get; set; }
        public bool RequiresReset { get; private set; }
#if PROJECT_MULTIUSER
        private readonly Func<LocalSession?> local;
#endif
        public SaveCloseAdapter(Func<TiaPortal?> portal, Func<EngineeringProject?> project, Func<SessionState> binding, Func<object?> generation,
            Func<bool> canDisconnect, Action checkThread, Action clearProject, Action savedCopy, Action detach, bool worker = false
#if PROJECT_MULTIUSER
            , Func<LocalSession?>? local = null
#endif
            )
        {
            this.portal = portal; this.project = project; this.binding = binding; this.generation = generation; this.canDisconnect = canDisconnect;
            this.checkThread = checkThread; this.clearProject = clearProject; this.savedCopy = savedCopy; this.detach = detach; this.worker = worker;
#if PROJECT_MULTIUSER
            this.local = local ?? (() => null);
#endif
        }
        public SaveCloseObservation Observe()
        {
            checkThread(); var state = binding(); var p = project(); var connection = portal(); var token = generation();
            if (captured && (!ReferenceEquals(token, lastGeneration) || !ReferenceEquals(p, lastProject) || !ReferenceEquals(connection, lastPortal))) epoch++;
            captured = true; lastGeneration = token; lastProject = p; lastPortal = connection;
            state.Epoch = epoch; state.WorkerEpoch = WorkerEpoch;
            if (state.ProcessId.HasValue)
            {
                if (!state.ProcessStartUtc.HasValue && state.ProcessId == lastProcessId) state.ProcessStartUtc = lastProcessStart;
                using var process = Process.GetProcessById(state.ProcessId.Value);
                var start = new DateTimeOffset(process.StartTime.ToUniversalTime());
                if (process.HasExited || state.ProcessStartUtc.HasValue && state.ProcessStartUtc != start) CandidatePrimitives.Fail("identity", "process-start-time");
                state.ProcessStartUtc = start;
            }
            lastProcessId = state.ProcessId; lastProcessStart = state.ProcessStartUtc;
            var result = new SaveCloseObservation { Binding = state, DisconnectSupported = canDisconnect(),
                WorkerCleanup = detached ? "detached" : worker ? "deferred-until-channel-close" : "not-requested",
                ObjectValidity = p == null ? closed ? "invalid" : "absent" : "unavailable" };
            if (p == null) return result;
            state.ProjectFile = CandidatePrimitives.CanonicalProject(p.Path.FullName);
            bool present = connection != null && connection.Projects.Any(x => object.Equals(x, p));
#if PROJECT_MULTIUSER
            var session = local(); result.LocalSession = session != null;
            if (session != null) present = connection != null && connection.LocalSessions.Any(s => object.Equals(s, session) && object.Equals(s.Project, p));
            if (p is MultiuserProject && session == null) CandidatePrimitives.Fail("precondition", "exact-local-session-handle");
#endif
            result.ObjectValidity = present ? "valid" : "invalid"; result.Dirty = p.IsModified;
            return result;
        }
        public void BeforeAction()
        { checkThread(); if (RequiresReset) CandidatePrimitives.Fail("precondition", "session-reset-required"); }
        public SaveCloseAttempt Execute(SaveCloseCheck check) => CandidateExecution.SaveClose(this, check);
        void ISaveCloseAdapter.Save()
        {
            checkThread();
#if PROJECT_MULTIUSER
            if (local() != null) { local()!.Save(); return; }
#endif
            ((Project)project()!).Save();
        }
        void ISaveCloseAdapter.SaveCopy(string directory)
        {
            checkThread();
#if PROJECT_MULTIUSER
            ((Project)project()!).SaveAs(new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(Path.GetFullPath(directory)))); savedCopy();
#else
            throw new NotSupportedException("SaveProjectCopy is not advertised by this host.");
#endif
        }
        void ISaveCloseAdapter.Close()
        {
            checkThread();
#if PROJECT_MULTIUSER
            if (local() != null) local()!.Close(); else
#endif
                ((Project)project()!).Close();
            clearProject(); closed = true;
        }
        void ISaveCloseAdapter.Disconnect() { checkThread(); detach(); detached = true; }
        public void MarkUncertain() { RequiresReset = true; }
    }
}
