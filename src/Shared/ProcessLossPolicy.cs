using System;

namespace TiaOpenness.Shared
{
    // Dependency-free so the Studio journal classifier shares the hosts' process-loss rule.
    internal static class ProcessLossPolicy
    {
        internal static string? Reason(Exception? error)
        {
            for (var cause = error; cause != null; cause = cause.InnerException)
            {
                string name = cause.GetType().FullName ?? "";
                foreach (string pattern in new[] { "NonRecoverable", "System.Runtime.InteropServices.COMException", "RemotingException", "CommunicationObjectFaultedException", "CommunicationObjectAbortedException" })
                    if (name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0) return name;
            }
            return null;
        }
    }
}
