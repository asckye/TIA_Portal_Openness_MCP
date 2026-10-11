using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;
using static TiaMcp.ReleaseTool.CatalogJson;

namespace TiaMcp.ReleaseTool;

internal static class CatalogGenerator
{
    internal const string Resource = "src/Logic/ModelContextProtocol/ToolProfiles.resx";
    internal const string Rejections = "tests/Engine/TiaMcp.Engine.Tests/FullEngineRejections.json";
    internal static Dictionary<string, string> Generate(string root, bool legacyNames = false, Action<Dictionary<string, string>>? prepareSources = null)
    {
        var data = new CatalogData(root); var tools = data.Archives.ToDictionary(p => p.Key, p => (JsonObject)p.Value.DeepClone());
        foreach (var (name, entry) in data.Additions)
            if (!data.HostOnly.ContainsKey(name)) foreach (var key in Strings(entry!["releases"])) tools[key][name] = new JsonObject { ["name"] = name, ["inputSchema"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("inputPath", "outputPath"), ["properties"] = new JsonObject { ["inputPath"] = new JsonObject { ["type"] = "string" }, ["outputPath"] = new JsonObject { ["type"] = "string" } } } };
        foreach (var key in Keys.Take(6)) foreach (var current in data.Reviewed[key]) { var old = tools["21"].Select(p => p.Key).First(n => data.Renames[n] == current); tools[key][old] = Copy(tools["21"][old]!); }
        var currentNames = data.Mapping;
        // A tool without any supported action on a release is not listed there, even if the 3.x archive had it
        // (maintainer 2026-10-11: one registration rule for all eight releases).
        foreach (var key in Keys)
        {
            var withheld = PortedFamilies.Withheld(data.Families, key);
            foreach (var old in tools[key].Select(p => p.Key).Where(n => currentNames.TryGetValue(n, out var current) && withheld.Contains(current)).ToList()) tools[key].Remove(old);
        }
        var only21 = Set(Matches(Source(root, "src/Logic/Siemens/ToolVersionPolicy.cs").Split("internal static string ToolProblem", 2)[0], "\\[\"([^\"]+)\"\\]\\s*="));
        Require(Set(tools["20"].Select(p => p.Key)).SetEquals(currentNames.Keys.Where(n => !only21.Contains(n) && !only21.Contains(currentNames[n]))), "V20 admission policy differs");
        Require(Set(Strings(Read(root, "manifest/history/contracts-v3/baseline/20.json")["liteTools"])).SetEquals(Strings(Read(root, "manifest/history/contracts-v3/baseline/21.json")["liteTools"])), "Frozen engine lite rosters differ");
        Require(Source(root, "src/Shared/ToolUsageCatalog.cs").Contains("GuideSelection(string topic)", StringComparison.Ordinal) && Source(root, "src/Logic/ModelContextProtocol/ToolRecipes.cs").Contains("ToolUsageCatalog.Sequences(", StringComparison.Ordinal) && Source(root, "src/Engine/ModelContextProtocol/Tools/ToolUsageTools.cs").Contains("ToolUsageCatalog.Examples(", StringComparison.Ordinal) && Source(root, "src/Engine/ModelContextProtocol/Tools/ToolUsageTools.cs").Contains("exampleId", StringComparison.Ordinal), "Guide merge proof differs");
        var calls = Read(root, "reference/tool-examples/calls.json")["profiles"]!; var sourceExamples = Read(root, "tests/Engine/TiaMcp.Engine.Tests/Fixtures/EngineSourceExamples.json");
        foreach (var key in Keys) Require(data.Rosters[key].All(calls[key is "20" or "21" ? "full-engine" : "plc-foundation"]!.AsObject().ContainsKey), "Missing call examples: " + key);
        var foundation = string.Join('\n', Directory.GetFiles(Path.Combine(root, "src/FoundationHost"), "*.cs").Order(StringComparer.Ordinal).Select(Repository.ReadSource));
        var foundationCurrent = Keys.Take(6).ToDictionary(k => k, k => CatalogData.Resolve(tools[k].Select(p => p.Key), Set(data.Rosters[k].Except(data.HostOnly.Keys)), data.Renames));
        foreach (var key in Keys.Take(6)) foreach (var (old, row) in tools[key])
        {
            if (data.Ported[key].Contains(foundationCurrent[key][old])) continue;
            Require(foundation.Contains('"' + foundationCurrent[key][old] + '"', StringComparison.Ordinal), "Missing Foundation registration: " + old);
            foreach (var p in row!["inputSchema"]!["properties"]!.AsObject()) Require(foundation.Contains('"' + p.Key + '"', StringComparison.Ordinal), "Missing Foundation parameter: " + old + "/" + p.Key);
        }
        var signatures = currentNames.Where(p => !CatalogData.Merged.Contains(p.Key)).ToDictionary(p => p.Key, p => Signature(data.Engine, data.Registrations[p.Value].Method));
        var typed = new Dictionary<string, Dictionary<string, HashSet<string>>>();
        foreach (var key in Keys) foreach (var (name, row) in tools[key]) foreach (var p in row!["inputSchema"]!["properties"]!.AsObject())
            if (p.Key.EndsWith("Json", StringComparison.Ordinal) || name == "PlcBuildAndImport" && p.Key == "json") { if (!typed.TryGetValue(name, out var entry)) typed[name] = entry = []; if (!entry.TryGetValue(p.Key, out var supported)) entry[p.Key] = supported = []; supported.Add(key); }
        var shapes = Read(root, "reference/tool-catalog/parameter-shapes.json");
        foreach (var (old, signature) in signatures)
        {
            Require(Regex.IsMatch(signature, @"\b(?:CallToolResult|Task<CallToolResult>)\s+\w+\s*\("), "Full-engine entry must return a V4 envelope: " + old);
            var actual = Parameters(signature).ToDictionary(p => p.Key, p => Text(p.Value!["type"]));
            var expected = typed.GetValueOrDefault(old, []).Where(p => p.Value.Contains("21")).Select(p => p.Key).ToArray();
            ValidateTransition(old, currentNames[old], data.Renames[old], actual, expected.ToDictionary(p => p, p => Shape(shapes, old, p)));
            if (data.Additions.ContainsKey(old))
            {
                var wanted = old switch { "StageImportFiles" => new Dictionary<string, string> { ["files"] = "StagedTextFile[]", ["dryRun"] = "bool" }, "ListStagedImportFiles" => [], "CleanupStagedImportFiles" => new() { ["batchId"] = "string", ["dryRun"] = "bool" }, _ => new() { ["inputPath"] = "string", ["outputPath"] = "string" } };
                Require(actual.Count == wanted.Count && actual.All(p => wanted.GetValueOrDefault(p.Key) == p.Value), "New V4 contract differs: " + old);
            }
        }
        if (foundation.Contains("new FoundationV4Tool(", StringComparison.Ordinal))
        {
            var adapter = Source(root, "src/FoundationHost/FoundationV4Tool.cs");
            foreach (var key in Keys.Take(6)) foreach (var (old, row) in tools[key])
            {
                if (data.Ported[key].Contains(foundationCurrent[key][old])) continue;
                Require(foundationCurrent[key][old] == data.Renames[old], "Foundation target mismatch: " + old);
                foreach (var p in typed.GetValueOrDefault(old, []).Where(p => p.Value.Contains(key)))
                {
                    var wanted = Regex.Split(Shape(shapes, old, p.Key), @"[({（]")[0]; var element = wanted.EndsWith("[]", StringComparison.Ordinal) ? wanted[..^2] : wanted;
                    Require(Regex.IsMatch(adapter, "<" + Regex.Escape(wanted) + ">|<" + Regex.Escape(element) + ">") && adapter.Contains('"' + p.Key[..^4] + '"', StringComparison.Ordinal), "Foundation typed transition differs: " + old + "/" + p.Key);
                }
            }
        }
        var liteGroups = Read(root, "reference/tool-catalog/lite-groups.json").AsObject(); var lite = new Dictionary<string, HashSet<string>>();
        foreach (var key in Keys.Skip(6))
        {
            lite[key] = [];
            foreach (var p in liteGroups) foreach (var target in Text(p.Value).Split(' '))
            {
                var old = tools[key].Select(p => p.Key).First(n => data.Renames[n] == target && !CatalogData.Merged.Contains(n)); var current = currentNames[old];
                if (!calls["full-engine"]!.AsObject().ContainsKey(current)) continue;
                Require(calls["full-engine"]![current]!["arguments"] is JsonObject && lite[key].Add(target), "Duplicate/missing lite example: " + target);
            }
            Require(lite[key].Count is >= 50 and <= 65, "Lite proposal count differs");
        }
        Require(lite["20"].SetEquals(lite["21"]), "Engine lite proposals differ");
        var ledger = Source(root, "docs/reference/real-machine-ledger.md"); var policies = BehaviorRecords(ledger); var entries = new JsonArray();
        var files = Repository.GitFiles(root, "src/Engine", "src/Shared").Where(p => p.EndsWith(".cs", StringComparison.Ordinal) && File.Exists(Path.Combine(root, p)));
        var candidateSource = string.Join('\n', files.Select(p => Source(root, p)));
        var candidates = Regex.Matches(candidateSource, "\\[BehaviorCandidate\\(\"([^\"\\n]+)\",\\s*\"([^\"\\n]+)\"").Select(m => (Entry: m.Groups[1].Value, Family: m.Groups[2].Value)).Distinct().OrderBy(p => p.Entry, PythonStringComparer.Instance).ThenBy(p => p.Family, PythonStringComparer.Instance);
        var behaviorExamples = Read(root, "reference/tool-catalog/behavior-examples.json");
        JsonNode Example(string key, string entry, string family) => Copy(behaviorExamples[key]![entry + "/" + family] ?? throw new ReleaseException("Missing reviewed candidate example: " + entry + "/" + family));
        foreach (var key in Keys) foreach (var candidate in candidates)
            if (tools[key].Any(p => data.Renames[p.Key] == candidate.Entry) && (candidate.Family != "P6-FALLBACK" || key is "20" or "21")) entries.Add(new JsonObject { ["releaseKey"] = key, ["entry"] = candidate.Entry, ["family"] = candidate.Family, ["example"] = Example(key, candidate.Entry, candidate.Family) });
        foreach (var key in Keys.Take(6)) foreach (var (family, record) in data.Families)
        {
            if (!record.Releases.Contains(key)) continue;
            policies.Add(new JsonObject { ["releaseKey"] = key, ["family"] = family, ["state"] = "current", ["l5"] = "NOT RUN" });
            foreach (var name in record.Tools.Where(data.Reviewed[key].Contains)) entries.Add(new JsonObject { ["releaseKey"] = key, ["entry"] = name, ["family"] = family, ["example"] = Copy(calls["plc-foundation"]![name]!["arguments"]!) });
        }
        var releases = new JsonObject(); var engineReleases = new JsonObject();
        foreach (var key in Keys.Skip(6).Concat(Keys.Take(6)))
        {
            var rows = new List<JsonNode>();
            foreach (var old in key is "20" or "21" ? tools[key].Select(p => p.Key) : Sorted(tools[key].Select(p => p.Key)))
            {
                if (CatalogData.Merged.Contains(old)) continue;
                var current = key is "20" or "21" ? currentNames[old] : foundationCurrent[key][old]; var target = data.Renames[old];
                rows.Add(new JsonObject { ["name"] = target, ["currentName"] = current, ["sourceName"] = old, ["profiles"] = key is "20" or "21" ? (lite[key].Contains(target) ? new JsonArray("full", "lite") : new JsonArray("full")) : new JsonArray("plc-foundation"), ["arguments"] = Copy(key is "20" or "21" ? sourceExamples[current]!["arguments"]! : calls["plc-foundation"]![current]!["arguments"]!), ["envelopeVersion"] = 4 });
            }
            releases[key] = new JsonArray((key is "20" or "21" ? rows.OrderBy(r => Text(r["name"]), PythonStringComparer.Instance) : rows.AsEnumerable()).ToArray());
            if (key is "20" or "21") engineReleases[key] = Copy(releases[key]!);
        }
        var shared = Set(tools["19"].Select(p => data.Renames[p.Key]).Except(data.Ported["19"]));
        // These existing Foundation implementations retain their modern profiles
        // even when the reviewed table withholds them from the older hosts.
        shared.UnionWith(data.Families["F00"].Tools);
        foreach (var key in Keys.Skip(6))
        {
            var rows = releases[key]!.AsArray().Where(r => !data.Removed.Contains(Text(r!["currentName"]))).ToDictionary(r => Text(r!["currentName"]), r => r!.DeepClone());
            foreach (var entry in releases["19"]!.AsArray()) { var current = Text(entry!["currentName"]); if (data.Ported["19"].Contains(current)) continue; var row = entry.DeepClone(); row["profiles"] = new JsonArray("full", "lite", "plc-foundation"); rows[current] = row; }
            foreach (var (current, entry) in calls["plc-foundation"]!.AsObject())
                if (!shared.Contains(current) && (!data.Ported[key].Contains(current) || data.FoundationCandidates.Contains(current))) rows[current] = new JsonObject { ["name"] = current, ["currentName"] = current, ["sourceName"] = current, ["profiles"] = new JsonArray("full", "plc-foundation"), ["arguments"] = Copy(entry!["arguments"]!), ["envelopeVersion"] = 4 };
            foreach (var name in data.Families["F00"].Tools) rows[name]["profiles"] = new JsonArray("full", "lite", "plc-foundation");
            foreach (var row in rows.Values.Where(r => !shared.Contains(Text(r["currentName"])))) row["profiles"] = data.Bridge.Contains(Text(row["currentName"])) ? new JsonArray("full", "lite") : new JsonArray(Strings(row["profiles"]).Where(p => p != "lite").Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            releases[key] = new JsonArray(rows.Values.OrderBy(r => Text(r["name"]), PythonStringComparer.Instance).ToArray());
            for (var i = entries.Count - 1; i >= 0; i--) if (Text(entries[i]!["releaseKey"]) == key && data.Removed.Contains(Text(entries[i]!["entry"]))) entries.RemoveAt(i);
            foreach (var entry in entries.Where(e => Text(e!["releaseKey"]) == "19").ToArray())
                if (Text(entry!["family"]) != "F00" && shared.Contains(Text(entry["entry"])) && !entries.Any(e => Text(e!["releaseKey"]) == key && Text(e["entry"]) == Text(entry["entry"]))) { var row = entry.DeepClone(); row["releaseKey"] = key; row["example"] = Example(key, Text(entry["entry"]), Text(entry["family"])); entries.Add(row); }
        }
        foreach (var key in Keys)
        {
            var profile = key is "20" or "21" ? "full-engine" : "plc-foundation";
            var rows = releases[key]!.AsArray().ToDictionary(r => Text(r!["currentName"]), r => r!.DeepClone());
            foreach (var name in Sorted(data.HostOnly.Keys)) rows[name] = new JsonObject { ["name"] = name, ["currentName"] = name, ["sourceName"] = name,
                ["profiles"] = key is "20" or "21" ? (data.Additions.ContainsKey(name) && name is not "DescribeStandardPackage" and not "PlanProjectGeneration" and not "ApplyGenerationPlan" ? new JsonArray("full", "plc-foundation") : new JsonArray("full", "lite", "plc-foundation")) : new JsonArray("plc-foundation"), ["arguments"] = Copy(calls[profile]![name]!["arguments"]!), ["envelopeVersion"] = 4 };
            releases[key] = new JsonArray((key is "20" or "21" ? rows.Values.OrderBy(r => Text(r["name"]), PythonStringComparer.Instance) : rows.Values.OrderBy(r => data.HostOnly.ContainsKey(Text(r["currentName"])) && !data.Additions.ContainsKey(Text(r["currentName"]))).ThenBy(r => Text(r["sourceName"]), PythonStringComparer.Instance)).ToArray());
            Require(Set(releases[key]!.AsArray().Select(r => Text(r!["currentName"]))).SetEquals(data.Rosters[key]), "Runtime roster differs: " + key);
            Require(releases[key]!.AsArray().Where(r => Strings(r!["profiles"]).Contains("lite")).All(r => r!["arguments"] is JsonObject), "Missing lite call arguments: " + key);
        }
        var runtime = new JsonObject { ["schemaVersion"] = 1, ["contractVersion"] = 4, ["foundationLite"] = true, ["behaviorPolicies"] = policies, ["behaviorEntries"] = entries, ["releases"] = releases, ["engineSourceReleases"] = engineReleases };
        var rejections = new JsonObject { ["contractVersion"] = 4, ["releases"] = new JsonObject() };
        var defaultSources = string.Join('\n', Repository.GitFiles(root, "src/Engine", "src/Logic").Where(p => p.EndsWith(".cs", StringComparison.Ordinal) && File.Exists(Path.Combine(root, p))).Select(p => Source(root, p)));
        foreach (var key in Keys.Skip(6))
        {
            var rows = new JsonArray(); var typedCount = 0;
            foreach (var row in engineReleases[key]!.AsArray())
            {
                var old = Text(row!["sourceName"]); var declarations = Parameters(signatures[old]); var source = Source(root, data.Registrations[currentNames[old]].Path);
                var aliases = Regex.Matches(source, @"^using (\w+) = ([\w.]+);", RegexOptions.Multiline).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
                foreach (var declaration in declarations.Select(p => p.Value!)) { declaration["type"] = Regex.Replace(Text(declaration["type"]), @"\b\w+\b", m => aliases.GetValueOrDefault(m.Value, m.Value)); if (declaration["default"] is not null) declaration["value"] = DefaultValue(Text(declaration["default"]), defaultSources); }
                var typedParameters = new JsonObject(); foreach (var p in Sorted(typed.GetValueOrDefault(old, []).Where(p => p.Value.Contains(key)).Select(p => p.Key))) typedParameters[p] = p == "json" ? "spec" : p[..^4];
                typedCount += typedParameters.Count;
                rows.Add(new JsonObject { ["name"] = Copy(row["currentName"]!), ["parameters"] = declarations, ["arguments"] = Copy(row["arguments"]!), ["renamedFrom"] = new JsonArray(Sorted(tools[key].Select(p => p.Key).Where(n => data.Renames[n] == Text(row["name"]) && n != Text(row["currentName"]))).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()), ["typedParameters"] = typedParameters });
            }
            Require(typedCount == typed.Where(p => tools[key].ContainsKey(p.Key)).Sum(p => p.Value.Count(p => p.Value.Contains(key))), "Typed rejection coverage differs");
            rejections["releases"]![key] = rows;
        }
        var outputs = VersionOutputs(data, legacyNames); outputs[Resource] = ResourceText(runtime); outputs[Rejections] = Encode(rejections);
        // A new host tool needs these source catalogs to compile the tools-list capturer.
        // Publish that validated preparation first; the unchanged roster/count gate still runs below.
        prepareSources?.Invoke(outputs);
        var package = Read(root, "manifest/package-manifest.json"); var original = Copy(package); var roster = Read(root, "manifest/tools-list.json");
        Require(Set(roster["tools"]!.AsArray().Select(t => Text(t!["name"]))).SetEquals(data.Rosters["21"]), "Regenerate tools-list before package counts");
        package["entrypoints"]!["mcpServerExe"] = "runtime/v21/TiaMcp.FoundationHost.exe"; package["entrypoints"]!["configurator"] = "TiaOpenness.exe";
        package["cli"]!["exe"] = "runtime/v21/worker/TiaMcp.Engine.V21.exe"; package["cli"]!["description"] = "Call runtime/v21/worker/TiaMcp.Engine.V21.exe or runtime/v20/worker/TiaMcp.Engine.V20.exe with a CLI verb. JSON/YAML generation requires no MCP client.";
        package["capabilities"]!["mcpToolCount"] = roster["tools"]!.AsArray().Count; var layers = new JsonObject(); foreach (var tool in roster["tools"]!.AsArray()) { var layer = Text(tool!["layer"]); layers[layer] = (layers[layer]?.GetValue<int>() ?? 0) + 1; } package["capabilities"]!["mcpToolLayers"] = layers;
        package["capabilities"]!["liteProfile"]!["toolCount"] = releases["21"]!.AsArray().Count(r => Strings(r!["profiles"]).Contains("lite"));
        // Release records own the surrounding package formatting; retain it only when the derived fields agree.
        outputs["manifest/package-manifest.json"] = JsonNode.DeepEquals(original, package) ? Source(root, "manifest/package-manifest.json") : Encode(package);
        var capabilities = new JsonObject(); foreach (var key in Keys) { var rows = new JsonArray(); foreach (var policy in policies.Where(p => Text(p!["releaseKey"]) == key)) rows.Add(new JsonObject { ["family"] = Copy(policy!["family"]!), ["state"] = Copy(policy["state"]!), ["l5"] = Copy(policy["l5"]!), ["entries"] = new JsonArray(Sorted(entries.Where(e => Text(e!["releaseKey"]) == key && Text(e["family"]) == Text(policy["family"])).Select(e => Text(e!["entry"])).Distinct()).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()) }); capabilities[key] = rows; }
        var matrix = Read(root, "reference/version-feature-matrix.json"); matrix["behaviorCapabilities"] = capabilities.DeepClone(); outputs["reference/version-feature-matrix.json"] = Encode(matrix);
        var lines = new List<string> { "逐版行为能力由 `" + (legacyNames ? "Generate-Phase6Plan.py" : "generate-catalogs") + "` 从上面的 L5 台账与实际入口目录生成。空入口数组表示该宿主无此族 MCP 入口；不授予原生验收。", "", "| releaseKey | family | state | L5 | entries |", "|---|---|---|---|---|" };
        foreach (var (key, rows) in capabilities) foreach (var row in rows!.AsArray()) lines.Add("| " + string.Join(" | ", key, Text(row!["family"]), Text(row["state"]), Text(row["l5"]), row["entries"]!.AsArray().Count > 0 ? string.Join(", ", Strings(row["entries"])) : "—") + " |");
        const string begin = "<!-- behavior-capabilities:start -->", end = "<!-- behavior-capabilities:end -->";
        var block = begin + "\n\n" + string.Join('\n', lines) + "\n\n" + end;
        if (ledger.Contains(begin, StringComparison.Ordinal)) { var start = ledger.IndexOf(begin, StringComparison.Ordinal); var finish = ledger.IndexOf(end, start, StringComparison.Ordinal) + end.Length; outputs["docs/reference/real-machine-ledger.md"] = ledger[..start] + block + ledger[finish..]; }
        else { var finish = ledger.IndexOf('\n', ledger.IndexOf("| P6-PRODUCT ", StringComparison.Ordinal)); outputs["docs/reference/real-machine-ledger.md"] = ledger[..finish] + "\n\n" + block + ledger[finish..]; }
        return outputs;
    }
    internal static string Signature(EngineSources engine, string method)
    {
        var member = engine.Member(method, tool: true); var (tokens, _) = new CSharpLexer(member).Scan(); var pairs = MatchingPairs.Find(tokens);
        var opening = Enumerable.Range(1, tokens.Count - 1).First(i => tokens[i].Value == "(" && tokens[i - 1].Value == method); return member[..tokens[pairs[opening]].OffsetEnd];
    }
    internal static JsonObject Parameters(string signature)
    {
        var (tokens, _) = new CSharpLexer(signature).Scan(); var pairs = MatchingPairs.Find(tokens); var opening = Enumerable.Range(0, tokens.Count).First(i => tokens[i].Value == "(");
        var result = new JsonObject(); var part = new List<string>(); string? description = null;
        void Add()
        {
            if (part.Count == 0) return; var eq = part.IndexOf("="); var declaration = eq < 0 ? part : part.Take(eq).ToList();
            var row = new JsonObject { ["type"] = string.Concat(declaration.SkipLast(1)), ["default"] = eq < 0 ? null : string.Concat(part.Skip(eq + 1)) }; if (description != null) row["description"] = description;
            result.Add(declaration[^1], row); part.Clear(); description = null;
        }
        for (var i = opening + 1; i < pairs[opening]; i++)
        {
            var token = tokens[i];
            if (token.Value == "[" && part.Count == 0) { var attribute = signature[token.OffsetStart..tokens[pairs[i]].OffsetEnd]; var match = Regex.Match(attribute, "\\bDescription\\((\"(?:\\\\.|[^\"\\\\])*\")\\)"); if (match.Success) description = Text(JsonNode.Parse(match.Groups[1].Value)); i = pairs[i]; continue; }
            if (token.Value is "(" or "[") { part.AddRange(tokens.Skip(i).Take(pairs[i] - i + 1).Select(t => t.Value)); i = pairs[i]; continue; }
            if (token.Value == "," && part.Count(v => v == "<") == part.Count(v => v == ">")) Add(); else part.Add(token.Value);
        }
        Add(); return result;
    }
    internal static void ValidateTransition(string name, string current, string target, Dictionary<string, string> actual, Dictionary<string, string> expected)
    {
        Require(current == target, "V4 tool has a legacy name: " + name);
        if (name == "ManageSafetyFunction") Require(actual.GetValueOrDefault("signals") == "string[]", "setTrace signals must be a separate typed array");
        var aliases = new Dictionary<string, string[]> { ["map<string,string>"] = ["AttributeMap<string>", "Dictionary<string,string>"], ["map<string,int32>"] = ["AttributeMap<int>", "Dictionary<string,int>"], ["map<string,bool>"] = ["AttributeMap<bool>", "Dictionary<string,bool>"], ["map<string,SivarcReference|null>"] = ["AttributeMap<SivarcReference?>", "Dictionary<string,SivarcReference?>"], ["int32[]"] = ["int[]"], ["PlcArtifactSpec"] = ["ConstructionSpec"] };
        foreach (var (old, shape) in expected) { var next = old == "json" ? "spec" : old[..^4]; Require(!actual.ContainsKey(old) && actual.ContainsKey(next), "Missing typed parameter: " + name + "/" + old); var wanted = Regex.Split(shape, @"[({（]")[0]; Require(aliases.GetValueOrDefault(wanted, []).Append(wanted).Contains(actual[next].TrimEnd('?')), "Wrong typed parameter: " + name + "/" + next); }
    }
    private static string Shape(JsonNode shapes, string name, string parameter)
    {
        var exact = shapes["exactShapes"]![name + "/" + parameter]; if (exact != null) return Text(exact);
        if (parameter == "designJson") return name.Contains("Classic", StringComparison.Ordinal) ? "ClassicScreenSpec" : "UnifiedScreenSpec";
        if (parameter == "packageJson") return "ClassicPackageSpec"; if (parameter == "tableJson") return "ClassicTagTableSpec";
        var family = parameter == "json" ? "B" : name == "RunPlcCompanionTool" && parameter == "argumentsJson" || parameter == "harmonizeOptionsJson" ? "S" : name == "PatchPlcBlockDocument" && parameter == "changesJson" ? "D" : shapes["families"]!.AsObject().First(p => Text(p.Value).Split(' ').Select(s => s + "Json").Contains(parameter)).Key;
        var result = Text(shapes["parameterShapes"]![parameter] ?? shapes["shapes"]![family]); Require(result is not "BuilderSpec" and not "HmiDesign" and not "DomainSpec" and not "DomainSelection", "Missing reviewed parameter shape: " + name + "/" + parameter); return result;
    }
    private static JsonNode? DefaultValue(string value, string sources)
    {
        if (value is "null!" or "default") return null;
        try { return JsonNode.Parse(value); } catch (System.Text.Json.JsonException) { }
        var symbol = value.Split('.').Last(); var constants = Matches(sources, "\\bconst\\s+\\w+\\s+" + Regex.Escape(symbol) + "\\s*=\\s*(@\"(?:[^\"]|\"\")*\"|\"(?:\\\\.|[^\"\\\\])*\")\\s*;").Distinct().ToArray(); Require(constants.Length == 1, "Ambiguous or missing default constant: " + value); return constants[0].StartsWith("@\"", StringComparison.Ordinal) ? JsonValue.Create(constants[0][2..^1].Replace("\"\"", "\"", StringComparison.Ordinal)) : JsonNode.Parse(constants[0]);
    }
    internal static JsonArray BehaviorRecords(string ledger)
    {
        var result = new JsonArray(); foreach (var family in "DEVICE IMPORT EXPORT SESSION CLOSE SOURCE COMPILE FALLBACK".Split(' '))
        {
            var rows = Regex.Matches(ledger, "^\\| P6-" + family + " .*", RegexOptions.Multiline); Require(rows.Count == 1, "Missing or duplicate L5 family: " + family);
            var decisions = Regex.Matches(rows[0].Value, @"L5\[([^\]]+)\]\s*=\s*(PASSED|NOT RUN|FAILED)\b"); var scoped = new Dictionary<string, string>();
            foreach (Match m in decisions) Require(Keys.Contains(m.Groups[1].Value) && scoped.TryAdd(m.Groups[1].Value, m.Groups[2].Value), "Duplicate or unknown L5 release");
            foreach (var key in Keys) { var status = scoped.GetValueOrDefault(key, "NOT RUN"); result.Add(new JsonObject { ["family"] = "P6-" + family, ["releaseKey"] = key, ["state"] = status == "PASSED" ? "safe-v4" : "current", ["l5"] = status }); }
        }
        return result;
    }
    internal static string ResourceText(JsonNode runtime)
    {
        var headers = new[] { ("resmimetype", "text/microsoft-resx"), ("version", "2.0"), ("reader", "System.Resources.ResXResourceReader, System.Windows.Forms"), ("writer", "System.Resources.ResXResourceWriter, System.Windows.Forms") };
        // ElementTree escapes text only, retains insertion order and adds no JSON indentation.
        var output = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n";
        foreach (var (name, value) in headers) output += $"  <resheader name=\"{name}\">\n    <value>{value}</value>\n  </resheader>\n";
        return output + "  <data name=\"Catalog\" xml:space=\"preserve\">\n    <value>" + PythonJson.Dumps(runtime, ensureAscii: false, indent: 2).Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal) + "</value>\n  </data>\n</root>\n";
    }
    internal static Dictionary<string, string> VersionOutputs(CatalogData data, bool legacyNames = false)
    {
        var groups = new JsonArray(); var counts = new JsonObject(); var releases = new JsonObject();
        foreach (var name in Sorted(data.Rosters.Values.SelectMany(r => r).Distinct())) { var keys = Keys.Where(k => data.Rosters[k].Contains(name)).ToArray(); var group = keys.Length == 8 ? "all-releases" : keys.Length == 1 ? "release-only" : "shared-subset"; counts[group] = (counts[group]?.GetValue<int>() ?? 0) + 1; groups.Add(new JsonObject { ["name"] = name, ["releases"] = new JsonArray(keys.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()), ["group"] = group, ["contract"] = "consult selected host schema; equal names do not guarantee equal contracts" }); }
        foreach (var key in Keys) releases[key] = new JsonObject { ["profile"] = key is "20" or "21" ? "full-engine" : "plc-foundation", ["toolCount"] = data.Rosters[key].Count, ["tools"] = new JsonArray(Sorted(data.Rosters[key]).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()) };
        var record = new JsonObject { ["schemaVersion"] = 1, ["basis"] = "registered Foundation definitions and full-engine ToolCatalog; verified by eight exact contract captures", ["nativeAcceptance"] = "NOT RUN for the newly enabled targets", ["releases"] = releases, ["groupCounts"] = counts, ["tools"] = groups };
        var lines = new List<string> { "# Generated release tool catalog", "", "Generated by `" + (legacyNames ? "Generate-Phase6Plan.py" : "generate-catalogs") + "` from registered host catalogs. See [scope and contracts](version-tools.md).", "Groups compare current V4 tool names. Read the selected host schema; V20/V21 share the existing Foundation tools and the explicitly ported families. Ported declarations are reflected from shared source; native acceptance remains separate.", "" };
        foreach (var (group, title) in new[] { ("all-releases", "All eight releases"), ("shared-subset", "Shared by some releases"), ("release-only", "One release only") }) { var selected = groups.Where(t => Text(t!["group"]) == group).ToArray(); lines.AddRange([$"## {title} ({selected.Length})", "", "| Tool | Release keys |", "|---|---|"]); lines.AddRange(selected.Select(t => $"| `{Text(t!["name"])}` | {string.Join(", ", Strings(t["releases"]))} |")); lines.Add(""); }
        return new() { ["manifest/version-tools.json"] = Encode(record), ["docs/reference/version-tool-catalog.md"] = string.Join('\n', lines) };
    }
}
