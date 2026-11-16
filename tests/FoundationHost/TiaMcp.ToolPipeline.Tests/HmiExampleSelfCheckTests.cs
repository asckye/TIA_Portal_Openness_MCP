extern alias foundationhost;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaOpenness.Shared;
using Xunit;
using Foundation = foundationhost::TiaMcp.FoundationHost;

public sealed class HmiExampleSelfCheckTests
{
    public static IEnumerable<object[]> Releases() => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }.Select(r => new object[] { r });

    [Theory, MemberData(nameof(Releases))]
    public void Every_HMI_call_and_operation_matches_its_release_schema_and_navigation_catalog(string release)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "CLAUDE.md"))) root = root.Parent ?? throw new Exception("Repository not found.");
        var directory = Path.Combine(root.FullName, "reference/tool-examples");
        // Parse every maintained example file, even when it has no calls for this release.
        var files = Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(directory, p).Replace('\\', '/'), p => JsonNode.Parse(File.ReadAllText(p))!);
        var calls = files["calls.json"]!["profiles"]!.AsObject();
        var types = files["hmi-item-types.json"]!["releases"]?[release]?.AsArray().Select(v => (string)v!).ToHashSet(StringComparer.Ordinal) ?? [];
        using var worker = new MetadataWorker();
        var catalog = new FoundationCatalog(release, Foundation.LegacyHostToolRegistry.Create(worker, release, false, hostPorts: false));
        var errors = new List<string>();
        int checkedCalls = 0;
        foreach (var row in ToolUsageCatalog.ProfileEntries(release).OfType<JsonObject>())
        {
            var name = (string)row["currentName"]!;
            if (!catalog.All.TryGetValue(name, out var descriptor) || descriptor.Classification?.Domain.StartsWith("HMI", StringComparison.Ordinal) != true)
                continue;
            var source = (string)row["exampleSource"]!;
            if (calls[source]?[name] is not JsonObject example) { errors.Add(name + ": missing maintained schema example " + source); continue; }
            var arguments = example["arguments"]!.AsObject();
            Check(name + "/maintained", arguments, descriptor.Tool.InputSchema);
            Check(name + "/served", row["arguments"]!.AsObject(), descriptor.Tool.InputSchema);
            if (example["parameters"] is JsonObject parameters)
                foreach (var parameter in parameters)
                    if (parameter.Value is JsonObject specification && specification.ContainsKey("exampleValue"))
                        Visit(new JsonObject { [parameter.Key] = specification["exampleValue"]?.DeepClone(), ["requiresBinding"] = specification["requiresBinding"]?.DeepClone() },
                            name + "/parameters", StringObjectPath(descriptor.Tool.InputSchema));
            if (example["operations"] is JsonObject operations)
                foreach (var operation in operations)
                {
                    var merged = arguments.DeepClone().AsObject();
                    if (operation.Value?["arguments"] is JsonObject overrides)
                        foreach (var pair in overrides) merged[pair.Key] = pair.Value?.DeepClone();
                    Check(name + "/" + operation.Key, merged, descriptor.Tool.InputSchema);
                }
        }
        foreach (var sequence in files["sequences.json"]!.AsArray().Concat(ToolUsageCatalog.Sequences(engineSource: true)))
        {
            if (sequence!["releaseKeys"] is JsonArray releases && !releases.Any(v => (string?)v == release)) continue;
            foreach (var step in sequence["steps"]!.AsArray())
            {
                var name = (string)step!["tool"]!;
                if (catalog.All.TryGetValue(name, out var descriptor) && descriptor.Classification?.Domain.StartsWith("HMI", StringComparison.Ordinal) == true)
                    Check(name + "/" + sequence["id"], step["arguments"]!.AsObject(), descriptor.Tool.InputSchema);
            }
        }
        Assert.True(checkedCalls > 20, "Too few HMI examples checked: " + release + ": " + checkedCalls);
        Assert.True(errors.Count == 0, release + " HMI example failures:\n" + string.Join("\n", errors));

        void Check(string name, JsonObject args, JsonElement schema)
        {
            checkedCalls++;
            string tool = name.Split('/')[0];
            if (TiaMcp.Logic.V4.CanonicalToolParameters.ValidateAliases(tool, args) is { } aliasError)
                errors.Add(name + ": " + aliasError.Message);
            if (schema.TryGetProperty("required", out var required))
                foreach (var field in required.EnumerateArray())
                    if (!args.ContainsKey(field.GetString()!)) errors.Add(name + ": missing required argument " + field.GetString());
            foreach (var pair in args)
                if (!schema.GetProperty("properties").TryGetProperty(pair.Key, out _)) errors.Add(name + ": unknown argument " + pair.Key);
            Visit(args, name, StringObjectPath(schema));
            if (name.StartsWith("ManageUnifiedPlantNode/", StringComparison.Ordinal))
                try { EngineeringInputValidation.Request(new UnifiedRequest { Operation = "ManageUnifiedPlantNode", PlantPath = (string?)args["plantPath"] ?? "", Action = (string?)args["action"] ?? "", PlantObjectType = (string?)args["plantObjectType"] ?? "" }); }
                catch (ArgumentException error) { errors.Add(name + ": " + error.Message); }
        }
        bool StringObjectPath(JsonElement schema) => schema.GetProperty("properties").TryGetProperty("objectPath", out var pathSchema)
            && pathSchema.TryGetProperty("type", out var pathType) && pathType.ValueKind == JsonValueKind.String && pathType.GetString() == "string";
        void Visit(JsonNode node, string location, bool stringPath)
        {
            if (node is JsonArray array) { foreach (var value in array) if (value != null) Visit(value, location, stringPath); return; }
            if (node is not JsonObject obj) return;
            foreach (var (field, value) in obj)
            {
                try
                {
                    if (field == "objectPath" && value != null)
                    {
                        if (stringPath && value is JsonValue scalar && scalar.TryGetValue<string>(out var classicPath))
                        {
                            if (classicPath.Length != 0) EngineeringInputValidation.AbsoluteHmiPath(classicPath, rootAllowed: true);
                            continue;
                        }
                        using var parsed = JsonDocument.Parse(value.ToJsonString());
                        EngineeringInputValidation.Path(value.ToJsonString(), EngineeringInputValidation.Value(TiaMcpServer.Siemens.Services.UnifiedInputCodec.Decode(parsed.RootElement)));
                        if (value is JsonArray steps && steps.Any(s => (string?)s?["property"] == "ScreenFolder")) throw new ArgumentException("Unified has no ScreenFolder.");
                    }
                    if (field is "screenPath" or "tagTablePath" or "groupPath" or "plantPath" or "typePath" or "folderPath" && value is JsonValue path && path.TryGetValue<string>(out var text) && text.Length != 0)
                        EngineeringInputValidation.AbsoluteHmiPath(text, rootAllowed: field is "folderPath" or "groupPath");
                    if (field == "itemType" && value is JsonValue type && type.TryGetValue<string>(out var itemType) && itemType.Length != 0)
                    {
                        bool bound = itemType.StartsWith('<') && obj["requiresBinding"]?.GetValue<bool>() == true;
                        bool known = types.Any(t => t == itemType || t.EndsWith("." + itemType, StringComparison.Ordinal) || t.EndsWith(".Hmi" + itemType, StringComparison.Ordinal));
                        if (!bound && !known) throw new ArgumentException("itemType absent from the release type catalog: " + itemType);
                    }
                }
                catch (ArgumentException error) { errors.Add(location + "/" + field + ": " + error.Message); }
                if (value is JsonObject or JsonArray) Visit(value, location + "/" + field, stringPath);
            }
        }
    }
    private sealed class MetadataWorker : Foundation.IFoundationWorker
    {
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token) => throw new Exception("Self-check must not dispatch native operations.");
        public void Dispose() { }
    }
}
