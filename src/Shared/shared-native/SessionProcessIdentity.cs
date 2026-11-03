#if WORKER_SESSION_LIFECYCLE
using System;
using System.Diagnostics;

// The original engine member moves with its declaration; the adapter compiles
// the same OS probe body in its shared primitive type.
namespace TiaMcp.Adapters.Session
{
    public static partial class SessionCallbackPrimitives
    {
        public static long ProcessStart(int pid)
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) throw new InvalidOperationException("TIA process exited.");
            return process.StartTime.ToUniversalTime().Ticks;
        }
    }
}
#endif
