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
    internal const string VersionAuditFolder = "version-audit";
    private const string Method = "Group actual release catalogs and compare official XML members with compiled native call sites.\n\nRead-only against Siemens SDKs; emits identifiers and hashes, never copies SDK files.\nDirect member-name references are evidence of compiled calls, not end-to-end tool coverage.\nOverloads are counted separately in domainSignatures and together in domainMemberNames.\n";
    private static readonly HashSet<string> Boiler = Set("Equals GetHashCode ToString GetEnumerator Any Contains IndexOf CopyTo GetAttribute GetAttributes GetAttributeInfos SetAttribute SetAttributes GetComposition GetCompositionInfos GetInvocationInfos GetCreationInfos GetService GetServiceInfos Invoke Find Count Parent Dispose Clone CompareTo GetType MemberwiseClone Finalize Item".Split(' '));
    internal static (HashSet<string> Signatures, HashSet<(string Kind, string Owner, string Member)> Members)
        DocumentedMembers(IEnumerable<string> xmlFiles)
    {
        var signatures = new HashSet<string>();
        var members = new HashSet<(string Kind, string Owner, string Member)>();
        foreach (var path in xmlFiles)
        foreach (var element in XDocument.Load(path).Descendants("member"))
        {
            var signature = (string?)element.Attribute("name") ?? "";
            if (!(signature.StartsWith("M:", StringComparison.Ordinal) || signature.StartsWith("P:", StringComparison.Ordinal)) ||
                signature.StartsWith("M:Siemens.Engineering.Private.", StringComparison.Ordinal) ||
                signature.StartsWith("P:Siemens.Engineering.Private.", StringComparison.Ordinal) ||
                signature.Contains("Siemens.Engineering.Compiler.CompileProvider", StringComparison.Ordinal)) continue;
            var stem = signature[2..].Split('(')[0];
            var split = stem.LastIndexOf('.');
            var owner = split < 0 ? "" : stem[..split];
            var member = stem[(split + 1)..];
            if (member.Contains('#') || Boiler.Contains(member)) continue;
            signatures.Add(signature);
            members.Add((signature[..1], owner, member.Split("``", 2)[0]));
        }
        return (signatures, members);
    }

    internal static HashSet<(string Kind, string Owner, string Member)> CompiledMembers(JsonNode inventory)
    {
        var calls = new HashSet<(string Kind, string Owner, string Member)>();
        foreach (var site in inventory["sites"]!.AsArray())
        {
            var match = Regex.Match(Text(site!["member"]), @" (Siemens\.Engineering\.[^ ]+?)::([^\(]+)\(");
            if (!match.Success) continue;
            var owner = Regex.Replace(match.Groups[1].Value, "<.*>", "").Replace('/', '.');
            var member = match.Groups[2].Value;
            var kind = member.StartsWith("get_", StringComparison.Ordinal) || member.StartsWith("set_", StringComparison.Ordinal) ? "P" : "M";
            if (kind == "P") member = member[4..];
            calls.Add((kind, owner, Regex.Replace(member, "<.*>", "")));
        }
        return calls;
    }
    /// <summary>The bin-build/releases/v&lt;version&gt; folder of a release build: the release build record's release label (a pre-release
    /// such as 4.0.0-beta.1 shares the file version 4.0.0.0 with 4.0.0) when its file version matches the tools list, otherwise the
    /// numeric version taken from the file version as before.</summary>
    internal static string ReleaseDirectoryVersion(string fileVersion, string? recordRelease)
    {
        var numeric = fileVersion[..fileVersion.LastIndexOf('.')];
        return recordRelease is { Length: > 0 } && ReleaseVersions.IsValid(recordRelease) && ReleaseVersions.FileVersionOf(recordRelease) == fileVersion ? recordRelease : numeric;
    }

    internal static Dictionary<string, string> Version(string root, string apiRoot, string output, string? inputsRoot = null, bool legacyLineEndings = false)
    {
        var input = Path.Combine(inputsRoot ?? Path.Combine(root, "bin-build"), "prepared", ReleaseVersions.ReadVersionProps(root).Label, VersionAuditFolder);
        Require(Directory.Exists(input), "Missing prepared audit inputs: " + input + ". Run prepare -BuildOnly for the current release.");
        var full = Read(root, "manifest/tools-list.json");
        var policy = Source(root, "src/Logic/Siemens/ToolVersionPolicy.cs").Split("internal static string ToolProblem", 2)[0];
        var only21 = Set(Matches(policy, "\\[\"([^\"]+)\"\\]\\s*=")); var names = Set(full["tools"]!.AsArray().Select(t => Text(t!["name"]))); Require(only21.IsSubsetOf(names), "V21 admission policy names absent from built roster");
        var data = new CatalogData(root);
        var results = new Dictionary<string, string>(); var statistics = new JsonArray();
        foreach (var key in Keys)
        {
            var hostBytes = File.ReadAllBytes(Path.Combine(input, $"tools-{key}.json"));
            var catalog = JsonNode.Parse(hostBytes)!;
            Require(Text(catalog["releaseKey"]) == key, "Host catalog release mismatch: " + key);
            var tools = catalog["tools"]!.AsArray();
            var hostNames = Set(tools.Select(t => Text(t!["name"])));
            Require(hostNames.Count == tools.Count && hostNames.SetEquals(data.Rosters[key]), "Built/source catalog mismatch: " + key);
            if (key is "20" or "21")
            {
                var admitted = key == "20" ? names.Except(only21) : names;
                Require(hostNames.SetEquals(admitted.Except(PortedFamilies.Withheld(data.Families, key))), "Host/manifest catalog mismatch: " + key);
            }
            var api = Path.Combine(apiRoot, ReleaseCommands.PlcWorkerApi(key).Directory);
            var xmlFiles = Directory.GetFiles(api, "Siemens.Engineering*.xml")
                .Where(p => !Path.GetFileName(p).Contains(".AddIn", StringComparison.Ordinal)).Order(PythonStringComparer.Instance).ToArray();
            Require(xmlFiles.Length > 0, "No XML: " + api);
            var (signatures, members) = DocumentedMembers(xmlFiles);
            var inventory = File.ReadAllBytes(Path.Combine(input, $"coverage-{key}.json"));
            var inventoryHash = Hash(inventory);
            var calls = CompiledMembers(JsonNode.Parse(inventory)!);
            var observed = members.Intersect(calls).ToHashSet(); var candidates = members.Except(observed).ToArray();
            var byNamespace = candidates.GroupBy(p => p.Owner.Contains('.') ? p.Owner[..p.Owner.LastIndexOf('.')] : p.Owner).Select(g => (Name: g.Key, Count: g.Count())).OrderByDescending(p => p.Count).ThenBy(p => p.Name, PythonStringComparer.Instance).Take(8);
            var hashes = new JsonObject(); foreach (var path in xmlFiles) hashes[Path.GetFileName(path)] = Hash(File.ReadAllBytes(path));
            var row = new JsonObject {
                ["releaseKey"] = key, ["mcpTools"] = tools.Count, ["xmlFiles"] = xmlFiles.Length,
                ["domainSignatures"] = signatures.Count, ["domainMemberNames"] = members.Count,
                ["compiledDirectMemberNames"] = observed.Count, ["withoutDirectMemberReference"] = candidates.Length,
                ["topCandidateNamespaces"] = new JsonArray(byNamespace.Select(p => (JsonNode)new JsonArray(p.Name, p.Count)).ToArray()),
                ["sdkXmlSha256"] = hashes, ["compiledCallInventorySha256"] = inventoryHash
            };
            if (key is "20" or "21")
            {
                row["plcCompiledDirectMemberNames"] = observed.Count;
                row["plcCompiledCallInventorySha256"] = inventoryHash;
                row["hostCatalogSha256"] = Hash(hostBytes);
            }
            statistics.Add(row);
            results[Path.Combine(output, $"api-candidates-{key}.json")] = Encode(new JsonObject { ["releaseKey"] = key, ["caveat"] = "No exact owner/member-name direct call found. Dynamic calls, inherited/generic targets and unadvertised adapter code require manual review. This is not a missing-tool count.", ["members"] = new JsonArray(candidates.OrderBy(p => p.Kind, PythonStringComparer.Instance).ThenBy(p => p.Owner, PythonStringComparer.Instance).ThenBy(p => p.Member, PythonStringComparer.Instance).Select(p => (JsonNode?)JsonValue.Create($"{p.Kind}:{p.Owner}.{p.Member}")).ToArray()) });
        }
        results["manifest/version-api-audit.json"] = Encode(new JsonObject { ["method"] = Method, ["nativeTiaExecuted"] = false, ["versions"] = statistics });
        return legacyLineEndings ? results.ToDictionary(p => p.Key, p => p.Value.Replace("\n", "\r\n", StringComparison.Ordinal)) : results;
    }
    internal static JsonObject Usage(string root, string? inputsRoot = null, string? recordsPath = null)
    {
        var input = inputsRoot ?? Path.Combine(root, "bin-build"); var matrix = Read(root, "manifest/version-tools.json")["releases"]!.AsObject();
        var source = "src/Shared/ToolUsageData.json"; var catalog = Read(root, source);
        if (recordsPath is not null)
        {
            // The remaining Python STDIO capturers emit one receipt per release.
            var receipts = Read(root, recordsPath).AsArray();
            Require(receipts.Count == Keys.Count && Set(receipts.Select(r => Text(r!["releaseKey"]))).SetEquals(Keys), "Exactly eight distinct usage receipts required");
            Require(receipts.All(r => r!["allSourceTextRetrievedAndHashChecked"]!.GetValue<bool>()), "Source text hash check required for every release");
            return UsageFromRecords(root, matrix, catalog, receipts);
        }
        var records = OneReceiptPerRelease(Read(input, "merge-check/transport/tool-usage.json").AsArray());
        // P8-04l: the V20/V21 receipts are the PLC usage jobs' (the engine resource-discovery receipts retired with the engine).
        // Their host/worker identity fields are proven by the jobs and are not part of manifest/tool-usage-coverage.json,
        // whose committed bytes check-all's PLC usage audit reproduces from the same receipts.
        foreach (var key in new[] { "20", "21" })
        {
            var receipt = Read(input, $"merge-check/usage-v{key}-plc.json").AsArray().Single()!.DeepClone().AsObject();
            foreach (var field in new[] { "hostSha256", "sdkWorkerSha256", "runtimeSha256", "workerModel" }) receipt.Remove(field);
            records.Add(receipt);
        }
        Require(records.Count == Keys.Count && Set(records.Select(row => Text(row!["releaseKey"]))).SetEquals(Keys), "Exactly eight transport usage receipts required");
        Require(records.All(row => row!["allSourceTextRetrievedAndHashChecked"]!.GetValue<bool>()), "Source text hash check required for every usage receipt");
        return UsageFromRecords(root, matrix, catalog, records);
    }
    // Test-FoundationTransport.py writes one receipt per exercised connection (stdio and two HTTP clients per release).
    // Every connection of a release must report the same receipt; the audits count one per release.
    internal static JsonArray OneReceiptPerRelease(JsonArray transportReceipts)
    {
        var records = new JsonArray();
        foreach (var connection in transportReceipts.GroupBy(row => Text(row!["releaseKey"]), StringComparer.Ordinal))
        {
            var first = connection.First()!;
            Require(connection.All(row => JsonNode.DeepEquals(row, first)), "Every transport connection must report the same usage receipt: " + connection.Key);
            records.Add(first.DeepClone());
        }
        return records;
    }
    internal static JsonObject UsageFromRecords(string root, JsonObject matrix, JsonNode catalog, JsonArray records)
    {
        var releases = new JsonObject();
        var runtime = JsonNode.Parse(XDocument.Load(Path.Combine(root, CatalogGenerator.Resource)).Root!.Elements("data")
            .Single(element => (string?)element.Attribute("name") == "Catalog").Element("value")!.Value)!;
        foreach (var record in records)
        {
            var key = Text(record!["releaseKey"]); Require(!record["exampleCallsExecuted"]!.GetValue<bool>(), "Native usage calls must not execute");
            Require(record["registeredToolCount"]!.GetValue<int>() == record["checkedToolCount"]!.GetValue<int>() && record["checkedToolCount"]!.GetValue<int>() == matrix[key]!["toolCount"]!.GetValue<int>(), "Usage count mismatch: " + key);
            Require(Set(record["tools"]!.AsArray().Select(r => Text(r!["toolName"]))).SetEquals(Strings(matrix[key]!["tools"])), "Usage roster mismatch: " + key);
            var schemas = record["inputSchemas"]?.AsObject() ?? throw new ReleaseException("Actual served input schemas required: " + key);
            Require(Set(schemas.Select(p => p.Key)).SetEquals(Strings(matrix[key]!["tools"])), "Usage schema roster mismatch: " + key);
            ValidateUsageExamples(catalog, schemas, key, record["tools"]!.AsArray()
                .ToDictionary(t => Text(t!["toolName"]), t => Text(t!["usageProfile"])),
                runtime["releases"]![key]!.AsArray().ToDictionary(row => Text(row!["currentName"]), row => Text(row!["exampleSource"])));
            var row = new JsonObject(); foreach (var p in record.AsObject().Where(p => p.Key is not "profile" and not "transport" and not "inputSchemas"))
                row[p.Key] = p.Value?.DeepClone(); releases[key] = row;
        }
        Require(Set(releases.Select(p => p.Key)).SetEquals(matrix.Select(p => p.Key)), "Usage release coverage differs"); var union = Set(matrix.SelectMany(p => Strings(p.Value!["tools"]))); Require(union.SetEquals(catalog["tools"]!.AsObject().Select(p => p.Key)), "Missing or obsolete usage mapping");
        return new JsonObject { ["basis"] = "Actual MCP tools/list and GetToolUsage responses for every tool/operation on all eight releases. Parameterized calls, input origins, result contracts, language files, sequence calls and source hashes checked. Allowlisted in-memory call examples execute on every release; target-bound native example operations NOT executed.",
            ["catalogSha256"] = Hash(Encoding.UTF8.GetBytes(Source(root, "src/Shared/ToolUsageData.json"))), ["uniqueTools"] = union.Count, ["documents"] = catalog["documents"]!.AsArray().Count, ["versionToolPairs"] = matrix.Sum(p => p.Value!["toolCount"]!.GetValue<int>()), ["offlineCallExecutions"] = releases.Sum(p => p.Value!["offlineCallExamplesExecuted"]!.AsArray().Count), ["indexedExampleBlocksOrMethods"] = catalog["documents"]!.AsArray().Sum(d => d!["examples"]!.AsArray().Count), ["programmingExamples"] = catalog["examples"]!.AsArray().Count, ["callSequences"] = catalog["sequences"]!.AsArray().Count,
            ["languages"] = new JsonArray(catalog["languages"]!.AsArray().Select(l => Copy(l!["id"]!)).ToArray()), ["relatedOfficialPatternMappings"] = catalog["tools"]!.AsObject().Count(p => Text(p.Value!["relationship"]) == "related-api-patterns"), ["noDirectVendoredExampleMappings"] = catalog["tools"]!.AsObject().Count(p => Text(p.Value!["relationship"]) == "no-direct-official-example"), ["sources"] = Copy(catalog["sources"]!), ["nativeTiaExecuted"] = false, ["releases"] = releases };
    }

    internal static void ValidateUsageExamples(JsonNode catalog, JsonObject schemas, string release, IReadOnlyDictionary<string, string>? usageProfiles = null,
            IReadOnlyDictionary<string, string>? exampleSources = null, IReadOnlyDictionary<string, JsonNode>? servedEntries = null)
    {
        const string profile = "plc-foundation";
        var problems = new List<string>();
        void Check(string name, JsonObject arguments, string location)
        {
            Require(schemas[name] is JsonObject, "Example tool is absent from the served catalog: " + location);
            var schema = new TiaMcp.Logic.V4.Inputs.InputSchema(JsonSerializer.SerializeToElement(schemas[name]));
            var error = schema.Validate(JsonSerializer.SerializeToElement(arguments), "arguments");
            error ??= TiaMcp.Logic.V4.CanonicalToolParameters.ValidateAliases(name, arguments);
            if (error != null) problems.Add(release + "/" + location + ": " + TiaMcp.Logic.V4.V4Json.Serialize(error));
        }
        JsonObject Bound(JsonNode arguments) => JsonNode.Parse(arguments.ToJsonString()
            .Replace("{release}", release, StringComparison.Ordinal).Replace("{major}", release is "14sp1" ? "14" : release is "15.1" ? "15" : release, StringComparison.Ordinal)
            .Replace("{extension}", Text(catalog["calls"]!["releaseExtensions"]![release]), StringComparison.Ordinal))!.AsObject();
        foreach (var name in schemas.Select(pair => pair.Key))
        {
            var source = exampleSources?.GetValueOrDefault(name) ?? profile;
            Require(catalog["calls"]!["profiles"]?[source]?[name] is JsonObject, "Missing served schema example: " + release + "/" + name);
        }
        foreach (var sourceProfile in catalog["calls"]!["profiles"]!.AsObject())
        foreach (var pair in sourceProfile.Value!.AsObject())
        {
            if (!schemas.ContainsKey(pair.Key)) continue; // A union profile also includes tools from other releases.
            var servedProfile = usageProfiles?.GetValueOrDefault(pair.Key);
            if (!string.IsNullOrEmpty(servedProfile)) Require(servedProfile == profile, "Usage profile differs: " + release + "/" + pair.Key);
            // Select the maintained variant by the generated tool/schema mapping, never the release family.
            if (sourceProfile.Key != (exampleSources?.GetValueOrDefault(pair.Key) ?? profile)) continue;
            var entry = pair.Value!;
            var args = Bound(entry["arguments"]!);
            var servedTemplate = servedEntries != null && servedEntries.TryGetValue(pair.Key, out var served)
                && TiaMcpServer.ModelContextProtocol.ToolMetadata.Find(pair.Key)?.Domain.StartsWith("HMI", StringComparison.Ordinal) != true
                ? served["arguments"]! : entry["arguments"]!;
            var servedArgs = Bound(TiaOpenness.Shared.ToolUsageCatalog.ServedArguments(catalog["calls"]!, pair.Key, release, servedTemplate));
            Check(pair.Key, servedArgs, "served/" + pair.Key);
            var capabilityArgs = TiaOpenness.Shared.ReleaseCapabilityQuery.WithSelectorDefaults(servedArgs, schemas[pair.Key]!["properties"]!.AsObject());
            if (TiaOpenness.Shared.ReleaseCapabilityQuery.Problem(pair.Key, release, capabilityArgs) is string reason)
            {
                // A registered tool with a blanket release refusal serves that refusal for every example.
                string? blocked = TiaMcp.Adapters.Contracts.OptionalEngineeringCapabilities.UnavailableTool(release, pair.Key);
                if (blocked == null || !reason.Contains(blocked, StringComparison.Ordinal)
                    || TiaOpenness.Shared.ToolUsageCatalog.ReleaseVariant(catalog["calls"]!, pair.Key, release) != null)
                    problems.Add(release + "/served/" + pair.Key + ": " + reason);
            }
            ToolUsageGenerator.ValidateHints(entry, schemas[pair.Key]!["properties"]!.AsObject().Select(p => p.Key), release + "/" + pair.Key);
            Check(pair.Key, args, sourceProfile.Key + "/" + pair.Key);
            void Variant(JsonNode patch, string location, bool complete = false)
            {
                foreach (var source in new[] { (args, sourceProfile.Key), (servedArgs, "served") })
                {
                    var variant = complete ? new JsonObject() : source.Item1.DeepClone().AsObject();
                    foreach (var field in Bound(patch)) variant[field.Key] = field.Value?.DeepClone();
                    Check(pair.Key, variant, source.Item2 + "/" + pair.Key + "/" + location);
                }
            }
            if (entry["execution"]?["arguments"] is JsonObject execution) Variant(execution, "execution");
            foreach (var operation in entry["operations"]?.AsObject() ?? [])
            {
                ToolUsageGenerator.ValidateHints(operation.Value!, schemas[pair.Key]!["properties"]!.AsObject().Select(p => p.Key), release + "/" + pair.Key + "/" + operation.Key);
                var mode = (string?)operation.Value!["argumentsMode"];
                Require(mode is null or "complete", "Unknown operation arguments mode: " + release + "/" + pair.Key + "/" + operation.Key);
                Variant(operation.Value!["arguments"]!, "operations/" + operation.Key, mode == "complete");
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
