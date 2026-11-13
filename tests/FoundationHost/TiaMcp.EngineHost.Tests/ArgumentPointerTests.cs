using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed partial class ReadBatchAdmissionTests
{
    [Theory]
    [InlineData("sofwarePath", "Undeclared input field (did you mean 'softwarePath'?)")]
    [InlineData("token=sk-live", "Undeclared input field.")]
    public async Task Preview_unknown_fields_keep_only_declared_suggestions(string key, string expected)
    {
        using var fixture = new Fixture("21");
        var result = await fixture.CallResult("PreviewToolCall", new JsonObject { ["name"] = "CompareProjects",
            ["arguments"] = new JsonObject { ["kind"] = "software", [key] = "PLC" } });
        Assert.Equal(new[] { expected }, result.StructuredContent!["data"]!["unknown"]!.AsArray().Select(v => (string?)v));
        Assert.DoesNotContain(key, result.StructuredContent.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(key, ((TextContentBlock)result.Content.Single()).Text, StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(result.StructuredContent, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)));
        fixture.AssertNothingDispatched();
    }

    public static IEnumerable<object[]> PrivateKeys() => Order.SelectMany(release =>
        new[] { "token=sk-live", "123456" }.Select(key => new object[] { release, key }));

    [Theory, MemberData(nameof(PrivateKeys))]
    public async Task Argument_pointer_routes_keep_undeclared_keys_private(string release, string key)
    {
        using var fixture = new Fixture(release);
        var cases = new[] {
            ("CompareProjects", "{\"kind\":\"software\",\"" + key + "\":1}"),
            ("CompareProjects", "{\"kind\":\"software\",\"devicePath\":{\"" + key + "\":1}}"),
            ("CallTool", "{\"name\":\"CompareProjects\",\"arguments\":{\"kind\":\"software\",\"" + key + "\":1}}"),
            ("RunReadOnlyToolBatch", "{\"operations\":[{\"name\":\"CompareProjects\",\"arguments\":{\"kind\":\"software\",\""
                + key + "\":1}}]}") };
        foreach (var (tool, json) in cases)
        {
            var result = await fixture.CallResult(tool, JsonNode.Parse(json)!.AsObject());
            Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent!["error"]!["code"]);
            Assert.DoesNotContain(key, result.StructuredContent.ToJsonString(), StringComparison.Ordinal);
            Assert.DoesNotContain(key, ((TextContentBlock)result.Content.Single()).Text, StringComparison.Ordinal);
        }
        var ambiguousInput = new JsonObject { [key] = 1 };
        ambiguousInput[key.ToUpperInvariant()] = 2;
        foreach (var child in new[] { new JsonObject { [key] = 1 }, ambiguousInput })
        {
            var preview = await fixture.CallResult("PreviewToolCall", new JsonObject { ["name"] = "CompareProjects", ["arguments"] = child });
            Assert.DoesNotContain(key, preview.StructuredContent!.ToJsonString(), StringComparison.Ordinal);
            Assert.DoesNotContain(key, ((TextContentBlock)preview.Content.Single()).Text, StringComparison.Ordinal);
        }
        using var session = fixture.EnterSession();
        var invoker = McpServer.ToolInvoker;
        var duplicate = JsonSerializer.Deserialize<JsonElement>("{\"" + key + "\":1,\"" + key + "\":2}");
        var target = McpServer.CatalogView.Find("CompareProjects")!;
        var error = invoker.ValidateArguments(target, duplicate, target.Tool.InputSchema)!;
        Assert.Equal("", ((InvalidArgumentDetails)error.Details).Parameter);
        var refusal = McpServer.V4Reject("CompareProjects", error);
        Assert.DoesNotContain(key, refusal.StructuredContent!.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(key, ((TextContentBlock)refusal.Content.Single()).Text, StringComparison.Ordinal);
        var ambiguous = new ToolArguments(JsonSerializer.Deserialize<JsonElement>(
            "{\"kind\":\"software\",\"Kind\":\"software\",\"" + key + "\":1}"));
        var duplicateName = invoker.Bind("CompareProjects", ambiguous, out _)!;
        Assert.Equal("", ((InvalidArgumentDetails)duplicateName.Details).Parameter);
        var duplicateRefusal = McpServer.V4Reject("CompareProjects", duplicateName);
        Assert.DoesNotContain(key, duplicateRefusal.StructuredContent!.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(key, ((TextContentBlock)duplicateRefusal.Content.Single()).Text, StringComparison.Ordinal);
        fixture.AssertNothingDispatched();
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Batch_binding_adds_the_row_prefix_to_nested_schema_errors(string release)
    {
        using var fixture = new Fixture(release);
        var result = await fixture.CallResult("RunReadOnlyToolBatch", JsonNode.Parse(
            "{\"operations\":[{\"name\":\"CompareProjects\",\"arguments\":{\"kind\":\"software\",\"devicePath\":[\"one\",\"two\",3]}}]}")!.AsObject());
        Assert.Equal("/operations/0/arguments/devicePath/2", (string?)result.StructuredContent!["error"]!["details"]!["parameter"]);
        fixture.AssertNothingDispatched();
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Typed_batch_errors_before_binding_keep_the_row_prefix(string release)
    {
        using var fixture = new Fixture(release);
        var result = await fixture.CallResult("PreviewToolBatch", JsonNode.Parse(
            "{\"expectedProject\":\"Fixture\",\"operations\":[{\"name\":\"GetProjectInfo\",\"arguments\":{}},"
                + "{\"name\":\"BuildUnifiedHmiThemeDesign\",\"arguments\":{\"theme\":{\"palette\":false}}}]}")!.AsObject());
        Assert.Equal("/operations/1/arguments/theme/palette", (string?)result.StructuredContent!["error"]!["details"]!["parameter"]);
        Assert.True(JsonNode.DeepEquals(result.StructuredContent, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)));
        fixture.AssertNothingDispatched();
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Bridge_type_errors_keep_the_existing_message_and_recovery_hint(string release)
    {
        using var fixture = new Fixture(release);
        var result = await fixture.CallResult("CallTool", JsonNode.Parse(
            "{\"name\":\"CompareProjects\",\"arguments\":{\"kind\":\"software\",\"softwarePath\":false}}")!.AsObject());
        Assert.Equal("Missing or invalid target field(s): softwarePath.", (string?)result.StructuredContent!["error"]!["message"]);
        Assert.Equal("/softwarePath", (string?)result.StructuredContent["error"]!["details"]!["parameter"]);
        Assert.Contains(result.StructuredContent["meta"]!["warnings"]!.AsArray(), w => (string?)w!["code"] == "RECOVERY_GUIDANCE");
        fixture.AssertNothingDispatched();
    }
}
