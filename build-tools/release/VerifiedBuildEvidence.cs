using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.ReleaseTool;

internal static class VerifiedBuildEvidence
{
    internal static void ValidateParity(string root, JsonObject? cache, List<string> problems)
    {
        if (cache == null) return;
        foreach (var unit in (cache["units"] as JsonArray ?? []).OfType<JsonObject>().Where(unit => (string?)unit["provenance"] == "restored"))
        {
            if (unit["coldRunIds"] is not JsonArray { Count: > 0 }) problems.Add("unverified restored parity manifest");
            var manifest = (string?)unit["manifest"];
            if (string.IsNullOrWhiteSpace(manifest) || !File.Exists(manifest)) { problems.Add("restored parity manifest missing"); continue; }
            ReleaseCommands.RequireCheckAllOwnedPath(root, manifest);
            if (ReleaseRecords.HashFile(manifest) != (string?)unit["manifestSha256"]) problems.Add("restored parity manifest changed after the run");
        }
    }

    internal static bool Validate(string root, JsonObject? cache, List<string> problems, int spotCheckColdUnits = 1, string dotnet = "dotnet")
    {
        try { return ValidateCore(root, cache, problems, spotCheckColdUnits, dotnet); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or ReleaseException or ArgumentException or System.Xml.XmlException or NullReferenceException)
        {
            problems.Add("invalid verified build evidence: " + ex.Message);
            return false;
        }
    }

    private static bool ValidateCore(string root, JsonObject? cache, List<string> problems, int spotCheckColdUnits, string dotnet)
    {
        if (cache?["mode"]?.GetValue<string>() != "verified-build-units") return false;
        var before = problems.Count;
        var buildRoot = (string?)cache["buildRoot"];
        if (string.IsNullOrWhiteSpace(buildRoot)) { problems.Add("build cache has no build root"); return false; }
        var full = Path.GetFullPath(buildRoot);
        if (!string.Equals(full, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            ReleaseCommands.RequireCheckAllOwnedPath(root, full);
        if (cache["failures"] is not JsonArray failures || failures.Count != 0) problems.Add("build cache has failures or an incomplete report");
        var units = cache["units"]?.Deserialize<CheckUnitEvidence[]>(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) ?? [];
        var scripts = cache["scripts"]?.Deserialize<CheckUnitEvidence[]>(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) ?? [];
        var expectedScripts = new[] { "Test-StaticCheckParity", "Test-MatchPlcName" }
            .Where(name => File.Exists(Path.Combine(full, "scripts/checks/" + name + ".cs"))).Select(name => "file-program:" + name).Order(StringComparer.Ordinal);
        if (!expectedScripts.SequenceEqual(scripts.Select(unit => unit.Unit).Order(StringComparer.Ordinal))) problems.Add("file program build unit coverage is incomplete");
        if (units.Length == 0) problems.Add("build cache has no unit evidence");
        if (units.Select(unit => unit.Unit).Distinct(StringComparer.Ordinal).Count() != units.Length) problems.Add("build cache has duplicate units");
        var graph = (string?)cache["graphPath"];
        if (string.IsNullOrWhiteSpace(graph) || !File.Exists(graph)) problems.Add("build unit graph is missing");
        else
        {
            ReleaseCommands.RequireCheckAllOwnedPath(root, graph);
            if (ReleaseRecords.HashFile(graph) != (string?)cache["graphSha256"]) problems.Add("build unit graph changed after the run");
            var expected = System.Xml.Linq.XDocument.Load(graph).Descendants("CheckGraphProject").Select(node =>
            {
                var id = node.Element("AdditionalProperties")!.Value.Split(';').Single(property => property.StartsWith("CheckAllNodeId=", StringComparison.OrdinalIgnoreCase)).Split('=', 2)[1];
                return Path.GetRelativePath(full, node.Attribute("Include")!.Value).Replace('\\', '/') + " " + id;
            }).Order(StringComparer.Ordinal).ToArray();
            if (!expected.SequenceEqual(units.Select(unit => unit.Unit).Order(StringComparer.Ordinal))) problems.Add("build unit coverage differs from the complete executable graph");
        }
        foreach (var unit in units.Concat(scripts))
        {
            if (unit.InputKey?.Length != 64 || string.IsNullOrWhiteSpace(unit.RunId) || unit.Outputs is not { Length: > 0 } || unit.Provenance is not ("cold" or "restored"))
                problems.Add("incomplete build unit evidence: " + unit.Unit);
            if (unit.Provenance == "restored" && (unit.ColdRunIds is not { Length: > 0 } || unit.Exclusion != null))
                problems.Add("unverified restored build unit: " + unit.Unit);
            if (unit.Provenance == "cold" && unit.Exclusion != null && !Path.IsPathFullyQualified(unit.Exclusion))
                problems.Add("incomplete cold build unit: " + unit.Unit + ": " + unit.Exclusion);
            foreach (var output in unit.Outputs ?? [])
            {
                var path = Path.GetFullPath(Path.Combine(full, output.Path));
                ReleaseCommands.RequireCheckAllOwnedPath(full, path);
                if (!Directory.Exists(path) || !BuildReproducibility.Inventory(path).SequenceEqual(output.Files))
                    problems.Add("build unit output hashes changed: " + unit.Unit + "/" + output.Path);
            }
        }
        if (units.Concat(scripts).Count(unit => unit.Provenance == "restored") != (int?)cache["outputsRestored"]) problems.Add("restored build unit count differs");
        if (spotCheckColdUnits < 0) throw new ReleaseException("-SpotCheckColdUnits must be non-negative", 64);
        if (problems.Count != before) return false;
        // Sample without replacement only after all original output hashes pass.
        foreach (var unit in units.Concat(scripts).Where(unit => unit.Provenance == "restored").OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).Take(spotCheckColdUnits))
        {
            var arguments = unit.BuildArguments;
            if (arguments is not { Length: >= 2 } || arguments[0] != "build") { problems.Add("missing cold spot-check recipe: " + unit.Unit); continue; }
            ReleaseCommands.RequireCheckAllOwnedPath(full, Path.GetFullPath(arguments[1]));
            // Match the graph's MSBuild global-property identity. dotnet build
            // adds its own CLI globals, which are intentionally distinct nodes.
            string[] coldArguments = unit.Unit.StartsWith("file-program:", StringComparison.Ordinal)
                ? [.. arguments, "--no-incremental", "--no-restore", "-m:1", "-nodeReuse:false"]
                : ["msbuild", .. arguments.Skip(1), "-t:Rebuild", "-m:1", "-nodeReuse:false"];
            var result = ProcessRunner.Run(dotnet, coldArguments, full,
                new Dictionary<string, string?> { ["TIA_MCP_CHECK_CACHE_HITS"] = null, ["TIA_MCP_BUILD_CACHE_DISABLED"] = "1" });
            if (result.ExitCode != 0) { problems.Add("cold spot-check build failed: " + unit.Unit + ": " + result.StandardError); continue; }
            foreach (var output in unit.Outputs.Where(output => !output.Path.StartsWith("bin-build/unit-diagnostics/", StringComparison.Ordinal)))
                if (!BuildReproducibility.Inventory(Path.Combine(full, output.Path)).SequenceEqual(output.Files))
                    problems.Add("cold spot-check output mismatch: " + unit.Unit + "/" + output.Path);
        }
        return problems.Count == before;
    }
}

internal static partial class ReleaseCommands
{
    private static int SpotCheckColdUnits(Options options) => int.TryParse(options.Get("SpotCheckColdUnits", "1"), out var count) && count >= 0
        ? count : throw new ReleaseException("-SpotCheckColdUnits must be non-negative", 64);
}
