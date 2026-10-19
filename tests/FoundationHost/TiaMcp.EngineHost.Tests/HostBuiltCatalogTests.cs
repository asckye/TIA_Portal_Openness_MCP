extern alias foundationhost;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using TiaMcpServer.ModelContextProtocol;
using Xunit;
using Foundation = foundationhost::TiaMcp.FoundationHost;

public sealed class HostBuiltCatalogTests
{
    [Theory, InlineData("20"), InlineData("21")]
    public void Host_built_catalog_matches_every_frozen_engine_descriptor(string release)
    {
        EngineHostConfiguration.ReleaseKey = release;
        using var worker = new MetadataWorker();
        var shared = Foundation.LegacyHostToolRegistry.Create(worker, release, false, hostPorts: false);
        var host = new FoundationCatalog(release, shared);
        var frozen = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "EngineCatalog" + release + ".json")))!.AsObject();
        var source = new EngineCatalog(frozen, (string)frozen["workerSha256"]!, release);
        source.CrossCheck(host, shared, host.Lite.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal));
        Assert.Equal(source.Instructions, host.Instructions);
        Assert.True(JsonNode.DeepEquals(source.BehaviorCapabilities, host.BehaviorCapabilities));
    }
    private sealed class MetadataWorker : Foundation.IFoundationWorker
    {
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
            => throw new InvalidOperationException("Building the catalog must not dispatch a worker operation.");
        public void Dispose() { }
    }
}
