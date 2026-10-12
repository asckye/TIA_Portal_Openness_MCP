using System;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;
using static TiaMcp.ReleaseTool.CatalogJson;

namespace TiaMcp.ReleaseTool;

internal static class ToolUsageGenerator
{
    internal static JsonObject Generate(string root)
    {
        var data = new CatalogData(root);
        var sources = new JsonArray(); var documents = new JsonArray();
        foreach (var (folder, prefix) in new[] { ("siemens-openness", "guides"), ("siemens-code-snippets", "snippets") })
        {
            var directory = Path.Combine(root, "reference", folder); var upstream = Read(root, "reference/" + folder + "/UPSTREAM.json").AsObject();
            var source = (JsonObject)upstream.DeepClone(); source["id"] = prefix; sources.Add(source);
            var search = prefix == "guides" ? Path.Combine(directory, "skills") : directory;
            var files = Directory.GetFiles(search, "*", SearchOption.AllDirectories).Order(Comparer<string>.Create((a, b) => ComparePath(Path.GetRelativePath(directory, a), Path.GetRelativePath(directory, b))));
            foreach (var path in files)
            {
                var extension = Path.GetExtension(path);
                if (!new[] { ".md", ".cs", ".csproj", ".props", ".targets" }.Contains(extension)) continue;
                var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                var upstreamPath = prefix == "guides" ? Text(upstream["upstreamSkillsPath"]) + "/" + Path.GetRelativePath(search, path).Replace('\\', '/') : relative;
                var text = Repository.ReadSource(path); var examples = new JsonArray();
                if (extension == ".md")
                    foreach (Match m in Regex.Matches(text, @"^```([^\n]*)\n(.*?)^```\s*$", RegexOptions.Multiline | RegexOptions.Singleline))
                        examples.Add(new JsonObject { ["line"] = text[..m.Index].Count(c => c == '\n') + 1, ["language"] = m.Groups[1].Value.Trim(), ["lineCount"] = SplitLines(m.Groups[2].Value).Length });
                else
                {
                    var lines = SplitLines(text);
                    for (var i = 0; i < lines.Length; i++)
                    {
                        var m = Regex.Match(lines[i], @"public (?:async )?(?:void|Task) (\w+)\(");
                        if (m.Success) examples.Add(new JsonObject { ["line"] = i + 1, ["symbol"] = m.Groups[1].Value });
                    }
                }
                documents.Add(new JsonObject { ["id"] = prefix + "/" + relative, ["source"] = prefix, ["url"] = Text(upstream["repository"]) + "/blob/" + Text(upstream["commit"]) + "/" + upstreamPath,
                    ["sha256"] = Hash(Encoding.UTF8.GetBytes(text)), ["lines"] = text.Split('\n').Length, ["examples"] = examples, ["text"] = text });
            }
        }
        Require(Set(data.Rosters["21"].Except(data.Registrations.Keys)).IsSubsetOf(data.Rosters["19"]), "New registrations must come from Foundation");
        var names = Set(data.Rosters.Values.SelectMany(v => v));
        var topicRules = Read(root, "reference/tool-catalog/usage-topic-rules.json");
        var manuals = Read(root, "reference/siemens-openness/manual-examples.json")["references"]!.AsArray();
        var mappings = new JsonObject();
        foreach (var name in Sorted(names))
        {
            var domain = data.Registrations.GetValueOrDefault(name).Domain ?? "";
            var topics = Strings(topicRules["domains"]![domain]);
            var defined = new[] { "Meta", "Guide", "Bootstrap", "Reports", "Exports", "PLC-Builders", "Validation", "Diagnostics", "Simulation", "Online-Monitoring" }.Contains(domain) || name == "GetToolUsage" || data.Additions.ContainsKey(name) || data.HostOnly.ContainsKey(name);
            var unsupported = new[] { "Hmi", "Unified", "Sivarc", "SiVArc" }.Any(name.Contains) || domain is "PLC-OpcUA" or "VersionControl";
            if (!defined && !unsupported)
                foreach (var rule in topicRules["rules"]!.AsArray())
                    if (Regex.IsMatch(name, Text(rule![0]))) { topics = Strings(rule[1]); break; }
            if (defined || unsupported) topics = [];
            var references = new List<string>();
            foreach (var topic in topics)
            {
                references.Add("guides/skills/" + topic + "/SKILL.md");
                foreach (var suffix in Strings(topicRules["snippet_topics"]![topic]))
                    references.AddRange(documents.Select(d => Text(d!["id"])).Where(id => id.EndsWith(suffix, StringComparison.Ordinal)));
            }
            var manualTopics = new List<string>();
            if (!defined)
            {
                if (name.Contains("Sivarc", StringComparison.Ordinal) || name.Contains("SiVArc", StringComparison.Ordinal)) manualTopics.AddRange(["sivarc", "sivarc-rules"]);
                else if (domain == "VersionControl" && !name.Contains("Git", StringComparison.Ordinal)) manualTopics.AddRange(["vci", "vci-formats"]);
                else if (domain == "PLC-OpcUA") manualTopics.Add("opcua");
                else if (domain == "HMI-Unified") { manualTopics.Add("hmi-unified"); if (name.Contains("Dynamization", StringComparison.Ordinal)) manualTopics.Add("hmi-dynamization"); if (name.Contains("Event", StringComparison.Ordinal)) manualTopics.Add("hmi-events"); if (name.Contains("Screen", StringComparison.Ordinal)) manualTopics.Add("hmi-screen"); }
                else if (domain is "HMI" or "HMI-Classic") manualTopics.Add("hmi-classic");
            }
            var record = new JsonObject {
                ["relationship"] = references.Count > 0 ? "related-api-patterns" : "no-direct-official-example",
                ["explanation"] = references.Count > 0 ? "Official topic examples, not an official implementation of this MCP wrapper. Read the full source and its setup; wrapper actions/arguments come from inputSchema." : "This project-defined/composed or uncovered API tool has no matching direct example in the pinned official sources. Use the project contract/template; do not invent an official equivalent.",
                ["documents"] = new JsonArray(references.Distinct(StringComparer.Ordinal).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                ["sourceReleaseEvidence"] = "Pinned snippet source fixtures use .zap21; upstream README still says V20. Guides are reference patterns with no per-release acceptance. V14 SP1-V19 require the selected foundation contract; no newer-API compatibility claim.",
                ["manualReferences"] = new JsonArray(manuals.Where(m => manualTopics.Contains(Text(m!["topic"]))).Select(m => m!.DeepClone()).ToArray())
            };
            if (name is "ManageStartdriveParameter" or "GetDriveParameters" or "GetOnlineDriveParameters")
                record["manualsByRelease"] = new JsonObject { ["20"] = "https://docs.tia.siemens.cloud/r/en-us/v20/functions-for-startdrive/code-examples/reading-and-writing-bico-parameters", ["21"] = "https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/code-examples/reading-and-writing-bico-parameters" };
            mappings.Add(name, record);
        }
        var ids = Set(documents.Select(d => Text(d!["id"])));
        Require(mappings.All(p => Strings(p.Value!["documents"]).All(ids.Contains)), "Unknown source document mapping");
        var library = Read(root, "reference/tool-examples/languages/catalog.json");
        var examplesList = library["examples"]!.AsArray();
        Require(examplesList.Select(e => Text(e!["id"])).Distinct().Count() == examplesList.Count, "Duplicate programming example id");
        foreach (var example in examplesList)
        {
            Require(Strings(example!["tools"]).All(names.Contains), "Programming example links to unknown tool");
            foreach (var asset in example["files"]?.AsArray() ?? []) { var content = Source(root, Text(asset!["path"])); asset["content"] = content; asset["contentSha256"] = Hash(Encoding.UTF8.GetBytes(content)); }
        }
        var meta = Read(root, "reference/tool-examples/metadata.json"); var calls = Read(root, "reference/tool-examples/calls.json"); var sequences = Read(root, "reference/tool-examples/sequences.json").AsArray();
        ValidateCoverage(data.Rosters, calls, data.Families["F00"].Tools); ValidateFoundation(calls, sequences); ValidateRetest(root, calls, sequences, meta);
        ValidateSourceHints(root, Read(root, "tests/Engine/TiaMcp.Engine.Tests/Fixtures/EngineSourceExamples.json"));
        var result = new JsonObject { ["schemaVersion"] = 2, ["scope"] = "Pinned Siemens source documents and project-authored MCP/programming examples. Per-release contracts are read from the running engine. Templates, complete sources and fragments are distinguished; native acceptance is separate.",
            ["sources"] = sources, ["documents"] = documents, ["tools"] = mappings,
            ["languages"] = Copy(library["languages"]!), ["examples"] = Copy(library["examples"]!),
            ["engineSourceCalls"] = Read(root, "tests/Engine/TiaMcp.Engine.Tests/Fixtures/EngineSourceExamples.json"), ["engineSourceSequences"] = Read(root, "tests/Engine/TiaMcp.Engine.Tests/Fixtures/EngineSourceSequences.json"),
            ["calls"] = calls, ["sequences"] = sequences };
        foreach (var p in meta.AsObject().Where(p => p.Key != "schemaVersion")) result[p.Key] = p.Value?.DeepClone();
        return result;
    }
    private static int ComparePath(string a, string b)
    {
        var x = a.Replace('\\', '/').Split('/'); var y = b.Replace('\\', '/').Split('/');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++) { var c = PythonStringComparer.Instance.Compare(x[i].ToLowerInvariant(), y[i].ToLowerInvariant()); if (c != 0) return c; }
        return x.Length.CompareTo(y.Length);
    }
    internal static void ValidateHints(JsonNode example, IEnumerable<string> parameters, string location)
    {
        var names = Set(parameters);
        foreach (var hint in example["parameters"]?.AsObject() ?? [])
            Require(names.Contains(hint.Key), "Unknown parameter hint: " + location + "/" + hint.Key);
    }
    internal static void ValidateSourceHints(string root, JsonNode examples)
    {
        var releases = Read(root, "tests/Engine/TiaMcp.Engine.Tests/FullEngineRejections.json")["releases"]!.AsObject();
        foreach (var release in releases)
            foreach (var tool in release.Value!.AsArray())
                if (examples[Text(tool!["name"])] is JsonObject example)
                    ValidateHints(example, tool["parameters"]!.AsObject().Select(p => p.Key), "engine-source/" + release.Key + "/" + Text(tool["name"]));
    }
    private static string[] SplitLines(string text) => text.Length == 0 ? [] : Regex.Split(text.TrimEnd('\n'), "\\r\\n|[\\n\\r\\v\\f\\x1c-\\x1e\\x85\\u2028\\u2029]");
    internal static void ValidateCoverage(Dictionary<string, HashSet<string>> rosters, JsonNode calls, IEnumerable<string>? sharedFoundation = null)
    {
        foreach (var profile in new[] { "full-engine", "plc-foundation" })
        {
            var expected = Set((profile == "full-engine" ? Keys.Skip(6) : Keys.Take(6)).SelectMany(k => rosters[k]));
            if (profile == "plc-foundation")
            {
                expected.UnionWith(["ExportPlcBlockDocuments", "ExportPlcBlocksDocuments", "ImportPlcBlockDocuments", "ImportPlcBlocksDocuments"]);
                expected.UnionWith(sharedFoundation ?? []);
            }
            Require(expected.SetEquals(calls["profiles"]![profile]!.AsObject().Select(p => p.Key)), "Call example coverage differs: " + profile);
        }
    }
    internal static void ValidateFoundation(JsonNode calls, JsonArray sequences)
    {
        void Check(string name, JsonNode args, string location)
        {
            if (new[] { "ExportPlcBlocks", "ExportPlcTypes", "ImportPlcBlocksFromDirectory", "ImportPlcProgramFromDirectory" }.Contains(name))
            {
                var path = Text(args["softwarePath"]); Require(path.StartsWith("devices/", StringComparison.Ordinal) || path.Contains("exact softwarePath", StringComparison.Ordinal) && path.Contains("GetProjectTree", StringComparison.Ordinal), "Batch requires exact software path: " + location);
            }
            if ((args["dryRun"]?.GetValue<bool>() == false || args["confirm"]?.GetValue<bool>() == true) && !new[] { "StageImportFiles", "CleanupStagedImportFiles", "ManageStandardPackage", "ManageMachineDescription" }.Contains(name)) Require(Text(args["expectedProjectFile"]).Length > 0, "Real call requires expectedProjectFile: " + location);
            if (name is "ImportPlcBlocksFromDirectory" or "ImportPlcProgramFromDirectory") { var pattern = Text(args["regexName"]); Require(pattern.Length > 0 && Regex.IsMatch("Main.xml", "\\A(?:" + pattern + ")\\z") && !Regex.IsMatch("Main", "\\A(?:" + pattern + ")\\z"), "Regex must select extension: " + location); }
            if (new[] { "BuildPlcTagTable", "BuildStructuredText", "BuildFlgNetCall", "BuildPlcFcBlock", "BuildPlcFbBlock", "BuildPlcLadFcBlock", "BuildPlcGlobalDb", "BuildPlcUdt" }.Contains(name)) Require(Text(args["outputReleaseKey"]) == "21", "Builder must select V21: " + location);
        }
        foreach (var (name, entry) in calls["profiles"]!["plc-foundation"]!.AsObject())
        {
            Check(name, entry!["arguments"]!, name);
            if (entry["execution"]?["arguments"] is JsonObject execution) { var a = entry["arguments"]!.DeepClone(); foreach (var p in execution) a[p.Key] = p.Value?.DeepClone(); Check(name, a, name + "/execution"); }
            foreach (var p in entry["operations"]?.AsObject() ?? []) Check(name, p.Value!["arguments"]!, name + "/" + p.Key);
        }
        foreach (var sequence in sequences.Where(s => Text(s!["profile"]) == "plc-foundation")) foreach (var step in sequence!["steps"]!.AsArray()) Check(Text(step!["tool"]), step["arguments"]!, Text(sequence["id"]));
    }
    internal static void ValidateRetest(string root, JsonNode calls, JsonArray sequences, JsonNode metadata)
    {
        string[] imports = ["ImportPlcBlock", "ImportPlcType", "ImportPlcTagTable"];
        foreach (var name in imports)
        {
            var entry = calls["profiles"]!["plc-foundation"]![name]!;
            Require(entry["arguments"]!["overwrite"]?.GetValue<bool>() == false && entry["execution"]!["arguments"]!["overwrite"]?.GetValue<bool>() == true && Text(entry["execution"]!["arguments"]!["expectedProjectFile"]).Length > 0, "Explicit bound overwrite lesson: " + name);
            var note = Text(entry["note"]);
            foreach (var token in new[] { "overwrite=false refuses replacement.", "INVALID_ARGUMENT", "parameter=overwrite", "rejected-before-operation", "not-started", "no Workbench request", "C:/Examples/exports/Tags.xml", "C:\\Examples\\exports\\Tags.xml", "normalized full path", "An existing Openness XML file is required.", "parameter=importPath" }) Require(note.Contains(token, StringComparison.Ordinal), name + ": missing " + token);
            Require(!note.Contains("Native imports do not overwrite an existing logical object.", StringComparison.Ordinal) && entry["parameters"]!["importPath"]!["requiresBinding"]!.GetValue<bool>(), "Import parameter lesson: " + name);
            Require(JsonNode.DeepEquals(entry, calls["profiles"]!["full-engine"]![name]), "Shared V20/V21 examples differ: " + name);
        }
        var indexed = sequences.ToDictionary(s => Text(s!["id"]), s => s!);
        foreach (var topic in new[] { "foundation-approval-precheck", "foundation-tag-table-round-trip", "foundation-block-round-trip", "plc-xml-round-trip" })
        {
            var sequence = indexed["sequence/" + topic]; var full = topic == "plc-xml-round-trip"; var keys = full ? Keys.Skip(6).ToArray() : Keys;
            Require(Strings(sequence["releaseKeys"]).SequenceEqual(keys) && Text(sequence["profile"]) == (full ? "full-engine" : "plc-foundation") && Text(sequence["validation"]).Contains("native", StringComparison.Ordinal) && Text(sequence["validation"]).Contains("NOT RUN", StringComparison.Ordinal), "Sequence release/acceptance lesson: " + topic);
            foreach (var key in keys)
            {
                var tools = Read(root, $"manifest/contracts/v4/baseline/{key}.json")["tools"]!.AsArray().ToDictionary(t => Text(t!["name"]), t => t!["inputSchema"]!);
                foreach (var step in sequence["steps"]!.AsArray()) { var schema = tools[Text(step!["tool"])]; var a = step["arguments"]!.AsObject(); Require(Strings(schema["required"]).All(a.ContainsKey) && a.All(p => schema["properties"]!.AsObject().ContainsKey(p.Key)), "Sequence schema mismatch: " + topic); if (!full && a["dryRun"]?.GetValue<bool>() == false) Require(a["confirm"]?.GetValue<bool>() == true && Text(a["expectedProjectFile"]).Length > 0, "Sequence identity lesson: " + topic); }
            }
        }
        var table = indexed["sequence/foundation-tag-table-round-trip"]; var block = indexed["sequence/foundation-block-round-trip"];
        var tableNames = table["steps"]!.AsArray().Select(s => Text(s!["tool"])).ToList(); var blockNames = block["steps"]!.AsArray().Select(s => Text(s!["tool"])).ToList();
        Require(tableNames.IndexOf("ExportPlcTagTable") < tableNames.IndexOf("CreatePlcTag") && tableNames.IndexOf("CreatePlcTag") < tableNames.IndexOf("ImportPlcTagTable") && tableNames[^1] == "ListPlcTags" && Text(table["steps"]!.AsArray().Last()!["expect"]).Contains("tag added after export is gone", StringComparison.Ordinal), "Table round-trip readback lesson");
        Require(table["steps"]!.AsArray().Any(s => Text(s!["tool"]) == "ExportPlcTagTable" && s["arguments"]!["dryRun"]?.GetValue<bool>() == false && Text(s["expect"]).Contains("Edit this exported file", StringComparison.Ordinal)), "Table exported edit lesson");
        Require(blockNames.IndexOf("ExportPlcBlock") < blockNames.IndexOf("ImportPlcBlock") && blockNames.IndexOf("ImportPlcBlock") < blockNames.IndexOf("CompilePlcSoftware") && blockNames[^1] == "CompilePlcSoftware" && block["steps"]!.AsArray().Last()!["arguments"]!["dryRun"]?.GetValue<bool>() == false, "Block compile lesson");
        Require(Text(block["preconditions"]).Contains("remove or move an old Main.xml", StringComparison.Ordinal) && Text(block["notes"]).Contains("Exports need no Workbench approval", StringComparison.Ordinal), "Block precondition/approval lesson");
        foreach (var sequence in new[] { table, block }) foreach (var step in sequence["steps"]!.AsArray().Where(s => imports.Contains(Text(s!["tool"])))) Require(step!["arguments"]!["overwrite"]?.GetValue<bool>() == true, "Round-trip replacement lesson");
        var refusal = indexed["sequence/foundation-approval-precheck"]; Require(Set(refusal["steps"]!.AsArray().Select(s => Text(s!["tool"]))).IsSupersetOf(imports.Concat(new[] { "CreatePlcTag", "CreatePlcTagTable", "CloseProject" })), "Precheck refusal coverage");
        foreach (var step in refusal["steps"]!.AsArray().Skip(1)) foreach (var token in new[] { "no Workbench request", "rejected-before-operation", "not-started" }) Require(Text(step!["expect"]).Contains(token, StringComparison.Ordinal), "Precheck refusal lesson");
        var approval = string.Join(' ', Strings(metadata["resultReading"]!["ResponseMessage"]));
        foreach (var token in new[] { "0.1-6.5 seconds", "need no approval click", "PRECONDITION_FAILED", "parameter=softwarePath", "parameter=name", "parameter=overwrite" }) Require(approval.Contains(token, StringComparison.Ordinal), "Approval result lesson: " + token);
    }
}
