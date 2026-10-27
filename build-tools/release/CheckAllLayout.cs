using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TiaMcp.ReleaseTool;

internal static class CheckAllLayout
{
    internal static string[] SourceInventory(string root)
    {
        var tracked = ProcessRunner.Run("git", ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], root);
        ProcessRunner.RequireSuccess(tracked, "layout input inventory");
        return tracked.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    internal static string[] ResultSourceInventory(string root, IEnumerable<string> layout)
    {
        var files = layout.ToHashSet(StringComparer.Ordinal);
        // The layout supplies the roots. Also include ignored/new data below
        // those roots; Git's ignore rules do not declare check dependencies.
        void Walk(string directory)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked source input: " + directory);
            foreach (var file in Directory.EnumerateFiles(directory)) files.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                // These are build outputs, enumerated from runtime receipts.
                if (name is "bin" or "obj" or ".git" || name.StartsWith("bin-", StringComparison.Ordinal) || name.StartsWith("obj-", StringComparison.Ordinal)) continue;
                Walk(child);
            }
        }
        foreach (var top in files.Select(file => file.Replace('\\', '/').Split('/')[0]).Distinct(StringComparer.Ordinal).ToArray())
            if (top != "bin-build" && Directory.Exists(Path.Combine(root, top))) Walk(Path.Combine(root, top));
        return files.Order(StringComparer.Ordinal).ToArray();
    }

    internal static string InputKey(IEnumerable<KeyValuePair<string, string>> inputs, IEnumerable<string> properties) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            inputs = inputs.OrderBy(pair => pair.Key, StringComparer.Ordinal), properties = properties.ToArray()
        })))).ToLowerInvariant();

    private static string InputHash(string root, string file)
    {
        if (file is "manifest/version-tools.json" or "manifest/tool-usage-coverage.json")
        {
            var value = JsonNode.Parse(File.ReadAllText(Path.Combine(root, file)))!.AsObject(); value.Remove("generatedAtUtc");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToJsonString())));
        }
        return ReleaseRecords.HashFile(Path.Combine(root, file));
    }

    internal static string[] Missing(string defaultOutput, string checkOutput, IEnumerable<string> files) => files
        .Where(file => !File.Exists(Path.Combine(defaultOutput, file)) || !File.Exists(Path.Combine(checkOutput, file)))
        .Order(StringComparer.Ordinal).ToArray();

    internal static string[] ReadInventory(string path)
    {
        var files = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [];
        if (files.Length == 0 || files.Any(file => string.IsNullOrWhiteSpace(file) || Path.IsPathRooted(file)
            || file.Replace('\\', '/').Split('/').Contains("..", StringComparer.Ordinal))
            || files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length)
            throw new ReleaseException("Invalid ordinary output inventory: " + path);
        return files;
    }

    internal static void CopyInventoryCache(string root, string source, string destination)
    {
        // Reviewable proposal only; Clean does not enable this carry-over.
        if (!Directory.Exists(source)) return;
        var entries = Directory.EnumerateFiles(source, "*.json").Where(path => Path.GetFileNameWithoutExtension(path) is { Length: 64 } key
            && key.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f')).OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        // Inventory metadata only: bounded to a few revisions of all variants.
        // A miss always rebuilds the ordinary baseline; no test result is cached.
        foreach (var path in entries.Skip(256))
        {
            ReleaseCommands.RequireCheckAllOwnedPath(root, path); File.Delete(path);
        }
        foreach (var path in entries.Take(256))
        {
            var target = Path.Combine(destination, Path.GetFileName(path));
            ReleaseCommands.RequireCheckAllOwnedPath(root, path); ReleaseCommands.RequireCheckAllOwnedPath(root, target);
            _ = ReadInventory(path);
            Directory.CreateDirectory(destination); File.Copy(path, target, true);
        }
    }

    internal static string CopyBaselineSources(string root, IEnumerable<string> inventory)
    {
        // A real ordinary-layout build, with identical source bytes, but no
        // writers in the runtime directories that concurrent suites discover.
        var baseline = Path.Combine(root, "bin-build/p", Guid.NewGuid().ToString("N")[..8]);
        foreach (var relative in inventory)
        {
            var source = Path.GetFullPath(Path.Combine(root, relative));
            var target = Path.GetFullPath(Path.Combine(baseline, relative));
            ReleaseCommands.RequireCheckAllOwnedPath(root, source);
            ReleaseCommands.RequireCheckAllOwnedPath(baseline, target);
            if (!File.Exists(source)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
        return baseline;
    }

    internal static CommandResult Verify(string root, IEnumerable<CheckBuild> builds, string dotnet, string nuget, string api, string directory, string manifest, int parallelism = 1, bool independent = false,
        Func<CommandResult>? awaitRuntime = null)
    {
        Directory.CreateDirectory(directory);
        var clock = Stopwatch.StartNew();
        double? baselineSeconds = null, runtimeWaitSeconds = null, comparisonStarted = null;
        bool runtimeManifestRead = false;
        var inputs = SourceInventory(root).Where(file => File.Exists(Path.Combine(root, file)))
            .Select(file => new KeyValuePair<string, string>(file, InputHash(root, file))).ToArray();
        inputs = inputs.Concat(Directory.EnumerateFiles(api, "*.dll", SearchOption.AllDirectories)
            .Select(file => new KeyValuePair<string, string>("sdk/" + Path.GetRelativePath(api, file), ReleaseRecords.HashFile(file)))).ToArray();
        var sdk = ProcessRunner.Run(dotnet, ["--version"], root); ProcessRunner.RequireSuccess(sdk, "Output inventory SDK version");
        var sdkIdentity = sdk.StandardOutput.Trim() + "/" + Environment.Version + "/" + System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        var receipts = new List<object>();
        var failures = new List<string>();
        int rebuilt = 0;
        try
        {
            var units = builds.Where(unit => unit.Project.StartsWith("tests/", StringComparison.Ordinal)).GroupBy(unit => unit.Project + "|" + unit.Variant).Select(group => group.First()).ToArray();
            var keys = units.ToDictionary(unit => unit, unit => InputKey(inputs, [unit.Project, unit.Variant, .. unit.Properties, api, nuget, ReleaseRecords.HashFile(nuget), sdkIdentity]));
            var pending = units.Where(unit => !File.Exists(Path.Combine(root, "bin-build/check-layout", keys[unit] + ".json"))).ToArray();
            if (pending.Length != 0)
            {
                rebuilt = pending.Length;
                var baselineRoot = independent ? CopyBaselineSources(root, inputs.Where(pair => !pair.Key.StartsWith("sdk/", StringComparison.Ordinal)).Select(pair => pair.Key)) : root;
                var result = BuildInventories(baselineRoot, pending, dotnet, nuget, api, directory, parallelism);
                if (result.ExitCode != 0) return result;
            }
            baselineSeconds = clock.Elapsed.TotalSeconds;
            if (awaitRuntime != null)
            {
                var ready = awaitRuntime();
                runtimeWaitSeconds = clock.Elapsed.TotalSeconds - baselineSeconds;
                if (ready.ExitCode != 0) return new(ready.ExitCode, "", "Runtime build failed before output parity comparison.\n" + ready.StandardError);
            }
            comparisonStarted = clock.Elapsed.TotalSeconds;
            var runtime = File.ReadLines(manifest).Select(line => line.Split('\t')).ToArray();
            runtimeManifestRead = true;
            foreach (var unit in units)
            {
                var row = runtime.Single(row => string.Equals(Path.GetFullPath(row[0]), Path.GetFullPath(Path.Combine(root, unit.Project)), StringComparison.OrdinalIgnoreCase) && row[1] == unit.Variant);
                // The baseline is produced by an ordinary registered-layout build.
                // Cache only its file inventory, never a test result. Every invocation
                // still checks every expected file in the actual isolated output.
                var key = keys[unit];
                var cache = Path.Combine(root, "bin-build/check-layout", key + ".json");
                var baseline = row[3];
                var ordinaryOutput = row[3];
                string[] files;
                if (File.Exists(cache)) files = ReadInventory(cache);
                else
                {
                    var lines = File.ReadAllLines(Receipt(directory, unit));
                    var defaultOutput = Path.GetFullPath(lines[0]);
                    ordinaryOutput = defaultOutput;
                    baseline = lines[1];
                    files = lines.Skip(2).Select(line => line.Split('\t')).Select(fields => fields[0] switch
                    {
                        "file" => Path.GetFullPath(fields[1]).StartsWith(defaultOutput, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(defaultOutput, fields[1]) : null,
                        "bridge" => Path.Combine("bridge", Path.GetFileName(fields[1])),
                        "fixture" => Path.Combine("fixtures", Path.GetFileName(fields[1])),
                        _ => throw new ReleaseException("Invalid ordinary output receipt: " + unit.Project)
                    }).Where(file => file != null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
                    if (files.Length == 0) return new(1, "", "Ordinary build produced no output inventory: " + unit.Project);
                    Directory.CreateDirectory(Path.GetDirectoryName(cache)!); File.WriteAllText(cache, JsonSerializer.Serialize(files) + "\n");
                }
                var missing = Missing(baseline, row[2], files);
                receipts.Add(new { unit.Project, unit.Variant, defaultOutput = ordinaryOutput, checkOutput = row[2], files, missing, inputKey = key });
                if (missing.Length != 0)
                {
                    failures.Add("Missing registered output files: " + unit.Project + "/" + unit.Variant + ": " + string.Join(", ", missing));
                }
            }
            File.WriteAllText(Path.Combine(directory, "comparison.json"), JsonSerializer.Serialize(receipts, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            File.WriteAllText(Path.Combine(directory, "inventory-cache.json"), JsonSerializer.Serialize(new {
                sdkIdentity, variants = receipts.Count, rebuilt, cached = receipts.Count - rebuilt, runtimeProjectionRequired = !independent
            }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            return new(failures.Count == 0 ? 0 : 1, $"Suite output parity: {receipts.Count} variants, {failures.Count} with missing files; {rebuilt} inventories rebuilt.\n", string.Join("\n", failures));
        }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "phases.json"), JsonSerializer.Serialize(new {
                baselineSeconds = baselineSeconds ?? clock.Elapsed.TotalSeconds, runtimeWaitSeconds = runtimeWaitSeconds ?? 0,
                comparisonSeconds = comparisonStarted == null ? 0 : clock.Elapsed.TotalSeconds - comparisonStarted,
                runtimeManifestRead, independent
            }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            // Ordinary builds may share their default outputs. Restore each isolated
            // production/fixture payload before any check starts.
            if (!independent) ReleaseCommands.PrepareCheckAllRuntime(root, builds, manifest);
        }
    }
    private static string Receipt(string directory, CheckBuild unit) => Path.Combine(directory, InputKey([], [unit.Project, unit.Variant]) + ".tsv");

    internal static CommandResult BuildInventories(string root, CheckBuild[] units, string dotnet, string nuget, string api, string directory, int parallelism)
    {
        Directory.CreateDirectory(directory);
        var targets = Path.Combine(directory, "inventory.targets");
        var inventory = new XElement("Target", new XAttribute("Name", "CheckOrdinaryInventory"), new XAttribute("DependsOnTargets", "Build"));
        // Even roots with equal global properties can reach a reference with
        // different properties (GlobalPropertiesToRemove/AdditionalProperties).
        // They then have different MSBuild node identities but the same ordinary
        // obj. The Foundation root itself reaches Logic both directly and via
        // EngineWorker.FaultFixture with TiaMcpEngineWorkerFaultFixture=true.
        // Order roots AND their reference writers in this single traversal.
        var graph = new XElement("Project");
        var build = new XElement("Target", new XAttribute("Name", "Build"));
        int index = 0;
        foreach (var group in units.GroupBy(unit => string.Join(";", unit.Properties.Order(StringComparer.Ordinal))))
        {
            var item = "Layout" + index++;
            foreach (var unit in group)
            {
                var condition = "'$(MSBuildProjectFullPath)' == '" + Path.GetFullPath(Path.Combine(root, unit.Project)) + "' And '$(_CheckLayoutGroup)' == '" + item + "'";
                var baseline = Receipt(directory, unit) + ".output";
                // Capture existence and preserve the ordinary output before another
                // property group changes this shared directory or its file list.
                inventory.Add(new XElement("ItemGroup", new XAttribute("Condition", condition),
                    new XElement("_CheckOrdinaryWrites", new XAttribute("Include", "@(FileWrites)"), new XAttribute("Condition", "Exists('%(FileWrites.FullPath)') And $([System.String]::Copy('%(FileWrites.FullPath)').StartsWith('$(TargetDir)', System.StringComparison.OrdinalIgnoreCase))")),
                    new XElement("_CheckOrdinaryOutput", new XAttribute("Include", "@(_CheckOrdinaryWrites->'%(FullPath)')"),
                        new XElement("_CheckRelative", "$([MSBuild]::MakeRelative('$(TargetDir)', '%(FullPath)'))")),
                    new XElement("_CheckOrdinaryOutput", new XAttribute("Include", "@(BridgePayload->'$(TargetDir)bridge/%(Filename)%(Extension)')"),
                        new XElement("_CheckRelative", "bridge/%(Filename)%(Extension)")),
                    new XElement("_CheckOrdinaryOutput", new XAttribute("Include", "@(FixtureFiles->'$(TargetDir)fixtures/%(Filename)%(Extension)')"),
                        new XElement("_CheckRelative", "fixtures/%(Filename)%(Extension)"))));
                inventory.Add(new XElement("RemoveDuplicates", new XAttribute("Condition", condition), new XAttribute("Inputs", "@(_CheckOrdinaryOutput)"),
                    new XElement("Output", new XAttribute("TaskParameter", "Filtered"), new XAttribute("ItemName", "_CheckUniqueOutput"))));
                inventory.Add(new XElement("Error", new XAttribute("Condition", condition + " And ('%(_CheckUniqueOutput._CheckRelative)' == '' Or $([System.IO.Path]::IsPathRooted('%(_CheckUniqueOutput._CheckRelative)')) Or $([System.String]::Copy('%(_CheckUniqueOutput._CheckRelative)').StartsWith('..', System.StringComparison.Ordinal)))"),
                    new XAttribute("Text", "Unsafe ordinary output baseline path: %(_CheckUniqueOutput._CheckRelative)")));
                inventory.Add(new XElement("Copy", new XAttribute("Condition", condition), new XAttribute("SourceFiles", "@(_CheckUniqueOutput)"),
                    new XAttribute("DestinationFiles", "@(_CheckUniqueOutput->'" + baseline + "/%(_CheckRelative)')"), new XAttribute("UseHardlinksIfPossible", "true")));
                inventory.Add(new XElement("WriteLinesToFile", new XAttribute("File", Receipt(directory, unit)), new XAttribute("Overwrite", "true"),
                    new XAttribute("Condition", condition),
                    new XAttribute("Lines", "$(TargetDir);" + baseline + ";@(_CheckOrdinaryWrites->'file\t%(FullPath)');@(BridgePayload->'bridge\t%(Identity)');@(FixtureFiles->'fixture\t%(Identity)')")));
            }
            int rootIndex = 0;
            var roots = new List<string>();
            foreach (var unit in group)
            {
                var rootItem = item + "Root" + rootIndex++;
                roots.Add(rootItem);
                graph.Add(new XElement("ItemGroup", new XElement(rootItem, new XAttribute("Include", Path.Combine(root, unit.Project)),
                    new XElement("AdditionalProperties", string.Join(";", unit.Properties.Select(property => property[3..])) +
                        ";CustomAfterMicrosoftCommonTargets=" + targets + ";_CheckLayoutGroup=" + item))));
            }
            // A root can also be a reference of an earlier root. Clean the entire
            // group before building any reference: a later Clean must not delete
            // outputs whose Build target is already cached in this traversal.
            foreach (var rootItem in roots)
                build.Add(new XElement("MSBuild", new XAttribute("Projects", "@(" + rootItem + ")"), new XAttribute("Targets", "Clean"),
                    new XAttribute("Properties", "BuildProjectReferences=false;BuildInParallel=false;_CheckLayoutPhase=Clean"), new XAttribute("BuildInParallel", "true")));
            // Restore and Build still use distinct project evaluations; each
            // property group consumes its assets before the next group restores.
            foreach (var rootItem in roots)
                build.Add(new XElement("MSBuild", new XAttribute("Projects", "@(" + rootItem + ")"), new XAttribute("Targets", "Restore"),
                    new XAttribute("Properties", "ExcludeRestorePackageImports=true;RestoreForce=true;BuildInParallel=false;_CheckLayoutPhase=Restore"), new XAttribute("BuildInParallel", "true")));
            foreach (var rootItem in roots)
                build.Add(new XElement("MSBuild", new XAttribute("Projects", "@(" + rootItem + ")"), new XAttribute("Targets", "CheckOrdinaryInventory"),
                    new XAttribute("Properties", "BuildInParallel=false;_CheckLayoutPhase=Build"), new XAttribute("BuildInParallel", "true")));
        }
        // SDK tasks cache parsed lock files by path for the whole MSBuild process.
        // Restoring a later variant to the same project.assets.json does not
        // invalidate that cache. Keep ordinary obj/bin and freeze only the assets
        // input under a distinct path for each serialized property group.
        var assets = new XElement("PropertyGroup", new XAttribute("Condition", "'$(_CheckLayoutPhase)' == 'Build' And '$(ProjectAssetsFile)' != ''"),
            new XElement("_CheckOriginalAssets", "$(ProjectAssetsFile)"),
            new XElement("ProjectAssetsFile", "$(IntermediateOutputPath)check-layout-$(_CheckLayoutGroup).assets.json"));
        var copyAssets = new XElement("Target", new XAttribute("Name", "CheckOrdinaryAssets"), new XAttribute("BeforeTargets", "ResolvePackageAssets"),
            new XAttribute("Condition", "'$(_CheckOriginalAssets)' != ''"),
            new XElement("Copy", new XAttribute("SourceFiles", "$(_CheckOriginalAssets)"), new XAttribute("DestinationFiles", "$(ProjectAssetsFile)")));
        new XDocument(new XElement("Project", assets, copyAssets, inventory)).Save(targets);
        graph.Add(build);
        var path = Path.Combine(directory, "ordinary.proj"); new XDocument(graph).Save(path);
        return ProcessRunner.Run(dotnet, ["msbuild", path, "-t:Build", "-m:" + parallelism, "-nodeReuse:false", "-v:minimal",
            "-p:Configuration=Release", "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-p:TiaSharedAdapterPaths=false", "-p:RestoreConfigFile=" + nuget,
            "-p:TiaPublicApiRoot=" + api, "-p:PublicApiRoot=" + api], root,
            new Dictionary<string, string?> { ["TIA_MCP_TEST_PUBLIC_API_ROOT"] = null,
                ["TIA_MCP_ANALYZER_REPORT_DIRECTORY"] = Path.Combine(directory, "analyzer-warnings") }, logPath: Path.Combine(directory, "ordinary-build.log"));
    }

}
