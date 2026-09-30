using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace TiaMcpServer.ModelContextProtocol
{
    // No native getters and no arguments/content: logging itself cannot re-enter Openness or expose credentials.
    internal static class InvocationJournal
    {
        private static readonly object Sync = new object();
        private static readonly string ProcessKey = Process.GetCurrentProcess().Id + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        private static readonly AsyncLocal<string?> Current = new AsyncLocal<string?>();
        internal static string Begin(string name)
        {
            string id = Guid.NewGuid().ToString("N"); Current.Value = id; Write(id, name, "BEFORE"); return id;
        }
        internal static T Native<T>(string stage, Func<T> call)
        {
            string id = Current.Value ?? Guid.NewGuid().ToString("N");
            Write(id, "native:" + stage, "BEFORE");
            try { T result = call(); Write(id, "native:" + stage, "RETURNED"); return result; }
            catch { Write(id, "native:" + stage, "THREW"); throw; }
        }
        internal static void Native(string stage, Action call) => Native(stage, () => { call(); return true; });
        internal static void Write(string id, string name, string phase)
        {
            try
            {
                lock (Sync)
                {
                    var root = Environment.GetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY");
                    if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TiaMcp", "diagnostics");
                    if (!Path.IsPathRooted(root)) return;
                    Directory.CreateDirectory(root);
                    string path = Path.Combine(root, "calls-" + ProcessKey + ".jsonl");
                    if (File.Exists(path) && new FileInfo(path).Length > 10 * 1024 * 1024)
                    { string previous = path + ".previous"; if (File.Exists(previous)) File.Delete(previous); File.Move(path, previous); }
                    string row = new JsonObject { ["utc"] = DateTime.UtcNow.ToString("O"), ["id"] = id, ["tool"] = name, ["phase"] = phase }.ToJsonString();
                    // Flush BEFORE before calling into native code, even if the native process later crashes.
                    using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) { writer.WriteLine(row); writer.Flush(); stream.Flush(true); }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("Invocation journal unavailable: " + ex.GetType().Name); }
        }
    }
}
