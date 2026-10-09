using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcp.Logic.ModelContextProtocol;

internal static class CrashJournalTests
{
    internal static void Run(string directory)
    {
        string root = Path.Combine(directory, "killed-worker");
        Directory.CreateDirectory(root);
        var start = new ProcessStartInfo(typeof(CrashJournalTests).Assembly.Location, "before-kill")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.EnvironmentVariables["TIA_MCP_DIAGNOSTICS_DIRECTORY"] = root;
        using (var child = Process.Start(start) ?? throw new Exception("Could not start journal fixture"))
        {
            try
            {
                var ready = child.StandardOutput.ReadLineAsync();
                if (!ready.Wait(TimeSpan.FromSeconds(30)) || ready.Result != "BEFORE_READY") throw new Exception("Fixture did not flush BEFORE");
                child.Kill();
                if (!child.WaitForExit(10000)) throw new Exception("Fixture did not exit");
            }
            finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(10000); } }
        }
        // Later sessions/process journals must not hide the native worker's last call.
        for (int i = 0; i < 8; i++)
            File.WriteAllText(Path.Combine(root, "calls-later-" + i + ".jsonl"), new JsonObject
            { ["utc"] = DateTime.UtcNow.ToString("O"), ["tool"] = "host-only", ["phase"] = "RETURNED" }.ToJsonString() + "\n");
        var result = NativeJournalReader.Read(root, 100);
        var pending = result["records"]!.AsArray().Where(r => r?["nativeCallId"] != null).ToArray();
        if (pending.Length != 1 || (string?)pending[0]!["phase"] != "BEFORE" || (string?)pending[0]!["callSite"] != "before-kill-site")
            throw new Exception("Later session could not recover killed worker BEFORE");
        if ((int?)result["filesRead"] != 9) throw new Exception("Cross-session journal scan was capped");
        Console.WriteLine("PASS killed worker BEFORE is durable and recoverable from a later session");
    }
}
