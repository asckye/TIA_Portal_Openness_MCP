using System;

namespace TiaMcp.PlcFoundation
{
    // A successful disconnect ends this session. An uncertain attempt is never retried,
    // including by the engine's final Dispose. No project operation is needed here.
    internal sealed class PlcDisconnectState
    {
        internal bool Attempted { get; private set; }
        internal PlcDisconnectResult? Result { get; private set; }
        internal void RequireActive()
        { if (Attempted) throw new InvalidOperationException("Disconnect ended this worker session; create a new explicit session before attaching again. An uncertain disconnect must be inspected, never retried."); }
        internal PlcDisconnectResult Execute(int? processId, bool? ownsPortal, Action detach)
        {
            if (Result != null) return Result;
            RequireActive();
            if (processId.HasValue && ownsPortal != false)
                throw new NotSupportedException("Disconnect supports only proven non-owning existing-process attachments; unknown or owned Portal lifetime semantics are blocked.");
            Attempted = true;
            if (processId.HasValue) detach();
            Result = new PlcDisconnectResult { ProcessId=processId, Detached=processId.HasValue };
            return Result;
        }
    }
}
