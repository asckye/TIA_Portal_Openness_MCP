using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaOpenness.Shared;

namespace TiaMcp.Logic.Generation
{
    // Inventories are loaded afresh for each request; callers never receive mutable cached resources.
    public sealed class StandardPackageStore
    {
        public string RepositoryRoot { get; }
        public string UserRoot { get; }
        private const int MaximumPackages = 256;

        public static StandardPackageStore Create()
        {
            var bundle = BundleLayout.RequireRoot(AppContext.BaseDirectory);
            var data = DataLocations.Current.Root;
            if (data == null) throw new IOException("The bundle data directory is unavailable.");
            return new StandardPackageStore(Path.Combine(bundle, "templates", "standards"), Path.Combine(data, "standards"));
        }

        public StandardPackageStore(string repositoryRoot, string userRoot)
        {
            RepositoryRoot = Absolute(repositoryRoot, "repositoryRoot");
            UserRoot = Absolute(userRoot, "userRoot");
            if (Within(UserRoot, RepositoryRoot) || Within(RepositoryRoot, UserRoot))
                throw new ArgumentException("Repository and user package roots must be separate.");
        }

        public JsonObject List()
        {
            var items = new JsonArray();
            foreach (var entry in Inventory())
            {
                var row = Summary(entry.Package);
                row["source"] = entry.Repository ? "repository" : "user";
                row["readOnly"] = entry.Repository;
                row["path"] = entry.Path;
                row["valid"] = true;
                items.Add(row);
            }
            return new JsonObject { ["items"] = items, ["repositoryRoot"] = RepositoryRoot, ["userRoot"] = UserRoot };
        }

        public StandardPackage Load(string id, string version) => Find(id, version).Package;

        public StandardPackage Effective(StandardPackage package)
        {
            var available = Inventory().Select(e => e.Package).ToArray();
            var pinned = new Dictionary<string, StandardPackage>(StringComparer.Ordinal);
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            Visit(package);
            return EffectiveGenerationPackage.Resolve(package, pinned.Values.Where(p => p != package));

            void Visit(StandardPackage item)
            {
                var key = item.Manifest.Id + "@" + item.Manifest.Version;
                if (!visiting.Add(key)) throw CanonicalJson.Failure("/dependsOn", "dependency", "Cyclic package dependencies.");
                foreach (var reference in (item.Manifest.DependsOn ?? new List<PackageReference>())
                    .Concat(item.Manifest.Extends == null ? Array.Empty<PackageReference>() : new[] { item.Manifest.Extends }))
                {
                    var matches = available.Where(p => p.Manifest.Id == reference.Package
                        && GenerationPlanner.VersionMatches(reference.Version, p.Manifest.Version)).ToArray();
                    if (matches.Length != 1) throw CanonicalJson.Failure("/dependsOn", "dependency", "Pin exactly one installed version satisfying " + reference.Package + " " + reference.Version + ".");
                    if (pinned.TryGetValue(reference.Package, out var previous) && previous.ContentHash != matches[0].ContentHash)
                        throw CanonicalJson.Failure("/dependsOn", "dependency", "Conflicting dependency versions.");
                    pinned[reference.Package] = matches[0];
                    Visit(matches[0]);
                }
                visiting.Remove(key);
            }
        }

        public JsonObject Describe(string id, string version)
        {
            var original = Load(id, version);
            var package = Effective(original);
            var result = Summary(original);
            result["effectiveHash"] = package.ContentHash;
            result["deviceTypes"] = new JsonArray((package.Manifest.Parts.Rules ?? new List<string>())
                .Select(path => JsonNode.Parse(package.Documents[path].GetRawText())).ToArray());
            result["naming"] = Part(package, "naming");
            result["library"] = Part(package, "library");
            result["cpuSelection"] = new JsonObject {
                ["parameter"] = "devices[].params.programAlarm", ["default"] = false,
                ["S7-1200"] = "Keep programAlarm=false; the basic controls do not require Program_Alarm.",
                ["S7-1500"] = "programAlarm=true selects the isolated Program_Alarm adapter. CPU firmware and native import/compile remain NOT RUN."
            };
            result["coverage"] = Coverage(package);
            result["notices"] = new JsonArray(original.FileNames.Where(p => p == "LICENSE" || p == "NOTICE.md" || p == "sources.json")
                .OrderBy(p => p, StringComparer.Ordinal).Select(p => (JsonNode)new JsonObject { ["path"] = p, ["sha256"] = CanonicalJson.HashBytes(original.ReadFile(p)) }).ToArray());
            return result;
        }

        public JsonObject Validate(string id, string version, string sourcePath = "")
        {
            var original = sourcePath.Length == 0 ? Load(id, version) : LoadSource(sourcePath);
            var package = Effective(original);
            var result = Summary(original);
            result["valid"] = true;
            result["effectiveHash"] = package.ContentHash;
            result["coverage"] = Coverage(package);
            result["errors"] = new JsonArray();
            result["selfChecks"] = SelfChecks(package);
            result["nativeAcceptance"] = "NOT RUN";
            return result;
        }

        private static JsonArray SelfChecks(StandardPackage package)
        {
            var examples = package.Documents.Where(p => p.Value.ValueKind == JsonValueKind.Object
                && p.Value.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.String
                && schema.GetString() == "tiamcp.machine/1" && p.Value.GetProperty("standard").GetProperty("package").GetString() == package.Manifest.Id)
                .OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
            if (examples.Length > 16) throw new ArgumentException("At most 16 machine examples can be self-checked.");
            var checks = new JsonArray();
            foreach (var example in examples)
            foreach (var release in package.Manifest.Targets.Releases)
            {
                var machine = GenerationDocuments.Load<MachineDescription>(example.Value.GetRawText());
                machine.Target.Release = release;
                // Synthetic readback exercises expansion and naming without a session or file writes.
                var observed = new ProjectModel {
                    ProjectIdentity = machine.Target.Project.ProjectIdentity ?? "",
                    Devices = machine.Stations.Where(s => s.Role == "plc.main").Select(s => new ProjectDevice {
                        Station = s.Id, Name = s.Id, Article = s.Article ?? "", Firmware = s.Firmware ?? ""
                    }).ToList()
                };
                var options = new GenerationPlanningOptions { ArtifactRoot = "C:/tiamcp-offline-self-check" };
                string json = GenerationDocuments.Canonical(machine);
                var first = GenerationPlanner.Build(package, json, observed, options);
                var second = GenerationPlanner.Build(package, json, observed, options);
                if (first.Plan.SelfCheck.Errors != 0 || first.CanonicalPlan != second.CanonicalPlan
                    || !first.Artifacts.Select(a => a.Sha256).SequenceEqual(second.Artifacts.Select(a => a.Sha256)))
                    throw CanonicalJson.Failure("/" + example.Key, "self-check", "Generated names or deterministic expansion failed for " + release + ".");
                checks.Add(new JsonObject { ["example"] = example.Key, ["release"] = release, ["errors"] = 0,
                    ["warnings"] = first.Plan.SelfCheck.Warnings, ["deterministic"] = true,
                    ["planHash"] = first.Plan.PlanHash, ["unavailablePhases"] = new JsonArray(first.Plan.Unavailable
                        .Select(u => u.Phase).Distinct(StringComparer.Ordinal).Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
                    ["nativeAcceptance"] = "NOT RUN" });
            }
            if (examples.Length == 0) checks.Add(new JsonObject { ["status"] = "machineExampleRequired",
                ["message"] = "Package declarations were validated; generated-name checks require a packaged machine example." });
            return checks;
        }

        public JsonObject Manage(string action, string id, string version, string sourcePath, string outputPath,
            string newId, string newVersion, bool dryRun, string expectedPlanHash)
        {
            if (!new[] { "import", "export", "copy", "fork", "remove" }.Contains(action)) throw new ArgumentException("Unknown package action.", "action");
            if (action == "import" && (id.Length != 0 || version.Length != 0 || newId.Length != 0 || newVersion.Length != 0 || outputPath.Length != 0)
                || action != "import" && sourcePath.Length != 0 || action != "export" && outputPath.Length != 0
                || action is not ("copy" or "fork") && (newId.Length != 0 || newVersion.Length != 0))
                throw new ArgumentException("Arguments do not belong to this package action.", "action");
            var entry = action == "import" ? null : Find(id, version);
            var package = entry?.Package ?? LoadSource(sourcePath);
            if (action is "copy" or "fork")
            {
                if (newId == package.Manifest.Id || string.IsNullOrWhiteSpace(newId) || string.IsNullOrWhiteSpace(newVersion))
                    throw new ArgumentException("Copy/fork requires a new package id and an explicit SemVer version.", "newId");
                package = Fork(package, newId, newVersion);
            }
            if (action != "remove") Effective(package);
            string target;
            byte[]? zip = null;
            if (action == "export")
            {
                target = NewOutput(outputPath, ".zip");
                using var stream = new MemoryStream(); package.WriteCanonicalZip(stream); zip = stream.ToArray();
                if (zip.LongLength > new PackageLoadLimits().MaximumArchiveBytes) throw new ArgumentException("Export exceeds the import archive limit.", "outputPath");
            }
            else if (action == "remove")
            {
                if (entry!.Repository) throw new ArgumentException("Repository packages are read-only.", "packageId");
                target = entry.Path;
            }
            else
            {
                StandardPackageLoader.ValidatePath(package.Manifest.Id + "/" + package.Manifest.Version);
                target = Path.Combine(UserRoot, package.Manifest.Id, package.Manifest.Version);
                Safe(target);
                if (Inventory().Any(e => e.Package.Manifest.Id == package.Manifest.Id && e.Package.Manifest.Version == package.Manifest.Version)
                    || File.Exists(target) || Directory.Exists(target)) throw new ArgumentException("Package id/version already exists; use a new identity.", "packageId");
            }
            var result = Summary(package);
            result["action"] = action; result["targetPath"] = target; result["dryRun"] = dryRun; result["executed"] = false;
            JsonNode planTarget = action == "export" ? JsonValue.Create(outputPath)! : new JsonObject {
                ["store"] = "user", ["path"] = target.Substring(UserRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/') };
            var plan = new JsonObject { ["action"] = action, ["target"] = planTarget, ["sourcePath"] = sourcePath, ["contentHash"] = package.ContentHash,
                ["sourceHash"] = entry?.Package.ContentHash ?? package.ContentHash, ["archiveHash"] = zip == null ? null : CanonicalJson.HashBytes(zip) };
            string hash = CanonicalJson.Hash(CanonicalJson.Parse(plan.ToJsonString()));
            result["planHash"] = hash;
            RequirePlan(dryRun, expectedPlanHash, hash);
            if (dryRun) return result;
            Safe(target);
            if (action == "export")
            {
                using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(zip!, 0, zip!.Length); file.Flush(true);
            }
            else if (action == "remove") Remove(entry!);
            else Publish(package, target);
            result["executed"] = true;
            return result;
        }

        public JsonObject Machine(string action, string id, string version, string machineJson, string inputPath,
            string outputPath, string format, bool dryRun, string expectedPlanHash)
        {
            if (!new[] { "validate", "import", "export", "exportTemplate" }.Contains(action)) throw new ArgumentException("Unknown machine action.", "action");
            if (format != "json" && format != "csv") throw new ArgumentException("Only JSON and package-generated CSV are supported; xlsx is deferred.", "format");
            if (action == "import" && machineJson.Length != 0 || action != "import" && inputPath.Length != 0
                || action == "exportTemplate" && machineJson.Length != 0 || action == "validate" && outputPath.Length != 0)
                throw new ArgumentException("Arguments do not belong to this machine action.", "action");
            var package = Effective(Load(id, version));
            if (action == "import") machineJson = format == "json" ? ReadJson(inputPath) : MachineCsv.Import(package, inputPath);
            if (action == "exportTemplate")
            {
                if (format != "csv") throw new ArgumentException("The spreadsheet template uses CSV.", "format");
                machineJson = MachineCsv.EmptyMachine(package);
            }
            if (Encoding.UTF8.GetByteCount(machineJson) > 4 * 1024 * 1024) throw new ArgumentException("Machine JSON exceeds 4 MiB.", "machine");
            var machine = package.ValidateMachine(machineJson);
            if (!GenerationPlanner.VersionMatches(machine.Standard.Version, package.Manifest.Version))
                throw CanonicalJson.Failure("/standard/version", "version", "Selected package does not satisfy the machine version range.");
            var cpuWarnings = new JsonArray();
            foreach (var device in machine.Devices.Where(d => d.Params.TryGetValue("programAlarm", out var alarm) && alarm.ValueKind == JsonValueKind.True))
            {
                var station = machine.Stations.Single(s => s.Id == device.Station);
                // The basic package deliberately isolates an S7-1500-only instruction.
                if (station.Kind == "S7-1200" || (station.Article ?? "").Replace(" ", "").StartsWith("6ES721", StringComparison.OrdinalIgnoreCase))
                    throw CanonicalJson.Failure("/devices/" + machine.Devices.IndexOf(device) + "/params/programAlarm", "cpu", "Program_Alarm is unavailable on S7-1200; select false.");
                cpuWarnings.Add("Verify S7-1500 CPU/firmware support for device " + device.Id + "; native acceptance NOT RUN.");
            }
            string canonical = Encoding.UTF8.GetString(CanonicalJson.Encode(CanonicalJson.Parse(machineJson)));
            var result = new JsonObject { ["valid"] = true, ["machine"] = JsonNode.Parse(canonical), ["machineHash"] = GenerationDocuments.MachineHash(canonical),
                ["packageHash"] = package.ContentHash, ["cpuWarnings"] = cpuWarnings, ["dryRun"] = dryRun, ["executed"] = false };
            if (action == "validate" || action == "import" && outputPath.Length == 0) return result;
            var files = format == "csv" && action != "import" ? MachineCsv.Export(package, canonical) :
                new Dictionary<string, byte[]> { ["machine.json"] = Encoding.UTF8.GetBytes(canonical + "\n") };
            string target = files.Count == 1 ? NewOutput(outputPath, ".json") : NewDirectory(outputPath);
            var fileHashes = new JsonObject();
            foreach (var file in files) fileHashes[file.Key] = CanonicalJson.HashBytes(file.Value);
            string hash = CanonicalJson.Hash(CanonicalJson.Parse(new JsonObject { ["action"] = action, ["target"] = outputPath, ["inputPath"] = inputPath,
                ["packageHash"] = package.ContentHash, ["files"] = fileHashes.DeepClone() }.ToJsonString()));
            result["planHash"] = hash; result["outputPath"] = target; result["files"] = fileHashes;
            RequirePlan(dryRun, expectedPlanHash, hash);
            if (dryRun) return result;
            if (files.Count == 1)
            {
                using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var bytes = files.Values.Single(); file.Write(bytes, 0, bytes.Length); file.Flush(true);
            }
            else PublishFiles(files, target);
            result["executed"] = true;
            return result;
        }

        private sealed class Entry
        {
            internal string Path = "";
            internal bool Repository;
            internal StandardPackage Package = null!;
        }
        private Entry[] Inventory()
        {
            var entries = new List<Entry>();
            Add(RepositoryRoot, true, 0); Add(UserRoot, false, 0);
            return entries.OrderBy(e => e.Package.Manifest.Id, StringComparer.Ordinal).ThenBy(e => e.Package.Manifest.Version, StringComparer.Ordinal).ToArray();
            void Add(string root, bool repository, int depth)
            {
                Safe(root);
                if (!Directory.Exists(root)) return;
                if (File.Exists(Path.Combine(root, "package.json")))
                {
                    if (entries.Count >= MaximumPackages) throw new ArgumentException("Too many installed packages.");
                    var package = StandardPackageLoader.LoadDirectory(root);
                    if (entries.Any(e => e.Package.Manifest.Id == package.Manifest.Id && e.Package.Manifest.Version == package.Manifest.Version))
                        throw new ArgumentException("Duplicate installed package identity.");
                    entries.Add(new Entry { Path = root, Repository = repository, Package = package }); return;
                }
                if (depth >= 2) return;
                var directories = Directory.EnumerateDirectories(root).Take(MaximumPackages + 1).ToArray();
                if (directories.Length > MaximumPackages) throw new ArgumentException("Too many package directories.");
                foreach (var directory in directories.OrderBy(p => p, StringComparer.Ordinal))
                {
                    if (!Path.GetFileName(directory).StartsWith(".pending-", StringComparison.Ordinal)) Add(directory, repository, depth + 1);
                }
            }
        }
        private Entry Find(string id, string version)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version)) throw new ArgumentException("Exact packageId and version are required.", "packageId");
            return Inventory().SingleOrDefault(e => e.Package.Manifest.Id == id && e.Package.Manifest.Version == version)
                ?? throw new ArgumentException("Package id/version is not installed.", "packageId");
        }
        private static JsonObject Summary(StandardPackage package) => new JsonObject { ["id"] = package.Manifest.Id, ["version"] = package.Manifest.Version,
            ["contentHash"] = package.ContentHash, ["manifest"] = JsonNode.Parse(package.Documents["package.json"].GetRawText()) };
        private static JsonNode? Part(StandardPackage package, string part)
            => package.Documents["package.json"].GetProperty("parts").TryGetProperty(part, out var path) ? JsonNode.Parse(package.Documents[path.GetString()!].GetRawText()) : null;
        private static JsonArray Coverage(StandardPackage package)
        {
            var library = Part(package, "library") as JsonObject;
            var result = new JsonArray();
            foreach (var release in package.Manifest.Targets.Releases)
            {
                var missing = new JsonArray();
                foreach (var type in library?["types"]?.AsArray() ?? new JsonArray())
                    if (!type!["implementations"]!.AsArray().Any(i => {
                        var releases = CanonicalJson.Parse(i!["releases"]!.ToJsonString());
                        return releases.ValueKind == JsonValueKind.Array ? releases.EnumerateArray().Any(r => r.GetString() == release)
                            : GenerationModelValidation.ReleaseMatches(releases.GetString()!, release);
                    })) missing.Add((string)type["id"]!);
                result.Add(new JsonObject { ["release"] = release, ["complete"] = missing.Count == 0, ["missingImplementations"] = missing, ["nativeAcceptance"] = "NOT RUN" });
            }
            return result;
        }
        private static StandardPackage LoadSource(string path)
        {
            path = Absolute(path, "sourcePath"); Safe(path);
            return Directory.Exists(path) ? StandardPackageLoader.LoadDirectory(path) : Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)
                ? StandardPackageLoader.LoadZip(path) : throw new ArgumentException("Use a package directory or .zip file.", "sourcePath");
        }
        private static StandardPackage Fork(StandardPackage source, string id, string version)
        {
            var files = source.FileNames.ToDictionary(p => p, source.ReadFile, StringComparer.Ordinal);
            foreach (var path in source.Documents.Keys)
            {
                var document = JsonNode.Parse(source.Documents[path].GetRawText())!;
                Rewrite(document);
                if (path == "package.json") { document["id"] = id; document["version"] = version; }
                files[path] = CanonicalJson.Encode(CanonicalJson.Parse(document.ToJsonString()));
            }
            var manifest = JsonNode.Parse(Encoding.UTF8.GetString(files["package.json"]))!;
            manifest["files"] = new JsonArray(files.Where(f => f.Key != "package.json").OrderBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => (JsonNode)new JsonObject { ["path"] = f.Key, ["sha256"] = CanonicalJson.HashBytes(f.Value) }).ToArray());
            files["package.json"] = CanonicalJson.Encode(CanonicalJson.Parse(manifest.ToJsonString()));
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                foreach (var file in files) { using var output = zip.CreateEntry(file.Key).Open(); output.Write(file.Value, 0, file.Value.Length); }
            stream.Position = 0;
            return StandardPackageLoader.LoadZip(stream);
            void Rewrite(JsonNode node)
            {
                if (node is JsonObject obj)
                {
                    foreach (var property in obj.ToArray())
                    {
                        if (property.Value is JsonValue value && value.TryGetValue<string>(out var text))
                        {
                            if (text.StartsWith("lib:" + source.Manifest.Id + "/", StringComparison.Ordinal)) obj[property.Key] = "lib:" + id + text.Substring(4 + source.Manifest.Id.Length);
                            else if (property.Key == "package" && text == source.Manifest.Id) { obj[property.Key] = id; if (obj.ContainsKey("version")) obj["version"] = version; }
                        }
                        else if (property.Value != null) Rewrite(property.Value);
                    }
                }
                else if (node is JsonArray array) foreach (var child in array) if (child != null) Rewrite(child);
            }
        }
        private static void Publish(StandardPackage package, string target)
            => PublishFiles(package.FileNames.ToDictionary(p => p, package.ReadFile, StringComparer.Ordinal), target);
        internal static void PublishFiles(IDictionary<string, byte[]> files, string target)
        {
            Safe(target);
            if (File.Exists(target) || Directory.Exists(target)) throw new ArgumentException("Output already exists.", "outputPath");
            string parent = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(parent);
            string staging = Path.Combine(parent, ".pending-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            // A failed publication is retained for inspection; inventory ignores pending directories.
            foreach (var file in files)
            {
                StandardPackageLoader.ValidatePath(file.Key);
                string path = Path.Combine(staging, file.Key.Replace('/', Path.DirectorySeparatorChar)); Safe(path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                output.Write(file.Value, 0, file.Value.Length); output.Flush(true);
            }
            Safe(target); Directory.Move(staging, target);
        }
        private void Remove(Entry entry)
        {
            if (!Within(entry.Path, UserRoot)) throw new ArgumentException("Removal must remain under the user package store.");
            var current = StandardPackageLoader.LoadDirectory(entry.Path);
            if (current.ContentHash != entry.Package.ContentHash) throw new ArgumentException("Package changed; review a fresh preview.", "expectedPlanHash");
            foreach (var name in current.FileNames.OrderBy(p => p, StringComparer.Ordinal))
            { var path = Path.Combine(entry.Path, name.Replace('/', Path.DirectorySeparatorChar)); Safe(path); File.Delete(path); }
            Empty(entry.Path);
            void Empty(string directory)
            { Safe(directory); foreach (var child in Directory.EnumerateDirectories(directory)) Empty(child); Directory.Delete(directory, false); }
        }
        internal static void RequirePlan(bool dryRun, string expected, string actual)
        {
            if ((!dryRun || expected.Length != 0) && expected != actual) throw new ArgumentException("Use expectedPlanHash from a fresh preview; inputs or target changed.", "expectedPlanHash");
        }
        internal static string Absolute(string path, string parameter)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || path.StartsWith("\\\\", StringComparison.Ordinal)
                || Path.DirectorySeparatorChar == '\\' && (path.Length < 3 || path[1] != ':' || path[2] is not ('\\' or '/')))
                throw new ArgumentException("An absolute local path is required.", parameter);
            return Path.GetFullPath(path);
        }
        internal static void Safe(string path)
        {
            for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Links and reparse points are prohibited.", "path");
        }
        private static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        private string NewOutput(string path, string extension)
        {
            path = NewDirectory(path);
            if (!Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must use " + extension + ".", "outputPath");
            return path;
        }
        private string NewDirectory(string path)
        {
            path = Absolute(path, "outputPath"); Safe(path);
            if (Within(path, RepositoryRoot) || Within(path, UserRoot) || ApprovalSettings.IsAdministrativeTarget(path))
                throw new ArgumentException("Output cannot modify package stores or approval settings.", "outputPath");
            if (File.Exists(path) || Directory.Exists(path) || !Directory.Exists(Path.GetDirectoryName(path)))
                throw new ArgumentException("Output must be new under an existing parent.", "outputPath");
            return path;
        }
        private static string ReadJson(string path)
        {
            path = Absolute(path, "inputPath"); Safe(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 4 * 1024 * 1024) throw new ArgumentException("Machine JSON exceeds 4 MiB.", "inputPath");
            using var memory = new MemoryStream(); stream.CopyTo(memory);
            return Encoding.UTF8.GetString(CanonicalJson.Encode(CanonicalJson.Parse(memory.ToArray())));
        }
    }
}
