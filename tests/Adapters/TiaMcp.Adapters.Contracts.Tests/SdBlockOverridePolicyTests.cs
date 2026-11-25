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

    [Theory]
    [InlineData("VAR\n    Type : Int;\n    Function : Bool;\n    Data_Block : Word;\nEND_VAR")]
    [InlineData("IF #Type = 1 THEN\n    #s.type := 2;\nEND_IF;")]
    [InlineData("#msg.Function := 1;")]
    [InlineData("DB.Data_Block := %Type;")]
    [InlineData("{ Attribute := \"FUNCTION_BLOCK\"; Type := \"Int\" }")]
    [InlineData("REGION Evaluate type of part\nEND_REGION")]
    [InlineData("REGION Bob's data\nEND_REGION")]
    [InlineData("IF #run THEN REGION Bob's FUNCTION_BLOCK section\nEND_REGION\nEND_IF;")]
    [InlineData("IF #run THEN #v := 1; ELSE (* note *) REGION Bob's section\nEND_REGION\nEND_IF;")]
    public void Member_names_qualified_operands_and_region_names_are_not_declarations(string body)
    {
        var header = SdBlockDeclaration.Scan("FUNCTION_BLOCK \"Real\"\n" + body + "\nEND_FUNCTION_BLOCK");
        Assert.Equal("FUNCTION_BLOCK", header.RequireBlock("Real"));
        Assert.Equal(("FUNCTION_BLOCK", "Real"), Assert.Single(header.Declarations));
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void Apostrophes_in_regions_do_not_hide_a_second_declaration(string newline)
    {
        string text = string.Join(newline, "FUNCTION_BLOCK First", "    REGION Bob's data", "    END_REGION", "END_FUNCTION_BLOCK",
            "FUNCTION_BLOCK Second", "    REGION Alice's data", "    END_REGION", "END_FUNCTION_BLOCK");
        var scanned = SdBlockDeclaration.Scan(text);
        Assert.Equal(new[] { "First", "Second" }, scanned.Declarations.Select(item => item.Name));
        Assert.Throws<AdapterPreconditionException>(() => scanned.RequireBlock("First"));
    }

    [Fact]
    public void Region_titles_between_blocks_end_before_the_next_header()
    {
        string text = "REGION Bob's blocks\nFUNCTION_BLOCK First\nEND_FUNCTION_BLOCK\nEND_REGION\n" +
            "REGION Alice's blocks\nFUNCTION_BLOCK Second\nEND_FUNCTION_BLOCK\nEND_REGION";
        var header = SdBlockDeclaration.Scan(text);
        Assert.Equal(new[] { "First", "Second" }, header.Declarations.Select(item => item.Name));
        Assert.Throws<AdapterPreconditionException>(() => header.RequireBlock("First"));
    }

    [Theory]
    [InlineData("'text\nEND_FUNCTION_BLOCK\nFUNCTION_BLOCK Hidden\n'")]
    [InlineData("\"name\rEND_FUNCTION_BLOCK\rFUNCTION_BLOCK Hidden\r\"")]
    [InlineData("'text$\r\nEND_FUNCTION_BLOCK\nFUNCTION_BLOCK Hidden\n'")]
    [InlineData("FUNCTION_BLOCK Hidden\nEND_FUNCTION_BLOCK")]
    [InlineData("REGION label\u2028END_FUNCTION_BLOCK\u2028FUNCTION_BLOCK Hidden")]
    [InlineData("// comment\u0085END_FUNCTION_BLOCK\u0085FUNCTION_BLOCK Hidden")]
    [InlineData("{ attribute := \"value\"\nEND_FUNCTION_BLOCK")]
    public void Ambiguous_input_cannot_authorize_an_import(string body)
    {
        Assert.Throws<AdapterPreconditionException>(() => SdBlockDeclaration.Scan("FUNCTION_BLOCK Real\n" + body).RequireBlock("Real"));
    }

    [Theory]
    [InlineData("DATA_BLOCK \"MotorDB\"\n    \"MotorFB\"\nEND_DATA_BLOCK", "MotorFB")]
    [InlineData("DATA_BLOCK MotorDB MotorFB\nEND_DATA_BLOCK", "MotorFB")]
    [InlineData("DATA_BLOCK MotorDB NON_RETAIN\nVAR_INPUT\n    Value : Int;\nEND_VAR\nEND_DATA_BLOCK", null)]
    [InlineData("DATA_BLOCK MotorDB RETAIN\nVAR_STATIC\n    Value : Int;\nEND_VAR\nEND_DATA_BLOCK", null)]
    [InlineData("DATA_BLOCK MotorDB\nVAR_TEMP\n    Value : Int;\nEND_VAR\nEND_DATA_BLOCK", null)]
    [InlineData("DATA_BLOCK MotorDB\nRETAIN\nLibrary.Motion.\"Motor FB\"\nEND_DATA_BLOCK", "Library.Motion.Motor FB")]
    [InlineData("DATA_BLOCK MotorDB : \"Library\".\"MotorFB\"\nEND_DATA_BLOCK", "Library.MotorFB")]
    [InlineData("DATA_BLOCK MotorDB\nVAR\n    Type : Int;\nEND_VAR\nEND_DATA_BLOCK", null)]
    public void Db_admission_distinguishes_an_instance_from_a_global(string text, string? expected)
    {
        var header = SdBlockDeclaration.Scan(text);
        Assert.Equal("DATA_BLOCK", header.RequireBlock("MotorDB"));
        Assert.Equal(expected, header.InstanceType);
        var global = new Dictionary<string, object?> { ["Type"] = "GlobalDB" };
        Assert.Equal(expected == null, SdBlockOverridePolicy.Refusal("MotorDB", "DATA_BLOCK", true, false, global, header.InstanceType) == null);
        if (expected != null)
            Assert.Contains("InstanceDB of " + expected, SdBlockOverridePolicy.Refusal("MotorDB", "DATA_BLOCK", true, false, global, header.InstanceType));
    }

    [Theory]
    [InlineData("(*", "*)")]
    [InlineData("/*", "*/")]
    public void Only_different_declarations_under_the_two_comment_readings_block_admission(string start, string end)
    {
        string Prefix(string tail) => $"{start} outer {start} inner {end} {tail} {end}\nFUNCTION_BLOCK Real\nEND_FUNCTION_BLOCK";
        Assert.Equal("FUNCTION_BLOCK", SdBlockDeclaration.Scan(Prefix("ordinary prose")).RequireBlock("Real"));
        Assert.Equal("FUNCTION_BLOCK", SdBlockDeclaration.Scan(Prefix("'FUNCTION_BLOCK Hidden'")).RequireBlock("Real"));
        Assert.Throws<AdapterPreconditionException>(() => SdBlockDeclaration.Scan(Prefix("DATA_BLOCK Hidden\nEND_DATA_BLOCK")));
        Assert.Throws<AdapterPreconditionException>(() => SdBlockDeclaration.Scan(Prefix("FUNCTION_BLOCK Real\nEND_FUNCTION_BLOCK")));
        // In the flat reading the line comment consumes the declaration that the nested reading admits.
        Assert.Throws<AdapterPreconditionException>(() => SdBlockDeclaration.Scan($"{start} {start} inner {end} // {end} FUNCTION FC1 : Void\nEND_FUNCTION"));
    }

    [Fact]
    public void An_opaque_value_does_not_hide_a_missing_or_added_attribute()
    {
        var before = new Dictionary<string, object?> { ["Lost"] = "Vendor.Value", ["Both"] = "Vendor.Value" };
        var after = new Dictionary<string, object?> { ["Added"] = "Vendor.Value", ["Both"] = 12 };
        var opaque = new HashSet<string> { "Added", "Both", "Lost" };
        var differences = SdBlockOverridePolicy.Compare(before, after, SdBlockDeclaration.Scan("FUNCTION F : Void\nEND_FUNCTION"), opaque);
        Assert.Equal(new[] { "Added", "Lost" }, differences.Select(row => (string?)row["name"]));
    }

    [Theory]
    [InlineData("(*", "*)")]
    [InlineData("/*", "*/")]
    public void An_unclosed_nested_reading_only_blocks_disagreement_about_declarations(string open, string close)
    {
        string text = $"DATA_BLOCK MotorDB\n{open} outer {open} inner {close} ";
        Assert.Equal("DATA_BLOCK", SdBlockDeclaration.Scan(text + "END_DATA_BLOCK").RequireBlock("MotorDB"));
        Assert.Equal("MotorFB", SdBlockDeclaration.Scan(text + "MotorFB\nEND_DATA_BLOCK").InstanceType);
        Assert.Equal("FUNCTION", SdBlockDeclaration.Scan($"FUNCTION Real : Void\nEND_FUNCTION\n{open} trailing prose").RequireBlock("Real"));
        string pragma = $"{{ S7_BlockNumber := \"17\" {open} outer {open} inner {close} }}\n";
        // Both readings see the block when the incomplete comment is in an attribute list after its declaration.
        Assert.Equal("FUNCTION", SdBlockDeclaration.Scan("FUNCTION Real : Void\n" + pragma + "END_FUNCTION").RequireBlock("Real"));
        Assert.Throws<AdapterPreconditionException>(() => SdBlockDeclaration.Scan(text + "END_DATA_BLOCK\nFUNCTION Hidden : Void\nEND_FUNCTION"));
        string otherOpen = open == "(*" ? "/*" : "(*";
        string flatUnclosed = $"FUNCTION Real : Void\nEND_FUNCTION\n{open} {open} inner {close} {otherOpen} {close}";
        Assert.Equal("FUNCTION", SdBlockDeclaration.Scan(flatUnclosed).RequireBlock("Real"));
        Assert.Throws<AdapterPreconditionException>(() => SdBlockDeclaration.Scan(flatUnclosed + "\nFUNCTION Hidden : Void\nEND_FUNCTION"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Nullness_is_comparable_for_an_opaque_attribute(bool startsNull)
    {
        var before = new Dictionary<string, object?> { ["Value"] = startsNull ? null : "Vendor.Value" };
        var after = new Dictionary<string, object?> { ["Value"] = startsNull ? "Vendor.Value" : null };
        var difference = Assert.Single(SdBlockOverridePolicy.Compare(before, after, SdBlockDeclaration.Scan("FUNCTION F : Void\nEND_FUNCTION"),
            new HashSet<string> { "Value" }));
        Assert.Equal("Value", difference["name"]);
        Assert.Equal(before["Value"], difference["before"]);
        Assert.Equal(after["Value"], difference["after"]);
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
