using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

internal static class DeveloperChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static int checks;
    private static int failures;

    private static void Check(bool condition, string description)
    {
        checks++;
        if (condition) Console.WriteLine("PASS " + description);
        else { failures++; Console.Error.WriteLine("FAIL " + description); }
    }

    internal static int MatchPlcName(Assembly server)
    {
        Reset();
        var type = FindType(server, "TiaMcpServer.Siemens.Guard");
        var method = type.GetMethod("MatchPlcName", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(type.FullName, "MatchPlcName");
        void Match(string label, string[] available, string token, string? expected)
        {
            var got = method.Invoke(null, new object[] { available, token }) as string;
            Check(string.Equals(got, expected, StringComparison.Ordinal),
                $"{label,-22} '{token}' -> {(got == null ? "<null>" : got)}");
        }

        Match("single exact", new[] { "PLC_1" }, "PLC_1", "PLC_1");
        Match("single case", new[] { "PLC_1" }, "plc_1", "PLC_1");
        Match("single trim", new[] { "PLC_1" }, " PLC_1 ", "PLC_1");
        Match("single wrong name", new[] { "PLC_1" }, "garbage", null);
        Match("single substr", new[] { "PLC_1" }, "plc", null);
        Match("single omitted", new[] { "PLC_1" }, "", "PLC_1");

        var multi = new[] { "MainPLC", "SafetyPLC", "PLC_1" };
        Match("multi exact", multi, "SafetyPLC", "SafetyPLC");
        Match("multi case", multi, "safetyplc", "SafetyPLC");
        Match("multi trim", multi, " SafetyPLC ", "SafetyPLC");
        Match("multi substr", multi, "Safety", null);
        Match("multi ambiguous", multi, "PLC", null);
        Match("multi not-found", multi, "NoSuch", null);
        Match("multi empty", multi, "", null);

        var two = new[] { "PLC_1", "PLC_2" };
        Match("two exact", two, "PLC_2", "PLC_2");
        Match("two digit", two, "1", null);
        Match("two ambiguous", two, "PLC", null);
        Match("empty list", Array.Empty<string>(), "PLC_1", null);
        Console.WriteLine($"RESULT: {checks} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    internal static int GenerateToolsList(Assembly server, string publicApiDirectory, string outputPath, string packageName,
        string foundationHost, string bundleRoot)
    {
        if (string.IsNullOrWhiteSpace(publicApiDirectory) || string.IsNullOrWhiteSpace(outputPath)
            || !File.Exists(foundationHost) || !Directory.Exists(bundleRoot))
            throw new ArgumentException("generate-tools-list requires PublicAPI directory, output file, FoundationHost and bundle root.");
        Reset();
        AddResolver(server, publicApiDirectory);
        var runtimePath = Path.GetDirectoryName(server.Location)!;
        string release = server.GetReferencedAssemblies().First(a => a.Name!.StartsWith("Siemens.Engineering", StringComparison.Ordinal)).Version!.Major.ToString();
        var logic = Assembly.LoadFrom(Path.Combine(runtimePath, "TiaMcp.Logic.dll"));
        var type = server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
        var taxonomy = logic.GetType("TiaMcpServer.ModelContextProtocol.ToolTaxonomy", true)!;
        var categoryOf = taxonomy.GetMethod("CategoryOf", All)!;
        var parseTag = taxonomy.GetMethod("For", All)!;
        var operationOf = taxonomy.GetMethod("OperationOf", All)!;
        var categories = new JsonArray();
        foreach (var category in (IEnumerable)taxonomy.GetField("Categories", All)!.GetValue(null)!)
        {
            categories.Add(new JsonObject
            {
                ["key"] = Get(category!, "Key")!.ToString(),
                ["nameZh"] = Get(category!, "NameZh")!.ToString(),
                ["nameEn"] = Get(category!, "NameEn")!.ToString(),
                ["description"] = Get(category!, "Description")!.ToString(),
                ["domains"] = new JsonArray(((IEnumerable)Get(category!, "Domains")!).Cast<object>().Select(item => (JsonNode?)JsonValue.Create(item.ToString())).ToArray())
            });
        }

        // Keep validating the engine recipe library; product examples below use the
        // same schema-aware usage library as FoundationHost, excluding withdrawn tools.
        var engineDescriptors = (IDictionary)type.GetMethod("AllToolDescriptors", All)!.Invoke(null, new object[] { true })!;
        Func<string, IReadOnlyList<KeyValuePair<string, bool>>?> recipeParameters = name => {
            if (engineDescriptors.Contains(name)) return ((IEnumerable)Get(engineDescriptors[name]!, "Parameters")!).Cast<object>()
                .Select(p => new KeyValuePair<string, bool>((string)Get(p, "Name")!, (bool)Get(p, "Required")!)).ToArray();
            if (!EngineSurface.IsHostTool(server, name)) return null;
            return HostPortRunner.Metadata(server, name)["parameters"]!.AsArray().Select(p =>
                new KeyValuePair<string, bool>((string)p!["name"]!, (bool)p["required"]!)).ToArray();
        };
        var recipeErrors = ((IEnumerable)logic.GetType("TiaMcpServer.ModelContextProtocol.ToolRecipes", true)!
            .GetMethod("ValidateAgainst", All)!.Invoke(null, new object[] { recipeParameters })!).Cast<string>().ToArray();
        Check(recipeErrors.Length == 0, recipeErrors.Length == 0 ? "ToolRecipes.ValidateAgainst accepts every recipe" : string.Join("; ", recipeErrors));

        using var product = ProductCatalog(server, foundationHost, bundleRoot, outputPath);
        var productTools = product.RootElement.GetProperty("tools").EnumerateArray().ToList();
        var supplementary = new HashSet<string>(StringComparer.Ordinal);
        var families = Program.FindReferencedType(server, "TiaMcp.Adapters.Contracts.PortedFamilies");
        var contains = families.GetMethod("Contains")!;
        var available = families.GetMethod("Available")!;
        var supplementalParameters = new List<object>();
        int supplementalEnumTools = 0, supplementalUndocumentedTools = 0;
        if (release == "21")
        {
            foreach (DictionaryEntry entry in engineDescriptors)
            {
                var name = (string)entry.Key;
                if (!(bool)contains.Invoke(null, new object[] { name })!) continue;
                if (!new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }.Any(key => (bool)available.Invoke(null, new object[] { key, name })!)
                    || (bool)available.Invoke(null, new object[] { "21", name })!) continue;
                var descriptor = entry.Value!;
                productTools.Add(JsonSerializer.SerializeToElement(Get(descriptor, "Tool")));
                supplementary.Add(name);
                var parameters = ((IEnumerable)Get(descriptor, "Parameters")!).Cast<object>().ToArray();
                supplementalParameters.AddRange(parameters);
                if (parameters.Any(parameter => ((IEnumerable)Get(parameter, "AllowedValues")!).Cast<object>().Any())) supplementalEnumTools++;
                if (parameters.Any(parameter => string.IsNullOrWhiteSpace((string)Get(parameter, "Description")!))) supplementalUndocumentedTools++;
            }
        }
        var disciplineRoot = product.RootElement.GetProperty("callDiscipline");
        var previousUndocumented = ReadPreviousUndocumented(outputPath, out var previousProduct);
        var undocumented = disciplineRoot.GetProperty("parametersUndocumented").GetInt32()
            + supplementalParameters.Count(parameter => string.IsNullOrWhiteSpace((string)Get(parameter, "Description")!));
        // Compare like rosters during the migration from a raw engine manifest.
        var comparableUndocumented = previousProduct ? undocumented
            : ((JsonObject)type.GetMethod("SchemaHintStatistics", All)!.Invoke(null, null)!)["parametersUndocumented"]!.GetValue<int>();
        Check(previousUndocumented == null || comparableUndocumented <= previousUndocumented.Value,
            previousUndocumented == null || comparableUndocumented <= previousUndocumented.Value
                ? "undocumented parameter count did not increase"
                : $"Parameters without a [Description] went up from {previousUndocumented} to {comparableUndocumented}");

        var catalogType = server.GetType("TiaMcpServer.ModelContextProtocol.ToolCatalog", true)!;
        var catalog = catalogType.GetProperty("Engine", All)!.GetValue(null)!;
        var methods = ((IEnumerable)catalogType.GetProperty("Methods", All)!.GetValue(catalog)!).Cast<object>()
            .ToDictionary(entry => Get(entry, "Key")!.ToString()!, entry => (MethodInfo)Get(entry, "Value")!, StringComparer.Ordinal);
        var usageType = logic.GetType("TiaOpenness.Shared.ToolUsageCatalog", true)!;
        var profiles = (IEnumerable)usageType.GetMethod("ProfileEntries", All)!.Invoke(null, new object[] { release, 4, false })!;
        var profileRows = profiles.Cast<JsonObject>().ToDictionary(row => (string)row["currentName"]!, StringComparer.Ordinal);
        var supplementaryProfiles = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (string key in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
            foreach (var row in ((IEnumerable)usageType.GetMethod("ProfileEntries", All)!.Invoke(null, new object[] { key, 4, false })!).Cast<JsonObject>())
                if (supplementary.Contains((string)row["currentName"]!)) supplementaryProfiles[(string)row["currentName"]!] = row;
        foreach (var row in supplementaryProfiles.Values) profileRows.Add((string)row["currentName"]!, row);
        var productNames = productTools.Select(entry => entry.GetProperty("name").GetString()!).ToArray();
        var rows = new List<(string Name, JsonObject Value)>();
        foreach (var entry in productTools)
        {
            var name = entry.GetProperty("name").GetString()!;
            var description = entry.GetProperty("description").GetString()!;
            var schema = entry.GetProperty("inputSchema");
            var properties = schema.GetProperty("properties").EnumerateObject().Select(parameter => parameter.Name).ToArray();
            var profile = profileRows[name];
            bool shared = (string?)profile["exampleSource"] == "plc-foundation";
            var usage = (JsonObject)usageType.GetMethod("Describe", All)!.Invoke(null, new object?[] {
                name, supplementary.Contains(name) ? "20" : release, "plc-foundation", description,
                JsonNode.Parse(schema.GetRawText())!.AsObject(), null, null, "", productNames, null, null, null
            })!;
            var example = usage["example"]!["request"]!["params"]!["arguments"]!.AsObject();
            Check(!example.Select(pair => pair.Key).Except(properties).Any()
                && (!schema.TryGetProperty("required", out var required) || required.EnumerateArray().All(parameter => example.ContainsKey(parameter.GetString()!))),
                name + " product usage example fits the advertised parameter roster");
            methods.TryGetValue(name, out var method);
            var tag = parseTag.Invoke(null, new object[] { name })!;
            var layer = Get(tag, "Item1")!.ToString()!;
            var domain = Get(tag, "Item2")!.ToString()!;
            var operation = operationOf.Invoke(null, new object[] { name, description })!;
            var opName = Get(operation, "Item1")!.ToString()!;
            var opInferred = Convert.ToBoolean(Get(operation, "Item2"));
            var category = categoryOf.Invoke(null, new object[] { domain })!.ToString()!;
            var parameters = new JsonArray(properties.Select(parameter => (JsonNode?)JsonValue.Create(parameter)).ToArray());
            rows.Add((name, new JsonObject
            {
                ["name"] = name,
                ["layer"] = layer,
                ["category"] = category,
                ["domain"] = domain,
                ["operation"] = opName,
                ["operationInferred"] = opInferred,
                ["method"] = shared ? "FoundationV4Tool.InvokeAsync" : method?.Name ?? HostPortRunner.Metadata(server, name)["method"]!.GetValue<string>(),
                ["returnType"] = shared ? "CallToolResult" : method?.ReturnType.Name ?? "CallToolResult",
                ["parameters"] = parameters,
                ["description"] = description,
                ["example"] = JsonValue.Create(example.ToJsonString())
            }));
        }
        var uncategorized = rows.Where(row => row.Value["category"]!.GetValue<string>() == "uncategorized").Select(row => row.Name).ToArray();
        Check(uncategorized.Length == 0, uncategorized.Length == 0 ? "all tool domains have registered taxonomy categories" : "Uncategorized tools: " + string.Join(", ", uncategorized));

        if (failures != 0)
        {
            Console.WriteLine($"Generator gates: {checks - failures} checks passed, {failures} failed; output was not written.");
            return 1;
        }

        var sortedRows = rows.Select(row => (JsonNode)row.Value).ToArray();
        // Keep the legacy generator's `Sort-Object name` ordering: PowerShell does not expose
        // OrderedDictionary keys through PSObject.Properties, so every sort key is null and
        // its unstable Array.Sort permutation is observable in manifest/tools-list.json.
        Array.Sort(sortedRows, Comparer<JsonNode>.Create((_, _) => 0));
        var recipeCount = ((IEnumerable)usageType.GetMethod("Sequences", All)!.Invoke(null, new object[] { false })!).Cast<object>().Count();
        var curatedCount = rows.Count(row => row.Value["example"] != null);
        var output = new JsonObject
        {
            ["package"] = packageName,
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("o"),
            ["source"] = "FoundationHost V" + release + " full product catalog and declared V20-only registrations; shared Foundation implementations and engine-only tools; no worker or TIA started",
            ["fileVersion"] = FileVersionInfo.GetVersionInfo(foundationHost).FileVersion,
            ["exeSha256"] = Sha256(foundationHost),
            ["workerSha256"] = Sha256(server.Location),
            ["toolCount"] = rows.Count,
            ["note"] = "Full product roster. Lite advertises the V19 shared tools plus the engine discovery bridge. Withdrawn lifecycle tools are absent. Runtime tools/list is authoritative.",
            ["callDiscipline"] = new JsonObject
            {
                ["parameters"] = disciplineRoot.GetProperty("parameters").GetInt32() + supplementalParameters.Count,
                ["parametersUndocumented"] = undocumented,
                ["parametersFromVocabulary"] = disciplineRoot.GetProperty("parametersFromVocabulary").GetInt32() + supplementalParameters.Count(parameter => (bool)Get(parameter, "Synthesized")!),
                ["toolsWithUndocumentedParameters"] = disciplineRoot.GetProperty("toolsWithUndocumentedParameters").GetInt32() + supplementalUndocumentedTools,
                ["toolsWithEnumHints"] = disciplineRoot.GetProperty("toolsWithEnumHints").GetInt32() + supplementalEnumTools,
                ["enumHints"] = disciplineRoot.GetProperty("enumHints").GetInt32() + supplementalParameters.Count(parameter => ((IEnumerable)Get(parameter, "AllowedValues")!).Cast<object>().Any()),
                ["defaultHints"] = disciplineRoot.GetProperty("defaultHints").GetInt32() + supplementalParameters.Count(parameter => Get(parameter, "DefaultJson") != null),
                ["examplesCurated"] = curatedCount,
                ["recipes"] = recipeCount
            },
            ["categories"] = categories,
            ["tools"] = new JsonArray(sortedRows)
        };
        var serializerOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var destination = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, output.ToJsonString(serializerOptions), new UTF8Encoding(false));
        Console.WriteLine($"FoundationHost product metadata: {rows.Count} tools; {curatedCount} with a validated example; {output["callDiscipline"]!["enumHints"]} enum hints on {output["callDiscipline"]!["toolsWithEnumHints"]} tools; {undocumented} of {output["callDiscipline"]!["parameters"]} parameters without their own description ({output["callDiscipline"]!["parametersFromVocabulary"]} covered by the vocabulary; {output["callDiscipline"]!["toolsWithUndocumentedParameters"]} tools); recipes validated");
        Console.WriteLine($"Generator gates: {checks} checks passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    private static JsonDocument ProductCatalog(Assembly server, string foundationHost, string bundleRoot, string outputPath)
    {
        string release = server.GetReferencedAssemblies().First(a => a.Name!.StartsWith("Siemens.Engineering", StringComparison.Ordinal)).Version!.Major.ToString();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        // The engine host oracle uses the private cross-check route: worker and catalog in its private output.
        var worker = HostPortRunner.PrivateWorker(server);
        var catalogPath = Path.Combine(Path.GetDirectoryName(worker)!, ".guard-engine-catalog-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            // Export descriptors only. The host's --catalog path overlays the same
            // Foundation registrations as STDIO/HTTP, without launching a worker.
            server.GetType("TiaMcpServer.Cli.ToolCatalogExport", true)!.GetMethod("Write", All)!.Invoke(null, new object[] { catalogPath });
            string Quote(string value) => "\"" + Regex.Replace(Regex.Replace(value, @"(\\*)""", "$1$1\\\""), @"\\+$", "$0$0") + "\"";
            var start = new ProcessStartInfo(HostPortRunner.PrivateHost(foundationHost)) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                Arguments = "--catalog --offline --profile full --release-key " + release + " --bundle-root " + Quote(Path.GetFullPath(bundleRoot))
                    + " --engine-worker " + Quote(worker) + " --engine-catalog " + Quote(catalogPath)
            };
            start.EnvironmentVariables[HostPortRunner.EngineCrossCheckVariable] = HostPortRunner.CompatibilityRoot(HostPortRunner.RepositoryRoot());
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60000)) { process.Kill(); throw new TimeoutException("Foundation product catalog timed out."); }
            var text = stdout.GetAwaiter().GetResult();
            var error = stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0) throw new InvalidOperationException("Foundation product catalog failed: " + error);
            var product = JsonDocument.Parse(text);
            if (product.RootElement.GetProperty("releaseKey").GetString() != release
                || product.RootElement.GetProperty("profile").GetString() != "plc-foundation")
            { product.Dispose(); throw new InvalidDataException("Guard roster requires the matching full product catalog."); }
            return product;
        }
        finally { if (File.Exists(catalogPath)) File.Delete(catalogPath); }
    }

    private static void Reset() { checks = 0; failures = 0; }

    private static void AddResolver(Assembly server, string publicApiDirectory)
    {
        var runtime = Path.GetDirectoryName(server.Location)!;
        var api = string.IsNullOrWhiteSpace(publicApiDirectory) ? "" : Path.GetFullPath(publicApiDirectory);
        ResolveEventHandler resolver = (sender, args) =>
        {
            var file = new AssemblyName(args.Name).Name + ".dll";
            foreach (var directory in new[] { runtime, api })
            {
                if (string.IsNullOrEmpty(directory)) continue;
                var candidate = Path.Combine(directory, file);
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            }
            return null;
        };
        AppDomain.CurrentDomain.AssemblyResolve += resolver;
    }

    private static Type FindType(Assembly assembly, string name, bool throwOnError = true)
    {
        var found = assembly.GetType(name, false);
        if (found != null) return found;
        var logicPath = Path.Combine(Path.GetDirectoryName(assembly.Location)!, "TiaMcp.Logic.dll");
        if (File.Exists(logicPath)) found = Assembly.LoadFrom(logicPath).GetType(name, false);
        return found ?? (throwOnError ? throw new TypeLoadException("Type not found: " + name) : null)!;
    }

    private static object? Get(object target, string name)
    {
        var type = target.GetType();
        return type.GetProperty(name, All)?.GetValue(target) ?? type.GetField(name, All)?.GetValue(target);
    }

    private static string FindRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TiaPortalOpenness.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found from " + start);
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static int? ReadPreviousUndocumented(string outputPath, out bool productRoster)
    {
        productRoster = false;
        if (!File.Exists(outputPath)) return null;
        try
        {
            using var previous = JsonDocument.Parse(File.ReadAllText(outputPath));
            productRoster = previous.RootElement.GetProperty("source").GetString()?.StartsWith("FoundationHost V21 full product catalog", StringComparison.Ordinal) == true;
            return previous.RootElement.GetProperty("callDiscipline").GetProperty("parametersUndocumented").GetInt32();
        }
        catch { return null; }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
}
