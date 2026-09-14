using System;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private IDisposable AcquireHmiEditAccess()
        {
            if (_portal == null) throw new PortalException(PortalErrorCode.InvalidState,"TIA session unavailable.");
            // Keep preview read-only. For a real deletion/archive, acquire TIA exclusive
            // access before reading the guard state so UI edits cannot race that check.
            return _portal.ExclusiveAccess("MCP: verifying a precise HMI operation");
        }
    }
}
