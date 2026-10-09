using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TiaMcp.Logic.Generation
{
    public sealed class PackageLoadLimits
    {
        public int MaximumFiles { get; }
        public int MaximumEntries { get; }
        public long MaximumFileBytes { get; }
        public long MaximumTotalBytes { get; }
        public long MaximumArchiveBytes { get; }

        public PackageLoadLimits(int maximumFiles = 1024, int maximumEntries = 2048,
            long maximumFileBytes = 16 * 1024 * 1024, long maximumTotalBytes = 64 * 1024 * 1024,
            long maximumArchiveBytes = 32 * 1024 * 1024)
        {
            if (maximumFiles < 1 || maximumEntries < maximumFiles || maximumFileBytes < 1 || maximumFileBytes > int.MaxValue
                || maximumTotalBytes < maximumFileBytes || maximumArchiveBytes < 1)
                throw new ArgumentOutOfRangeException(nameof(maximumFiles), "Package limits must be positive and consistent.");
            MaximumFiles = maximumFiles;
            MaximumEntries = maximumEntries;
            MaximumFileBytes = maximumFileBytes;
            MaximumTotalBytes = maximumTotalBytes;
            MaximumArchiveBytes = maximumArchiveBytes;
        }
    }

    public sealed class StandardPackage
    {
        private readonly IReadOnlyDictionary<string, byte[]> files;
        public IReadOnlyDictionary<string, JsonElement> Documents { get; }
        public StandardPackageManifest Manifest => GenerationDocuments.Deserialize<StandardPackageManifest>(Documents["package.json"]);
        public string ContentHash { get; }

        internal StandardPackage(IDictionary<string, byte[]> files, IDictionary<string, JsonElement> documents)
        {
            this.files = new ReadOnlyDictionary<string, byte[]>(new Dictionary<string, byte[]>(files, StringComparer.Ordinal));
            Documents = new ReadOnlyDictionary<string, JsonElement>(new Dictionary<string, JsonElement>(documents, StringComparer.Ordinal));
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("format", "tiamcp.package-content/1");
                writer.WriteStartArray("files");
                foreach (var file in files.OrderBy(f => f.Key, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", file.Key);
                    writer.WriteString("sha256", CanonicalJson.HashBytes(file.Value));
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            ContentHash = CanonicalJson.Hash(CanonicalJson.Parse(stream.ToArray()));
        }

        public T GetPart<T>(string path) where T : class => GenerationDocuments.Load<T>(Documents[path].GetRawText());
        public byte[] ReadFile(string path) => (byte[])files[path].Clone();
        public IReadOnlyCollection<string> FileNames => files.Keys.ToArray();

        public MachineDescription ValidateMachine(string json)
        {
            var machine = GenerationDocuments.Load<MachineDescription>(json);
            if (machine.Standard.Package != Manifest.Id) throw CanonicalJson.Failure("/standard/package", "reference", "Machine must select this package.");
            var definitions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var path in Manifest.Parts.Rules ?? new List<string>())
                definitions.Add(Documents[path].GetProperty("deviceType").GetString()!, Documents[path]);
            var value = CanonicalJson.Parse(json);
            var index = 0;
            var errors = new List<GenerationValidationError>();
            foreach (var device in value.GetProperty("devices").EnumerateArray())
            {
                var location = "/devices/" + index++;
                if (!definitions.TryGetValue(device.GetProperty("type").GetString()!, out var rule))
                { errors.Add(new GenerationValidationError(location + "/type", "reference", "Device type is not defined in this package; resolve inherited packages first.")); continue; }
                foreach (var error in GenerationSchemas.ValidateSchema(rule.GetProperty("params"), device.GetProperty("params"), "common.schema.json"))
                    errors.Add(new GenerationValidationError(location + "/params" + error.Path, error.Rule, error.Message));
                var signals = new HashSet<string>(rule.GetProperty("signals").EnumerateArray().Select(s => s.GetProperty("role").GetString()!), StringComparer.Ordinal);
                foreach (var io in device.GetProperty("io").EnumerateObject())
                    if (!signals.Contains(io.Name)) errors.Add(new GenerationValidationError(location + "/io/" + CanonicalJson.Pointer(io.Name), "reference", "Unknown signal role."));
            }
            if (!Manifest.Targets.Releases.Contains(machine.Target.Release)) errors.Add(new GenerationValidationError("/target/release", "release-coverage", "Package does not support the machine target release."));
            if (errors.Count != 0) throw new GenerationValidationException(errors);
            return machine;
        }

        public void WriteCanonicalZip(Stream destination)
        {
            using var archive = new ZipArchive(destination, ZipArchiveMode.Create, true);
            foreach (var file in files.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(file.Key, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var output = entry.Open();
                output.Write(file.Value, 0, file.Value.Length);
            }
        }
    }

    public static class StandardPackageLoader
    {
        public static StandardPackage LoadDirectory(string directory, PackageLoadLimits? limits = null)
        {
            limits ??= new PackageLoadLimits();
            try
            {
                var root = Path.GetFullPath(directory);
                RejectLink(root, "");
                var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                var names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                var directoryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                long total = 0;
                var entries = 0;
                Walk(root, "");
                return Finish(files);

                void Walk(string folder, string prefix)
                {
                    // Enumerate one level at a time so count limits also bound directory traversal.
                    foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
                    {
                        var name = prefix + Path.GetFileName(entry);
                        ValidatePath(name);
                        if (++entries > limits.MaximumEntries) throw CanonicalJson.Failure(name, "entry-count", "Too many package entries.");
                        RejectLink(entry, name);
                        var isDirectory = (File.GetAttributes(entry) & FileAttributes.Directory) != 0;
                        Register(names, directoryNames, name, isDirectory);
                        if (isDirectory) Walk(entry, name + "/");
                        else
                        {
                            if (files.Count >= limits.MaximumFiles) throw CanonicalJson.Failure(name, "file-count", "Too many package files.");
                            using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                            files.Add(name, ReadBounded(stream, stream.Length, name, limits, ref total));
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            { throw CanonicalJson.Failure("", "io", ex.Message); }
        }

        public static StandardPackage LoadZip(string path, PackageLoadLimits? limits = null)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return LoadZip(stream, limits);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            { throw CanonicalJson.Failure("", "zip", ex.Message); }
        }

        public static StandardPackage LoadZip(Stream source, PackageLoadLimits? limits = null)
        {
            limits ??= new PackageLoadLimits();
            try
            {
                if (!source.CanSeek || !source.CanRead) throw CanonicalJson.Failure("", "zip", "Archive stream must be readable and seekable.");
                if (source.Length > limits.MaximumArchiveBytes) throw CanonicalJson.Failure("", "archive-size", "Archive exceeds the compressed size limit.");
                using var archive = new ZipArchive(source, ZipArchiveMode.Read, true, new UTF8Encoding(false, true));
                if (archive.Entries.Count > limits.MaximumEntries) throw CanonicalJson.Failure("", "entry-count", "Too many archive entries.");
                var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                var names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                var directoryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                long total = 0;
                foreach (var entry in archive.Entries)
                {
                    var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                    var name = isDirectory ? entry.FullName.Substring(0, entry.FullName.Length - 1) : entry.FullName;
                    ValidatePath(name);
                    var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
                    if (unixType != 0 && unixType != (isDirectory ? 0x4000 : 0x8000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                        throw CanonicalJson.Failure(name, "link", "Links and special archive entries are prohibited.");
                    Register(names, directoryNames, name, isDirectory);
                    if (isDirectory)
                    {
                        if (entry.Length != 0) throw CanonicalJson.Failure(name, "zip", "Directory entries must be empty.");
                        continue;
                    }
                    if (files.Count >= limits.MaximumFiles) throw CanonicalJson.Failure(name, "file-count", "Too many package files.");
                    using var stream = entry.Open();
                    files.Add(name, ReadBounded(stream, entry.Length, name, limits, ref total));
                }
                return Finish(files);
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is NotSupportedException)
            { throw CanonicalJson.Failure("", "zip", ex.Message); }
        }

        internal static void ValidatePath(string name)
        {
            if (name.Length == 0 || name.Length > 240 || name.Split('/').Length > 16 || name != name.Normalize(NormalizationForm.FormC)
                || name.Any(ch => ch < 32 || ch == 127 || "\\:*?\"<>|".IndexOf(ch) >= 0))
                throw CanonicalJson.Failure(name, "path", "Package path is not a portable relative path.");
            foreach (var segment in name.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == ".." || segment.EndsWith(".", StringComparison.Ordinal)
                    || segment.EndsWith(" ", StringComparison.Ordinal) || Regex.IsMatch(segment, @"\A(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    throw CanonicalJson.Failure(name, "path", "Package path contains an unsafe segment.");
            }
        }

        private static void RejectLink(string path, string name)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw CanonicalJson.Failure(name, "link", "Symbolic links and reparse points are prohibited.");
        }

        private static void Register(IDictionary<string, bool> names, IDictionary<string, string> directoryNames, string name, bool directory)
        {
            if (names.ContainsKey(name)) throw CanonicalJson.Failure(name, "duplicate-name", "Package names must be unique ignoring case.");
            names.Add(name, directory);
            var prefix = directory ? name : name.LastIndexOf('/') < 0 ? "" : name.Substring(0, name.LastIndexOf('/'));
            while (prefix.Length > 0)
            {
                if (directoryNames.TryGetValue(prefix, out var spelling) && spelling != prefix)
                    throw CanonicalJson.Failure(name, "duplicate-name", "Directory names must use consistent casing.");
                directoryNames[prefix] = prefix;
                if (names.TryGetValue(prefix, out var isDirectory) && !isDirectory)
                    throw CanonicalJson.Failure(name, "duplicate-name", "A file is also used as a directory.");
                prefix = prefix.LastIndexOf('/') < 0 ? "" : prefix.Substring(0, prefix.LastIndexOf('/'));
            }
            if (!directory && names.Keys.Any(key => key.StartsWith(name + "/", StringComparison.OrdinalIgnoreCase)))
                throw CanonicalJson.Failure(name, "duplicate-name", "A directory is also used as a file.");
        }

        private static byte[] ReadBounded(Stream input, long declared, string name, PackageLoadLimits limits, ref long total)
        {
            if (declared > limits.MaximumFileBytes) throw CanonicalJson.Failure(name, "file-size", "Package file exceeds the size limit.");
            if (declared > limits.MaximumTotalBytes - total) throw CanonicalJson.Failure(name, "total-size", "Package exceeds the total size limit.");
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > limits.MaximumFileBytes) throw CanonicalJson.Failure(name, "file-size", "Decompressed file exceeds the size limit.");
                if (read > limits.MaximumTotalBytes - total) throw CanonicalJson.Failure(name, "total-size", "Decompressed package exceeds the total size limit.");
                total += read;
                output.Write(buffer, 0, read);
            }
            if (output.Length != declared) throw CanonicalJson.Failure(name, "file-size", "Actual length differs from declared file length.");
            return output.ToArray();
        }

        private static StandardPackage Finish(IDictionary<string, byte[]> files)
        {
            if (!files.ContainsKey("package.json")) throw CanonicalJson.Failure("package.json", "required", "Package root must contain package.json.");
            var documents = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var name in files.Keys.ToArray())
            {
                if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                var value = CanonicalJson.Parse(files[name], name);
                documents.Add(name, value);
                files[name] = CanonicalJson.Encode(value);
            }
            var manifest = documents["package.json"];
            RequireFileSchema("package", manifest, "package.json");
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.GetProperty("files").EnumerateArray())
            {
                var name = file.GetProperty("path").GetString()!;
                ValidatePath(name);
                if (!listed.Add(name)) throw CanonicalJson.Failure(name, "duplicate-name", "Manifest lists a file more than once.");
                if (name == "package.json") throw CanonicalJson.Failure(name, "file-hash", "Manifest cannot list its own hash.");
                if (!files.TryGetValue(name, out var bytes)) throw CanonicalJson.Failure(name, "file-reference", "Manifest file is missing or has different casing.");
                if (CanonicalJson.HashBytes(bytes) != file.GetProperty("sha256").GetString()) throw CanonicalJson.Failure(name, "file-hash", "Manifest content hash does not match.");
            }
            foreach (var name in files.Keys.Where(n => n != "package.json"))
                if (!listed.Contains(name)) throw CanonicalJson.Failure(name, "file-reference", "Every package file must be listed in the manifest.");
            var partPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in manifest.GetProperty("parts").EnumerateObject())
            {
                var paths = part.Name == "rules" ? part.Value.EnumerateArray().Select(p => p.GetString()!) : new[] { part.Value.GetString()! };
                foreach (var name in paths)
                {
                    ValidatePath(name);
                    if (!partPaths.Add(name) || name == "package.json") throw CanonicalJson.Failure(name, "duplicate-name", "Each part must have a distinct JSON file.");
                    if (!documents.TryGetValue(name, out var value)) throw CanonicalJson.Failure(name, "part-reference", "Part must reference an existing JSON file.");
                    RequireFileSchema(part.Name == "rules" ? "rule" : part.Name, value, name);
                }
            }
            GenerationModelValidation.ValidatePackage(manifest, documents, files.Keys);
            return new StandardPackage(files, documents);
        }

        private static void RequireFileSchema(string schema, JsonElement value, string name)
        {
            var errors = GenerationSchemas.Validate(schema, value).Select(e => new GenerationValidationError(name + "#" + e.Path, e.Rule, e.Message)).ToArray();
            if (errors.Length != 0) throw new GenerationValidationException(errors);
            try { GenerationDocuments.ValidateSemantics(schema, value); }
            catch (GenerationValidationException ex)
            { throw new GenerationValidationException(ex.Errors.Select(e => new GenerationValidationError(name + "#" + e.Path, e.Rule, e.Message))); }
        }
    }
}
