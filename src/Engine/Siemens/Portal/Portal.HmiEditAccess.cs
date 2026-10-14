#if TIA_ENGINE_LOCAL_PRIMITIVES
using SessionCallbackPrimitives = TiaMcp.Shared.Native.SessionCallbackPrimitives;
#else
using SessionCallbackPrimitives = TiaMcp.Adapters.Session.SessionCallbackPrimitives;
#endif
using System;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        internal Func<bool>? EngineeringTransactionActive { get; set; }
        private IDisposable AcquireHmiEditAccess()
        {
            // inside RunToolsInTransaction the transaction's own ExclusiveAccess is ambient; reuse it instead of nesting.
            // Keep preview read-only. For a real deletion/archive, acquire TIA exclusive
            // access before reading the guard state so UI edits cannot race that check.
            var ambient = SessionCallbackPrimitives.BeginEditAccess(_portal != null,
                () => EnsureBoundProjectUnchanged("Write"), () => EngineeringTransactionActive?.Invoke() == true);
            return ambient ?? _portal!.ExclusiveAccess("MCP: verifying a precise HMI operation");
        }
    }
}
