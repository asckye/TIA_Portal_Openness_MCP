using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaOpenness.Shared;

namespace TiaMcp.Logic.Generation
{
    public sealed class GenerationRecord
    {
        public string Schema { get; set; } = "tiamcp.generation-record/1";
        public GenerationRecordProject Project { get; set; } = new GenerationRecordProject();
        public string? PreviousRecordHash { get; set; }
        public List<GenerationRecordRun> Runs { get; set; } = new List<GenerationRecordRun>();
        public List<GenerationRecordObject> Objects { get; set; } = new List<GenerationRecordObject>();
        public string RecordHash { get; set; } = "sha256:" + new string('0', 64);
        public static string Hash(GenerationRecord record)
        {
            var value = JsonNode.Parse(GenerationDocuments.Canonical(record))!.AsObject();
            value.Remove("recordHash");
            return CanonicalJson.Hash(CanonicalJson.Parse(value.ToJsonString()));
        }
        public static GenerationRecord Parse(string json)
        {
            var record = GenerationDocuments.Load<GenerationRecord>(json);
            if (Hash(record) != record.RecordHash) throw new ArgumentException("Generation recordHash verification failed.", "recordHash");
            if (record.Objects.Select(o => o.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != record.Objects.Count)
                throw new ArgumentException("Duplicate generation ownership key.", "objects");
            if (record.Objects.Any(o => o.State != "unknown" && o.Witness == null))
                throw new ArgumentException("Owned/adopted objects require a post-save witness.", "objects");
            if (record.Runs.Select(r => r.RunId).Distinct(StringComparer.Ordinal).Count() != record.Runs.Count)
                throw new ArgumentException("Duplicate generation run identity.", "runs");
            return record;
        }
    }
    public sealed class GenerationRecordProject
    {
        public string File { get; set; } = "";
        public string Release { get; set; } = "";
    }
    public sealed class GenerationRecordRun
    {
        public string RunId { get; set; } = "";
        public string PlanId { get; set; } = "";
        public string PlanHash { get; set; } = "";
        public GenerationPlanPackage Package { get; set; } = new GenerationPlanPackage();
        public string MachineHash { get; set; } = "";
        public string Status { get; set; } = "";
        public string StartedUtc { get; set; } = "";
        public string EndedUtc { get; set; } = "";
        public string ParentRequestId { get; set; } = "";
        public string ApprovalRequestId { get; set; } = "";
        public string Actor { get; set; } = "mcp";
    }
    public sealed class GenerationRecordObject
    {
        public string Key { get; set; } = "";
        public string Path { get; set; } = "";
        public string ExpectedFingerprint { get; set; } = "";
        public FileDigest? Artifact { get; set; }
        public GenerationWitness? Witness { get; set; }
        public string State { get; set; } = "unknown";
        public GenerationObjectOrigin CreatedBy { get; set; } = new GenerationObjectOrigin();
    }
    public sealed class GenerationWitness
    {
        public string Kind { get; set; } = "";
        public string Tool { get; set; } = "";
        public Dictionary<string, JsonElement> Values { get; set; } = new Dictionary<string, JsonElement>();
        public bool Matches(GenerationWitness? current) => current != null && Kind == current.Kind && Tool == current.Tool
            && CanonicalJson.Hash(JsonSerializer.SerializeToElement(Values)) == CanonicalJson.Hash(JsonSerializer.SerializeToElement(current.Values));
    }
    public sealed class GenerationObjectOrigin
    {
        public string RunId { get; set; } = "";
        public string StepId { get; set; } = "";
        public string RequestId { get; set; } = "";
    }

    // The raw-file token also detects edits which leave the canonical recordHash unchanged.
    public sealed class GenerationRecordSnapshot
    {
        public string Token { get; internal set; } = "absent";
        public GenerationRecord? Record { get; internal set; }
        public string PreviousHash { get; internal set; } = "absent";
    }
    public static class GenerationRecordFile
    {
        public static string DefaultPath(string project)
        {
            string normalized = GenerationProjectPath.Normalize(project).Replace('\\', '/');
            int dot = normalized.LastIndexOf('.');
            if (dot < normalized.LastIndexOf('/')) throw new ArgumentException("Project file extension required.", "project");
            return normalized.Substring(0, dot) + ".tiagen.json";
        }
        public static void RejectLinks(string path)
        {
            path = System.IO.Path.GetFullPath(path);
            Reject(path);
            for (var directory = new DirectoryInfo(System.IO.Path.GetDirectoryName(path)!); directory != null; directory = directory.Parent) Reject(directory.FullName);
            void Reject(string item)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(item); }
                catch (FileNotFoundException) { /* swallow(probe-optional): an absent record leaf is allowed; its existing parents are still checked */ return; }
                catch (DirectoryNotFoundException) { /* swallow(probe-optional): absent parents cannot be reparse points; publication separately requires an existing folder */ return; }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Generation files refuse links/reparse points.");
            }
        }
        public static GenerationRecordSnapshot Read(string path)
        {
            RejectLinks(path);
            if (!File.Exists(path)) return new GenerationRecordSnapshot();
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > 16 * 1024 * 1024) throw new IOException("Generation record exceeds 16 MiB.");
            var snapshot = new GenerationRecordSnapshot { Token = "sha256:" + CanonicalJson.HashBytes(bytes) };
            try { snapshot.Record = GenerationRecord.Parse(new UTF8Encoding(false, true).GetString(bytes)); }
            catch (Exception ex) when (ex is ArgumentException || ex is GenerationValidationException || ex is JsonException || ex is DecoderFallbackException)
            { snapshot.Record = null; }
            snapshot.PreviousHash = snapshot.Record?.RecordHash ?? snapshot.Token;
            return snapshot;
        }
        public static void Write(string path, GenerationRecord record, string previousToken)
        {
            path = System.IO.Path.GetFullPath(path); RejectLinks(path);
            GenerationRecord.Parse(GenerationDocuments.Canonical(record));
            string directory = System.IO.Path.GetDirectoryName(path)!;
            if (!Directory.Exists(directory)) throw new IOException("The project directory must already exist.");
            string lockPath = path + ".lock"; RejectLinks(lockPath);
            using var locked = JournalFileLock.Acquire(lockPath);
            if (Read(path).Token != previousToken) throw new ArgumentException("Previous generation record changed; preview again.", "expectedPlanHash");
            string temporary = System.IO.Path.Combine(directory, ".tiagen-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(GenerationDocuments.Canonical(record) + "\n");
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
                RejectLinks(path);
                if (Read(path).Token != previousToken) throw new ArgumentException("Previous generation record changed; preview again.", "expectedPlanHash");
                if (previousToken == "absent") File.Move(temporary, path);
                else File.Replace(temporary, path, null);
            }
            finally { if (File.Exists(temporary)) { RejectLinks(temporary); File.Delete(temporary); } }
            if (Read(path).Record?.RecordHash != record.RecordHash) throw new IOException("Generation record readback differs after publication.");
        }
    }
}
