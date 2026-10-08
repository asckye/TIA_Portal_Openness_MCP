using System;

namespace TiaMcp.Adapters
{
    // A successful disconnect ends this session. An uncertain attempt is never retried,
    // including by the engine's final Dispose. No project operation is needed here.
    internal sealed class PlcDisconnectState
    {
        internal bool Attempted { get; private set; }
        internal PlcDisconnectResult? Result { get; private set; }
        internal void RequireActive()
        { if (Attempted) throw new InvalidOperationException("Disconnect ended this worker session; create a new explicit session before attaching again. An uncertain disconnect must be inspected, never retried."); }
        internal static bool Supports(bool? ownsPortal, bool sharedPortal) => ownsPortal==false || sharedPortal && ownsPortal==true;
        internal PlcDisconnectResult Execute(int? processId, bool? ownsPortal, Action detach, bool sharedPortal = false)
        {
            if (Result != null) return Result;
            RequireActive();
            if (processId.HasValue && !Supports(ownsPortal,sharedPortal))
                throw new NotSupportedException("Disconnect supports only proven non-owning existing-process attachments; unknown or owned Portal lifetime semantics are blocked.");
            Attempted = true;
            if (processId.HasValue) detach();
            Result = new PlcDisconnectResult { ProcessId=processId, Detached=processId.HasValue,
                Strategy=sharedPortal && ownsPortal==true ? "owned-shared-portal-dispose" : "non-owning-attachment-only",
                LaunchMode=ownsPortal==true ? "engine-started" : "never" };
            return Result;
        }
    }
}
