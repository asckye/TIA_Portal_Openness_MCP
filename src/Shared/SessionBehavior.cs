namespace TiaOpenness.Shared
{
    internal static class SessionBehavior
    {
        internal const string PortalRequired = "No TIA Portal is connected. Call ConnectPortal with an explicitly selected processId first.";
        // The process lease refusals are authored admission text: the host passes them through to the caller.
        internal const string LeaseReserved =
            "TIA instance is already reserved by another MCP, or its lease is inaccessible. Use a separate TIA instance; no attachment attempted.";
    }
}
