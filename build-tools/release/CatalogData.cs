using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal static class CatalogJson
{
    internal static readonly IReadOnlyList<string> Keys = PortedFamilies.ReleaseKeys;
    internal static JsonNode Read(string root, string path) => JsonNode.Parse(Repository.ReadSource(Path.Combine(root, path))) ?? throw new ReleaseException("Empty JSON: " + path);
    internal static string Text(JsonNode? node) => node?.GetValue<string>() ?? "";
    internal static string[] Strings(JsonNode? node) => node?.AsArray().Select(Text).ToArray() ?? [];
    internal static JsonNode Copy(JsonNode node) => node.DeepClone();
    internal static string Encode(JsonNode node) => PythonJson.Dumps(node, ensureAscii: false, indent: 2) + "\n";
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static string Source(string root, string path) => Repository.ReadSource(Path.Combine(root, path));
    internal static void Require(bool value, string message) { if (!value) throw new ReleaseException(message); }
    internal static string[] Matches(string text, string pattern, int group = 1) => Regex.Matches(text, pattern, RegexOptions.Multiline).Select(m => m.Groups[group].Value).ToArray();
    internal static HashSet<string> Set(IEnumerable<string> values) => values.ToHashSet(StringComparer.Ordinal);
    internal static IEnumerable<string> Sorted(IEnumerable<string> values) => values.Order(PythonStringComparer.Instance);
    internal static int Write(string root, Dictionary<string, string> outputs, bool check)
    {
        // Compare raw bytes: File.ReadAllText would silently accept a BOM.
        var stale = outputs.Where(p => !File.Exists(Path.Combine(root, p.Key)) || !File.ReadAllBytes(Path.Combine(root, p.Key)).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(p.Value))).Select(p => p.Key).ToArray();
        if (check)
        {
            foreach (var path in stale) Console.Error.WriteLine("stale generated output: " + path);
            return stale.Length == 0 ? 0 : 1;
        }
        foreach (var path in stale)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path))!);
            File.WriteAllText(Path.Combine(root, path), outputs[path], new UTF8Encoding(false));
        }
        return 0;
    }
}

internal sealed class CatalogData
{
    internal readonly string Root;
    internal readonly EngineSources Engine;
    internal readonly JsonObject Additions;
    internal readonly Dictionary<string, string> Renames;
    internal readonly Dictionary<string, string> HostOnly;
    internal readonly Dictionary<string, PortedFamilies.Family> Families;
    internal readonly Dictionary<string, HashSet<string>> Ported;
    internal readonly Dictionary<string, HashSet<string>> Reviewed;
    internal readonly HashSet<string> FoundationCandidates;
    internal readonly Dictionary<string, JsonObject> Archives = [];
    internal readonly Dictionary<string, HashSet<string>> Rosters = [];
    internal readonly Dictionary<string, string> Mapping;
    internal readonly Dictionary<string, string> Definitions = [];
    internal readonly Dictionary<string, string> HostMap;
    internal readonly HashSet<string> Removed;
    internal readonly HashSet<string> Bridge;
    internal readonly Dictionary<string, (string Path, string Method, string Domain)> Registrations = [];
    internal static readonly HashSet<string> Merged = ["GetAuthoringGuide", "GetRecipe"];

    internal CatalogData(string root)
    {
        Root = root; Engine = new EngineSources(root);
        Additions = CatalogJson.Read(root, "reference/tool-catalog/new-v4-tools.json").AsObject();
        Renames = CatalogJson.Read(root, "reference/tool-catalog/v3-v4-renames.json").AsObject().ToDictionary(p => p.Key, p => CatalogJson.Text(p.Value));
        Families = PortedFamilies.Load(root);
        FoundationCandidates = PortedFamilies.FoundationCandidates(root);
        Reviewed = CatalogJson.Keys.ToDictionary(k => k, k => PortedFamilies.Additions(Families, k));
        Ported = Reviewed.ToDictionary(p => p.Key, p => CatalogJson.Set(p.Value.Except(Families["F00"].Tools)));
        foreach (var key in CatalogJson.Keys)
            Archives[key] = CatalogJson.Read(root, $"manifest/history/contracts-v3/baseline/{key}.json")["tools"]!.AsArray().ToDictionary(t => CatalogJson.Text(t!["name"]), t => t!.AsObject()).ToJsonObject();
        ValidateReviewedData(Additions, Renames, CatalogJson.Set(Archives.Values.SelectMany(v => v.Select(p => p.Key))));
        foreach (var name in Additions.Select(p => p.Key)) Renames.Add(name, name);
        var metadata = CatalogJson.Source(root, "src/Logic/ModelContextProtocol/ToolMetadata.cs");
        var workbench = CatalogJson.Source(root, "src/FoundationHost/WorkbenchControlTools.cs").Split("string[] Names = {", 2)[1].Split("};", 2)[0];
        var wbNames = CatalogJson.Matches(workbench, "\"([A-Za-z0-9]+)\"");
        CatalogJson.Require(wbNames.Length == 8 && wbNames.Distinct().Count() == 8, "Closed Workbench roster differs");
        HostOnly = Regex.Matches(metadata, "\\[\"([^\"\\n]+)\"\\] = new Classification\\(\"L1\", \"Workbench\", \"(UI|READ)\",").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        CatalogJson.Require(CatalogJson.Set(HostOnly.Keys).SetEquals(wbNames), "Workbench classification coverage differs");
        var packages = Additions.Where(p => CatalogJson.Text(p.Value!["owner"]) is "P8-31d" or "P8-31e").ToDictionary(p => p.Key, p => CatalogJson.Text(p.Value!["operation"]));
        var generation = CatalogJson.Source(root, "src/FoundationHost/GenerationTool.cs");
        CatalogJson.Require(CatalogJson.Set(CatalogJson.Matches(generation, "new GenerationTool\\(\"([^\"]+)\"")).SetEquals(packages.Keys), "Closed package roster differs");
        var classifications = Regex.Matches(metadata, "\\[\"([^\"\\n]+)\"\\] = new Classification\\(\"L1\", \"Project\", \"(READ|FILE|OFFLINE|WRITE)\",").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        CatalogJson.Require(packages.All(p => classifications.GetValueOrDefault(p.Key) == p.Value), "Package classification differs");
        foreach (var p in packages) HostOnly.Add(p.Key, p.Value);
        foreach (var (path, source) in Engine.Sources)
            foreach (Match m in Regex.Matches(source, "\\[McpServerTool\\(Name\\s*=\\s*\"([^\"]+)\""))
            {
                var tail = source[(m.Index + m.Length)..];
                var decl = Regex.Match(tail, @"^\s*public\s+(?:static\s+)?(?:async\s+)?[\w<>?,\[\] .]+?\s+(\w+)\s*\(", RegexOptions.Multiline);
                var description = Regex.Match(tail, "\\)\\s*,\\s*Description\\(\\s*\"([^\"\\n]*)");
                var tags = Regex.Match(description.Groups[1].Value, @"\[L\d\]\[([^\]]+)\]");
                CatalogJson.Require(decl.Success && description.Success && tags.Success, "Missing declaration/description/domain: " + m.Groups[1].Value);
                CatalogJson.Require(Registrations.TryAdd(m.Groups[1].Value, (Path.GetRelativePath(root, path).Replace('\\', '/'), decl.Groups[1].Value, tags.Groups[1].Value.Replace("Category:", "", StringComparison.Ordinal))), "Duplicate source registration");
            }
        var full = CatalogJson.Set(Registrations.Keys.Except(HostOnly.Keys));
        var historical = CatalogJson.Keys.ToDictionary(k => k, k => CatalogJson.Set(Archives[k].Select(p => p.Key)));
        foreach (var (name, row) in Additions)
            if (!HostOnly.ContainsKey(name)) foreach (var key in CatalogJson.Strings(row!["releases"])) historical[key].Add(name);
        Mapping = Resolve(historical["21"], full, Renames, true);
        foreach (var key in new[] { "20", "21" }) Rosters[key] = CatalogJson.Set(historical[key].Select(n => Mapping[n]));
        var sources = Directory.GetFiles(Path.Combine(root, "src/FoundationHost"), "*.cs").Order(StringComparer.Ordinal).ToDictionary(p => Path.GetFileName(p)!, Repository.ReadSource);
        foreach (var line in sources["FoundationTools.cs"].Split('\n'))
        {
            var m = Regex.Match(line, "^\\s*new\\(\"([^\"]+)\"");
            if (m.Success) Definitions[m.Groups[1].Value] = Regex.Match(line, ",\\s*\"([^\"]+)\"\\),?\\s*$").Groups[1].Value;
        }
        var helpers = CatalogJson.Set(sources.Where(p => p.Key != "FoundationTools.cs").SelectMany(p => Regex.Matches(p.Value, "new (?:Offline\\w+Tool|PassiveDiagnosticTool|GenerationTool)\\(\"([^\"]+)\"|\\bName\\s*=\\s*\"([^\"]+)\"").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)));
        helpers.IntersectWith(Renames.Keys.Concat(Renames.Values)); helpers.ExceptWith(HostOnly.Keys);
        HostMap = sources.Values.Any(s => s.Contains("new FoundationV4Tool(", StringComparison.Ordinal)) ? Regex.Matches(sources.GetValueOrDefault("FoundationV4Tool.cs", "").Split("internal static string Name", 2)[0], "\\[\"([^\"]+)\"\\]\\s*=\\s*\"([^\"]+)\"").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value) : [];
        CatalogJson.Require(HostMap.All(p => Renames[p.Key] == p.Value), "Foundation names differ from reviewed rename data");
        foreach (var key in CatalogJson.Keys.Take(6))
        {
            var major = key == "14sp1" ? 14 : int.Parse(key.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
            var accepted = Definitions.Where(p => !(p.Value is "HardwareCatalog" or "DeviceAdd" && major < 18) && !(p.Value == "SpecialExport" && major < (p.Key == "ExportPlcWatchTable" ? 15 : 16)) && !(p.Value is "DocumentExport" or "BatchDocumentExport" or "DocumentImport" or "BatchDocumentImport" && major < 20) && !(p.Key is "GetPlcWatchTables" or "ListPlcWatchTables" && key == "14sp1")).Select(p => p.Key);
            var registered = CatalogJson.Set(accepted.Concat(helpers).Select(n => HostMap.GetValueOrDefault(n, n)).Concat(Reviewed[key]));
            var withheld = PortedFamilies.Withheld(Families, key);
            registered.ExceptWith(withheld);
            foreach (var n in Reviewed[key]) Renames.TryAdd(n, n);
            Resolve(historical[key].Where(n => !withheld.Contains(Renames[n])).Concat(Reviewed[key]), registered, Renames);
            Rosters[key] = registered;
        }
        var policy = CatalogJson.Source(root, "src/EngineHost/SharedToolCatalog.cs");
        Removed = CatalogJson.Set(CatalogJson.Matches(policy.Split("Removed =", 2)[1].Split("};", 2)[0], "\"(\\w+)\""));
        Bridge = CatalogJson.Set(CatalogJson.Matches(policy.Split("Bridge =", 2)[1].Split("};", 2)[0], "\"(\\w+)\""));
        var extras = Definitions.Where(p => p.Value is "DocumentExport" or "BatchDocumentExport" or "DocumentImport" or "BatchDocumentImport").Select(p => HostMap.GetValueOrDefault(p.Key, p.Key));
        foreach (var key in new[] { "20", "21" }) { Rosters[key].ExceptWith(Removed); Rosters[key].UnionWith(Rosters["19"]); Rosters[key].UnionWith(extras); Rosters[key].ExceptWith(PortedFamilies.Withheld(Families, key)); }
        foreach (var roster in Rosters.Values) roster.UnionWith(HostOnly.Keys);
        foreach (var key in CatalogJson.Keys) Rosters[key].ExceptWith(PortedFamilies.Withheld(Families, key));
        foreach (var name in Rosters.Values.SelectMany(r => r).Distinct(StringComparer.Ordinal)) ValidateName(name);
    }

    internal static void ValidateReviewedData(JsonObject additions, Dictionary<string, string> renames, HashSet<string> archived)
    {
        CatalogJson.Require(CatalogJson.Set(renames.Keys).SetEquals(archived), "Frozen rename table must cover the archive exactly");
        var verbs = "Analyze Apply Archive Attach Audit Bind Build Call Check Cleanup Clear Close Compare Compile Configure Connect Create Decode Delete Describe Disconnect Download Ensure Exchange Export Extract Find Generate Get Import Initialize Inspect Instantiate Invoke List Manage Monitor Move Open Patch Plan Plug Prefill Preview Probe Publish Release Render Repair Resolve Restart Retrieve Run Sample Save Scan Search Seed Set Show Stage Synchronize Trace Upgrade Upload Validate Write".Split(' ');
        foreach (var (old, target) in renames)
            CatalogJson.Require(Regex.IsMatch(target, "^[A-Z][A-Za-z0-9]+$") && verbs.Contains(Regex.Match(target, "^[A-Z][a-z]*").Value) && !new[] { "Json", "Xml", "Xlsx", "SiVArc" }.Any(target.Contains), "Unreviewed rename: " + old);
        foreach (var group in renames.GroupBy(p => p.Value, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            CatalogJson.Require(group.Key == "GetToolUsage" && CatalogJson.Set(group.Select(p => p.Key)).SetEquals(new[] { "GetToolUsage", "GetAuthoringGuide", "GetRecipe" }), "Unproven duplicate rename: " + group.Key);
        var reviewed = new Dictionary<string, (string Owner, string Operation)>
        {
            ["RenderPlcBlock"] = ("P6-48", "FILE"), ["RenderPlcProgramAtlas"] = ("P6-48", "FILE"),
            ["StageImportFiles"] = ("P6-67", "FILE"), ["ListStagedImportFiles"] = ("P6-67", "READ"), ["CleanupStagedImportFiles"] = ("P6-67", "FILE"),
            ["ListStandardPackages"] = ("P8-31d", "READ"), ["ManageStandardPackage"] = ("P8-31d", "FILE"), ["ValidateStandardPackage"] = ("P8-31d", "OFFLINE"), ["DescribeStandardPackage"] = ("P8-31d", "READ"), ["ManageMachineDescription"] = ("P8-31d", "FILE"),
            ["PlanProjectGeneration"] = ("P8-31e", "READ"), ["ApplyGenerationPlan"] = ("P8-31e", "WRITE"), ["GetGenerationRun"] = ("P8-31e", "READ")
        };
        foreach (var (name, node) in additions)
        {
            var row = node!.AsObject(); var owner = CatalogJson.Text(row["owner"]); var operation = CatalogJson.Text(row["operation"]);
            CatalogJson.Require(!archived.Contains(name) && Regex.IsMatch(name, "^[A-Z][A-Za-z0-9]+$") && verbs.Contains(Regex.Match(name, "^[A-Z][a-z]*").Value) && CatalogJson.Set(row.Select(p => p.Key)).SetEquals(new[] { "owner", "operation", "releases" }), "Invalid new V4 entry: " + name);
            CatalogJson.Require(CatalogJson.Strings(row["releases"]).SequenceEqual(CatalogJson.Keys), "Unreviewed new V4 release list: " + name);
            CatalogJson.Require(reviewed.TryGetValue(name, out var expected) ? expected == (owner, operation) : owner is "P8-31d" or "P8-31e" && operation is "READ" or "FILE" or "OFFLINE" or "WRITE", "Unreviewed owner/operation: " + name);
        }
        CatalogJson.Require(reviewed.Keys.All(additions.ContainsKey), "Missing reviewed new V4 entry");
    }
    private static void ValidateName(string name)
    {
        var verbs = "Analyze Apply Archive Attach Audit Bind Build Call Check Cleanup Clear Close Compare Compile Configure Connect Create Decode Delete Describe Disconnect Download Ensure Exchange Export Extract Find Generate Get Import Initialize Inspect Instantiate Invoke List Manage Monitor Move Open Patch Plan Plug Prefill Preview Probe Publish Release Render Repair Resolve Restart Retrieve Run Sample Save Scan Search Seed Set Show Stage Synchronize Trace Upgrade Upload Validate Write".Split(' ');
        CatalogJson.Require(Regex.IsMatch(name, "^[A-Z][A-Za-z0-9]+$") && verbs.Contains(Regex.Match(name, "^[A-Z][a-z]*").Value), "Unreviewed V4 tool name: " + name);
    }
    internal static Dictionary<string, string> Resolve(IEnumerable<string> baseline, HashSet<string> registered, Dictionary<string, string> targets, bool merged = false)
    {
        var result = new Dictionary<string, string>();
        foreach (var old in baseline)
        {
            var candidates = CatalogJson.Set(new[] { old, targets[old] }.Where(registered.Contains));
            if (merged && Merged.Contains(old)) { CatalogJson.Require(!registered.Contains(old), "Merged alias still registered: " + old); candidates = CatalogJson.Set(new[] { "GetToolUsage" }.Where(registered.Contains)); }
            CatalogJson.Require(candidates.Count == 1, "Missing or double registration: " + old);
            result[old] = candidates.Single();
        }
        CatalogJson.Require(CatalogJson.Set(result.Values).SetEquals(registered), "Unmapped registrations: " + string.Join(", ", registered.Except(result.Values)));
        return result;
    }
}

internal static class CatalogDictionaryExtensions
{
    internal static JsonObject ToJsonObject(this Dictionary<string, JsonObject> values) { var result = new JsonObject(); foreach (var p in values) result.Add(p.Key, p.Value.DeepClone()); return result; }
}
