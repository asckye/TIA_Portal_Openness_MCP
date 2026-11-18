extern alias toolpipeline;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Port = toolpipeline::TiaMcpServer.Siemens.Services.OptionalEngineeringPort;
using Service = toolpipeline::TiaMcpServer.Siemens.Services.SivarcService;
using Xunit;

public sealed class SivarcGuardTests
{
    [Fact]
    public void Layout_import_preserves_worker_refusal_condition_parameter_and_mutation_evidence()
    {
        using var scope = Host.HardwareContract.UseRelease("21");
        int calls = 0;
        var service = new Service((operation, arguments) => {
            calls++;
            Assert.Equal("sivarc.ExecuteSivarc", operation);
            Assert.False((bool?)arguments["dryRun"]);
            Assert.False((bool?)arguments["request"]!["AllowRemoveScreenItems"]);
            return new JsonObject { ["Message"] = "Screen items would be removed.", ["Meta"] = new JsonObject {
                ["status"] = "PreconditionFailed", ["precondition"] = "screen-items-would-be-removed", ["parameter"] = "allowRemoveScreenItems",
                ["refusal"] = "Screen items would be removed.", ["operationSuccess"] = false, ["mayHaveChanged"] = false } };
        }, () => "fixture.ap21");
        var tools = new Host.SivarcTools(service);
        string file = Path.Combine(Path.GetTempPath(), "G.yml");
        var refused = tools.ManageSivarcScreenLayoutV4("HMI", "G", "import", file, dryRun: false).StructuredContent!;
        Assert.Equal("PRECONDITION_FAILED", (string?)refused["error"]!["code"]);
        Assert.Equal("screen-items-would-be-removed", (string?)refused["error"]!["details"]!["condition"]);
        Assert.Equal("allowRemoveScreenItems", (string?)refused["error"]!["details"]!["parameter"]);
        Assert.Equal("rejected-before-operation", (string?)refused["meta"]!["outcome"]);
        Assert.False((bool?)refused["data"]!["evidence"]!["mayHaveChanged"]);
        var invalid = tools.ManageSivarcScreenLayoutV4("HMI", "G", "export", file, allowRemoveScreenItems: true).StructuredContent!;
        Assert.Equal("INVALID_ARGUMENT", (string?)invalid["error"]!["code"]);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Layout_import_consent_is_forwarded_on_the_preview_without_a_write()
    {
        using var scope = Host.HardwareContract.UseRelease("21");
        var service = new Service((_, arguments) => {
            Assert.True((bool?)arguments["request"]!["AllowRemoveScreenItems"]);
            Assert.False((bool?)arguments["request"]!["IsWrite"]);
            Assert.True((bool?)arguments["dryRun"]);
            return new JsonObject { ["Message"] = "Preview", ["Meta"] = new JsonObject { ["operationSuccess"] = true } };
        }, () => throw new InvalidOperationException("Preview requested write identity."));
        var result = new Host.SivarcTools(service).ManageSivarcScreenLayoutV4("HMI", "G", "import", Path.Combine(Path.GetTempPath(), "G.yml"),
            allowRemoveScreenItems: true);
        Assert.True((bool?)result.StructuredContent!["ok"], result.StructuredContent.ToJsonString());
    }

    [Fact]
    public void Layout_export_defaults_to_a_read_only_preview_in_the_host_envelope()
    {
        using var scope = Host.HardwareContract.UseRelease("21");
        var service = new Service((_, arguments) => {
            Assert.True((bool?)arguments["dryRun"]);
            Assert.False((bool?)arguments["request"]!["IsWrite"]);
            return new JsonObject { ["Message"] = "SiVArc layout export preview; no file written.", ["Meta"] = new JsonObject {
                ["dryRun"] = true, ["mayHaveChanged"] = false, ["operationSuccess"] = true } };
        }, () => throw new InvalidOperationException("Preview requested write identity."));
        var result = new Host.SivarcTools(service).ManageSivarcScreenLayoutV4("HMI", "G", "export", Path.Combine(Path.GetTempPath(), "G.yml"));
        var body = result.StructuredContent!;
        Assert.True((bool?)body["ok"], body.ToJsonString());
        Assert.Equal("read-only", (string?)body["meta"]!["execution"]);
        Assert.Null(body["data"]!["evidence"]!["mayHaveWrittenFiles"]);
    }

    public static IEnumerable<object[]> V20DefinitionCases()
    {
        foreach (string block in new[] { "Folder/FB_1", "Folder/FC_1", "Folder/DB_1", "Main_OB" })
        {
            yield return new object[] { block, "list", true };
            foreach (string action in new[] { "read", "create", "update", "delete" })
                foreach (bool dryRun in new[] { true, false })
                    yield return new object[] { block, action, dryRun };
        }
    }

    [Theory, MemberData(nameof(V20DefinitionCases))]
    public void V20_definitions_are_refused_before_worker_dispatch(string block, string action, bool dryRun)
    {
        using var scope = Host.HardwareContract.UseRelease("20");
        int calls = 0;
        var service = new Service((_, _) => {
            calls++;
            return new JsonObject { ["Message"] = "worker dispatched", ["Meta"] = new JsonObject() };
        }, () => "fixture.ap20");

        var reply = action == "list" ? service.ReadSivarcBlockDefinitions("PLC", block)
            : service.ManageSivarcBlockDefinition("PLC", block, "tagDefinition", "Tag_1", action,
                confirmDelete: true, dryRun: dryRun);

        Assert.Equal(0, calls);
        Assert.Contains("TIA Portal V20", reply.Message);
        Assert.Contains("crash", reply.Message);
        Assert.Contains("V21", reply.Message);
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)reply.Meta!["v4Rejection"]);
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)reply.Meta["rejectionCode"]);
        Assert.True((bool?)reply.Meta["operationNotStarted"]);
        Assert.False((bool?)reply.Meta["writeOutcomeUnknown"]);
        Assert.False((bool?)reply.Meta["mayHaveChanged"]);
        Assert.False((bool?)reply.Meta["mayHaveWrittenFiles"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V20_read_refusal_keeps_the_V4_outcome_known(bool manage)
    {
        using var scope = Host.HardwareContract.UseRelease("20");
        int calls = 0;
        var service = new Service((_, _) => {
            calls++;
            return new JsonObject { ["Message"] = "worker dispatched", ["Meta"] = new JsonObject() };
        }, () => "fixture.ap20");
        var tools = new Host.SivarcTools(service);

        var result = manage ? tools.ManageSivarcBlockDefinitionV4("PLC", "Folder/FB_1", "tagDefinition", "Tag_1")
            : tools.ListSivarcBlockDefinitionsV4("PLC", "Folder/FB_1");
        var body = result.StructuredContent!.AsObject();

        Assert.Equal(0, calls);
        Assert.True(result.IsError);
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)body["error"]!["code"]);
        Assert.Contains("TIA Portal V20", (string?)body["error"]!["message"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]!["outcome"]);
        Assert.False((bool?)body["data"]!["evidence"]!["writeOutcomeUnknown"]);
    }

    [Theory]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("21")]
    public void Other_releases_still_dispatch_FC_definition_reads(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var calls = new List<string>();
        var service = new Service((operation, arguments) => {
            Assert.Equal("sivarc.ExecuteSivarc", operation);
            Assert.Equal("Folder/FC_1", (string?)arguments["request"]!["BlockPath"]);
            Assert.False((bool?)arguments["request"]!["IsWrite"]);
            calls.Add((string)arguments["request"]!["Operation"]!);
            return new JsonObject { ["Message"] = "worker dispatched", ["Meta"] = new JsonObject() };
        }, () => "fixture.ap" + release);

        Assert.Equal("worker dispatched", service.ReadSivarcBlockDefinitions("PLC", "Folder/FC_1").Message);
        Assert.Equal("worker dispatched", service.ManageSivarcBlockDefinition("PLC", "Folder/FC_1", "tagDefinition", "Tag_1").Message);
        Assert.Equal(new[] { "ListSivarcBlockDefinitions", "ManageSivarcBlockDefinition" }, calls);
    }

    public static IEnumerable<object[]> V20DependentCases()
    {
        foreach (string operation in new[] { "ResolveSivarcExpression", "UpgradeSivarcDefinitions" })
            foreach (string action in new[] { "", "read", "create", "update", "delete", "checkValidity", "checkTraceValidity", "other" })
                foreach (bool dryRun in new[] { true, false })
                    yield return new object[] { operation, action, dryRun };
    }

    [Theory, MemberData(nameof(V20DependentCases))]
    public void V20_dependent_operations_refuse_every_action_and_preview(string operation, string action, bool dryRun)
    {
        using var scope = Host.HardwareContract.UseRelease("20");
        int calls = 0;
        var port = new Port((_, _) => {
            calls++;
            return new JsonObject { ["Message"] = "worker dispatched", ["Meta"] = new JsonObject() };
        }, () => "fixture.ap20");
        var request = new OptionalEngineeringRequest {
            Operation = operation, Action = action, DryRun = dryRun, SoftwarePath = "PLC", BlockPath = "FC_1"
        };

        Assert.Equal(OptionalEngineeringCapabilities.SivarcBlockDefinitionProblem("20"),
            OptionalEngineeringCapabilities.Unsupported("20", request));
        var reply = port.Invoke("sivarc.ExecuteSivarc", request);

        Assert.Equal(0, calls);
        Assert.Contains("TIA Portal V20", reply.Message);
        Assert.Contains("crash", reply.Message);
        Assert.Contains("V21", reply.Message);
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)reply.Meta!["v4Rejection"]);
        Assert.True((bool?)reply.Meta["operationNotStarted"]);
        Assert.False((bool?)reply.Meta["writeOutcomeUnknown"]);
        Assert.False((bool?)reply.Meta["mayHaveChanged"]);
    }

    [Theory]
    [InlineData("20", true)]
    [InlineData("20", false)]
    [InlineData("21", true)]
    [InlineData("21", false)]
    public void Dependent_service_entries_use_the_release_guard(string release, bool dryRun)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var calls = new List<string>();
        var service = new Service((module, arguments) => {
            Assert.Equal("sivarc.ExecuteSivarc", module);
            string operation = (string)arguments["request"]!["Operation"]!;
            Assert.Equal(operation == "UpgradeSivarcDefinitions" && !dryRun, (bool?)arguments["request"]!["IsWrite"]);
            calls.Add(operation);
            return new JsonObject { ["Message"] = "worker dispatched", ["Meta"] = new JsonObject() };
        }, () => "fixture.ap" + release);

        var expression = service.ResolveSivarcExpression("PLC", "FC_1", "[\"HMI\"]", "[\"Runtime\"]",
            "masterCopy", "Screen_1", "Block.DB.SymbolicName");
        var upgrade = service.UpgradeSivarcDefinitions("PLC", dryRun);
        if (release == "21")
        {
            Assert.Equal(new[] { "ResolveSivarcExpression", "UpgradeSivarcDefinitions" }, calls);
            Assert.Equal("worker dispatched", expression.Message);
            Assert.Equal("worker dispatched", upgrade.Message);
        }
        else
        {
            Assert.Empty(calls);
            foreach (var reply in new[] { expression, upgrade })
            {
                Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)reply.Meta!["rejectionCode"]);
                Assert.False((bool?)reply.Meta["writeOutcomeUnknown"]);
            }
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void V20_dependent_tool_refusals_keep_the_V4_outcome_known(bool upgrade, bool dryRun)
    {
        using var scope = Host.HardwareContract.UseRelease("20");
        int calls = 0;
        var service = new Service((_, _) => {
            calls++;
            return new JsonObject { ["Message"] = "worker dispatched", ["Meta"] = new JsonObject() };
        }, () => "fixture.ap20");
        var tools = new Host.SivarcTools(service);
        var result = upgrade ? tools.UpgradeSivarcDefinitionsV4("PLC", dryRun)
            : tools.ResolveSivarcExpressionV4("PLC", "FC_1", new[] { "HMI" }, new[] { "Runtime" },
                "masterCopy", "Screen_1", "Block.DB.SymbolicName");
        var body = result.StructuredContent!.AsObject();

        Assert.Equal(0, calls);
        Assert.True(result.IsError);
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)body["error"]!["code"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]!["outcome"]);
        Assert.False((bool?)body["data"]!["evidence"]!["writeOutcomeUnknown"]);
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Served_definition_examples_use_root_code_blocks(string release)
    {
        using var scope = release is "20" or "21" ? EngineHostParity.EnterScope() : null;
        var worker = new FakeWorker();
        var roster = LegacyHostToolRegistry.Create(worker, release, true);
        var server = DispatchProxy.Create<IMcpServer, ServerProxy>();
        var guide = roster.Single(tool => tool.ProtocolTool.Name == "GetToolUsage");
        foreach (string name in new[] { "ListSivarcBlockDefinitions", "ManageSivarcBlockDefinition" })
        {
            Assert.Equal(PortedFamilies.Available(release, name), roster.Any(tool => tool.ProtocolTool.Name == name));
            if (!PortedFamilies.Available(release, name)) continue;
            var result = await guide.InvokeAsync(new RequestContext<CallToolRequestParams>(server) {
                Params = new CallToolRequestParams {
                    Name = "GetToolUsage", Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                        JsonSerializer.Serialize(new { toolName = name }))
                }
            });
            Assert.False(result.IsError);
            var usage = result.StructuredContent!["data"]!;
            Assert.Equal("FC_1", (string?)usage["example"]!["request"]!["params"]!["arguments"]!["blockPath"]);
            string guidance = (string)usage["inputSchema"]!["properties"]!["blockPath"]!["description"]!;
            Assert.Contains("Group/Subgroup/Name", guidance);
            Assert.Contains("GetSoftwareTree", guidance);
            Assert.Contains("root block", guidance);
            Assert.Contains("V20", guidance);
        }
        Assert.Equal(0, worker.Calls);
    }
}
