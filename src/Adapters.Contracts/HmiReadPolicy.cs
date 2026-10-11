using System;
namespace TiaMcp.Adapters.Contracts
{
    public static class HmiReadPolicy
    {
        public static bool ConnectionUnavailable(Exception error)
        {
            for (var e = error; e != null; e = e.InnerException)
            {
                var name = e.GetType().Name;
                if (name == "EngineeringObjectDisposedException" || name == "ObjectDisposedException"
                    || name == "RemotingException" || name.IndexOf("NonRecoverable", StringComparison.Ordinal) >= 0)
                    return true;
                // COM failures are not proof of a process exit, but are unsafe to retry in a graph walk.
                if (e is System.Runtime.InteropServices.COMException) return true;
            }
            return false;
        }

        public static bool DisposedObjectOnly(Exception error)
        {
            bool disposed = false;
            for (var e = error; e != null; e = e.InnerException)
            {
                var name = e.GetType().Name;
                if (name == "EngineeringObjectDisposedException") { disposed = true; continue; }
                if (name == "ObjectDisposedException" || name == "RemotingException" || name.IndexOf("NonRecoverable", StringComparison.Ordinal) >= 0
                    || e is System.Runtime.InteropServices.COMException) return false;
            }
            return disposed;
        }

        public static string? SkipReason(Type type, string property)
        {
            if (property == "ScriptDiagnosisOverviewText" &&
                (type.FullName == "Siemens.Engineering.HmiUnified.UI.Controls.HmiSystemDiagnosisControl"
                 || type.FullName == "Siemens.Engineering.HmiUnified.ModernUI.Controls.HmiSystemDiagnosisControl"))
                return "Read quarantined: two V21 captures failed at this getter immediately before disposed-handle errors. "
                    + "Causality is unconfirmed. Getter was not invoked; content remains unknown pending Siemens diagnosis.";
            return null;
        }

        public static void RequireReadable(Type type, string property)
        {
            var reason = SkipReason(type, property);
            if (reason != null) throw new NotSupportedException(reason);
        }
    }
}
