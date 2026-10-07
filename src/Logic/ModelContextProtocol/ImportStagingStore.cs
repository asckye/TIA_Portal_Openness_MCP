using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using TiaMcp.Logic.V4;
using TiaOpenness.Shared;

namespace TiaMcp.Logic.ModelContextProtocol
{
    public sealed class StagedTextFile
    {
        public string FileName { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Content { get; set; } = "";
    }

    // One store belongs to one MCP session. No public operation accepts a filesystem destination.
    public sealed class ImportStagingStore
    {
        public const int MaximumFiles = 128, MaximumBatches = 32;
        public const long MaximumFileBytes = 4 * 1024 * 1024, MaximumBytes = 32 * 1024 * 1024;
        private readonly object gate = new object();
        private readonly string root;
        private readonly string bundleRoot;
        private readonly string release;
        private readonly Dictionary<string, JsonObject> batches = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal Action<string>? BeforeWriteForTests { get; set; }
        internal Action<string>? WriteAccessForTests { get; set; }
        public static ImportStagingStore Create(string release)
        {
            var bundle = BundleLayout.RequireRoot(AppContext.BaseDirectory);
            return new ImportStagingStore(bundle, release, Guid.NewGuid().ToString("N"));
        }
        public static Envelope Unavailable(string tool, string release, IOException error, string? id = null)
        {
            string attempted = error is BundleResourceUnavailableException missing ? missing.Resource : AppContext.BaseDirectory;
            return Envelope.Create(new JsonObject { ["attemptedPath"] = attempted },
                new Error("The installed bundle could not be resolved for staging; attempted path: " + attempted, new IoFailedDetails("bundle-staging", attempted)),
                new Meta(DateTimeOffset.UtcNow, release, tool, Meta.Correlate(id), Outcome.RejectedBeforeOperation, Execution.NotStarted,
                    false, BehaviorPolicy.NotApplicable, Completeness.None, null, Array.Empty<Warning>()));
        }
        public ImportStagingStore(string bundleRoot, string release, string session)
        {
            TiaMcp.Versioning.TiaVersionCatalog.RequireRunnable(release);
            if (!Path.IsPathRooted(bundleRoot) || !Guid.TryParseExact(session, "N", out _)) throw new ArgumentException("An absolute bundle root and server session identity are required.");
            this.release = release;
            this.bundleRoot = Path.GetFullPath(bundleRoot);
            root = Path.Combine(this.bundleRoot, "staging", session);
        }
        private sealed class StagingUnavailableException : IOException
        { internal StagingUnavailableException(string message) : base(message) { } }
        private void CheckWritable()
        {
            Safe(Path.Combine(root, "probe"));
            try { NativeExportPolicy.CheckWritableDirectory(root, WriteAccessForTests); }
            catch (IOException error) { throw new StagingUnavailableException(error.Message); }
        }
        private static void Safe(string path)
        {
            for (var current = new DirectoryInfo(Path.GetDirectoryName(path)!); current != null; current = current.Parent)
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Staging ancestry contains a link/reparse point.", "files");
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Staging file is a link/reparse point.", "files");
        }
        private byte[] Validate(StagedTextFile file)
        {
            if (file == null || string.IsNullOrWhiteSpace(file.FileName) || file.FileName.Length > 128 || file.FileName.Trim() != file.FileName
                || file.FileName.Contains("..") || file.FileName.EndsWith(".", StringComparison.Ordinal)
                || file.FileName.Any(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c))) throw new ArgumentException("Use a safe filename without separators, traversal, trailing spaces/dots or control characters.", "files");
            var stem = file.FileName.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (new[] { "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$" }.Contains(stem)
                || System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(COM|LPT)[0-9¹²³]$")) throw new ArgumentException("Reserved filenames are refused.", "files");
            string extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            bool xml = file.Kind == "simaticml" || file.Kind == "tagtable" || file.Kind == "udt" && extension == ".xml";
            bool source = file.Kind == "scl" && extension == ".scl" || file.Kind == "udt" && (extension == ".scl" || extension == ".udt")
                || file.Kind == "s7dcl" && extension == ".s7dcl" && (release == "20" || release == "21");
            bool resource = file.Kind == "s7res" && extension == ".s7res" && (release == "20" || release == "21");
            if (!(xml && extension == ".xml") && !source && !resource) throw new ArgumentException("Filename extension/kind is not supported on this release.", "files");
            if (string.IsNullOrWhiteSpace(file.Content) || file.Content.Length > MaximumFileBytes) throw new ArgumentException("Content must be nonempty and at most 4 MiB.", "files");
            if (file.Content.Any(c => c == '\0' || c == '\ufeff' || char.IsControl(c) && c != '\t' && c != '\r' && c != '\n')
                || source && file.Content.Any(c => c > 127)) throw new ArgumentException("External sources require ASCII without BOM; document control characters are refused.", "files");
            byte[] bytes = Utf8.GetBytes(file.Content);
            if (bytes.LongLength > MaximumFileBytes) throw new ArgumentException("UTF-8 file size exceeds 4 MiB.", "files");
            if (xml)
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumFileBytes };
                using (var reader = XmlReader.Create(new StringReader(file.Content), settings))
                    while (reader.Read()) if (reader.Depth > 64) throw new ArgumentException("XML nesting exceeds 64.", "files");
                XDocument document;
                using (var reader = XmlReader.Create(new StringReader(file.Content), settings)) document = XDocument.Load(reader);
                if (document.Root?.Name != XName.Get("Document")) throw new ArgumentException("XML requires the Openness Document root.", "files");
                if (document.Declaration?.Encoding != null && !document.Declaration.Encoding.Equals("utf-8", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("XML encoding declaration must be UTF-8.", "files");
                if (file.Kind == "tagtable" && !document.Root.Elements("SW.Tags.PlcTagTable").Any()
                    || file.Kind == "udt" && !document.Root.Elements("SW.Types.PlcStruct").Any()) throw new ArgumentException("XML root object does not match the requested kind.", "files");
            }
            return bytes;
        }
        private static string Hash(byte[] bytes)
        { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        public JsonObject Stage(StagedTextFile[] files, bool dryRun = true)
        {
            lock (gate)
            {
                if (files == null || files.Length < 1 || files.Length > MaximumFiles) throw new ArgumentException("Supply 1..128 files.", "files");
                var bytes = new List<byte[]>(); long total = 0;
                foreach (var file in files)
                {
                    var content = Validate(file); total += content.LongLength;
                    if (total > MaximumBytes) throw new ArgumentException("Batch staging byte quota exceeded.", "files");
                    bytes.Add(content);
                }
                if (files.Select(f => f.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length) throw new ArgumentException("Case-insensitive duplicate filenames are refused.", "files");
                long retained = batches.Values.Sum(b => b["byteLength"]!.GetValue<long>());
                int count = batches.Values.Sum(b => b["files"]!.AsArray().Count);
                if (batches.Count >= MaximumBatches || count + files.Length > MaximumFiles || retained + total > MaximumBytes) throw new ArgumentException("Session staging quota exceeded; review and clean up staged batches.", "files");
                CheckWritable();
                foreach (var file in files)
                    if (Path.Combine(root, new string('0', 32), file.FileName).Length >= 260)
                        throw new ArgumentException("Staged import path exceeds the net48 MAX_PATH limit; use a shorter bundle path or filename.", "files");
                var entries = new JsonArray();
                for (int i = 0; i < files.Length; i++) entries.Add(new JsonObject { ["fileName"] = files[i].FileName, ["kind"] = files[i].Kind,
                    ["encoding"] = files[i].Kind == "scl" || files[i].Kind == "s7dcl" || files[i].Kind == "udt" && !Path.GetExtension(files[i].FileName).Equals(".xml", StringComparison.OrdinalIgnoreCase) ? "ascii" : "utf-8",
                    ["byteLength"] = bytes[i].LongLength, ["sha256"] = Hash(bytes[i]) });
                var result = new JsonObject { ["executed"] = !dryRun, ["stagingDirectory"] = root, ["byteLength"] = total, ["files"] = entries };
                if (dryRun) return result;
                var batchId = Guid.NewGuid().ToString("N");
                var folder = Path.Combine(root, batchId);
                result["batchId"] = batchId; result["directory"] = folder;
                result["createdUtc"] = DateTimeOffset.UtcNow.ToString("O");
                result["writtenFileCount"] = 0;
                // Reserve quota before publication, including partially written batches after IO failures.
                batches.Add(batchId, result);
                try
                {
                    Directory.CreateDirectory(folder);
                    for (int i = 0; i < files.Length; i++)
                    {
                        var path = Path.Combine(folder, files[i].FileName); BeforeWriteForTests?.Invoke(path); Safe(path);
                        entries[i]!["path"] = path;
                        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        stream.Write(bytes[i], 0, bytes[i].Length); stream.Flush(true);
                        result["writtenFileCount"] = i + 1;
                    }
                }
                catch (Exception error) when (error is ArgumentException || error is IOException || error is UnauthorizedAccessException)
                { result["partial"] = true; throw new IOException("Staging publication failed after batch reservation.", error); }
                return result.DeepClone().AsObject();
            }
        }
        public JsonObject List()
        {
            lock (gate) return new JsonObject { ["batches"] = new JsonArray(batches.Values.Select(b => b.DeepClone()).ToArray()),
                ["stagingDirectory"] = root, ["maximumFiles"] = MaximumFiles, ["maximumBytes"] = MaximumBytes };
        }
        public JsonObject Cleanup(string batchId, bool dryRun = true)
        {
            lock (gate)
            {
                if (!Guid.TryParseExact(batchId, "N", out _) || !batches.TryGetValue(batchId, out var batch)) throw new ArgumentException("Select a batchId listed in this session.", "batchId");
                var folder = Path.Combine(root, batchId);
                CheckWritable();
                foreach (var entry in batch!["files"]!.AsArray())
                {
                    string path = Path.Combine(folder, entry!["fileName"]!.GetValue<string>()); Safe(path);
                    if (!dryRun && File.Exists(path)) File.Delete(path);
                }
                if (!dryRun) { if (Directory.Exists(folder)) Directory.Delete(folder, false); batches.Remove(batchId); }
                return new JsonObject { ["batchId"] = batchId, ["executed"] = !dryRun, ["deleted"] = !dryRun, ["directory"] = folder };
            }
        }
        public Envelope Run(string tool, StagedTextFile[]? files = null, string batchId = "", bool dryRun = true, string? id = null)
        {
            JsonObject? data = null; Error? error = null;
            Outcome outcome = Outcome.Succeeded; Execution execution = dryRun || tool == "ListStagedImportFiles" ? Execution.ReadOnly : Execution.Completed;
            try { data = tool == "StageImportFiles" ? Stage(files!, dryRun) : tool == "CleanupStagedImportFiles" ? Cleanup(batchId, dryRun) : List(); }
            catch (Exception ex) when (ex is ArgumentException || ex is XmlException || ex is EncoderFallbackException)
            { error = new Error(ex.Message, new InvalidArgumentDetails(tool == "CleanupStagedImportFiles" ? "batchId" : "files", Array.Empty<string>())); outcome = Outcome.RejectedBeforeOperation; execution = Execution.NotStarted; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                data = List(); data["attemptedPath"] = root;
                bool partial = !dryRun && ex is not StagingUnavailableException;
                var retained = data["batches"]!.AsArray().OfType<JsonObject>().LastOrDefault(b => (bool?)b["partial"] == true);
                int kept = (int?)retained?["writtenFileCount"] ?? 0;
                int count = (retained?["files"] as JsonArray)?.Count ?? 1;
                error = new Error("Staging IO failed; attempted path: " + root + ". Review retained batches before cleanup or retry.",
                    partial ? (ErrorDetails)new PartialFailureDetails(kept, 1, Math.Max(0, count - kept - 1)) : new IoFailedDetails("import-staging", root));
                outcome = partial ? Outcome.Partial : Outcome.RejectedBeforeOperation; execution = partial ? Execution.Partial : Execution.NotStarted;
            }
            return Envelope.Create(data, error, new Meta(DateTimeOffset.UtcNow, release, tool, Meta.Correlate(id), outcome, execution,
                false, BehaviorPolicy.NotApplicable, error == null ? Completeness.Complete : Completeness.Partial, null, Array.Empty<Warning>()));
        }
    }
}
