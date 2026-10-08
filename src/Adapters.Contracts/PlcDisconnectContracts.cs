using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters
{
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
