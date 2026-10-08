using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcpServer.ModelContextProtocol
{
    // Shared V4 schema and admission rules for direct, bridge and batch calls.
    public static partial class McpServer
    {
        private static readonly HashSet<string> V4ToolNames = new HashSet<string>(
            TiaOpenness.Shared.ToolUsageCatalog.ProfileEntries(ReleaseKey)
                .Where(row => (int?)row!["envelopeVersion"] == 4).Select(row => (string)row!["currentName"]!), StringComparer.Ordinal);
        internal static bool IsInfrastructureV4(string name) => V4ToolNames.Contains(name);
        internal static void AssertV4Tool(string name)
        {
            if (!IsInfrastructureV4(name)) throw new InvalidOperationException("Registered tool has no generated V4 contract: " + name);
        }

        private static void InfrastructureSchema(string name, JsonObject schema)
        {
            RemoveV4NullDefaults(schema);
            schema["additionalProperties"] = false;
            var properties = schema["properties"]!.AsObject();
            if (name == "CallTool" || name == "PreviewToolCall")
                properties["arguments"] = new JsonObject { ["type"] = "object", ["description"] = "Arguments validated against the named tool's inputSchema." };
            if (name == "RunReadOnlyToolBatch" || name == "PreviewToolBatch")
                properties["operations"] = JsonNode.Parse("{\"type\":\"array\",\"minItems\":1,\"maxItems\":50,\"items\":{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"name\",\"arguments\"],\"properties\":{\"name\":{\"type\":\"string\"},\"arguments\":{\"type\":\"object\"}}}}");
            if (properties["offset"] is JsonObject offset) offset["minimum"] = 0;
            if (properties["limit"] is JsonObject limit)
            {
                limit["minimum"] = 1;
                if (name == "GetToolUsage") limit["maximum"] = 200;
            }
            if (name == "GetToolUsage") properties["exampleKind"]!["enum"] = new JsonArray("all", "sequence", "language");
        }

        internal static void RemoveV4NullDefaults(JsonObject schema)
        {
            void Visit(JsonNode? node)
            {
                if (!(node is JsonObject value)) return;
                if (value.TryGetPropertyValue("default", out var fallback) && fallback == null) value.Remove("default");
                foreach (string key in new[] { "properties", "$defs", "patternProperties", "dependentSchemas" })
                    if (value[key] is JsonObject map) foreach (var child in map) Visit(child.Value);
                foreach (string key in new[] { "items", "additionalProperties", "propertyNames", "not", "if", "then", "else", "contains" })
                    Visit(value[key]);
                foreach (string key in new[] { "anyOf", "oneOf", "allOf", "prefixItems" })
                    if (value[key] is JsonArray branches) foreach (var branch in branches) Visit(branch);
            }
            Visit(schema);
        }

        // Inline shared definitions. Only a back edge retains a root-local definition.
        internal static JsonObject InlineSchema(JsonObject schema)
        {
            var recursive = new Dictionary<string, string>(StringComparer.Ordinal);
            JsonNode Resolve(string pointer)
            {
                if (!pointer.StartsWith("#/", StringComparison.Ordinal)) throw new ArgumentException("Only local schema references are supported.");
                JsonNode? node = schema;
                foreach (var part in pointer.Substring(2).Split('/')) node = node?[part.Replace("~1", "/").Replace("~0", "~")];
                return node ?? throw new ArgumentException("Missing schema reference.");
            }
            JsonNode? Expand(JsonNode? node, HashSet<string> active)
            {
                if (node is JsonArray array) return new JsonArray(array.Select(n => Expand(n, active)).ToArray());
                if (!(node is JsonObject obj)) return node?.DeepClone();
                var result = new JsonObject();
                if (obj["$ref"] is JsonValue reference)
                {
                    string pointer = reference.GetValue<string>();
                    if (Reaches(Resolve(pointer), pointer, new HashSet<string>(StringComparer.Ordinal)))
                    {
                        if (!recursive.ContainsKey(pointer)) recursive.Add(pointer, "recursive" + recursive.Count);
                        result["$ref"] = "#/$defs/" + recursive[pointer];
                    }
                    else
                    {
                        var next = new HashSet<string>(active, StringComparer.Ordinal) { pointer };
                        result = (JsonObject)Expand(Resolve(pointer), next)!;
                    }
                }
                foreach (var pair in obj)
                    if (pair.Key != "$ref" && pair.Key != "$defs") result[pair.Key] = Expand(pair.Value, active);
                return result;
            }
            bool Reaches(JsonNode? node, string target, HashSet<string> visited)
            {
                if (node is JsonArray items) return items.Any(item => Reaches(item, target, visited));
                if (!(node is JsonObject value)) return false;
                if (value["$ref"] is JsonValue reference)
                {
                    string pointer = reference.GetValue<string>();
                    if (pointer == target) return true;
                    if (visited.Add(pointer) && Reaches(Resolve(pointer), target, visited)) return true;
                }
                return value.Any(pair => pair.Key != "$defs" && pair.Key != "$ref" && Reaches(pair.Value, target, visited));
            }
            var output = (JsonObject)Expand(schema, new HashSet<string>(StringComparer.Ordinal))!;
            if (recursive.Count > 0)
            {
                var definitions = new JsonObject();
                for (int i = 0; i < recursive.Count; i++)
                {
                    var pair = recursive.ElementAt(i);
                    definitions[pair.Value] = Expand(Resolve(pair.Key), new HashSet<string>(StringComparer.Ordinal) { pair.Key });
                }
                output["$defs"] = definitions;
            }
            return output;
        }
        private static readonly JsonSerializerOptions DisciplineJson = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>Apply documented hints and the shared V4 admission boundary; schema failures stop registration.</summary>
        internal static McpServerTool WithSchemaHints(McpServerTool tool, string name, IReadOnlyList<PreflightLogic.ParameterSpec> specs, Action<JsonObject, JsonElement> preserveTypedSchemas)
        {
            var protocol = tool.ProtocolTool;
            var schema = JsonNode.Parse(protocol.InputSchema.GetRawText())!.AsObject();
            var example = ToolExamples.Find(name);
            var exampleArgs = TiaMcp.Logic.V4.BehaviorCapabilities.CandidateExample(ReleaseKey, name)
                ?? (example == null ? null : JsonNode.Parse(example.ArgumentsJson)!.AsObject());
            SchemaHintsLogic.Augment(schema, specs, exampleArgs);
            preserveTypedSchemas(schema, protocol.InputSchema);
            InfrastructureSchema(name, schema);
            schema = InlineSchema(schema);
            using var doc = JsonDocument.Parse(schema.ToJsonString(DisciplineJson));
            var clone = new Tool
            {
                Name = protocol.Name,
                Title = protocol.Title,
                Description = protocol.Description,
                InputSchema = doc.RootElement.Clone(),
                OutputSchema = protocol.OutputSchema,
                Annotations = protocol.Annotations,
                Meta = protocol.Meta,
            };
            return new SchemaHintedTool(tool, clone);
        }

        static partial void ValidateV4Admission(RequestContext<CallToolRequestParams> request, ref CallToolResult? result)
        {
            string name = request.Params?.Name ?? "";
            if (!AllToolDescriptors(true).TryGetValue(name, out var method)) return;
            // The same order as CallTool and batches: typed families, the argument budget,
            // then BindV4Call (duplicates, release, typed contract, binding).
            var arguments = JsonSerializer.SerializeToElement(request.Params?.Arguments ?? new Dictionary<string, JsonElement>());
            var error = ToolInvoker.ValidateArguments(method, arguments, method.Tool.InputSchema, typedFamiliesOnly: true);
            if (error == null)
            {
                try { error = ToolInvoker.Bind(name, new ToolArguments(arguments), out _); }
                catch (InputRejection rejection) { error = rejection.ToError("arguments"); }
            }
            if (error != null) result = V4TargetReject(name, error, current: CurrentBehaviorTargets(name, arguments));
        }

        /// <summary>Counts, for the build log: how many tools / properties carry enum hints (reflection over the roster).</summary>
        public static JsonObject SchemaHintStatistics()
        {
            int toolsWithEnums = 0, enums = 0, defaults = 0, undocumented = 0, parameters = 0, vocabulary = 0;
            var undocumentedTools = new List<string>();
            foreach (var kv in AllToolDescriptors())
            {
                var specs = SpecsOf(kv.Value);
                var schema = new JsonObject { ["properties"] = new JsonObject(specs.Select(s => new KeyValuePair<string, JsonNode?>(s.Name, new JsonObject()))) };
                var r = SchemaHintsLogic.Augment(schema, specs, null);
                if (r.Enums > 0) toolsWithEnums++;
                enums += r.Enums; defaults += r.Defaults;
                parameters += specs.Count;
                // Source-level gap (the number the gate tracks): no [Description], whether or not the vocabulary covers the name.
                int missing = specs.Count(s => s.Synthesized || s.Description.Length == 0);
                vocabulary += specs.Count(s => s.Synthesized);
                undocumented += missing;
                if (missing > 0) undocumentedTools.Add(kv.Key);
            }
            return new JsonObject
            {
                ["tools"] = AllToolDescriptors().Count, ["parameters"] = parameters, ["toolsWithEnumHints"] = toolsWithEnums, ["enumHints"] = enums, ["defaultHints"] = defaults,
                ["parametersUndocumented"] = undocumented, ["parametersFromVocabulary"] = vocabulary, ["toolsWithUndocumentedParameters"] = undocumentedTools.Count,
                ["undocumentedTools"] = new JsonArray(undocumentedTools.OrderBy(t => t, StringComparer.Ordinal).Select(t => (JsonNode)t).ToArray()),
            };
        }

    }

    internal sealed class InfrastructureInputTool : McpServerTool
    {
        private readonly McpServerTool inner;
        private readonly ToolDescriptor method;
        private readonly Func<JsonElement, JsonElement, Error?>? validate;
        internal InfrastructureInputTool(McpServerTool inner, ToolDescriptor method, Func<JsonElement, JsonElement, Error?>? validate = null)
        { this.inner = inner; this.method = method; this.validate = validate; }
        public override Tool ProtocolTool => inner.ProtocolTool;
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            var arguments = JsonSerializer.SerializeToElement(request.Params?.Arguments ?? new Dictionary<string, JsonElement>());
            var error = validate != null ? validate(arguments, ProtocolTool.InputSchema)
                : McpServer.ToolInvoker.ValidateArguments(method, arguments, ProtocolTool.InputSchema);
            return error != null ? new ValueTask<CallToolResult>(McpServer.V4TargetReject(ProtocolTool.Name, error, McpServer.CurrentBehaviorTargets(ProtocolTool.Name, arguments))) : inner.InvokeAsync(request, cancellationToken);
        }
    }

    internal sealed class SchemaHintedTool : McpServerTool
    {
        private readonly McpServerTool _inner;
        private readonly Tool _tool;
        public SchemaHintedTool(McpServerTool inner, Tool tool) { _inner = inner; _tool = tool; }
        public override Tool ProtocolTool => _tool;
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
            => _inner.InvokeAsync(request, cancellationToken);
    }
}
