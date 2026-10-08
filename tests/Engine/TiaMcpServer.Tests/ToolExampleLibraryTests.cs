using System;
using System.Linq;
using System.Text.Json.Nodes;
using TiaOpenness.Shared;

internal static class ToolExampleLibraryTests
{
    internal static void Run(Action<bool, string> check)
    {
        var schema = JsonNode.Parse(@"{""type"":""object"",""required"":[""softwarePath""],""properties"":{
            ""softwarePath"":{""type"":""string""},""action"":{""type"":""string"",""enum"":[""list"",""read"",""createFromFile""],""default"":""list""},
            ""dryRun"":{""type"":""boolean"",""default"":true},""name"":{""type"":""string""},""filePath"":{""type"":""string"",""default"":""""}}}")!.AsObject();
        var roster = new[] { "GetProjectTree", "ManagePlcExternalSources" };
        JsonObject Describe(string action) => ToolUsageCatalog.Describe("ManagePlcExternalSources", "21", "full-engine", "test contract", schema,
            "{\"softwarePath\":\"PLC_Example\",\"action\":\"read\"}", "read example", action, roster);
        var read = Describe("read");
        var import = Describe("CREATEFROMFILE");
        check((string?)read["example"]!["kind"] == "parameterized-call-example", "library uses the shared call record");
        check(((string)import["example"]!["request"]!["params"]!["arguments"]!["filePath"]!).EndsWith("FC_Add.scl"), "source creation supplies its conditional file input");
        check((string?)import["example"]!["request"]!["params"]!["arguments"]!["action"] == "createFromFile", "operation selection resolves canonical case");
        check(import["parameterSources"]!["filePath"] != null, "optional operation inputs remain discoverable");
        check(import["parameterSources"]!["softwarePath"]!["tools"]!.AsArray().Select(n => (string)n!).SequenceEqual(new[] { "GetProjectTree" }), "input origins use the exact roster");
        bool refused = false;
        try { Describe("invented"); } catch (ArgumentException) { refused = true; }
        check(refused, "unknown operation cannot masquerade as an example");
        read["example"]!["request"]!["params"]!["arguments"]!["softwarePath"] = "modified";
        check((string?)Describe("read")["example"]!["request"]!["params"]!["arguments"]!["softwarePath"] == "PLC_1", "example retrieval does not mutate cached data");

        var pathSchema = JsonNode.Parse(@"{""type"":""object"",""required"":[""path""],""properties"":{""path"":{""type"":""string""}}}")!.AsObject();
        var oldPath = ToolUsageCatalog.Describe("OpenProject", "15.1", "plc-foundation", "", pathSchema);
        check((bool?)oldPath["parameterSources"]!["path"]!["requiresBinding"] == true
            && oldPath["example"]!["bindings"]!.AsArray().Any(p => (string?)p == "path")
            && ((string?)oldPath["example"]!["request"]!["params"]!["arguments"]!["path"])?.Contains("closed project file for this release") == true,
            "Foundation open example binds an exact closed target file for the selected release");
        var modernPath = ToolUsageCatalog.InlineCalls("20").First(r => (string?)r!["tool"] == "OpenProject")!;
        check(((string)modernPath["arguments"]!["path"]!).EndsWith(".ap20"), "inline discovery uses the same V20 example");
        var objectSchema = JsonNode.Parse(@"{""type"":""object"",""required"":[""softwarePath"",""objectPath"",""properties""],""properties"":{""softwarePath"":{""type"":""string""},""objectPath"":{""type"":""array""},""properties"":{""type"":""object""},""dryRun"":{""type"":""boolean""}}}")!.AsObject();
        var objectUsage = ToolUsageCatalog.Describe("SetUnifiedObjectProperties", "21", "full-engine", "", objectSchema);
        var objectArgs = objectUsage["example"]!["request"]!["params"]!["arguments"]!;
        var steps = TiaMcpServer.Siemens.EngineeringObjectAddress.Parse(objectArgs["objectPath"]!.ToJsonString());
        check(steps.Count == 3 && (string?)steps[2]!["property"] == "ScreenItems", "Unified example uses the actual property-step address grammar");
        check(objectUsage["example"]!["bindings"]!.AsArray().Any(p => (string?)p == "properties"), "unresolved native property values are explicit bindings");
        var patchSchema = JsonNode.Parse(@"{""type"":""object"",""required"":[""filePath"",""changes"",""expectedFingerprint""],""properties"":{""filePath"":{""type"":""string""},""changes"":{""type"":""array""},""expectedFingerprint"":{""type"":""string""},""dryRun"":{""type"":""boolean""}}}")!.AsObject();
        patchSchema["properties"]!["changes"] = JsonNode.Parse(TiaMcp.Logic.V4.Domain.DomainValidation.Contract<TiaMcp.Logic.V4.Domain.BlockEdit[]>().Schema.GetRawText());
        var patchCall = ToolUsageCatalog.Describe("PatchPlcBlockDocument", "21", "full-engine", "", patchSchema)["example"]!["request"]!["params"]!["arguments"]!;
        const string fixture = "<Document><Engineering version='V21'/><SW.Blocks.FC ID='0'><AttributeList><Name>FC_Example</Name><Number>1</Number><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList><ObjectList><MultilingualText ID='1' CompositionName='Title'><ObjectList><MultilingualTextItem ID='2' CompositionName='Items'><AttributeList><Culture>en-US</Culture><Text>Old title</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.Blocks.FC></Document>";
        var fingerprint = TiaMcpServer.ModelContextProtocol.PlcDocumentEditing.HashText(TiaMcpServer.ModelContextProtocol.PlcDocumentEditing.Canonical(TiaMcpServer.ModelContextProtocol.PlcDocumentEditing.Parse(fixture)));
        var edited = TiaMcpServer.ModelContextProtocol.PlcDocumentEditing.Patch(fixture, patchCall["changes"]!.ToJsonString(), fingerprint);
        check(edited.Contains("<Text>Example</Text>") && edited.Contains("<Name>FC_Example</Name>"), "example patch payload edits the intended text while preserving the block identity");

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

        var devices = ToolUsageCatalog.Sequences().Single(s => (string?)s!["id"] == "sequence/device-add-foundation")!;
        check(devices["releaseKeys"]!.AsArray().Select(k => (string)k!).SequenceEqual(new[] { "19" }), "device add native example is scoped to V19");
        check(devices["steps"]!.AsArray().Skip(1).Select(s => (string)s!["tool"]!).SequenceEqual(new[] {
            "ConnectPortal", "AttachOpenProject", "SearchHardwareCatalog", "CreateHardwareDevice", "CreateHardwareDevice", "GetProjectTree" }), "device add uses explicit binding, row lookup, preview/apply and readback");
        var deviceArgs = devices["steps"]![4]!["arguments"]!;
        check((string?)deviceArgs["preferredMlfb"] == "6ES7 513-1AM03-0AB0" && (string?)deviceArgs["preferredVersion"] == "V3.1", "device catalog example preserves the verified same-row article/version");
        var deviceApply = devices["steps"]![5]!["arguments"]!;
        check(deviceApply["expectedPlanHash"] != null && deviceApply["expectedProjectFile"] != null && (bool?)deviceApply["dryRun"] == false,
            "device add applies its own reviewed hash and exact project file");
        check(devices["notes"]!.GetValue<string>().Contains("same deviceName") && devices["notes"]!.GetValue<string>().Contains("SaveProject"), "device add documents duplicate refusal and separate save");
    }
}
