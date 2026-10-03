using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.LegacyHost;

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

    internal OfflineXmlTool(string name, string jsonParameter)
    {
        this.name = name;
        this.jsonParameter = jsonParameter;
        var declaration = name == "BuildPlcUdtXml";
        tool = new Tool
        {
            Name = name,
            Description = "[offline candidate; native unverified] Generate XML in memory only; outputReleaseKey explicitly selects its format independently of the host release. " +
                (declaration ? "Flat UDT generation supports 14sp1, 15.1 and 16-21, with their target interface schema. " + PlcDeclarationXmlFormat.Warning : OfflineXmlBuilders.Warning) + " JSON supports flat UDT members or PLC tags only; no unknown fields or conflicting aliases.",
            InputSchema = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = new JsonArray(jsonParameter, "outputReleaseKey"),
                ["properties"] = new JsonObject
                {
                    [jsonParameter] = new JsonObject { ["type"] = "string", ["maxLength"] = OfflineXmlBuilders.MaxJsonCharacters },
                    ["outputReleaseKey"] = new JsonObject { ["type"] = "string", ["enum"] = declaration ? new JsonArray(PlcDeclarationXmlFormat.ReleaseKeys.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray()) : new JsonArray("21"),
                        ["description"] = "Required exact candidate output format, never inferred from the host release. Runtime XSD and native import validation are separate." }
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
            if (args.Count != 2 || args.Keys.Any(key => key != jsonParameter && key != "outputReleaseKey"))
                throw new ArgumentException("Exactly the JSON argument and outputReleaseKey are required.");
            string Read(string key) => args.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()! : throw new ArgumentException("Missing or non-string argument: " + key);
            var result = OfflineXmlBuilders.Build(name, Read("outputReleaseKey"), Read(jsonParameter));
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
