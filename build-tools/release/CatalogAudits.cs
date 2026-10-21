using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TiaMcp.BuildCommon;
using static TiaMcp.ReleaseTool.CatalogJson;

namespace TiaMcp.ReleaseTool;

internal static class CatalogAudits
{
    private const string Method = "Group actual release catalogs and compare official XML members with compiled native call sites.\n\nRead-only against Siemens SDKs; emits identifiers and hashes, never copies SDK files.\nDirect member-name references are evidence of compiled calls, not end-to-end tool coverage.\nOverloads are counted separately in domainSignatures and together in domainMemberNames.\n";
    private static readonly HashSet<string> Boiler = Set("Equals GetHashCode ToString GetEnumerator Any Contains IndexOf CopyTo GetAttribute GetAttributes GetAttributeInfos SetAttribute SetAttributes GetComposition GetCompositionInfos GetInvocationInfos GetCreationInfos GetService GetServiceInfos Invoke Find Count Parent Dispose Clone CompareTo GetType MemberwiseClone Finalize Item".Split(' '));
    /// <summary>The bin-build/releases/v&lt;version&gt; folder of the full-engine evidence: the engine record's release label (a pre-release
    /// such as 4.0.0-beta.1 shares the file version 4.0.0.0 with 4.0.0) when its file version matches the tools list, otherwise the
    /// numeric version taken from the file version as before.</summary>
    internal static string ReleaseDirectoryVersion(string fileVersion, string? recordRelease)
    {
        var numeric = fileVersion[..fileVersion.LastIndexOf('.')];
        return recordRelease is { Length: > 0 } && ReleaseVersions.IsValid(recordRelease) && ReleaseVersions.FileVersionOf(recordRelease) == fileVersion ? recordRelease : numeric;
    }

    internal static Dictionary<string, string> Version(string root, string apiRoot, string output, string? inputsRoot = null, bool legacyLineEndings = false)
    {
        var input = inputsRoot ?? Path.Combine(root, "bin-build"); var full = Read(root, "manifest/tools-list.json");
        var policy = Source(root, "src/Logic/Siemens/ToolVersionPolicy.cs").Split("internal static string ToolProblem", 2)[0];
        var only21 = Set(Matches(policy, "\\[\"([^\"]+)\"\\]\\s*=")); var names = Set(full["tools"]!.AsArray().Select(t => Text(t!["name"]))); Require(only21.IsSubsetOf(names), "V21 admission policy names absent from built roster");
        var families = PortedFamilies.Load(root);
        var catalogs = Keys.Take(6).ToDictionary(k => k, k => Set(Read(input, $"multi-version/tools-{k}.json")["tools"]!.AsArray().Select(t => Text(t!["name"]))));
        catalogs["20"] = Set(names.Except(only21).Except(PortedFamilies.Withheld(families, "20")));
        catalogs["21"] = Set(names.Except(PortedFamilies.Withheld(families, "21")));
        var expected = new CatalogData(root).Rosters; foreach (var key in Keys) Require(catalogs[key].SetEquals(expected[key]), "Built/source catalog mismatch: " + key);
        foreach (var key in new[] { "20", "21" })
        {
            var hostCatalog = Set(Read(input, $"multi-version/tools-{key}.json")["tools"]!.AsArray().Select(t => Text(t!["name"])));
            Require(hostCatalog.SetEquals(expected[key]) && hostCatalog.SetEquals(catalogs[key]), "Host/engine/source catalog mismatch: " + key);
        }
        var results = new Dictionary<string, string>(); var statistics = new JsonArray();
        foreach (var key in Keys)
        {
            var relative = key switch { "14sp1" => "TIA_V14SP1_PublicAPI/V14 SP1", "15.1" => "TIA_V15.1_PublicAPI/V15.1", "21" => "TIA_V21_PublicAPI/V21/net48", _ => $"TIA_V{key}_PublicAPI/V{key}" };
            var api = Path.Combine(apiRoot, relative); var xmlFiles = Directory.GetFiles(api, "Siemens.Engineering*.xml").Where(p => !Path.GetFileName(p).Contains(".AddIn", StringComparison.Ordinal)).Order(PythonStringComparer.Instance).ToArray(); Require(xmlFiles.Length > 0, "No XML: " + api);
            var signatures = new HashSet<string>(); var members = new HashSet<(string Kind, string Owner, string Member)>();
            foreach (var path in xmlFiles) foreach (var element in XDocument.Load(path).Descendants("member"))
            {
                var signature = (string?)element.Attribute("name") ?? "";
                if (!(signature.StartsWith("M:", StringComparison.Ordinal) || signature.StartsWith("P:", StringComparison.Ordinal)) || signature.StartsWith("M:Siemens.Engineering.Private.", StringComparison.Ordinal) || signature.StartsWith("P:Siemens.Engineering.Private.", StringComparison.Ordinal) || signature.Contains("Siemens.Engineering.Compiler.CompileProvider", StringComparison.Ordinal)) continue;
                var stem = signature[2..].Split('(')[0]; var split = stem.LastIndexOf('.'); var owner = split < 0 ? "" : stem[..split]; var member = stem[(split + 1)..];
                if (member.Contains('#') || Boiler.Contains(member)) continue; signatures.Add(signature); members.Add((signature[..1], owner, member.Split("``", 2)[0]));
            }
            string coverage;
            if (key is "20" or "21") { var recordPath = Path.Combine(root, "manifest/release-build.json"); var version = ReleaseDirectoryVersion(Text(full["fileVersion"]), File.Exists(recordPath) ? Text(Read(root, "manifest/release-build.json")["release"]) : null); var releaseDir = Path.Combine(input, "releases/v" + version); coverage = Path.Combine(releaseDir, $"v{key}/native-call-coverage-v{key}.json"); if (!File.Exists(coverage)) coverage = Path.Combine(releaseDir, $"native-call-coverage-v{key}.json"); }
            else coverage = Path.Combine(input, $"multi-version/adapters/coverage-{key}.json");
            var calls = new HashSet<(string Kind, string Owner, string Member)>();
            foreach (var site in Read("", coverage)["sites"]!.AsArray())
            {
                var match = Regex.Match(Text(site!["member"]), @" (Siemens\.Engineering\.[^ ]+?)::([^\(]+)\("); if (!match.Success) continue;
                var owner = Regex.Replace(match.Groups[1].Value, "<.*>", "").Replace('/', '.'); var member = match.Groups[2].Value; var kind = member.StartsWith("get_", StringComparison.Ordinal) || member.StartsWith("set_", StringComparison.Ordinal) ? "P" : "M"; if (kind == "P") member = member[4..]; calls.Add((kind, owner, Regex.Replace(member, "<.*>", "")));
            }
            var observed = members.Intersect(calls).ToHashSet(); var candidates = members.Except(observed).ToArray();
            var byNamespace = candidates.GroupBy(p => p.Owner.Contains('.') ? p.Owner[..p.Owner.LastIndexOf('.')] : p.Owner).Select(g => (Name: g.Key, Count: g.Count())).OrderByDescending(p => p.Count).ThenBy(p => p.Name, PythonStringComparer.Instance).Take(8);
            var hashes = new JsonObject(); foreach (var path in xmlFiles) hashes[Path.GetFileName(path)] = Hash(File.ReadAllBytes(path));
            statistics.Add(new JsonObject { ["releaseKey"] = key, ["mcpTools"] = catalogs[key].Count, ["xmlFiles"] = xmlFiles.Length, ["domainSignatures"] = signatures.Count, ["domainMemberNames"] = members.Count, ["compiledDirectMemberNames"] = observed.Count, ["withoutDirectMemberReference"] = candidates.Length,
                ["topCandidateNamespaces"] = new JsonArray(byNamespace.Select(p => (JsonNode)new JsonArray(p.Name, p.Count)).ToArray()), ["sdkXmlSha256"] = hashes, ["compiledCallInventorySha256"] = Hash(File.ReadAllBytes(coverage)) });
            if (key is "20" or "21")
            {
                var plcCoverage = Path.Combine(input, $"multi-version/adapters/coverage-{key}.json");
                var plcCalls = new HashSet<(string Kind, string Owner, string Member)>();
                foreach (var site in Read("", plcCoverage)["sites"]!.AsArray())
                {
                    var match = Regex.Match(Text(site!["member"]), @" (Siemens\.Engineering\.[^ ]+?)::([^\(]+)\("); if (!match.Success) continue;
                    var owner = Regex.Replace(match.Groups[1].Value, "<.*>", "").Replace('/', '.'); var member = match.Groups[2].Value; var kind = member.StartsWith("get_", StringComparison.Ordinal) || member.StartsWith("set_", StringComparison.Ordinal) ? "P" : "M"; if (kind == "P") member = member[4..]; plcCalls.Add((kind, owner, Regex.Replace(member, "<.*>", "")));
                }
                statistics[^1]!["plcCompiledDirectMemberNames"] = members.Intersect(plcCalls).Count();
                statistics[^1]!["plcCompiledCallInventorySha256"] = Hash(File.ReadAllBytes(plcCoverage));
                statistics[^1]!["hostCatalogSha256"] = Hash(File.ReadAllBytes(Path.Combine(input, $"multi-version/tools-{key}.json")));
            }
            results[Path.Combine(output, $"api-candidates-{key}.json")] = Encode(new JsonObject { ["releaseKey"] = key, ["caveat"] = "No exact owner/member-name direct call found. Dynamic calls, inherited/generic targets and unadvertised adapter code require manual review. This is not a missing-tool count.", ["members"] = new JsonArray(candidates.OrderBy(p => p.Kind, PythonStringComparer.Instance).ThenBy(p => p.Owner, PythonStringComparer.Instance).ThenBy(p => p.Member, PythonStringComparer.Instance).Select(p => (JsonNode?)JsonValue.Create($"{p.Kind}:{p.Owner}.{p.Member}")).ToArray()) });
        }
        results["manifest/version-api-audit.json"] = Encode(new JsonObject { ["method"] = Method, ["nativeTiaExecuted"] = false, ["versions"] = statistics });
        return legacyLineEndings ? results.ToDictionary(p => p.Key, p => p.Value.Replace("\n", "\r\n", StringComparison.Ordinal)) : results;
    }
    internal static JsonObject Usage(string root, string? inputsRoot = null, string? recordsPath = null)
    {
        var input = inputsRoot ?? Path.Combine(root, "bin-build"); var matrix = Read(root, "manifest/version-tools.json")["releases"]!.AsObject(); var release = Text(Read(root, "manifest/release-build.json")["release"]);
        var source = "src/Shared/ToolUsageData.json"; var catalog = Read(root, source);
        if (recordsPath is not null)
        {
            // The remaining Python STDIO capturers emit one receipt per release.
            var receipts = Read(root, recordsPath).AsArray();
            Require(receipts.Count == Keys.Count && Set(receipts.Select(r => Text(r!["releaseKey"]))).SetEquals(Keys), "Exactly eight distinct usage receipts required");
            Require(receipts.All(r => r!["allSourceTextRetrievedAndHashChecked"]!.GetValue<bool>()), "Source text hash check required for every release");
            return UsageFromRecords(root, matrix, catalog, receipts);
        }
        var records = Read(input, "multi-version/transport/tool-usage.json").AsArray();
        foreach (var key in new[] { "20", "21" })
        {
            var path = $"releases/v{release}/v{key}/tool-usage-v{key}.json"; if (!File.Exists(Path.Combine(input, path))) path = $"releases/v{release}/tool-usage-v{key}.json";
            var full = Read(input, path).AsArray(); Require(full.Count == 4 && full.All(r => r!["checkedToolCount"]!.GetValue<int>() > 0), "Four full/lite transport reports required: " + key);
            var selected = full.First(r => Text(r!["profile"]) == "full" && Text(r["transport"]) == "stdio")!; Require(selected["allSourceTextRetrievedAndHashChecked"]!.GetValue<bool>(), "Source text hash check required: " + key); records.Add(Copy(selected));
        }
        var result = UsageFromRecords(root, matrix, catalog, records);
        var plcRecords = new JsonArray(records.Where(row => Text(row!["releaseKey"]) is not ("20" or "21")).Select(row => row!.DeepClone()).ToArray());
        foreach (var key in new[] { "20", "21" })
            plcRecords.Add(Read(input, $"multi-version/usage-v{key}-plc.json").AsArray().Single()!.DeepClone());
        Require(plcRecords.Count == Keys.Count && Set(plcRecords.Select(row => Text(row!["releaseKey"]))).SetEquals(Keys), "Exactly eight PLC transport usage receipts required");
        Require(plcRecords.All(row => row!["allSourceTextRetrievedAndHashChecked"]!.GetValue<bool>()), "Source text hash check required for every PLC usage receipt");
        // Validated with the same rules, but not written: audit-tool-usage writes this result to manifest/tool-usage-coverage.json,
        // whose committed bytes check-all's engine and PLC usage audits both reproduce from their own receipts.
        _ = UsageFromRecords(root, matrix, catalog, plcRecords);
        return result;
    }
    internal static JsonObject UsageFromRecords(string root, JsonObject matrix, JsonNode catalog, JsonArray records)
    {
        var releases = new JsonObject();
        foreach (var record in records)
        {
            var key = Text(record!["releaseKey"]); Require(!record["exampleCallsExecuted"]!.GetValue<bool>(), "Native usage calls must not execute");
            Require(record["registeredToolCount"]!.GetValue<int>() == record["checkedToolCount"]!.GetValue<int>() && record["checkedToolCount"]!.GetValue<int>() == matrix[key]!["toolCount"]!.GetValue<int>(), "Usage count mismatch: " + key);
            Require(Set(record["tools"]!.AsArray().Select(r => Text(r!["toolName"]))).SetEquals(Strings(matrix[key]!["tools"])), "Usage roster mismatch: " + key);
            var schemas = record["inputSchemas"]?.AsObject() ?? throw new ReleaseException("Actual served input schemas required: " + key);
            Require(Set(schemas.Select(p => p.Key)).SetEquals(Strings(matrix[key]!["tools"])), "Usage schema roster mismatch: " + key);
            ValidateUsageExamples(catalog, schemas, key, record["tools"]!.AsArray()
                .ToDictionary(t => Text(t!["toolName"]), t => Text(t!["usageProfile"])));
            var row = new JsonObject(); foreach (var p in record.AsObject().Where(p => p.Key is not "profile" and not "transport" and not "inputSchemas")) row[p.Key] = p.Value?.DeepClone(); releases[key] = row;
        }
        Require(Set(releases.Select(p => p.Key)).SetEquals(matrix.Select(p => p.Key)), "Usage release coverage differs"); var union = Set(matrix.SelectMany(p => Strings(p.Value!["tools"]))); Require(union.SetEquals(catalog["tools"]!.AsObject().Select(p => p.Key)), "Missing or obsolete usage mapping");
        return new JsonObject { ["basis"] = "Actual MCP tools/list and GetToolUsage responses for every tool/operation on all eight releases. Parameterized calls, input origins, result contracts, language files, sequence calls and source hashes checked. Allowlisted in-memory call examples execute on every release; target-bound native example operations NOT executed.",
            ["catalogSha256"] = Hash(Encoding.UTF8.GetBytes(Source(root, "src/Shared/ToolUsageData.json"))), ["uniqueTools"] = union.Count, ["documents"] = catalog["documents"]!.AsArray().Count, ["versionToolPairs"] = matrix.Sum(p => p.Value!["toolCount"]!.GetValue<int>()), ["offlineCallExecutions"] = releases.Sum(p => p.Value!["offlineCallExamplesExecuted"]!.AsArray().Count), ["indexedExampleBlocksOrMethods"] = catalog["documents"]!.AsArray().Sum(d => d!["examples"]!.AsArray().Count), ["programmingExamples"] = catalog["examples"]!.AsArray().Count, ["callSequences"] = catalog["sequences"]!.AsArray().Count,
            ["languages"] = new JsonArray(catalog["languages"]!.AsArray().Select(l => Copy(l!["id"]!)).ToArray()), ["relatedOfficialPatternMappings"] = catalog["tools"]!.AsObject().Count(p => Text(p.Value!["relationship"]) == "related-api-patterns"), ["noDirectVendoredExampleMappings"] = catalog["tools"]!.AsObject().Count(p => Text(p.Value!["relationship"]) == "no-direct-official-example"), ["sources"] = Copy(catalog["sources"]!), ["nativeTiaExecuted"] = false, ["releases"] = releases };
    }

    internal static void ValidateUsageExamples(JsonNode catalog, JsonObject schemas, string release, IReadOnlyDictionary<string, string>? usageProfiles = null)
    {
        var profile = release is "20" or "21" ? "full-engine" : "plc-foundation";
        var problems = new List<string>();
        void Check(string name, JsonObject arguments, string location)
        {
            Require(schemas[name] is JsonObject, "Example tool is absent from the served catalog: " + location);
            var schema = new TiaMcp.Logic.V4.Inputs.InputSchema(JsonSerializer.SerializeToElement(schemas[name]));
            var error = schema.Validate(JsonSerializer.SerializeToElement(arguments), "arguments");
            if (error != null) problems.Add(release + "/" + location + ": " + TiaMcp.Logic.V4.V4Json.Serialize(error));
        }
        JsonObject Bound(JsonNode arguments) => JsonNode.Parse(arguments.ToJsonString()
            .Replace("{release}", release, StringComparison.Ordinal).Replace("{major}", release is "14sp1" ? "14" : release is "15.1" ? "15" : release, StringComparison.Ordinal)
            .Replace("{extension}", Text(catalog["calls"]!["releaseExtensions"]![release]), StringComparison.Ordinal))!.AsObject();
        foreach (var sourceProfile in catalog["calls"]!["profiles"]!.AsObject())
        foreach (var pair in sourceProfile.Value!.AsObject())
        {
            if (!schemas.ContainsKey(pair.Key)) continue; // A union profile also includes tools from other releases.
            var servedProfile = usageProfiles?.GetValueOrDefault(pair.Key);
            if (sourceProfile.Key != (string.IsNullOrEmpty(servedProfile) ? profile : servedProfile)) continue;
            var entry = pair.Value!; var args = Bound(entry["arguments"]!);
            ToolUsageGenerator.ValidateHints(entry, schemas[pair.Key]!["properties"]!.AsObject().Select(p => p.Key), release + "/" + pair.Key);
            Check(pair.Key, args, sourceProfile.Key + "/" + pair.Key);
            void Variant(JsonNode patch, string location)
            {
                var variant = args.DeepClone().AsObject();
                foreach (var field in Bound(patch)) variant[field.Key] = field.Value?.DeepClone();
                Check(pair.Key, variant, sourceProfile.Key + "/" + pair.Key + "/" + location);
            }
            if (entry["execution"]?["arguments"] is JsonObject execution) Variant(execution, "execution");
            foreach (var operation in entry["operations"]?.AsObject() ?? [])
            {
                ToolUsageGenerator.ValidateHints(operation.Value!, schemas[pair.Key]!["properties"]!.AsObject().Select(p => p.Key), release + "/" + pair.Key + "/" + operation.Key);
                Variant(operation.Value!["arguments"]!, "operations/" + operation.Key);
            }
        }
        foreach (var example in catalog["examples"]!.AsArray().Concat(catalog["sequences"]!.AsArray()))
        {
            if (!Strings(example!["releaseKeys"]).Contains(release) || example["profile"] != null && Text(example["profile"]) != profile) continue;
            foreach (var step in example["steps"]?.AsArray() ?? [])
                Check(Text(step!["tool"]), Bound(step["arguments"]!), Text(example["id"]));
        }
        Require(problems.Count == 0, "Example arguments refused:\n" + string.Join("\n", problems));
    }
}
