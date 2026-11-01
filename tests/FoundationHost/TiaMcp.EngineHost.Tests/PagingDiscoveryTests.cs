using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed partial class PipelineTests
{
    [Fact]
    public void Tool_search_requires_relevant_capability_words_and_keeps_exact_names_first()
    {
        using var fixture = new Fixture(all: true);
        var exact = McpServer.FindTools("ListPlcBlocks");
        Assert.StartsWith("ListPlcBlocks(", exact.Items!.First());
        Assert.Empty(McpServer.FindTools("plc nonexistent-capability").Items ?? Array.Empty<string>());
        var page = McpServer.FindTools("plc", limit: 2);
        Assert.Equal(6, page.Items!.Count());
        var matches = McpServer.FindTools("watch table", limit: 100);
        Assert.NotEmpty(matches.Items!);
        Assert.All(matches.Items!.Where((_, i) => i % 3 == 0), line => {
            Assert.Contains("Watch", line); Assert.Contains("Table", line);
        });
        var descriptor = fixture.Catalog.All["SaveProject"];
        descriptor.Tool.Description = "uniqueincidentalneedle secondneedle";
        ((IDictionary<string, ToolDescriptor>)fixture.Catalog.All)[descriptor.Name] = new ToolDescriptor(descriptor.Name,
            descriptor.Tool.Description, descriptor.Tool, descriptor.Classification, descriptor.Signature, descriptor.Parameters,
            descriptor.DryRun, descriptor.CandidateFamily, descriptor.Execution);
        Assert.Empty(McpServer.FindTools("uniqueincidentalneedle").Items ?? Array.Empty<string>());
        Assert.Single(McpServer.FindTools("uniqueincidentalneedle secondneedle").Items!.Where((_, i) => i % 3 == 0));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Large_direct_and_CallTool_results_expose_a_working_continuation(bool bridge)
    {
        using var fixture = new Fixture(all: true);
        fixture.Worker.Payload = string.Concat(Enumerable.Repeat("PLC \"quoted\" 😀\n", 5000));
        var result = await fixture.Call(bridge ? "CallTool" : "GetSessionState",
            bridge ? "{\"name\":\"GetSessionState\",\"arguments\":{}}" : "{}");
        Assert.True(((ModelContextProtocol.Protocol.TextContentBlock)result.Content.Single()).Text.Length <= 20000);
        var first = result.StructuredContent!.AsObject();
        Assert.Equal("GetSessionState", (string?)first["meta"]!["tool"]);
        Assert.Equal("GetExportContent", (string?)first["data"]!["continuation"]!["name"]);
        var text = new StringBuilder((string)first["data"]!["text"]!);
        string id = (string)first["data"]!["export"]!["id"]!;
        JsonNode page = first;
        while (!(bool)page["meta"]!["paging"]!["complete"]!)
        {
            int offset = (int)page["meta"]!["paging"]!["nextOffset"]!;
            var args = new JsonObject { ["exportId"] = id, ["offset"] = offset };
            var next = await fixture.Call("CallTool", new JsonObject { ["name"] = "GetExportContent", ["arguments"] = args }.ToJsonString());
            Assert.True(((ModelContextProtocol.Protocol.TextContentBlock)next.Content.Single()).Text.Length <= 20000);
            page = next.StructuredContent!;
            Assert.Equal(id, (string?)page["data"]!["export"]!["id"]);
            text.Append((string?)page["data"]!["text"]);
        }
        Assert.Equal(fixture.Worker.Payload, (string?)JsonNode.Parse(text.ToString())!["data"]!["payload"]);
        Assert.Equal((string?)first["data"]!["export"]!["sha256"], Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))));
        Assert.Single(fixture.Worker.Calls);
    }
}
