using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    // Calling discipline for every tool, done by the engine rather than left to the model.
    //
    //   1. SchemaHintedTool - the documented alternatives, defaults and example values of a parameter go into the
    //      tool's inputSchema (enum / default / examples). A client that validates against the schema stops an invalid
    //      value before the call is sent; every client shows the model exact choices instead of prose.
    //   2. AttachPreflightOnFailure - every failed call (IsError, or meta.success=false in the tool JSON) gets a compact
    //      meta.preflight block: what was wrong with the arguments, the allowed values, the prerequisite, the worked
    //      example and one next step. The corrected plan arrives with the failure; no second guess, no extra call.
    //
    // The analysis itself is the linked, offline-tested code (PreflightLogic / SchemaHintsLogic / ToolExamples /
    // McpServer.PreflightSummary); this file only wires it into the SDK objects.
    public static partial class McpServer
    {
        internal static bool IsInfrastructureV4(string name) => new[] { "CallTool", "PreviewToolCall", "FindTools",
            "ListToolCategories", "GetToolUsage", "RunReadOnlyToolBatch", "PreviewToolBatch", "ApplyToolBatch" }.Contains(name, StringComparer.Ordinal);

        internal static JsonElement ToolInputSchema(string name, MethodInfo method)
        {
            var raw = ToolCatalog.CreateTool(method).ProtocolTool.InputSchema;
            var schema = (JsonObject)JsonNode.Parse(raw.GetRawText())!;
            SchemaHintsLogic.Augment(schema, SpecsOf(method), null);
            InfrastructureSchema(name, schema);
            schema["additionalProperties"] = false;
            return JsonSerializer.SerializeToElement(InlineSchema(schema));
        }

        private static void InfrastructureSchema(string name, JsonObject schema)
        {
            if (!IsInfrastructureV4(name)) return;
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
                    if (active.Contains(pointer))
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
            var output = (JsonObject)Expand(schema, new HashSet<string>(StringComparer.Ordinal))!;
            if (recursive.Count > 0)
            {
                var definitions = new JsonObject();
                foreach (var pair in recursive.ToArray())
                    definitions[pair.Value] = Expand(Resolve(pair.Key), new HashSet<string>(StringComparer.Ordinal) { pair.Key });
                output["$defs"] = definitions;
            }
            return output;
        }
        private static readonly JsonSerializerOptions DisciplineJson = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>The tool with its schema enriched; the original when the schema gained nothing.</summary>
        internal static McpServerTool WithSchemaHints(McpServerTool tool, string name, MethodInfo method)
        {
            try
            {
                var protocol = tool.ProtocolTool;
                if (protocol.InputSchema.ValueKind != JsonValueKind.Object) return tool;
                if (!(JsonNode.Parse(protocol.InputSchema.GetRawText()) is JsonObject schema)) return tool;
                var example = ToolExamples.Find(name);
                JsonObject? exampleArgs = null;
                if (example != null) { try { exampleArgs = JsonNode.Parse(example.ArgumentsJson) as JsonObject; } catch (JsonException) /* swallow(parse-fallback): malformed example arguments omit example hints while retaining the tool schema */ { } }
                var result = SchemaHintsLogic.Augment(schema, SpecsOf(method), exampleArgs);
                InfrastructureSchema(name, schema);
                schema = InlineSchema(schema);
                if (!result.Changed && !IsInfrastructureV4(name) && schema.ToJsonString() == protocol.InputSchema.GetRawText()) return tool;
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
                var hinted = new SchemaHintedTool(tool, clone);
                return IsInfrastructureV4(name) ? new InfrastructureInputTool(hinted) : hinted;
            }
            catch /* swallow(fail-open-guard): failure to enrich schema hints must leave the original tool available */
            {
                return tool;    // a hint that cannot be built must never cost the tool itself
            }
        }

        /// <summary>Counts, for the build log: how many tools / properties carry enum hints (reflection over the roster).</summary>
        public static JsonObject SchemaHintStatistics()
        {
            int toolsWithEnums = 0, enums = 0, defaults = 0, undocumented = 0, parameters = 0, vocabulary = 0;
            var undocumentedTools = new List<string>();
            foreach (var kv in AllToolMethods())
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
                ["tools"] = AllToolMethods().Count, ["parameters"] = parameters, ["toolsWithEnumHints"] = toolsWithEnums, ["enumHints"] = enums, ["defaultHints"] = defaults,
                ["parametersUndocumented"] = undocumented, ["parametersFromVocabulary"] = vocabulary, ["toolsWithUndocumentedParameters"] = undocumentedTools.Count,
                ["undocumentedTools"] = new JsonArray(undocumentedTools.OrderBy(t => t, StringComparer.Ordinal).Select(t => (JsonNode)t).ToArray()),
            };
        }

        /// <summary>
        /// Adds meta.preflight to a failed result. Shape-conservative like the response guard: only a single text block that
        /// parses as an object is rewritten; a plain-text error gets the summary appended as a second line.
        /// </summary>
        internal static CallToolResult AttachPreflightOnFailure(string toolName, IReadOnlyDictionary<string, JsonElement>? arguments, CallToolResult result)
        {
            try
            {
                if (result?.Content == null || result.Content.Count != 1 || !(result.Content[0] is TextContentBlock text)) return result!;
                string body = text.Text ?? "";
                JsonObject? payload = null;
                if (body.TrimStart().StartsWith("{"))
                {
                    try { payload = JsonNode.Parse(body) as JsonObject; } catch (JsonException) /* swallow(parse-fallback): a non-JSON failure body remains plain text for preflight guidance */ { payload = null; }
                }
                bool failed = result.IsError == true;
                JsonObject? meta = null;
                if (payload != null)
                {
                    if (payload["schemaVersion"]?.GetValue<int?>() == 4) return result;
                    meta = (payload["meta"] ?? payload["Meta"]) as JsonObject;
                    var success = meta?["success"] as JsonValue;
                    if (success != null && success.TryGetValue<bool>(out var ok) && !ok) failed = true;
                    // CallTool: the inner tool's outcome is what counts.
                    var operation = meta?["operationSuccess"] as JsonValue;
                    if (operation != null && operation.TryGetValue<bool>(out var innerOk) && !innerOk) failed = true;
                }
                if (!failed) return result;
                if (meta != null && meta["preflight"] != null) return result;

                // Through CallTool the interesting call is the inner one.
                string target = toolName;
                var args = new JsonObject();
                if (arguments != null)
                    foreach (var kv in arguments)
                        if (!string.Equals(kv.Key, "_meta", StringComparison.OrdinalIgnoreCase))
                            args[kv.Key] = JsonNode.Parse(kv.Value.GetRawText());
                if (string.Equals(toolName, "CallTool", StringComparison.OrdinalIgnoreCase))
                {
                    var inner = args["name"] as JsonValue;
                    if (inner == null || !inner.TryGetValue<string>(out var innerName) || string.IsNullOrWhiteSpace(innerName)) return result;
                    target = innerName;
                    var innerArgs = args["arguments"];
                    if (innerArgs is JsonValue v && v.TryGetValue<string>(out var innerText))
                    {
                        try { innerArgs = JsonNode.Parse(innerText); } catch (JsonException) /* swallow(parse-fallback): malformed bridge arguments leave an empty argument set for optional preflight guidance */ { innerArgs = null; }
                    }
                    args = innerArgs as JsonObject ?? new JsonObject();
                }
                var summary = PreflightSummary(target, args);
                if (summary.Count == 0) return result;

                if (payload != null && meta != null)
                {
                    meta["preflight"] = summary;
                    string rewritten = payload.ToJsonString(DisciplineJson);
                    return new CallToolResult
                    {
                        IsError = result.IsError,
                        StructuredContent = result.StructuredContent is JsonObject structured ? Structured(structured, summary) : result.StructuredContent,
                        Content = new List<ContentBlock> { new TextContentBlock { Text = rewritten } },
                    };
                }
                return new CallToolResult
                {
                    IsError = result.IsError,
                    StructuredContent = result.StructuredContent,
                    Content = new List<ContentBlock> { new TextContentBlock { Text = body + "\npreflight: " + summary.ToJsonString(DisciplineJson) } },
                };
            }
            catch /* swallow(fail-open-guard): failure to append preflight guidance must preserve the original tool answer */
            {
                return result!;   // guidance must never replace or break the tool's own answer
            }
        }

        private static JsonNode Structured(JsonObject structured, JsonObject summary)
        {
            var copy = (JsonObject)structured.DeepClone();
            var meta = (copy["meta"] ?? copy["Meta"]) as JsonObject;
            if (meta != null) meta["preflight"] = summary.DeepClone();
            return copy;
        }
    }

    internal sealed class InfrastructureInputTool : McpServerTool
    {
        private readonly McpServerTool inner;
        internal InfrastructureInputTool(McpServerTool inner) { this.inner = inner; }
        public override Tool ProtocolTool => inner.ProtocolTool;
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            var arguments = JsonSerializer.SerializeToElement(request.Params?.Arguments ?? new Dictionary<string, JsonElement>());
            var error = new InputSchema(ProtocolTool.InputSchema).Validate(arguments, "arguments");
            return error != null ? new ValueTask<CallToolResult>(McpServer.V4Reject(ProtocolTool.Name, error)) : inner.InvokeAsync(request, cancellationToken);
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
