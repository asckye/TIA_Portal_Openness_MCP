using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    // No native getters and no arguments/content: logging itself cannot re-enter Openness or expose credentials.
    internal static class InvocationJournal
    {
        private static readonly object Sync = new object();
        private static readonly string ProcessKey = Process.GetCurrentProcess().Id + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        internal static string Begin(string name)
        {
            string id = Guid.NewGuid().ToString("N"); Write(id, name, "BEFORE"); return id;
        }
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
