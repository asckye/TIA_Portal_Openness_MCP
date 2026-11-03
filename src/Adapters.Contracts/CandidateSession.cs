using System;

namespace TiaMcp.Adapters.Contracts.Candidates
{
    public static class SessionPrimitives
    {
        public const string ConfirmationReason = "awaiting-openness-confirmation";
        public const string ConfirmationGuidance = "Answer the TIA Portal Openness access prompt (the Openness firewall dialog: Yes / Yes to all), or have an administrator add the worker to the Openness AllowList (Workbench environment check 'Openness AllowList'), then inspect the session state and retry with a fresh session and preview.";
        public static string? TimeoutReason(bool timeout, bool matchingProcess) => timeout && matchingProcess ? ConfirmationReason : null;
        public static bool IsTimeout(Exception error)
        {
            for (Exception? current = error; current != null; current = current.InnerException)
                if (current is TimeoutException) return true;
            return false;
        }
        public static string? ExceptionReason(Exception error)
        {
            for (Exception? current = error; current != null; current = current.InnerException)
                if (current.Data["sessionReason"] is string reason && reason == ConfirmationReason) return reason;
            return null;
        }
    }
}
