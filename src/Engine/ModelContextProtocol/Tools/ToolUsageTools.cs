using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaOpenness.Shared;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    public sealed class ToolUsageTools
    {
        [McpServerTool(Name = "GetToolUsage"), Description("[L0][Guide][READ] Unified examples for every tool and operation in this release: exact arguments, parameter sources, result fields, programming files and call sequences. Select toolName + optional operation; language lists programming examples; exampleId returns full files/steps. query/documentId searches/reads embedded official source. Empty selectors list tools, languages and examples. Reads embedded data only.")]
        public CallToolResult GetToolUsage(
            [Description("Exact available tool name, including non-lite tools. Empty searches/lists references.")] string toolName = "",
            [Description("Space-separated words matched against official source text; empty lists documents.")] string query = "",
            [Description("Exact document ID from a tool's officialReference.documents or search result.")] string documentId = "",
            [Description("Zero-based document list or line offset.")] int offset = 0,
            [Description("Page size 1..200, default 80. Continue with nextOffset until null.")] int limit = 80,
            [Description("Exact action/operation from this tool's operations list.")] string operation = "",
            [Description("Programming language/format, e.g. scl, scl-sd, lad, fbd, db, udt, s7res, stl, graph, hmi-javascript, hmi-vbscript, csharp.")] string language = "",
            [Description("Example ID from the examples list; returns complete source files or ordered calls.")] string exampleId = "",
            [Description("Example filter: all/sequence/language; default all.")] string exampleKind = "all")
        {
            try
            {
                if (exampleKind != "all" && exampleKind != "sequence" && exampleKind != "language")
                    return McpServer.V4Reject("GetToolUsage", McpServer.InvalidInput("exampleKind"));
                if (offset < 0 || limit < 1 || limit > 200) throw new ArgumentException("offset >= 0 and limit 1..200 are required.");
                var methods = McpServer.AllToolMethods();
                var release = McpServer.ReleaseKey;
                Paging? paging = null;
                if ((toolName.Length > 0 || operation.Length > 0 || language.Length > 0 || exampleId.Length > 0) && (query.Length > 0 || documentId.Length > 0))
                    throw new ArgumentException("Select a tool/language/example, or search/read official references.");
                if (operation.Length > 0 && toolName.Length == 0) throw new ArgumentException("operation requires toolName.");
                JsonObject usage;
                if (toolName.Length > 0)
                {
                    if (query.Length > 0 || documentId.Length > 0) throw new ArgumentException("Use toolName alone, or query/documentId for references.");
                    if (!methods.TryGetValue(toolName, out var method))
                        return McpServer.V4Reject("GetToolUsage", McpServer.AllToolMethods(includeUnavailable: true).ContainsKey(toolName)
                            ? new Error("Tool is not available in this release.", new UnsupportedCapabilityDetails(release, toolName, null))
                            : new Error("Tool is not registered in this release.", new ToolNotFoundDetails(toolName)));
                    var name = methods.Keys.First(k => string.Equals(k, toolName, StringComparison.OrdinalIgnoreCase));
                    var tool = McpServer.CreateTool(name, method).ProtocolTool;
                    var example = ToolExamples.Find(name);
                    usage = ToolUsageCatalog.Describe(name, release, "full-engine", McpServer.ToolDescription(method),
                        (JsonObject)JsonNode.Parse(tool.InputSchema.GetRawText())!, example?.ArgumentsJson, example?.Note, operation,
                        methods.Keys, UsageResultContract(name),
                        args => Siemens.ToolVersionPolicy.CallProblem(release, name, key => args[key]?.ToString()));
                    if (language.Length > 0 || exampleId.Length > 0 || exampleKind != "all")
                        usage["examples"] = ToolUsageCatalog.Examples(release, "full-engine", methods.Keys, language, exampleId, name, exampleKind)["examples"]!.DeepClone();
                }
                else if (language.Length > 0 || exampleId.Length > 0 || exampleKind != "all")
                    usage = ToolUsageCatalog.Examples(release, "full-engine", methods.Keys, language, exampleId, exampleKind: exampleKind);
                else
                {
                    usage = ToolUsageCatalog.ReadReference(query, documentId, offset, limit);
                    int total = (int)(usage["totalLines"] ?? usage["totalMatches"])!;
                    if (query.Length == 0 && documentId.Length == 0)
                    {
                        var names = methods.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();
                        usage["tools"] = new JsonArray(names.Skip(offset).Take(limit).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
                        usage["toolCount"] = names.Length;
                        usage["behaviorCapabilities"] = BehaviorCapabilities.Table(typeof(ToolUsageTools).Assembly, release);
                        total = Math.Max(names.Length, total);
                        usage["exampleLibrary"] = ToolUsageCatalog.Examples(release, "full-engine", methods.Keys);
                    }
                    if (offset > total) return McpServer.V4Reject("GetToolUsage", McpServer.InvalidInput("offset"));
                    paging = McpServer.OffsetPage(offset, limit, total);
                }
                if (exampleId.Length > 0 && usage["examples"] is JsonArray found && found.Count == 0)
                    return McpServer.V4Reject("GetToolUsage", new Error("Example is not available in this release.", new NotFoundDetails(exampleId)));
                usage.Remove("offset"); usage.Remove("nextOffset");
                return McpServer.V4Result("GetToolUsage", usage, paging: paging);
            }
            catch (ArgumentException) /* swallow(privacy): selector failures return a bounded V4 error without catalog implementation details */
            {
                return McpServer.V4Reject("GetToolUsage", exampleId.Length > 0 || documentId.Length > 0
                    ? new Error("Example or document was not found for these selectors.", new NotFoundDetails(exampleId.Length > 0 ? exampleId : documentId))
                    : McpServer.InvalidInput("selectors"));
            }
        }

        private static JsonObject UsageResultContract(string toolName)
        {
            if (toolName == "CallTool") return new JsonObject { ["type"] = "TargetResult",
                ["status"] = "Returns the target tool's own result unchanged. Bridge admission failures use the V4 envelope. Read GetToolUsage for the target's result contract." };
            return new JsonObject { ["type"] = "Envelope", ["schemaVersion"] = 4,
                ["status"] = "Read ok, error and meta.outcome/execution/completeness. Paging is in meta.paging; batch data.items retain each target's result." };
        }
    }
}
