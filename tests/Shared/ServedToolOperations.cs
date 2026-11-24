using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaOpenness.Shared;

namespace TiaMcp.Engine.Tests.Shared;

internal sealed class ServedToolOperations(string release)
{
    private readonly Dictionary<string, JsonObject> schemas = JsonNode.Parse(File.ReadAllText(Path.Combine(
        TestPaths.RepositoryRoot(AppContext.BaseDirectory), "manifest", "contracts", "v4", "baseline", release + ".json")))!
        ["tools"]!.AsArray().ToDictionary(row => (string)row!["name"]!, row => row!["inputSchema"]!.AsObject());

    public IEnumerable<(string Operation, JsonObject Usage)> For(string name)
    {
        if (!schemas.TryGetValue(name, out var schema)) yield break;
        JsonObject Describe(string operation) => ToolUsageCatalog.Describe(name, release, "plc-foundation", "", schema,
            operation: operation, schemaFor: target => schemas.GetValueOrDefault(target));
        var usage = Describe("");
        yield return ("default", usage);
        var operations = usage["operations"]!.AsArray().Select(option => (string)option!["operation"]!);
        var named = usage["namedExamples"]?.AsArray().Select(option => (string)option!) ?? [];
        foreach (string operation in operations.Concat(named).Distinct(StringComparer.Ordinal)) yield return (operation, Describe(operation));
    }
}
