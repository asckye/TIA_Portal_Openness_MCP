using System.Text.Json;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class SdBlockOverridePolicyTests
{
    [Theory]
    [InlineData("MCP_RC_OBCyc", 150)]
    [InlineData("MCP_RC_OBStart", 151)]
    [InlineData("MCP_RC_DBAttr", 9810)]
    [InlineData("MCP_RC_OBC", 151)]
    [InlineData("MCP_RC_SDDB", 9820)]
    public void Recorded_override_is_refused_and_all_observed_losses_are_reported(string name, int number)
    {
        using var stream = typeof(SdBlockOverridePolicyTests).Assembly.GetManifestResourceStream("SdBlockOverride.json")!;
        using var json = JsonDocument.Parse(stream);
        var row = json.RootElement.EnumerateArray().Single(r => r.GetProperty("name").GetString() == name);
        var before = Map(row.GetProperty("before"));
        var after = Map(row.GetProperty("after"));
        string kind = before["Type"] as string == "OB" ? "ORGANIZATION_BLOCK" : "DATA_BLOCK";
        var header = SdBlockDeclaration.Scan(kind + " \"" + name + "\"");
        var refusal = SdBlockOverridePolicy.Refusal(name, header.RequireBlock(name), true, false, before);
        Assert.Contains(name, refusal);
        Assert.Contains("SimaticML", refusal);
        Assert.Null(SdBlockOverridePolicy.Refusal(name, kind, true, false, after));
        var differences = SdBlockOverridePolicy.Compare(before, after, header);
        string[] expected = kind == "DATA_BLOCK" ? ["DBAccessibleFromOPCUA", "IsOnlyStoredInLoadMemory", "IsWriteProtectedInAS", "Number"]
            : name == "MCP_RC_OBStart" ? ["EventClass", "Number", "ProcessImagePartNumber", "SecondaryType"]
            : ["CyclicTime", "EnableTimeError", "EventClass", "EventThresholdForTimeError", "EventsToBeQueued", "Number",
                "PhaseOffset", "PriorityNumber", "ReportEvents", "SecondaryType"];
        Assert.Equal(expected, differences.Select(d => d["name"]));
        Assert.All(differences, d => Assert.Equal(false, d["restored"]));
        var renumber = differences.Single(d => Equals(d["name"], "Number"));
        Assert.Equal(number, renumber["before"]);
        Assert.Equal(1, renumber["after"]);
        Assert.Equal(true, before["AutoNumber"]);
        Assert.Equal(true, after["AutoNumber"]);
    }

    private static Dictionary<string, object?> Map(JsonElement obj) => obj.EnumerateObject()
        .ToDictionary(p => p.Name, p => p.Value.ValueKind switch {
            JsonValueKind.Number => (object?)p.Value.GetInt32(), JsonValueKind.True or JsonValueKind.False => p.Value.GetBoolean(),
            _ => p.Value.GetString()
        });

    [Theory]
    [InlineData("FB", "FUNCTION_BLOCK", "LAD", true)]
    [InlineData("FC", "FUNCTION", "SCL", true)]
    [InlineData("GlobalDB", "DATA_BLOCK", "DB", true)]
    [InlineData("OB", "ORGANIZATION_BLOCK", "LAD", true)]
    [InlineData("InstanceDB", "DATA_BLOCK", "DB", false)]
    [InlineData("ArrayDB", "DATA_BLOCK", "DB", false)]
    [InlineData("TechnologyDB", "DATA_BLOCK", "DB", false)]
    [InlineData("FB", "FUNCTION_BLOCK", "F_LAD", false)]
    [InlineData("GlobalDB", "DATA_BLOCK", "F_DB", false)]
    [InlineData("OB", "FUNCTION", "LAD", false)]
    public void Existing_block_kind_and_language_gate(string type, string kind, string language, bool admitted)
    {
        var block = new Dictionary<string, object?> { ["Type"] = type, ["SecondaryType"] = "ProgramCycle", ["ProgrammingLanguage"] = language };
        Assert.Equal(admitted, SdBlockOverridePolicy.Refusal("Main", kind, true, false, block) == null);
    }

    [Theory]
    [InlineData("FB", "IsIECCheckEnabled", true)]
    [InlineData("FC", "IsKnowHowProtected", true)]
    [InlineData("FC", "IsWriteProtected", true)]
    [InlineData("FB", "IsSafetyBlock", true)]
    [InlineData("GlobalDB", "DBAccessibleFromWebserver", false)]
    [InlineData("GlobalDB", "DownloadWithoutReinit", true)]
    [InlineData("GlobalDB", "IsRetainMemResEnabled", true)]
    [InlineData("GlobalDB", "MemoryReserve", 0)]
    public void Present_attributes_must_be_preservable(string type, string key, object value)
    {
        string kind = type == "GlobalDB" ? "DATA_BLOCK" : type == "FB" ? "FUNCTION_BLOCK" : "FUNCTION";
        var block = new Dictionary<string, object?> { ["Type"] = type, [key] = value };
        Assert.Contains(key, SdBlockOverridePolicy.Refusal("Target", kind, true, false, block));
    }

    [Fact]
    public void Both_import_options_refuse_new_OB_and_override_requires_the_exact_group()
    {
        foreach (bool replace in new[] { false, true })
            Assert.Contains("ProgramCycle", SdBlockOverridePolicy.Refusal("Startup", "ORGANIZATION_BLOCK", replace, false, null));
        Assert.Contains("another group", SdBlockOverridePolicy.Refusal("DB", "DATA_BLOCK", true, true, null));
    }

    [Fact]
    public void Only_header_pragmas_authorize_changes_and_the_requested_number_must_match()
    {
        using var source = typeof(SdBlockOverridePolicyTests).Assembly.GetManifestResourceStream("LadFb.s7dcl")!;
        string lad = new StreamReader(source).ReadToEnd();
        var header = SdBlockDeclaration.Scan(lad);
        Assert.Equal("FUNCTION_BLOCK", header.RequireBlock("mcpverify_fb_lad_v3"));
        Assert.Equal("59989", header.Pragmas["S7_BlockNumber"]);
        Assert.Equal("0.1", header.Pragmas["S7_Version"]);
        var before = new Dictionary<string, object?> { ["Number"] = 150, ["HeaderVersion"] = "0.0", ["MemoryLayout"] = "Standard",
            ["ProgrammingLanguage"] = "SCL", ["CompileDate"] = "before" };
        var after = new Dictionary<string, object?> { ["Number"] = 59989, ["HeaderVersion"] = "0.1", ["MemoryLayout"] = "Optimized",
            ["ProgrammingLanguage"] = "LAD", ["CompileDate"] = "after" };
        Assert.Empty(SdBlockOverridePolicy.Compare(before, after, header));
        Assert.Empty(SdBlockOverridePolicy.Compare(null, after, header));
        after["Number"] = 150;
        Assert.Equal(59989, Assert.Single(SdBlockOverridePolicy.Compare(null, after, header))["expected"]);
        Assert.Equal(59989, Assert.Single(SdBlockOverridePolicy.Compare(before, after, header))["expected"]);
        var none = SdBlockDeclaration.Scan("FUNCTION_BLOCK FB1\n{ S7_Version := \"1.0\" }");
        Assert.Empty(none.Pragmas);
        Assert.Equal(new[] { "HeaderVersion", "MemoryLayout" }, SdBlockOverridePolicy.Compare(before, after, none).Select(d => d["name"]));
    }

    [Fact]
    public void Comments_strings_and_repeated_declarations_cannot_select_a_different_block()
    {
        const string text = "// FUNCTION Hidden\r(* nested (* FUNCTION_BLOCK Bad *) DATA_BLOCK Nope *)\n" +
            "{ S7_BlockTitle := \"FUNCTION Bad\" } FUNCTION \"Real\" : Void\n#x := 'ORGANIZATION_BLOCK Bad'; END_FUNCTION";
        Assert.Equal("FUNCTION", SdBlockDeclaration.Scan(text).RequireBlock("real"));
        Assert.Equal("name", Assert.Throws<AdapterPreconditionException>(() => SdBlockDeclaration.Scan(text).RequireBlock("Hidden")).ParamName);
        Assert.Throws<AdapterPreconditionException>(() => SdBlockDeclaration.Scan(text + "\nDATA_BLOCK Real").RequireBlock("Real"));
        Assert.Throws<AdapterPreconditionException>(() => SdBlockDeclaration.Scan("(* DATA_BLOCK Real *)").RequireBlock("Real"));
        Assert.Throws<AdapterPreconditionException>(() => SdBlockDeclaration.Scan("DATA_BLOCK Namespace.Other").RequireBlock("Namespace"));
        Assert.Throws<AdapterPreconditionException>(() => SdBlockDeclaration.Scan("DATA_BLOCK \"Namespace\".\"Other\"").RequireBlock("Namespace"));
    }
}
