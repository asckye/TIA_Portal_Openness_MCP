using System;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private sealed class AmbientAccess : IDisposable { public void Dispose() { } }
        private IDisposable AcquireHmiEditAccess()
        {
            if (_portal == null) throw new PortalException(PortalErrorCode.InvalidState,"TIA session unavailable.");
            EnsureBoundProjectUnchanged("Write");
            // 2.7.33: inside RunToolsInTransaction the transaction's own ExclusiveAccess is ambient; reuse it instead of nesting.
            if (_ambientExclusiveAccess != null) return new AmbientAccess();
            // Keep preview read-only. For a real deletion/archive, acquire TIA exclusive
            // access before reading the guard state so UI edits cannot race that check.
            return _portal.ExclusiveAccess("MCP: verifying a precise HMI operation");
        }
    }
}
