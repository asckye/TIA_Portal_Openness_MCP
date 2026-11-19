using System;

namespace TiaOpenness.Shared
{
    // Dependency-free so the Studio journal classifier shares the hosts' process-loss rule.
    internal static class ProcessLossPolicy
    {
        private static readonly string[] ChannelLossNames = {
            "NonRecoverable", "RemotingException", "CommunicationObjectFaultedException", "CommunicationObjectAbortedException"
        };

        internal static bool IsComException(string reason)
            => reason.IndexOf("System.Runtime.InteropServices.COMException", StringComparison.OrdinalIgnoreCase) >= 0;

        internal static bool IsConfirmedLoss(string? reason)
        {
            if (reason == null) return false;
            if (reason == "tia-process-exited") return true;
            foreach (var name in ChannelLossNames)
                if (reason.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        internal static string? Reason(Exception? error)
        {
            string? comReason = null;
            for (var cause = error; cause != null; cause = cause.InnerException)
            {
                string name = cause.GetType().FullName ?? "";
                if (IsComException(name)) comReason ??= name;
                if (IsConfirmedLoss(name)) return name;
            }
            return comReason;
        }
    }
}
