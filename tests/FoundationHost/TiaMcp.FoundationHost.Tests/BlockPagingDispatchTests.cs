using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts.Paging;
using TiaMcp.FoundationHost;
using Xunit;

public sealed class BlockPagingDispatchTests
{
    private sealed class Worker : IFoundationWorker
    {
        internal int Calls;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            Calls++;
            Assert.Equal("plc-paging.ReadBlockPage", operation);
            var paths = Enumerable.Range(0, 130).Select(i => "G%2F1/B" + i.ToString("D3")).ToArray();
            var page = PlcBlockPaging.Plan(paths, "fixture", (int)arguments["limit"]!, (string)arguments["cursor"]!);
            page.Items = paths.Skip(page.Offset).Take((int)arguments["limit"]!).Select(path => new PlcBlockDetails { Path = path, Name = path.Split('/').Last() }).ToArray();
            return Task.FromResult(JsonSerializer.SerializeToNode(page));
        }
        public void Dispose() { }
    }

    [Theory]
    [InlineData("14sp1")] [InlineData("15.1")] [InlineData("16")] [InlineData("17")]
    [InlineData("18")] [InlineData("19")] [InlineData("20")] [InlineData("21")]
    public async Task All_releases_dispatch_pages_and_preserve_exact_paths(string release)
    {
        var worker = new Worker();
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "ListPlcBlocks"), worker), release, () => new(false, 1));
        var found = new List<string>();
        string cursor = "";
        do
        {
            var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>())
            { Params = new() { Name = "ListPlcBlocks", Arguments = new Dictionary<string, JsonElement> {
                ["softwarePath"] = JsonSerializer.SerializeToElement("PLC"), ["limit"] = JsonSerializer.SerializeToElement(20),
                ["cursor"] = JsonSerializer.SerializeToElement(cursor) } } };
            var result = await tool.InvokeAsync(request);
            Assert.False(result.IsError);
            var body = result.StructuredContent!;
            Assert.Equal("cursor", (string?)body["meta"]!["paging"]!["mode"]);
            found.AddRange(body["data"]!["items"]!.AsArray().Select(row => (string)row!["path"]!));
            cursor = (string?)body["meta"]!["paging"]!["nextCursor"] ?? "";
        } while (cursor.Length != 0);
        Assert.Equal(7, worker.Calls);
        Assert.Equal(130, found.Distinct().Count());
        Assert.Equal(found.Order(StringComparer.Ordinal), found);
    }
}
