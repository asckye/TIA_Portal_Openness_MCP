using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcp.LegacyHost;

internal static class OfflineBlockCompositionTools
{
    internal static IList<McpServerTool> Create() => new McpServerTool[]
    {
        new OfflineBlockCompositionTool("ComposePlcFcBlockXml", "fcBlockJson"),
        new OfflineBlockCompositionTool("ComposePlcFbBlockXml", "fbBlockJson")
    };
}

internal sealed class OfflineBlockCompositionTool : McpServerTool
{
    private readonly string name, jsonParameter;
    private readonly Tool tool;
    internal OfflineBlockCompositionTool(string name, string jsonParameter)
    {
        this.name = name; this.jsonParameter = jsonParameter;
        var properties = new JsonObject
        {
            [jsonParameter] = new JsonObject { ["type"] = "string", ["maxLength"] = OfflineCompositionBuilders.MaxJsonCharacters,
                ["description"] = "JSON {blockName,blockNumber,inputs:[],outputs:[],structuredText:{operations:[...]}}; FB also allows optional inouts/statics/temps. FC requires inputs/outputs arrays (empty allowed); FB interface arrays optional. Flat members {name,datatype,commentZhCn?}; 1000 members total; strings <=4096. Raw XML rejected. Exact aliases and comments documented in legacy-offline-block-composition-candidate.md." },
            ["outputReleaseKey"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("21"), ["description"] = "Exact explicit V21 candidate output; independent of host selection. No conversion." }
        };
        tool = new Tool
        {
            Name = name, Description = "[offline candidate; native unverified] In-memory V21 SCL block composition with existing helpers. " + OfflineCompositionBuilders.Warning + " No file reads/writes, connection or worker calls.",
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
            if (args.Keys.Any(key => key != jsonParameter && key != "outputReleaseKey")) throw new ArgumentException("Unknown argument.");
            string Read(string key) => args.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ArgumentException("String argument required.");
            var result = OfflineBlockCompositionBuilders.Build(name, Read("outputReleaseKey"), Read(jsonParameter));
            cancellationToken.ThrowIfCancellationRequested();
            var wire = new JsonObject();
            foreach (var pair in result) wire[McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName(pair.Key) ?? pair.Key] = pair.Value?.DeepClone();
            return ValueTask.FromResult(new CallToolResult { Content = new List<ContentBlock> { new TextContentBlock { Text = wire.ToJsonString() } } });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or JsonException or System.Xml.XmlException)
        {
            throw new McpException("Invalid offline block composition input. Check exact output release, documented fields and aliases, value types, unique names, operation shape and size limits.", null, McpErrorCode.InvalidParams);
        }
        catch (Exception)
        {
            throw new McpException("Offline block composition generation failed.", null, McpErrorCode.InternalError);
        }
    }
}
