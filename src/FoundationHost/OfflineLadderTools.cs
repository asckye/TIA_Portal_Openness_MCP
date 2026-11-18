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
        new OfflineLadderTool("BuildFlgNetCall", "flgNetJson"),
        new OfflineLadderTool("BuildPlcLadFcBlock", "ladFcBlockJson")
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
                ["description"] = "Call {callName,parameters:[]} or block {blockName,blockNumber,networks:[{call|rung}]}; optional inputs/outputs. " +
                    "Rung {series,outputs,fanOut?}: NO/NC, rising/falling with global memory, compare, OR, fan-out, coil/set/reset, MOVE with ENO chain. " +
                    (name == "BuildPlcLadFcBlock" ? "TON/TOF/TP and CTU/CTD/CTUD with global instances; " +
                        "user FC/FB CALL with named pins and ENO chains. " : "") +
                    "Global constants, indexed components and local inputs/outputs. Exact camelCase. 64 networks, 256 parts per rung network, " +
                    "1000 call parameters/interface members, depth 16, strings <=4096. No raw XML or caller UIds. " +
                    "No symbol/type resolution; compile is the authority." },
            ["outputReleaseKey"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("21"), ["description"] = "Exact explicit V21 candidate output; independent of host selection. No conversion." }
        };
        tool = new Tool
        {
            Name = name, Description = "[offline candidate; native unverified] In-memory V21 LAD candidate generation. " +
                (name == "BuildPlcLadFcBlock" ? "Each network contains exactly one call or rung. Rungs support contacts, edges, compares, OR, fan-out, " +
                    "coil/set/reset, MOVE with ENO chaining, TON/TOF/TP, CTU/CTD/CTUD and user FC/FB CALL with named pins. " +
                    "Global instances only; Time PT; Output/InOut pins require symbols. Known instruction names and the MC_ prefix are reserved; no block resolution. Registered on V21 only. For unsupported timers, counters and math or library/system instructions and other " +
                    "unsupported LAD, author S7DCL text and import with ImportPlcBlocksDocuments, or start from an exported donor block. "
                    : "Builds only one FC-call fragment; use BuildPlcLadFcBlock for rung networks on V21. For TON/TOF/TP, CTU/CTD/CTUD and user FC/FB calls use its rung outputs. For unsupported timers, counters and math or library/system instructions, " +
                        "and all general LAD on V20, author S7DCL text and import with ImportPlcBlocksDocuments, or start from an exported donor block. " +
                        "A bare fragment cannot be imported. ") +
                "No symbol/type resolution; compile is the authority. " + OfflineCompositionBuilders.Warning + " No file reads/writes, connection or worker calls.",
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
            var outputReleaseKey = Read("outputReleaseKey");
            if (outputReleaseKey != "21") throw new ArgumentException("Explicit release 21 required.");
            var result = OfflineLadderBuilders.Build(name, outputReleaseKey, Read(jsonParameter));
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
