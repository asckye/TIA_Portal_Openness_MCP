using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // No Siemens dependency: request gating and file rules for the Unified native exchange tools
    // (HmiTagComposition.Export/Import, HmiScriptModuleComposition.Export/Import, OpcUaAlarm.Import).
    internal static class UnifiedExchangeLogic
    {
        internal static readonly string[] ExchangeActions = { "export", "import" };
        internal static readonly string[] OpcUaAlarmActions = { "read", "import" };

        // Exports go to a NEW absolute directory (native Export overwrites silently); imports read an existing one.
        internal static DirectoryInfo ValidateDirectory(string directory, bool forExport)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory)) throw new ArgumentException("Absolute directory on the MCP server required.");
            var dir = new DirectoryInfo(directory);
            if (forExport && dir.Exists) throw new InvalidOperationException("Use a new export directory; overwrites refused: " + dir.FullName);
            if (forExport && dir.Parent?.Exists != true) throw new DirectoryNotFoundException("Export parent directory must exist: " + dir.FullName);
            if (!forExport && !dir.Exists) throw new DirectoryNotFoundException("Import directory not found: " + dir.FullName);
            return dir;
        }

        // Optional native file name: plain name without path separators or extension games.
        internal static string ValidateFileName(string fileName)
        {
            var name = (fileName ?? "").Trim();
            if (name.Length == 0) return "";
            if (name.Length > 128 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(new[] { '/', '\\' }) >= 0 || name == "." || name == "..")
                throw new ArgumentException("fileName must be a plain file name without path separators.");
            return name;
        }

        internal static string[] ParseExpectedNames(string json, int max = 500)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Trim() == "[]") return Array.Empty<string>();
            var array = JsonNode.Parse(json) as JsonArray ?? throw new ArgumentException("expectedNamesJson must be a JSON array of exact names.");
            var names = array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new ArgumentException("Every expected name must be a string.")).ToArray();
            if (names.Length > max || names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length) throw new ArgumentException("Provide up to " + max + " distinct nonempty names.");
            return names;
        }

        internal static string ValidateXmlPath(string xmlPath)
        {
            if (string.IsNullOrWhiteSpace(xmlPath) || !Path.IsPathRooted(xmlPath)) throw new ArgumentException("Absolute xmlPath on the MCP server required.");
            if (!string.Equals(Path.GetExtension(xmlPath), ".xml", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("OpcUaAlarm.Import reads an .xml file.");
            return Path.GetFullPath(xmlPath);
        }

        // Native Export results are FileInfo sequences; every file must be inside the export directory and nonempty.
        // HmiTagComposition.Export(dir, name) reports "<dir>\<name>" without the ".hmi.yml" extension TIA actually writes
        // (real project, 2.7.28), so a reported path that does not exist is resolved to the file TIA created from it.
        internal static readonly string[] NativeExtensions = { ".hmi.yml", ".hmi.js", ".yml", ".js", ".xlsx", ".xml" };
        internal static JsonArray VerifyNativeFiles(object nativeResult, DirectoryInfo dir)
        {
            if (nativeResult is not IEnumerable sequence || nativeResult is string) throw new InvalidOperationException("Native Export returned no file sequence.");
            var records = new JsonArray(); string prefix = dir.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var item in sequence)
            {
                var reported = item as FileInfo ?? throw new InvalidOperationException("Unexpected native output entry; export not complete.");
                if (!reported.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Native export file outside the export directory: " + reported.FullName);
                var file = ResolveReportedFile(reported, dir) ?? throw new InvalidOperationException("Native export file missing or empty: " + reported.FullName);
                using var sha = SHA256.Create(); using var stream = file.OpenRead();
                var row = new JsonObject { ["path"] = file.FullName, ["bytes"] = file.Length, ["sha256"] = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() };
                if (!string.Equals(file.FullName, reported.FullName, StringComparison.OrdinalIgnoreCase)) row["reportedPath"] = reported.FullName;
                records.Add(row);
            }
            if (records.Count == 0) throw new InvalidOperationException("Native export returned no files.");
            return records;
        }
        internal static FileInfo? ResolveReportedFile(FileInfo reported, DirectoryInfo dir)
        {
            reported.Refresh();
            if (reported.Exists && reported.Length > 0) return reported;
            foreach (var extension in NativeExtensions)
            {
                var candidate = new FileInfo(reported.FullName + extension);
                if (candidate.Exists && candidate.Length > 0) return candidate;
            }
            var stem = reported.Name + ".";
            return dir.Exists ? dir.EnumerateFiles().Where(f => f.Name.StartsWith(stem, StringComparison.OrdinalIgnoreCase) && f.Length > 0).OrderBy(f => f.Name, StringComparer.Ordinal).FirstOrDefault() : null;
        }
        // Everything TIA left in the export directory, so side files (NameData.yml, *.def.hmi.yml) are visible even when the native result omits them.
        internal static JsonArray DirectoryListing(DirectoryInfo dir)
        {
            var rows = new JsonArray();
            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories).OrderBy(f => f.FullName, StringComparer.Ordinal).Take(500))
                rows.Add(new JsonObject { ["path"] = file.FullName, ["bytes"] = file.Length });
            return rows;
        }

        // Import(DirectoryInfo) reads *.hmi.yml (tags) / *.js-style module files present in the directory; report what is there so an empty directory is not mistaken for success.
        internal static JsonArray ListImportCandidates(DirectoryInfo dir, string[] extensions)
        {
            var rows = new JsonArray();
            foreach (var file in dir.EnumerateFiles().Where(f => extensions.Any(e => f.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase))).OrderBy(f => f.Name, StringComparer.Ordinal).Take(200))
                rows.Add(new JsonObject { ["name"] = file.Name, ["bytes"] = file.Length });
            return rows;
        }

        internal static bool IsUnifiedTagCollection(object? value) => value?.GetType().FullName == "Siemens.Engineering.HmiUnified.HmiTags.HmiTagComposition";
    }
}
