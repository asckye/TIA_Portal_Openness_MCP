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
            foreach (var tool in roster)
            {
                var response = await Call(JsonSerializer.Serialize(new { toolName = tool.ProtocolTool.Name }));
                check(response.IsError != true, "usage retrieval: " + release + "/" + tool.ProtocolTool.Name);
                var usage = JsonNode.Parse(((TextContentBlock)response.Content.Single()).Text)!["usage"]!;
                check(usage["releaseKey"]!.GetValue<string>() == release, "usage has exact release");
                check(JsonNode.DeepEquals(usage["inputSchema"], JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())), "usage reflects actual schema");
            }
            var connect = JsonNode.Parse(((TextContentBlock)(await Call("{\"toolName\":\"Connect\"}")).Content.Single()).Text)!["usage"]!;
            var arguments = connect["example"]!["request"]!["params"]!["arguments"]!.AsObject();
            check(arguments.Count == 1 && arguments.ContainsKey("processId"), "foundation Connect must not reuse full-engine projectName example");
            check((await Call("{\"toolName\":\"ManageStartdriveParameter\"}")).IsError == true, "foundation must reject unavailable full-engine usage");
            check((await Call("{\"offset\":-1}")).IsError == true, "invalid page is an error");
            check((await Call("{\"toolName\":\"Connect\",\"query\":\"x\"}")).IsError == true, "conflicting selectors rejected");
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
