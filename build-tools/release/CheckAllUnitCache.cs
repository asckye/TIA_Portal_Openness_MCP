using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace TiaMcp.ReleaseTool;

internal sealed record CheckUnitEvidence(string Unit, string InputKey, string Provenance, string RunId, string[] ColdRunIds,
    BuildCacheOutput[] Outputs, string? Exclusion = null, string[]? BuildArguments = null);

// Cache the executable graph's units, never test results. A hit replaces only the
// Build target; GetTargetPath and runtime receipt discovery still run in MSBuild.
internal sealed class CheckAllUnitCache(string root, string directory, string cacheRoot, string dotnet,
    IDictionary<string, string?> environment, bool noReads, string runId, int parallelism = 1, BuildUnitDeadline? budget = null)
{
    private sealed record Unit(string Project, string Id, string[] Arguments, string Key, string[] Outputs,
        BuildOutputCache Cache, BuildCacheFiles? Restored);
    private Unit?[] units = [];
    private readonly BuildUnitDeadline deadline = budget ?? new();
    private BuildKeyMemo? prepared;
    private string? graphHash;
    private double prepareSeconds;
    internal string Hits => Path.Combine(directory, "cache-hits");

    internal void Prepare(string graph)
    {
        var timer = Stopwatch.StartNew();
        Directory.CreateDirectory(Hits);
        var memo = prepared = new BuildKeyMemo(deadline.Check);
        var nodes = XDocument.Load(graph).Descendants("CheckGraphProject").ToArray();
        try { if (!deadline.Expired) graphHash = ReleaseRecords.HashFile(graph, deadline.Check); }
        catch (OperationCanceledException) when (deadline.Expired) { }
        units = new Unit?[nodes.Length];
        var timeout = deadline.ForUnits(nodes.Length, parallelism, "Prepare", index =>
        {
            var node = nodes[index];
            var project = node.Attribute("Include")!.Value;
            // Keep MSBuild's percent escapes: literal semicolons in a compiler
            // property must not become additional CLI property assignments.
            var properties = node.Element("AdditionalProperties")!.Value.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var id = properties.Single(property => property.StartsWith("CheckAllNodeId=", StringComparison.OrdinalIgnoreCase)).Split('=', 2)[1];
            var arguments = new[] { "build", project }.Concat(properties.Select(property => "-p:" + property)).ToArray();
            var name = Path.GetRelativePath(root, project).Replace('\\', '/') + " " + id;
            var cache = new BuildOutputCache(cacheRoot, name, checkDeadline: deadline.Check);
            try
            {
                var key = ReleaseCommands.BuildKey(dotnet, project, arguments, root, environment, memo);
                // Analyzer receipts are cached as portable text, with the same source
                // hashes. They are rebound to this checkout before the unchanged gate.
                var diagnostics = Path.Combine("bin-build/unit-diagnostics", id, key.Key).Replace('\\', '/');
                var outputs = key.Outputs.Append(diagnostics).ToArray();
                BuildCacheFiles? restored = null;
                if (!noReads && cache.RestoreVerified(key.Key, root, outputs))
                {
                    // Retention/another cache owner may evict between operations.
                    // Without provenance, leave no hit marker and compile cold.
                    var candidate = cache.Record(key.Key);
                    if (candidate?.ColdRunIds is { Length: > 0 } && !string.IsNullOrWhiteSpace(candidate.RunId))
                    {
                        restored = candidate;
                        RestoreDiagnostics(Path.Combine(root, diagnostics));
                        File.WriteAllText(Path.Combine(Hits, id), key.Key);
                    }
                }
                units[index] = new(project, id, arguments, key.Key, outputs, cache, restored);
            }
            catch (Exception ex) when (!deadline.Expired && ex is (IOException or ReleaseException or JsonException or InvalidOperationException))
            {
                // An uncertain closure can never cause a hit. Keep the reason in
                // the report; release adoption rejects missing unit fingerprints.
                Console.WriteLine("CACHE excluded " + name + ": " + ex.Message);
                units[index] = new(project, id, arguments, "", [], cache, null);
            }
        });
        prepareSeconds = timer.Elapsed.TotalSeconds;
        Console.WriteLine($"CACHE Prepare: {nodes.Length} units, degree {Math.Max(1, parallelism)}, {prepareSeconds:F3}s");
        if (timeout != null) throw new ReleaseException(timeout);
    }

    internal void Complete(string report, bool succeeded, Action<Action>? prepareRuntime = null)
    {
        double runtimeSeconds = 0;
        if (succeeded && prepareRuntime != null)
        {
            var runtimeTimer = Stopwatch.StartNew();
            try { deadline.Check(); prepareRuntime(deadline.Check); deadline.Check(); }
            catch (Exception) when (deadline.Expired) { }
            runtimeSeconds = runtimeTimer.Elapsed.TotalSeconds;
        }
        var timer = Stopwatch.StartNew();
        var rows = new CheckUnitEvidence?[units.Length];
        var errors = new string?[units.Length];
        // Probes/imports and every repository byte are reevaluated after Build.
        var memo = prepared?.ForCompletion() ?? new BuildKeyMemo(deadline.Check);
        var timeout = deadline.ForUnits(units.Length, parallelism, "Complete", index =>
        {
            var unit = units[index]!;
            var name = Path.GetRelativePath(root, unit.Project).Replace('\\', '/') + " " + unit.Id;
            if (unit.Key.Length == 0) { rows[index] = new(name, "", "cold", runId, [], [], "input closure unavailable"); return; }
            if (!succeeded) return;
            try
            {
                var diagnostics = Path.Combine(root, unit.Outputs[^1]);
                if (unit.Restored == null) CaptureDiagnostics(unit.Project, unit.Id, diagnostics);
                var outputs = unit.Outputs.Where(output => Directory.Exists(Path.Combine(root, output)))
                    .Order(StringComparer.Ordinal).Select(output => new BuildCacheOutput(output, BuildReproducibility.Inventory(Path.Combine(root, output), deadline.Check))).ToArray();
                var exclusion = outputs.Select(output => BuildOutputCache.EmbeddedPath(Path.Combine(root, output.Path), root, deadline.Check)).FirstOrDefault(path => path != null);
                var after = ReleaseCommands.BuildKey(dotnet, unit.Project, unit.Arguments, root, environment, memo);
                if (after.Key != unit.Key)
                    throw new ReleaseException("Build unit inputs changed during build: " + name);
                if (unit.Restored != null)
                {
                    if (!BuildOutputCache.SameOutputs(unit.Restored.Outputs, outputs))
                        throw new ReleaseException("Restored unit changed during build: " + name);
                }
                else if (exclusion == null && outputs.Length == unit.Outputs.Length && outputs.All(output => output.Files.Length > 0))
                {
                    // Cold verification needs hashes/provenance, not a payload that
                    // this run has explicitly forbidden itself from restoring.
                    unit.Cache.RecordBuild(unit.Key, root, unit.Outputs, runId, cold: true, copyOutputs: !noReads);
                }
                else exclusion ??= "missing outputs or analyzer receipts";
                var record = unit.Cache.Record(unit.Key);
                rows[index] = new(name, unit.Key, unit.Restored == null ? "cold" : "restored",
                    unit.Restored?.RunId ?? runId, unit.Restored?.ColdRunIds ?? record?.ColdRunIds ?? [], outputs, exclusion, unit.Arguments);
            }
            catch (Exception ex) when (!deadline.Expired && ex is (BuildNonDeterminismException or ReleaseException)) { errors[index] = ex.Message; }
        });
        var evidence = rows.OfType<CheckUnitEvidence>().ToArray();
        var failures = errors.OfType<string>().Concat(timeout == null ? [] : new[] { timeout }).ToArray();
        var completeSeconds = timer.Elapsed.TotalSeconds;
        Console.WriteLine($"CACHE Complete: {evidence.Length}/{units.Length} units, degree {Math.Max(1, parallelism)}, {completeSeconds:F3}s");
        File.WriteAllText(report, JsonSerializer.Serialize(new { schema = 1, mode = "verified-build-units", buildRoot = root, runId, noReads,
            keyParallelism = Math.Max(1, parallelism), prepareSeconds, completeSeconds, runtimeSeconds, copyOutputs = !noReads,
            graphPath = Path.Combine(directory, "build-nodes.props"), graphSha256 = graphHash,
            outputsRestored = evidence.Count(unit => unit.Provenance == "restored"), units = evidence, failures },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }) + "\n");
        if (failures.Length != 0) throw new ReleaseException(string.Join("\n", failures));
    }

    private void CaptureDiagnostics(string project, string id, string destination)
    {
        deadline.Check();
        Directory.CreateDirectory(destination);
        // Projects that explicitly disable analyzers still have an inventoried receipt.
        File.WriteAllText(Path.Combine(destination, "unit.txt"), Path.GetRelativePath(root, project).Replace('\\', '/'));
        environment.TryGetValue("TIA_MCP_ANALYZER_REPORT_DIRECTORY", out var observed);
        if (observed == null || !Directory.Exists(observed)) return;
        foreach (var receipt in Directory.EnumerateFiles(observed, "*.inputs"))
        {
            deadline.Check();
            var header = File.ReadLines(receipt).FirstOrDefault()?.Split('|');
            if (header is not { Length: 4 } || !string.Equals(header[1], project, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.ReadAllText(receipt).Replace('\\', '/').Contains("/" + id + "/", StringComparison.OrdinalIgnoreCase)) continue;
            var report = Path.ChangeExtension(receipt, ".sarif");
            foreach (var file in new[] { receipt, report })
            {
                deadline.Check();
                File.WriteAllText(Path.Combine(destination, id + "-" + header[2] + Path.GetExtension(file)), ReleaseCommands.NormalizeCacheText(File.ReadAllText(file), root));
            }
        }
    }

    private void RestoreDiagnostics(string source)
    {
        environment.TryGetValue("TIA_MCP_ANALYZER_REPORT_DIRECTORY", out var observed);
        if (observed == null) return;
        Directory.CreateDirectory(observed);
        foreach (var file in Directory.EnumerateFiles(source).Where(file => Path.GetExtension(file) is ".inputs" or ".sarif"))
        {
            deadline.Check();
            File.WriteAllText(Path.Combine(observed, Path.GetFileName(file)), File.ReadAllText(file).Replace("<repo>", root.Replace('\\', '/'), StringComparison.Ordinal));
        }
    }
}

internal sealed class BuildUnitDeadline((string Name, DateTime Deadline)? budget = null, Func<DateTime>? clock = null)
{
    private readonly (string Name, DateTime Deadline)? limit = budget ?? CheckScheduler.Deadline;
    internal bool Expired => limit is { } value && (clock?.Invoke() ?? DateTime.UtcNow) >= value.Deadline;
    internal void Check()
    {
        if (Expired) throw new OperationCanceledException("Build unit deadline reached");
    }

    internal string? ForUnits(int count, int parallelism, string phase, Action<int> run)
    {
        int completed = 0;
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism) }, (index, state) =>
        {
            try { Check(); run(index); Check(); Interlocked.Increment(ref completed); }
            catch (Exception) when (Expired) { state.Stop(); }
        });
        return Expired ? $"Check job timed out: {limit!.Value.Name} during build unit {phase}; {count - completed} of {count} units not completed." : null;
    }
}
