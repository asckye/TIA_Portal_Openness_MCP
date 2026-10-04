using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaOpenness.Shared;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    public sealed class ToolUsageTools
    {
        [McpServerTool(Name = "GetToolUsage"), Description("[L0][Guide][READ] Unified examples for every tool and operation in this release: exact arguments, parameter sources, result fields, programming files and call sequences. Select toolName + optional operation; language lists programming examples; exampleId returns full files/steps. query/documentId searches/reads embedded official source. Empty selectors list tools, languages and examples. Reads embedded data only.")]
        public ResponseMessage GetToolUsage(
            [Description("Exact available tool name, including non-lite tools. Empty searches/lists references.")] string toolName = "",
            [Description("Space-separated words matched against official source text; empty lists documents.")] string query = "",
            [Description("Exact document ID from a tool's officialReference.documents or search result.")] string documentId = "",
            [Description("Zero-based document list or line offset.")] int offset = 0,
            [Description("Page size 1..200, default 80. Continue with nextOffset until null.")] int limit = 80,
            [Description("Exact action/operation from this tool's operations list.")] string operation = "",
            [Description("Programming language/format, e.g. scl, scl-sd, lad, fbd, db, udt, s7res, stl, graph, hmi-javascript, hmi-vbscript, csharp.")] string language = "",
            [Description("Example ID from the examples list; returns complete source files or ordered calls.")] string exampleId = "")
        {
            try
            {
                if (offset < 0 || limit < 1 || limit > 200) throw new ArgumentException("offset >= 0 and limit 1..200 are required.");
                var methods = McpServer.AllToolMethods();
                var release = Siemens.EngineRouter.CompiledTiaMajorVersion.ToString();
                if ((toolName.Length > 0 || operation.Length > 0 || language.Length > 0 || exampleId.Length > 0) && (query.Length > 0 || documentId.Length > 0))
                    throw new ArgumentException("Select a tool/language/example, or search/read official references.");
                if (operation.Length > 0 && toolName.Length == 0) throw new ArgumentException("operation requires toolName.");
                JsonObject usage;
                if (toolName.Length > 0)
                {
                    if (query.Length > 0 || documentId.Length > 0) throw new ArgumentException("Use toolName alone, or query/documentId for references.");
                    if (!methods.TryGetValue(toolName, out var method)) throw new ArgumentException("Tool is not available in this release: " + toolName + ". Use FindTools or empty GetToolUsage.");
                    var name = methods.Keys.First(k => string.Equals(k, toolName, StringComparison.OrdinalIgnoreCase));
                    var tool = McpServer.CreateTool(name, method).ProtocolTool;
                    var example = ToolExamples.Find(name);
                    usage = ToolUsageCatalog.Describe(name, release, "full-engine", McpServer.ToolDescription(method),
                        (JsonObject)JsonNode.Parse(tool.InputSchema.GetRawText())!, example?.ArgumentsJson, example?.Note, operation,
                        methods.Keys, UsageResultContract(method.ReturnType),
                        args => Siemens.ToolVersionPolicy.CallProblem(release, name, key => args[key]?.ToString()));
                    if (language.Length > 0 || exampleId.Length > 0)
                        usage["examples"] = ToolUsageCatalog.Examples(release, "full-engine", methods.Keys, language, exampleId, name)["examples"]!.DeepClone();
                }
                else if (language.Length > 0 || exampleId.Length > 0)
                    usage = ToolUsageCatalog.Examples(release, "full-engine", methods.Keys, language, exampleId);
                else
                {
                    usage = ToolUsageCatalog.ReadReference(query, documentId, offset, limit);
                    if (query.Length == 0 && documentId.Length == 0)
                    {
                        var names = methods.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();
                        usage["tools"] = new JsonArray(names.Skip(offset).Take(limit).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
                        usage["toolCount"] = names.Length;
                        usage["nextToolOffset"] = offset + limit < names.Length ? JsonValue.Create(offset + limit) : null;
                        usage["exampleLibrary"] = ToolUsageCatalog.Examples(release, "full-engine", methods.Keys);
                    }
                }
                return new ResponseMessage { Message = "Tool and programming examples for the selected release.", Meta = new JsonObject { ["success"] = true, ["usage"] = usage } };
            }
            catch (ArgumentException ex)
            {
                return new ResponseMessage { Message = ex.Message, Meta = new JsonObject { ["success"] = false } };
            }
        }

        private static JsonObject UsageResultContract(Type type, int depth = 0)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(System.Threading.Tasks.Task<>)) type = type.GetGenericArguments()[0];
            var fields = new JsonObject();
            if (depth < 2 && type.Namespace == typeof(ResponseMessage).Namespace)
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    var field = new JsonObject { ["type"] = property.PropertyType.Name };
                    if (typeof(ResponseMessage).IsAssignableFrom(property.PropertyType)) field["fields"] = UsageResultContract(property.PropertyType, depth + 1)["fields"]!.DeepClone();
                    fields[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = field;
                }
            return new JsonObject { ["type"] = type.Name, ["fields"] = fields,
                ["status"] = "Read meta.success/operationSuccess when present and the operation-specific result fields. Call completion alone is not engineering success." };
        }
    }
}
