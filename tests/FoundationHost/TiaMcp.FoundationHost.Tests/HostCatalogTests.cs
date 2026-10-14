extern alias enginehost;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaOpenness.Shared;
using Xunit;
using Catalog = enginehost::TiaMcp.FoundationHost.FoundationCatalog;
using Export = enginehost::TiaMcp.FoundationHost.EngineCatalog;
using Data = enginehost::TiaMcp.FoundationHost.ReleaseCatalogData;

namespace TiaMcp.FoundationHost.Tests;

public sealed class HostCatalogTests
{
    [Fact]
    public void Presentation_data_covers_exactly_the_eight_releases()
        => Assert.Equal(new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }, Data.Releases.Keys);

    [Theory]
    [InlineData("14sp1", true), InlineData("15.1", true), InlineData("16", true), InlineData("17", true)]
    [InlineData("18", true), InlineData("19", true), InlineData("20", false), InlineData("21", false)]
    public void Catalog_preserves_the_existing_internal_name_lookup(string release, bool ignoreCase)
    {
        var (host, _, _) = Build(release);
        Assert.Equal(ignoreCase, host.Find("gettoolusage") != null);
        Assert.NotNull(host.Find("GetToolUsage"));
    }

    [Theory, InlineData("20"), InlineData("21")]
    public void Host_catalog_matches_every_frozen_export_field_and_order(string release)
    {
        var (host, shared, essentials) = Build(release);
        var root = Read(release);
        new Export(root, (string)root["workerSha256"]!, release).CrossCheck(host, shared, essentials);
    }

    [Theory, InlineData("20"), InlineData("21")]
    public void Example_profile_data_preserves_the_original_registration_mapping(string release)
    {
        var (_, shared, _) = Build(release);
        var foundationNames = shared.Select(tool => tool.ProtocolTool.Name)
            .Where(name => !PortedFamilies.IsMigratedDeclaration(name) || PortedFamilies.UsesFoundationCandidate(name))
            .ToHashSet(StringComparer.Ordinal);
        foreach (string name in Data.Releases[release].ExampleProfiles.Keys.Concat(new[] { "", "UnknownTool", "gettoolusage" }))
            Assert.Equal(foundationNames.Contains(name) ? "plc-foundation" : "full-engine", Data.Releases[release].ExampleProfile(name));
    }

    [Theory, InlineData("20"), InlineData("21")]
    public void Changed_export_instructions_reports_the_field(string release)
    {
        var (host, shared, essentials) = Build(release);
        var root = Read(release);
        root["serverInstructions"] = "Changed export.";
        var error = Assert.Throws<InvalidDataException>(() =>
            new Export(root, (string)root["workerSha256"]!, release).CrossCheck(host, shared, essentials));
        Assert.Contains("serverInstructions", error.Message);
    }

    [Theory, InlineData("20"), InlineData("21")]
    public void Changed_host_description_reports_the_field(string release)
    {
        var (host, shared, essentials) = Build(release);
        host.All["FindTools"].Tool.Description = "Changed host description.";
        var root = Read(release);
        var error = Assert.Throws<InvalidDataException>(() =>
            new Export(root, (string)root["workerSha256"]!, release).CrossCheck(host, shared, essentials));
        Assert.Contains("FindTools.tool.description", error.Message);
    }

    private static JsonObject Read(string release)
    {
        string repo = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        return JsonNode.Parse(File.ReadAllText(Path.Combine(repo,
            "tests/FoundationHost/TiaMcp.EngineHost.Tests/Fixtures/EngineCatalog" + release + ".json")))!.AsObject();
    }

    private static (Catalog Host, IReadOnlyList<McpServerTool> Shared, ISet<string> Essentials) Build(string release)
    {
        enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = release;
        using var worker = new MetadataWorker();
        var shared = LegacyHostToolRegistry.Create(worker, release, false, hostPorts: false);
        var essentials = LegacyHostToolRegistry.Create(worker, "19", false, hostPorts: false)
            .Select(tool => tool.ProtocolTool.Name).Where(name => !PortedFamilies.IsMigratedDeclaration(name)
                || PortedFamilies.UsesFoundationCandidate(name)).ToHashSet(StringComparer.Ordinal);
        essentials.UnionWith(shared.Select(tool => tool.ProtocolTool.Name)
            .Where(name => PortedFamilies.Contains(name) && PortedFamilies.ForTool(name).Name == "F00"));
        essentials.IntersectWith(ToolUsageCatalog.ProfileEntries(release)
            .Where(row => row!["profiles"]!.AsArray().Any(profile => (string?)profile == "lite"))
            .Select(row => (string)row!["currentName"]!));
        return (new Catalog(release, shared), shared, essentials);
    }

    private sealed class MetadataWorker : IFoundationWorker
    {
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Catalog construction must not dispatch a worker call.");
        public void Dispose() { }
    }
}
