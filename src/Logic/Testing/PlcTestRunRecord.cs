using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TiaMcp.Logic.Testing
{
    public sealed class PlcTestCaseResult
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Requirement { get; set; }
        public string Kind { get; set; } = "plcsimScenario";
        public string CaseSha256 { get; set; } = "";
        public string Verdict { get; set; } = "skipped";
        public long DurationMs { get; set; }
        public string Message { get; set; } = "";
        public JsonObject Evidence { get; set; } = new JsonObject();
    }

    public sealed class PlcTestRunRecord
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        public int SchemaVersion { get; set; } = 1;
        public string RunId { get; set; } = NewRunId();
        public string Status { get; set; } = "running";
        public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedUtc { get; set; }
        public string ReleaseKey { get; set; } = "";
        public string HostVersion { get; set; } = "";
        public JsonObject? Project { get; set; }
        public string PlanHash { get; set; } = "";
        public JsonObject Audit { get; set; } = new JsonObject();
        public JsonObject Selection { get; set; } = new JsonObject();
        public List<PlcTestCaseResult> Cases { get; set; } = new List<PlcTestCaseResult>();
        public JsonObject Totals => new JsonObject {
            ["passed"] = Count("passed"), ["failed"] = Count("failed"), ["errors"] = Count("error"),
            ["skipped"] = Count("skipped"), ["unknown"] = Count("unknown")
        };
        public int ExitCode => CalculateExitCode(Status, Cases.Select(c => c.Verdict));
        [JsonIgnore]
        public JsonObject Document => JsonSerializer.SerializeToNode(this, Options)!.AsObject();
        [JsonIgnore]
        public string CanonicalJson => PlcTestCaseDocument.Format(Document);
        private int Count(string verdict) => Cases.Count(c => c.Verdict == verdict);

        public static string NewRunId() => DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-", CultureInfo.InvariantCulture)
            + Guid.NewGuid().ToString("N").Substring(0, 8);

        public static int CalculateExitCode(string status, IEnumerable<string> verdicts)
        {
            var values = verdicts.ToArray();
            if (status != "completed" || values.Contains("unknown")) return 5;
            if (values.Contains("failed") || values.Contains("error")) return 3;
            return values.Contains("skipped") ? 4 : 0;
        }

        // Input is the underlying service reply (data and meta), before the MCP bridge maps it.
        public static string MapVerdict(string kind, JsonObject reply)
        {
            var meta = reply["meta"] as JsonObject;
            var data = reply["data"] as JsonObject;
            var steps = data?["steps"] as JsonArray;
            if ((bool?)meta?["outcomeUnknown"] == true || (string?)meta?["outcome"] == "unknown"
                || steps?.Any(s => (bool?)s?["outcomeUnknown"] == true) == true) return "unknown";
            if (kind == "testSuite")
            {
                if ((string?)reply["error"]?["code"] == "UNSUPPORTED_CAPABILITY"
                    || (string?)meta?["errorCode"] == "UNSUPPORTED_CAPABILITY") return "skipped";
                if ((bool?)meta?["testPassed"] == true) return "passed";
                string? state = (string?)meta?["result"]?["state"];
                return (bool?)meta?["apiCallSuccess"] == true && (state == "Error" || state == "Warning") ? "failed" : "error";
            }
            if (kind != "plcsimScenario") throw new ArgumentException("Unknown PLC test kind.", nameof(kind));
            if ((string?)data?["verdict"] == "passed" && ((bool?)reply["ok"] ?? (bool?)meta?["operationSuccess"]) == true)
                return "passed";
            var failed = steps?.Where(s => (bool?)s?["ok"] == false).ToArray() ?? Array.Empty<JsonNode?>();
            return (string?)data?["verdict"] == "failed" && failed.Length > 0 && failed.All(s => (string?)s?["kind"] == "assert")
                ? "failed" : "error";
        }

        public static PlcTestRunRecord Parse(string json)
        {
            var record = JsonSerializer.Deserialize<PlcTestRunRecord>(json, Options) ?? throw new ArgumentException("Run record is missing.");
            ValidateRunId(record.RunId);
            if (record.SchemaVersion != 1 || !new[] { "running", "completed", "stopped" }.Contains(record.Status)
                || record.Cases == null || record.Cases.Any(c => c == null || c.DurationMs < 0
                    || !new[] { "passed", "failed", "error", "skipped", "unknown" }.Contains(c.Verdict)
                    || !new[] { "plcsimScenario", "testSuite" }.Contains(c.Kind)))
                throw new ArgumentException("Invalid PLC test run record.");
            return record;
        }

        public void Save(string runsDirectory)
        {
            ValidateRunId(RunId);
            WriteAtomic(Path.Combine(runsDirectory, RunId, "run.json"), CanonicalJson);
        }

        public static PlcTestRunRecord Read(string runsDirectory, string runId)
        {
            if (runId == "latest") runId = RunIds(runsDirectory).FirstOrDefault() ?? throw new FileNotFoundException("No PLC test run exists.");
            ValidateRunId(runId);
            var record = Parse(File.ReadAllText(Path.Combine(runsDirectory, runId, "run.json"), Encoding.UTF8));
            if (record.RunId != runId) throw new ArgumentException("runId must equal its directory name.");
            return record;
        }

        public static JsonArray List(string runsDirectory) => new JsonArray(RunIds(runsDirectory).Select(id => {
            var record = Read(runsDirectory, id);
            return (JsonNode)new JsonObject { ["runId"] = id, ["status"] = record.Status,
                ["startedUtc"] = record.StartedUtc.ToString("O", CultureInfo.InvariantCulture),
                ["totals"] = record.Totals, ["exitCode"] = record.ExitCode };
        }).ToArray());

        private static IEnumerable<string> RunIds(string root) => Directory.Exists(root)
            ? Directory.GetDirectories(root).Select(Path.GetFileName).Where(id => IsRunId(id!) && File.Exists(Path.Combine(root, id!, "run.json")))
                .Select(id => id!).OrderByDescending(id => id, StringComparer.Ordinal)
            : Enumerable.Empty<string>();
        private static bool IsRunId(string id) => System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9]{8}-[0-9]{6}-[0-9a-f]{8}\\z");
        private static void ValidateRunId(string id)
        {
            if (id == null || !IsRunId(id)) throw new ArgumentException("Invalid runId.", nameof(id));
        }

        // The temporary file is in the destination directory, so replacement is atomic on the same volume.
        public static void WriteAtomic(string path, string text, bool overwrite = true, Action<string, string>? writeTemporary = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                if (writeTemporary != null) writeTemporary(temp, text);
                else File.WriteAllText(temp, text, new UTF8Encoding(false));
                // Both files inherit the same data-directory ACL; replacement need not merge file metadata.
                if (File.Exists(path) && overwrite) File.Replace(temp, path, null, ignoreMetadataErrors: true);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
