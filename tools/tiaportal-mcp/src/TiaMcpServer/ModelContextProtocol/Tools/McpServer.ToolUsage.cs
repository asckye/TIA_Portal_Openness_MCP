using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaOpenness.Shared;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name = "GetToolUsage"), Description("[L0][Guide][READ] Get any available tool's exact version-specific MCP example/template, parameter schema, workflow and related official Siemens source examples. toolName selects a tool; query searches ALL embedded official source text; documentId reads its complete paginated source including setup and caveats. Empty selectors list current tool names and official sources. Reference only: never calls TIA, executes a sample or requires network/repository files. Official coverage gaps and source-version limits are explicit.")]
        public static ResponseMessage GetToolUsage(
            [Description("Exact available tool name, including non-lite tools. Empty searches/lists references.")] string toolName = "",
            [Description("Space-separated words matched against official source text; empty lists documents.")] string query = "",
            [Description("Exact document ID from a tool's officialReference.documents or search result.")] string documentId = "",
            [Description("Zero-based document list or line offset.")] int offset = 0,
            [Description("Page size 1..200, default 80. Continue with nextOffset until null.")] int limit = 80)
        {
            try
            {
                if (offset < 0 || limit < 1 || limit > 200) throw new ArgumentException("offset >= 0 and limit 1..200 are required.");
                var methods = AllToolMethods();
                JsonObject usage;
                if (toolName.Length > 0)
                {
                    if (query.Length > 0 || documentId.Length > 0) throw new ArgumentException("Use toolName alone, or query/documentId for references.");
                    if (!methods.TryGetValue(toolName, out var method)) throw new ArgumentException("Tool is not available in this release: " + toolName + ". Use FindTools or empty GetToolUsage.");
                    var name = methods.Keys.First(k => string.Equals(k, toolName, StringComparison.OrdinalIgnoreCase));
                    var tool = CreateTool(name, method).ProtocolTool;
                    var example = ToolExamples.Find(name);
                    usage = ToolUsageCatalog.Describe(name, Siemens.EngineRouter.CompiledTiaMajorVersion.ToString(), "full-engine", ToolDescription(method),
                        (JsonObject)JsonNode.Parse(tool.InputSchema.GetRawText())!, example?.ArgumentsJson, example?.Note);
                }
                else
                {
                    usage = ToolUsageCatalog.ReadReference(query, documentId, offset, limit);
                    if (query.Length == 0 && documentId.Length == 0)
                    {
                        var names = methods.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();
                        usage["tools"] = new JsonArray(names.Skip(offset).Take(limit).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
                        usage["toolCount"] = names.Length;
                        usage["nextToolOffset"] = offset + limit < names.Length ? JsonValue.Create(offset + limit) : null;
                    }
                }
                return new ResponseMessage { Message = "Tool usage and official reference data. Resolve the example for your exact target before calling.", Meta = new JsonObject { ["success"] = true, ["usage"] = usage } };
            }
            catch (ArgumentException ex)
            {
                return new ResponseMessage { Message = ex.Message, Meta = new JsonObject { ["success"] = false } };
            }
        }
    }
}
