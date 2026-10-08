using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;

namespace TiaMcp.LegacyHost;

internal sealed class EngineCatalog
{
    internal static readonly string[] Slice = { "GetSessionState", "SaveProject", "ManagePlcTagDefinition", "GetExportContent", "ListExportHandles", "BuildPlcUdt" };
    internal IReadOnlyList<Tool> Tools { get; }
    internal IReadOnlyList<string> LiteTools { get; }
    internal IReadOnlyDictionary<string, JsonObject> Descriptors { get; }
    internal string Instructions { get; }
    internal string WorkerHash { get; }

    internal EngineCatalog(string path, string worker)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if ((int?)root["formatVersion"] != 1 || (string?)root["release"] != "21")
            throw new InvalidDataException("Engine catalog format or release mismatch.");
        WorkerHash = (string)root["workerSha256"]!;
        if (WorkerHash != EngineWorkerClient.Hash(worker)) throw new InvalidDataException("Engine catalog worker SHA-256 mismatch.");
        var capabilities = JsonSerializer.SerializeToNode(BehaviorCapabilities.Table(typeof(EngineCatalog).Assembly, "21"), global::ModelContextProtocol.McpJsonUtilities.DefaultOptions);
        if (!JsonNode.DeepEquals(root["behaviorCapabilities"], capabilities))
            throw new InvalidDataException("Engine catalog behavior capabilities mismatch.");
        Tools = root["tools"]!.AsArray().Select(t => JsonSerializer.Deserialize<Tool>(t!.ToJsonString(), global::ModelContextProtocol.McpJsonUtilities.DefaultOptions)!).ToArray();
        if (Tools.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() != Tools.Count)
            throw new InvalidDataException("Duplicate engine tool names.");
        LiteTools = root["liteTools"]!.AsArray().Select(t => (string)t!).ToArray();
        Descriptors = root["descriptors"]!.AsArray().Select(d => d!.AsObject()).ToDictionary(d => (string)d["name"]!, StringComparer.Ordinal);
        foreach (var name in Slice)
            if (!Tools.Any(t => t.Name == name) || !Descriptors.ContainsKey(name)) throw new InvalidDataException("Missing slice tool: " + name);
        Instructions = (string)root["serverInstructions"]!;
    }
}
