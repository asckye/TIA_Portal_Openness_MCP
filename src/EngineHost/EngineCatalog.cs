using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.FoundationHost
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
            : this(JsonNode.Parse(File.ReadAllText(path))!.AsObject(), Hash(worker), release) { }

        internal EngineCatalog(JsonObject root, string workerSha256, string release)
        {
            if ((int?)root["formatVersion"] != 1 || (string?)root["release"] != release || release is not ("20" or "21"))
                throw new InvalidDataException("Engine catalog format or release mismatch.");
            if ((string?)root["workerSha256"] != workerSha256) throw new InvalidDataException("Engine catalog worker SHA-256 mismatch.");
            BehaviorCapabilities = root["behaviorCapabilities"]!.AsArray();
            var expected = JsonSerializer.SerializeToNode(BehaviorCapabilitiesTable(release), global::ModelContextProtocol.McpJsonUtilities.DefaultOptions);
            if (!JsonNode.DeepEquals(BehaviorCapabilities, expected)) throw new InvalidDataException("Engine catalog behavior capabilities mismatch.");
            var unavailable = root["unavailableTools"]?.AsArray() ?? new JsonArray();
            var tools = root["tools"]!.AsArray().Concat(unavailable.Select(t => t!["tool"]))
                .Select(t => JsonSerializer.Deserialize<Tool>(t!.ToJsonString(), global::ModelContextProtocol.McpJsonUtilities.DefaultOptions)!).ToDictionary(t => t.Name, StringComparer.Ordinal);
            var descriptors = new Dictionary<string, ToolDescriptor>(StringComparer.Ordinal);
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
            // The transitional engine export excludes migrated declarations. The
            // host reflects the shared source and merges it in the original order.
            foreach (var method in PortedToolDeclarations.Methods(release, includeUnavailable: true))
            {
                string name = method.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>()!.Name!;
                if (descriptors.ContainsKey(name)) throw new InvalidDataException("Ported declaration remained in the engine export: " + name);
                DeclaredToolMetadata.Create(name, method, _ => throw new InvalidOperationException("Metadata only."), out var descriptor);
                descriptors.Add(name, descriptor);
            }
            descriptors = descriptors.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            IncludingUnavailable = descriptors;
            All = root["tools"]!.AsArray().ToDictionary(t => (string)t!["name"]!, t => descriptors[(string)t!["name"]!], StringComparer.Ordinal);
            All = All.Concat(descriptors.Where(p => TiaMcp.Adapters.Contracts.PortedFamilies.IsMigratedDeclaration(p.Key)
                    && TiaMcp.Adapters.Contracts.PortedFamilies.Available(release, p.Key)))
                .OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            Lite = root["liteTools"]!.AsArray().Select(n => descriptors[(string)n!]).ToArray();
            Instructions = (string)root["serverInstructions"]!;
        }

        private static JsonArray BehaviorCapabilitiesTable(string release) => TiaMcp.Logic.V4.BehaviorCapabilities.Table(typeof(EngineCatalog).Assembly, release);

        // Offline swap safety net. Runtime sessions do not construct EngineCatalog.
        internal void CrossCheck(FoundationCatalog host, IReadOnlyList<ModelContextProtocol.Server.McpServerTool> shared,
            ISet<string> essentials)
        {
            var expected = new SharedToolCatalog(this, shared, essentials);
            Compare(JsonSerializer.SerializeToNode(expected.All.Keys), JsonSerializer.SerializeToNode(host.All.Keys), "tools.order");
            Compare(JsonSerializer.SerializeToNode(expected.IncludingUnavailable.Keys),
                JsonSerializer.SerializeToNode(host.IncludingUnavailable.Keys), "unavailableTools.order");
            foreach (var pair in expected.IncludingUnavailable)
                Compare(JsonSerializer.SerializeToNode(pair.Value, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions),
                    JsonSerializer.SerializeToNode(host.IncludingUnavailable[pair.Key], global::ModelContextProtocol.McpJsonUtilities.DefaultOptions), pair.Key);
            Compare(JsonSerializer.SerializeToNode(expected.Lite.Select(tool => tool.Name)),
                JsonSerializer.SerializeToNode(host.Lite.Select(tool => tool.Name)), "liteTools");
            Compare(JsonValue.Create(Instructions), JsonValue.Create(host.Instructions), "serverInstructions");
            Compare(expected.BehaviorCapabilities, host.BehaviorCapabilities, "behaviorCapabilities");
        }

        private static void Compare(JsonNode? expected, JsonNode? actual, string field)
        {
            if (JsonNode.DeepEquals(expected, actual)) return;
            if (expected is JsonObject left && actual is JsonObject right)
            {
                foreach (var key in left.Select(pair => pair.Key).Concat(right.Select(pair => pair.Key)).Distinct(StringComparer.Ordinal))
                    Compare(left[key], right[key], field + "." + key);
            }
            if (expected is JsonArray leftArray && actual is JsonArray rightArray && leftArray.Count == rightArray.Count)
            {
                for (int i = 0; i < leftArray.Count; i++) Compare(leftArray[i], rightArray[i], field + "[" + i + "]");
            }
            throw new InvalidDataException("Engine catalog cross-check mismatch: " + field);
        }

        internal static string Hash(string path)
        {
            using var input = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        }
    }
}
