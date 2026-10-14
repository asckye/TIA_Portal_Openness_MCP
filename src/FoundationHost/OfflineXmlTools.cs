using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.FoundationHost;

internal static class OfflineXmlTools
{
    internal static IList<McpServerTool> Create() => new McpServerTool[]
    {
        new OfflineXmlTool("BuildPlcUdtXml", "udtJson"),
        new OfflineXmlTool("BuildPlcTagTableXml", "tagTableJson")
    };
}

internal sealed class OfflineXmlTool : McpServerTool
{
    private readonly string name;
    private readonly string jsonParameter;
    private readonly Tool tool;
    private readonly string? servingRelease;

    internal OfflineXmlTool ForRelease(string release) => new(name, jsonParameter, release);

    internal OfflineXmlTool(string name, string jsonParameter, string? servingRelease = null)
    {
        this.name = name;
        this.servingRelease = servingRelease;
        if (servingRelease != null) PlcDeclarationXmlFormat.ForRelease(servingRelease);
        this.jsonParameter = jsonParameter;
        var declaration = name == "BuildPlcUdtXml";
        tool = new Tool
        {
            Name = name,
            Description = "[offline candidate; native unverified] Generate XML in memory only; the default output release is the serving release; an explicit outputReleaseKey selects an existing supported format. " +
                (declaration ? "Flat UDT generation supports 14sp1, 15.1 and 16-21, with their target interface schema. " + PlcDeclarationXmlFormat.Warning : PlcDeclarationXmlFormat.Warning) + " JSON supports flat UDT members or PLC tags only; no unknown fields or conflicting aliases.",
            InputSchema = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = servingRelease == null ? new JsonArray(jsonParameter, "outputReleaseKey") : new JsonArray(jsonParameter),
                ["properties"] = new JsonObject
                {
                    [jsonParameter] = new JsonObject { ["type"] = "string", ["maxLength"] = OfflineXmlBuilders.MaxJsonCharacters },
                    ["outputReleaseKey"] = new JsonObject { ["type"] = "string", ["enum"] = declaration ? new JsonArray(PlcDeclarationXmlFormat.ReleaseKeys.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray()) : new JsonArray("21"),
                        ["description"] = "Optional exact candidate output format; omitted uses the serving release. Explicit tag-table output remains limited to 21. Runtime XSD and native import validation are separate." }
                }
            })
        };
    }

    public override Tool ProtocolTool => tool;

    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var args = request.Params?.Arguments ?? throw new ArgumentException("Missing builder arguments.");
            if (args.Count != (args.ContainsKey("outputReleaseKey") ? 2 : servingRelease == null ? 2 : 1)
                || args.Keys.Any(key => key != jsonParameter && key != "outputReleaseKey"))
                throw new ArgumentException("Supply the builder JSON argument and an optional outputReleaseKey; no other arguments are accepted.");
            string Read(string key) => args.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()! : throw new ArgumentException("Missing or non-string argument: " + key);
            bool defaultOutput = !args.ContainsKey("outputReleaseKey") && servingRelease != null;
            var result = OfflineXmlBuilders.Build(name, defaultOutput ? servingRelease! : Read("outputReleaseKey"), Read(jsonParameter), defaultOutput);
            cancellationToken.ThrowIfCancellationRequested();
            // Match the SDK response property naming policy, preserving dictionary keys inside Data and Meta.
            var wire = new JsonObject();
            foreach (var pair in result)
                wire[McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName(pair.Key) ?? pair.Key] = pair.Value?.DeepClone();
            return ValueTask.FromResult(new CallToolResult
            {
                Content = new List<ContentBlock> { new TextContentBlock { Text = wire.ToJsonString() } }
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or JsonException or System.Xml.XmlException)
        {
            throw new McpException("Invalid offline XML builder input. Check the exact output release, documented fields and aliases, value types, unique names, address prefix, and size limits.", null, McpErrorCode.InvalidParams);
        }
        catch (Exception)
        {
            throw new McpException("Offline XML generation failed.", null, McpErrorCode.InternalError);
        }
    }
}
