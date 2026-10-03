using System;
using System.Linq;
using System.Text.Json.Nodes;
using TiaOpenness.Shared;

internal static class ToolExampleLibraryTests
{
    internal static void Run(Action<bool, string> check)
    {
        var schema = JsonNode.Parse(@"{""type"":""object"",""required"":[""softwarePath""],""properties"":{
            ""softwarePath"":{""type"":""string""},""action"":{""type"":""string"",""enum"":[""read"",""import""],""default"":""read""},
            ""dryRun"":{""type"":""boolean"",""default"":true},""filePath"":{""type"":""string"",""default"":""""}}}")!.AsObject();
        var roster = new[] { "GetProjectTree", "ManagePlcExternalSources" };
        JsonObject Describe(string action) => ToolUsageCatalog.Describe("ManagePlcExternalSources", "21", "full-engine", "test contract", schema,
            "{\"softwarePath\":\"PLC_Example\",\"action\":\"read\"}", "read example", action, roster);
        var read = Describe("read");
        var import = Describe("IMPORT");
        check((string?)read["example"]!["kind"] == "curated-project-example", "library preserves a matching curated call");
        check((string?)import["example"]!["kind"] == "schema-template", "library does not reuse read arguments for import");
        check((string?)import["example"]!["request"]!["params"]!["arguments"]!["action"] == "import", "operation selection resolves canonical case");
        check(import["parameterSources"]!["filePath"] != null, "optional operation inputs remain discoverable");
        check(import["parameterSources"]!["softwarePath"]!["tools"]!.AsArray().Select(n => (string)n!).SequenceEqual(new[] { "GetProjectTree" }), "input origins use the exact roster");
        bool refused = false;
        try { Describe("invented"); } catch (ArgumentException) { refused = true; }
        check(refused, "unknown operation cannot masquerade as an example");
        read["example"]!["request"]!["params"]!["arguments"]!["softwarePath"] = "modified";
        check((string?)Describe("read")["example"]!["request"]!["params"]!["arguments"]!["softwarePath"] == "PLC_Example", "example retrieval does not mutate cached data");

        var full = ToolUsageCatalog.Examples("21", "full-engine", roster, language: "scl");
        var source = ToolUsageCatalog.Examples("21", "full-engine", roster, exampleId: "scl-add")["examples"]![0]!;
        check(((string)source["files"]![0]!["content"]!).Contains("END_FUNCTION"), "complete programming file is retrievable");
        check(ToolUsageCatalog.Examples("21", "full-engine", roster, language: "AWL")["language"]!.GetValue<string>() == "stl", "language aliases resolve to one library");
        var old = ToolUsageCatalog.Examples("17", "plc-foundation", roster, exampleId: "scl-sd-add")["examples"]![0]!;
        check(old["releaseMatches"]!.GetValue<bool>() == false, "new source format is not represented as V17 compatible");
        var sequence = ToolUsageCatalog.Examples("17", "plc-foundation", roster, exampleId: "sequence/connect-project")["examples"]![0]!;
        check(sequence["available"]!.GetValue<bool>() == false, "full-engine sequence is not usable on a foundation profile");
        var cpp = ToolUsageCatalog.Examples("21", "full-engine", roster, language: "csharp");
        check(cpp["sourceDocuments"]!.AsArray().Count > 30, "C# retrieves original official source examples");
        var eventExample = ToolUsageCatalog.Examples("21", "full-engine", roster, exampleId: "unified-tag-read")["examples"]![0]!;
        var moduleExample = ToolUsageCatalog.Examples("21", "full-engine", roster, exampleId: "unified-global-module")["examples"]![0]!;
        check((string?)eventExample["steps"]![0]!["tool"] == "SetUnifiedHmiButtonEventScriptCode" &&
            (string?)moduleExample["steps"]![0]!["tool"] == "ExchangeUnifiedScriptModules", "event and module examples use the correct engineering contracts");
    }
}
