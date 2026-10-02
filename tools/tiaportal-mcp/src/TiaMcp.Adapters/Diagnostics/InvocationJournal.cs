// Portable serializer variant of the existing diagnostic runtime; shared by all eight adapters.
// Newtonsoft.Json preserves the actual net461 target; no Siemens reference or SDK stub.
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using System.Threading;

namespace TiaMcpServer.ModelContextProtocol
{
    // No native getters and no arguments/content: logging itself cannot re-enter Openness or expose credentials.
    internal static class InvocationJournal
    {
        private static readonly object Sync = new object();
        private static readonly int ProcessId = Process.GetCurrentProcess().Id;
        private static readonly string ProcessKey = ProcessId + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        private static readonly AsyncLocal<string?> Current = new AsyncLocal<string?>();
        internal static string CorrelationId => Current.Value ?? (Current.Value = Guid.NewGuid().ToString("N"));
        internal static Func<JObject?>? BindingSnapshot;
        private static long failedWrites;
        private static string? lastWriteFailure;
        internal static JObject Health() { lock (Sync) return new JObject { ["failedWrites"] = failedWrites, ["lastFailure"] = lastWriteFailure, ["durability"] = "best-effort; successful writes flushed to disk",
            ["nativeBoundaryCoverage"] = typeof(InvocationJournal).Assembly.GetType("TiaMcpServer.Diagnostics.GeneratedNativeCalls", false) != null ? "build-instrumented" : "not-instrumented" }; }
        internal static string Begin(string name, string? correlation = null)
        {
            string id = Guid.TryParseExact(correlation, "N", out var parsed) ? parsed.ToString("N") : Guid.NewGuid().ToString("N");
            Current.Value = id; Write(id, name, "BEFORE"); return id;
        }
        internal static T Native<T>(string stage, Func<T> call, string? objectType = null, string? objectPath = null)
        {
            string id = Current.Value ?? Guid.NewGuid().ToString("N");
            Write(id, "native:" + stage, "BEFORE", objectType, objectPath);
            try { T result = call(); Write(id, "native:" + stage, "RETURNED", objectType, objectPath); return result; }
            catch (Exception ex) { _ = PortalFailureClassifier.IsPortalProcessLost(ex); Write(id, "native:" + stage, "THREW", objectType, objectPath); throw; }
        }
        internal static void Native(string stage, Action call, string? objectType = null, string? objectPath = null) => Native(stage, () => { call(); return true; }, objectType, objectPath);
        internal static void Write(string id, string name, string phase, string? objectType = null, string? objectPath = null, JObject? details = null)
        {
            try
            {
                lock (Sync)
                {
                    var root = Environment.GetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY");
                    if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TiaMcp", "diagnostics");
                    if (!Path.IsPathRooted(root)) throw new IOException("Diagnostic path must be absolute.");
                    Directory.CreateDirectory(root);
                    string path = Path.Combine(root, "calls-" + ProcessKey + ".jsonl");
                    if (File.Exists(path) && new FileInfo(path).Length > 10 * 1024 * 1024)
                    { string previous = path + ".previous"; if (File.Exists(previous)) File.Delete(previous); File.Move(path, previous); }
                    var entry = new JObject { ["utc"] = DateTime.UtcNow.ToString("O"), ["id"] = id, ["tool"] = name, ["phase"] = phase, ["mcpProcessId"] = ProcessId,
                        ["objectType"] = objectType, ["objectPath"] = objectPath, ["binding"] = BindingSnapshot?.Invoke(),
                        ["threadId"] = Thread.CurrentThread.ManagedThreadId, ["apartment"] = Thread.CurrentThread.GetApartmentState().ToString() };
                    if (details != null) foreach (var pair in details.Properties()) entry[pair.Name] = pair.Value.DeepClone();
                    string row = entry.ToString(Newtonsoft.Json.Formatting.None);
                    // Flush BEFORE before calling into native code, even if the native process later crashes.
                    using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) { writer.WriteLine(row); writer.Flush(); stream.Flush(true); }
                }
            }
            catch (Exception ex) { lock (Sync) { failedWrites++; lastWriteFailure = ex.GetType().Name; } try { Console.Error.WriteLine("Invocation journal unavailable: " + ex.GetType().Name); } catch { } }
        }
    }
}
