extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using Xunit;

[Collection(BundleSelectionCollection.Name)]
public sealed class ToolHintOnceTests
{
    public static IEnumerable<object[]> Releases => B3HostPortsTests.Releases;

    [Theory, MemberData(nameof(Releases))]
    public async Task Every_description_contains_the_usage_hint_once(string release)
    {
        using var fixture = new DiscoveryFixture(release);
        var tools = fixture.Tools;
        foreach (var tool in tools)
            Assert.Single(Regex.Matches(tool.ProtocolTool.Description ?? "", "Examples: GetToolUsage"));
        for (int offset = 0; offset < tools.Count; offset += 17)
        {
            var found = await fixture.Call("FindTools", new { offset, limit = 17 });
            var lines = found["data"]!["items"]!.AsArray();
            for (int i = 1; i < lines.Count; i += 3)
                Assert.Single(Regex.Matches((string)lines[i]!, "Examples: GetToolUsage"));
        }
    }

    internal static async Task<JsonNode> Invoke(McpServerTool tool, object args)
    {
        var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, WorkerRobustnessTests.ServerProxy>()) {
            Params = new() { Name = tool.ProtocolTool.Name,
                Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args)) } };
        return (await tool.InvokeAsync(request)).StructuredContent!;
    }
}

internal sealed class DiscoveryFixture : IDisposable
{
    private readonly HardwareAddressingAdmissionTests.UnboundWorker worker = new();
    private readonly enginehost::TiaMcp.FoundationHost.EngineHostPipeline pipeline;
    private readonly string previous = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey;
    internal IReadOnlyList<McpServerTool> Tools => pipeline.Tools.ToArray();
    internal DiscoveryFixture(string release)
    {
        enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = release;
        var shared = LegacyHostToolRegistry.Create(worker, release, false, hostPorts: false);
        pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, shared);
    }
    internal async Task<JsonNode> Call(string name, object args)
    {
        var result = await ToolHintOnceTests.Invoke(Tools.Single(t => t.ProtocolTool.Name == name), args);
        if (result["data"]?["export"]?["id"] is not JsonNode id) return result;
        var text = new System.Text.StringBuilder((string?)result["data"]?["text"]);
        int? next = (int?)result["meta"]?["paging"]?["nextOffset"];
        while (next != null)
        {
            var page = await ToolHintOnceTests.Invoke(Tools.Single(t => t.ProtocolTool.Name == "GetExportContent"),
                new { exportId = (string)id!, offset = next.Value, limit = 20000 });
            text.Append((string?)page["data"]?["text"]);
            next = (int?)page["meta"]?["paging"]?["nextOffset"];
        }
        return JsonNode.Parse(text.ToString())!;
    }

    public void Dispose()
    {
        pipeline.Dispose(); worker.Dispose();
        enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = previous;
    }
}
