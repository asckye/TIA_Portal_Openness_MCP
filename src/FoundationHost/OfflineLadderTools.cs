using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcp.FoundationHost;

internal static class OfflineLadderTools
{
    internal static IList<McpServerTool> Create() => new McpServerTool[]
    {
        new OfflineLadderTool("BuildFlgNetCallXml", "flgNetJson"),
        new OfflineLadderTool("ComposePlcLadFcBlockXml", "ladFcBlockJson")
    };
}

internal sealed class OfflineLadderTool : McpServerTool
{
    private readonly string name, jsonParameter;
    private readonly Tool tool;
    internal OfflineLadderTool(string name, string jsonParameter)
    {
        this.name = name; this.jsonParameter = jsonParameter;
        var properties = new JsonObject
        {
            [jsonParameter] = new JsonObject { ["type"] = "string", ["maxLength"] = OfflineCompositionBuilders.MaxJsonCharacters,
                ["description"] = "Bounded call {callName,parameters:[]} or block {blockName,blockNumber,networks:[{callJson:call}]}; optional inputs/outputs. Exact Input/Output directions; constant source must be explicit; omitted sourceKind means global; simple symbol components. 64 networks, 1000 total parameters, 1000 interface members, strings <=4096. No raw XML, paths, caller UIds or other LAD elements. See legacy-offline-ladder-candidate.md." },
            ["outputReleaseKey"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("21"), ["description"] = "Exact explicit V21 candidate output; independent of host selection. No conversion." }
        };
        tool = new Tool
        {
            Name = name, Description = "[offline candidate; native unverified] In-memory V21 LAD FC-call candidate generation with existing helpers. " + OfflineCompositionBuilders.Warning + " No file reads/writes, connection or worker calls.",
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
            string Read(string key)
            {
                if (!args.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.String) throw new ArgumentException("String argument required.");
                try { return value.GetString()!; }
                catch (InvalidOperationException) { throw new ArgumentException("Invalid Unicode argument."); }
            }
            var result = OfflineLadderBuilders.Build(name, Read("outputReleaseKey"), Read(jsonParameter));
            cancellationToken.ThrowIfCancellationRequested();
            var wire = new JsonObject();
            foreach (var pair in result) wire[McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName(pair.Key) ?? pair.Key] = pair.Value?.DeepClone();
            return ValueTask.FromResult(new CallToolResult { Content = new List<ContentBlock> { new TextContentBlock { Text = wire.ToJsonString() } } });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or JsonException or System.Xml.XmlException)
        {
            throw new McpException("Invalid offline ladder input. Check exact output release, documented fields and aliases, value types, unique names, supported port/type/source, call shape and size limits.", null, McpErrorCode.InvalidParams);
        }
        catch (Exception)
        {
            throw new McpException("Offline ladder generation failed.", null, McpErrorCode.InternalError);
        }
    }
}
