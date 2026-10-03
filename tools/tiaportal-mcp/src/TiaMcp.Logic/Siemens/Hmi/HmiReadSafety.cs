using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class HmiReadSafety
    {
        internal static bool ConnectionUnavailable(Exception error)
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

        // True when the only connection-shaped failure in the chain is an Openness "disposed object" error, which TIA also
        // raises for a proxy it released after Create/Delete (2.7.30 real project: MrpDomain) while the session is fine.
        internal static bool DisposedObjectOnly(Exception error)
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

        internal static string? SkipReason(Type type, string property)
        {
            if (property == "ScriptDiagnosisOverviewText" &&
                (type.FullName == "Siemens.Engineering.HmiUnified.UI.Controls.HmiSystemDiagnosisControl"
                 || type.FullName == "Siemens.Engineering.HmiUnified.ModernUI.Controls.HmiSystemDiagnosisControl"))
                return "Read quarantined: two V21 captures failed at this getter immediately before disposed-handle errors. "
                    + "Causality is unconfirmed. Getter was not invoked; content remains unknown pending Siemens diagnosis.";
            return null;
        }

        internal static void RequireReadable(Type type, string property)
        {
            var reason = SkipReason(type, property);
            if (reason != null) throw new NotSupportedException(reason);
        }
    }

    // Local diagnostics only: no property values, script bodies or engineering calls.
    // The rolling log retains the last calls even if the TIA process exits during a getter.
    internal sealed class HmiReadTrace
    {
        private static readonly object LogGate = new object();
        private static StreamWriter? Writer;
        internal static readonly string LogPath = Path.Combine(Path.GetTempPath(), "TiaMcpServer.hmi-read.log");
        internal string OperationId { get; } = Guid.NewGuid().ToString("N");
        internal string Phase { get; private set; } = "start";
        internal string? LastAttemptedPath { get; private set; }
        internal string? LastCompletedPath { get; private set; }
        internal string? LogError { get; private set; }
        internal void Step(string phase, string path, Exception? error = null)
        {
            Phase = phase;
            if (phase == "before") LastAttemptedPath = path;
            if (phase == "after") LastCompletedPath = path;
            var row = new JsonObject { ["timestamp"] = DateTimeOffset.Now.ToString("o"),
                ["operationId"] = OperationId, ["phase"] = phase, ["path"] = path };
            if (error != null) row["exception"] = error.ToString();
            try
            {
                lock (LogGate)
                {
                    if (Writer != null && Writer.BaseStream.Length > 8 * 1024 * 1024)
                    {
                        Writer.Dispose(); Writer = null;
                        File.WriteAllText(LogPath, "", new UTF8Encoding(false));
                    }
                    Writer ??= new StreamWriter(new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
                    Writer.WriteLine(row.ToJsonString());
                }
            }
            catch (Exception ex) { LogError = ex.GetType().Name + ": " + ex.Message; }
        }
        internal void AddTo(JsonObject meta)
        {
            meta["operationId"] = OperationId; meta["diagnosticLog"] = LogPath;
            meta["phase"] = Phase; meta["lastAttemptedPath"] = LastAttemptedPath;
            meta["lastCompletedPath"] = LastCompletedPath; meta["diagnosticLogError"] = LogError;
        }
    }
}
