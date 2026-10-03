using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    public static class UnifiedCwcPackage
    {
        static void NoLinks(string path)
        {
            for (FileSystemInfo? p = new DirectoryInfo(path); p != null; p = p is DirectoryInfo d ? d.Parent : ((FileInfo)p).Directory)
                if (p.Exists && (p.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Reparse paths refused.");
        }
        static void UniqueKeys(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object) {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject()) { if (!names.Add(p.Name)) throw new ArgumentException("Duplicate manifest property: " + p.Name); UniqueKeys(p.Value); }
            } else if (e.ValueKind == JsonValueKind.Array) foreach (var p in e.EnumerateArray()) UniqueKeys(p);
        }
        public static JsonObject Run(string directory, string action, string outputPath, bool dryRun, string expectedFingerprint)
        {
            if (action != "inspect" && action != "build") throw new ArgumentException("action must be inspect/build.");
            if (!Path.IsPathRooted(directory) || !Directory.Exists(directory)) throw new ArgumentException("Existing absolute CWC source directory required.");
            var root = new DirectoryInfo(Path.GetFullPath(directory)); NoLinks(root.FullName);
            var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal); var queue = new Queue<DirectoryInfo>(); queue.Enqueue(root);
            long total = 0; int directories = 0;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue(); if (++directories > 256) throw new ArgumentException("CWC directory budget exceeded.");
                foreach (var item in current.EnumerateFileSystemInfos())
                {
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Reparse package entries refused.");
                    if (item is DirectoryInfo child) { if (queue.Count >= 256) throw new ArgumentException("CWC directory budget exceeded."); queue.Enqueue(child); continue; }
                    var file = (FileInfo)item;
                    if (files.Count >= 1024 || file.Length > 16 * 1024 * 1024 || total + file.Length > 64 * 1024 * 1024) throw new ArgumentException("CWC supports <=1024 files, <=16 MiB each and <=64 MiB total.");
                    using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var content = new MemoryStream(); var buffer = new byte[8192]; int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0) { total += n; if (content.Length + n > 16 * 1024 * 1024 || total > 64 * 1024 * 1024) throw new ArgumentException("CWC byte budget exceeded while reading."); content.Write(buffer, 0, n); }
                    files.Add(file.FullName.Substring(root.FullName.TrimEnd('\\', '/').Length + 1).Replace('\\', '/'), content.ToArray());
                }
            }
            if (!files.TryGetValue("manifest.json", out var manifest) || manifest.Length > 1024 * 1024) throw new ArgumentException("Root manifest.json <=1 MiB required.");
            var jsonText = new UTF8Encoding(false, true).GetString(manifest).TrimStart('\uFEFF');
            using var json = JsonDocument.Parse(jsonText); UniqueKeys(json.RootElement);
            var doc = JsonNode.Parse(jsonText)?.AsObject() ?? throw new ArgumentException("Manifest object required.");
            string Required(JsonNode? value, string name) { var s = value?.GetValue<string>(); return !string.IsNullOrWhiteSpace(s) ? s! : throw new ArgumentException(name + " must be a nonempty string."); }
            Required(doc["mver"], "mver");
            var identity = doc["control"]?["identity"]?.AsObject() ?? throw new ArgumentException("control.identity required.");
            var name = Required(identity["name"], "name"); var version = Required(identity["version"], "version"); Required(identity["displayname"], "displayname");
            var type = Required(identity["type"], "type");
            if (!type.StartsWith("guid://", StringComparison.Ordinal) || !Guid.TryParseExact(type.Substring(7), "D", out var guid)) throw new ArgumentException("identity.type must be guid:// followed by an 8-4-4-4-12 GUID.");
            string LocalReference(string value)
            {
                if (!value.StartsWith("./", StringComparison.Ordinal)) throw new ArgumentException("Local manifest references must start with ./.");
                var relative = value.Substring(2);
                if (relative.Split('/').Any(p => p == ".." || p == "." || p.Length == 0) || relative.IndexOfAny(new[] { '\\', ':', '?', '#', '\0' }) >= 0 || !files.ContainsKey(relative)) throw new ArgumentException("Missing or invalid local manifest reference: " + value);
                return relative;
            }
            var start = LocalReference(identity["start"]?.GetValue<string>() ?? "./control/index.html");
            var warnings = new JsonArray();
            var icon = identity["icon"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(icon)) { if (icon!.StartsWith("./", StringComparison.Ordinal)) LocalReference(icon); else warnings.Add("External/data icon was not fetched or decoded."); }
            if (!files.Keys.Any(p => p.StartsWith("control/", StringComparison.Ordinal))) throw new ArgumentException("control directory content required.");
            if (!files.Keys.Any(p => p.StartsWith("assets/", StringComparison.Ordinal))) warnings.Add("No assets files; icon is optional in the manifest contract.");
            var inventory = new JsonArray(files.Select(p => (JsonNode)new JsonObject { ["path"] = p.Key, ["bytes"] = p.Value.Length, ["sha256"] = PlcDocumentEditing.Hash(p.Value) }).ToArray());
            var fingerprint = PlcDocumentEditing.HashText(inventory.ToJsonString());
            var suggestedName = "{" + guid.ToString("D").ToUpperInvariant() + "}.zip";
            var result = new JsonObject { ["name"] = name, ["version"] = version, ["guid"] = guid.ToString("D"), ["start"] = start,
                ["packageFingerprint"] = fingerprint, ["suggestedFileName"] = suggestedName, ["files"] = inventory, ["warnings"] = warnings,
                ["basicChecksPassed"] = true, ["fullManifestSchemaValidated"] = false, ["runtimeValidated"] = false,
                ["dryRun"] = dryRun, ["written"] = false, ["nativeTiaExecuted"] = false,
                ["scope"] = "Folder/identity/start/local icon checks and content snapshot only. HTML/JavaScript, contracts, types, image dimensions, extensions and target-device support require separate validation. Does not create or rename a Unified faceplate/library type." };
            if (action == "inspect" || dryRun) return result;
            if (fingerprint != expectedFingerprint) throw new InvalidOperationException("Package content changed or preview fingerprint missing. No file written.");
            if (!Path.IsPathRooted(outputPath) || !string.Equals(Path.GetFileName(outputPath), suggestedName, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Absolute outputPath must use the suggested GUID ZIP filename.");
            var output = Path.GetFullPath(outputPath); NoLinks(Path.GetDirectoryName(output)!);
            if (!Directory.Exists(Path.GetDirectoryName(output)) || output.StartsWith(root.FullName.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must be in an existing directory outside the source tree.");
            // Snapshot bytes, not reopened inputs, are archived. Existing ZIPs are never replaced.
            using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                foreach (var file in files) { var entry = archive.CreateEntry(file.Key, CompressionLevel.Optimal); entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); using var dest = entry.Open(); dest.Write(file.Value, 0, file.Value.Length); }
            result["written"] = true; result["outputPath"] = output; result["sha256"] = PlcDocumentEditing.Hash(File.ReadAllBytes(output));
            return result;
        }
    }
}
