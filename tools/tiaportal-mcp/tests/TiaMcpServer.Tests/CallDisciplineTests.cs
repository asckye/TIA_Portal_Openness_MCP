using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    // 2.7.58: schema hints (enum / default / examples), derived examples for every tool, the compact preflight summary
    // attached to failed calls, and the verified recipes.
    internal static class CallDisciplineTests
    {
        private static JsonObject O(string json) => (JsonObject)JsonNode.Parse(json)!;
        private static PreflightLogic.ParameterSpec S(string name, string kind, bool required, string? def = null, string desc = "")
            => new PreflightLogic.ParameterSpec(name, kind, required, def, desc);

        internal static void Run(Action<bool, string> check)
        {
            // ---- SchemaHintsLogic.Augment ----
            var specs = new List<PreflightLogic.ParameterSpec>
            {
                S("softwarePath", "string", true, null, "softwarePath: PLC software path, e.g. 'PLC_1'"),
                S("action", "string", false, "\"read\"", "action: read | create | delete"),
                S("tableKind", "string", false, "\"watch\"", "tableKind: watch|force"),
                S("mode", "string", false, "\"\"", "mode: one of default/singleStep"),
                S("limit", "integer", false, "200"),
                S("ratio", "number", false, "0.5"),
                S("dryRun", "boolean", false, "true"),
                S("free", "string", false, "\"\"", "free: any text, e.g. 'x'"),
            };
            var schema = O("{\"type\":\"object\",\"properties\":{\"softwarePath\":{\"type\":\"string\"},\"action\":{\"type\":\"string\"},\"tableKind\":{\"type\":\"string\"},\"mode\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\"},\"ratio\":{\"type\":\"number\"},\"dryRun\":{\"type\":\"boolean\"},\"free\":{\"type\":\"string\"}},\"required\":[\"softwarePath\"]}");
            var r = SchemaHintsLogic.Augment(schema, specs, O("{\"softwarePath\":\"PLC_1\",\"action\":\"read\",\"free\":[\"a\"]}"));
            var props = (JsonObject)schema["properties"]!;
            check(r.Enums == 3 && r.EnumProperties.SequenceEqual(new[] { "action", "tableKind", "mode" }), "schema: enum for pipe / unspaced pipe / slash lists, none for prose");
            check(props["action"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(new[] { "read", "create", "delete" }), "schema: enum values in documented order");
            check(props["mode"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(new[] { "default", "singleStep", "" }), "schema: the empty default is added to the enum so the default stays valid");
            check(props["action"]!["default"]!.GetValue<string>() == "read" && props["limit"]!["default"]!.GetValue<long>() == 200 && Math.Abs(props["ratio"]!["default"]!.GetValue<double>() - 0.5) < 1e-9 && props["dryRun"]!["default"]!.GetValue<bool>() == true && props["free"]!["default"]!.GetValue<string>() == "", "schema: defaults typed per kind");
            check(props["softwarePath"]!["default"] == null && props["softwarePath"]!["examples"]!.AsArray()[0]!.GetValue<string>() == "PLC_1" && props["action"]!["examples"]!.AsArray().Count == 1 && props["limit"]!["examples"] == null, "schema: examples only where the worked example has a value, no default for required");
            check(r.Defaults == 7 && r.Examples == 3 && r.Changed, "schema: counts");
            check(props["free"]!["examples"]!.AsArray()[0]!.GetValue<string>() == "[\"a\"]", "schema: array example on a string parameter is shown as JSON text");
            var again = SchemaHintsLogic.Augment(schema, specs, null);
            check(!again.Changed, "schema: idempotent (existing enum / default / examples kept)");
            check(!SchemaHintsLogic.Augment(O("{\"type\":\"object\"}"), specs, null).Changed, "schema: no properties -> nothing invented");
            check(SchemaHintsLogic.DefaultString("\"read\"") == "read" && SchemaHintsLogic.DefaultString("null") == null && SchemaHintsLogic.DefaultString("200") == null, "schema: default string extraction");

            // ---- derived examples ----
            var derivedSpecs = new List<PreflightLogic.ParameterSpec>
            {
                S("softwarePath", "string", true, null, "softwarePath: the PLC"),
                S("hmiSoftwarePath", "string", true, null, "hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')"),
                S("action", "string", true, null, "action: register | powerOn"),
                S("devicePathJson", "string", true, null, ""),
                S("propertiesJson", "string", true, null, ""),
                S("exportPath", "string", true, null, "exportPath: full file path to write to (e.g. C:\\\\temp\\\\screen.xml)"),
                S("outputDirectory", "string", true, null, "outputDirectory: folder"),
                S("blockPath", "string", true, null, ""),
                S("count", "integer", true, null, ""),
                S("enabled", "boolean", true, "false", ""),
                S("optional", "string", false, "\"x\"", ""),
                S("thing", "string", true, null, "thing: something"),
            };
            var derived = ToolExamples.Derive("SomeTool", derivedSpecs);
            var d = O(derived.ArgumentsJson);
            check(derived.Note == ToolExamples.DerivedNote && d.Count == 11 && d["optional"] == null, "derive: required parameters only, note marks it derived");
            check(d["softwarePath"]!.GetValue<string>() == "PLC_1" && d["hmiSoftwarePath"]!.GetValue<string>() == "HMI_RT_1" && d["action"]!.GetValue<string>() == "register", "derive: known names / e.g. values / first alternative");
            check(d["devicePathJson"]!.GetValue<string>() == "[]" && d["propertiesJson"]!.GetValue<string>() == "{}" && d["blockPath"]!.GetValue<string>() == "Main", "derive: *Json shapes and blockPath");
            check(d["exportPath"]!.GetValue<string>() == "C:\\temp\\screen.xml" && d["outputDirectory"]!.GetValue<string>() == "C:\\Temp\\SomeTool", "derive: file path from e.g., directory placeholder");
            check(d["count"]!.GetValue<long>() == 1 && d["enabled"]!.GetValue<bool>() == false && d["thing"]!.GetValue<string>() == "<thing>", "derive: numbers, booleans from default, unknown -> <name>");
            check(ToolExamples.FindOrDerive("GetBlocks", derivedSpecs).Note != ToolExamples.DerivedNote && ToolExamples.FindOrDerive("NoSuchTool", derivedSpecs).Note == ToolExamples.DerivedNote, "derive: curated example wins");
            // 2.7.59 real machine: the vocabulary's e.g. [\"PLC_1\"] produced the fragment "[" and chartPath a host path
            var vmSpecs = new List<PreflightLogic.ParameterSpec>
            {
                S("devicePathJson", "string", true, null, ParameterVocabulary.Describe("devicePathJson")!),
                S("itemPathJson", "string", true, null, ParameterVocabulary.Describe("itemPathJson")!),
                S("chartPath", "string", true, null, ParameterVocabulary.Describe("chartPath")!),
                S("tablePath", "string", true, null, "tablePath: 'Group/Table' path"),
                S("importPath", "string", true, null, ParameterVocabulary.Describe("importPath")!),
                S("logFilePath", "string", true, null, "logFilePath: full path of the log file to write on the TIA machine ('' = no log)."),
                S("culturesJson", "string", true, null, "culturesJson: JSON array of language tags, e.g. ['en-US','zh-CN'] ('[]' = all)."),
            };
            var vm = O(ToolExamples.Derive("ManageDccChartInterface", vmSpecs).ArgumentsJson);
            check(vm["devicePathJson"]!.GetValue<string>() == "[]" && vm["itemPathJson"]!.GetValue<string>() == "[]" && vm["culturesJson"]!.GetValue<string>() == "[]", "derive: *Json placeholders never come from an e.g. fragment (real-machine '[')");
            check(vm["chartPath"]!.GetValue<string>() == "<Folder/Name>" && vm["tablePath"]!.GetValue<string>() == "<Folder/Name>", "derive: object paths are not host paths");
            check(vm["importPath"]!.GetValue<string>().StartsWith(@"C:\Temp\") && vm["logFilePath"]!.GetValue<string>().StartsWith(@"C:\Temp\"), "derive: host paths by name");

            // ---- PreflightSummary (what a failed call carries) ----
            var summary = McpServer.PreflightSummary("UpdateUnifiedRuntimeSettings", O("{\"SoftwarePath\":\"HMI\",\"changesJson\":\"{}\"}"));
            check(summary["missing"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(new[] { "expectedProject" }) && summary["caseFixes"]!.AsArray().Count == 1 && summary["example"] is JsonObject && summary["exampleDerived"]!.GetValue<bool>() && summary["next"]!.GetValue<string>().StartsWith("Correct the argument problems"), "summary: missing + case fix + derived example + next step");
            check(summary["unknown"] == null && summary["typeProblems"] == null && summary["prerequisite"] == null, "summary: empty lists omitted, prerequisite unknown offline");
            var clean = McpServer.PreflightSummary("UpdateUnifiedRuntimeSettings", O("{\"softwarePath\":\"HMI\",\"expectedProject\":\"P\",\"changesJson\":\"{}\"}"));
            check(clean["missing"] == null && clean["next"]!.GetValue<string>().StartsWith("The message names the cause"), "summary: clean arguments -> business-failure guidance");
            var unknownTool = McpServer.PreflightSummary("NoSuchTool", O("{}"));
            check(unknownTool["next"]!.GetValue<string>().StartsWith("No tool named 'NoSuchTool'"), "summary: unknown tool");
            var enumSpecsTool = McpServer.PreflightSummary("UpdateUnifiedRuntimeSettings", O("{\"softwarePath\":\"HMI\",\"expectedProject\":\"P\",\"changesJson\":\"{}\",\"dryRun\":\"maybe\"}"));
            check(enumSpecsTool["typeProblems"]!.AsArray().Count == 1, "summary: type problem listed");

            // ---- vocabulary (parameters without their own [Description]) ----
            check(ParameterVocabulary.Describe("dryRun")!.StartsWith("dryRun: true (default) previews") && ParameterVocabulary.Describe("nope") == null && ParameterVocabulary.Names.Count >= 60, "vocabulary: common names covered, unknown -> null");
            check(PreflightLogic.Alternatives(ParameterVocabulary.Describe("unitKind")).Count == 0 && PreflightLogic.Alternatives(ParameterVocabulary.Describe("action")).Count == 0 && PreflightLogic.Alternatives(ParameterVocabulary.Describe("eventType")).Count == 0, "vocabulary: generic texts never turn into a (wrong) enum");
            var probeSpecs = McpServer.SpecsOf(typeof(McpServer).GetMethod("ProbeResult")!);
            check(probeSpecs.Count == 1 && probeSpecs[0].Name == "success" && !probeSpecs[0].Synthesized && probeSpecs[0].Description.Length == 0, "vocabulary: a name outside the vocabulary stays undescribed and unsynthesized");
            var vocabSchema = O("{\"properties\":{\"dryRun\":{\"type\":\"boolean\"},\"x\":{\"type\":\"string\",\"description\":\"own\"}}}");
            var vocabResult = SchemaHintsLogic.Augment(vocabSchema, new List<PreflightLogic.ParameterSpec> { new PreflightLogic.ParameterSpec("dryRun", "boolean", false, "true", ParameterVocabulary.Describe("dryRun")!, true), new PreflightLogic.ParameterSpec("x", "string", true, null, "synthesized", true) }, null);
            check(vocabResult.Descriptions == 1 && ((JsonObject)vocabSchema["properties"]!["dryRun"]!)["description"]!.GetValue<string>().StartsWith("dryRun:") && ((JsonObject)vocabSchema["properties"]!["x"]!)["description"]!.GetValue<string>() == "own", "schema: vocabulary description injected only where the SDK schema has none");

            // ---- recipes ----
            check(ToolRecipes.All.Count >= 12 && ToolRecipes.Find("DOWNLOAD-PLCSIM") != null && ToolRecipes.Find("nope") == null, "recipes: table populated, lookup case-insensitive");
            check(ToolRecipes.All.All(x => x.Steps.Count >= 3 && x.Purpose.Length > 0), "recipes: every recipe has purpose and at least 3 steps");
            foreach (var recipe in ToolRecipes.All)
                foreach (var step in recipe.Steps)
                    check(JsonNode.Parse(step.ArgumentsJson) is JsonObject, "recipes: '" + recipe.Topic + "' step " + step.Tool + " is a JSON object");
            var sentinel = ToolRecipes.ValidateAgainst(tool => tool == "Bootstrap" ? new List<KeyValuePair<string, bool>>() : tool == "Connect" ? new List<KeyValuePair<string, bool>> { new KeyValuePair<string, bool>("project", true) } : null);
            check(sentinel.Any(p => p.Contains("connect-project step 2 (Connect): 'projectName' is not a parameter")) && sentinel.Any(p => p.Contains("required parameter 'project' missing")) && sentinel.Any(p => p.EndsWith("no tool of that name")), "recipes: validation flags wrong keys, missing required and unknown tools (sentinel)");
            IReadOnlyList<KeyValuePair<string, bool>>? Linked(string tool)
            {
                var method = typeof(McpServer).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.GetCustomAttribute<global::ModelContextProtocol.Server.McpServerToolAttribute>() != null
                                         && string.Equals(m.GetCustomAttribute<global::ModelContextProtocol.Server.McpServerToolAttribute>()!.Name ?? m.Name, tool, StringComparison.Ordinal));
                return method == null ? null : method.GetParameters().Where(p => !McpServer.IsInfrastructureParameter(p.ParameterType)).Select(p => new KeyValuePair<string, bool>(p.Name!, !p.HasDefaultValue)).ToList();
            }
            var linkedProblems = ToolRecipes.ValidateAgainst(Linked).Where(p => !p.EndsWith("no tool of that name")).ToList();
            check(linkedProblems.Count == 0, "recipes: steps of linked tools fit" + (linkedProblems.Count > 0 ? ": " + string.Join("; ", linkedProblems) : ""));

            var list = McpServer.GetRecipe("");
            check(list.Meta!["success"]!.GetValue<bool>() && list.Items!.Count() == ToolRecipes.All.Count && list.Meta["topics"]!.AsArray().Count == ToolRecipes.All.Count, "GetRecipe: empty topic lists every recipe");
            var one = McpServer.GetRecipe("download-plcsim");
            check(one.Meta!["success"]!.GetValue<bool>() && one.Meta["steps"]!.AsArray().Count == 11 && one.Items!.Any(i => i.StartsWith("7. DownloadToPlc ")) && one.Items!.First().StartsWith("Purpose:"), "GetRecipe: numbered exact calls with expectations");
            check(!McpServer.GetRecipe("nope").Meta!["success"]!.GetValue<bool>() && McpServer.GetRecipe("nope").Message!.Contains("download-plcsim"), "GetRecipe: unknown topic lists the topics");
        }
    }
}
