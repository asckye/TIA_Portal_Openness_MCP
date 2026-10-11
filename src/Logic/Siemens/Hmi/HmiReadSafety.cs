using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class HmiReadSafety
    {
        internal static bool ConnectionUnavailable(Exception error)
            => TiaMcp.Adapters.Contracts.HmiReadPolicy.ConnectionUnavailable(error);

        // True when the only connection-shaped failure in the chain is an Openness "disposed object" error, which TIA also
        // raises for a proxy it released after Create/Delete (real-project observation: MrpDomain) while the session is fine.
        internal static bool DisposedObjectOnly(Exception error)
            => TiaMcp.Adapters.Contracts.HmiReadPolicy.DisposedObjectOnly(error);

        internal static string? SkipReason(Type type, string property)
            => TiaMcp.Adapters.Contracts.HmiReadPolicy.SkipReason(type, property);

        internal static void RequireReadable(Type type, string property)
            => TiaMcp.Adapters.Contracts.HmiReadPolicy.RequireReadable(type, property);
    }

    // Local diagnostics only: no property values, script bodies or engineering calls.
    // The rolling log retains the last calls even if the TIA process exits during a getter.
    internal sealed class HmiReadTrace
    {
        private static readonly object LogGate = new object();
        private static StreamWriter? Writer;
        internal static string LogPath => TiaOpenness.Shared.DataLocations.Current.LogFile("TiaMcpServer.hmi-read.log", TiaOpenness.Shared.DataLocations.HostReleaseKey);
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
                WriteLine(LogPath, row.ToJsonString());
            }
            catch (Exception ex) { LogError = "IO_FAILED: " + ex.Message; TiaOpenness.Shared.DataLocations.ReportLogFailure("TiaMcpServer.hmi-read", ex); }
        }
        internal static void WriteLine(string path, string row)
        {
            lock (LogGate)
            {
                if (Writer != null && Writer.BaseStream.Length > 8 * 1024 * 1024)
                {
                    Writer.Dispose(); Writer = null;
                    File.WriteAllText(path, "", new UTF8Encoding(false));
                }
                Writer ??= new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
                Writer.WriteLine(row);
            }
        }
        internal void AddTo(JsonObject meta)
        {
            meta["operationId"] = OperationId;
            try { meta["diagnosticLog"] = LogPath; }
            catch (Exception ex) { meta["diagnosticLog"] = null; LogError = "IO_FAILED: " + ex.Message; TiaOpenness.Shared.DataLocations.ReportLogFailure("TiaMcpServer.hmi-read", ex); }
            meta["phase"] = Phase; meta["lastAttemptedPath"] = LastAttemptedPath;
            meta["lastCompletedPath"] = LastCompletedPath; meta["diagnosticLogError"] = LogError;
        }
    }
}
