using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;

namespace TiaMcp.FoundationHost;

// The host catalog owns the manifest rows. The old harness remains a validation
// oracle during retirement; it writes only the comparison file, never the manifest.
internal static class HostToolsListWriter
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static void Write(HostOptions options, EngineHostPipeline pipeline)
    {
        var catalog = McpServer.CatalogView;
        string release = options.ReleaseKey;
        var supplementary = catalog.IncludingUnavailable.Values.Where(tool => !catalog.All.ContainsKey(tool.Name)
            && PortedFamilies.Contains(tool.Name)
            && ReleaseCatalogData.Releases[release].ManifestSupplementaryReleases.Any(key => PortedFamilies.Available(key, tool.Name))).ToArray();
        var entries = pipeline.Tools.Select(tool => tool.ProtocolTool).Concat(supplementary.Select(tool => tool.Tool)).ToArray();
        var profiles = ToolUsageCatalog.ProfileEntries(release).ToDictionary(row => (string)row!["currentName"]!, StringComparer.Ordinal);
        foreach (var key in ReleaseCatalogData.Releases.Keys)
            foreach (var row in ToolUsageCatalog.ProfileEntries(key).Where(row => supplementary.Any(tool => tool.Name == (string)row!["currentName"]!)))
                profiles[(string)row!["currentName"]!] = row;
        var methods = typeof(EngineHostPipeline).Assembly.GetTypes().SelectMany(type => type.GetMethods())
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null)
            .ToDictionary(ReleaseCatalogData.Name, StringComparer.Ordinal);
        var names = entries.Select(tool => tool.Name).ToArray();
        var rows = new List<JsonNode>();
        foreach (var tool in entries)
        {
            var properties = tool.InputSchema.GetProperty("properties").EnumerateObject().Select(parameter => parameter.Name).ToArray();
            bool shared = profiles[tool.Name]!["profiles"]!.AsArray().Any(value => (string?)value == "plc-foundation");
            var usage = ToolUsageCatalog.Describe(tool.Name, supplementary.Any(entry => entry.Name == tool.Name) ? "20" : release,
                shared ? "plc-foundation" : "full-engine", tool.Description ?? "", JsonNode.Parse(tool.InputSchema.GetRawText())!.AsObject(), roster: names);
            var example = usage["example"]!["request"]!["params"]!["arguments"]!.AsObject();
            if (example.Select(pair => pair.Key).Except(properties, StringComparer.Ordinal).Any()
                || tool.InputSchema.TryGetProperty("required", out var required) && required.EnumerateArray().Any(parameter => !example.ContainsKey(parameter.GetString()!)))
                throw new InvalidDataException(tool.Name + " product usage example does not fit the advertised parameter roster");
            var tag = ToolTaxonomy.For(tool.Name);
            var operation = ToolTaxonomy.OperationOf(tool.Name, tool.Description);
            string category = ToolTaxonomy.CategoryOf(tag.Domain);
            if (category == "uncategorized")
                throw new InvalidDataException("Uncategorized tool: " + tool.Name);
            methods.TryGetValue(tool.Name, out var method);
            rows.Add(new JsonObject
            {
                ["name"] = tool.Name, ["layer"] = tag.Layer, ["category"] = category, ["domain"] = tag.Domain,
                ["operation"] = operation.Operation, ["operationInferred"] = operation.Inferred,
                ["method"] = shared ? "FoundationV4Tool.InvokeAsync" : method?.Name ?? throw new InvalidDataException("Missing declaration: " + tool.Name),
                ["returnType"] = shared ? "CallToolResult" : ReleaseCatalogData.Releases[release].ManifestReturnTypes
                    .GetValueOrDefault(tool.Name, method!.ReturnType.Name),
                ["parameters"] = new JsonArray(properties.Select(parameter => (JsonNode?)JsonValue.Create(parameter)).ToArray()),
                ["description"] = tool.Description, ["example"] = example.ToJsonString()
            });
        }
        var sorted = rows.ToArray();
        // Preserve the historical PowerShell Sort-Object permutation, including its null sort keys.
        Array.Sort(sorted, Comparer<JsonNode>.Create((_, _) => 0));
        var parameters = supplementary.SelectMany(tool => tool.Parameters).ToArray();
        var discipline = McpServer.SchemaHintStatistics();
        int Count(string field) => (int)discipline[field]!;
        var output = new JsonObject
        {
            ["package"] = options.CatalogPackage,
            ["generatedAt"] = options.CatalogGeneratedAt ?? DateTimeOffset.UtcNow.ToString("o"),
            ["source"] = "FoundationHost V" + release + " full product catalog and declared V20-only registrations; shared Foundation implementations and engine-only tools; no worker or TIA started",
            ["fileVersion"] = FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).FileVersion,
            ["exeSha256"] = EngineCatalog.Hash(Environment.ProcessPath!),
            ["workerSha256"] = EngineCatalog.Hash(options.EngineWorkerExe!),
            ["toolCount"] = rows.Count,
            ["note"] = "Full product roster. Lite advertises the V19 shared tools plus the engine discovery bridge. Withdrawn lifecycle tools are absent. Runtime tools/list is authoritative.",
            ["callDiscipline"] = new JsonObject
            {
                ["parameters"] = Count("parameters") + parameters.Length,
                ["parametersUndocumented"] = Count("parametersUndocumented") + parameters.Count(parameter => string.IsNullOrWhiteSpace(parameter.Description)),
                ["parametersFromVocabulary"] = Count("parametersFromVocabulary") + parameters.Count(parameter => parameter.Synthesized),
                ["toolsWithUndocumentedParameters"] = Count("toolsWithUndocumentedParameters") + supplementary.Count(tool => tool.Parameters.Any(parameter => string.IsNullOrWhiteSpace(parameter.Description))),
                ["toolsWithEnumHints"] = Count("toolsWithEnumHints") + supplementary.Count(tool => tool.Parameters.Any(parameter => parameter.AllowedValues.Count != 0)),
                ["enumHints"] = Count("enumHints") + parameters.Count(parameter => parameter.AllowedValues.Count != 0),
                ["defaultHints"] = Count("defaultHints") + parameters.Count(parameter => parameter.DefaultJson != null),
                ["examplesCurated"] = rows.Count,
                ["recipes"] = ToolUsageCatalog.Sequences().Count
            },
            ["categories"] = new JsonArray(ToolTaxonomy.Categories.Select(category => (JsonNode)new JsonObject
            {
                ["key"] = category.Key, ["nameZh"] = category.NameZh, ["nameEn"] = category.NameEn,
                ["description"] = category.Description,
                ["domains"] = new JsonArray(category.Domains.Select(domain => (JsonNode?)JsonValue.Create(domain)).ToArray())
            }).ToArray()),
            ["tools"] = new JsonArray(sorted)
        };
        string destination = Path.GetFullPath(options.CatalogToolsList!);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, output.ToJsonString(s_options), new UTF8Encoding(false));
        Console.WriteLine("FoundationHost product metadata: " + rows.Count + " tools; all examples and taxonomy categories validated.");
    }
}
