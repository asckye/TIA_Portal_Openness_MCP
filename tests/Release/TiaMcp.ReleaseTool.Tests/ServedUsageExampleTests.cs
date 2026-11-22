using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class ServedUsageExampleTests
{
    private const string Tool = "GetDriveParameters";

    [Theory, InlineData("14sp1", "write"), InlineData("19", "write"), InlineData("20", "read"), InlineData("21", "read")]
    public void Served_drive_example_uses_the_native_parameter_view_for_its_release(string release, string source)
    {
        var calls = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.FindRoot(), "reference", "tool-examples", "calls.json")))!;
        var args = ToolUsageCatalog.ReleaseArguments(calls, Tool, release, calls["profiles"]!["plc-foundation"]![Tool]!["arguments"]!);
        Assert.Equal(source, (string?)args["source"]);
        Assert.Null(ReleaseCapabilityQuery.Problem(Tool, release, args));
    }

    [Theory]
    [InlineData("ManageHardwareObject", "action", "changeType", "18")]
    [InlineData("CompareProjects", "kind", "hardware", "15.1")]
    [InlineData("BuildAndImportPlcArtifact", "kind", "fc", "21")]
    [InlineData("ManageGlobalLibrary", "action", "archive", "15.1")]
    public void Legacy_refusals_and_action_guidance_identify_the_first_supported_release(string tool, string key, string value, string minimum)
    {
        var args = new JsonObject { [key] = value };
        var reason = ReleaseCapabilityQuery.Problem(tool, "14sp1", args);
        Assert.Contains(tool + "." + value, reason);
        Assert.Contains("V14 SP1", reason);
        Assert.Contains("Minimum release for this request is V" + minimum, reason);
        Assert.Null(ReleaseCapabilityQuery.Problem(tool, minimum, args));
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17")]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Hardware_and_SiVArc_examples_match_the_selected_release(string release)
    {
        var tools = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.FindRoot(), "manifest", "contracts", "v4", "baseline",
            release + ".json")))!["tools"]!.AsArray();
        JsonObject Example(string tool, string operation = "")
        {
            var schema = tools.Single(row => (string?)row!["name"] == tool)!["inputSchema"]!.AsObject();
            var usage = ToolUsageCatalog.Describe(tool, release, "plc-foundation", "", schema, operation: operation);
            return usage["example"]!["request"]!["params"]!["arguments"]!.AsObject();
        }
        var aml = Example("BuildDeviceAmlDocument");
        Assert.Null(aml["exportFile"]);
        Assert.NotNull(aml["spec"]?["devices"]?[0]);
        if (release == "14sp1") return;
        var transfer = Example("ManageTransferArea");
        Assert.Equal(release == "15.1" ? "CD" : "IN", (string?)transfer["type"]);
        Assert.Null(ReleaseCapabilityQuery.Problem("ManageTransferArea", release, transfer));
        var rule = Example("ManageSivarcRule", "update");
        Assert.Null(rule["properties"]?["Name"]);
        Assert.True((bool?)rule["properties"]?["Enabled"]);
        Assert.Empty(Example("ManageSivarcRule", "delete")["properties"]!.AsObject());
        if (release == "15.1") Assert.True((bool?)Example("ManageSivarcRule")["properties"]?["Enabled"]);
    }

    [Fact]
    public void Simotion_guidance_observes_the_registered_V14_provider()
    {
        var tool = "InitializeSimotionScripting";
        var root = Repository.FindRoot();
        var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "manifest", "contracts", "v4", "baseline", "14sp1.json")))!
            ["tools"]!.AsArray().Single(row => (string?)row!["name"] == tool)!["inputSchema"]!.DeepClone().AsObject();
        Assert.Null(ToolUsageCatalog.Describe(tool, "14sp1", "plc-foundation", "", schema)["example"]!["releaseProblem"]);
    }

    [Theory, InlineData("bogus", "unsupported"), InlineData("source", "read"), InlineData(null, "read")]
    public void Served_example_audit_refuses_invalid_and_release_incompatible_overrides(string? field, string value)
    {
        const string release = "14sp1";
        string root = Repository.FindRoot();
        var calls = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "reference", "tool-examples", "calls.json")))!;
        var source = calls["profiles"]!["plc-foundation"]![Tool]!;
        var minimal = new JsonObject {
            ["calls"] = new JsonObject {
                ["profiles"] = new JsonObject { ["plc-foundation"] = new JsonObject { [Tool] = source.DeepClone() } },
                ["releaseExtensions"] = calls["releaseExtensions"]!.DeepClone(),
                ["releaseArguments"] = new JsonObject { [Tool] = new JsonArray(new JsonObject {
                    ["releases"] = new JsonArray(release), ["arguments"] = new JsonObject { [field ?? "source"] = value } }) } },
            ["examples"] = new JsonArray(), ["sequences"] = new JsonArray() };
        if (field == null) minimal["calls"]!["releaseArguments"]!.AsObject().Clear();
        var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "manifest", "contracts", "v4", "baseline", release + ".json")))!
            ["tools"]!.AsArray().Single(row => (string?)row!["name"] == Tool)!["inputSchema"]!;
        var schemas = new JsonObject { [Tool] = schema.DeepClone() };
        var served = new Dictionary<string, JsonNode> { [Tool] = source };
        var error = Assert.Throws<ReleaseException>(() => CatalogAudits.ValidateUsageExamples(minimal, schemas, release, servedEntries: served));
        Assert.Contains(release + "/served/" + Tool, error.Message);
    }
}
