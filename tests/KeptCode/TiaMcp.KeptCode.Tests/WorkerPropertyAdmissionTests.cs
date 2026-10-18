using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed class WorkerPropertyAdmissionTests
{
    public sealed class FontPart
    {
        public int Size { get; set; }
        public string Name { get; set; } = "";
        public int ReadOnly => 1;
    }
    public sealed class MultilingualText { }
    public sealed class CollectionPart : IEnumerable
    {
        public int Size { get; set; }
        public IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();
    }
    public sealed class Screen
    {
        public object? Width { get; set; }
        public int ReadOnly => 1;
        public string Name { get; set; } = "";
        public object? Owner { get; set; }
        public FontPart Font { get; } = new();
        public MultilingualText Text { get; } = new();
        public CollectionPart Items { get; } = new();
        public object this[int index] => index;
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string input in new[]
        {
            "{}", "{\"Width\":1}", "{\"Width\":null}", "{\"Width\":true}", "{\"Width\":\"120\"}",
            "{\"Font\":{}}", "{\"Font\":{\"Size\":18}}", "{\"Text\":{\"en-US\":\"Start\"}}",
            "{\"Text\":{\"zh-CN\":\"启动\"}}", "{\"Text\":{\"en-US\":\"\"}}",
            "{\"width\":1}", "{\"ReadOnly\":1}", "{\"Name\":\"changed\"}", "{\"Owner\":null}",
            "{\"Unknown\":1}", "{\"Font\":null}", "{\"Width\":{\"Size\":1}}",
            "{\"Font\":{\"ReadOnly\":1}}", "{\"Font\":{\"Name\":\"changed\"}}", "{\"Font\":{\"Unknown\":1}}",
            "{\"Text\":\"plain\"}", "{\"Text\":{\"en-US\":null}}", "{\"Text\":{\"\":\"Start\"}}",
            "{\"Text\":{\"CultureNameExceeds16\":\"Start\"}}", "{\"Text\":{\"Owner\":\"Start\"}}",
            "{\"Items\":{\"Size\":1}}", "{\"Item\":1}"
        }) yield return new object[] { input };
        string cultures = string.Join(",", Enumerable.Range(0, 50).Select(i => "\"c" + i + "\":\"text\""));
        yield return new object[] { "{\"Text\":{" + cultures + "}}" };
        yield return new object[] { "{\"Text\":{" + cultures + ",\"last\":\"text\"}}" };
        yield return new object[] { "{\"Text\":{" + cultures + "},\"Width\":1}" };
        yield return new object[] { "{\"Text\":{" + cultures + "},\"Width\":1,\"Font\":{\"Size\":1}}" };
        yield return new object[] { "{\"Text\":{" + cultures + "},\"Width\":1,\"Unknown\":1}" };
        yield return new object[] { JsonSerializer.Serialize(new { Width = new string('x', 65530) }) };
    }

    private static InputContract<CompositeAttributeMap> OriginalContract()
    {
        // Frozen rules for the fake metadata above, supplied to the original shared
        // domain validator. This oracle is independent of the worker's reflection.
        var scalar = new AttributeRule(InputSchema.Scalar());
        var fields = new Dictionary<string, AttributeRule>
        {
            ["Width"] = scalar, ["Name"] = scalar, ["Owner"] = scalar,
            ["ReadOnly"] = new AttributeRule(InputSchema.Scalar(), false)
        };
        var parts = new Dictionary<string, IReadOnlyDictionary<string, AttributeRule>>
        {
            ["Font"] = new Dictionary<string, AttributeRule>
            {
                ["Size"] = scalar, ["Name"] = scalar, ["ReadOnly"] = new AttributeRule(InputSchema.Scalar(), false)
            },
            ["Text"] = Enumerable.Range(0, 50).Select(i => "c" + i)
                .Concat(new[] { "en-US", "zh-CN", "Owner", "last" })
                .ToDictionary(name => name, _ => new AttributeRule(InputSchema.String()), StringComparer.Ordinal)
        };
        return CompositeAttributeMapValidator.ScreenItem(fields, parts);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Both_workers_match_the_original_contract_including_complete_refusal_details(string input)
    {
        var expected = OriginalContract().Read(input, "properties").Error;
        var actual = TiaMcp.Shared.UnifiedHmiPropertyAdmission.Validate(typeof(Screen), input);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(V4Json.Serialize(expected)), JsonSerializer.SerializeToNode(actual)), input);
        var typed = V4Json.Deserialize<CompositeAttributeMap>(input);
        Assert.Equal(V4Json.Serialize(expected), V4Json.Serialize(UnifiedHmiPropertyPolicy.ValidateProperties(typeof(Screen), typed)));
        if (actual != null && (string?)actual["code"] == "INVALID_ARGUMENT")
        {
            var details = JsonSerializer.SerializeToNode(actual)!["details"]!;
            Assert.Equal("properties", (string?)details["parameter"]);
            Assert.Equal(new[] { "properties" }, details["parameters"]!.AsArray().Select(value => (string)value!).ToArray());
            Assert.Empty(details["allowedValues"]!.AsArray());
        }
    }

    [Theory]
    [InlineData("{\"Items\":{\"Size\":1}}", "INVALID_ARGUMENT")]
    [InlineData("{\"Width\":1,\"Width\":2}", "INVALID_ARGUMENT")]
    public void Standalone_deltas_use_the_strict_canonical_expectations(string input, string code)
    {
        Assert.Equal(code, (string?)TiaMcp.Shared.UnifiedHmiPropertyRules.Validate(typeof(Screen), input)!["code"]);
        Assert.Equal(code, (string?)TiaMcp.Shared.UnifiedHmiPropertyAdmission.Validate(typeof(Screen), input)!["code"]);
    }
}
