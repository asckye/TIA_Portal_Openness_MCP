#if WORKER_SESSION_LIFECYCLE || TIA_ENGINE_LOCAL_PRIMITIVES
using System;
using System.Diagnostics;

// The original engine member moves with its declaration; the adapter compiles
// the same OS probe body in its shared primitive type.
#if TIA_ENGINE_LOCAL_PRIMITIVES
namespace TiaMcpServer.Siemens
#else
namespace TiaMcp.Adapters.Session
#endif
{
#if TIA_ENGINE_LOCAL_PRIMITIVES
    public partial class Portal
#else
    public static partial class SessionCallbackPrimitives
#endif
    {
#if TIA_ENGINE_LOCAL_PRIMITIVES
        internal static long ProcessStart(int pid)
#else
        public static long ProcessStart(int pid)
#endif
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) throw new InvalidOperationException("TIA process exited.");
            return process.StartTime.ToUniversalTime().Ticks;
        }
    }
}
#endif
