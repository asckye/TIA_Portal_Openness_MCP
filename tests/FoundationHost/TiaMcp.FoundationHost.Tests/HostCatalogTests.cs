extern alias foundationhost;
extern alias enginehost;
using System.Text.Json.Nodes;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaOpenness.Shared;
using Xunit;
using Catalog = enginehost::TiaMcp.FoundationHost.FoundationCatalog;
using Data = enginehost::TiaMcp.FoundationHost.ReleaseCatalogData;

namespace TiaMcp.FoundationHost.Tests;

public sealed class HostCatalogTests
{
    [Fact]
    public void Presentation_data_covers_exactly_the_eight_releases()
        => Assert.Equal(new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }, Data.Releases.Keys);

    [Theory]
    [InlineData("14sp1", false), InlineData("15.1", false), InlineData("16", false), InlineData("17", false)]
    [InlineData("18", false), InlineData("19", false), InlineData("20", false), InlineData("21", false)]
    public void Catalog_uses_ordinal_name_lookup_on_every_release(string release, bool ignoreCase)
    {
        var (host, _, _) = Build(release);
        Assert.Equal(ignoreCase, host.Find("gettoolusage") != null);
        Assert.NotNull(host.Find("GetToolUsage"));
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"),
            InlineData("21")]
    public void Example_profile_is_the_same_on_every_release(string release)
    {
        var (host, _, _) = Build(release);
        foreach (string name in host.All.Keys.Concat(new[] { "", "UnknownTool", "gettoolusage" }))
            Assert.Equal("plc-foundation", Data.Releases[release].ExampleProfile(name));
    }




    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"),
            InlineData("21")]
    public void Every_release_has_the_same_presentation_policy_and_exact_generated_lite_roster(string release)
    {
        var data = Data.Releases[release];
        var reference = Data.Releases["21"];
        foreach (var property in new[] { "Instructions", "ProfileName", "HasLite", "RegisterDiscoveryHandlers", "CatalogCallDiscipline",
            "HasManifestCatalog", "RetainUnavailable", "UseDeclaredDiscovery", "NameComparer", "DefaultExampleProfile" })
        {
            var member = typeof(Data).GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            Assert.Equal(member.GetValue(reference), member.GetValue(data));
        }
        Assert.Equal(reference.ManifestSupplementaryReleases, data.ManifestSupplementaryReleases);
        Assert.Equal(reference.ManifestReturnTypes.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            data.ManifestReturnTypes.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.Equal(reference.ExampleProfiles.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            data.ExampleProfiles.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        var (host, _, _) = Build(release);
        Assert.Equal(host.All.Keys.OrderBy(name => name, StringComparer.Ordinal), host.All.Keys);
        Assert.Equal(host.IncludingUnavailable.Keys.OrderBy(name => name, StringComparer.Ordinal), host.IncludingUnavailable.Keys);
        Assert.Equal(data.LiteNames.OrderBy(name => name, StringComparer.Ordinal), host.Lite.Select(tool => tool.Name));
        Assert.All(host.Lite, tool => Assert.True(host.All.ContainsKey(tool.Name)));
        Assert.NotEmpty(host.Lite);
        Assert.True(host.Lite.Count < host.All.Count);
        var catalog = foundationhost::TiaMcp.FoundationHost.SharedReleaseHost.CatalogDocument(release,
            host.All.Values.Select(tool => tool.Tool), host.BehaviorCapabilities, new JsonObject());
        Assert.Equal(new[] { "behaviorCapabilities", "callDiscipline", "profile", "releaseKey", "tools" },
            catalog.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal("plc-foundation", (string?)catalog["profile"]);
        Assert.Equal(host.All.Keys, catalog["tools"]!.AsArray().Select(tool => (string)tool!["name"]!));
        Assert.Contains("FindTools", data.Instructions);
        Assert.Contains("CallTool", data.Instructions);
        Assert.Contains("InitializeEnvironment", data.Instructions);
        Assert.Equal("lite", foundationhost::TiaMcp.FoundationHost.HostOptions.Parse(new[] { "--release-key", release }, AppContext.BaseDirectory).Profile);
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"),
            InlineData("21")]
    public void Prompt_registration_is_exactly_the_dependency_complete_subset_of_one_inventory(string release)
    {
        var universe = Data.Releases.Keys.SelectMany(key => ToolUsageCatalog.ProfileEntries(key))
            .Select(row => (string)row!["currentName"]!).ToHashSet(StringComparer.Ordinal);
        var (host, _, _) = Build(release);
        var requirements = enginehost::TiaMcpServer.ModelContextProtocol.McpPromptRegistration.Requirements(universe);
        Assert.Equal(30, requirements.Count);
        Assert.All(requirements.Values, tools => Assert.Contains("GetToolUsage", tools));
        var services = new ServiceCollection();
        enginehost::TiaMcpServer.ModelContextProtocol.McpPromptRegistration.Configure(services.AddMcpServer(), host.All.Keys, universe);
        using var provider = services.BuildServiceProvider();
        var actual = provider.GetServices<McpServerPrompt>().Select(prompt => prompt.ProtocolPrompt.Name).OrderBy(name => name, StringComparer.Ordinal);
        var expected = requirements.Where(pair => pair.Value.All(host.All.ContainsKey)).Select(pair => pair.Key).OrderBy(name => name, StringComparer.Ordinal);
        Assert.Equal(expected, actual);
        Assert.Contains("Connect", actual);
        Assert.Equal(host.All.ContainsKey("ExportPlcBlocksDocuments"), actual.Contains("ExportBlocksAsDocuments"));
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"),
            InlineData("21")]
    public void Maintained_examples_keep_the_served_parameter_set_and_one_profile(string release)
    {
        var (host, _, _) = Build(release);
        foreach (var descriptor in host.All.Values)
        {
            var usage = ToolUsageCatalog.Describe(descriptor.Name, release, "plc-foundation", descriptor.Tool.Description ?? "",
                JsonNode.Parse(descriptor.Tool.InputSchema.GetRawText())!.AsObject());
            Assert.Equal("plc-foundation", (string?)usage["profile"]);
            var arguments = usage["example"]!["request"]!["params"]!["arguments"]!.AsObject();
            Assert
                .All(arguments, argument => Assert
                    .True(descriptor.Tool.InputSchema.GetProperty("properties").TryGetProperty(argument.Key, out _), release + "/" + descriptor.Name + "/"
                        + argument.Key));
            if (descriptor.Tool.InputSchema.TryGetProperty("required", out var requiredParameters))
                foreach (var required in requiredParameters.EnumerateArray())
                    Assert.True(arguments.ContainsKey(required.GetString()!));
        }
    }

    [Fact]
    public void Identical_tool_schemas_select_identical_maintained_example_arguments()
    {
        var examples = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var release in Data.Releases.Keys)
        {
            var (host, _, _) = Build(release);
            var rows = ToolUsageCatalog.ProfileEntries(release).ToDictionary(row => (string)row!["currentName"]!);
            foreach (var descriptor in host.All.Values)
            {
                string identity = descriptor.Name + "\n" + descriptor.Tool.InputSchema.GetRawText();
                string arguments = rows[descriptor.Name]!["arguments"]!.ToJsonString();
                if (examples.TryGetValue(identity, out var existing)) Assert.Equal(existing, arguments);
                else examples.Add(identity, arguments);
            }
        }
    }

    [Fact]
    public void Release_identity_has_one_host_kind_and_no_retired_presentation_properties()
    {
        var type = typeof(TiaMcp.Versioning.TiaVersionDescriptor);
        foreach (var property in new[] { "IsFullEngine", "SupportState", "WorkerKind" }) Assert.Null(type.GetProperty(property));
        foreach (var release in TiaMcp.Versioning.TiaVersionCatalog.All)
        {
            Assert.Equal("foundation", release.HostKind);
            Assert.Equal("v" + release.Key, release.RuntimeDirectory);
            Assert.Equal(release.Key is "20" or "21", release.EngineOutputDirectory != null);
        }
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"),
        InlineData("20"), InlineData("21")]
    public void Catalog_preserves_every_master_input_and_output_schema(string release)
    {
        string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        var baseline = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "manifest/contracts/v4/baseline", release + ".json")))!;
        var (host, _, _) = Build(release);
        Assert.Equal(baseline["tools"]!.AsArray().Select(tool => (string)tool!["name"]!).Order(StringComparer.Ordinal),
            host.All.Keys.Order(StringComparer.Ordinal));
        foreach (var row in baseline["tools"]!.AsArray())
        {
            string name = (string)row!["name"]!;
            var tool = host.All[name].Tool;
            Assert.True(JsonNode.DeepEquals(row["inputSchema"], JsonNode.Parse(tool.InputSchema.GetRawText())), release + "/" + name + "/input");
            string? hash = (string?)row["outputSchemaSha256"];
            var expectedOutput = hash == null ? null : baseline["schemas"]![hash];
            var output = tool.OutputSchema.HasValue ? JsonNode.Parse(tool.OutputSchema.Value.GetRawText()) : null;
            Assert.True(JsonNode.DeepEquals(expectedOutput, output), release + "/" + name + "/output");
            string descriptionHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(tool.Description ?? ""))).ToLowerInvariant();
            Assert.True((string?)row["descriptionSha256"] == descriptionHash, release + "/" + name + "/description");

        }
    }

    private static (Catalog Host, IReadOnlyList<McpServerTool> Shared, ISet<string> Essentials) Build(string release)
    {
        enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = release;
        using var worker = new MetadataWorker();
        var shared = LegacyHostToolRegistry.Create(worker, release, false, hostPorts: false);
        var essentials = ToolUsageCatalog.ProfileEntries(release)
            .Where(row => row!["profiles"]!.AsArray().Any(profile => (string?)profile == "lite"))
            .Select(row => (string)row!["currentName"]!).ToHashSet(StringComparer.Ordinal);
        return (new Catalog(release, shared), shared, essentials);
    }

    private sealed class MetadataWorker : IFoundationWorker
    {
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Catalog construction must not dispatch a worker call.");
        public void Dispose() { }
    }
}
