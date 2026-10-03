using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaOpenness.Shared;

namespace TiaMcp.LegacyHost;

internal sealed class UsageHintTool : McpServerTool
{
    private readonly McpServerTool inner;
    private readonly Tool protocol;
    internal UsageHintTool(McpServerTool inner)
    {
        this.inner = inner;
        var source = inner.ProtocolTool;
        protocol = new Tool { Name = source.Name, Title = source.Title,
            Description = source.Description + ToolUsageCatalog.Hint(source.Name),
            InputSchema = source.InputSchema, OutputSchema = source.OutputSchema,
            Annotations = source.Annotations, Meta = source.Meta };
    }
    public override Tool ProtocolTool => protocol;
    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        => inner.InvokeAsync(request, cancellationToken);
}

internal sealed class ToolUsageTool(string releaseKey, Func<IReadOnlyList<McpServerTool>> roster) : McpServerTool
{
    private readonly Tool tool = new() { Name = "GetToolUsage", Description = "Read this exact release's tool contracts, MCP argument templates, workflows and official Siemens source examples. toolName selects one registered tool; query searches all embedded reference text; documentId reads complete paginated source including setup. No native/worker/file/network calls. Official source-version limits and example gaps are explicit.",
        InputSchema = JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JsonObject {
                ["toolName"] = new JsonObject { ["type"] = "string", ["default"] = "", ["description"] = "Exact available tool name. Do not combine with query or documentId." },
                ["query"] = new JsonObject { ["type"] = "string", ["default"] = "", ["description"] = "Words to search in official source text." },
                ["documentId"] = new JsonObject { ["type"] = "string", ["default"] = "", ["description"] = "Exact reference ID from a tool's officialReference.documents or search." },
                ["offset"] = new JsonObject { ["type"] = "integer", ["default"] = 0, ["minimum"] = 0 },
                ["limit"] = new JsonObject { ["type"] = "integer", ["default"] = 80, ["minimum"] = 1, ["maximum"] = 200 }
            } }) };
    public override Tool ProtocolTool => tool;
    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var args = request.Params?.Arguments;
            if (args != null && args.Keys.Except(new[] { "toolName", "query", "documentId", "offset", "limit" }).Any()) throw new ArgumentException("Unknown argument.");
            string Text(string key) => args != null && args.TryGetValue(key, out var value) ? value.GetString() ?? "" : "";
            int Number(string key, int fallback) => args != null && args.TryGetValue(key, out var value) ? value.GetInt32() : fallback;
            var name = Text("toolName"); var query = Text("query"); var id = Text("documentId");
            var offset = Number("offset", 0); var limit = Number("limit", 80);
            if (offset < 0 || limit < 1 || limit > 200) throw new ArgumentException("offset >= 0 and limit 1..200 are required.");
            JsonObject usage;
            var all = roster();
            if (name.Length > 0)
            {
                if (query.Length > 0 || id.Length > 0) throw new ArgumentException("Use toolName alone, or query/documentId for references.");
                var target = all.Select(t => t.ProtocolTool).FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException("Tool is not available in this release: " + name);
                usage = ToolUsageCatalog.Describe(target.Name, releaseKey, "plc-foundation", target.Description ?? "",
                    (JsonObject)JsonNode.Parse(target.InputSchema.GetRawText())!);
            }
            else
            {
                usage = ToolUsageCatalog.ReadReference(query, id, offset, limit);
                if (query.Length == 0 && id.Length == 0)
                {
                    var names = all.Select(t => t.ProtocolTool.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                    usage["tools"] = new JsonArray(names.Skip(offset).Take(limit).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
                    usage["toolCount"] = names.Length;
                    usage["nextToolOffset"] = offset + limit < names.Length ? JsonValue.Create(offset + limit) : null;
                }
            }
            return ValueTask.FromResult(new CallToolResult { Content = new List<ContentBlock> { new TextContentBlock { Text = new JsonObject { ["success"] = true, ["usage"] = usage }.ToJsonString() } } });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            return ValueTask.FromResult(new CallToolResult { IsError = true, Content = new List<ContentBlock> { new TextContentBlock { Text = ex.Message } } });
        }
    }
}
