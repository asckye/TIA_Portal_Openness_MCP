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
    public sealed class PlcDisconnectResult
    {
        public string Stage { get; internal set; } = "disconnected";
        public string SessionState { get; internal set; } = "terminal";
        public string Strategy { get; internal set; } = "non-owning-attachment-only";
        public bool WorkerAcknowledged { get; internal set; } = true;
        public bool Detached { get; internal set; }
        public int? ProcessId { get; internal set; }
        public bool SavedProject { get; internal set; }
        public bool ClosedProject { get; internal set; }
        public string LaunchMode { get; internal set; } = "never";
    }
}
