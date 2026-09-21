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

namespace TiaMcpServer.ModelContextProtocol
{
    // 2.7.58: calling discipline for every tool, done by the engine rather than left to the model.
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
                if (example != null) { try { exampleArgs = JsonNode.Parse(example.ArgumentsJson) as JsonObject; } catch (JsonException) { } }
                var result = SchemaHintsLogic.Augment(schema, SpecsOf(method), exampleArgs);
                if (!result.Changed) return tool;
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
            catch
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
                    try { payload = JsonNode.Parse(body) as JsonObject; } catch (JsonException) { payload = null; }
                }
                bool failed = result.IsError == true;
                JsonObject? meta = null;
                if (payload != null)
                {
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
                    var innerArgs = args["argumentsJson"];
                    if (innerArgs is JsonValue v && v.TryGetValue<string>(out var innerText))
                    {
                        try { innerArgs = JsonNode.Parse(innerText); } catch (JsonException) { innerArgs = null; }
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
            catch
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
