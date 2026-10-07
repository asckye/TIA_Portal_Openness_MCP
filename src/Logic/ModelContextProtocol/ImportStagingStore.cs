using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private const string CompanionName = ".staging-batch.previous.json";
        public const int MaximumListedBatches = 200;
        private readonly int hostPid = Process.GetCurrentProcess().Id;
        private readonly string hostStartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("O");
        private readonly Dictionary<string, JsonObject> batches = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal Action<string>? BeforeWriteForTests { get; set; }
        internal Action<string>? WriteAccessForTests { get; set; }
        internal Action<string>? BeforeManifestPublishForTests { get; set; }
        internal Action<string>? BeforeDeleteForTests { get; set; }
        internal Action<string>? DiscoverForTests { get; set; }
        internal Func<int, string, bool>? OwnerAliveForTests { get; set; }
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
                foreach (var disk in Discover(root).Where(b => (bool?)b["identified"] == true))
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
                result["hostPid"] = hostPid; result["hostStartedUtc"] = hostStartedUtc;
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
                        stream.Write(bytes[i], 0, bytes[i].Length); stream.Flush(true);
                        result["writtenFileCount"] = i + 1; SaveManifest(folder, result);
                    }
                }
                catch (Exception error) when (error is ArgumentException || error is IOException || error is UnauthorizedAccessException)
                { result["partial"] = true; var failure = new IOException("Staging publication failed after batch reservation: " + error.Message, error);
                    failure.Data["stagingMutationStarted"] = Directory.Exists(folder); failure.Data["batchId"] = batchId; failure.Data["attemptedPath"] = folder;
                    failure.Data["succeeded"] = (int)result["writtenFileCount"]!; failure.Data["total"] = files.Length; throw failure; }
                return result.DeepClone().AsObject();
            }
        }
        private static readonly string[] ManifestKeys = { "manifestVersion", "sessionId", "releaseKey", "batchId", "createdUtc", "hostPid", "hostStartedUtc", "writtenFileCount", "partial" };
        private static readonly string[] FileKeys = { "fileName", "kind", "encoding", "byteLength", "sha256" };
        private void AtomicWrite(string path, byte[] bytes)
        {
            Safe(path);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                BeforeManifestPublishForTests?.Invoke(path);
                if (File.Exists(path)) File.Replace(temporary, path, null, true);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) System.IO.File.Delete(temporary); }
        }
        private void SaveManifest(string folder, JsonObject batch)
        {
            var manifest = new JsonObject();
            foreach (var key in ManifestKeys) manifest[key] = batch[key]?.DeepClone();
            manifest["files"] = new JsonArray(batch["files"]!.AsArray().Select(entry => {
                var file = new JsonObject(); foreach (var key in FileKeys) file[key] = entry![key]?.DeepClone(); return (JsonNode?)file;
            }).ToArray());
            byte[] bytes = Utf8.GetBytes(manifest.ToJsonString());
            // Each publication is atomic. The companion may lag by one publication after a crash.
            AtomicWrite(Path.Combine(folder, ManifestName), bytes);
            AtomicWrite(Path.Combine(folder, CompanionName), bytes);
        }
        private static bool ProbeFailure(Exception error) => error is IOException || error is UnauthorizedAccessException
            || error is ArgumentException || error is JsonException || error is InvalidOperationException || error is FormatException;
        private sealed class ManifestIdentityException : ArgumentException { }
        private JsonObject? ReadManifest(string folder, string owner, string id, string name)
        {
            string path = Path.Combine(folder, name); Safe(path);
            if (!Guid.TryParseExact(owner, "N", out _) || !Guid.TryParseExact(id, "N", out _)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < 1 || stream.Length > 128 * 1024) return null;
            var input = JsonNode.Parse(stream)?.AsObject();
            if (input != null && ((string?)input["sessionId"] != owner || (string?)input["batchId"] != id)) throw new ManifestIdentityException();
            if ((int?)input?["manifestVersion"] != 1 || !DateTimeOffset.TryParse((string?)input?["createdUtc"], out _) || input?["files"] is not JsonArray files
                || files.Count > MaximumFiles || (int?)input["writtenFileCount"] is not int written || written < 0 || written > files.Count) return null;
            if ((string?)input["releaseKey"] is not string releaseKey || !new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }.Contains(releaseKey)) return null;
            if (input["hostPid"] != null && (input["hostPid"] is not JsonValue pidValue || !pidValue.TryGetValue<int>(out var pid) || pid < 1)
                || input["hostStartedUtc"] != null && (input["hostStartedUtc"] is not JsonValue startValue || !startValue.TryGetValue<string>(out var started) || !DateTimeOffset.TryParse(started, out _))
                || input["partial"] != null && (input["partial"] is not JsonValue partialValue || !partialValue.TryGetValue<bool>(out _))) return null;
            // Persisted JSON is untrusted; publish only the schema we own.
            var batch = new JsonObject(); foreach (string key in ManifestKeys) batch[key] = input[key]?.DeepClone();
            var outputFiles = new JsonArray(); long total = 0; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in files)
            {
                if (item is not JsonObject entry) return null;
                string? leaf = (string?)entry["fileName"], hash = (string?)entry["sha256"];
                if ((string?)entry["kind"] is not ("scl" or "simaticml" or "tagtable" or "udt" or "s7dcl" or "s7res") || (string?)entry["encoding"] is not ("ascii" or "utf-8")) return null;
                if (leaf == null || leaf.Length < 1 || leaf.Length > 128 || leaf != Path.GetFileName(leaf) || leaf.Contains("..")
                    || leaf.Any(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c)) || leaf.Trim() != leaf || leaf.EndsWith(".", StringComparison.Ordinal)
                    || leaf.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) || leaf.Equals(CompanionName, StringComparison.OrdinalIgnoreCase) || !names.Add(leaf)
                    || hash == null || hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))
                    || (long?)entry["byteLength"] is not long size || size < 0 || size > MaximumFileBytes) return null;
                total += size; if (total > MaximumBytes) return null;
                var file = new JsonObject(); foreach (string key in FileKeys) file[key] = entry[key]?.DeepClone();
                file["path"] = Path.Combine(folder, leaf); outputFiles.Add(file);
            }
            batch["files"] = outputFiles; batch["directory"] = folder; batch["stagingDirectory"] = Path.GetDirectoryName(folder);
            batch["byteLength"] = total; batch["executed"] = true; batch["identified"] = true; batch["currentSession"] = owner == session;
            return batch;
        }
        private static bool Matches(Stream stream, JsonNode entry)
        {
            if (stream.Length != (long)entry["byteLength"]!) return false;
            using var sha = SHA256.Create();
            return string.Equals(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""), (string)entry["sha256"]!, StringComparison.OrdinalIgnoreCase);
        }
        private static JsonArray UnknownEntries(string folder, JsonObject batch)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestName, CompanionName };
            var reasons = new JsonObject();
            foreach (var entry in batch["files"]!.AsArray())
            {
                string leaf = (string)entry!["fileName"]!, path = Path.Combine(folder, leaf);
                try
                {
                    Safe(path);
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                    if (Matches(stream, entry)) known.Add(leaf); else reasons[leaf] = "content-mismatch";
                }
                catch (FileNotFoundException) { /* swallow(probe-optional): absent owned leaves need no deletion */ }
                catch (Exception error) when (ProbeFailure(error)) { reasons[leaf] = "unreadable-or-unsafe-file"; }
            }
            var entries = Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).Where(name => !known.Contains(name!)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            batch["unknownEntryReasons"] = reasons;
            return new JsonArray(entries.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
        }
        private JsonObject Unknown(string folder, string reason) => new JsonObject { ["directory"] = folder,
            ["sessionId"] = Path.GetFileName(Path.GetDirectoryName(folder)), ["batchId"] = Path.GetFileName(folder),
            ["identified"] = false, ["currentSession"] = Path.GetDirectoryName(folder) == root,
            ["files"] = new JsonArray(), ["byteLength"] = null, ["reason"] = reason };
        private JsonObject Inspect(string folder)
        {
            try
            {
                DiscoverForTests?.Invoke(folder); Safe(Path.Combine(folder, "probe"));
                string owner = Path.GetFileName(Path.GetDirectoryName(folder)), id = Path.GetFileName(folder);
                JsonObject? batch;
                try
                {
                    batch = ReadManifest(folder, owner, id, ManifestName);
                    if (batch == null)
                    { batch = ReadManifest(folder, owner, id, CompanionName); if (batch != null) batch["reason"] = "manifest-invalid"; }
                }
                catch (ManifestIdentityException) { /* swallow(probe-optional): identity mismatch is never a cleanup target */ return Unknown(folder, "manifest-invalid"); }
                catch (Exception error) when (ProbeFailure(error))
                {
                    // A truncated/unreadable primary can use the last atomically published companion.
                    batch = ReadManifest(folder, owner, id, CompanionName);
                    if (batch != null) batch["reason"] = "manifest-invalid";
                }
                if (batch == null) return Unknown(folder, "manifest-invalid");
                batch["unknownEntries"] = UnknownEntries(folder, batch);
                return batch;
            }
            catch (Exception error) when (ProbeFailure(error)) { return Unknown(folder, "unreadable-or-missing-batch"); }
        }
        private IEnumerable<JsonObject> Discover(string? onlySession = null)
        {
            var found = new List<JsonObject>(); string staging = Path.Combine(bundleRoot, "staging");
            string[] owners;
            try
            {
                Safe(Path.Combine(staging, "probe"));
                owners = onlySession != null ? new[] { onlySession } : Directory.Exists(staging) ? Directory.GetDirectories(staging) : Array.Empty<string>();
            }
            catch (Exception error) when (ProbeFailure(error)) { return new[] { Unknown(staging, "unreadable-staging-root") }; }
            foreach (string owner in owners)
            {
                try
                {
                    DiscoverForTests?.Invoke(owner); Safe(Path.Combine(owner, "probe"));
                    if (!Directory.Exists(owner))
                    {
                        if (onlySession != null) continue;
                        throw new DirectoryNotFoundException("The enumerated staging session disappeared.");
                    }
                    foreach (string folder in Directory.GetDirectories(owner)) found.Add(Inspect(folder));
                }
                catch (Exception error) when (ProbeFailure(error))
                {
                    var unknown = Unknown(owner, "unreadable-or-missing-session");
                    unknown["sessionId"] = Path.GetFileName(owner); unknown.Remove("batchId"); unknown["currentSession"] = owner == root;
                    found.Add(unknown);
                }
            }
            return found;
        }
        public JsonObject List()
        {
            lock (gate)
            {
                var found = Discover().OrderByDescending(b => (string?)b["createdUtc"] ?? "", StringComparer.Ordinal).ThenBy(b => (string?)b["directory"], StringComparer.Ordinal).ToArray();
                return new JsonObject { ["batches"] = new JsonArray(found.Take(MaximumListedBatches).Select(b => (JsonNode?)b).ToArray()),
                    ["totalBatches"] = found.Length, ["returnedBatches"] = Math.Min(found.Length, MaximumListedBatches), ["truncated"] = found.Length > MaximumListedBatches,
                    ["stagingDirectory"] = Path.Combine(bundleRoot, "staging"), ["sessionId"] = session,
                    ["maximumFiles"] = MaximumFiles, ["maximumBytes"] = MaximumBytes };
            }
        }
        private bool OwnerAlive(JsonObject batch)
        {
            if ((bool?)batch["currentSession"] == true) return false;
            if ((int?)batch["hostPid"] is not int pid || pid < 1 || (string?)batch["hostStartedUtc"] is not string started) return true;
            if (OwnerAliveForTests != null) return OwnerAliveForTests(pid, started);
            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited && process.StartTime.ToUniversalTime().ToString("O") == started;
            }
            catch (ArgumentException) { /* swallow(probe-optional): the owning process no longer exists */ return false; }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception || error is InvalidOperationException)
            { /* swallow(probe-optional): an uninspectable owner must not be treated as dead */ return true; }
        }
        public JsonObject Cleanup(string batchId, bool dryRun = true)
        {
            lock (gate)
            {
                if (!Guid.TryParseExact(batchId, "N", out _)) throw new ArgumentException("Select an identified batchId from ListStagedImportFiles.", "batchId");
                var matches = Discover().Where(b => (string?)b["batchId"] == batchId).ToArray();
                if (matches.Length != 1 || (bool?)matches[0]["identified"] != true) throw new ArgumentException("Batch is unknown or ambiguous; a valid staging manifest is required for cleanup.", "batchId");
                var batch = matches[0]; string folder = (string)batch["directory"]!;
                if (OwnerAlive(batch)) throw new ArgumentException("The batch belongs to another live or uninspectable host session; cleanup refused.", "batchId");
                Safe(Path.Combine(folder, "probe"));
                try { NativeExportPolicy.CheckWritableDirectory(folder, WriteAccessForTests); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                { error.Data["attemptedPath"] = folder; error.Data["batchId"] = batchId; throw; }
                var unknown = UnknownEntries(folder, batch); int deleted = 0; bool mutated = false;
                var leaves = batch["files"]!.AsArray().ToArray();
                if (!dryRun)
                {
                    try
                    {
                        foreach (var entry in leaves)
                        {
                            string path = Path.Combine(folder, (string)entry!["fileName"]!);
                            if (unknown.Any(x => x!.ToString() == (string)entry["fileName"]!) || !System.IO.File.Exists(path)) continue;
                            BeforeDeleteForTests?.Invoke(path); Safe(path);
                            if (NativeExportPolicy.DeleteVerifiedFile(path, stream => Matches(stream, entry))) { deleted++; mutated = true; }
                        }
                        unknown = UnknownEntries(folder, batch);
                        batch["files"] = new JsonArray(); batch["writtenFileCount"] = 0; batch["byteLength"] = 0;
                        if (unknown.Count > 0) SaveManifest(folder, batch);
                        else
                        {
                            foreach (string metadata in new[] { ManifestName, CompanionName })
                            {
                                string path = Path.Combine(folder, metadata); Safe(path);
                                if (System.IO.File.Exists(path)) { System.IO.File.Delete(path); mutated = true; }
                            }
                            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder, false);
                            else { SaveManifest(folder, batch); unknown = UnknownEntries(folder, batch); }
                        }
                        batches.Remove(batchId);
                    }
                    catch (Exception cause) when (ProbeFailure(cause))
                    {
                        var failure = new IOException("Cleanup failed: " + cause.Message, cause);
                        failure.Data["stagingMutationStarted"] = mutated; failure.Data["batchId"] = batchId;
                        failure.Data["attemptedPath"] = folder; failure.Data["succeeded"] = Math.Min(deleted, leaves.Length);
                        failure.Data["total"] = leaves.Length; throw failure;
                    }
                }
                return new JsonObject { ["batchId"] = batchId, ["executed"] = !dryRun, ["deleted"] = !dryRun,
                    ["directory"] = folder, ["sessionId"] = batch["sessionId"]!.DeepClone(), ["currentSession"] = batch["currentSession"]!.DeepClone(),
                    ["folderRetained"] = unknown.Count > 0, ["unknownEntries"] = unknown };
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
                int kept = ex.Data["succeeded"] is int succeeded ? succeeded : 0;
                int count = ex.Data["total"] is int total ? total : 1;
                error = new Error("Staging IO failed for " + (tool == "CleanupStagedImportFiles" ? "batchId " + batchId : "files") + ": " + ex.Message + " Attempted path: " + attemptedPath,
                    partial ? (ErrorDetails)new PartialFailureDetails(kept, 1, Math.Max(0, count - kept - 1)) : new IoFailedDetails("import-staging", attemptedPath));
                outcome = partial ? Outcome.Partial : Outcome.RejectedBeforeOperation; execution = partial ? Execution.Partial : Execution.NotStarted;
            }
            if ((bool?)data?["folderRetained"] == true) warnings.Add(new Warning(WarningCode.StagingFolderRetained,
                "The batch folder is retained because it contains unknown entries; only staging-owned files are deleted.",
                new Dictionary<string, JsonElement> { ["unknownEntries"] = JsonSerializer.SerializeToElement(data["unknownEntries"]) }));
            return Envelope.Create(data, error, new Meta(DateTimeOffset.UtcNow, release, tool, Meta.Correlate(id), outcome, execution,
                false, BehaviorPolicy.NotApplicable, error == null ? ((bool?)data?["truncated"] == true ? Completeness.Partial : Completeness.Complete) : execution == Execution.NotStarted ? Completeness.None : Completeness.Partial, null, warnings));
        }
    }
}
