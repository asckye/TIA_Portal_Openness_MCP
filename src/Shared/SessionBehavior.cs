namespace TiaOpenness.Shared
{
    internal static class SessionBehavior
    {
        internal const string PortalRequired = "No TIA Portal is connected. Call ConnectPortal with an explicitly selected processId first.";
        // The process lease refusals are authored admission text: the host passes them through to the caller.
        internal const string LeaseReserved =
            "TIA instance is already reserved by another MCP host, or its lease is inaccessible. End the holder's connection in its Workbench or MCP client, " +
                "then call ConnectPortal again. No attachment attempted.";
        internal static string LeaseReservedBy(int hostPid, string release)
            => "TIA instance is reserved by MCP host PID " + hostPid.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", release " + release
                    + ". End the holder's connection in that host's Workbench or MCP client, then call ConnectPortal again. No attachment attempted.";
        internal static bool IsLeaseReserved(string message)
            => message == LeaseReserved || System.Text.RegularExpressions.Regex.IsMatch(message,
                @"\ATIA instance is reserved by MCP host PID [1-9][0-9]*, release (14sp1|15\.1|16|17|18|19|20|21)\. "
                + @"End the holder's connection in that host's Workbench or MCP client, then call ConnectPortal again\. No attachment attempted\.\z");
    }
}
