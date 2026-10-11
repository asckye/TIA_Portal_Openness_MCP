using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using Xunit;
using static TiaMcp.ReleaseTool.CatalogJson;

namespace TiaMcp.ReleaseTool.Tests;

[Trait("Category", "Generators")]
public sealed class GeneratorTests
{
    private static string Root => Repository.FindRoot();
    [Theory]
    [InlineData("generate-catalogs")]
    [InlineData("generate-tool-usage")]
    [InlineData("generate-release-migration")]
    [InlineData("audit-version-tools")]
    [InlineData("audit-tool-usage")]
    [InlineData("check-tools-list")]
    public void CommandsAreRegistered(string command) => Assert.Contains(command, CommandLine.Commands);

    [Fact]
    public void ProductBytesAndLegacyGeneratorNamesRemainReviewable()
    {
        var root = Root; var outputs = CatalogGenerator.Generate(root, legacyNames: true);
        outputs["src/Shared/ToolUsageData.json"] = Encode(ToolUsageGenerator.Generate(root)); outputs[ReleaseMigrationGenerator.Output] = ReleaseMigrationGenerator.Render(root, legacyNames: true);
        foreach (var p in outputs)
        {
            var expected = Source(root, p.Key);
            if (p.Key == "docs/reference/version-tool-catalog.md" || p.Key == "docs/reference/real-machine-ledger.md") expected = expected.Replace("`generate-catalogs`", "`Generate-Phase6Plan.py`", StringComparison.Ordinal);
            if (p.Key == ReleaseMigrationGenerator.Output) expected = expected.Replace("reference/tool-catalog/v3-v4-renames.json.", "docs/development/phase6-review.md.", StringComparison.Ordinal);
            Assert.Equal(expected, p.Value);
        }
    }
    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    [InlineData("tr-TR")]
    public void GeneratorsUseInvariantFormatting(string culture)
    {
        var old = CultureInfo.CurrentCulture; var oldUi = CultureInfo.CurrentUICulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture); var outputs = CatalogGenerator.Generate(Root); outputs[ReleaseMigrationGenerator.Output] = ReleaseMigrationGenerator.Render(Root); outputs["src/Shared/ToolUsageData.json"] = Encode(ToolUsageGenerator.Generate(Root)); Assert.All(outputs.Values, s => { Assert.DoesNotContain('\r', s); Assert.False(s.StartsWith('\ufeff')); }); Assert.Equal(0, Write(Root, outputs, true)); }
        finally { CultureInfo.CurrentCulture = old; CultureInfo.CurrentUICulture = oldUi; }
    }
    [Theory]
    [InlineData("byte")]
    [InlineData("crlf")]
    [InlineData("bom")]
    [InlineData("missing")]
    public void CheckRejectsByteLineEndingBomAndMissingChanges(string mutation)
    {
        var path = Path.Combine(Root, "bin-build/refactor/check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        var expected = new Dictionary<string, string> { ["output.json"] = "{\n  \"value\": 1\n}\n" }; var bytes = Encoding.UTF8.GetBytes(expected["output.json"]);
        try { File.WriteAllBytes(Path.Combine(path, "output.json"), bytes); Assert.Equal(0, Write(path, expected, true));
            if (mutation == "missing") File.Delete(Path.Combine(path, "output.json"));
            else File.WriteAllBytes(Path.Combine(path, "output.json"), mutation == "bom" ? new byte[] { 239, 187, 191 }.Concat(bytes).ToArray() : Encoding.UTF8.GetBytes(mutation == "crlf" ? expected["output.json"].Replace("\n", "\r\n", StringComparison.Ordinal) : expected["output.json"].Replace('1', '2')));
            Assert.Equal(1, Write(path, expected, true)); Assert.Equal(0, Write(path, expected, false)); Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(path, "output.json")));
        } finally { Directory.Delete(path, true); }
    }
    [Theory]
    [InlineData("owner")]
    [InlineData("operation")]
    [InlineData("release")]
    [InlineData("missing")]
    [InlineData("duplicate-rename")]
    [InlineData("missing-rename")]
    [InlineData("json-name")]
    public void ReviewDataFailsClosed(string mutation)
    {
        var data = new CatalogData(Root); var additions = (JsonObject)data.Additions.DeepClone(); var mapping = Read(Root, "reference/tool-catalog/v3-v4-renames.json").AsObject().ToDictionary(p => p.Key, p => Text(p.Value)); var archived = Set(data.Archives.Values.SelectMany(v => v.Select(p => p.Key)));
        switch (mutation) { case "owner": additions["RenderPlcBlock"]!["owner"] = "P6-67"; break; case "operation": additions["RenderPlcBlock"]!["operation"] = "READ"; break; case "release": additions["RenderPlcBlock"]!["releases"] = new JsonArray("21"); break; case "missing": additions.Remove("RenderPlcBlock"); break; case "duplicate-rename": mapping["CompileSoftware"] = mapping["CompileAndDiagnosePlc"]; break; case "missing-rename": mapping.Remove(mapping.Keys.First()); break; case "json-name": mapping[mapping.Keys.First()] = "GetJsonResult"; break; }
        Assert.Throws<ReleaseException>(() => CatalogData.ValidateReviewedData(additions, mapping, archived));
    }
    [Theory]
    [InlineData("partial")]
    [InlineData("duplicate")]
    [InlineData("unparsed")]
    [InlineData("override")]
    [InlineData("unknown-override")]
    public void PortedFamiliesPreserveActionSwitchesAndToolReleaseDecisions(string kind)
    {
        const string row = "new Family(\"family\", \"prefix\", new[] { \"20\", \"21\" }, new[] { \"Tool\" }, new Dictionary<string, string[]> { [\"write\"] = new[] { \"21\" } })";
        if (kind == "partial") { var parsed = PortedFamilies.Parse(row); Assert.Contains("Tool", PortedFamilies.Additions(parsed, "20")); return; }
        if (kind == "override") { var parsed = PortedFamilies.Parse(row + "\nToolReleases = new Dictionary<string, string[]>(StringComparer.Ordinal) { [\"Tool\"] = new[] { \"21\" }\n};"); Assert.Empty(PortedFamilies.Additions(parsed, "20")); Assert.Contains("Tool", PortedFamilies.Additions(parsed, "21")); return; }
        Assert.Throws<ArgumentException>(() => PortedFamilies.Parse(kind == "duplicate" ? row + row : kind == "unparsed" ? row + "new Family(other, prefix, releases, tools)" : row + "\nToolReleases = new Dictionary<string, string[]>(StringComparer.Ordinal) { [\"Other\"] = new[] { \"21\" }\n};"));
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("double")]
    [InlineData("extra")]
    [InlineData("merged")]
    public void RosterMappingRejectsUnreviewedRegistrations(string kind)
    {
        var targets = new Dictionary<string, string> { ["Old"] = "New" }; var registered = Set(kind == "missing" ? [] : kind == "double" ? ["Old", "New"] : kind == "extra" ? ["New", "Extra"] : ["GetAuthoringGuide", "GetToolUsage"]);
        if (kind == "merged") { targets = new() { ["GetAuthoringGuide"] = "GetToolUsage" }; }
        Assert.Throws<ReleaseException>(() => CatalogData.Resolve(targets.Keys, registered, targets, true));
    }
    [Theory]
    [InlineData("valuesJson", "string")]
    [InlineData("values", "string")]
    [InlineData("values", "AttributeMap<Scalar>")]
    public void TypedTransitionPreservesNativeShape(string parameter, string type)
    {
        var actual = new Dictionary<string, string> { [parameter] = type }; var expected = new Dictionary<string, string> { ["valuesJson"] = "AttributeMap<Scalar>" };
        if (parameter == "values" && type == "AttributeMap<Scalar>") CatalogGenerator.ValidateTransition("Keep", "Keep", "Keep", actual, expected);
        else Assert.Throws<ReleaseException>(() => CatalogGenerator.ValidateTransition("Keep", "Keep", "Keep", actual, expected));
        Assert.Throws<ReleaseException>(() => CatalogGenerator.ValidateTransition("Old", "Old", "New", actual, expected));
    }
    [Theory]
    [InlineData("unscoped")]
    [InlineData("scoped")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    public void BehaviorAcceptanceIsReleaseScoped(string kind)
    {
        var ledger = string.Join('\n', "DEVICE IMPORT EXPORT SESSION CLOSE SOURCE COMPILE FALLBACK".Split(' ').Select(f => "| P6-" + f + " | NOT RUN | acceptance |"));
        if (kind == "duplicate" || kind == "unknown") { ledger = ledger.Replace("NOT RUN", kind == "duplicate" ? "L5[19]=PASSED L5[19]=FAILED" : "L5[15]=PASSED", StringComparison.Ordinal); Assert.Throws<ReleaseException>(() => CatalogGenerator.BehaviorRecords(ledger)); return; }
        var records = CatalogGenerator.BehaviorRecords(ledger.Replace("NOT RUN", kind == "scoped" ? "L5[19]=PASSED; L5[20]=FAILED" : "offline PASSED; L5 PASSED", StringComparison.Ordinal)); Assert.Equal(kind == "scoped" ? 8 : 0, records.Count(r => Text(r!["state"]) == "safe-v4"));
    }
    [Fact]
    public void ParameterParserRetainsDescriptionsDefaultsAndNestedCommas()
    {
        var p = CatalogGenerator.Parameters("public void Probe([Description(\"Message\")] string name = \"\", Dictionary<string, int> values = null!, int count = 3)"); Assert.Equal("Message", Text(p["name"]!["description"])); Assert.Equal("Dictionary<string,int>", Text(p["values"]!["type"])); Assert.Equal("3", Text(p["count"]!["default"]));
    }
    [Theory]
    [InlineData("overwrite")]
    [InlineData("path")]
    [InlineData("full-parameters")]
    [InlineData("sequence")]
    [InlineData("readback")]
    [InlineData("compile")]
    [InlineData("approval")]
    [InlineData("identity")]
    [InlineData("release")]
    public void RetestLessonsCannotDisappear(string mutation)
    {
        var calls = Read(Root, "reference/tool-examples/calls.json"); var sequences = Read(Root, "reference/tool-examples/sequences.json").AsArray(); var metadata = Read(Root, "reference/tool-examples/metadata.json"); var table = sequences.First(s => Text(s!["id"]) == "sequence/foundation-tag-table-round-trip")!; var block = sequences.First(s => Text(s!["id"]) == "sequence/foundation-block-round-trip")!; var entry = calls["profiles"]!["plc-foundation"]!["ImportPlcTagTable"]!;
        switch (mutation) { case "overwrite": entry["execution"]!["arguments"]!.AsObject().Remove("overwrite"); break; case "path": entry["note"] = Text(entry["note"]).Replace("normalized full path", "", StringComparison.Ordinal); break; case "full-parameters": calls["profiles"]!["full-engine"]!["ImportPlcBlock"]!["arguments"]!["overwrite"] = true; break; case "sequence": sequences.Remove(table); break; case "readback": table["steps"]!.AsArray().RemoveAt(table["steps"]!.AsArray().Count - 1); break; case "compile": block["steps"]!.AsArray().RemoveAt(block["steps"]!.AsArray().Count - 1); break; case "approval": metadata["resultReading"]!["ResponseMessage"]!.AsArray().RemoveAt(metadata["resultReading"]!["ResponseMessage"]!.AsArray().Count - 1); break; case "identity": table["steps"]!.AsArray()[^2]!["arguments"]!.AsObject().Remove("expectedProjectFile"); break; case "release": table["releaseKeys"]!.AsArray().RemoveAt(5); break; }
        Assert.ThrowsAny<Exception>(() => ToolUsageGenerator.ValidateRetest(Root, calls, sequences, metadata));
    }
    [Theory]
    [InlineData("ExportPlcBlocks")]
    [InlineData("ExportPlcTypes")]
    [InlineData("ImportPlcBlocksFromDirectory")]
    [InlineData("ImportPlcProgramFromDirectory")]
    public void FoundationBatchExamplesRequireExactPaths(string name) { var calls = Read(Root, "reference/tool-examples/calls.json"); calls["profiles"]!["plc-foundation"]![name]!["arguments"]!["softwarePath"] = "PLC_1"; Assert.Throws<ReleaseException>(() => ToolUsageGenerator.ValidateFoundation(calls, [])); }
    [Fact]
    public void SingleWriterOwnershipIsExplicit()
    {
        Assert.Contains("ToolProfiles", CatalogGenerator.Resource); Assert.DoesNotContain("ToolProfiles", Source(Root, "build-tools/release/ToolUsageGenerator.cs"));
        var audits = Source(Root, "build-tools/release/CatalogAudits.cs"); Assert.DoesNotContain("results[\"manifest/version-tools.json\"]", audits); Assert.DoesNotContain("results[\"docs/reference/version-tool-catalog.md\"]", audits);
        foreach (var path in new[] { "scripts/checks/Test-ToolUsage.py", "scripts/checks/Snapshot-SharedToolUsage.py" }) Assert.Contains("audit-tool-usage is the only writer", Source(Root, path));
    }
    [Fact]
    public void UnknownTaxonomyGrammarAndCategoryDowngradeAreRefused()
    {
        var source = Source(Root, "src/Logic/ModelContextProtocol/ToolTaxonomy.cs"); Assert.Equal("ONLINE-WRITE", ToolsListChecks.Classifier(source)("DownloadSentinel")); Assert.Equal("UNSPECIFIED", ToolsListChecks.Classifier(source)("Anything")); Assert.Throws<ReleaseException>(() => ToolsListChecks.Classifier(source.Replace("Starts(\"Compile\", \"Run\")", "OtherRule(name)", StringComparison.Ordinal)));
        Assert.Throws<ReleaseException>(() => ToolsListChecks.CompareOperation("Renamed", "WRITE", new JsonObject { ["Renamed"] = "ONLINE-WRITE" }, new JsonObject { ["EXCEPTIONS"] = new JsonObject() }, new JsonObject()));
    }
    [Fact]
    public void AuditCommandsRefuseMissingInputs()
    {
        Assert.ThrowsAny<Exception>(() => CatalogAudits.Usage(Root, Path.Combine(Root, "bin-build/missing-audit-inputs")));
        Assert.ThrowsAny<Exception>(() => CatalogAudits.Version(Root, Path.Combine(Root, "bin-build/missing-sdk"), "bin-build/test-api", Path.Combine(Root, "bin-build/missing-audit-inputs")));
    }
    [Fact]
    public void NativeAuditCountsXmlOverloadsPropertiesAndExactCompiledMembers()
    {
        var directory = Path.Combine(Root, "bin-build/refactor/audit-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "inputs"); var sdk = Path.Combine(directory, "sdk"); var data = new CatalogData(Root);
        void Put(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); }
        try
        {
            foreach (var key in Keys)
            {
                var relative = key switch { "14sp1" => "TIA_V14SP1_PublicAPI/V14 SP1", "15.1" => "TIA_V15.1_PublicAPI/V15.1", "21" => "TIA_V21_PublicAPI/V21/net48", _ => $"TIA_V{key}_PublicAPI/V{key}" };
                Put(Path.Combine(sdk, relative, "Siemens.Engineering.xml"), "<doc><members><member name=\"M:Siemens.Engineering.Domain.Widget.Import(System.String)\"/><member name=\"M:Siemens.Engineering.Domain.Widget.Import(System.Int32)\"/><member name=\"P:Siemens.Engineering.Domain.Widget.Name\"/><member name=\"M:Siemens.Engineering.Domain.Widget.Export\"/><member name=\"M:Siemens.Engineering.Private.Secret.Invoke\"/><member name=\"M:Siemens.Engineering.Domain.Widget.Dispose\"/></members></doc>");
                Put(Path.Combine(sdk, relative, "Siemens.Engineering.AddIn.xml"), "<invalid/>");
                var coverage = key is "20" or "21" ? $"releases/v3.3.0/v{key}/native-call-coverage-v{key}.json" : $"multi-version/adapters/coverage-{key}.json";
                Put(Path.Combine(input, coverage), "{\"sites\":[{\"member\":\"void Siemens.Engineering.Domain.Widget::Import(System.String)\"},{\"member\":\"string Siemens.Engineering.Domain.Widget::get_Name()\"},{\"member\":\"ignored\"}]}");
                if (key is not "20" and not "21") Put(Path.Combine(input, $"multi-version/tools-{key}.json"), Encode(new JsonObject { ["tools"] = new JsonArray(data.Rosters[key].Select(n => (JsonNode)new JsonObject { ["name"] = n }).ToArray()) }));
            }
            var outputs = CatalogAudits.Version(Root, sdk, "candidates", input); var legacy = CatalogAudits.Version(Root, sdk, "candidates", input, legacyLineEndings: true);
            Assert.Equal(9, outputs.Count); Assert.DoesNotContain("manifest/version-tools.json", outputs.Keys); Assert.DoesNotContain("docs/reference/version-tool-catalog.md", outputs.Keys);
            foreach (var p in outputs) Assert.Equal(p.Value.Replace("\n", "\r\n", StringComparison.Ordinal), legacy[p.Key]);
            var statistics = JsonNode.Parse(outputs["manifest/version-api-audit.json"])!["versions"]!.AsArray();
            Assert.Equal(8, statistics.Count);
            foreach (var row in statistics) { Assert.Equal(4, row!["domainSignatures"]!.GetValue<int>()); Assert.Equal(3, row["domainMemberNames"]!.GetValue<int>()); Assert.Equal(2, row["compiledDirectMemberNames"]!.GetValue<int>()); Assert.Equal(1, row["withoutDirectMemberReference"]!.GetValue<int>()); Assert.Equal(1, row["xmlFiles"]!.GetValue<int>()); }
            foreach (var key in Keys) Assert.Equal("M:Siemens.Engineering.Domain.Widget.Export", Text(JsonNode.Parse(outputs[Path.Combine("candidates", $"api-candidates-{key}.json")])!["members"]![0]));
            Put(Path.Combine(input, "multi-version/tools-19.json"), "{\"tools\":[]}"); Assert.Throws<ReleaseException>(() => CatalogAudits.Version(Root, sdk, "candidates", input));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void UsageAuditRequiresEveryToolReleaseAndNoNativeExampleCalls()
    {
        var matrix = Read(Root, "manifest/version-tools.json")["releases"]!.AsObject(); var catalog = Read(Root, "src/Shared/ToolUsageData.json"); var records = new JsonArray();
        foreach (var key in Keys) records.Add(new JsonObject { ["releaseKey"] = key, ["profile"] = "full", ["transport"] = "stdio", ["exampleCallsExecuted"] = false, ["registeredToolCount"] = Copy(matrix[key]!["toolCount"]!), ["checkedToolCount"] = Copy(matrix[key]!["toolCount"]!), ["tools"] = new JsonArray(Strings(matrix[key]!["tools"]).Select(n => (JsonNode)new JsonObject { ["toolName"] = n }).ToArray()), ["offlineCallExamplesExecuted"] = new JsonArray() });
        var result = CatalogAudits.UsageFromRecords(Root, matrix, catalog, records); Assert.Equal(8, result["releases"]!.AsObject().Count); Assert.Equal(Hash(Encoding.UTF8.GetBytes(Source(Root, "src/Shared/ToolUsageData.json"))), Text(result["catalogSha256"])); Assert.Null(result["releases"]!["21"]!["profile"]);
        records[0]!["exampleCallsExecuted"] = true; Assert.Throws<ReleaseException>(() => CatalogAudits.UsageFromRecords(Root, matrix, catalog, records)); records[0]!["exampleCallsExecuted"] = false;
        records[0]!["checkedToolCount"] = 0; Assert.Throws<ReleaseException>(() => CatalogAudits.UsageFromRecords(Root, matrix, catalog, records)); records[0]!["checkedToolCount"] = Copy(matrix["14sp1"]!["toolCount"]!);
        records.RemoveAt(7); Assert.Throws<ReleaseException>(() => CatalogAudits.UsageFromRecords(Root, matrix, catalog, records));
    }
    [Fact]
    public void StdioAuditReceiptsMustBeDistinctAndHashChecked()
    {
        var path = Path.Combine(Root, "bin-build/refactor/usage-records-" + Guid.NewGuid().ToString("N") + ".json");
        var rows = new JsonArray(Keys.Select(k => (JsonNode)new JsonObject { ["releaseKey"] = k, ["allSourceTextRetrievedAndHashChecked"] = false }).ToArray());
        try
        {
            File.WriteAllText(path, Encode(rows), new UTF8Encoding(false)); Assert.Throws<ReleaseException>(() => CatalogAudits.Usage(Root, recordsPath: path));
            rows[0]!["releaseKey"] = "21"; File.WriteAllText(path, Encode(rows), new UTF8Encoding(false)); Assert.Throws<ReleaseException>(() => CatalogAudits.Usage(Root, recordsPath: path));
        }
        finally { File.Delete(path); }
    }
}
