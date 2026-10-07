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

    // Quotas belong to one MCP session; persisted batches remain visible after reconnection.
    public sealed class ImportStagingStore
    {
        public const int MaximumFiles = 128, MaximumBatches = 32;
        public const long MaximumFileBytes = 4 * 1024 * 1024, MaximumBytes = 32 * 1024 * 1024;
        private readonly object gate = new object();
        private readonly string root;
        private readonly string bundleRoot;
        private readonly string release;
        private readonly string session;
        private const string ManifestName = ".staging-batch.json";
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
            this.session = session;
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
                || file.FileName.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) || file.FileName.Contains("..") || file.FileName.EndsWith(".", StringComparison.Ordinal)
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
                foreach (var disk in Discover().Where(b => (bool?)b["identified"] == true && (bool?)b["currentSession"] == true))
                    batches[(string)disk["batchId"]!] = disk;
                foreach (var id in batches.Keys.Where(id => !Directory.Exists(Path.Combine(root, id))).ToArray()) batches.Remove(id);
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
                result["sessionId"] = session; result["releaseKey"] = release; result["manifestVersion"] = 1;
                result["writtenFileCount"] = 0;
                // Reserve quota before publication, including partially written batches after IO failures.
                batches.Add(batchId, result);
                try
                {
                    Directory.CreateDirectory(folder);
                    SaveManifest(folder, result);
                    for (int i = 0; i < files.Length; i++)
                    {
                        var path = Path.Combine(folder, files[i].FileName); BeforeWriteForTests?.Invoke(path); Safe(path);
                        entries[i]!["path"] = path;
                        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        result["writtenFileCount"] = i + 1; SaveManifest(folder, result);
                        stream.Write(bytes[i], 0, bytes[i].Length); stream.Flush(true);
                    }
                }
                catch (Exception error) when (error is ArgumentException || error is IOException || error is UnauthorizedAccessException)
                { result["partial"] = true; var failure = new IOException("Staging publication failed after batch reservation: " + error.Message, error);
                    failure.Data["stagingMutationStarted"] = true; failure.Data["batchId"] = batchId; failure.Data["attemptedPath"] = folder; throw failure; }
                return result.DeepClone().AsObject();
            }
        }
        private static void SaveManifest(string folder, JsonObject batch)
        {
            string path = Path.Combine(folder, ManifestName); Safe(path);
            var manifest = new JsonObject();
            foreach (var key in new[] { "manifestVersion", "sessionId", "releaseKey", "batchId", "createdUtc", "writtenFileCount", "partial" }) manifest[key] = batch[key]?.DeepClone();
            manifest["files"] = new JsonArray(batch["files"]!.AsArray().Select(entry => {
                var file = new JsonObject(); foreach (var key in new[] { "fileName", "kind", "encoding", "byteLength", "sha256" }) file[key] = entry![key]?.DeepClone(); return (JsonNode?)file;
            }).ToArray());
            byte[] bytes = Utf8.GetBytes(manifest.ToJsonString());
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
        }
        private JsonObject? ReadManifest(string folder, string owner, string id)
        {
            try
            {
                string path = Path.Combine(folder, ManifestName); Safe(path);
                if (!Guid.TryParseExact(owner, "N", out _) || !Guid.TryParseExact(id, "N", out _) || !File.Exists(path)) return null;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length < 1 || stream.Length > 128 * 1024) return null;
                var batch = JsonNode.Parse(stream)?.AsObject();
                if ((int?)batch?["manifestVersion"] != 1 || (string?)batch?["sessionId"] != owner || (string?)batch?["batchId"] != id
                    || !DateTimeOffset.TryParse((string?)batch?["createdUtc"], out _) || batch?["files"] is not JsonArray files
                    || files.Count > MaximumFiles || (int?)batch["writtenFileCount"] is not int written || written < 0 || written > files.Count) return null;
                long total = 0; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in files.OfType<JsonObject>())
                {
                    string? name = (string?)entry["fileName"], hash = (string?)entry["sha256"];
                    if (name == null || name.Length < 1 || name.Length > 128 || name != Path.GetFileName(name) || name.Contains("..")
                        || name.Any(c => char.IsControl(c) || "\\/:*\"<>|".Contains(c)) || name.Trim() != name || name.EndsWith(".", StringComparison.Ordinal)
                        || name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) || !names.Add(name)
                        || hash == null || hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))
                        || (long?)entry["byteLength"] is not long size || size < 0 || size > MaximumFileBytes) return null;
                    total += size; if (total > MaximumBytes) return null;
                    entry["path"] = Path.Combine(folder, name);
                }
                if (names.Count != files.Count) return null;
                batch["directory"] = folder; batch["stagingDirectory"] = Path.GetDirectoryName(folder); batch["byteLength"] = total;
                batch["executed"] = true;
                batch["identified"] = true; batch["currentSession"] = owner == session;
                batch["unknownEntries"] = UnknownEntries(folder, batch);
                return batch;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is JsonException || error is InvalidOperationException || error is FormatException)
            { /* swallow(probe-optional): an unreadable or invalid manifest is unknown content, never a cleanup target */ return null; }
        }
        private static JsonArray UnknownEntries(string folder, JsonObject batch)
        {
            int written = (int?)batch["writtenFileCount"] ?? 0;
            var known = new HashSet<string>(batch["files"]!.AsArray().Take(written).Select(x => (string)x!["fileName"]!), StringComparer.OrdinalIgnoreCase) { ManifestName };
            return new JsonArray(Directory.EnumerateFileSystemEntries(folder).Where(path => !known.Contains(Path.GetFileName(path)))
                .OrderBy(path => path, StringComparer.Ordinal).Select(path => (JsonNode?)JsonValue.Create(Path.GetFileName(path))).ToArray());
        }
        private IEnumerable<JsonObject> Discover()
        {
            string staging = Path.Combine(bundleRoot, "staging");
            Safe(Path.Combine(staging, "probe"));
            if (!Directory.Exists(staging)) yield break;
            foreach (string ownerFolder in Directory.EnumerateDirectories(staging).OrderBy(x => x, StringComparer.Ordinal))
            {
                if ((File.GetAttributes(ownerFolder) & FileAttributes.ReparsePoint) != 0)
                { yield return new JsonObject { ["directory"] = ownerFolder, ["sessionId"] = Path.GetFileName(ownerFolder), ["identified"] = false, ["reason"] = "linked-session-folder" }; continue; }
                foreach (string folder in Directory.EnumerateDirectories(ownerFolder).OrderBy(x => x, StringComparer.Ordinal))
                {
                    string owner = Path.GetFileName(ownerFolder), id = Path.GetFileName(folder);
                    var batch = ReadManifest(folder, owner, id);
                    yield return batch ?? new JsonObject { ["directory"] = folder, ["sessionId"] = owner, ["batchId"] = id,
                        ["createdUtc"] = Directory.GetCreationTimeUtc(folder).ToString("O"), ["identified"] = false, ["currentSession"] = owner == session,
                        ["files"] = new JsonArray(), ["byteLength"] = null, ["reason"] = "missing-or-invalid-manifest" };
                }
            }
        }
        public JsonObject List()
        {
            lock (gate) return new JsonObject { ["batches"] = new JsonArray(Discover().Select(b => (JsonNode?)b).ToArray()),
                ["stagingDirectory"] = Path.Combine(bundleRoot, "staging"), ["sessionId"] = session,
                ["maximumFiles"] = MaximumFiles, ["maximumBytes"] = MaximumBytes };
        }
        public JsonObject Cleanup(string batchId, bool dryRun = true)
        {
            lock (gate)
            {
                if (!Guid.TryParseExact(batchId, "N", out _)) throw new ArgumentException("Select an identified batchId from ListStagedImportFiles.", "batchId");
                var matches = Discover().Where(b => (string?)b["batchId"] == batchId).ToArray();
                if (matches.Length != 1 || (bool?)matches[0]["identified"] != true) throw new ArgumentException("Batch is unknown or ambiguous; a valid staging manifest is required for cleanup.", "batchId");
                var batch = matches[0]; string folder = (string)batch["directory"]!;
                Safe(Path.Combine(folder, "probe"));
                NativeExportPolicy.CheckWritableDirectory(folder, WriteAccessForTests);
                var leaves = batch["files"]!.AsArray().Take((int)batch["writtenFileCount"]!).Select(x => Path.Combine(folder, (string)x!["fileName"]!)).ToArray();
                foreach (var path in leaves)
                {
                    try { Safe(path); }
                    catch (ArgumentException error) { throw new ArgumentException(error.Message, "batchId", error); }
                    if (Directory.Exists(path)) throw new ArgumentException("A known staged file was replaced by a directory; cleanup refused.", "batchId");
                }
                var unknown = UnknownEntries(folder, batch); bool retained = unknown.Count > 0;
                if (!dryRun)
                {
                    try
                    {
                    foreach (var path in leaves) if (File.Exists(path)) File.Delete(path);
                    // Recheck after deleting known leaves: new or unknown content is always retained.
                    unknown = UnknownEntries(folder, batch); retained = unknown.Count > 0;
                    batch["files"] = new JsonArray(); batch["writtenFileCount"] = 0; batch["byteLength"] = 0;
                    if (retained) SaveManifest(folder, batch);
                    else
                    {
                        File.Delete(Path.Combine(folder, ManifestName));
                        if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder, false);
                        else { retained = true; SaveManifest(folder, batch); unknown = UnknownEntries(folder, batch); }
                    }
                    batches.Remove(batchId);
                    }
                    catch (Exception cause) when (cause is IOException || cause is UnauthorizedAccessException)
                    { var failure = new IOException("Cleanup failed after starting deletion: " + cause.Message, cause);
                        failure.Data["stagingMutationStarted"] = true; failure.Data["attemptedPath"] = folder; throw failure; }
                }
                return new JsonObject { ["batchId"] = batchId, ["executed"] = !dryRun, ["deleted"] = !dryRun,
                    ["directory"] = folder, ["sessionId"] = batch["sessionId"]!.DeepClone(), ["currentSession"] = batch["currentSession"]!.DeepClone(),
                    ["folderRetained"] = retained, ["unknownEntries"] = unknown };
            }
        }
        public Envelope Run(string tool, StagedTextFile[]? files = null, string batchId = "", bool dryRun = true, string? id = null)
        {
            JsonObject? data = null; Error? error = null; var warnings = new List<Warning>();
            Outcome outcome = Outcome.Succeeded; Execution execution = dryRun || tool == "ListStagedImportFiles" ? Execution.ReadOnly : Execution.Completed;
            try { data = tool == "StageImportFiles" ? Stage(files!, dryRun) : tool == "CleanupStagedImportFiles" ? Cleanup(batchId, dryRun) : List(); }
            catch (Exception ex) when (ex is ArgumentException || ex is XmlException || ex is EncoderFallbackException)
            { error = new Error(ex.Message, new InvalidArgumentDetails(tool == "CleanupStagedImportFiles" ? "batchId" : "files", Array.Empty<string>())); outcome = Outcome.RejectedBeforeOperation; execution = Execution.NotStarted; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                string attemptedPath = ex.Data["attemptedPath"] as string ?? root;
                data = new JsonObject { ["attemptedPath"] = attemptedPath, ["batchId"] = ex.Data["batchId"] as string ?? batchId, ["parameter"] = tool == "CleanupStagedImportFiles" ? "batchId" : "files", ["reason"] = ex.Message };
                bool partial = !dryRun && ex.Data["stagingMutationStarted"] is true;
                var retained = batches.Values.LastOrDefault(b => (bool?)b["partial"] == true);
                int kept = (int?)retained?["writtenFileCount"] ?? 0;
                int count = (retained?["files"] as JsonArray)?.Count ?? 1;
                error = new Error("Staging IO failed for " + (tool == "CleanupStagedImportFiles" ? "batchId " + batchId : "files") + ": " + ex.Message + " Attempted path: " + attemptedPath,
                    partial ? (ErrorDetails)new PartialFailureDetails(kept, 1, Math.Max(0, count - kept - 1)) : new IoFailedDetails("import-staging", attemptedPath));
                outcome = partial ? Outcome.Partial : Outcome.RejectedBeforeOperation; execution = partial ? Execution.Partial : Execution.NotStarted;
            }
            if ((bool?)data?["folderRetained"] == true) warnings.Add(new Warning(WarningCode.NativeWarning,
                "The batch folder is retained because it contains unknown entries; only staging-owned files are deleted.",
                new Dictionary<string, JsonElement> { ["unknownEntries"] = JsonSerializer.SerializeToElement(data["unknownEntries"]) }));
            return Envelope.Create(data, error, new Meta(DateTimeOffset.UtcNow, release, tool, Meta.Correlate(id), outcome, execution,
                false, BehaviorPolicy.NotApplicable, error == null ? Completeness.Complete : Completeness.Partial, null, warnings));
        }
    }
}
