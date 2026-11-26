using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using TiaMcp.BuildCommon;
using Xunit;
using static TiaMcp.ReleaseTool.CatalogJson;

namespace TiaMcp.ReleaseTool.Tests;

[Trait("Category", "Generators")]
public sealed class PlcPatternLibraryTests
{
    private static string Root => Repository.FindRoot();

    [Fact]
    public void Real_library_has_six_sources_and_one_not_run_row_per_required_cell()
    {
        var library = PlcPatternLibrary.Load(Root);
        Assert.Equal(new[] { "alarm-latch", "analog-scaling", "counters-ctu-ctd-ctud", "cylinder-timeout", "mc-power-axis",
            "modbus-rtu-master", "motor-fwd-rev", "sequence-station", "star-delta", "start-stop", "timers-ton-tof-tp" },
            library.Patterns.Select(p => Text(p!["id"])));
        var variants = library.Patterns.SelectMany(p => p!["variants"]!.AsArray()).ToArray();
        Assert.Equal(12, variants.Where(v => Text(v!["id"]) != "lad").Sum(v => v!["files"]!.AsArray().Count));
        Assert.Equal(14, variants.Where(v => Text(v!["id"]) == "lad").Sum(v => v!["files"]!.AsArray().Count));
        Assert.Equal(182, library.Evidence.Count);
        Assert.All(library.Evidence, row => { Assert.Equal("NOT RUN", Text(row!["status"])); Assert.Equal(5, row.AsObject().Count); });
        var expected = library.Patterns.SelectMany(p => p!["variants"]!.AsArray().OrderBy(v => Text(v!["id"]), StringComparer.Ordinal)
            .SelectMany(v => (Text(v!["id"]) == "lad" ? new[] { "21" } : Keys).SelectMany(k =>
            Strings(v!["cpuFamilies"]).Order(StringComparer.Ordinal).Select(f => $"{Text(p["id"])}/{Text(v["id"])}/{k}/{f}"))));
        Assert.Equal(expected, library.Evidence.Select(r => $"{Text(r!["pattern"])}/{Text(r["variant"])}/{Text(r["releaseKey"])}/{Text(r["cpuFamily"])}"));
        Assert.Empty(PlcPatternLibrary.Served(Root));
        Assert.All(library.Patterns.SelectMany(p => p!["variants"]!.AsArray()).SelectMany(v => v!["instructions"]!.AsArray()),
            i => { Assert.Equal("unverified", Text(i!["verification"])); Assert.Equal("unverified", Text(i["version"])); });
    }

    [Fact]
    public void Modbus_variants_keep_family_specific_instructions_and_motion_covers_only_s7_1500()
    {
        var patterns = PlcPatternLibrary.Load(Root).Patterns;
        var modbus = Assert.Single(patterns, p => Text(p!["id"]) == "modbus-rtu-master")!;
        var variants = modbus["variants"]!.AsArray();
        Assert.Equal(2, variants.Count);
        var small = Assert.Single(variants, v => Text(v!["id"]) == "scl-s7-1200")!;
        var large = Assert.Single(variants, v => Text(v!["id"]) == "scl-s7-1500")!;
        Assert.Equal(new[] { "S7-1200" }, Strings(small["cpuFamilies"]));
        Assert.Equal(new[] { "S7-1500" }, Strings(large["cpuFamilies"]));
        Assert.Equal(new[] { "MB_COMM_LOAD", "MB_MASTER" }, small["instructions"]!.AsArray().Select(i => Text(i!["name"])));
        Assert.Equal(new[] { "Modbus_Comm_Load", "Modbus_Master" }, large["instructions"]!.AsArray().Select(i => Text(i!["name"])));
        var motion = Assert.Single(patterns, p => Text(p!["id"]) == "mc-power-axis")!;
        Assert.Equal(new[] { "S7-1500" }, Strings(Assert.Single(motion["variants"]!.AsArray())!["cpuFamilies"]));
    }

    [Theory]
    [InlineData("star-delta", "scl", "pattern/star-delta")]
    [InlineData("cylinder-timeout", "scl", "pattern/cylinder-timeout")]
    [InlineData("sequence-station", "scl", "pattern/sequence-station")]
    [InlineData("modbus-rtu-master", "scl-s7-1200", "pattern/modbus-rtu-master/scl-s7-1200")]
    [InlineData("modbus-rtu-master", "scl-s7-1500", "pattern/modbus-rtu-master/scl-s7-1500")]
    [InlineData("mc-power-axis", "scl", "pattern/mc-power-axis")]
    public void Added_variants_require_manual_verification_and_every_declared_cell(string id, string variantId, string exampleId)
    {
        using var fixture = new Fixture();
        var evidence = fixture.Evidence();
        var cells = evidence["rows"]!.AsArray().Where(r => Text(r!["pattern"]) == id && Text(r["variant"]) == variantId).ToArray();
        foreach (var row in cells) Fixture.Verify(row!.AsObject());
        fixture.SaveEvidence(evidence);
        Assert.Empty(PlcPatternLibrary.Served(fixture.Root));

        string path = Path.Combine(fixture.Root, "reference/plc-patterns/" + id + "/pattern.json");
        var pattern = JsonNode.Parse(File.ReadAllText(path))!;
        var variant = Assert.Single(pattern["variants"]!.AsArray(), v => Text(v!["id"]) == variantId)!;
        foreach (var instruction in variant["instructions"]!.AsArray())
        {
            instruction!["verification"] = "verified"; instruction["version"] = "1.0";
            instruction["sourceUrl"] = "https://docs.tia.siemens.cloud/fixture/instruction";
        }
        File.WriteAllText(path, Encode(pattern), new UTF8Encoding(false));
        var example = Assert.Single(PlcPatternLibrary.Served(fixture.Root))!;
        Assert.Equal(exampleId, Text(example["id"]));
        Assert.Equal(Keys, Strings(example["releaseKeys"]));
        Assert.Equal(Strings(variant["cpuFamilies"]), Strings(example["cpuFamilies"]));
        Assert.Equal(Keys.Count * Strings(variant["cpuFamilies"]).Count(), cells.Length);
        var embedded = Assert.Single(ToolUsageGenerator.Generate(Root, new JsonArray(example.DeepClone()))["examples"]!.AsArray(),
            e => Text(e!["id"]) == exampleId)!;
        var file = Assert.Single(embedded["files"]!.AsArray())!;
        Assert.Equal(Hash(Encoding.UTF8.GetBytes(Text(file["content"]))), Text(file["contentSha256"]));

        foreach (var row in cells)
        {
            row!["status"] = "NOT RUN"; fixture.SaveEvidence(evidence);
            Assert.Empty(PlcPatternLibrary.Served(fixture.Root));
            row["status"] = "verified";
        }
    }

    [Fact]
    public void Not_run_library_leaves_generated_usage_byte_identical()
    {
        var generated = ToolUsageGenerator.Generate(Root);
        Assert.Equal(Encode(ToolUsageGenerator.Generate(Root, [])), Encode(generated));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Root, "src/Shared/ToolUsageData.json")), Encoding.UTF8.GetBytes(Encode(generated)));
        Assert.DoesNotContain(generated["languages"]!.AsArray(), l => Text(l!["id"]) == "plc-pattern");
        Assert.DoesNotContain(generated["examples"]!.AsArray(), e => Text(e!["id"]).StartsWith("pattern/", StringComparison.Ordinal));
    }

    [Fact]
    public void Analog_scaling_declares_both_fault_limits_and_bypasses_scaling_with_a_substitute()
    {
        string source = Source(Root, "reference/plc-patterns/analog-scaling/PatAnalogScaling.scl");
        Assert.Contains("WireBreakBelow : Real := -100.0;", source);
        Assert.Contains("OverflowAtOrAbove : Real := 32767.0;", source);
        Assert.Contains("SubstituteValue : Real := 0.0;", source);
        Assert.Contains("Fault : Bool;", source);
        Assert.Contains("#Fault := (#Raw < #WireBreakBelow) OR (#Raw >= #OverflowAtOrAbove);", source);
        Assert.Contains("#InRange := #ConfigurationValid AND NOT #Fault AND (#Raw >= #RawLow) AND (#Raw <= #RawHigh);", source);
        Assert.Contains("(#WireBreakBelow < #RawLow) AND (#OverflowAtOrAbove > #RawHigh)", source);
        int substitute = source.IndexOf("#FC_PatAnalogScaling := #SubstituteValue;", StringComparison.Ordinal);
        int guard = source.IndexOf("IF #ConfigurationValid AND NOT #Fault THEN", StringComparison.Ordinal);
        int normalize = source.IndexOf("#Fraction := NORM_X", StringComparison.Ordinal);
        int scale = source.IndexOf("#FC_PatAnalogScaling := SCALE_X", StringComparison.Ordinal);
        Assert.True(substitute >= 0 && substitute < guard && guard < normalize && normalize < scale);
        Assert.EndsWith("    END_IF;\nEND_FUNCTION\n", source);
    }

    [Fact]
    public void Sequence_station_holds_extension_during_work_and_prioritizes_loss_of_position()
    {
        string source = Source(Root, "reference/plc-patterns/sequence-station/PatSequenceStation.scl");
        Assert.Contains("#Extend := (#State.Step = 10) OR (#State.Step = 20);", source);
        Assert.Contains("#Work := (#State.Step = 20);", source);
        Assert.Contains("#Retract := (#State.Step = 30);", source);
        Assert.Contains("        20:\n"
            + "            IF NOT #Extended THEN\n"
            + "                #State.Fault := TRUE;\n"
            + "                #State.Step := 900;\n"
            + "                #State.Done := FALSE;\n"
            + "            ELSIF #WorkComplete THEN\n"
            + "                #State.Step := 30;\n"
            + "            END_IF;", source);
        Assert.True(source.IndexOf("IF NOT #Extended THEN", StringComparison.Ordinal)
            < source.IndexOf("#Extend :=", StringComparison.Ordinal));
        var pattern = Read(Root, "reference/plc-patterns/sequence-station/pattern.json");
        Assert.Contains(Strings(pattern["notesZhCn"]), n => n.Contains("单稳态阀", StringComparison.Ordinal)
            && n.Contains("双稳态阀", StringComparison.Ordinal));
    }

    [Fact]
    public void Star_delta_requires_released_feedback_and_latches_timed_mismatches_before_stopping_outputs()
    {
        string source = Source(Root, "reference/plc-patterns/star-delta/PatStarDelta.scl");
        Assert.Contains("StarFeedback : Bool;", source);
        Assert.Contains("DeltaFeedback : Bool;", source);
        Assert.Contains("FeedbackLimit : Time := T#500ms;", source);
        Assert.Contains("AND (#FeedbackLimit > T#0ms)", source);
        Assert.Contains("ELSIF (#Step = 0) AND #StartEdge AND NOT #StarFeedback AND NOT #DeltaFeedback THEN", source);
        Assert.Contains("IF #GapClock.Q AND NOT #StarFeedback AND NOT #DeltaFeedback THEN\n                #Step := 30;", source);
        Assert.Contains("#IO.StarOn := (#Step = 10) AND NOT #DeltaFeedback;", source);
        Assert.Contains("#IO.DeltaOn := (#Step = 30) AND NOT #StarFeedback;", source);
        Assert.Contains("#StarFeedbackClock(IN := (#IO.StarOn <> #StarFeedback), PT := #FeedbackLimit);", source);
        Assert.Contains("#DeltaFeedbackClock(IN := (#IO.DeltaOn <> #DeltaFeedback), PT := #FeedbackLimit);", source);
        Assert.Contains("IF #StarFeedbackClock.Q OR #DeltaFeedbackClock.Q THEN\n"
            + "        #FeedbackFault := TRUE;\n"
            + "        #Step := 0;\n"
            + "        #IO.MainOn := FALSE;\n"
            + "        #IO.StarOn := FALSE;\n"
            + "        #IO.DeltaOn := FALSE;\n"
            + "    END_IF;", source);
        Assert.Contains("OR #FeedbackFault THEN\n        #Step := 0;", source);
        Assert.Contains("IF #Reset AND #IO.Stop AND NOT #IO.Start AND NOT #StarFeedback AND NOT #DeltaFeedback THEN\n"
            + "        #FeedbackFault := FALSE;\n    END_IF;", source);
        Assert.Equal(1, source.Split("#FeedbackFault := FALSE;", StringSplitOptions.None).Length - 1);
        Assert.True(source.IndexOf("#IO.DeltaOn := (#Step = 30)", StringComparison.Ordinal)
            < source.IndexOf("IF #StarFeedbackClock.Q", StringComparison.Ordinal));
    }

    [Fact]
    public void Modbus_ports_use_the_same_hardware_identifier_type_and_remain_unverified()
    {
        var pattern = Read(Root, "reference/plc-patterns/modbus-rtu-master/pattern.json");
        foreach (var variant in pattern["variants"]!.AsArray())
        {
            string source = Source(Root, Text(Assert.Single(variant!["files"]!.AsArray())!["path"]));
            Assert.Contains("    Port : PORT;", source);
            Assert.Contains("PORT := #Port,", source);
            Assert.All(variant["instructions"]!.AsArray(), i => {
                Assert.Equal("unverified", Text(i!["verification"]));
                Assert.Equal("unverified", Text(i["version"]));
                Assert.Contains("PORT (hardware identifier)", Text(i["verificationNote"]));
            });
        }
    }

    [Fact]
    public void Fully_verified_variant_is_embedded_with_availability_and_native_scope()
    {
        using var fixture = new Fixture(); fixture.VerifyStartStop();
        var served = PlcPatternLibrary.Served(fixture.Root);
        var example = Assert.Single(served)!;
        Assert.Equal("pattern/start-stop", Text(example["id"]));
        Assert.Equal(Keys, Strings(example["releaseKeys"]));
        Assert.StartsWith("Real-machine verified", Text(example["validation"]));
        Assert.Contains("Behaviour on a running PLC NOT RUN", Text(example["validation"]));
        Assert.Contains("fixture-package", Text(example["validation"]));
        Assert.Equal("verified fixture-order-number fixture-firmware", Text(example["availability"]!["14sp1"]!["S7-1200"]));
        var usage = ToolUsageGenerator.Generate(Root, served);
        var language = Assert.Single(usage["languages"]!.AsArray(), l => Text(l!["id"]) == "plc-pattern")!;
        Assert.Contains("patterns", Strings(language["aliases"]));
        var embedded = Assert.Single(usage["examples"]!.AsArray(), e => Text(e!["id"]) == "pattern/start-stop")!;
        var file = Assert.Single(embedded["files"]!.AsArray())!;
        Assert.Equal(Hash(Encoding.UTF8.GetBytes(Text(file["content"]))), Text(file["contentSha256"]));
        Assert.Equal(File.ReadAllText(Path.Combine(Root, Text(file["path"]))), Text(file["content"]));
    }

    [Theory]
    [InlineData("NOT RUN")]
    [InlineData("failed")]
    public void Any_unfinished_or_failed_cell_holds_the_whole_variant(string status)
    {
        using var fixture = new Fixture(); fixture.VerifyStartStop();
        var evidence = fixture.Evidence();
        var row = fixture.StartStopRows(evidence).First(); row["status"] = status;
        if (status == "failed") row["nativeMessage"] = "fixture compile error";
        fixture.SaveEvidence(evidence);
        Assert.Empty(PlcPatternLibrary.Served(fixture.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_absence_is_disclosed_and_only_releases_without_verified_families_are_excluded(bool wholeRelease)
    {
        using var fixture = new Fixture(); fixture.VerifyStartStop();
        var evidence = fixture.Evidence();
        foreach (var row in fixture.StartStopRows(evidence).Where(r => Text(r["releaseKey"]) == "14sp1"
            && (wholeRelease || Text(r["cpuFamily"]) == "S7-1500")))
        {
            row["status"] = "absent"; row["nativeMessage"] = "fixture instruction unavailable";
            row["sourceUrl"] = "https://support.industry.siemens.com/cs/document/fixture";
        }
        fixture.SaveEvidence(evidence);
        var example = Assert.Single(PlcPatternLibrary.Served(fixture.Root))!;
        Assert.Equal(wholeRelease ? Keys.Skip(1) : Keys, Strings(example["releaseKeys"]));
        Assert.Equal("absent: fixture instruction unavailable", Text(example["availability"]!["14sp1"]!["S7-1500"]));
        Assert.Contains("14sp1/S7-1500", Text(example["validation"]));
        if (!wholeRelease) Assert.StartsWith("verified ", Text(example["availability"]!["14sp1"]!["S7-1200"]));
    }

    [Theory]
    [InlineData("sourceUrl")]
    [InlineData("nativeMessage")]
    public void Absence_requires_the_native_message_and_official_url(string missing)
    {
        using var fixture = new Fixture(); fixture.VerifyStartStop();
        var evidence = fixture.Evidence(); var row = fixture.StartStopRows(evidence).First();
        row["status"] = "absent"; row["nativeMessage"] = "fixture absence";
        row["sourceUrl"] = "https://docs.tia.siemens.cloud/fixture";
        row.Remove(missing); fixture.SaveEvidence(evidence);
        Assert.Throws<ReleaseException>(() => PlcPatternLibrary.Served(fixture.Root));
    }

    [Fact]
    public void All_absent_cells_cannot_serve_a_variant()
    {
        using var fixture = new Fixture();
        var evidence = fixture.Evidence();
        foreach (var row in fixture.StartStopRows(evidence))
        {
            row["status"] = "absent"; row["nativeMessage"] = "fixture absence";
            row["sourceUrl"] = "https://docs.tia.siemens.cloud/fixture";
        }
        fixture.SaveEvidence(evidence);
        Assert.Empty(PlcPatternLibrary.Served(fixture.Root));
    }

    [Theory]
    [InlineData("bom")]
    [InlineData("non-ascii")]
    [InlineData("crlf")]
    [InlineData("tab")]
    [InlineData("absolute")]
    [InlineData("ltime")]
    [InlineData("long-timer-instance")]
    [InlineData("long-literal")]
    [InlineData("undeclared")]
    [InlineData("block-order")]
    [InlineData("instance-type")]
    [InlineData("lint-error")]
    [InlineData("lint-warning")]
    public void Invalid_scl_source_is_refused(string mutation)
    {
        using var fixture = new Fixture();
        string path = Path.Combine(fixture.Root, "reference/plc-patterns/start-stop/PatStartStop.scl");
        string source = File.ReadAllText(path);
        switch (mutation)
        {
            case "bom": File.WriteAllBytes(path, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(source)).ToArray()); break;
            case "non-ascii": File.WriteAllBytes(path, Encoding.UTF8.GetBytes(source + "// 中文\n")); break;
            case "crlf": File.WriteAllBytes(path, Encoding.UTF8.GetBytes(source.Replace("\n", "\r\n", StringComparison.Ordinal))); break;
            case "tab": File.WriteAllBytes(path, Encoding.UTF8.GetBytes(source.Replace("    #Run", "\t#Run", StringComparison.Ordinal))); break;
            default:
                source = mutation switch {
                    "absolute" => source.Replace("#Run :=", "%Q0.0 :=", StringComparison.Ordinal),
                    "ltime" => source.Replace("Start : Bool;", "Start : LTIME;", StringComparison.Ordinal),
                    "long-timer-instance" => source.Replace("Start : Bool;", "Start : LTON_LTIME;", StringComparison.Ordinal),
                    "long-literal" => source.Replace("#Start", "LT#1s", StringComparison.Ordinal),
                    "undeclared" => source.Replace("Start : Bool;", "Start : MB_MASTER;", StringComparison.Ordinal),
                    "block-order" => source.Replace("FUNCTION_BLOCK \"FB_PatStartStop\"", "FUNCTION_BLOCK \"FB_PatDifferent\"", StringComparison.Ordinal),
                    "instance-type" => source.Replace("\n\"FB_PatStartStop\"\n", "\n\"FB_PatDifferent\"\n", StringComparison.Ordinal),
                    "lint-error" => source.Replace("END_FUNCTION_BLOCK", "END_IF;\nEND_FUNCTION_BLOCK", StringComparison.Ordinal),
                    "lint-warning" => source.Replace("#Run :=", "GOTO Exit;\n    #Run :=", StringComparison.Ordinal),
                    _ => throw new InvalidOperationException(mutation) };
                File.WriteAllText(path, source, new UTF8Encoding(false)); break;
        }
        Assert.Throws<ReleaseException>(() => PlcPatternLibrary.Load(fixture.Root));
    }

    [Theory]
    [InlineData("unknown-key")]
    [InlineData("nested-key")]
    [InlineData("missing-file")]
    [InlineData("escape-file")]
    [InlineData("unknown-tool")]
    [InlineData("unknown-family")]
    [InlineData("duplicate-variant")]
    [InlineData("contains-order")]
    [InlineData("instruction-url")]
    [InlineData("instruction-page")]
    [InlineData("instruction-family")]
    public void Invalid_pattern_metadata_is_refused(string mutation)
    {
        using var fixture = new Fixture();
        string id = mutation.StartsWith("instruction", StringComparison.Ordinal) ? "timers-ton-tof-tp" : "start-stop";
        string path = Path.Combine(fixture.Root, "reference/plc-patterns/" + id + "/pattern.json");
        var pattern = JsonNode.Parse(File.ReadAllText(path))!;
        var variant = pattern["variants"]![0]!;
        switch (mutation)
        {
            case "unknown-key": pattern["unknown"] = true; break;
            case "nested-key": variant["files"]![0]!["unknown"] = true; break;
            case "missing-file": variant["files"]![0]!["path"] = "reference/plc-patterns/start-stop/missing.scl"; break;
            case "escape-file": variant["files"]![0]!["path"] = "reference/plc-patterns/start-stop/../timers-ton-tof-tp/PatTimers.scl"; break;
            case "unknown-tool": variant["tools"]![0] = "UnknownTool"; break;
            case "unknown-family": variant["cpuFamilies"]![0] = "S7-1200 G2"; break;
            case "duplicate-variant": pattern["variants"]!.AsArray().Add(variant.DeepClone()); break;
            case "contains-order":
                var blocks = variant["contains"]!.AsArray();
                var first = blocks[0]!.DeepClone(); blocks[0] = blocks[1]!.DeepClone(); blocks[1] = first; break;
            case "instruction-url": variant["instructions"]![0]!["sourceUrl"] = "https://example.com"; break;
            case "instruction-page": variant["instructions"]![0]!["verification"] = "verified"; break;
            case "instruction-family": variant["instructions"]![0]!["cpuFamilies"] = new JsonArray("S7-1500"); break;
        }
        File.WriteAllText(path, Encode(pattern), new UTF8Encoding(false));
        Assert.Throws<ReleaseException>(() => PlcPatternLibrary.Load(fixture.Root));
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("status")]
    [InlineData("date")]
    [InlineData("package-commit")]
    [InlineData("tiaUpdate")]
    [InlineData("cpu")]
    [InlineData("firmware")]
    [InlineData("runId")]
    [InlineData("compile-error")]
    [InlineData("unknown-key")]
    public void Invalid_evidence_is_refused(string mutation)
    {
        using var fixture = new Fixture(); fixture.VerifyStartStop();
        var evidence = fixture.Evidence(); var rows = evidence["rows"]!.AsArray();
        var row = fixture.StartStopRows(evidence).First();
        switch (mutation)
        {
            case "extra": var extra = row.DeepClone(); extra["pattern"] = "nonexistent"; rows.Add(extra); break;
            case "duplicate": rows.Add(row.DeepClone()); break;
            case "missing": rows.RemoveAt(0); break;
            case "status": row["status"] = "passed"; break;
            case "package-commit": row.Remove("package"); row.Remove("commit"); break;
            case "compile-error": row["compile"]!["errors"] = 1; break;
            case "unknown-key": row["unknown"] = true; break;
            default: row.Remove(mutation); break;
        }
        fixture.SaveEvidence(evidence);
        Assert.Throws<ReleaseException>(() => PlcPatternLibrary.Load(fixture.Root));
    }

    [Fact]
    public void Unverified_manual_names_hold_a_variant_even_with_complete_vm_evidence()
    {
        using var fixture = new Fixture();
        var evidence = fixture.Evidence();
        foreach (var row in evidence["rows"]!.AsArray().Where(r => Text(r!["pattern"]) == "timers-ton-tof-tp")) Fixture.Verify(row!.AsObject());
        fixture.SaveEvidence(evidence);
        Assert.Empty(PlcPatternLibrary.Served(fixture.Root));
    }

    [Theory]
    [InlineData("missing-verification")]
    [InlineData("empty-verification")]
    [InlineData("unverified-version")]
    public void Instruction_verification_cannot_be_omitted_or_claim_an_unverified_version(string mutation)
    {
        using var fixture = new Fixture();
        var evidence = fixture.Evidence();
        foreach (var row in evidence["rows"]!.AsArray().Where(r => Text(r!["pattern"]) == "timers-ton-tof-tp")) Fixture.Verify(row!.AsObject());
        fixture.SaveEvidence(evidence);
        string path = Path.Combine(fixture.Root, "reference/plc-patterns/timers-ton-tof-tp/pattern.json");
        var pattern = JsonNode.Parse(File.ReadAllText(path))!;
        var instructions = pattern["variants"]![0]!["instructions"]!.AsArray();
        foreach (var instruction in instructions)
        {
            instruction!["verification"] = "verified"; instruction["version"] = "1.0";
            instruction["sourceUrl"] = "https://docs.tia.siemens.cloud/fixture/instruction";
        }
        File.WriteAllText(path, Encode(pattern), new UTF8Encoding(false));
        Assert.Equal("pattern/timers-ton-tof-tp", Text(Assert.Single(PlcPatternLibrary.Served(fixture.Root))!["id"]));
        var changed = instructions[0]!.AsObject();
        switch (mutation)
        {
            case "missing-verification": changed.Remove("verification"); break;
            case "empty-verification": changed["verification"] = ""; break;
            case "unverified-version": changed["version"] = "unverified"; break;
        }
        File.WriteAllText(path, Encode(pattern), new UTF8Encoding(false));
        Assert.Throws<ReleaseException>(() => PlcPatternLibrary.Served(fixture.Root));
    }

    public static IEnumerable<object[]> LadderPatterns() => new[] { "alarm-latch", "counters-ctu-ctd-ctud", "cylinder-timeout",
        "motor-fwd-rev", "star-delta", "start-stop", "timers-ton-tof-tp" }.Select(id => new object[] { id });

    [Fact]
    public void Ladder_variants_have_distinct_db_and_fc_numbers_in_pattern_id_order()
    {
        var dbNumbers = new List<int>();
        var fcNumbers = new List<int>();
        foreach (var row in LadderPatterns())
        {
            var pattern = Read(Root, "reference/plc-patterns/" + (string)row[0] + "/pattern.json");
            var variant = Assert.Single(pattern["variants"]!.AsArray(), v => Text(v!["id"]) == "lad")!;
            var files = variant["files"]!.AsArray();
            var db = Read(Root, Text(Assert.Single(files, f => Text(f!["path"]).EndsWith(".db.json", StringComparison.Ordinal))!["path"]));
            var fc = Read(Root, Text(Assert.Single(files, f => Text(f!["path"]).EndsWith(".rung.json", StringComparison.Ordinal))!["path"]));
            dbNumbers.Add(db["dbNumber"]!.GetValue<int>());
            fcNumbers.Add(fc["blockNumber"]!.GetValue<int>());
        }
        Assert.Equal(new[] { 9101, 9102, 9103, 9104, 9105, 9106, 9107 }, dbNumbers);
        Assert.Equal(dbNumbers, fcNumbers);
        Assert.Equal(7, dbNumbers.Distinct().Count());
        Assert.Equal(7, fcNumbers.Distinct().Count());
    }

    [Fact]
    public void Ladder_timers_keep_independent_presets_and_scl_output_names()
    {
        const string directory = "reference/plc-patterns/timers-ton-tof-tp/lad/";
        var db = Read(Root, directory + "PatTimers.db.json");
        var fc = Read(Root, directory + "PatTimers.rung.json");
        var settings = db["staticMembers"]!.AsArray().Where(m => m!["startValue"] != null).ToArray();
        string[] times = ["OnTime", "OffTime", "PulseTime"];
        string[] outputs = ["OnDone", "OffActive", "PulseActive"];
        string[] elapsed = ["OnElapsed", "OffElapsed", "PulseElapsed"];
        Assert.Equal(times, settings.Select(m => Text(m!["name"])));
        Assert.All(settings, m => { Assert.Equal("Time", Text(m!["datatype"])); Assert.Equal("T#1s", Text(m["startValue"])); });
        Assert.DoesNotContain(db["staticMembers"]!.AsArray(), m => Text(m!["name"]) == "Delay");
        Assert.Equal(outputs, fc["outputs"]!.AsArray().Select(m => Text(m!["name"])));
        Assert.Equal(new[] { "TON", "TOF", "TP" }, fc["networks"]!.AsArray().Select(n => Text(n!["rung"]!["outputs"]![0]!["type"])));
        for (int i = 0; i < times.Length; i++)
        {
            var timer = fc["networks"]![i]!["rung"]!["outputs"]![0]!;
            Assert.Equal(new[] { "DB_PatTimersLad", times[i] }, Strings(timer["pt"]!["global"]));
            Assert.Equal(new[] { outputs[i] }, Strings(Assert.Single(timer["q"]!.AsArray())!["operand"]!["local"]));
            Assert.Equal(new[] { "DB_PatTimersLad", elapsed[i] }, Strings(timer["et"]!["global"]));
            Assert.Contains(db["staticMembers"]!.AsArray(), m => Text(m!["name"]) == elapsed[i] && Text(m["datatype"]) == "Time");
        }
    }

    [Theory]
    [MemberData(nameof(LadderPatterns))]
    public void Ladder_inputs_build_v21_state_db_and_fc_with_rungs_and_validate_official_xsd(string id)
    {
        var pattern = Read(Root, "reference/plc-patterns/" + id + "/pattern.json");
        var variant = Assert.Single(pattern["variants"]!.AsArray(), v => Text(v!["id"]) == "lad")!;
        Assert.Equal(new[] { "S7-1200", "S7-1500" }, Strings(variant["cpuFamilies"]));
        Assert.Contains("V21 only", Text(variant["importSequence"]));
        var documents = PlcPatternLadderInputs.Build(Root, variant);
        Assert.Equal(2, documents.Count);
        Assert.NotNull(documents[0].Root!.Element("SW.Blocks.GlobalDB"));
        Assert.NotNull(documents[1].Root!.Element("SW.Blocks.FC"));
        Assert.All(documents, d => Assert.Equal("V21", (string?)d.Root!.Element("Engineering")!.Attribute("version")));
        XNamespace flg = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";
        var nets = documents[1].Descendants(flg + "FlgNet").ToArray();
        Assert.NotEmpty(nets);
        Assert.DoesNotContain(documents[1].Descendants(), e => e.Name.LocalName is "Call" or "StructuredText");
        string? sdkRoot = Environment.GetEnvironmentVariable("TIA_MCP_TEST_PUBLIC_API_ROOT");
        if (string.IsNullOrEmpty(sdkRoot))
        {
            Console.WriteLine("[XSD NOT RUN] Set TIA_MCP_TEST_PUBLIC_API_ROOT for V21 LAD and DB interface schema validation.");
            return;
        }
        string directory = Path.Combine(sdkRoot, "TIA_V21_PublicAPI/V21/Schemas");
        if (!Directory.Exists(directory)) directory = Path.Combine(sdkRoot, "TIA_V21_PublicAPI/V21/net48/Schemas");
        foreach (var fragment in nets.Concat(documents[0].Descendants()
            .Where(e => e.Name.LocalName == "Sections")))
        {
            string schema = fragment.Name.LocalName == "FlgNet" ? "SW.PlcBlocks.LADFBD_v5.xsd" : "SW.InterfaceSections_v5.xsd";
            var schemas = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
            var issues = new List<string>();
            schemas.ValidationEventHandler += (_, args) => issues.Add(args.Message);
            schemas.Add(fragment.Name.NamespaceName, Path.Combine(directory, schema)); schemas.Compile();
            new XDocument(new XElement(fragment)).Validate(schemas, (_, args) => issues.Add(args.Message));
            Assert.True(issues.Count == 0, id + " " + schema + ": " + string.Join("; ", issues));
        }
    }

    [Fact]
    public void Ladder_gate_requires_both_v21_families_and_keeps_the_scl_example_identity()
    {
        using var fixture = new Fixture();
        var evidence = fixture.Evidence();
        var cells = evidence["rows"]!.AsArray().Where(r => Text(r!["pattern"]) == "start-stop" && Text(r["variant"]) == "lad").ToArray();
        Assert.Equal(2, cells.Length);
        Assert.All(cells, r => Assert.Equal("21", Text(r!["releaseKey"])));
        Fixture.Verify(cells[0]!.AsObject()); fixture.SaveEvidence(evidence);
        Assert.Empty(PlcPatternLibrary.Served(fixture.Root));
        Fixture.Verify(cells[1]!.AsObject()); fixture.SaveEvidence(evidence);
        var example = Assert.Single(PlcPatternLibrary.Served(fixture.Root))!;
        Assert.Equal("pattern/start-stop/lad", Text(example["id"]));
        Assert.Equal(new[] { "21" }, Strings(example["releaseKeys"]));
        Assert.Equal("lad", Text(example["sourceLanguage"]));
        Assert.Equal("builder-input", Text(example["kind"]));
        Assert.Equal(new[] { "21" }, example["availability"]!.AsObject().Select(p => p.Key));
        Assert.Contains("V21 only; global DB and LAD FC XML import", Text(example["validation"]));
        var usage = ToolUsageGenerator.Generate(Root, PlcPatternLibrary.Served(fixture.Root));
        var embedded = Assert.Single(usage["examples"]!.AsArray(), e => Text(e!["id"]) == "pattern/start-stop/lad")!;
        Assert.Equal(2, embedded["files"]!.AsArray().Count);
        Assert.All(embedded["files"]!.AsArray(), f => Assert.Equal(Hash(Encoding.ASCII.GetBytes(Text(f!["content"]))), Text(f["contentSha256"])));
        fixture.VerifyStartStop();
        Assert.Contains(PlcPatternLibrary.Served(fixture.Root), e => Text(e!["id"]) == "pattern/start-stop");
        evidence = fixture.Evidence();
        var failed = evidence["rows"]!.AsArray().First(r => Text(r!["pattern"]) == "start-stop" && Text(r["variant"]) == "lad")!;
        failed["status"] = "failed"; failed["nativeMessage"] = "fixture LAD compile error";
        fixture.SaveEvidence(evidence);
        Assert.Equal("pattern/start-stop", Text(Assert.Single(PlcPatternLibrary.Served(fixture.Root))!["id"]));
    }

    [Theory]
    [InlineData("unknown-rung-key")][InlineData("malformed-json")][InlineData("undeclared-local")]
    [InlineData("undeclared-global")][InlineData("state-type")][InlineData("instruction")]
    [InlineData("contains")][InlineData("encoding")][InlineData("absolute-operand")][InlineData("unknown-db-key")]
    public void Invalid_ladder_inputs_are_rejected_before_any_evidence_can_serve_them(string mutation)
    {
        using var fixture = new Fixture();
        string metadataPath = Path.Combine(fixture.Root, "reference/plc-patterns/timers-ton-tof-tp/pattern.json");
        var metadata = JsonNode.Parse(File.ReadAllText(metadataPath))!;
        var variant = Assert.Single(metadata["variants"]!.AsArray(), v => Text(v!["id"]) == "lad")!;
        string dbPath = Path.Combine(fixture.Root, Text(variant["files"]![0]!["path"]));
        string rungPath = Path.Combine(fixture.Root, Text(variant["files"]![1]!["path"]));
        var db = JsonNode.Parse(File.ReadAllText(dbPath))!;
        var block = JsonNode.Parse(File.ReadAllText(rungPath))!;
        var rung = block["networks"]![0]!["rung"]!;
        switch (mutation)
        {
            case "unknown-rung-key": rung["unknown"] = true; break;
            case "undeclared-local": rung["series"]![0]!["operand"]!["local"]![0] = "Missing"; break;
            case "undeclared-global": rung["outputs"]![0]!["instance"]!["global"]![0] = "DB_Missing"; break;
            case "absolute-operand": rung["series"]![0]!["operand"]!["local"]![0] = "%I0.0"; break;
            case "state-type": db["staticMembers"]![0]!["datatype"] = "TOF_TIME"; break;
            case "unknown-db-key": db["unknown"] = true; break;
            case "instruction": variant["instructions"]!.AsArray().RemoveAt(0); break;
            case "contains": variant["contains"]![1]!["name"] = "FC_PatWrong"; break;
            case "encoding": variant["files"]![1]!["encoding"] = "UTF-8"; break;
        }
        File.WriteAllText(metadataPath, Encode(metadata), new UTF8Encoding(false));
        File.WriteAllText(dbPath, Encode(db), new UTF8Encoding(false));
        File.WriteAllText(rungPath, mutation == "malformed-json" ? "{" : Encode(block), new UTF8Encoding(false));
        var refusal = Assert.Throws<ReleaseException>(() => PlcPatternLibrary.Load(fixture.Root));
        if (mutation == "absolute-operand")
        {
            Assert.Contains("/operand/local/0", refusal.Message);
            Assert.Contains("simple PLC identifier", refusal.Message);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(PlcPatternLibraryTests.Root, "bin-build", "plc-pattern-tests", Guid.NewGuid().ToString("N"));
        public Fixture()
        {
            string source = Path.Combine(PlcPatternLibraryTests.Root, "reference/plc-patterns");
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(Root, "reference/plc-patterns", Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
            }
            Directory.CreateDirectory(Path.Combine(Root, "manifest"));
            File.Copy(Path.Combine(PlcPatternLibraryTests.Root, "manifest/version-tools.json"), Path.Combine(Root, "manifest/version-tools.json"));
        }
        public JsonNode Evidence() => Read(Root, "reference/plc-patterns/evidence.json");
        public IEnumerable<JsonObject> StartStopRows(JsonNode evidence) => evidence["rows"]!.AsArray()
            .Where(r => Text(r!["pattern"]) == "start-stop" && Text(r["variant"]) == "scl").Select(r => r!.AsObject());
        public void SaveEvidence(JsonNode evidence) => File
            .WriteAllText(Path.Combine(Root, "reference/plc-patterns/evidence.json"), Encode(evidence), new UTF8Encoding(false));
        public void VerifyStartStop()
        {
            var evidence = Evidence(); foreach (var row in StartStopRows(evidence)) Verify(row); SaveEvidence(evidence);
        }
        public static void Verify(JsonObject row)
        {
            row["status"] = "verified"; row["date"] = "2026-11-17"; row["package"] = "fixture-package";
            row["tiaUpdate"] = "fixture-update"; row["cpu"] = "fixture-order-number"; row["firmware"] = "fixture-firmware";
            row["compile"] = new JsonObject { ["errors"] = 0, ["warnings"] = 0 }; row["runId"] = "fixture-run";
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
