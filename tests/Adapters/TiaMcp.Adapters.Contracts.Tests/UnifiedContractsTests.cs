using System.Text.Json;
using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class UnifiedContractsTests
{
    [Theory]
    [InlineData("update")]
    [InlineData("create")]
    public void Global_script_accepts_only_the_two_supported_actions(string action)
        => EngineeringInputValidation.Request(new UnifiedRequest { Operation = "SetUnifiedGlobalScript", Action = action });

    [Theory]
    [InlineData("delete")]
    [InlineData("")]
    [InlineData("Create")]
    public void Global_script_invalid_action_is_refused_before_native_dispatch(string action)
        => Assert.Throws<ArgumentException>(() => EngineeringInputValidation.Request(new UnifiedRequest {
            Operation = "SetUnifiedGlobalScript", Action = action }));

    [Theory]
    [InlineData("EventText")]
    [InlineData("EventText1")]
    [InlineData("EventText9")]
    [InlineData("InfoText")]
    public void Alarm_texts_accept_supported_properties_and_empty_text(string property)
        => EngineeringInputValidation.Request(Request("{\"" + property + "\":{\"en-US\":\"\",\"zh-CN\":\"报警\"}}"));

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"EventText0\":{\"en-US\":\"text\"}}")]
    [InlineData("{\"EventText10\":{\"en-US\":\"text\"}}")]
    [InlineData("{\"Text\":{\"en-US\":\"text\"}}")]
    [InlineData("{\"EventText\":{}}")]
    [InlineData("{\"EventText\":\"text\"}")]
    [InlineData("{\"EventText\":{\"en-US\":null}}")]
    [InlineData("{\"EventText\":{\"en-US\":2}}")]
    [InlineData("{\"EventText\":{\"\":\"text\"}}")]
    [InlineData("{\"EventText\":{\"en-US\":\"a\",\"EN-us\":\"b\"}}")]
    public void Invalid_text_shapes_are_refused(string texts)
        => Assert.Throws<ArgumentException>(() => EngineeringInputValidation.Request(Request(texts)));

    [Theory]
    [InlineData("delete", "discreteAlarms")]
    [InlineData("update", "alarmClasses")]
    [InlineData("delete", "connections")]
    public void Texts_apply_only_to_alarm_create_and_update(string action, string category)
    {
        var request = Request("{\"EventText\":{\"en-US\":\"Alarm\"}}");
        request.Action = action; request.Category = category;
        Assert.Throws<ArgumentException>(() => EngineeringInputValidation.Request(request));
    }

    [Theory]
    [InlineData("TriggerAddress")]
    [InlineData("TriggerBitAddress")]
    public void Trigger_addresses_are_read_only(string property)
    {
        var request = Request("{}");
        request.PropertiesJson = "{\"" + property + "\":\"DB1.DBX0.0\"}";
        request.Inputs[request.PropertiesJson] = Decode(JsonDocument.Parse(request.PropertiesJson).RootElement);
        var error = Assert.Throws<ArgumentException>(() => EngineeringInputValidation.Request(request));
        Assert.Contains("read-only", error.Message);
    }

    [Theory]
    [InlineData("/Diagnostic/DiagInfo", true)]
    [InlineData("/Diagnostic/Diag%2FInfo", true)]
    [InlineData("DiagInfo", true)]
    [InlineData("Diagnostic/DiagInfo", false)]
    [InlineData("/Diagnostic//DiagInfo", false)]
    [InlineData("/Diagnostic\\DiagInfo", false)]
    public void Table_deletion_uses_the_existing_exact_name_or_absolute_path_rules(string name, bool valid)
    {
        var request = Request("{}"); request.Action = "delete"; request.Category = "tagTables"; request.Name = name;
        if (valid) EngineeringInputValidation.Request(request);
        else Assert.Throws<ArgumentException>(() => EngineeringInputValidation.Request(request));
    }

    [Fact]
    public void Tag_tables_are_manage_only_and_bulk_items_are_not_added()
    {
        var request = Request("{}"); request.Operation = "ListUnifiedEngineeringObjects"; request.Category = "tagTables";
        Assert.Throws<ArgumentException>(() => EngineeringInputValidation.Request(request));
        Assert.Null(typeof(UnifiedRequest).GetProperty("ItemsJson"));
        Assert.Null(typeof(UnifiedRequest).GetProperty("ParameterTagsJson"));
    }

    private static UnifiedRequest Request(string texts) => new() {
        Operation = "ManageUnifiedEngineeringObject", Category = "discreteAlarms", Action = "create", SoftwarePath = "HMI_RT_1", Name = "Alarm",
        TextsJson = texts, Inputs = new() { [texts] = Decode(JsonDocument.Parse(texts).RootElement) } };

    private static UnifiedValue Decode(JsonElement value) => value.ValueKind switch {
        JsonValueKind.Object => new() { Kind = "object", Properties = value.EnumerateObject().ToDictionary(p => p.Name, p => Decode(p.Value)) },
        JsonValueKind.String => new() { Kind = "string", Text = value.GetString()! },
        JsonValueKind.Array => new() { Kind = "array" },
        JsonValueKind.Number => new() { Kind = "number", Text = value.GetRawText() },
        _ => new() };
}
