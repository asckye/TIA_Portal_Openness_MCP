using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class HmiPortTests
{
    public static IEnumerable<object[]> ReleasesAndTools =>
        from release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        from family in PortedFamilies.All.Where(f => f.Name is "F24" or "F25")
        from tool in family.Tools
        select new object[] { release, tool, family.Name };

    [Theory, MemberData(nameof(ReleasesAndTools))]
    public void Registration_and_screen_item_refusal_follow_the_official_release(string release, string tool, string family)
    {
        bool expected = tool != "DescribeHmiScreenItem" || release is "19" or "20" or "21";
        Assert.Equal(expected, PortedFamilies.Available(release, tool));
        Assert.Equal(family, PortedFamilies.ForTool(tool).Name);
        bool missingScreenItems = tool == "DescribeHmiScreenItem" && release is not ("19" or "20" or "21");
        Assert.Equal(missingScreenItems, HmiCapabilities.Unsupported(release, tool) != null);
    }

    [Fact]
    public void A_disposed_object_requires_read_quarantine()
    {
        var fault = new ObjectDisposedException("HMI");
        Assert.True(HmiReadPolicy.ConnectionUnavailable(fault));
        Assert.True(HmiReadPolicy.ConnectionUnavailable(new System.Reflection.TargetInvocationException(fault)));
    }

    [Fact]
    public void Step_reply_keeps_dictionary_order_nulls_and_boolean_types_on_the_wire()
    {
        var message = new HmiMessage { Message = "partial", Meta = new Dictionary<string, object?> {
            ["operationSuccess"] = false, ["failurePath"] = "$/0/B", ["retained"] = null,
            ["requiresExplicitRebind"] = true } };
        const string expected = "{\"Message\":\"partial\",\"Meta\":{\"operationSuccess\":false,\"failurePath\":\"$/0/B\",\"retained\":null,\"requiresExplicitRebind\":true}}";
        Assert.Equal(expected, Newtonsoft.Json.JsonConvert.SerializeObject(message));
        var copy = Newtonsoft.Json.JsonConvert.DeserializeObject<HmiMessage>(expected)!;
        Assert.False(Assert.IsType<bool>(copy.Meta["operationSuccess"]));
        Assert.True(Assert.IsType<bool>(copy.Meta["requiresExplicitRebind"]));
    }

    [Theory]
    [InlineData("1", typeof(int))]
    [InlineData("1.0", typeof(double))]
    [InlineData("1e2", typeof(double))]
    [InlineData("2147483648", typeof(double))]
    public void Object_attributes_keep_the_original_json_number_type(string token, Type expectedType)
    {
        using var original = System.Text.Json.JsonDocument.Parse(token);
        object expected = original.RootElement.TryGetInt32(out var integer) ? (object)integer : original.RootElement.GetDouble();
        object actual = HmiScalarPolicy.ConvertValue(new HardwareScalar { Kind = "number", Text = token }, typeof(object))!;
        Assert.Equal(expectedType, actual.GetType());
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("1.2345678901234567890123456789", typeof(decimal))]
    [InlineData("1e-5", typeof(decimal))]
    [InlineData("65535", typeof(ushort))]
    [InlineData("1.25", typeof(float))]
    public void Typed_attributes_convert_the_original_token_without_an_intermediate_double(string token, Type type)
    {
        object expected = System.Text.Json.JsonSerializer.Deserialize(token, type)!;
        Assert.Equal(expected, HmiScalarPolicy.ConvertValue(new HardwareScalar { Kind = "number", Text = token }, type));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1e2")]
    public void Integer_attributes_refuse_noninteger_json_tokens_before_a_native_write(string token)
    {
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<int>(token));
        Assert.Throws<FormatException>(() => HmiScalarPolicy.ConvertValue(new HardwareScalar { Kind = "number", Text = token }, typeof(int)));
    }

    [Theory]
    [InlineData("2026-10-11")]
    [InlineData("2026-10-11T10:23")]
    [InlineData("2026-10-11T10:23:01Z")]
    [InlineData("2026-10-11T10:23:01.1234567890123456-07:00")]
    public void Date_attributes_keep_the_original_iso_value_and_kind(string text)
    {
        var expected = System.Text.Json.JsonSerializer.Deserialize<DateTime>(System.Text.Json.JsonSerializer.Serialize(text));
        var actual = Assert.IsType<DateTime>(HmiScalarPolicy.ConvertValue(new HardwareScalar { Kind = "string", Text = text }, typeof(DateTime)));
        Assert.Equal(expected, actual);
        Assert.Equal(expected.Kind, actual.Kind);
    }

    [Theory]
    [InlineData("October 11, 2026")]
    [InlineData("2026-10-11 10:23:01")]
    [InlineData("2026-10-11T10:23:01+14:01")]
    public void Date_attributes_refuse_formats_the_original_json_converter_refuses(string text)
    {
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<DateTime>(System.Text.Json.JsonSerializer.Serialize(text)));
        Assert.Throws<FormatException>(() => HmiScalarPolicy.ConvertValue(new HardwareScalar { Kind = "string", Text = text }, typeof(DateTime)));
    }
}
