using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace LibraryRenameProbe
{
    internal sealed class PortalIdentity
    {
        public int pid { get; set; }
        public string startUtc { get; set; } = "";
    }

    internal sealed class CampaignStageResult
    {
        internal int ExitCode;
        internal bool TimedOut;
        internal string Stdout = "", Stderr = "";
    }

    internal interface ICampaignHost
    {
        IReadOnlyList<PortalIdentity> ExistingPortals();
        bool ExistingPortalIsSame(PortalIdentity identity);
        CampaignStageResult RunStage(string executable, string stage, string testCase, string output, string source, int timeoutSeconds);
        bool WaitOwnedProcessExit(PortalIdentity identity, TimeSpan timeout);
        object CollectApplicationEvents(DateTime campaignStart);
    }

    internal static class CampaignRunner
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        internal static int Run(string executable, string sourceLibrary, string resultsRoot, int timeoutSeconds, ICampaignHost host)
        {
            if (timeoutSeconds < 60 || timeoutSeconds > 1800) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
            if (!File.Exists(executable)) throw new FileNotFoundException("Extract the complete compiled package before running.", executable);
            if (!File.Exists(sourceLibrary)) throw new FileNotFoundException("Diagnostic source library is missing: " + sourceLibrary, sourceLibrary);
            if (sourceLibrary.IndexOf('"') >= 0) throw new ArgumentException("Quote characters in paths are refused.");
            sourceLibrary = Path.GetFullPath(sourceLibrary);
            string campaign = NewCampaignPath(resultsRoot);
            Directory.CreateDirectory(campaign);
            var records = new List<Dictionary<string, object?>>();
            IReadOnlyList<PortalIdentity> foreign = host.ExistingPortals();
            Write(Path.Combine(campaign, "existing-portals.json"), foreign);
            DateTime campaignStart = DateTime.Now;
            string? stopReason = null;
            try
            {
                Console.WriteLine("Nine isolated cases. Results: " + campaign);
                Console.WriteLine("The diagnostic library is opened read-only. Existing projects are never attached or saved.");
                Console.WriteLine("Native projects use short per-case paths under %LOCALAPPDATA%\\TRP; logs and exported scripts remain in this results folder.");
                foreach (string testCase in Options.Cases)
                {
                    string output = Path.Combine(campaign, testCase);
                    var prepared = InvokeStage(host, executable, "prepare", testCase, output, sourceLibrary, timeoutSeconds, foreign, campaign, records);
                    if (prepared.Stage.ExitCode != 0 || !(prepared.Record["result"] is Dictionary<string, object?> prepResult && IsStatus(prepResult, "PREPARED")))
                        throw new InvalidOperationException("Preparation failed for " + testCase + "; remaining cases were not run.");
                    var tested = InvokeStage(host, executable, "run", testCase, output, sourceLibrary, timeoutSeconds, foreign, campaign, records);
                    if (testCase == "control" && (tested.Stage.ExitCode != 0 || !(tested.Record["result"] is Dictionary<string, object?> testResult && IsStatus(testResult, "CONTROL_PASSED"))))
                        throw new InvalidOperationException("Read/save/reopen control failed; rename cases were not run.");
                }
            }
            catch (Exception ex)
            {
                stopReason = ex.Message;
                Console.WriteLine("WARNING: " + stopReason);
            }
            finally
            {
                try { Write(Path.Combine(campaign, "application-events.json"), host.CollectApplicationEvents(campaignStart)); }
                catch (Exception ex) { Write(Path.Combine(campaign, "event-collection-note.txt"), ex.Message); }
                Write(Path.Combine(campaign, "summary.json"), new
                {
                    schemaVersion = 2, sourceLibrary, completedStages = records.Count, attemptedStages = records.Count,
                    executionStagesAttempted = records.Count(record => (string)record["stage"]! == "run"),
                    stopReason, note = "Campaign completion is not proof that rename passed. Read each case status; CLONE_RENAMED_PERSISTED has a different type GUID.",
                    finishedUtc = DateTime.UtcNow.ToString("o")
                });
                Console.WriteLine("Evidence retained at " + campaign);
            }
            if (stopReason != null) return 2;
            return records.Any(record => Convert.ToInt32(record["exitCode"]) != 0) ? 1 : 0;
        }

        private static string NewCampaignPath(string root)
        {
            string fullRoot = Path.GetFullPath(root);
            string candidate = Path.Combine(fullRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            if (File.Exists(candidate) || Directory.Exists(candidate)) throw new IOException("Output directory already exists; existing evidence is never overwritten: " + candidate);
            return candidate;
        }

        private static (CampaignStageResult Stage, Dictionary<string, object?> Record) InvokeStage(ICampaignHost host, string executable,
            string stage, string testCase, string output, string source, int timeout, IReadOnlyList<PortalIdentity> foreign,
            string campaign, List<Dictionary<string, object?>> records)
        {
            AssertExistingPortals(host, foreign);
            if (output.IndexOf('"') >= 0) throw new ArgumentException("Quote characters in paths are refused.");
            Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + testCase + " / " + stage);
            var result = host.RunStage(executable, stage, testCase, output, source, timeout);
            string resultPath = Path.Combine(output, stage + "-result.json");
            var document = File.Exists(resultPath) ? Json.Deserialize<Dictionary<string, object?>>(File.ReadAllText(resultPath, Encoding.UTF8)) : null;
            var record = new Dictionary<string, object?>
            {
                ["case"] = testCase, ["stage"] = stage, ["exitCode"] = result.ExitCode, ["timedOut"] = result.TimedOut,
                ["result"] = document, ["ownedProcessExitedVerified"] = false, ["existingPortalsPreserved"] = false
            };
            records.Add(record);
            Write(Path.Combine(campaign, "cases.json"), records);
            if (result.ExitCode != 0 || document == null || IsStatus(document, "FAILED"))
            {
                string detail = document == null ? result.Stderr : (String(document, "error") ?? "");
                if (!string.IsNullOrEmpty(detail)) Console.WriteLine("Failure details:" + Environment.NewLine + detail);
                else if (!string.IsNullOrEmpty(result.Stderr)) Console.WriteLine("Probe stderr:" + Environment.NewLine + result.Stderr);
                Console.WriteLine("Result file: " + resultPath);
                string eventsPath = Path.Combine(output, stage + "-events.jsonl");
                if (File.Exists(eventsPath)) Console.WriteLine("Native call log: " + eventsPath);
            }
            if (result.TimedOut) throw new InvalidOperationException("Probe timed out. Owned TIA state is uncertain; no additional cases will start. Retain logs and inspect the recorded PID.");
            if (document == null) throw new InvalidOperationException("Probe exited without a result. No additional cases will start.");

            string identityPath = Path.Combine(output, stage + "-owned-process.json");
            if (!File.Exists(identityPath)) throw new InvalidOperationException("Owned TIA process identity was not recorded; campaign stopped.");
            var identity = Json.Deserialize<PortalIdentity>(File.ReadAllText(identityPath, Encoding.UTF8));
            if (identity == null) throw new InvalidOperationException("Owned TIA process identity was unreadable; campaign stopped.");
            bool exited = host.WaitOwnedProcessExit(identity, TimeSpan.FromSeconds(20));
            if (!exited) throw new InvalidOperationException("Owned TIA PID " + identity.pid + " is still alive. Campaign stopped; no automatic TIA termination.");
            AssertExistingPortals(host, foreign);
            record["ownedProcessExitedVerified"] = true;
            record["existingPortalsPreserved"] = true;
            Write(Path.Combine(campaign, "cases.json"), records);
            Console.WriteLine("  " + String(document, "status"));
            return (result, record);
        }

        private static void AssertExistingPortals(ICampaignHost host, IReadOnlyList<PortalIdentity> foreign)
        {
            foreach (var portal in foreign)
                if (!host.ExistingPortalIsSame(portal))
                    throw new InvalidOperationException("An existing TIA process changed or exited (PID " + portal.pid + "); campaign stopped. No process was killed by this runner.");
        }

        private static bool IsStatus(Dictionary<string, object?>? record, string expected)
            => record != null && string.Equals(String(record, "status"), expected, StringComparison.Ordinal);

        private static string? String(Dictionary<string, object?>? record, string key)
            => record != null && record.TryGetValue(key, out var value) ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : null;

        private static void Write(string path, object? value)
            => File.WriteAllText(path, Json.Serialize(value), Utf8NoBom);

        internal static void SelfTest()
        {
            string root = Path.Combine(Path.GetTempPath(), "trp-supervisor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string executable = Path.Combine(root, "LibraryRenameProbe.exe");
                string source = Path.Combine(root, "MCP_GeneralScripts_Rename_fixture.al21");
                File.WriteAllText(executable, "offline fixture");
                File.WriteAllText(source, "offline fixture; never passed to Siemens");
                foreach (string mode in new[] { "matrix", "prepare-failure", "missing", "alive" })
                {
                    var host = new FakeCampaignHost(mode);
                    var console = new StringWriter();
                    TextWriter originalConsole = Console.Out;
                    int result;
                    try { Console.SetOut(console); result = Run(executable, source, Path.Combine(root, mode), 60, host); }
                    finally { Console.SetOut(originalConsole); }
                    string campaign = Directory.GetDirectories(Path.Combine(root, mode)).Single();
                    var summary = Json.Deserialize<Dictionary<string, object?>>(File.ReadAllText(Path.Combine(campaign, "summary.json"), Encoding.UTF8))!;
                    var cases = Json.Deserialize<List<Dictionary<string, object?>>>(File.ReadAllText(Path.Combine(campaign, "cases.json"), Encoding.UTF8))!;
                    int expectedStages = mode == "matrix" ? 18 : 1;
                    int expectedExit = mode == "matrix" ? 1 : 2;
                    Check(cases.Count == expectedStages && Convert.ToInt32(summary["completedStages"]) == expectedStages && result == expectedExit,
                        "Unexpected supervisor stage count or exit for " + mode + ": cases=" + cases.Count + ", completed=" +
                        Convert.ToString(summary["completedStages"]) + ", exit=" + result + ", stop=" + Convert.ToString(summary["stopReason"]));
                    if (mode == "matrix")
                    {
                        Check(summary["stopReason"] == null, "Recoverable isolated failure stopped the campaign");
                        Check(cases.All(record => (bool)record["ownedProcessExitedVerified"]! && (bool)record["existingPortalsPreserved"]!),
                            "Process identity checks were not recorded");
                        Check(cases.Any(record => record["result"] is Dictionary<string, object?> document &&
                            Convert.ToString(document["detail"]) == "path limit: 路径"), "UTF-8 result detail changed");
                    }
                    else
                    {
                        Check(summary["stopReason"] != null, "Unsafe supervisor state did not stop the campaign");
                        Check(Convert.ToInt32(summary["executionStagesAttempted"]) == 0, "Failed preparation was counted as rename execution");
                    }
                    if (mode == "alive") Check(Convert.ToString(summary["stopReason"])!.IndexOf("still alive", StringComparison.Ordinal) >= 0,
                        "Owned process guard did not identify the live fixture");
                    if (mode == "missing") Check(Convert.ToString(summary["stopReason"])!.IndexOf("without a result", StringComparison.Ordinal) >= 0,
                        "Missing child result did not stop the campaign");
                    if (mode == "matrix" || mode == "prepare-failure") Check(console.ToString().IndexOf("SIMULATED_NATIVE_EXCEPTION", StringComparison.Ordinal) >= 0,
                        "Native failure details were hidden from the console");
                    if (mode == "missing") Check(console.ToString().IndexOf("SIMULATED_BOOTSTRAP_EXCEPTION", StringComparison.Ordinal) >= 0,
                        "Bootstrap stderr was hidden from the console");
                }
                Console.WriteLine("Four offline supervisor scenarios passed; native execution NOT RUN.");
            }
            finally
            {
                string resolved = Path.GetFullPath(root);
                string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || resolved == temp.TrimEnd(Path.DirectorySeparatorChar))
                    throw new IOException("Unsafe supervisor fixture cleanup path.");
                if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
            }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Supervisor self-test failed: " + message);
        }

        private sealed class FakeCampaignHost : ICampaignHost
        {
            private readonly string mode;
            private readonly PortalIdentity foreign = new PortalIdentity { pid = 8675309, startUtc = "fixture" };
            internal FakeCampaignHost(string mode) => this.mode = mode;
            public IReadOnlyList<PortalIdentity> ExistingPortals() => new[] { foreign };
            public bool ExistingPortalIsSame(PortalIdentity identity) => identity.pid == foreign.pid && identity.startUtc == foreign.startUtc;
            public CampaignStageResult RunStage(string executable, string stage, string testCase, string output, string source, int timeoutSeconds)
            {
                Directory.CreateDirectory(output);
                const int fakePid = 8675310;
                Write(Path.Combine(output, stage + "-owned-process.json"), new PortalIdentity { pid = fakePid, startUtc = "fixture-owned" });
                if (mode == "missing") return new CampaignStageResult { ExitCode = 1, Stderr = "SIMULATED_BOOTSTRAP_EXCEPTION" };
                bool fail = mode == "prepare-failure" || mode == "matrix" && stage == "run" && testCase == "property";
                string status = fail ? "FAILED" : stage == "prepare" ? "PREPARED" : testCase == "control" ? "CONTROL_PASSED" : "SIMULATED_SUCCESS";
                Write(Path.Combine(output, stage + "-result.json"), new Dictionary<string, object?>
                {
                    ["status"] = status, ["nativeExecuted"] = false, ["simulation"] = true,
                    ["detail"] = "path limit: 路径", ["error"] = fail ? "SIMULATED_NATIVE_EXCEPTION" : null
                });
                return new CampaignStageResult { ExitCode = fail ? 1 : 0, Stderr = fail ? "SIMULATED_NATIVE_EXCEPTION" : "" };
            }
            public bool WaitOwnedProcessExit(PortalIdentity identity, TimeSpan timeout) => mode != "alive";
            public object CollectApplicationEvents(DateTime campaignStart) => Array.Empty<object>();
        }
    }

    internal sealed class WindowsCampaignHost : ICampaignHost
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };

        public IReadOnlyList<PortalIdentity> ExistingPortals()
        {
            var rows = new List<PortalIdentity>();
            foreach (Process process in Process.GetProcessesByName("Siemens.Automation.Portal"))
            {
                using (process) rows.Add(new PortalIdentity { pid = process.Id, startUtc = process.StartTime.ToUniversalTime().ToString("o") });
            }
            return rows;
        }

        public bool ExistingPortalIsSame(PortalIdentity identity)
        {
            try
            {
                using (var process = Process.GetProcessById(identity.pid))
                    return process.StartTime.ToUniversalTime().ToString("o") == identity.startUtc;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        public CampaignStageResult RunStage(string executable, string stage, string testCase, string output, string source, int timeoutSeconds)
        {
            Directory.CreateDirectory(output);
            string stdoutPath = Path.Combine(Path.GetDirectoryName(output)!, testCase + "-" + stage + ".stdout.txt");
            string stderrPath = Path.Combine(Path.GetDirectoryName(output)!, testCase + "-" + stage + ".stderr.txt");
            var args = new[] { "--stage", stage, "--case", testCase, "--output", output, "--source", source };
            var start = new ProcessStartInfo(executable, string.Join(" ", args.Select(Quote)))
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)!,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start probe stage."))
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                DateTime deadline = DateTime.Now.AddSeconds(timeoutSeconds), nextProgress = DateTime.Now.AddSeconds(30);
                bool timedOut = false;
                while (!process.HasExited)
                {
                    if (DateTime.Now >= deadline) { process.Kill(); process.WaitForExit(); timedOut = true; break; }
                    if (DateTime.Now >= nextProgress)
                    {
                        Console.WriteLine("  Still running. If TIA shows the Openness firewall prompt, verify LibraryRenameProbe.exe and allow this test application.");
                        nextProgress = DateTime.Now.AddSeconds(30);
                    }
                    process.WaitForExit(500);
                }
                process.WaitForExit();
                string outputText = stdout.Result, errorText = stderr.Result;
                File.WriteAllText(stdoutPath, outputText, new UTF8Encoding(false));
                File.WriteAllText(stderrPath, errorText, new UTF8Encoding(false));
                return new CampaignStageResult { ExitCode = process.ExitCode, TimedOut = timedOut, Stdout = outputText, Stderr = errorText };
            }
        }

        public bool WaitOwnedProcessExit(PortalIdentity identity, TimeSpan timeout)
        {
            DateTime deadline = DateTime.Now + timeout;
            do
            {
                if (!ExistingPortalIsSame(identity)) return true;
                System.Threading.Thread.Sleep(500);
            } while (DateTime.Now < deadline);
            return !ExistingPortalIsSame(identity);
        }

        public object CollectApplicationEvents(DateTime campaignStart)
        {
            var rows = new List<object>();
            using (var log = new EventLog("Application"))
            {
                foreach (EventLogEntry entry in log.Entries)
                    if (entry.TimeGenerated >= campaignStart && new[] { 1000, 1001, 1026 }.Contains((int)entry.InstanceId)
                        && (entry.Message.IndexOf("Siemens", StringComparison.OrdinalIgnoreCase) >= 0
                            || entry.Message.IndexOf("LibraryRenameProbe", StringComparison.OrdinalIgnoreCase) >= 0))
                        rows.Add(new { timeCreated = entry.TimeGenerated, id = entry.InstanceId, provider = entry.Source, message = entry.Message });
            }
            return rows;
        }

        private static string Quote(string value)
        {
            if (value.Length > 0 && value.All(character => !char.IsWhiteSpace(character) && character != '"')) return value;
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"') { result.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
                result.Append('\\', slashes).Append(character); slashes = 0;
            }
            result.Append('\\', slashes * 2).Append('"');
            return result.ToString();
        }
    }
}
