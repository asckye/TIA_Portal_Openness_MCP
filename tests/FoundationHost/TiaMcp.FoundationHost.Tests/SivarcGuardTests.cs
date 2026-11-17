extern alias toolpipeline;
using System.Text.Json.Nodes;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Service = toolpipeline::TiaMcpServer.Siemens.Services.SivarcService;
using Xunit;

public sealed class SivarcGuardTests
{
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
}
