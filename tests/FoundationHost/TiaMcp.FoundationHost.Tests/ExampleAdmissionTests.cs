extern alias enginehost;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;
using Xunit;
using Examples = enginehost::TiaMcpServer.ModelContextProtocol.ToolExamples;

public sealed class ExampleAdmissionTests
{
    public static IEnumerable<object[]> AliasCases() =>
        from tool in new[] { "AnalyzePlcReferences", "ImportPlcTagTablesFromDirectory" }
        from legacyRequired in new[] { false, true }
        from canonicalRequired in new[] { false, true }
        select new object[] { tool, legacyRequired, canonicalRequired };
    public static IEnumerable<object[]> Releases() => CanonicalParameterRound2Tests.Releases();

    [Theory, MemberData(nameof(AliasCases))]
    public void Derived_input_examples_choose_one_canonical_name(string tool, bool legacyRequired, bool canonicalRequired)
    {
        var alias = Assert.Single(CanonicalToolParameters.Aliases(tool));
        const string legacyPath = @"C:\Examples\legacy";
        const string canonicalPath = @"C:\Examples\canonical";
        var example = Examples.Derive(tool, new[] {
            new PreflightLogic.ParameterSpec(alias.Legacy, "string", legacyRequired, null, "", exampleJson: JsonSerializer.Serialize(legacyPath)),
            new PreflightLogic.ParameterSpec(alias.Canonical, "string", canonicalRequired, null, "", exampleJson: JsonSerializer.Serialize(canonicalPath))
        });
        var args = JsonNode.Parse(example.ArgumentsJson)!.AsObject();
        Assert.False(args.ContainsKey(alias.Legacy));
        Assert.Equal(canonicalRequired || !legacyRequired ? canonicalPath : legacyPath, (string?)args[alias.Canonical]);
        Assert.Single(args);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Aml_example_uses_the_existing_output_root_default(string release)
    {
        const string name = "BuildDeviceAmlDocument";
        var args = ToolUsageCatalog.ServedCall(name, release)!["arguments"]!.AsObject();
        Assert.False(args.ContainsKey("outputPath"));
        Assert.False(args.ContainsKey("exportFile"));
        string directory = Path.Combine(AppContext.BaseDirectory, "example-output");
        var effective = CanonicalToolParameters.WithDefaults(name, args, new[] { "spec", "outputPath" }, directory);
        Assert.Equal(Path.Combine(directory, "BuildDeviceAmlDocument.aml"), (string?)effective["exportFile"]);
        Assert.NotNull(args["spec"]);
    }
}
