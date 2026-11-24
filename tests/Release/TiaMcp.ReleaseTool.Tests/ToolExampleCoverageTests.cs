using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class ToolExampleCoverageTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static JsonNode Read(string path) => JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.FindRoot(), path)))!;
    private static readonly Lazy<Dictionary<string, string>> Descriptions = new(() => Read("manifest/tools-list.json")["tools"]!.AsArray()
        .ToDictionary(row => (string)row!["name"]!, row => (string)row!["description"]!));

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17")]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Registered_parameters_and_operations_have_maintained_examples_or_guidance(string release)
    {
        var calls = Read("reference/tool-examples/calls.json");
        var shared = Read("reference/tool-examples/metadata.json")["parameterSources"]!.AsObject();
        var steps = Read("reference/tool-examples/sequences.json").AsArray().Where(sequence =>
            sequence!["releaseKeys"]!.AsArray().Any(key => (string?)key == release))
            .SelectMany(sequence => sequence!["steps"]!.AsArray()).ToLookup(step => (string)step!["tool"]!);
        var missing = new List<string>();
        int shown = 0, ownNotes = 0, schemas = 0, sharedNotes = 0;
        foreach (var tool in Read($"manifest/contracts/v4/baseline/{release}.json")["tools"]!.AsArray())
        {
            string name = (string)tool!["name"]!;
            var schema = tool["inputSchema"]!.AsObject();
            var properties = schema["properties"]!.AsObject();
            var entry = calls["profiles"]![ToolUsageCatalog.ExampleSource(name, release)]![name]!;
            var arguments = ToolUsageCatalog.ReleaseArguments(calls, name, release, entry["arguments"]!);
            if (arguments.Count == 0 && (schema["required"] is JsonArray { Count: > 0 } || string.IsNullOrWhiteSpace((string?)entry["note"])))
                missing.Add($"{release}/{name} empty call requires an explanation and must meet all required inputs");
            var examples = new List<JsonObject> { arguments };
            var served = new Lazy<JsonObject>(() => ToolUsageCatalog.Describe(name, release, "plc-foundation", "", schema));
            void Add(JsonNode? value)
            {
                if (value is JsonObject args) examples.Add(args);
            }
            Add(entry["execution"]?["arguments"]);
            foreach (var step in steps[name]) Add(step!["arguments"]);
            foreach (var operation in entry["operations"]?.AsObject() ?? [])
            {
                if (operation.Value!["releaseKeys"] is JsonArray keys && !keys.Any(key => (string?)key == release)) continue;
                Add(operation.Value["arguments"]);
                Add(operation.Value["execution"]?["arguments"]);
            }
            foreach (var parameter in entry["parameters"]?.AsObject() ?? [])
                if (parameter.Value?["argumentExamples"] is JsonArray)
                    foreach (var variant in served.Value["parameterSources"]![parameter.Key]!["argumentExamples"]!.AsArray()) Add(variant);
            foreach (var (parameter, property) in properties)
            {
                string description = (string?)property?["description"] ?? "";
                if (examples.Any(example => example.ContainsKey(parameter))) shown++;
                else if (HasNote(entry["parameters"]?[parameter])) ownNotes++;
                else if (HasDescription(parameter, description)) schemas++;
                else if (HasNote(shared[parameter]) && (shared[parameter]?["fallbackMeaning"] == null || string.IsNullOrWhiteSpace(description))) sharedNotes++;
                else missing.Add($"{release}/{name} parameter {parameter}");
            }

            string? selector = new[] { "action", "operation", "actionKind", "direction", "mode" }.FirstOrDefault(properties.ContainsKey)
                ?? (name is "CompareProjects" or "BuildAndImportPlcArtifact" ? "kind" : name == "GetDriveParameters" ? "source" : null);
            void CheckChoices(string field, IEnumerable<string> choices)
            {
                var spec = properties[field]!;
                string description = (string?)spec["description"] ?? "";
                foreach (string choice in choices.Distinct().Where(value => value.Length > 0))
                {
                    if (examples.Any(example => (string?)example[field] == choice)
                        || (string?)(arguments[field] ?? spec["default"]) == choice) continue;
                    var options = served.Value["operations"]!.AsArray();
                    if (field == selector)
                    {
                        var option = options.FirstOrDefault(row => (string?)row!["operation"] == choice);
                        if ((bool?)option?["refusedByReleaseGuard"] == true && option["variants"]!.AsArray().All(row =>
                            !string.IsNullOrWhiteSpace((string?)row!["reason"]))) continue;
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace((string?)served.Value["parameterSources"]![field]?["unavailableChoices"]?[choice])) continue;
                        var contexts = options.SelectMany(row => row!["variants"]!.AsArray())
                            .Where(row => (string?)row!["context"]?[field] == choice).ToArray();
                        if (contexts.Length > 0 && contexts.All(row => (bool?)row!["availableInRelease"] == false
                            && !string.IsNullOrWhiteSpace((string?)row["reason"]))) continue;
                    }
                    if (description.Contains($"refuses '{choice}'", StringComparison.Ordinal)
                        || description.Contains($"{choice} is refused until ", StringComparison.Ordinal)) continue;
                    missing.Add($"{release}/{name} {field}={choice}");
                }
            }
            if (selector != null)
            {
                string description = (string?)properties[selector]!["description"] ?? "";
                CheckChoices(selector, (properties[selector]!["enum"]?.AsArray().Select(choice => (string)choice!) ?? [])
                    .Concat(Choices(description)).Concat(DescribedChoices(selector, description))
                    .Concat(DescribedChoices(selector, ToolUsageCatalog.ReleaseDescription(Descriptions.Value[name], release))));
            }
            foreach (string field in new[] { "direction", "mode", "family", "objectKind" })
                if (field != selector && properties[field]?["enum"] is JsonArray values) CheckChoices(field, values.Select(value => (string)value!));
        }
        output.WriteLine($"V{release} parameter coverage: examples={shown}, own notes={ownNotes}, schema text={schemas}, shared notes={sharedNotes}");
        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    private static bool HasDescription(string parameter, string description) => !string.IsNullOrWhiteSpace(description)
        && !description.Trim(' ', '.', ':').Equals(parameter, StringComparison.OrdinalIgnoreCase)
        && !description.StartsWith("Exact foundation argument:", StringComparison.OrdinalIgnoreCase);

    private static bool Placeholder(string value) => string.IsNullOrWhiteSpace(value)
        || Regex.IsMatch(value, @"<[^<>]*>|\bTODO\b|^\s*x+\s*$", RegexOptions.IgnoreCase);

    private static bool ConcreteValue(JsonNode? value) => value switch
    {
        JsonObject obj => obj.Count > 0 && obj.All(field => !Placeholder(field.Key) && ConcreteValue(field.Value)),
        JsonArray array => array.Count > 0 && array.All(ConcreteValue),
        JsonValue scalar => !scalar.TryGetValue<string>(out var text) || !Placeholder(text),
        _ => false
    };

    private static bool HasNote(JsonNode? note) => note is JsonObject obj && (ConcreteValue(obj["exampleValue"])
        || !string.IsNullOrWhiteSpace((string?)(obj["meaning"] ?? obj["description"] ?? obj["fallbackMeaning"])));

    [Theory, InlineData("<name>"), InlineData("TODO"), InlineData("xXx"), InlineData("C:/<directory>/File.xml")]
    [InlineData("<>"), InlineData(" ")]
    public void Placeholder_values_alone_do_not_explain_parameters(string value)
    {
        Assert.False(HasNote(new JsonObject { ["exampleValue"] = value }));
        Assert.False(HasNote(new JsonObject { ["exampleValue"] = new JsonArray(JsonValue.Create(value)) }));
        Assert.False(HasNote(new JsonObject { ["exampleValue"] = new JsonObject { [value] = true } }));
    }

    [Theory, InlineData("GetExportContent", "offset", "UTF-16"), InlineData("ManageTechnologyObject", "confirm", "delete")]
    [InlineData("ListPlcBlocks", "limit", "1..100"), InlineData("RunCapabilitySelfTest", "limit", "1 through 50")]
    [InlineData("ManageClassicHmiGraphic", "importOptions", "None | Override")]
    public void Specific_schema_meaning_reaches_the_served_parameter(string name, string field, string detail)
    {
        var schema = Read("manifest/contracts/v4/baseline/21.json")["tools"]!.AsArray()
            .Single(row => (string?)row!["name"] == name)!["inputSchema"]!.AsObject();
        var usage = ToolUsageCatalog.Describe(name, "21", "plc-foundation", "", schema);
        Assert.Contains(detail, (string?)usage["parameterSources"]![field]!["meaning"]);
    }

    [Fact]
    public void Harvested_delete_preview_evidence_does_not_verify_the_other_actions()
    {
        Assert.Contains("delete preview. rc4-verify-v20, row 138", (string?)Usage("ManageTechnologyObject", "20", "delete")["validation"]);
        Assert.Contains("NOT RUN", (string?)Usage("ManageTechnologyObject", "20", "create")["validation"]);
        Assert.Contains("NOT RUN", (string?)Usage("ManageTechnologyObject", "21", "delete")["validation"]);
        var scopedCompile = (string)Usage("CompilePlcSoftware", "18", "block")["validation"]!;
        Assert.DoesNotContain("Real-machine verified", scopedCompile);
        Assert.Contains("NOT RUN", scopedCompile);
    }

    [Theory, InlineData("18", "left blocks-", "right MCP_RC_FC.xml"), InlineData("20", "left MCP_RC_FB.xml", "right blocks-")]
    [InlineData("21", "left MCP_RC_FC.xml", "right blocks-")]
    public void Comparison_evidence_preserves_the_direction_of_the_recorded_file_pair(string release, string left, string right)
    {
        var example = Usage("ComparePlcBlockDocuments", release);
        Assert.Contains(left, (string?)example["validation"]);
        Assert.Contains(right, (string?)example["validation"]);
        Assert.Contains("passed in rows 57 and 58", (string?)example["note"]);
    }

    [Theory, InlineData("GetVersionControlStatus"), InlineData("GetProjectSettings"), InlineData("CheckLibraryUpdates")]
    public void Default_queries_need_no_named_target(string name)
    {
        var example = Usage(name, "20");
        Assert.Empty(example["request"]!["params"]!["arguments"]!.AsObject());
        Assert.False(string.IsNullOrWhiteSpace((string?)example["note"]));
    }

    [Theory, InlineData("HmiScreen", 2), InlineData("HmiTagTable", 2), InlineData("HmiTag", 3), InlineData("HmiScreenItem", 3)]
    public void Reflection_examples_supply_each_component_of_the_native_hmi_address(string kind, int components)
    {
        foreach (string name in new[] { "DescribeObject", "GetObjectProperty", "InvokeObject", "ListObjectChildren" })
        {
            var args = Usage(name, "21", kind)["request"]!["params"]!["arguments"]!;
            Assert.Equal(kind, (string?)args["objectKind"]);
            var path = ((string)args["objectPath"]!).Split(':');
            Assert.Equal(components, path.Length);
            Assert.Contains("GetProjectTree", path[0]);
        }
    }

    [Theory]
    [InlineData("20", "create", "rc4-verify-v20, row 115")]
    [InlineData("20", "", "NOT RUN")]
    [InlineData("20", "createFromType", "NOT RUN")]
    [InlineData("21", "create", "NOT RUN")]
    public void Folder_preview_evidence_is_scoped_to_its_release_and_operation(string release, string operation, string expected)
    {
        var example = Usage("ManageSivarcRuleContainer", release, operation);
        Assert.Contains(expected, (string?)example["validation"]);
        var args = example["request"]!["params"]!["arguments"]!.AsObject();
        Assert.Equal(operation.Length == 0 ? "create" : operation, (string?)args["action"]);
        Assert.Equal(operation == "createFromType", args.ContainsKey("typePath"));
    }

    [Fact]
    public void Catalog_pairs_and_target_dependent_examples_reach_the_served_request()
    {
        JsonObject Args(string name, string release, string operation = "") =>
            Usage(name, release, operation)["request"]!["params"]!["arguments"]!.AsObject();
        var device = Args("CreateDevice", "16");
        Assert.Equal("6ES7 513-1AL02-0AB0", (string?)device["orderNumber"]);
        Assert.Equal("V2.8", (string?)device["version"]);
        var hardware = Args("CreateHardwareDevice", "18");
        Assert.Equal("6ES7 515-2AM02-0AB0", (string?)hardware["preferredMlfb"]);
        Assert.Equal("V2.9", (string?)hardware["preferredVersion"]);
        foreach (string release in new[] { "18", "19" })
            Assert.Equal("OrderNumber:6AV2 124-0GC01-0AX0/17.0.0.0", (string?)Args("CreateHardwareCatalogDevice", release)["keyword"]);
        Assert.Equal(443, (int?)Args("ManageMultiuserSession", "19", "connectServer")["port"]);
        Assert.NotEmpty((string)Args("ManagePlcAlarmTextList", "19", "createFromMasterCopy")["libraryName"]!);
        Assert.Contains("GetProjectTree", (string?)Args("ExportPlcWatchTablesToDirectory", "19", "force")["softwarePath"]);
        Assert.Contains("ListHmiTagTables", (string?)Args("DescribeHmiTagTable", "19", "localized")["tagTableName"]);
        Assert.NotEmpty((string)Args("ManageClassicHmiFolder", "18", "create")["newName"]!);
        Assert.NotEmpty((string)Args("EnsureUnifiedHmiTag", "21")["connectionName"]!);
        Assert.Contains("existing directory", (string?)Usage("CreateVersionControlWorkspace", "19")["note"]);
        Assert.Contains("data.outputFile", (string?)Usage("ComparePlcBlockDocuments", "14sp1")["note"]);
    }

    private static JsonNode Usage(string name, string release, string operation = "")
    {
        var tool = Read($"manifest/contracts/v4/baseline/{release}.json")["tools"]!.AsArray().Single(row => (string?)row!["name"] == name)!;
        return ToolUsageCatalog.Describe(name, release, "plc-foundation", Descriptions.Value[name],
            tool["inputSchema"]!.AsObject(), operation: operation)["example"]!;
    }

    [Theory]
    [InlineData("EnsureUnifiedHmiButtonAction", "actionKind", "reset-bit")]
    [InlineData("BuildUnifiedHmiButtonActionScript", "actionKind", "open-popup")]
    [InlineData("SetPlcWebOperatingMode", "mode", "stop")]
    [InlineData("RunPlcCompanionTool", "mode", "help")]
    [InlineData("PlanOnlineReadOnlyMonitoring", "mode", "watch-table-export-plan")]
    public void Prose_operation_names_select_their_own_complete_request(string name, string selector, string operation)
    {
        var example = Usage(name, "21", operation);
        Assert.Equal("parameterized-call-example", (string?)example["kind"]);
        Assert.Equal(operation, (string?)example["request"]!["params"]!["arguments"]![selector]);
    }

    // Some descriptions enumerate operations with a parenthetical explanation after each name.
    private static IEnumerable<string> DescribedChoices(string selector, string description)
    {
        string prose = Regex.Split(description, @" Examples?:")[0];
        while (Regex.IsMatch(prose, @"\([^()]*\)")) prose = Regex.Replace(prose, @"\([^()]*\)", "");
        const string separator = @"(?:\s*,\s*(?:or\s+)?|\s*[/|]\s*|\s+or\s+)";
        string list = @"\b" + selector + @"s?\s*(?:[:=]\s*|\s+)([a-z][\w-]*(?:" + separator + @"[a-z][\w-]*)+)";
        return Regex.Matches(prose, list, RegexOptions.IgnoreCase)
            .SelectMany(match => Regex.Split(match.Groups[1].Value, separator));
    }

    private static IEnumerable<string> Choices(string description)
    {
        // Older string selectors enumerate values in prose: 'run' or 'stop', catalog/help/run, or two hyphenated modes.
        var quoted = Regex.Matches(description, "'([A-Za-z][A-Za-z0-9-]*)'");
        if (quoted.Count > 1) return quoted.Select(match => match.Groups[1].Value);
        var list = Regex.Match(description, @"^(?:\w+:\s*)?([\w-]+(?:(?:/|\s*\|\s*|\s+or\s+)[\w-]+)+)(?=[.;]|$)");
        return list.Success ? Regex.Split(list.Groups[1].Value, @"/|\s*\|\s*|\s+or\s+") : [];
    }
}
