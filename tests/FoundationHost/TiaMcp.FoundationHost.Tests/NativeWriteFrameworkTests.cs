using System.Text.Json.Nodes;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

public sealed class NativeWriteFrameworkTests
{
    [Theory]
    [InlineData("opc-cleaned", false, 0)]
    [InlineData("opc-retained", true, 1)]
    [InlineData("opc-no-create", false, 0)]
    public void Opc_import_reports_the_verified_cleanup_on_net48(string scenario, bool unknown, int remaining)
    {
        using var input = new Inputs();
        var probe = Run(scenario, input.Nodes);
        Assert.Equal(remaining, (int?)probe["remaining"]);
        Assert.Equal(unknown, (bool?)probe["reply"]?["OutcomeUnknown"]);
        Assert.Equal(unknown, (bool?)probe["reply"]?["Meta"]?["mayHaveChanged"]);
        var message = (string)probe["reply"]!["Message"]!;
        if (scenario == "opc-no-create") Assert.Contains("Create(string)", message);
        else Assert.Contains("OPC UA import interrupted", message);
        Assert.Equal(scenario == "opc-cleaned", message.Contains("removed again"));
    }

    [Theory, InlineData("opc-create-refused", false, 0), InlineData("opc-create-interrupted", true, 1)]
    public void Opc_creation_refusal_requires_a_fresh_unchanged_inventory_on_net48(string scenario, bool unknown, int count)
    {
        using var input = new Inputs();
        var probe = Run(scenario, input.Nodes);
        Assert.Equal(count, (int?)probe["remaining"]);
        Assert.Contains("creation interrupted", (string?)probe["reply"]?["Message"]);
        Assert.Equal(unknown, (bool?)probe["reply"]?["OutcomeUnknown"]);
        Assert.Equal(unknown, (bool?)probe["reply"]?["Meta"]?["mayHaveChanged"]);
    }

    [Theory]
    [InlineData("alarm-class", true)][InlineData("alarm-class-missing", false)]
    [InlineData("alarm-text", true)][InlineData("alarm-text-missing", false)]
    public void Alarm_import_distinguishes_admission_from_provider_failure_on_net48(string scenario, bool issued)
    {
        using var inputs = new Inputs();
        string file = scenario.StartsWith("alarm-class") ? inputs.Classes : inputs.Workbook;
        var probe = Run(scenario, issued ? file : file + ".missing");
        Assert.Equal(issued ? 1 : 0, (int?)probe["calls"]);
        Assert.Equal(issued, (bool?)probe["reply"]?["Meta"]?["mayHaveChanged"]);
        Assert.Equal(issued, (bool?)probe["reply"]?["OutcomeUnknown"]);
    }

    [Theory, InlineData("dyn-create", 0), InlineData("dyn-entry", 1)]
    public void Dynamization_remains_after_the_scalar_is_restored_on_net48(string scenario, int entries)
    {
        var probe = Run(scenario, "");
        Assert.Equal(1, (int?)probe["remaining"]);
        Assert.Equal(0, (int?)probe["offset"]);
        Assert.Equal(entries, (int?)probe["entries"]);
        Assert.True((bool?)probe["meta"]?["writeOutcomeUnknown"], probe.ToJsonString());
        Assert.False((bool?)probe["meta"]?["rollbackCompleted"]);
    }

    [Theory, InlineData("mapping-create"), InlineData("mapping-update")]
    public void Mapping_rule_retains_the_target_after_scalar_rollback_on_net48(string scenario)
    {
        var probe = Run(scenario, "");
        Assert.Equal(1, (int?)probe["remaining"]);
        Assert.Equal(0, (int?)probe["begin"]);
        Assert.Equal("Device/module", (string?)probe["target"]);
        Assert.True((bool?)probe["meta"]?["writeOutcomeUnknown"], probe.ToJsonString());
        Assert.False((bool?)probe["meta"]?["rollbackCompleted"]);
    }

    [Theory, InlineData("dyn-refused"), InlineData("mapping-refused"), InlineData("target-refused")]
    public void Verified_native_refusals_keep_a_definite_outcome_on_net48(string scenario)
    {
        var probe = Run(scenario, "");
        Assert.Equal(scenario == "target-refused" ? 1 : 0, (int?)probe["remaining"]);
        Assert.False((bool?)probe["meta"]?["mayHaveChanged"] ?? false, probe.ToJsonString());
        Assert.False((bool?)probe["meta"]?["writeOutcomeUnknown"] ?? false);
        Assert.Contains("refused", (string?)probe["message"], StringComparison.OrdinalIgnoreCase);
    }

    private static JsonNode Run(string scenario, string file)
        => SafetyLoginFrameworkTests.RunFixture("native-writes", scenario, new JsonObject { ["file"] = file })["probe"]!;

    private sealed class Inputs : IDisposable
    {
        private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("native-import-");
        internal string Nodes => Path.Combine(root.FullName, "Model.xml");
        internal string Classes => Path.Combine(root.FullName, "classes.dat");
        internal string Workbook => Path.Combine(TestPaths.RepositoryRoot(AppContext.BaseDirectory),
            "tests", "FoundationHost", "TiaMcp.ToolPipeline.Tests", "Fixtures", "TableExchange", "inline-text.xlsx");
        internal Inputs()
        {
            File.WriteAllText(Nodes, "<UANodeSet xmlns='http://opcfoundation.org/UA/2011/03/UANodeSet.xsd'/>");
            // The fault is at provider dispatch, before the provider decodes its proprietary DAT payload.
            File.WriteAllBytes(Classes, Array.Empty<byte>());
        }
        public void Dispose() => root.Delete(true);
    }
}
