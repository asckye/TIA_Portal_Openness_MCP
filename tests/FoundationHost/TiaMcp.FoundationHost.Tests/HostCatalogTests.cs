extern alias foundationhost;
extern alias toolpipeline;
using System.Text.Json.Nodes;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaOpenness.Shared;
using Xunit;
using Catalog = toolpipeline::TiaMcp.FoundationHost.FoundationCatalog;
using Data = toolpipeline::TiaMcp.FoundationHost.ReleaseCatalogData;

namespace TiaMcp.FoundationHost.Tests;

public sealed class HostCatalogTests : IDisposable
{
    private readonly string previousRelease = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey;
    public void Dispose() => toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = previousRelease;

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17")]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void All_release_catalog_policy_fields_have_one_value(string release)
    {
        foreach (var field in typeof(Data).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            Assert.Equal(field.GetValue(Data.Releases["14sp1"]), field.GetValue(Data.Releases[release]));
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"),
        InlineData("20"), InlineData("21")]
    public void Whole_roster_runtime_disclosure_and_description_agree_in_both_directions(string release)
    {
        var (host, _, _) = Build(release);
        foreach (var descriptor in host.All.Values)
        foreach (bool suppliedCurrent in new[] { false, true })
        {
            var result = FoundationV4Result.Reject(release, descriptor.Name, "roster-disclosure",
                FoundationV4Result.Invalid("fixture"), suppliedCurrent);
            bool current = (string?)result.StructuredContent?["meta"]?["behaviorPolicy"] == "current";
            Assert.Equal(descriptor.Tool.Description!.Contains(TiaMcp.Logic.V4.BehaviorCapabilities.CurrentDisclosure, StringComparison.Ordinal), current);
            Assert.Equal(current, result.StructuredContent!["meta"]!["warnings"]!.AsArray()
                .Any(warning => (string?)warning?["code"] == "UNVERIFIED_BEHAVIOR"));
        }
        using var worker = new MetadataWorker();
        var raw = FoundationTools.Create(worker, release).Concat(PortedToolContract.Create(worker, release))
            .Append(PlcCrossReferenceAnalysis.Create(worker, release));
        foreach (var foundation in raw.Cast<FoundationTool>().Where(tool => host.Find(tool.ProtocolTool.Name) != null))
        {
            var policy = TiaMcp.Logic.V4.BehaviorCapabilities.EntryPolicy(typeof(HostCatalogTests).Assembly, release,
                foundation.ProtocolTool.Name, TiaMcp.Logic.V4.BehaviorPolicy.NotApplicable);
            Assert.Equal(foundation.IsNative, policy == TiaMcp.Logic.V4.BehaviorPolicy.Current);
        }
        foreach (var offline in OfflineXmlTools.Create().Concat(OfflineCompositionTools.Create()).Concat(OfflineBlockCompositionTools.Create())
            .Concat(OfflineLadderTools.Create()).Concat(OfflineSymbolManifestTools.Create()).Where(tool => host.Find(tool.ProtocolTool.Name) != null))
            Assert.Equal(TiaMcp.Logic.V4.BehaviorPolicy.NotApplicable,
                TiaMcp.Logic.V4.BehaviorCapabilities.EntryPolicy(typeof(HostCatalogTests).Assembly, release,
                    offline.ProtocolTool.Name, TiaMcp.Logic.V4.BehaviorPolicy.NotApplicable));
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"),
        InlineData("20"), InlineData("21")]
    public void Shared_roster_and_catalog_both_publish_the_ledger_disclosure_suffix(string release)
    {
        var (host, shared, _) = Build(release);
        int current = 0;
        foreach (var tool in shared.Select(implementation => implementation.ProtocolTool).Concat(host.All.Values.Select(row => row.Tool)))
        {
            var policy = TiaMcp.Logic.V4.BehaviorCapabilities.EntryPolicy(typeof(HostCatalogTests).Assembly,
                release, tool.Name, TiaMcp.Logic.V4.BehaviorPolicy.NotApplicable);
            if (policy != TiaMcp.Logic.V4.BehaviorPolicy.Current) continue;
            current++;
            Assert.EndsWith(TiaMcp.Logic.V4.BehaviorCapabilities.CurrentDisclosure, tool.Description, StringComparison.Ordinal);
            Assert.Equal(tool.Description, TiaMcp.Logic.V4.BehaviorCapabilities.Describe(release, tool.Name, tool.Description));
        }
        Assert.True(current > 0);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Secondary_absence_is_exposed_in_v4_data(bool gone)
    {
        var definition = FoundationTools.ForRelease(FoundationTools.Definitions.Single(d => d.Name == "CloseProject"), "21");
        var raw = new JsonObject { ["Executed"] = false, ["ProjectFile"] = @"C:\Reference\Line1.ap21", ["SecondaryProjectGone"] = gone };
        var validated = V17ProjectEnvelope.Wrap("CloseProject", "ProjectMutation", raw, true);
        var body = FoundationV4Result.Worker("21", definition, "secondary-preview", new JsonObject { ["dryRun"] = true }, raw, validated)
            .StructuredContent!;
        Assert.True((bool?)body["ok"]);
        Assert.Equal(gone, (bool?)body["data"]?["secondaryProjectGone"]);
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"),
        InlineData("21")]
    public void Secondary_project_parameters_follow_the_release_api(string release)
    {
        var (host, _, _) = Build(release);
        var open = host.Find("OpenProject")!.Tool.InputSchema.GetProperty("properties");
        var close = host.Find("CloseProject")!.Tool.InputSchema.GetProperty("properties");
        Assert.False(open.TryGetProperty("closeForeignProject", out _));
        Assert.False(host.Find("AttachOpenProject")!.Tool.InputSchema.GetProperty("properties")
            .TryGetProperty("recordedSecondaryProjectFile", out _));
        Assert.False(host.Find("AttachOpenProject")!.Tool.InputSchema.GetProperty("properties")
            .TryGetProperty("recordedSecondaryProcessId", out _));
        Assert.False(host.Find("AttachOpenProject")!.Tool.InputSchema.GetProperty("properties")
            .TryGetProperty("recordedSecondaryProcessStartTicks", out _));
        var connect = host.Find("ConnectPortal")!.Tool.InputSchema.GetProperty("properties");
        Assert.False(connect.TryGetProperty("recordedSecondaryProjectFile", out _));
        Assert.False(connect.TryGetProperty("recordedSecondaryProcessId", out _));
        Assert.False(connect.TryGetProperty("recordedSecondaryProcessStartTicks", out _));
        Assert.Equal(release != "14sp1", open.TryGetProperty("openMode", out var mode));
        Assert.Equal(release != "14sp1", close.TryGetProperty("secondaryProjectFile", out var file));
        if (release == "14sp1") return;
        string passwordDescription = open.GetProperty("umacPassword").GetProperty("description").GetString()!;
        Assert.Contains("vault:", passwordDescription);
        Assert.Contains("SecureString", passwordDescription);
        Assert.Contains("Global (UMC)", passwordDescription);
        Assert.Equal("primary", mode.GetProperty("default").GetString());
        Assert.Equal(new[] { "primary", "secondary" }, mode.GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("", file.GetProperty("default").GetString());
        var descriptor = host.Find("OpenProject")!.Tool;
        var usage = ToolUsageCatalog.Describe("OpenProject", release, "plc-foundation", descriptor.Description!,
            JsonNode.Parse(descriptor.InputSchema.GetRawText())!.AsObject());
        Assert.False((bool?)usage["parameterSources"]?["openMode"]?["requiresBinding"]);
        Assert.Null(usage["parameterSources"]?["openMode"]?["tools"]);
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"),
        InlineData("21")]
    public void Project_sequences_validate_against_the_served_release_schemas(string release)
    {
        var (host, _, _) = Build(release);
        JsonObject? Schema(string name) => host.Find(name) is { } descriptor
            ? JsonNode.Parse(descriptor.Tool.InputSchema.GetRawText())!.AsObject() : null;
        foreach (string id in new[] { "sequence/project-overview", "sequence/secondary-project-compare" })
        {
            var examples = ToolUsageCatalog.Examples(release, "plc-foundation", host.All.Keys, exampleId: id, schemaFor: Schema);
            var sequence = Assert.Single(examples["examples"]!.AsArray())!;
            Assert.Equal(id != "sequence/secondary-project-compare" || release != "14sp1", (bool?)sequence["releaseMatches"]);
            Assert.Equal(id != "sequence/secondary-project-compare" || release != "14sp1", (bool?)sequence["profileMatches"]);
            Assert.Equal(id != "sequence/secondary-project-compare" || release != "14sp1", (bool?)sequence["available"]);
        }
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Hardware_catalog_declares_its_limit_bounds_where_registered(string release)
    {
        var (host, _, _) = Build(release);
        var tool = host.Find("SearchHardwareCatalog");
        if (release is "14sp1" or "15.1" or "16" or "17") { Assert.Null(tool); return; }
        var limit = tool!.Tool.InputSchema.GetProperty("properties").GetProperty("limit");
        Assert.Equal("integer", limit.GetProperty("type").GetString());
        Assert.Equal(50, limit.GetProperty("default").GetInt32());
        Assert.Equal(1, limit.GetProperty("minimum").GetInt32());
        Assert.Equal(100, limit.GetProperty("maximum").GetInt32());
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17")]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Technology_export_is_registered_on_every_release_and_watch_export_keeps_its_api_range(string release)
    {
        var (host, _, _) = Build(release);
        Assert.NotNull(host.Find("ExportTechnologyObject"));
        Assert.Equal(release != "14sp1", host.Find("ExportPlcWatchTable") != null);
        Assert.NotNull(host.Find("SeedProjectFromReference"));
        Assert.DoesNotContain("preview only", host.Find("ExportTechnologyObject")!.Tool.Description!, StringComparison.Ordinal);
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"),
            InlineData("21")]
    public void Plc_xml_exports_declare_optional_volatile_stripping_on_every_release(string release)
    {
        var (host, _, _) = Build(release);
        foreach (string name in new[] { "ExportPlcBlock", "ExportPlcType", "ExportPlcTagTable", "ExportPlcBlocks", "ExportPlcTypes", "ExportPlcTagTables" })
        {
            var schema = host.Find(name)!.Tool.InputSchema;
            var parameter = schema.GetProperty("properties").GetProperty("stripVolatile");
            Assert.Equal("boolean", parameter.GetProperty("type").GetString());
            Assert.False(parameter.GetProperty("default").GetBoolean());
            Assert.DoesNotContain(schema.GetProperty("required").EnumerateArray(), value => value.GetString() == "stripVolatile");
            Assert.Contains("DocumentInfo", parameter.GetProperty("description").GetString());
        }
        foreach (string name in new[] { "ExportPlcBlockDocuments", "ExportPlcWatchTable", "ExportTechnologyObject" })
            if (host.Find(name) is { } tool) Assert.False(tool.Tool.InputSchema.GetProperty("properties").TryGetProperty("stripVolatile", out _));
    }

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
        Assert.Equal(Data.Rosters[release].LiteNames.OrderBy(name => name, StringComparer.Ordinal), host.Lite.Select(tool => tool.Name));
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
        var requirements = toolpipeline::TiaMcpServer.ModelContextProtocol.McpPromptRegistration.Requirements(universe);
        Assert.Equal(30, requirements.Count);
        Assert.All(requirements.Values, tools => Assert.Contains("GetToolUsage", tools));
        var services = new ServiceCollection();
        toolpipeline::TiaMcpServer.ModelContextProtocol.McpPromptRegistration.Configure(services.AddMcpServer(), host.All.Keys, universe);
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
        Assert.Null(type.GetProperty("EngineOutputDirectory"));
        foreach (var release in TiaMcp.Versioning.TiaVersionCatalog.All)
        {
            Assert.Equal("foundation", release.HostKind);
            Assert.Equal("v" + release.Key, release.RuntimeDirectory);
        }
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"),
        InlineData("20"), InlineData("21")]
    public void Catalog_preserves_every_master_input_and_output_schema(string release)
    {
        string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        var baseline = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "manifest/contracts/v4/baseline", release + ".json")))!;
        var (host, _, _) = Build(release);
        Assert.Equal(baseline["tools"]!.AsArray().Select(tool => (string)tool!["name"]!)
            .Append("GetApprovalStatus").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            host.All.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(host.All["GetApprovalStatus"].Tool.InputSchema.GetProperty("properties").EnumerateObject());
        Assert.Equal(host.All["GetCallResult"].Tool.OutputSchema!.Value.GetRawText(),
            host.All["GetApprovalStatus"].Tool.OutputSchema!.Value.GetRawText());
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
        toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = release;
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
