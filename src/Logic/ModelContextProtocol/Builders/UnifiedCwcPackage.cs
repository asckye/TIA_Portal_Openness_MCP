using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;

namespace TiaMcpServer.ModelContextProtocol
{
    public static class UnifiedCwcPackage
    {
        private static void NoLinks(string path, string parameter)
        {
            for (FileSystemInfo? p = new DirectoryInfo(path); p != null; p = p is DirectoryInfo d ? d.Parent : ((FileInfo)p).Directory)
                if (p.Exists && (p.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new AdapterPreconditionException("Use a path without reparse points in its ancestry.", parameter);
        }
        private static void UniqueKeys(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object) {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject()) {
                    if (!names.Add(p.Name)) throw new AdapterPreconditionException("Remove duplicate properties from the package manifest.", "directory"); UniqueKeys(p.Value); }
            } else if (e.ValueKind == JsonValueKind.Array) foreach (var p in e.EnumerateArray()) UniqueKeys(p);
        }
        public static JsonObject Run(string directory, string action, string outputPath, bool dryRun, string expectedFingerprint)
        {
            if (action != "inspect" && action != "build") throw new AdapterPreconditionException("Set action to inspect or build.", "action");
            if (!Path.IsPathRooted(directory) || !Directory.Exists(directory))
                throw new AdapterPreconditionException("Supply an existing absolute CWC source directory.", "directory");
            var root = new DirectoryInfo(Path.GetFullPath(directory)); NoLinks(root.FullName, "directory");
            var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal); var queue = new Queue<DirectoryInfo>(); queue.Enqueue(root);
            long total = 0; int directories = 0;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue(); if (++directories > 256) throw new AdapterPreconditionException("Reduce the CWC source to at most 256 directories.", "directory");
                foreach (var item in current.EnumerateFileSystemInfos())
                {
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new AdapterPreconditionException("Remove reparse entries from the CWC source.", "directory");
                    if (item is DirectoryInfo child) {
                        if (queue.Count >= 256) throw new AdapterPreconditionException("Reduce the CWC source to at most 256 directories.", "directory"); queue.Enqueue(child); continue; }
                    var file = (FileInfo)item;
                    if (files.Count >= 1024 || file.Length > 16 * 1024 * 1024 || total + file.Length > 64 * 1024 * 1024)
                        throw new AdapterPreconditionException("Reduce the CWC source to <=1024 files, <=16 MiB each and <=64 MiB total.", "directory");
                    using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var content = new MemoryStream(); var buffer = new byte[8192]; int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0) {
                        total += n; if (content.Length + n > 16 * 1024 * 1024 || total > 64 * 1024 * 1024)
                            throw new AdapterPreconditionException("Reduce the CWC source to <=16 MiB per file and <=64 MiB total.", "directory"); content.Write(buffer, 0, n);
                    }
                    files.Add(file.FullName.Substring(root.FullName.TrimEnd('\\', '/').Length + 1).Replace('\\', '/'), content.ToArray());
                }
            }
            if (!files.TryGetValue("manifest.json", out var manifest) || manifest.Length > 1024 * 1024)
                throw new AdapterPreconditionException("Supply root manifest.json of at most 1 MiB in the CWC source.", "directory");
            JsonObject doc;
            try
            {
                var jsonText = new UTF8Encoding(false, true).GetString(manifest).TrimStart('\uFEFF');
                using var json = JsonDocument.Parse(jsonText); UniqueKeys(json.RootElement);
                doc = JsonNode.Parse(jsonText) as JsonObject
                    ?? throw new AdapterPreconditionException("Supply a valid UTF-8 JSON object in the package manifest.", "directory");
            }
            catch (Exception ex) when (ex is ArgumentException || ex is JsonException)
            { throw new AdapterPreconditionException("Supply a valid UTF-8 JSON object in the package manifest.", "directory"); }
            string Required(JsonNode? value, string name) {
                return value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text
                    : throw new AdapterPreconditionException("Supply a nonempty string for manifest " + name + ".", "directory");
            }
            Required(doc["mver"], "mver");
            var identity = (doc["control"] as JsonObject)?["identity"] as JsonObject
                ?? throw new AdapterPreconditionException("Supply control.identity as an object in the package manifest.", "directory");
            var name = Required(identity["name"], "name"); var version = Required(identity["version"], "version");
            Required(identity["displayname"], "displayname");
            var type = Required(identity["type"], "type");
            if (!type.StartsWith("guid://", StringComparison.Ordinal) || !Guid.TryParseExact(type.Substring(7), "D", out var guid))
                throw new AdapterPreconditionException("Set manifest identity.type to guid:// followed by an 8-4-4-4-12 GUID.", "directory");
            string LocalReference(string value)
            {
                if (!value.StartsWith("./", StringComparison.Ordinal)) throw new AdapterPreconditionException("Start local manifest references with ./.", "directory");
                var relative = value.Substring(2);
                if (relative.Split('/').Any(p => p == ".." || p == "." || p.Length == 0) || relative.IndexOfAny(new[] { '\\', ':', '?', '#', '\0' }) >= 0
                        || !files.ContainsKey(relative))
                    throw new AdapterPreconditionException("Supply local manifest references to existing package files without traversal or reserved characters.", "directory");
                return relative;
            }
            var start = LocalReference(identity["start"] == null ? "./control/index.html" : Required(identity["start"], "identity.start"));
            var warnings = new JsonArray();
            string? icon = null;
            if (identity["icon"] != null)
            {
                if (identity["icon"] is not JsonValue iconValue || !iconValue.TryGetValue<string>(out icon))
                    throw new AdapterPreconditionException("Supply a string for manifest identity.icon.", "directory");
            }
            if (!string.IsNullOrEmpty(icon)) {
                if (icon!.StartsWith("./", StringComparison.Ordinal)) LocalReference(icon); else warnings.Add("External/data icon was not fetched or decoded.");
            }
            if (!files.Keys.Any(p => p.StartsWith("control/", StringComparison.Ordinal))) throw new AdapterPreconditionException("Supply control directory content in the CWC source.", "directory");
            if (!files.Keys.Any(p => p.StartsWith("assets/", StringComparison.Ordinal))) warnings
                .Add("No assets files; icon is optional in the manifest contract.");
            var inventory =
                new JsonArray(files
                    .Select(p => (JsonNode)new JsonObject { ["path"] = p.Key, ["bytes"] = p.Value.Length, ["sha256"] = PlcDocumentEditing.Hash(p.Value) })
                    .ToArray());
            var fingerprint = PlcDocumentEditing.HashText(inventory.ToJsonString());
            var suggestedName = "{" + guid.ToString("D").ToUpperInvariant() + "}.zip";
            var result = new JsonObject { ["name"] = name, ["version"] = version, ["guid"] = guid.ToString("D"), ["start"] = start,
                ["packageFingerprint"] = fingerprint, ["suggestedFileName"] = suggestedName, ["files"] = inventory, ["warnings"] = warnings,
                ["basicChecksPassed"] = true, ["fullManifestSchemaValidated"] = false, ["runtimeValidated"] = false,
                ["dryRun"] = dryRun, ["written"] = false, ["nativeTiaExecuted"] = false,
                ["scope"] =
                    "Folder/identity/start/local icon checks and content snapshot only. HTML/JavaScript, contracts, types, image dimensions, extensions and " +
                    "target-device support require separate validation. Does not create or rename a Unified faceplate/library type."
                };
            if (action == "inspect" || dryRun) return result;
            if (fingerprint != expectedFingerprint)
                throw new AdapterPreconditionException("Inspect or preview the package again and supply its unchanged expectedPackageHash. No file written.", "expectedPackageHash");
            if (!Path.IsPathRooted(outputPath) || !string.Equals(Path.GetFileName(outputPath), suggestedName, StringComparison.OrdinalIgnoreCase))
                throw new AdapterPreconditionException("Supply an absolute outputPath using the suggested GUID ZIP filename.", "outputPath");
            string output;
            try { output = Path.GetFullPath(outputPath); NoLinks(Path.GetDirectoryName(output)!, "outputPath"); }
            catch (ArgumentException)
            { throw new AdapterPreconditionException("Supply a valid absolute outputPath using the suggested GUID ZIP filename.", "outputPath"); }
            if (!Directory.Exists(Path.GetDirectoryName(output)) || output
                    .StartsWith(root.FullName.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new AdapterPreconditionException("Choose an existing output directory outside the source tree.", "outputPath");
            if (File.Exists(output) || Directory.Exists(output))
                throw new AdapterPreconditionException("Choose a new outputPath; existing files and directories cannot be overwritten.", "outputPath");
            // Snapshot bytes, not reopened inputs, are archived. Existing ZIPs are never replaced.
            using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                foreach (var file in files) {
                    var entry = archive.CreateEntry(file.Key, CompressionLevel.Optimal); entry.LastWriteTime =
                        new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); using var dest = entry.Open(); dest.Write(file.Value, 0, file.Value.Length);
                }
            result["written"] = true; result["outputPath"] = output; result["sha256"] = PlcDocumentEditing.Hash(File.ReadAllBytes(output));
            return result;
        }
    }
}
