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

    [Fact]
    public void Git_attributes_preserve_LF_for_pattern_inputs_and_generated_files()
    {
        var patterns = PlcPatternLibrary.Load(Root).Patterns;
        var inputs = from pattern in patterns
                     from variant in pattern!["variants"]!.AsArray()
                     from file in variant!["files"]!.AsArray()
                     select Text(file!["path"]);
        var paths = inputs.Concat(CatalogGenerator.Generate(Root).Keys).Concat(ToolCountGenerator.Targets)
            .Concat([RealMachineStatusGenerator.Page, RealMachineStatusGenerator.Manifest, ReleaseMigrationGenerator.Output,
                "src/Shared/ToolUsageData.json", "docs/reference/tool-matrix.md"]).Distinct(StringComparer.Ordinal).ToArray();
        var result = ReviewChangeSet.Git("git", ["check-attr", "-z", "eol", "text", "--", .. paths], Root);
        ProcessRunner.RequireSuccess(result, "Check Git attributes for LF inputs");
        var preserved = result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Chunk(3)
            .Where(row => (row[1], row[2]) is ("eol", "lf") or ("text", "unset"))
            .Select(row => row[0]).ToHashSet(StringComparer.Ordinal);
        Assert.All(paths, path => Assert.True(preserved.Contains(path), path + " needs eol=lf or -text in .gitattributes"));
    }

    [Fact]
    public void Siemens_guides_keep_the_refreshed_pin_attribution_and_scope()
    {
        var usage = ToolUsageGenerator.Generate(Root);
        var source = usage["sources"]!.AsArray().Single(row => Text(row!["id"]) == "guides")!;
        string commit = Text(source["commit"]);
        Assert.Equal("6aa36504d76ddb9f92e3e52c1f7ff0cc571c4d48", commit);
        Assert.StartsWith("6aa3650", commit);
        Assert.Equal(40, commit.Length);
        Assert.Equal("2026 Siemens AG", Text(source["copyright"]));
        Assert.Equal("reference/siemens-openness/LICENSE", Text(source["licenseFile"]));
        Assert.Equal(new[] {
            "Local snapshot layout: upstream openness_development/skills is relocated to reference/siemens-openness/skills. " +
            "The 32 relative skills/*/SKILL.md links in openness_development/README.md are rewritten to ../skills/*/SKILL.md " +
            "so they resolve locally; no other text is changed. Upstream plugin.json, marketplace metadata and validation " +
            "scripts are not included. This reference snapshot is not an installable upstream plugin." }, Strings(source["scopeNotes"]));
        Assert.Equal("Reference material only; not installed or executed. Upstream guides target TIA Portal V21 and contain instructions " +
            "addressed to coding assistants that write Openness client code (for example to ask for the TIA version and to reject versions " +
            "below V21). They are not instructions for calling this server, which serves V14 SP1-V21 with per-release tool contracts.",
            Text(source["scope"]));
        var guides = usage["documents"]!.AsArray().Where(row => Text(row!["id"]).StartsWith("guides/skills/", StringComparison.Ordinal)
            && Text(row!["id"]).EndsWith("/SKILL.md", StringComparison.Ordinal)).ToArray();
        Assert.Equal(32, guides.Length);
        Assert.All(guides, guide => Assert.Contains("/blob/" + commit + "/openness_development/skills/", Text(guide!["url"])));
    }

    [Fact]
    public void Unknown_topic_tool_fails_generation_with_the_spelling_from_the_rules()
    {
        var rules = Read(Root, "reference/tool-catalog/usage-topic-rules.json");
        rules["tools"]!["ComparePorjects"] = new JsonArray("change-detection");
        var names = new CatalogData(Root).Rosters.Values.SelectMany(tools => tools).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("ComparePorjects", Assert.Throws<ReleaseException>(() => ToolUsageGenerator.ValidateTopicTools(rules, names)).Message);
    }

    [Fact]
    public void Explicit_guide_topics_cover_comparisons_and_read_batches_on_every_release()
    {
        var usage = ToolUsageGenerator.Generate(Root);
        var data = new CatalogData(Root);
        foreach (var (name, topic) in new[] {
            ("CompareProjects", "change-detection"), ("CompareSoftwareToOnline", "change-detection"),
            ("ComparePlcBlockDocuments", "change-detection"), ("RunReadOnlyToolBatch", "performance-and-caching") })
        {
            foreach (string release in Keys) Assert.Contains(name, data.Rosters[release]);
            Assert.Equal(new[] { "guides/skills/" + topic + "/SKILL.md" }, Strings(usage["tools"]![name]!["documents"]));
        }
        Assert.Empty(Strings(usage["tools"]!["ManagePlcGitRepository"]!["documents"]));
    }

    [Fact]
    public void Behavior_catalog_covers_all_eight_releases_and_registered_fallback_entries()
    {
        var resource = CatalogGenerator.Generate(Root)[CatalogGenerator.Resource];
        var catalog = JsonNode.Parse(System.Xml.Linq.XDocument.Parse(resource).Root!
            .Elements("data").Single(row => (string?)row.Attribute("name") == "Catalog").Element("value")!.Value)!;
        foreach (string key in Keys)
        {
            var policies = catalog["behaviorPolicies"]!.AsArray().Where(row => Text(row!["releaseKey"]) == key).ToArray();
            Assert.Contains(policies, row => Text(row!["family"]) == "F19" && Text(row["l5"]) == "NOT RUN");
            Assert.Contains(policies, row => Text(row!["family"]) == "FOUNDATION" && Text(row["state"]) == "current"
                && Text(row["l5"]) == "NOT RUN");
            var entries = catalog["behaviorEntries"]!.AsArray().Where(row => Text(row!["releaseKey"]) == key).ToArray();
            Assert.Contains(entries, row => Text(row!["family"]) == "F19" && Text(row["entry"]) == "GetDeviceAddressing");
            Assert.Contains(entries, row => Text(row!["family"]) == "P6-FALLBACK" && Text(row["entry"]) == "CompilePlcSoftware");
            Assert.Contains(entries, row => Text(row!["family"]) == "FOUNDATION" && Text(row["entry"]) == "GetSoftwareInfo");
            Assert.Contains(entries, row => Text(row!["family"]) == "FOUNDATION" && Text(row["entry"]) == "AnalyzePlcCrossReferences");
            Assert.DoesNotContain(entries, row => Text(row!["entry"]) is "StageImportFiles" or "RenderPlcBlock" or "BuildPlcTagTable");
            Assert.All(entries, entry => Assert.IsType<JsonObject>(entry!["example"]));
            Assert.Equal(policies.Length, policies.Select(row => Text(row!["family"])).Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void Technology_export_additions_preserve_the_foundation_confirmation_contract()
    {
        var source = Read(Root, "manifest/history/contracts-v3/baseline/16.json")["tools"]!.AsArray()
            .Single(row => Text(row!["name"]) == "ExportTechnologyObject")!;
        var data = new CatalogData(Root);
        var inputs = data.CreateContractInputs();
        foreach (string release in new[] { "14sp1", "15.1" })
        {
            Assert.False(data.Archives[release].ContainsKey("ExportTechnologyObject"));
            var contract = inputs[release]["ExportTechnologyObject"]!;
            Assert.True(JsonNode.DeepEquals(source, contract), release);
            var schema = contract["inputSchema"]!;
            Assert.False(schema["additionalProperties"]!.GetValue<bool>());
            Assert.Equal(new[] { "softwarePath", "toName", "exportPath" }, Strings(schema["required"]));
            foreach (string argument in new[] { "confirm", "expectedProjectFile", "expectedPlanHash" })
                Assert.NotNull(schema["properties"]![argument]);
            Assert.False(schema["properties"]!["confirm"]!["default"]!.GetValue<bool>());
            Assert.Equal("", Text(schema["properties"]!["expectedProjectFile"]!["default"]));
            Assert.Equal("", Text(schema["properties"]!["expectedPlanHash"]!["default"]));
        }
    }

    [Fact]
    public void Technology_export_additional_releases_have_exact_rosters_and_maintained_examples()
    {
        var data = new CatalogData(Root);
        Assert.Equal(new[] { "14sp1", "15.1" }, Strings(data.EngineSource["registrations"]!["ExportTechnologyObject"]!["additionalReleases"]));
        var outputs = CatalogGenerator.Generate(Root);
        var catalog = JsonNode.Parse(System.Xml.Linq.XDocument.Parse(outputs[CatalogGenerator.Resource]).Descendants("data")
            .Single(element => (string?)element.Attribute("name") == "Catalog").Element("value")!.Value)!;
        foreach (string release in Keys)
        {
            Assert.Contains("ExportTechnologyObject", data.Rosters[release]);
            var row = Assert.Single(catalog["releases"]![release]!.AsArray(), entry => (string?)entry!["currentName"] == "ExportTechnologyObject");
            Assert.NotNull(row!["arguments"]!["softwarePath"]);
            Assert.NotNull(row["arguments"]!["toName"]);
            Assert.Equal(release != "14sp1", data.Rosters[release].Contains("ExportPlcWatchTable"));
        }
    }

    [Fact]
    public void Catalog_rows_take_the_maintained_parameter_set()
    {
        string frozenPath = Path.Combine(Root, "reference/tool-catalog/EngineSourceExamples.json");
        byte[] before = File.ReadAllBytes(frozenPath);
        var resource = CatalogGenerator.Generate(Root)[CatalogGenerator.Resource];
        var catalog = JsonNode.Parse(System.Xml.Linq.XDocument.Parse(resource).Descendants("data")
            .Single(element => (string?)element.Attribute("name") == "Catalog").Element("value")!.Value)!;
        var calls = Read(Root, "reference/tool-examples/calls.json")["profiles"]!;
        foreach (string release in Keys)
        {
            var row = catalog["releases"]![release]!.AsArray().Single(entry => (string?)entry!["currentName"] == "ListIoSystems")!;
            var maintained = calls[(string)row["exampleSource"]!]!["ListIoSystems"]!["arguments"]!;
            Assert.True(JsonNode.DeepEquals(maintained, row["arguments"]), release + "/ListIoSystems");
            // Directory imports also guard against restoring the frozen alias while retaining the maintained values.
            var import = catalog["releases"]![release]!.AsArray()
                .Single(entry => (string?)entry!["currentName"] == "ImportPlcTagTablesFromDirectory")!;
            Assert.True(import["arguments"]!.AsObject().ContainsKey("sourceDirectory"), release + "/ImportPlcTagTablesFromDirectory");
            Assert.False(import["arguments"]!.AsObject().ContainsKey("dir"));
        }
        Assert.Equal(before, File.ReadAllBytes(frozenPath));
    }

    private static JsonObject CurrentInputSchemas(string key)
    {
        return Read(Root, "manifest/contracts/v4/baseline/" + key + ".json")["tools"]!.AsArray()
            .ToDictionary(tool => Text(tool!["name"]), tool => JsonNode.Parse(TiaMcp.Logic.V4.CanonicalToolParameters.Schema(
                Text(tool!["name"]), System.Text.Json.JsonSerializer.SerializeToElement(tool["inputSchema"])).GetRawText())!.AsObject())
            .ToJsonObject();
    }

    [Fact]
    public void PortedDocumentCandidatesRetainModernFoundationProfiles()
    {
        var data = new CatalogData(Root); var resource = CatalogGenerator.Generate(Root)[CatalogGenerator.Resource];
        var catalog = JsonNode
            .Parse(System.Xml.Linq.XDocument.Parse(resource).Descendants("data").Single(e => (string?)e.Attribute("name") == "Catalog").Element("value")!
                .Value)!;
        Assert
            .Equal(new[] { "ExportPlcBlockDocuments", "ExportPlcBlocksDocuments", "ImportPlcBlockDocuments", "ImportPlcBlocksDocuments" }, data
                .FoundationCandidates.Order(StringComparer.Ordinal));
        foreach (var key in Keys)
        {
            var rows = catalog["releases"]![key]!.AsArray().ToDictionary(r => Text(r!["currentName"]));
            foreach (var name in data.FoundationCandidates)
                if (rows.TryGetValue(name, out var row)) Assert.Equal("plc-foundation", Text(row!["exampleSource"]));
                else Assert.Contains(name, PortedFamilies.Withheld(data.Families, key));
            foreach (var name in data.Ported[key].Except(data.FoundationCandidates)) Assert.Equal("full-engine", Text(rows[name]!["exampleSource"]));
        }
    }
    [Fact]
    public void Every_release_uses_the_exact_unified_lite_intersection()
    {
        var data = new CatalogData(Root);
        var resource = CatalogGenerator.Generate(Root)[CatalogGenerator.Resource];
        var catalog = JsonNode
            .Parse(System.Xml.Linq.XDocument.Parse(resource).Descendants("data").Single(element => (string?)element.Attribute("name") == "Catalog")
                .Element("value")!.Value)!;
        var proposal = Set(Read(Root, "reference/tool-catalog/lite-groups.json").AsObject().SelectMany(group => Text(group.Value).Split(' ')));
        var unified = Set(proposal.Concat(data.Rosters["19"].Except(data.Ported["19"]).Except(data.HostOnly.Keys))
            .Concat(data.Families["F00"].Tools).Concat(data.Bridge)
            .Concat(data.HostOnly.Keys.Where(name => !data.Additions.ContainsKey(name)
                || name is "DescribeStandardPackage" or "PlanProjectGeneration" or "ApplyGenerationPlan")));
        unified.ExceptWith(data.Removed);
        foreach (var key in Keys)
        {
            var rows = catalog["releases"]![key]!.AsArray();
            Assert.True(data.Rosters[key].SetEquals(rows.Select(row => Text(row!["currentName"]))));
            var lite = Set(rows.Where(row => Strings(row!["profiles"]).Contains("lite")).Select(row => Text(row!["currentName"])));
            Assert.True(lite.SetEquals(unified.Intersect(data.Rosters[key])));
            Assert.True(lite.IsProperSubsetOf(data.Rosters[key]));
            Assert.All(rows, row => Assert.Contains("full", Strings(row!["profiles"])));
            Assert.Equal(rows.Select(row => Text(row!["name"])).Order(StringComparer.Ordinal), rows.Select(row => Text(row!["name"])));
        }
        foreach (var key in new[] { "20", "21" })
            Assert.InRange(proposal.Count(name => data.Rosters[key].Contains(name) || data.Removed.Contains(name)), 50, 65);
    }

    [Fact]
    public void Explicitly_withheld_chart_protection_keeps_the_sibling_chart_tool_on_its_supported_releases()
    {
        var families = PortedFamilies.Load(Root, Keys);
        Assert.Empty(families["F32"].ToolReleases["ManageCfcChartProtection"]);
        foreach (var key in Keys)
        {
            Assert.Contains("ManageCfcChartProtection", PortedFamilies.Withheld(families, key));
            Assert.Equal(key is "18" or "19" or "20" or "21", PortedFamilies.Additions(families, key).Contains("ExchangeCfcCharts"));
        }
    }

    [Fact]
    public void Lite_proposal_rejects_unknown_names_before_roster_intersection()
    {
        var data = new CatalogData(Root);
        var proposal = Read(Root, "reference/tool-catalog/lite-groups.json").AsObject()
            .SelectMany(group => Text(group.Value).Split(' ')).ToArray();
        CatalogGenerator.ValidateLiteProposal(proposal, data);
        var error = Assert.Throws<ReleaseException>(() =>
            CatalogGenerator.ValidateLiteProposal(proposal.Append("MisspelledToolForRegression"), data));
        Assert.Contains("unknown tool", error.Message);
    }

    [Theory, InlineData("full-engine"), InlineData("plc-foundation")]
    public void Each_maintained_schema_variant_is_required_even_when_another_source_has_the_tool(string source)
    {
        var data = new CatalogData(Root);
        var calls = Read(Root, "reference/tool-examples/calls.json");
        ToolUsageGenerator.ValidateCoverage(data.Rosters, calls, data.Families["F00"].Tools);
        calls["profiles"]![source]!.AsObject().Remove("ManageStartdriveParameter");
        Assert.Throws<ReleaseException>(() => ToolUsageGenerator.ValidateCoverage(data.Rosters, calls, data.Families["F00"].Tools));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("duplicate-declaration")]
    public void FoundationCandidateReaderFailsClosed(string mutation)
    {
        const string row = "new Family(\"family\", \"prefix\", new[] { \"20\", \"21\" }, new[] { \"Tool\" })";
        var declaration = mutation switch { "missing" => "", "malformed" => "FoundationCandidates = { Tool };", "duplicate" => "FoundationCandidates = { \"Tool\", \"Tool\" };", "unknown" => "FoundationCandidates = { \"Other\" };", _ => "FoundationCandidates = { \"Tool\" }; FoundationCandidates = { \"Tool\" };" };
        Assert.Throws<ArgumentException>(() => PortedFamilies.ParseFoundationCandidates(row + declaration, Keys));
        Assert.Contains("Tool", PortedFamilies.ParseFoundationCandidates(row + "FoundationCandidates = { \"Tool\" };", Keys));
    }
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
            if (p.Key == ReleaseMigrationGenerator.Output) expected = expected.Replace("reference/tool-catalog/v3-v4-renames.json.", "docs/archive/development/phase6-review.md.", StringComparison.Ordinal);
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
    [InlineData("ReleaseRange.All", "14sp1,15.1,16,17,18,19,20,21")]
    [InlineData("ReleaseRange.Since(\"19\")", "19,20,21")]
    [InlineData("ReleaseRange.Between(\"17\", \"20\")", "17,18,19,20")]
    public void PortedFamiliesExpandReleaseRanges(string expression, string expected)
    {
        var source = "new Family(\"family\", \"prefix\", " + expression + ", new[] { \"Tool\" })\n"
            + "ToolReleases = new Dictionary<string, string[]>(StringComparer.Ordinal) { [\"Tool\"] = " + expression + "\n};";
        var family = PortedFamilies.Parse(source, Keys)["family"];
        Assert.Equal(expected.Split(','), family.Releases);
        Assert.Equal(expected.Split(','), family.ToolReleases["Tool"]);
    }
    [Theory]
    [InlineData("ReleaseRange.Since(\"22\")")]
    [InlineData("ReleaseRange.Between(\"17\", \"22\")")]
    [InlineData("ReleaseRange.Between(\"20\", \"17\")")]
    public void PortedFamiliesRejectInvalidReleaseRanges(string expression)
    {
        Assert.Throws<ArgumentException>(() => PortedFamilies.Parse(
            "new Family(\"family\", \"prefix\", " + expression + ", new[] { \"Tool\" })", Keys));
        Assert.Throws<ArgumentException>(() => PortedFamilies.Parse(
            "new Family(\"family\", \"prefix\", ReleaseRange.All, new[] { \"Tool\" })\n"
            + "ToolReleases = new Dictionary<string, string[]>(StringComparer.Ordinal) { [\"Tool\"] = " + expression + "\n};", Keys));
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
        if (kind == "partial") { var parsed = PortedFamilies.Parse(row, Keys); Assert.Contains("Tool", PortedFamilies.Additions(parsed, "20")); Assert.Empty(PortedFamilies.Withheld(parsed, "20")); return; }
        if (kind == "override")
        {
            var parsed = PortedFamilies.Parse(row + "\nToolReleases = new Dictionary<string, string[]>(StringComparer.Ordinal) { [\"Tool\"] = new[] { \"21\" }\n};", Keys);
            Assert.Empty(PortedFamilies.Additions(parsed, "20")); Assert.Contains("Tool", PortedFamilies.Additions(parsed, "21"));
            // A tool without any supported action on a release is withheld there, modern releases included.
            Assert.Contains("Tool", PortedFamilies.Withheld(parsed, "20")); Assert.Empty(PortedFamilies.Withheld(parsed, "21"));
            return;
        }
        Assert.Throws<ArgumentException>(() => PortedFamilies.Parse(kind == "duplicate" ? row + row : kind == "unparsed" ? row + "new Family(other, prefix, releases, tools)" : row + "\nToolReleases = new Dictionary<string, string[]>(StringComparer.Ordinal) { [\"Other\"] = new[] { \"21\" }\n};", Keys));
    }

    [Theory]
    [InlineData("F34/21", true)]
    [InlineData("Unknown/21", false)]
    [InlineData("F34/unknown", false)]
    public void MigrationUnavailableReasonComesFromReviewedFamilyData(string owner, bool valid)
    {
        const string row = "new Family(\"F34\", \"options\", new[] { \"20\", \"21\" }, new[] { \"Tool\" })\nToolReleases = new Dictionary<string, string[]>(StringComparer.Ordinal) { [\"Tool\"] = new[] { \"20\" }\n};\n";
        string source = row + "UnavailableReasons = new Dictionary<string, string>(StringComparer.Ordinal) { [\"" + owner + "\"] = \"no API in the supplied V21 SDK\"\n};";
        if (!valid) { Assert.Throws<ArgumentException>(() => PortedFamilies.Parse(source, Keys)); return; }
        var families = PortedFamilies.Parse(source, Keys);
        Assert.Equal("no API in the supplied V21 SDK", PortedFamilies.WithheldReason(families, "21", "Tool"));
        Assert.Throws<ArgumentException>(() => PortedFamilies.WithheldReason(families, "20", "Tool"));
        Assert.Throws<ArgumentException>(() => PortedFamilies.Parse(source.Replace("\"no API in the supplied V21 SDK\"\n};", "\"no API in the supplied V21 SDK\", [\"F34/21\"] = \"duplicate\"\n};", StringComparison.Ordinal), Keys));
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
        var calls = Read(Root, "reference/tool-examples/calls.json"); var sequences = Read(Root, "reference/tool-examples/sequences.json").AsArray();
        var metadata = Read(Root, "reference/tool-examples/metadata.json"); var table = sequences
            .First(s => Text(s!["id"]) == "sequence/foundation-tag-table-round-trip")!; var block = sequences
            .First(s => Text(s!["id"]) == "sequence/foundation-block-round-trip")!; var entry = calls["profiles"]!["plc-foundation"]!["ImportPlcTagTable"]!;
        switch (mutation) {
            case "overwrite": entry["execution"]!["arguments"]!.AsObject().Remove("overwrite"); break; case "path": entry["note"] = Text(entry["note"])
                .Replace("normalized full path", "", StringComparison.Ordinal); break; case "full-parameters"
                : calls["profiles"]!["full-engine"]!["ImportPlcBlock"]!["arguments"]!["overwrite"] = true; break; case "sequence": sequences.Remove(table);
            break; case "readback": table["steps"]!.AsArray().Remove(table["steps"]!.AsArray().Last(s => Text(s!["tool"]) == "ListPlcTags")); break;
            case "compile": block["steps"]!.AsArray().Remove(block["steps"]!.AsArray().Last(s => Text(s!["tool"]) == "CompilePlcSoftware")); break;
            case "approval": metadata["resultReading"]!["ResponseMessage"]!.AsArray()
                .RemoveAt(metadata["resultReading"]!["ResponseMessage"]!.AsArray().Count - 1); break; case "identity": table["steps"]!.AsArray()
                .Last(s => Text(s!["tool"]) == "ImportPlcTagTable" && s["arguments"]!["dryRun"]?.GetValue<bool>() == false)!["arguments"]!.AsObject()
                .Remove("expectedProjectFile"); break; case "release": table["releaseKeys"]!.AsArray().RemoveAt(5); break;
        }
        Assert.ThrowsAny<Exception>(() => ToolUsageGenerator.ValidateRetest(Root, calls, sequences, metadata));
    }
    [Theory]
    [InlineData("")]
    [InlineData("ShowObjectInEditor")]
    [InlineData("ShowWorkbenchPage")]
    [InlineData("ListPlcBlocks")]
    public void RoundTripAllowsOnlyTrailingEditorUiSteps(string trailingTool)
    {
        var calls = Read(Root, "reference/tool-examples/calls.json");
        var sequences = Read(Root, "reference/tool-examples/sequences.json").AsArray();
        var metadata = Read(Root, "reference/tool-examples/metadata.json");
        var block = sequences.Single(s => Text(s!["id"]) == "sequence/foundation-block-round-trip")!;
        Assert.Equal("ShowObjectInEditor", Text(block["steps"]!.AsArray()[^1]!["tool"]));
        ToolUsageGenerator.ValidateRetest(Root, calls, sequences, metadata);
        if (trailingTool.Length == 0) block["steps"]!.AsArray().RemoveAt(block["steps"]!.AsArray().Count - 1);
        else if (trailingTool == "ShowObjectInEditor") block["steps"]!.AsArray().Add(block["steps"]!.AsArray()[^1]!.DeepClone());
        else block["steps"]!.AsArray().Add(new JsonObject { ["tool"] = trailingTool, ["arguments"] = trailingTool == "ShowWorkbenchPage"
            ? new JsonObject { ["page"] = "home" } : new JsonObject { ["softwarePath"] = "PLC_1" } });
        if (trailingTool is "" or "ShowObjectInEditor") ToolUsageGenerator.ValidateRetest(Root, calls, sequences, metadata);
        else Assert.Contains("Block compile lesson", Assert.Throws<ReleaseException>(() =>
            ToolUsageGenerator.ValidateRetest(Root, calls, sequences, metadata)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TrailingEditorUiDoesNotLoosenExplicitExecutionIdentityCheck()
    {
        var calls = Read(Root, "reference/tool-examples/calls.json");
        var sequences = Read(Root, "reference/tool-examples/sequences.json").AsArray();
        var metadata = Read(Root, "reference/tool-examples/metadata.json");
        var block = sequences.Single(s => Text(s!["id"]) == "sequence/foundation-block-round-trip")!;
        block["steps"]!.AsArray()[^1]!["arguments"]!["dryRun"] = false;
        Assert.Contains("Sequence identity lesson", Assert.Throws<ReleaseException>(() =>
            ToolUsageGenerator.ValidateRetest(Root, calls, sequences, metadata)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ExportPlcBlocks")]
    [InlineData("ExportPlcTypes")]
    [InlineData("ExportPlcTagTables")]
    [InlineData("ImportPlcBlocksFromDirectory")]
    [InlineData("ImportPlcProgramFromDirectory")]
    public void FoundationBatchExamplesRequireExactPaths(string name) {
        var calls = Read(Root, "reference/tool-examples/calls.json"); calls["profiles"]!["plc-foundation"]![name]!["arguments"]!["softwarePath"] = "PLC_1";
        Assert.Throws<ReleaseException>(() => ToolUsageGenerator.ValidateFoundation(Root, calls, []));
    }
    [Fact]
    public void UsageExecutionIdentityRequirementFollowsTheActualContract()
    {
        var calls = Read(Root, "reference/tool-examples/calls.json");
        // File-only source generation has no expectedProjectFile parameter; project writes still require it.
        calls["profiles"]!["plc-foundation"]!["GeneratePlcSourceFromBlocks"]!["arguments"]!["dryRun"] = false;
        ToolUsageGenerator.ValidateFoundation(Root, calls, []);
        var bound = calls["profiles"]!["plc-foundation"]!["CreatePlcTag"]!["arguments"]!.AsObject();
        bound["dryRun"] = false; bound.Remove("expectedProjectFile");
        Assert
            .Contains("Real call requires expectedProjectFile: CreatePlcTag", Assert
                .Throws<ReleaseException>(() => ToolUsageGenerator.ValidateFoundation(Root, calls, [])).Message);
        bound["expectedProjectFile"] = "C:/Examples/Project.ap21";
        ToolUsageGenerator.ValidateFoundation(Root, calls, []);
    }
    [Theory]
    [InlineData("plc-foundation", "InvokeObject")]
    [InlineData("full-engine", "InvokeObject")]
    [InlineData("plc-foundation", "InvokeService")]
    [InlineData("full-engine", "InvokeService")]
    [InlineData("plc-foundation", "ManageOnlineDriveFunctions")]
    [InlineData("full-engine", "ManageOnlineDriveFunctions")]
    public void ReadExamplesUseTheApprovalFreeReadMode(string profile, string tool)
    {
        var arguments = Read(Root, "reference/tool-examples/calls.json")["profiles"]![profile]![tool]!["arguments"]!;
        Assert.True((bool?)arguments["dryRun"]);
        Assert.Null(arguments["expectedProjectFile"]);
        if (tool is "InvokeObject" or "InvokeService")
        {
            Assert.Equal("GetAttributeInfos", Text(arguments["methodName"]));
            Assert.NotEqual(true, (bool?)arguments["allowWrite"]);
        }
        else Assert.Equal("read", Text(arguments["action"]));
    }

    private static readonly HashSet<string> PreviewWriteTools = new(StringComparer.Ordinal)
    {
        "AttachDeviceNodeToSubnet", "ConnectDeviceNodesToProfinetSubnet", "CreateGsdDevice", "CreateVersionControlWorkspace",
        "DownloadPlc", "EnsureSubnet", "ImportAlarmClasses", "ImportAlarmTextLists", "ImportHmiConnection", "ImportHmiScreen",
        "ImportHmiScreensFromDirectory", "ImportHmiTagTable", "ImportHmiTagTablesFromDirectory", "ImportMasterCopyFromGlobalLibrary",
        "ImportOpcUaInterface", "ImportTechnologyObject", "ImportTechnologyObjectsFromDirectory", "InvokeObject", "InvokeService",
        "ManageOnlineDriveFunctions", "SeedProjectFromReference", "SetDeviceItemAttribute", "SetOpcUaInterfaceEnabled", "SetPlcCpuSettings",
        "SetPlcWatchTableModifyValue", "SetUnifiedHmiButtonEventScriptCode", "SetUnifiedHmiRuntimeState"
    };

    public static IEnumerable<object[]> PreviewWriteEffectSequences()
    {
        foreach (var sequence in Read(Root, "reference/tool-examples/sequences.json").AsArray())
            if (sequence!["steps"]!.AsArray().Any(step => PreviewWriteTools.Contains(Text(step!["tool"]))))
                yield return new object[] { Text(sequence["id"]) };
    }

    [Theory]
    [MemberData(nameof(PreviewWriteEffectSequences))]
    public void PreviewWriteSequencesApplyBeforeFollowingSteps(string sequenceId)
    {
        var sequence = Read(Root, "reference/tool-examples/sequences.json").AsArray()
            .Single(item => Text(item!["id"]) == sequenceId)!;
        var steps = sequence["steps"]!.AsArray();
        for (var index = 0; index < steps.Count - 1; index++)
        {
            var step = steps[index]!;
            var tool = Text(step["tool"]);
            var preview = step["arguments"]!.AsObject();
            if (!PreviewWriteTools.Contains(tool) || preview["dryRun"]?.GetValue<bool>() == false) continue;
            // A continuing write recipe must apply the reviewed preview before expecting its effects.
            var apply = steps[index + 1]!;
            var arguments = apply["arguments"]!.AsObject();
            Assert.True(preview["dryRun"]?.GetValue<bool>() == true, sequenceId + ": make the preview explicit for " + tool);
            Assert.Equal(tool, Text(apply["tool"]));
            Assert.True(arguments["dryRun"]?.GetValue<bool>() == false, sequenceId + ": apply before continuing after " + tool);
            Assert.False(string.IsNullOrWhiteSpace(Text(arguments["expectedPlanHash"])));
            Assert.False(string.IsNullOrWhiteSpace(Text(arguments["expectedProjectFile"])));
            var previewInputs = preview.DeepClone().AsObject();
            var applyInputs = arguments.DeepClone().AsObject();
            foreach (var control in new[] { "dryRun", "expectedPlanHash", "expectedProjectFile" })
            {
                previewInputs.Remove(control);
                applyInputs.Remove(control);
            }
            Assert.True(JsonNode.DeepEquals(previewInputs, applyInputs), sequenceId + ": apply the same reviewed arguments for " + tool);
        }
    }

    [Fact]
    public void UsageExamplesRejectUnknownToolSchemas()
    {
        var calls = new JsonObject { ["profiles"] = new JsonObject { ["plc-foundation"] = new JsonObject {
            ["UnknownUsageTool"] = new JsonObject { ["arguments"] = new JsonObject { ["dryRun"] = false } } } } };
        Assert.Contains("Unknown tool schema", Assert.Throws<ReleaseException>(() => ToolUsageGenerator.ValidateFoundation(Root, calls, [])).Message);
    }
    [Fact]
    public void SingleWriterOwnershipIsExplicit()
    {
        Assert.Contains("ToolProfiles", CatalogGenerator.Resource); Assert.DoesNotContain("ToolProfiles", Source(Root, "build-tools/release/ToolUsageGenerator.cs"));
        var audits = Source(Root, "build-tools/release/CatalogAudits.cs"); Assert.DoesNotContain("results[\"manifest/version-tools.json\"]", audits); Assert.DoesNotContain("results[\"docs/reference/version-tool-catalog.md\"]", audits);
        Assert.Contains("audit-tool-usage is the only writer", Source(Root, "scripts/checks/Test-ToolUsage.py"));
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

    private static (string Input, string Sdk, string Prepared) WriteNativeAuditInputs(StaticTree tree)
    {
        var input = Path.Combine(tree.Root, "inputs");
        var sdk = Path.Combine(tree.Root, "sdk");
        var prepared = Path.Combine(input, "prepared", ReleaseVersions.ReadLabel(Root), "version-audit");
        foreach (var key in Keys)
        {
            var relative = key switch {
                "14sp1" => "TIA_V14SP1_PublicAPI/V14 SP1", "15.1" => "TIA_V15.1_PublicAPI/V15.1",
                "21" => "TIA_V21_PublicAPI/V21/net48", _ => $"TIA_V{key}_PublicAPI/V{key}"
            };
            tree.Write(Path.Combine(sdk, relative, "Siemens.Engineering.xml"), """
                <doc><members>
                  <member name="M:Siemens.Engineering.Domain.Widget.Import(System.String)"/>
                  <member name="M:Siemens.Engineering.Domain.Widget.Import(System.Int32)"/>
                  <member name="P:Siemens.Engineering.Domain.Widget.Name"/>
                  <member name="M:Siemens.Engineering.Domain.Widget.Export"/>
                  <member name="M:Siemens.Engineering.Private.Secret.Invoke"/>
                  <member name="M:Siemens.Engineering.Domain.Widget.Dispose"/>
                </members></doc>
                """);
            tree.Write(Path.Combine(sdk, relative, "Siemens.Engineering.AddIn.xml"), "<invalid/>");
            var members = new[] { "System.Void Siemens.Engineering.Domain.Widget::Import(System.String)",
                "System.String Siemens.Engineering.Domain.Widget::get_Name()", "System.Void System.IDisposable::Dispose()" };
            var sites = members.Select((member, offset) => new {
                id = offset.ToString("x20"), caller = $"System.Void Fixture.V{key}::Run()", offset, opcode = "callvirt", member,
                category = offset == 2 ? "interface" : "direct", wrapper = "Call_" + offset.ToString("x20")
            });
            tree.Write(Path.Combine(prepared, $"coverage-{key}.json"), System.Text.Json.JsonSerializer.Serialize(new {
                schemaVersion = 1, instrumenter = "native-call-weaver/1", instrumenterSha256 = new string('a', 64),
                scope = "Fixture PLC worker adapter", count = members.Length, sites
            }));
            var baseline = Read(Root, $"manifest/contracts/v4/baseline/{key}.json");
            var tools = baseline["tools"]!.AsArray().Select(t => (JsonNode)new JsonObject {
                ["name"] = Text(t!["name"]), ["inputSchema"] = t["inputSchema"]!.DeepClone()
            });
            tree.Write(Path.Combine(prepared, $"tools-{key}.json"), new JsonObject {
                ["releaseKey"] = key, ["profile"] = "plc-foundation", ["behaviorCapabilities"] = baseline["behaviorCapabilities"]!.DeepClone(),
                ["callDiscipline"] = new JsonObject(), ["tools"] = new JsonArray(tools.ToArray())
            }.ToJsonString());
        }
        return (input, sdk, prepared);
    }

    [Fact]
    public void NativeAuditCountsXmlOverloadsPropertiesAndExactCompiledMembers()
    {
        using var tree = new StaticTree();
        var (input, sdk, prepared) = WriteNativeAuditInputs(tree);
        var outputs = CatalogAudits.Version(Root, sdk, "candidates", input);
        var legacy = CatalogAudits.Version(Root, sdk, "candidates", input, legacyLineEndings: true);
        Assert.Equal(9, outputs.Count);
        Assert.DoesNotContain("manifest/version-tools.json", outputs.Keys);
        Assert.DoesNotContain("docs/reference/version-tool-catalog.md", outputs.Keys);
        foreach (var p in outputs) Assert.Equal(p.Value.Replace("\n", "\r\n", StringComparison.Ordinal), legacy[p.Key]);
        var statistics = JsonNode.Parse(outputs["manifest/version-api-audit.json"])!["versions"]!.AsArray();
        Assert.Equal(8, statistics.Count);
        foreach (var row in statistics)
        {
            Assert.Equal(4, row!["domainSignatures"]!.GetValue<int>());
            Assert.Equal(3, row["domainMemberNames"]!.GetValue<int>());
            Assert.Equal(2, row["compiledDirectMemberNames"]!.GetValue<int>());
            Assert.Equal(1, row["withoutDirectMemberReference"]!.GetValue<int>());
            Assert.Equal(1, row["xmlFiles"]!.GetValue<int>());
        }
        foreach (var key in Keys)
        {
            var candidates = JsonNode.Parse(outputs[Path.Combine("candidates", $"api-candidates-{key}.json")])!;
            Assert.Equal("M:Siemens.Engineering.Domain.Widget.Export", Text(candidates["members"]![0]));
        }
        foreach (var key in new[] { "20", "21" })
        {
            var row = statistics.Single(row => Text(row!["releaseKey"]) == key)!;
            Assert.Equal(2, row["plcCompiledDirectMemberNames"]!.GetValue<int>());
            Assert.Equal(64, Text(row["hostCatalogSha256"]).Length);
            Assert.Equal(64, Text(row["plcCompiledCallInventorySha256"]).Length);
            var hostPath = Path.Combine(prepared, $"tools-{key}.json");
            Assert.Equal(Hash(File.ReadAllBytes(hostPath)), Text(row["hostCatalogSha256"]));
            var original = File.ReadAllText(hostPath);
            tree.Write(hostPath, new JsonObject { ["releaseKey"] = key, ["tools"] = new JsonArray() }.ToJsonString());
            var mismatch = Assert.Throws<ReleaseException>(() => CatalogAudits.Version(Root, sdk, "candidates", input));
            Assert.Contains("Built/source catalog mismatch: " + key, mismatch.Message);
            tree.Write(hostPath, original);
            var adapterPath = Path.Combine(prepared, $"coverage-{key}.json");
            Assert.Equal(Hash(File.ReadAllBytes(adapterPath)), Text(row["compiledCallInventorySha256"]));
            Assert.Equal(Text(row["compiledCallInventorySha256"]), Text(row["plcCompiledCallInventorySha256"]));
            var adapter = File.ReadAllText(adapterPath); File.Delete(adapterPath);
            Assert.ThrowsAny<Exception>(() => CatalogAudits.Version(Root, sdk, "candidates", input));
            tree.Write(adapterPath, adapter);
        }
        tree.Write(Path.Combine(prepared, "tools-19.json"), """{"releaseKey":"19","tools":[]}""");
        var legacyMismatch = Assert.Throws<ReleaseException>(() => CatalogAudits.Version(Root, sdk, "candidates", input));
        Assert.Contains("Built/source catalog mismatch: 19", legacyMismatch.Message);
    }

    [Fact]
    public void NativeAuditRejectsCatalogWithAnotherReleaseKey()
    {
        using var tree = new StaticTree();
        var (input, sdk, prepared) = WriteNativeAuditInputs(tree);
        var path = Path.Combine(prepared, "tools-21.json");
        var catalog = Read(prepared, "tools-21.json");
        catalog["releaseKey"] = "20";
        tree.Write(path, catalog.ToJsonString());

        var error = Assert.Throws<ReleaseException>(() => CatalogAudits.Version(Root, sdk, "candidates", input));
        Assert.Equal("Host catalog release mismatch: 21", error.Message);
    }

    [Theory]
    [InlineData("20", "GetToolUsage")]
    [InlineData("21", "BuildClassicHmiTagTable")]
    public void NativeAuditRejectsManifestMissingAnAdmittedHostTool(string key, string tool)
    {
        using var tree = new StaticTree();
        var (input, sdk, _) = WriteNativeAuditInputs(tree);
        foreach (var file in Repository.GitFiles(Root, "Version.props", "src/FoundationHost/*.cs", "src/Adapters.Contracts/PortedFamilies.cs",
            "src/Logic/Siemens/ToolVersionPolicy.cs", "src/Logic/ModelContextProtocol/ToolMetadata.Classifications.cs", "src/Shared/PlcOfflineTools.cs",
            "src/ToolPipeline/SharedToolCatalog.cs", "reference/tool-catalog/*.json", "manifest/history/contracts-v3/baseline/*.json"))
            tree.Copy(file);

        const string manifestPath = "manifest/tools-list.json";
        var manifest = Read(Root, manifestPath);
        var tools = manifest["tools"]!.AsArray();
        tools.Remove(tools.Single(row => Text(row!["name"]) == tool));
        manifest["toolCount"] = tools.Count;
        tree.Write(manifestPath, manifest.ToJsonString());

        var error = Assert.Throws<ReleaseException>(() => CatalogAudits.Version(tree.Root, sdk, "candidates", input));
        Assert.Equal("Host/manifest catalog mismatch: " + key, error.Message);
    }

    [Fact]
    public void UsageAuditRequiresEveryToolReleaseAndNoNativeExampleCalls()
    {
        var matrix = Read(Root, "manifest/version-tools.json")["releases"]!.AsObject(); var catalog = Read(Root, "src/Shared/ToolUsageData.json"); var records = new JsonArray();
        foreach (var key in Keys) records.Add(new JsonObject { ["releaseKey"] = key, ["profile"] = "full", ["transport"] = "stdio", ["exampleCallsExecuted"] = false, ["registeredToolCount"] = Copy(matrix[key]!["toolCount"]!), ["checkedToolCount"] = Copy(matrix[key]!["toolCount"]!), ["tools"] = new JsonArray(Strings(matrix[key]!["tools"]).Select(n => (JsonNode)new JsonObject { ["toolName"] = n }).ToArray()), ["offlineCallExamplesExecuted"] = new JsonArray(),
            ["inputSchemas"] = CurrentInputSchemas(key) });
        var result = CatalogAudits.UsageFromRecords(Root, matrix, catalog, records); Assert.Equal(8, result["releases"]!.AsObject().Count); Assert.Equal(Hash(Encoding.UTF8.GetBytes(Source(Root, "src/Shared/ToolUsageData.json"))), Text(result["catalogSha256"])); Assert.Null(result["releases"]!["21"]!["profile"]);
        records[0]!["exampleCallsExecuted"] = true; Assert.Throws<ReleaseException>(() => CatalogAudits.UsageFromRecords(Root, matrix, catalog, records)); records[0]!["exampleCallsExecuted"] = false;
        records[0]!["checkedToolCount"] = 0; Assert.Throws<ReleaseException>(() => CatalogAudits.UsageFromRecords(Root, matrix, catalog, records)); records[0]!["checkedToolCount"] = Copy(matrix["14sp1"]!["toolCount"]!);
        records.RemoveAt(7); Assert.Throws<ReleaseException>(() => CatalogAudits.UsageFromRecords(Root, matrix, catalog, records));
    }
    [Fact]
    public void MultiVersionUsageAuditValidatesPlcReceiptsWithoutChangingTheManifestShape()
    {
        // audit-tool-usage writes this result to manifest/tool-usage-coverage.json; check-all's engine and PLC usage
        // audits reproduce those committed bytes from receipts only, so the PLC receipts are validated, never written.
        var matrix = Read(Root, "manifest/version-tools.json")["releases"]!.AsObject(); var release = Text(Read(Root, "manifest/delivery.json")["release"]);
        JsonObject Receipt(string key, string profile = "full", string transport = "stdio") => new JsonObject { ["releaseKey"] = key, ["profile"] = profile, ["transport"] = transport, ["exampleCallsExecuted"] = false, ["allSourceTextRetrievedAndHashChecked"] = true,
            ["registeredToolCount"] = Copy(matrix[key]!["toolCount"]!), ["checkedToolCount"] = Copy(matrix[key]!["toolCount"]!), ["tools"] = new JsonArray(Strings(matrix[key]!["tools"]).Select(n => (JsonNode)new JsonObject { ["toolName"] = n }).ToArray()), ["offlineCallExamplesExecuted"] = new JsonArray(),
            ["inputSchemas"] = CurrentInputSchemas(key) };
        var input = Path.Combine(Root, "bin-build", "usage-audit-" + Guid.NewGuid().ToString("N")); var receipts = Path.Combine(input, "receipts.json");
        void Put(string relative, JsonNode value) { var path = Path.Combine(input, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, Encode(value), new UTF8Encoding(false)); }
        try
        {
            Put("merge-check/transport/tool-usage.json", new JsonArray(Keys.Take(6).Select(key => (JsonNode)Receipt(key)).ToArray()));
            foreach (var key in new[] { "20", "21" })
            {
                Put($"releases/v{release}/v{key}/tool-usage-v{key}.json", new JsonArray(new[] { ("full", "stdio"), ("full", "http"), ("lite", "stdio"), ("lite", "http") }.Select(row => (JsonNode)Receipt(key, row.Item1, row.Item2)).ToArray()));
                var plc = Receipt(key); plc["workerModel"] = "foundation-host-plc-worker"; foreach (var field in new[] { "hostSha256", "runtimeSha256", "sdkWorkerSha256" }) plc[field] = new string('0', 64);
                Put($"merge-check/usage-v{key}-plc.json", new JsonArray(plc));
            }
            Put("receipts.json", new JsonArray(Keys.Select(key => (JsonNode)Receipt(key)).ToArray()));
            var audit = CatalogAudits.Usage(Root, input);
            Assert.Null(audit["plcReleases"]);
            Assert.Equal(Encode(CatalogAudits.Usage(Root, recordsPath: receipts)), Encode(audit));
            var unchecked21 = Receipt("21"); unchecked21["allSourceTextRetrievedAndHashChecked"] = false;
            Put("merge-check/usage-v21-plc.json", new JsonArray(unchecked21));
            Assert.Throws<ReleaseException>(() => CatalogAudits.Usage(Root, input));
            var foreign = Receipt("21"); foreign["checkedToolCount"] = 0;
            Put("merge-check/usage-v21-plc.json", new JsonArray(foreign));
            Assert.Throws<ReleaseException>(() => CatalogAudits.Usage(Root, input));
            File.Delete(Path.Combine(input, "merge-check/usage-v21-plc.json"));
            Assert.ThrowsAny<Exception>(() => CatalogAudits.Usage(Root, input));
        }
        finally { Directory.Delete(input, true); }
    }
    [Fact]
    public void MultiVersionUsageAuditCountsOneReceiptPerReleaseAcrossIdenticalTransportConnections()
    {
        // The transport check writes one receipt per connection: stdio and two HTTP clients for each release.
        var matrix = Read(Root, "manifest/version-tools.json")["releases"]!.AsObject();
        JsonObject Receipt(string key) => new JsonObject { ["releaseKey"] = key, ["exampleCallsExecuted"] = false, ["allSourceTextRetrievedAndHashChecked"] = true,
            ["registeredToolCount"] = Copy(matrix[key]!["toolCount"]!), ["checkedToolCount"] = Copy(matrix[key]!["toolCount"]!), ["tools"] = new JsonArray(Strings(matrix[key]!["tools"]).Select(n => (JsonNode)new JsonObject { ["toolName"] = n }).ToArray()), ["offlineCallExamplesExecuted"] = new JsonArray(),
            ["inputSchemas"] = CurrentInputSchemas(key) };
        var input = Path.Combine(Root, "bin-build", "usage-audit-" + Guid.NewGuid().ToString("N")); var receipts = Path.Combine(input, "receipts.json");
        void Put(string relative, JsonNode value) { var path = Path.Combine(input, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, Encode(value), new UTF8Encoding(false)); }
        try
        {
            var old = Keys.Take(6).ToArray();
            var connections = old.Select(key => (JsonNode)Receipt(key)).Concat(old.SelectMany(key => new JsonNode[] { Receipt(key), Receipt(key) }));
            Put("merge-check/transport/tool-usage.json", new JsonArray(connections.ToArray()));
            foreach (var key in new[] { "20", "21" })
            {
                var plc = Receipt(key); plc["workerModel"] = "foundation-host-plc-worker"; foreach (var field in new[] { "hostSha256", "runtimeSha256", "sdkWorkerSha256" }) plc[field] = new string('0', 64);
                Put($"merge-check/usage-v{key}-plc.json", new JsonArray(plc));
            }
            Put("receipts.json", new JsonArray(Keys.Select(key => (JsonNode)Receipt(key)).ToArray()));
            Assert.Equal(Encode(CatalogAudits.Usage(Root, recordsPath: receipts)), Encode(CatalogAudits.Usage(Root, input)));
            var different = Receipt("16"); different["checkedToolCount"] = 1;
            Put("merge-check/transport/tool-usage.json", new JsonArray(old.Select(key => (JsonNode)Receipt(key)).Append(different).ToArray()));
            Assert.Contains("same usage receipt: 16", Assert.Throws<ReleaseException>(() => CatalogAudits.Usage(Root, input)).Message);
            Put("merge-check/transport/tool-usage.json", new JsonArray(old.Take(5).Select(key => (JsonNode)Receipt(key)).ToArray()));
            Assert.Throws<ReleaseException>(() => CatalogAudits.Usage(Root, input));
        }
        finally { Directory.Delete(input, true); }
    }
    [Theory]
    [InlineData("ApplyToolBatch", "previewToken", "token")]
    [InlineData("PatchPlcBlockDocument", "expectedDocumentHash", "expectedFingerprint")]
    [InlineData("ManageUnifiedCwcPackage", "expectedPackageHash", "expectedFingerprint")]
    [InlineData("SetUnifiedGlobalScript", "previewToken", "expectedToken")]
    [InlineData("DeleteUnifiedHmiButtonEvent", "previewToken", "expectedToken")]
    [InlineData("DeleteUnifiedHmiDynamization", "previewToken", "expectedToken")]
    [InlineData("ImportPlcBlockVerified", "previewToken", "expectedToken")]
    [InlineData("ManageUnifiedEvent", "previewToken", "expectedToken")]
    [InlineData("SetUnifiedRuntimeSettings", "previewToken", "expectedToken")]
    public void SourceBindingHintsCannotDetachFromRenamedParameters(string name, string current, string old)
    {
        var examples = Read(Root, "reference/tool-catalog/EngineSourceExamples.json");
        var hints = examples[name]!["parameters"]!.AsObject();
        Assert.True(hints[current]!["requiresBinding"]!.GetValue<bool>());
        ToolUsageGenerator.ValidateSourceHints(Root, examples);
        hints[old] = hints[current]!.DeepClone(); hints.Remove(current);
        Assert.Throws<ReleaseException>(() => ToolUsageGenerator.ValidateSourceHints(Root, examples));
    }
    [Theory]
    [InlineData("argument")]
    [InlineData("hint")]
    [InlineData("type")]
    [InlineData("required")]
    [InlineData("nested")]
    [InlineData("execution")]
    [InlineData("operation")]
    [InlineData("sequence")]
    [InlineData("language")]
    public void UsageExamplesMustSatisfyEveryServedSchema(string mutation)
    {
        var schema = JsonNode.Parse("""{"type":"object","additionalProperties":false,"required":["fix","target"],"properties":{"fix":{"type":"boolean"},"target":{"type":"object","additionalProperties":false,"required":["name"],"properties":{"name":{"type":"string","minLength":1}}}}}""")!;
        var entry = JsonNode.Parse("""{"arguments":{"fix":false,"target":{"name":"fixture"}},"parameters":{"fix":{"requiresBinding":true}},"operations":{}}""")!;
        var catalog = new JsonObject { ["calls"] = new JsonObject { ["releaseExtensions"] = new JsonObject { ["19"] = "19" },
            ["profiles"] = new JsonObject { ["plc-foundation"] = new JsonObject { ["GetEnvironmentDiagnostics"] = entry } } }, ["examples"] = new JsonArray(), ["sequences"] = new JsonArray() };
        var schemas = new JsonObject { ["GetEnvironmentDiagnostics"] = schema };
        CatalogAudits.ValidateUsageExamples(catalog, schemas, "19");
        switch (mutation)
        {
            case "argument": entry["arguments"]!["unknown"] = 1; break;
            case "hint": entry["parameters"]!["oldFix"] = new JsonObject { ["requiresBinding"] = true }; break;
            case "type": entry["arguments"]!["fix"] = "false"; break;
            case "required": entry["arguments"]!.AsObject().Remove("fix"); break;
            case "nested": entry["arguments"]!["target"]!["name"] = ""; break;
            case "execution": entry["execution"] = new JsonObject { ["arguments"] = new JsonObject { ["fix"] = "false" } }; break;
            case "operation": entry["operations"]!["repair"] = new JsonObject { ["arguments"] = new JsonObject { ["fix"] = "false" } }; break;
            case "sequence": case "language":
                catalog[mutation == "sequence" ? "sequences" : "examples"]!.AsArray().Add(new JsonObject {
                    ["id"] = mutation, ["releaseKeys"] = new JsonArray("19"), ["profile"] = "plc-foundation",
                    ["steps"] = new JsonArray(new JsonObject { ["tool"] = "GetEnvironmentDiagnostics", ["arguments"] = new JsonObject { ["fix"] = "false" } }) }); break;
        }
        Assert.Throws<ReleaseException>(() => CatalogAudits.ValidateUsageExamples(catalog, schemas, "19"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OperationArgumentModesRetainEverySchemaAndHintCheck(bool complete)
    {
        var schema = JsonNode.Parse("""
            {"type":"object","additionalProperties":false,"required":["fix","target"],"properties":{
              "fix":{"type":"boolean"},"target":{"type":"object","additionalProperties":false,"required":["name"],
              "properties":{"name":{"type":"string","minLength":1}}}}}
            """)!;
        var entry = JsonNode.Parse("""
            {"arguments":{"fix":false,"target":{"name":"fixture"}},"operations":{"repair":{"arguments":{"fix":true}}}}
            """)!;
        var operation = entry["operations"]!["repair"]!;
        if (complete)
        {
            operation["argumentsMode"] = "complete";
            operation["arguments"]!["target"] = entry["arguments"]!["target"]!.DeepClone();
        }
        var catalog = new JsonObject { ["calls"] = new JsonObject {
            ["releaseExtensions"] = new JsonObject { ["19"] = "19" },
            ["profiles"] = new JsonObject { ["plc-foundation"] = new JsonObject { ["GetEnvironmentDiagnostics"] = entry } } },
            ["examples"] = new JsonArray(), ["sequences"] = new JsonArray() };
        var schemas = new JsonObject { ["GetEnvironmentDiagnostics"] = schema };
        CatalogAudits.ValidateUsageExamples(catalog, schemas, "19");
        foreach (string mutation in new[] { "argument", "type", "nested", "hint", "mode", "execution" })
        {
            var changed = catalog.DeepClone();
            var row = changed["calls"]!["profiles"]!["plc-foundation"]!["GetEnvironmentDiagnostics"]!;
            var variant = row["operations"]!["repair"]!;
            switch (mutation)
            {
                case "argument": variant["arguments"]!["unknown"] = 1; break;
                case "type": variant["arguments"]!["fix"] = "true"; break;
                case "nested": variant["arguments"]!["target"] = new JsonObject { ["name"] = "" }; break;
                case "hint": variant["parameters"] = new JsonObject { ["unknown"] = new JsonObject { ["requiresBinding"] = true } }; break;
                case "mode": variant["argumentsMode"] = "unknown"; break;
                case "execution": row["execution"] = new JsonObject { ["arguments"] = new JsonObject { ["fix"] = "true" } }; break;
            }
            Assert.Throws<ReleaseException>(() => CatalogAudits.ValidateUsageExamples(changed, schemas, "19"));
        }
        if (complete)
        {
            operation["arguments"]!.AsObject().Remove("target");
            Assert.Throws<ReleaseException>(() => CatalogAudits.ValidateUsageExamples(catalog, schemas, "19"));
        }
        else
        {
            // Execution remains a patch too: it must inherit the required target from the top-level call.
            entry["execution"] = new JsonObject { ["arguments"] = new JsonObject { ["fix"] = true } };
            CatalogAudits.ValidateUsageExamples(catalog, schemas, "19");
            entry["arguments"]!["target"]!["name"] = "";
            Assert.Throws<ReleaseException>(() => CatalogAudits.ValidateUsageExamples(catalog, schemas, "19"));
        }
    }

    [Fact]
    public void StdioAuditReceiptsMustBeDistinctAndHashChecked()
    {
        var path = Path.Combine(Path.GetTempPath(), "usage-records-" + Guid.NewGuid().ToString("N") + ".json");
        var rows = new JsonArray(Keys.Select(k => (JsonNode)new JsonObject { ["releaseKey"] = k, ["allSourceTextRetrievedAndHashChecked"] = false }).ToArray());
        try
        {
            File.WriteAllText(path, Encode(rows), new UTF8Encoding(false)); Assert.Throws<ReleaseException>(() => CatalogAudits.Usage(Root, recordsPath: path));
            rows[0]!["releaseKey"] = "21"; File.WriteAllText(path, Encode(rows), new UTF8Encoding(false)); Assert.Throws<ReleaseException>(() => CatalogAudits.Usage(Root, recordsPath: path));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void Immutable_foundation_candidates_keep_exact_source_validation()
    {
        const string family = "new Family(\"F00\", \"F00\", new[] { \"21\" }, new[] { \"Tool\" })";
        string source = family + "; FoundationCandidates = Array.AsReadOnly(new string[] { \"Tool\" });";
        Assert.Equal(new[] { "Tool" }, PortedFamilies.ParseFoundationCandidates(source, Keys));
        Assert.Throws<ArgumentException>(() => PortedFamilies.ParseFoundationCandidates(source.Replace("AsReadOnly", "Unknown"), Keys));
        Assert.Throws<ArgumentException>(() => PortedFamilies.ParseFoundationCandidates(source.Replace("{ \"Tool\" });", "{ \"Tool\", \"Tool\" });"), Keys));
        Assert.Throws<ArgumentException>(() => PortedFamilies.ParseFoundationCandidates(source + " FoundationCandidates = { \"Tool\" };", Keys));
    }

}
