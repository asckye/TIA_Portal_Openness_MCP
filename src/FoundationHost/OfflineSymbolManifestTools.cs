using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcp.LegacyHost;

internal static class OfflineSymbolManifestTools
{
    internal static IList<McpServerTool> Create() => new McpServerTool[] { new OfflineSymbolManifestTool() };
}
internal sealed class OfflineSymbolManifestTool : McpServerTool
{
    private readonly Tool tool = new()
    {
        Name = "BuildPlcSymbolManifestFromXmlPath",
        Description = "[offline candidate; native unverified] Read an explicit bounded list of XML exports in a caller-owned immutable snapshot. Extract PLC tag and GlobalDB declarations; empty tag tables are valid, tag comments are ignored, user constants include their raw value, and system constants are skipped with a warning. No directory enumeration, file writes, TIA, worker, network, XML schema validation, import validation or program-semantic certification. Not safe for concurrently hostile filesystem mutation.",
        InputSchema = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("inputRoot", "files", "expectedOrigin"),
            ["properties"] = new JsonObject
            {
                ["inputRoot"] = new JsonObject { ["type"] = "string", ["maxLength"] = 4096, ["description"] = "Existing absolute caller-owned immutable snapshot directory; no root directory or symlink/reparse-point ancestry." },
                ["files"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = OfflineSymbolManifest.MaxFiles, ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = 512 }, ["description"] = "Exact slash-separated relative XML file paths. No traversal, globbing, case aliases or recursive discovery. <=1 MiB/file, <=8 MiB total." },
                ["expectedOrigin"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(OfflineSymbolManifest.ExpectedOrigin), ["description"] = "Required caller attestation of snapshot ownership and immutability during the read; not authentication or proof of export provenance." }
            }
        })
    };
    public override Tool ProtocolTool => tool;
    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var args = request.Params?.Arguments ?? throw new ArgumentException();
            if (args.Keys.Any(k => k != "inputRoot" && k != "files" && k != "expectedOrigin")) throw new ArgumentException();
            string Read(string name) => args.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ArgumentException();
            if (!args.TryGetValue("files", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() > OfflineSymbolManifest.MaxFiles) throw new ArgumentException();
            var paths = files.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : throw new ArgumentException()).ToArray();
            var result = OfflineSymbolManifest.Build(Read("inputRoot"), paths, Read("expectedOrigin"), cancellationToken);
            return ValueTask.FromResult(new CallToolResult { IsError = result["ok"]?.GetValue<bool>() != true, Content = new List<ContentBlock> { new TextContentBlock { Text = result.ToJsonString() } } });
        }
        catch (OperationCanceledException) { throw; }
        catch (ArgumentException) { throw new McpException("Expected inputRoot, an explicit files array and expectedOrigin. Consult the bounded snapshot contract.", null, McpErrorCode.InvalidParams); }
        catch (Exception) { throw new McpException("Offline manifest extraction failed.", null, McpErrorCode.InternalError); }
    }
}
