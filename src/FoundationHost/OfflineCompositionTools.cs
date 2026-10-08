using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.FoundationHost;

internal static class OfflineCompositionTools
{
    internal static IList<McpServerTool> Create() => new McpServerTool[]
    {
        new OfflineCompositionTool("BuildPlcGlobalDbXml", "globalDbJson", false),
        new OfflineCompositionTool("BuildStructuredTextXml", "structuredTextJson", true)
    };
}

internal sealed class OfflineCompositionTool : McpServerTool
{
    private readonly string name, jsonParameter;
    private readonly bool supportsInnerOnly;
    private readonly Tool tool;
    internal OfflineCompositionTool(string name, string jsonParameter, bool supportsInnerOnly)
    {
        this.name = name; this.jsonParameter = jsonParameter; this.supportsInnerOnly = supportsInnerOnly;
        var properties = new JsonObject
        {
            [jsonParameter] = new JsonObject { ["type"] = "string", ["maxLength"] = OfflineCompositionBuilders.MaxJsonCharacters,
                ["description"] = supportsInnerOnly
                    ? "JSON {firstUid?:1..1000000000,operations:[{op,...}]}; if/elsif/else/endif, assignment (target and exactly one source or value), token, blank, newline, global/local/symbol/literal, line items. 1..1000 rows; strings <=4096; no unknown, duplicate or conflicting fields. Candidate generation, not SCL validation."
                    : "JSON {dbName,dbNumber:positive integer,staticMembers:[{name,datatype,externalWritable?:boolean,commentZhCn?:string,startValue?:string}]}; flat members, 1..1000 rows, strings <=4096. Aliases documented in legacy-offline-composition-candidate.md." },
            ["outputReleaseKey"] = new JsonObject { ["type"] = "string", ["enum"] = supportsInnerOnly ? new JsonArray("21") : new JsonArray(PlcDeclarationXmlFormat.ReleaseKeys.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray()), ["description"] = "Exact explicit candidate output release; independent of host selection. GlobalDB supports 14sp1, 15.1 and 16-21; StructuredText remains V21 only. No XML conversion." }
        };
        if (supportsInnerOnly) properties["innerOnly"] = new JsonObject { ["type"] = "boolean", ["default"] = false, ["description"] = "Return inner XML in StructuredText/v4 namespace context instead of the enclosing StructuredText element." };
        tool = new Tool
        {
            Name = name, Description = "[offline candidate; native unverified] In-memory XML generation. " + (supportsInnerOnly ? OfflineCompositionBuilders.Warning : "Flat GlobalDB generation uses the explicitly selected release's interface schema. " + PlcDeclarationXmlFormat.Warning) + " No file reads/writes, connection or worker calls.",
            InputSchema = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray(jsonParameter, "outputReleaseKey"), ["properties"] = properties
            })
        };
    }
    public override Tool ProtocolTool => tool;
    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var args = request.Params?.Arguments ?? throw new ArgumentException("Missing arguments.");
            if (args.Keys.Any(key => key != jsonParameter && key != "outputReleaseKey" && !(supportsInnerOnly && key == "innerOnly"))) throw new ArgumentException("Unknown argument.");
            string Read(string key) => args.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ArgumentException("String argument required.");
            var innerOnly = false;
            if (args.TryGetValue("innerOnly", out var inner))
            {
                if (inner.ValueKind != JsonValueKind.True && inner.ValueKind != JsonValueKind.False) throw new ArgumentException("Boolean required.");
                innerOnly = inner.GetBoolean();
            }
            var result = OfflineCompositionBuilders.Build(name, Read("outputReleaseKey"), Read(jsonParameter), innerOnly);
            cancellationToken.ThrowIfCancellationRequested();
            var wire = new JsonObject();
            foreach (var pair in result) wire[McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName(pair.Key) ?? pair.Key] = pair.Value?.DeepClone();
            return ValueTask.FromResult(new CallToolResult { Content = new List<ContentBlock> { new TextContentBlock { Text = wire.ToJsonString() } } });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or JsonException or System.Xml.XmlException)
        {
            throw new McpException("Invalid offline composition input. Check exact output release, documented fields and aliases, value types, unique names, operation shape and size limits.", null, McpErrorCode.InvalidParams);
        }
        catch (Exception)
        {
            throw new McpException("Offline composition generation failed.", null, McpErrorCode.InternalError);
        }
    }
}
