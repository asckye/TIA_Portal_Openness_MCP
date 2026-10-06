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
        private static PreflightLogic.ParameterSpec S(string name, string kind, bool required, string? def = null, string desc = "", string[]? values = null, string? example = null)
            => new PreflightLogic.ParameterSpec(name, kind, required, def, desc, allowedValues: values, exampleJson: example);

        internal static void Run(Action<bool, string> check)
        {
            // ---- SchemaHintsLogic.Augment ----
            var specs = new List<PreflightLogic.ParameterSpec>
            {
                S("softwarePath", "string", true, null, "softwarePath: PLC software path, e.g. 'PLC_1'", example: "\"PLC_1\""),
                S("action", "string", false, "\"read\"", "action: read | create | delete", values: new[] { "read", "create", "delete" }),
                S("tableKind", "string", false, "\"watch\"", "tableKind: watch|force", values: new[] { "watch", "force" }),
                S("mode", "string", false, "\"\"", "mode: one of default/singleStep", values: new[] { "default", "singleStep" }),
                S("limit", "integer", false, "200"),
                S("ratio", "number", false, "0.5"),
                S("dryRun", "boolean", false, "true"),
                S("free", "array", false, null, "free: array values"),
            };
            var schema = O("{\"type\":\"object\",\"properties\":{\"softwarePath\":{\"type\":\"string\"},\"action\":{\"type\":\"string\"},\"tableKind\":{\"type\":\"string\"},\"mode\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\"},\"ratio\":{\"type\":\"number\"},\"dryRun\":{\"type\":\"boolean\"},\"free\":{\"type\":\"array\"}},\"required\":[\"softwarePath\"]}");
            var r = SchemaHintsLogic.Augment(schema, specs, O("{\"softwarePath\":\"PLC_1\",\"action\":\"read\",\"free\":[\"a\"]}"));
            var props = (JsonObject)schema["properties"]!;
            check(r.Enums == 3 && r.EnumProperties.SequenceEqual(new[] { "action", "tableKind", "mode" }), "schema: enum for pipe / unspaced pipe / slash lists, none for prose");
            check(props["action"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(new[] { "read", "create", "delete" }), "schema: enum values in documented order");
            check(props["mode"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(new[] { "default", "singleStep", "" }), "schema: the empty default is added to the enum so the default stays valid");
            check(props["action"]!["default"]!.GetValue<string>() == "read" && props["limit"]!["default"]!.GetValue<long>() == 200 && Math.Abs(props["ratio"]!["default"]!.GetValue<double>() - 0.5) < 1e-9 && props["dryRun"]!["default"]!.GetValue<bool>() == true && props["free"]!["default"] == null, "schema: defaults typed per kind");
            check(props["softwarePath"]!["default"] == null && props["softwarePath"]!["examples"]!.AsArray()[0]!.GetValue<string>() == "PLC_1" && props["action"]!["examples"]!.AsArray().Count == 1 && props["limit"]!["examples"] == null, "schema: examples only where the worked example has a value, no default for required");
            check(r.Defaults == 6 && r.Examples == 3 && r.Changed, "schema: counts");
            check(props["free"]!["examples"]!.AsArray()[0]!.AsArray()[0]!.GetValue<string>() == "a", "schema: array example retains its JSON array type");
            var again = SchemaHintsLogic.Augment(schema, specs, null);
            check(!again.Changed, "schema: idempotent (existing enum / default / examples kept)");
            check(!SchemaHintsLogic.Augment(O("{\"type\":\"object\"}"), specs, null).Changed, "schema: no properties -> nothing invented");
            check(SchemaHintsLogic.DefaultString("\"read\"") == "read" && SchemaHintsLogic.DefaultString("null") == null && SchemaHintsLogic.DefaultString("200") == null, "schema: default string extraction");

            // ---- derived examples ----
            var derivedSpecs = new List<PreflightLogic.ParameterSpec>
            {
                S("softwarePath", "string", true, null, "softwarePath: the PLC", example: "\"PLC_1\""),
                S("hmiSoftwarePath", "string", true, null, "hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')", example: "\"HMI_RT_1\""),
                S("action", "string", true, null, "action: register | powerOn", values: new[] { "register", "powerOn" }),
                S("devicePathJson", "string", true, null, "", example: "\"[]\""),
                S("propertiesJson", "string", true, null, "", example: "\"{}\""),
                S("exportPath", "string", true, null, "exportPath: full file path to write to (e.g. C:\\\\temp\\\\screen.xml)", example: "\"C:\\\\temp\\\\screen.xml\""),
                S("outputDirectory", "string", true, null, "outputDirectory: folder", example: "\"C:\\\\Temp\\\\SomeTool\""),
                S("blockPath", "string", true, null, "", example: "\"Main\""),
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
            check(ToolExamples.FindOrDerive("ListPlcBlocks", derivedSpecs).Note != ToolExamples.DerivedNote && ToolExamples.FindOrDerive("NoSuchTool", derivedSpecs).Note == ToolExamples.DerivedNote, "derive: curated example wins");
            // 2.7.59 real machine: the vocabulary's e.g. [\"PLC_1\"] produced the fragment "[" and chartPath a host path
            var vmSpecs = new List<PreflightLogic.ParameterSpec>
            {
                S("devicePathJson", "string", true, null, ParameterVocabulary.Describe("devicePathJson")!, example: "\"[]\""),
                S("itemPathJson", "string", true, null, ParameterVocabulary.Describe("itemPathJson")!, example: "\"[]\""),
                S("chartPath", "string", true, null, ParameterVocabulary.Describe("chartPath")!, example: "\"<Folder/Name>\""),
                S("tablePath", "string", true, null, "tablePath: 'Group/Table' path", example: "\"<Folder/Name>\""),
                S("importPath", "string", true, null, ParameterVocabulary.Describe("importPath")!, example: "\"C:\\\\Temp\\\\ManageDccChartInterface.xml\""),
                S("logFilePath", "string", true, null, "logFilePath: full path of the log file to write on the TIA machine ('' = no log).", example: "\"C:\\\\Temp\\\\ManageDccChartInterface.xml\""),
                S("culturesJson", "string", true, null, "culturesJson: JSON array of language tags, e.g. ['en-US','zh-CN'] ('[]' = all).", example: "\"[]\""),
            };
            var vm = O(ToolExamples.Derive("ManageDccChartInterface", vmSpecs).ArgumentsJson);
            check(vm["devicePathJson"]!.GetValue<string>() == "[]" && vm["itemPathJson"]!.GetValue<string>() == "[]" && vm["culturesJson"]!.GetValue<string>() == "[]", "derive: *Json placeholders never come from an e.g. fragment (real-machine '[')");
            check(vm["chartPath"]!.GetValue<string>() == "<Folder/Name>" && vm["tablePath"]!.GetValue<string>() == "<Folder/Name>", "derive: object paths are not host paths");
            check(vm["importPath"]!.GetValue<string>().StartsWith(@"C:\Temp\") && vm["logFilePath"]!.GetValue<string>().StartsWith(@"C:\Temp\"), "derive: host paths by name");

            // ---- vocabulary (parameters without their own [Description]) ----
            check(ParameterVocabulary.Describe("dryRun")!.StartsWith("dryRun: true (default) previews") && ParameterVocabulary.Describe("nope") == null && ParameterVocabulary.Names.Count >= 60, "vocabulary: common names covered, unknown -> null");
            check(new[] { "unitKind", "action", "eventType" }.All(name => SchemaHintsLogic.Augment(O("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}}}"), new[] { S("value", "string", true, desc: ParameterVocabulary.Describe(name)!) }, null).Enums == 0), "vocabulary: prose never invents an enum");
            var probeSpecs = McpServer.SpecsOf(typeof(ToolBridgeProbes).GetMethod("ProbeResult")!);
            check(probeSpecs.Count == 1 && probeSpecs[0].Name == "success" && !probeSpecs[0].Synthesized && probeSpecs[0].Description.Length == 0, "vocabulary: a name outside the vocabulary stays undescribed and unsynthesized");
            var vocabSchema = O("{\"properties\":{\"dryRun\":{\"type\":\"boolean\"},\"x\":{\"type\":\"string\",\"description\":\"own\"}}}");
            var vocabResult = SchemaHintsLogic.Augment(vocabSchema, new List<PreflightLogic.ParameterSpec> { new PreflightLogic.ParameterSpec("dryRun", "boolean", false, "true", ParameterVocabulary.Describe("dryRun")!, true), new PreflightLogic.ParameterSpec("x", "string", true, null, "synthesized", true) }, null);
            check(vocabResult.Descriptions == 1 && ((JsonObject)vocabSchema["properties"]!["dryRun"]!)["description"]!.GetValue<string>().StartsWith("dryRun:") && ((JsonObject)vocabSchema["properties"]!["x"]!)["description"]!.GetValue<string>() == "own", "schema: vocabulary description injected only where the SDK schema has none");

            // ---- recipes ----
            check(ToolRecipes.All.Count >= 12 && ToolRecipes.Find("DOWNLOAD-PLCSIM") != null && ToolRecipes.Find("nope") == null, "recipes: table populated, lookup case-insensitive");
            check(ToolRecipes.All.All(x => x.Steps.Count >= 3 && x.Purpose.Length > 0), "recipes: every recipe has purpose and at least 3 steps");
            check(ToolRecipes.Find("plc-scl-block")!.Steps.All(s => !s.ArgumentsJson.Contains("expectedProjectFile") && !s.ArgumentsJson.Contains("expectedPlanHash")), "recipes: full-engine source flow excludes foundation-only arguments");
            foreach (var recipe in ToolRecipes.All)
                foreach (var step in recipe.Steps)
                    check(JsonNode.Parse(step.ArgumentsJson) is JsonObject, "recipes: '" + recipe.Topic + "' step " + step.Tool + " is a JSON object");
            var sentinel = ToolRecipes.ValidateAgainst(tool => tool == "ListPortalProcessProjects" ? new List<KeyValuePair<string, bool>>() : tool == "ConnectProject" ? new List<KeyValuePair<string, bool>> { new KeyValuePair<string, bool>("project", true) } : null);
            check(sentinel.Any(p => p.Contains("connect-project step 2 (ConnectProject): 'processId' is not a parameter")) && sentinel.Any(p => p.Contains("required parameter 'project' missing")) && sentinel.Any(p => p.EndsWith("no tool of that name")), "recipes: validation flags wrong keys, missing required and unknown tools (sentinel)");
            IReadOnlyList<KeyValuePair<string, bool>>? Linked(string tool)
            {
                var method = ToolBridgeFixture.Catalog.Methods.FirstOrDefault(entry => string.Equals(entry.Key, tool, StringComparison.Ordinal)).Value;
                return method == null ? null : method.GetParameters().Where(p => !McpServer.IsInfrastructureParameter(p.ParameterType)).Select(p => new KeyValuePair<string, bool>(p.Name!, !p.HasDefaultValue)).ToList();
            }
            var linkedProblems = ToolRecipes.ValidateAgainst(Linked).Where(p => !p.EndsWith("no tool of that name")).ToList();
            check(linkedProblems.Count == 0, "recipes: steps of linked tools fit" + (linkedProblems.Count > 0 ? ": " + string.Join("; ", linkedProblems) : ""));

            var usage = new ToolUsageTools();
            var list = McpServer.ResultBody(usage.GetToolUsage(exampleKind: "sequence"))!;
            check((bool)list["ok"]! && list["data"]!["examples"]!.AsArray().Count > 0, "usage: lists call sequences");
            var one = McpServer.ResultBody(usage.GetToolUsage(exampleId: "sequence/download-plcsim"))!;
            check((bool)one["ok"]! && one["data"]!["examples"]![0]!["steps"]!.AsArray().Count == 11, "usage: preserves the ordered sequence");
            check((string?)McpServer.ResultBody(usage.GetToolUsage(exampleId: "sequence/nope"))!["error"]!["code"] == "NOT_FOUND", "usage: unknown sequence is not found");
        }
    }
}
