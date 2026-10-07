using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.LegacyHost;
using TiaOpenness.Shared;

internal static class ToolUsageTests
{
    internal static async Task Run(IMcpServer server, Action<bool,string> check)
    {
        foreach (var release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        {
            var worker = new FakeWorker();
            var roster = LegacyHostToolRegistry.Create(worker, release, true);
            var guide = roster.Single(t => t.ProtocolTool.Name == "GetToolUsage");
            async Task<CallToolResult> Call(string args) => await guide.InvokeAsync(new RequestContext<CallToolRequestParams>(server) {
                Params = new CallToolRequestParams { Name = "GetToolUsage", Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args) } });
            if (release is not ("20" or "21"))
            {
                var indexEnvelope = JsonNode.Parse(((TextContentBlock)(await Call("{}")).Content.Single()).Text)!;
                var index = indexEnvelope["data"]!;
                check((int?)indexEnvelope["schemaVersion"] == 4 && (string?)indexEnvelope["meta"]!["releaseKey"] == release
                    && index["tools"]!.AsArray().Count > 0,
                    "GetToolUsage index has the V4 envelope and exact Foundation release: " + release);

                var languageEnvelope = JsonNode.Parse(((TextContentBlock)(await Call("{\"language\":\"scl\",\"exampleKind\":\"language\"}")).Content.Single()).Text)!;
                check((string?)languageEnvelope["meta"]!["releaseKey"] == release
                    && languageEnvelope["data"]!["examples"]!.AsArray().Any(row => (bool?)row!["releaseMatches"] == true
                        && (bool?)row["profileMatches"] == true), "SCL examples match Foundation release: " + release);

                foreach (var language in new[] { "scl", "scl-sd", "lad", "fbd", "mixed", "db", "udt", "s7res", "stl", "graph", "hmi-javascript", "hmi-vbscript", "csharp" })
                {
                    var languageLibrary = JsonNode.Parse(((TextContentBlock)(await Call(JsonSerializer.Serialize(new { language })).ConfigureAwait(false)).Content.Single()).Text)!;
                    var languageData = languageLibrary["data"]!;
                    check((string?)languageLibrary["meta"]!["releaseKey"] == release
                        && (languageData["examples"]!.AsArray().Count > 0 || languageData["sourceDocuments"]!.AsArray().Count > 0),
                        "Foundation language library loads " + language + " for " + release);
                }

                var sourceEnvelope = JsonNode.Parse(((TextContentBlock)(await Call("{\"exampleId\":\"scl-add\",\"exampleKind\":\"language\"}")).Content.Single()).Text)!;
                var source = sourceEnvelope["data"]!["examples"]![0]!;
                check((string?)sourceEnvelope["meta"]!["releaseKey"] == release && (bool?)source["releaseMatches"] == true
                    && source["files"]!.AsArray().Count > 0, "Complete SCL source matches Foundation release: " + release);

                var sequenceEnvelope = JsonNode.Parse(((TextContentBlock)(await Call("{\"exampleId\":\"sequence/plc-scl-block-foundation\",\"exampleKind\":\"sequence\"}")).Content.Single()).Text)!;
                var sequence = sequenceEnvelope["data"]!["examples"]![0]!;
                check((string?)sequenceEnvelope["meta"]!["releaseKey"] == release && (bool?)sequence["releaseMatches"] == true
                    && (bool?)sequence["available"] == true && sequence["steps"]!.AsArray().Count > 0,
                    "Foundation sequence is registered and matches release: " + release);

                var searchEnvelope = JsonNode.Parse(((TextContentBlock)(await Call("{\"query\":\"openness-base\"}")).Content.Single()).Text)!;
                check((string?)searchEnvelope["meta"]!["releaseKey"] == release
                    && searchEnvelope["data"]!["matches"]!.AsArray().Any(row => (string?)row!["id"] == "guides/skills/openness-base/SKILL.md"),
                    "Official document search returns the openness guide for release: " + release);
            }
            foreach (var tool in roster)
            {
                var response = await Call(JsonSerializer.Serialize(new { toolName = tool.ProtocolTool.Name }));
                check(response.IsError != true, "usage retrieval: " + release + "/" + tool.ProtocolTool.Name);
                var usage = JsonNode.Parse(((TextContentBlock)response.Content.Single()).Text)!["data"]!;
                check(usage["releaseKey"]!.GetValue<string>() == release, "usage has exact release");
                check(JsonNode.DeepEquals(usage["inputSchema"], JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())), "usage reflects actual schema");
                if(release is not ("20" or "21"))
                    check((string?)usage["example"]!["kind"]=="parameterized-call-example", "every deployed foundation tool has a maintained call example: "+release+"/"+tool.ProtocolTool.Name+" ("+(string?)usage["example"]!["kind"]+")");
                if(tool.ProtocolTool.Name is "BuildPlcUdt" or "BuildPlcGlobalDb")
                    check((string?)usage["example"]!["request"]!["params"]!["arguments"]!["outputReleaseKey"]=="21",
                        "Foundation declaration example keeps the VM-tested V21 output independent of host release");
                if (release is not ("20" or "21") && tool.ProtocolTool.Name is ("ExportPlcBlocks" or "ExportPlcTypes" or "ImportPlcBlocksFromDirectory" or "ImportPlcProgramFromDirectory"))
                    check(((string?)usage["example"]!["request"]!["params"]!["arguments"]!["softwarePath"])?.Contains("exact softwarePath from GetProjectTree") == true,
                        "batch usage binds the exact software path: " + release + "/" + tool.ProtocolTool.Name);
            }
            var connect = JsonNode.Parse(((TextContentBlock)(await Call("{\"toolName\":\"ConnectPortal\"}")).Content.Single()).Text)!["data"]!;
            var arguments = connect["example"]!["request"]!["params"]!["arguments"]!.AsObject();
            check(arguments.Count == 1 && arguments.ContainsKey("processId"), "foundation Connect must not reuse full-engine projectName example");
            check((await Call("{\"toolName\":\"ManageStartdriveParameter\"}")).IsError == true, "foundation must reject unavailable full-engine usage");
            check((await Call("{\"offset\":-1}")).IsError == true, "invalid page is an error");
            check((await Call("{\"toolName\":\"ConnectPortal\",\"query\":\"x\"}")).IsError == true, "conflicting selectors rejected");
            check(worker.Calls == 0, "usage never invokes worker even when native access enabled");
        }
        var docId = "snippets/src/TiaPortal.Openness.CodeSnippets.Plain.Startdrive/ParameterSnippets.cs";
        var first = ToolUsageCatalog.ReadReference("", docId, 0, 7);
        var second = ToolUsageCatalog.ReadReference("", docId, 7, 7);
        check(first["nextOffset"]!.GetValue<int>() == 7 && second["offset"]!.GetValue<int>() == 7, "source has sequential pages");
        var map = ToolUsageCatalog.Mapping("ManageStartdriveParameter");
        map["documents"] = new JsonArray();
        check(ToolUsageCatalog.Mapping("ManageStartdriveParameter")["documents"]!.AsArray().Count > 0, "caller cannot corrupt shared catalog");
    }
}
