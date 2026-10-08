using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.LegacyHost
{
    internal sealed class EngineCatalog : IToolCatalogView
    {
        internal string Instructions { get; }
        public JsonArray BehaviorCapabilities { get; }
        public IReadOnlyDictionary<string, ToolDescriptor> All { get; }
        public IReadOnlyDictionary<string, ToolDescriptor> IncludingUnavailable { get; }
        public IReadOnlyList<ToolDescriptor> Lite { get; }
        public ToolDescriptor? Find(string name, bool includeUnavailable = false)
            => (includeUnavailable ? IncludingUnavailable : All).TryGetValue(name, out var tool) ? tool : null;

        internal EngineCatalog(string path, string worker, string release)
        {
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            if ((int?)root["formatVersion"] != 1 || (string?)root["release"] != release || release is not ("20" or "21"))
                throw new InvalidDataException("Engine catalog format or release mismatch.");
            if ((string?)root["workerSha256"] != Hash(worker)) throw new InvalidDataException("Engine catalog worker SHA-256 mismatch.");
            BehaviorCapabilities = root["behaviorCapabilities"]!.AsArray();
            var expected = JsonSerializer.SerializeToNode(BehaviorCapabilitiesTable(release), global::ModelContextProtocol.McpJsonUtilities.DefaultOptions);
            if (!JsonNode.DeepEquals(BehaviorCapabilities, expected)) throw new InvalidDataException("Engine catalog behavior capabilities mismatch.");
            var unavailable = root["unavailableTools"]?.AsArray() ?? new JsonArray();
            var tools = root["tools"]!.AsArray().Concat(unavailable.Select(t => t!["tool"]))
                .Select(t => JsonSerializer.Deserialize<Tool>(t!.ToJsonString(), global::ModelContextProtocol.McpJsonUtilities.DefaultOptions)!).ToDictionary(t => t.Name, StringComparer.Ordinal);
            var descriptors = new Dictionary<string, ToolDescriptor>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in root["descriptors"]!.AsArray().Concat(unavailable.Select(t => t!["descriptor"])))
            {
                var d = node!.AsObject();
                string name = (string)d["name"]!;
                var c = d["classification"];
                var parameters = d["parameters"]!.AsArray().Select(p => new ToolParameterDescriptor(
                    (string)p!["name"]!, (string)p["friendlyType"]!, (string)p["clrType"]!, (bool)p["required"]!,
                    (string?)p["defaultJson"], (string?)p["defaultText"], (string)p["description"]!, (bool)p["synthesized"]!,
                    p["allowedValues"]!.AsArray().Select(v => (string)v!).ToArray())).ToArray();
                string execution = (string)d["execution"]!;
                if (!ToolExecution.Table.TryGetValue(name, out var owner) || owner != execution)
                    throw new InvalidDataException("Engine catalog execution ownership mismatch: " + name);
                descriptors.Add(name, new ToolDescriptor(name, (string)d["rawDescription"]!, tools[name], c == null ? null :
                    new ToolDescriptorClassification((string)c["level"]!, (string)c["domain"]!, (string)c["operation"]!,
                        (bool)c["batchRead"]!, (bool)c["batchWrite"]!), (string)d["signature"]!, parameters,
                    new ToolDryRunDescriptor((bool)d["dryRun"]!["present"]!, (bool?)d["dryRun"]!["default"] ?? true),
                    (string?)d["candidateFamily"], execution));
            }
            if (descriptors.Count != tools.Count) throw new InvalidDataException("Engine catalog descriptor coverage mismatch.");
            IncludingUnavailable = descriptors;
            All = root["tools"]!.AsArray().ToDictionary(t => (string)t!["name"]!, t => descriptors[(string)t!["name"]!], StringComparer.OrdinalIgnoreCase);
            Lite = root["liteTools"]!.AsArray().Select(n => descriptors[(string)n!]).ToArray();
            Instructions = (string)root["serverInstructions"]!;
        }

        private static JsonArray BehaviorCapabilitiesTable(string release) => TiaMcp.Logic.V4.BehaviorCapabilities.Table(typeof(EngineCatalog).Assembly, release);
        internal static string Hash(string path)
        {
            using var input = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        }
    }
}
