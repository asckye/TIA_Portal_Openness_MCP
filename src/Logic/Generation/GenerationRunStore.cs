using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaOpenness.Shared;

namespace TiaMcp.Logic.Generation
{
    // A durable intent row precedes every tool dispatch. An interrupted intent is never replayed.
    public sealed class GenerationRunStore
    {
        private readonly string root;
        public GenerationRunStore(string root) { this.root = Path.GetFullPath(root); }
        public string Begin(JsonObject run)
        {
            string id = Guid.NewGuid().ToString("N");
            run["schema"] = "tiamcp.run/1"; run["runId"] = id; run["event"] = "begin";
            Append(id, run, true);
            return id;
        }
        public void Append(string runId, JsonObject row) => Append(runId, row, false);
        private void Append(string runId, JsonObject row, bool create)
        {
            var path = RunPath(runId);
            SafeRoot();
            using (JournalFileLock.Acquire(Path.Combine(root, ".runs.lock")))
            {
                if (!create && !File.Exists(path)) throw new ArgumentException("Unknown generation run.", "runId");
                RejectLink(path);
                var copy = (JsonObject)row.DeepClone();
                copy["utc"] = DateTimeOffset.UtcNow.ToString("O");
                var bytes = new UTF8Encoding(false).GetBytes(copy.ToJsonString() + "\n");
                using var output = new FileStream(path, create ? FileMode.CreateNew : FileMode.Append, FileAccess.Write, FileShare.Read);
                output.Write(bytes, 0, bytes.Length); output.Flush(true);
            }
        }
        public JsonObject Read(string runId)
        {
            var path = RunPath(runId);
            SafeRoot();
            if (!File.Exists(path)) throw new ArgumentException("Unknown generation run.", "runId");
            RejectLink(path);
            using var locked = JournalFileLock.Acquire(Path.Combine(root, ".runs.lock"), readOnly: true);
            var lines = File.ReadAllLines(path, new UTF8Encoding(false, true));
            var events = new JsonArray();
            bool interrupted = false;
            for (int i = 0; i < lines.Length; i++)
            {
                try { events.Add(JsonNode.Parse(lines[i])!.AsObject()); }
                catch (System.Text.Json.JsonException ex) when (i == lines.Length - 1)
                { interrupted = true; events.Add(new JsonObject { ["event"] = "interrupted-tail", ["error"] = ex.Message }); }
            }
            if (events.FirstOrDefault() is not JsonObject begin) throw new IOException("Generation record has no complete begin row.");
            var result = (JsonObject)begin.DeepClone(); result.Remove("event");
            var steps = result["steps"]!.AsArray();
            foreach (var step in steps.OfType<JsonObject>())
            {
                string id = (string)step["id"]!;
                var start = events.OfType<JsonObject>().LastOrDefault(e => (string?)e["stepId"] == id && (string?)e["event"] == "step-start");
                var end = events.OfType<JsonObject>().LastOrDefault(e => (string?)e["stepId"] == id && (string?)e["event"] == "step-end");
                step["status"] = end?["status"]?.DeepClone() ?? JsonValue.Create(start == null ? "NOT_EXECUTED" : "unknown");
                step["requestId"] = (end ?? start)?["requestId"]?.DeepClone();
                step["result"] = end?["result"]?.DeepClone();
            }
            var completed = events.OfType<JsonObject>().LastOrDefault(e => (string?)e["event"] == "end");
            result["status"] = completed?["status"]?.DeepClone() ?? JsonValue.Create("unknown");
            result["stoppingReason"] = completed?["stoppingReason"]?.DeepClone() ?? JsonValue.Create("host-interrupted; inspect project and replan; never replay this run");
            result["createdProjects"] = new JsonArray(events.OfType<JsonObject>().Where(e => (string?)e["event"] == "transition")
                .Select(e => (string?)e["createdProject"]).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
            result["interruptedTail"] = interrupted;
            result["events"] = events;
            return result;
        }
        public System.Collections.Generic.Dictionary<string, string>? Lineage(string project, string release)
        {
            if (!Directory.Exists(root)) return null;
            SafeRoot();
            JsonObject? latest = null;
            foreach (var file in Directory.GetFiles(root, "*.jsonl"))
            {
                var run = Read(Path.GetFileNameWithoutExtension(file));
                if (!string.Equals(((string?)run["project"])?.Replace('\\', '/'), project.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) || (string?)run["release"] != release) continue;
                // Known pre-operation refusals did not consume this plan. An interrupted intent did.
                if (!run["steps"]!.AsArray().Any(s => (string?)s!["status"] is not ("NOT_EXECUTED" or "rejected-before-operation" or "diverged" or "cancelled" or "cap"))) continue;
                if (latest == null || string.CompareOrdinal((string?)run["utc"], (string?)latest["utc"]) > 0) latest = run;
            }
            return latest == null ? null : new System.Collections.Generic.Dictionary<string, string> { ["runId"] = (string)latest["runId"]!, ["status"] = (string)latest["status"]! };
        }
        public ClaimScope Claim(string planId)
        {
            if (!Regex.IsMatch(planId, @"\Agen-[0-9a-f]{64}\z")) throw new ArgumentException("Invalid plan identity.", "planId");
            SafeRoot();
            string path = Path.Combine(root, planId + ".consumed");
            RejectLink(path);
            try
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                file.Flush(true);
                return new ClaimScope(path);
            }
            catch (IOException ex) when (File.Exists(path)) { throw new ArgumentException("Plan was already applied; observe the project and replan.", "planId", ex); }
        }
        public sealed class ClaimScope : IDisposable
        {
            private readonly string path;
            private bool consumed;
            internal ClaimScope(string path) { this.path = path; }
            public void Consume() { consumed = true; }
            public void Dispose() { if (!consumed) { RejectLink(path); File.Delete(path); } }
        }
        private string RunPath(string id)
        {
            if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid generation run identity.", "runId");
            return Path.Combine(root, id + ".jsonl");
        }
        private void SafeRoot()
        {
            for (var directory = new DirectoryInfo(root); directory != null; directory = directory.Parent) RejectLink(directory.FullName);
            Directory.CreateDirectory(root);
        }
        private static void RejectLink(string path)
        {
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Generation records refuse links/reparse points.");
        }
    }
}
