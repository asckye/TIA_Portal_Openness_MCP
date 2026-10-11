using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaOpenness.Shared;

namespace TiaMcp.Logic.Generation
{
    public sealed class GenerationArtifactCleanupException : IOException
    {
        public int DeletedFiles { get; }
        internal GenerationArtifactCleanupException(int deletedFiles, Exception cause) : base(cause.Message, cause) { DeletedFiles = deletedFiles; }
    }
    // Only framework-owned SCL files are removable. Journals/claims/backups are never removed.
    public sealed class GenerationArtifacts
    {
        private readonly string root;
        private string Staging => Path.Combine(root, "artifacts", "staging");
        public GenerationArtifacts(string root) { this.root = Path.GetFullPath(root); }
        public JsonObject Manage(string action, string artifactId, bool preview, string expectedHash)
        {
            if (action != "list" && action != "cleanup") throw new ArgumentException("Choose list or cleanup.", "action");
            if (action == "list" && artifactId.Length != 0) throw new ArgumentException("list has no artifactId.", "artifactId");
            if (action == "cleanup" && !Regex.IsMatch(artifactId, @"\Agen-[0-9a-f]{64}\z")) throw new ArgumentException("Exact artifactId returned by list required.", "artifactId");
            GenerationRecordFile.RejectLinks(Staging);
            var entries = List();
            var selected = action == "list" ? entries : new JsonArray(entries.OfType<JsonObject>().Where(e => (string?)e["artifactId"] == artifactId).Select(e => (JsonNode?)e.DeepClone()).ToArray());
            if (action == "cleanup" && selected.Count != 1) throw new ArgumentException("Unknown artifactId.", "artifactId");
            if (action == "cleanup" && selected[0]!["runs"]!.AsArray().Any(r => (bool?)r!["active"] == true)) throw new ArgumentException("Artifacts referenced by an active/interrupted run must be retained.", "artifactId");
            var data = new JsonObject { ["action"] = action, ["items"] = selected, ["wouldChange"] = action == "cleanup", ["executed"] = false };
            string hash = CanonicalJson.Hash(CanonicalJson.Parse(data.ToJsonString())).Substring(7);
            data["planHash"] = hash;
            if (action == "cleanup" && !preview)
            {
                if (hash != expectedHash) throw new ArgumentException("Artifact preview changed; list and preview again.", "expectedPlanHash");
                string lockPath = Path.Combine(root, ".artifacts.lock"); GenerationRecordFile.RejectLinks(lockPath);
                using var locked = JournalFileLock.Acquire(lockPath);
                var recheck = Manage(action, artifactId, true, "");
                if ((string?)recheck["planHash"] != hash) throw new ArgumentException("Artifact evidence changed before cleanup.", "expectedPlanHash");
                Remove(selected[0]!.AsObject()); data["executed"] = true;
            }
            return data;
        }
        public void ExpireUnreferenced(DateTimeOffset now)
        {
            foreach (var entry in List().OfType<JsonObject>().Where(e => e["runs"]!.AsArray().Count == 0))
            {
                string directory = (string)entry["directory"]!;
                var modified = Directory.GetLastWriteTimeUtc(directory);
                if (entry["files"]!.AsArray().Any()) modified = entry["files"]!.AsArray().Select(f => File.GetLastWriteTimeUtc(Path.Combine(directory, (string)f!["path"]!))).Append(modified).Max();
                if (new DateTimeOffset(modified, TimeSpan.Zero).AddMinutes(10).AddHours(24) >= now) continue;
                var preview = Manage("cleanup", (string)entry["artifactId"]!, true, "");
                Manage("cleanup", (string)entry["artifactId"]!, false, (string)preview["planHash"]!);
            }
        }
        private JsonArray List()
        {
            var result = new JsonArray();
            if (!Directory.Exists(Staging)) return result;
            GenerationRecordFile.RejectLinks(Staging);
            var runsPath = Path.Combine(root, "runs");
            var store = new GenerationRunStore(runsPath);
            var runs = Directory.Exists(runsPath) ? Directory.GetFiles(runsPath, "*.jsonl").Select(p => store.Read(Path.GetFileNameWithoutExtension(p))).ToArray() : Array.Empty<JsonObject>();
            foreach (string directory in Directory.GetDirectories(Staging).OrderBy(p => p, StringComparer.Ordinal))
            {
                string id = Path.GetFileName(directory);
                if (!Regex.IsMatch(id, @"\Agen-[0-9a-f]{64}\z")) throw new IOException("Unknown directory under generation staging; retain for inspection.");
                GenerationRecordFile.RejectLinks(directory);
                if (Directory.GetDirectories(directory).Length != 0) throw new IOException("Unexpected nested generation artifact directory.");
                var files = new JsonArray();
                foreach (string file in Directory.GetFiles(directory).OrderBy(p => p, StringComparer.Ordinal))
                {
                    GenerationRecordFile.RejectLinks(file);
                    string name = Path.GetFileName(file);
                    if (!Regex.IsMatch(name, @"\Asource_[0-9a-f]{16}_[0-9a-f]{16}\.scl\z")) throw new IOException("Unrecognized generation artifact; retain for inspection.");
                    files.Add(new JsonObject { ["path"] = name, ["sha256"] = CanonicalJson.HashBytes(File.ReadAllBytes(file)) });
                }
                string prefix = "staging/" + id + "/";
                var references = runs.Where(r => r["plan"]?["artifacts"] is JsonArray artifacts && artifacts.Any(a => ((string?)a!["path"])?.StartsWith(prefix, StringComparison.Ordinal) == true));
                result.Add(new JsonObject { ["artifactId"] = id, ["directory"] = directory, ["files"] = files,
                    ["runs"] = new JsonArray(references.Select(r => (JsonNode?)new JsonObject { ["runId"] = (string)r["runId"]!, ["status"] = (string)r["status"]!,
                        ["active"] = !r["events"]!.AsArray().Any(e => (string?)e!["event"] == "end") }).ToArray()) });
            }
            return result;
        }
        private void Remove(JsonObject entry)
        {
            string directory = Path.GetFullPath((string)entry["directory"]!);
            if (!string.Equals(Path.GetDirectoryName(directory), Path.GetFullPath(Staging), StringComparison.OrdinalIgnoreCase)) throw new IOException("Artifact cleanup escaped staging.");
            GenerationRecordFile.RejectLinks(directory);
            int deleted = 0;
            try
            {
                foreach (var file in entry["files"]!.AsArray())
                {
                    string path = Path.Combine(directory, (string)file!["path"]!); GenerationRecordFile.RejectLinks(path);
                    if (CanonicalJson.HashBytes(File.ReadAllBytes(path)) != (string?)file["sha256"]) throw new IOException("Artifact changed during cleanup.");
                    File.Delete(path); deleted++;
                }
                Directory.Delete(directory, false);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { throw new GenerationArtifactCleanupException(deleted, ex); }
        }
    }
}
