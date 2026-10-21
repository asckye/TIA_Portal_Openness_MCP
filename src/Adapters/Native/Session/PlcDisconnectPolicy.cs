using System;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters
{
    // A successful disconnect ends this session. An uncertain attempt is never retried,
    // including by the engine's final Dispose. No project operation is needed here.
    // A portal this worker started (ConnectIsolatedPortal, or a project lifecycle start) is owned: Disconnect disposes it,
    // which closes the started TIA Portal, but only when no project is open in it (Terminating-the-connection topic).
    internal sealed class PlcDisconnectState
    {
        internal const string OwnedOpenProjectRefusal = "DisconnectPortal would close the TIA Portal instance this session started and the project open in it. "
            + "Save it explicitly (SaveProject) and close it (CloseProject) first; DisconnectPortal never saves, closes or discards a project.";
        internal bool Attempted { get; private set; }
        internal PlcDisconnectResult? Result { get; private set; }
        internal void RequireActive()
        { if (Attempted) throw new InvalidOperationException("Disconnect ended this worker session; create a new explicit session before attaching again. An uncertain disconnect must be inspected, never retried."); }
        internal static bool Supports(bool? ownsPortal, bool sharedPortal, bool workerStarted = false)
            => ownsPortal==false || ownsPortal==true && (sharedPortal || workerStarted);
        internal PlcDisconnectResult Execute(int? processId, bool? ownsPortal, Action detach, bool sharedPortal = false,
            bool workerStarted = false, bool ownedProjectOpen = false)
        {
            if (Result != null) return Result;
            RequireActive();
            if (processId.HasValue && !Supports(ownsPortal,sharedPortal,workerStarted))
                throw new NotSupportedException("Disconnect supports only proven non-owning existing-process attachments and portals this worker started; unknown Portal lifetime semantics are blocked.");
            bool started = processId.HasValue && ownsPortal==true && workerStarted && !sharedPortal;
            // Refused before any native call: the session stays usable and nothing is detached or disposed.
            if (started && ownedProjectOpen) throw new AdapterPreconditionException(OwnedOpenProjectRefusal, isArgument: false);
            Attempted = true;
            if (processId.HasValue) detach();
            Result = new PlcDisconnectResult { ProcessId=processId, Detached=processId.HasValue,
                Strategy=sharedPortal && ownsPortal==true ? "owned-shared-portal-dispose" : started ? "worker-started-portal-dispose" : "non-owning-attachment-only",
                LaunchMode=ownsPortal==true ? sharedPortal ? "engine-started" : "worker-started" : "never" };
            return Result;
        }
    }
}
