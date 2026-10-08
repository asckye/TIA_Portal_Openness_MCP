using System;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;

internal static class StagingSessionChecks
{
    internal static void Run(Assembly server, Action<bool, string> check)
    {
        const BindingFlags all = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var http = server.GetType("TiaMcpServer.HttpMcpServer", true)!;
        var storeType = Program.FindServerType(server, "TiaMcp.Logic.ModelContextProtocol.ImportStagingStore");
        var fileType = Program.FindServerType(server, "TiaMcp.Logic.ModelContextProtocol.StagedTextFile");
        int checks = 0;
        void Check(bool value, string message) { check(value, message); checks++; }
        foreach (string state in new[] { "open", "closed", "expired", "restarted", "unknown", "active" })
        {
            string root = Path.Combine(Path.GetTempPath(), "tia-stg-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            object a = http.GetMethod("OpenSession", all)!.Invoke(null, null)!;
            object b = http.GetMethod("OpenSession", all)!.Invoke(null, null)!;
            string Id(object s) => (string)s.GetType().GetField("Id", all)!.GetValue(s)!;
            object Owner(object s) => s.GetType().GetField("Owner", all)!.GetValue(s)!;
            object Store(object s) => Activator.CreateInstance(storeType, root, "18", Owner(s))!;
            JsonNode List(object store) => (JsonNode)storeType.GetMethod("List")!.Invoke(store, null)!;
            JsonNode Cleanup(object store, string id, bool preview) => (JsonNode)storeType.GetMethod("Cleanup")!.Invoke(store, new object[] { id, preview })!;
            IDisposable? active = null;
            try
            {
                var old = Store(a); var current = Store(b);
                var file = Activator.CreateInstance(fileType)!;
                fileType.GetProperty("FileName")!.SetValue(file, "F.scl"); fileType.GetProperty("Kind")!.SetValue(file, "scl");
                fileType.GetProperty("Content")!.SetValue(file, "FUNCTION F : Void\nBEGIN\nEND_FUNCTION");
                var files = Array.CreateInstance(fileType, 1); files.SetValue(file, 0);
                var batch = (JsonNode)storeType.GetMethod("Stage")!.Invoke(old, new object[] { files, false })!;
                string id = (string)batch["batchId"]!, folder = (string)batch["directory"]!;
                Check((string?)batch["mcpSessionId"] == Id(a), "Manifest lost the real HTTP MCP session id.");
                string injected = (string)http.GetMethod("BindSessionMetadata", all)!.Invoke(null, new object[] {
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"ListStagedImportFiles\",\"_meta\":{\"tiaMcpStagingSession\":\"caller-peer-token\",\"other\":\"kept\"}}}", a })!;
                var meta = JsonNode.Parse(injected)!["params"]!["_meta"]!;
                Check((string?)meta["tiaMcpStagingSession"] == (string?)batch["sessionId"] && (string?)meta["other"] == "kept", "HTTP staging identity was not server-owned.");
                if (state == "active") active = (IDisposable)Owner(a).GetType().GetMethod("EnterRequest")!.Invoke(Owner(a), null)!;
                if (state is "closed" or "active") http.GetMethod("CloseSession", all)!.Invoke(null, new object[] { Id(a) });
                if (state == "expired")
                {
                    a.GetType().GetField("LastSeenUtc", all)!.SetValue(a, DateTime.UtcNow.AddHours(-3));
                    http.GetMethod("ExpireSessions", all)!.Invoke(null, new object[] { DateTime.UtcNow });
                }
                if (state is "unknown" or "restarted")
                    foreach (string name in new[] { ".staging-batch.json", ".staging-batch.previous.json" })
                    {
                        string path = Path.Combine(folder, name); var manifest = JsonNode.Parse(File.ReadAllText(path))!;
                        manifest["hostInstanceId"] = Guid.NewGuid().ToString("N");
                        if (state == "restarted") manifest["hostStartedUtc"] = DateTimeOffset.UtcNow.AddYears(-1).ToString("O");
                        File.WriteAllText(path, manifest.ToJsonString());
                    }
                bool ended = state is "closed" or "expired" or "restarted";
                Check((string?)List(current)["batches"]![0]!["ownerState"] == (ended ? "ended" : state == "unknown" ? "unknown" : "live-other"), "HTTP owner state mismatch: " + state);
                foreach (bool preview in new[] { true, false })
                {
                    bool refused = false;
                    try { Cleanup(current, id, preview); }
                    catch (TargetInvocationException error) when (error.InnerException is ArgumentException) { refused = true; }
                    Check(refused != ended, "HTTP cleanup admission mismatch: " + state);
                }
                if (state == "active") { active!.Dispose(); active = null; Cleanup(current, id, false); Check(!Directory.Exists(folder), "Closed HTTP session remained protected after its request finished."); }
            }
            finally
            {
                active?.Dispose();
                http.GetMethod("CloseSession", all)!.Invoke(null, new object[] { Id(a) });
                http.GetMethod("CloseSession", all)!.Invoke(null, new object[] { Id(b) });
                Directory.Delete(root, true);
            }
        }
        Console.WriteLine("COMPLETE: " + checks + " staging HTTP lifecycle checks passed; 0 failed; no listener or native calls");
    }
}
