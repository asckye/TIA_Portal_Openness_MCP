using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Construction;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.LegacyHost;

internal static class EngineLocalTools
{
    internal static CallToolResult Invoke(string name, JsonObject args)
    {
        if (name == "BuildPlcUdt") return new XmlBuilderTools().BuildPlcUdtXmlV4(
            V4Json.Deserialize<UdtSpec>(args["udt"]!.ToJsonString()), (string?)args["outputReleaseKey"] ?? "21");
        if (name == "ListExportHandles")
        {
            int limit = (int?)args["limit"] ?? 20;
            if (limit < 1) return Reject(name, McpServer.InvalidInput("limit"));
            var matches = ExportStore.List((string?)args["tool"], int.MaxValue);
            var entries = matches.Take(limit).ToArray();
            var (count, chars) = ExportStore.Stats();
            return Result(name, new JsonObject {
                ["items"] = new JsonArray(entries.Select(e => (JsonNode)new JsonObject { ["export"] = Describe(e) }).ToArray()),
                ["count"] = count, ["totalCharacters"] = chars, ["matchingCount"] = matches.Count,
                ["returnedCount"] = entries.Length }, completeness: entries.Length < matches.Count ? Completeness.Partial : Completeness.Complete);
        }
        string id = (string)args["exportId"]!;
        int offset = (int?)args["offset"] ?? 0, length = (int?)args["length"] ?? 0;
        if (offset < 0 || length < 0) return Reject(name, McpServer.InvalidInput("offset/length"));
        var entry = ExportStore.Get(id);
        if (entry == null) return Reject(name, new Error("Export handle is missing, expired or evicted in this engine session.", new NotFoundDetails(id)));
        if (offset > entry.Length) return Reject(name, McpServer.InvalidInput("offset"));
        if (length == 0) length = McpServer.ResolvedMaxResponseChars();
        length = length <= 0 ? ExportStore.MaxSliceChars : Math.Min(length, ExportStore.MaxSliceChars);
        var slice = ExportStore.Slice(id, offset, length);
        return Result(name, new JsonObject { ["text"] = slice.Text, ["export"] = Describe(entry) }, paging: McpServer.OffsetPage(offset, length, slice.TotalLength));
    }
    private static JsonObject Describe(ExportEntry entry)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(entry.Content);
        return new JsonObject { ["id"] = entry.Id, ["tool"] = entry.Tool, ["target"] = entry.Target,
            ["createdUtc"] = entry.CreatedUtc.ToUniversalTime().ToString("O"), ["totalLength"] = entry.Length,
            ["mediaType"] = "text/plain", ["byteLength"] = (long)bytes.Length,
            ["expiresUtc"] = entry.CreatedUtc.ToUniversalTime().AddHours(ExportStore.DefaultTtlHours).ToString("O"),
            ["sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant() };
    }
    private static CallToolResult Reject(string name, Error error) => Result(name, null, error, Outcome.RejectedBeforeOperation, Execution.NotStarted, Completeness.None);
    private static CallToolResult Result(string name, JsonObject? data, Error? error = null, Outcome outcome = Outcome.Succeeded,
        Execution execution = Execution.ReadOnly, Completeness completeness = Completeness.Complete, Paging? paging = null)
    {
        var warnings = completeness is Completeness.Partial or Completeness.Unknown ? new[] { new Warning(WarningCode.IncompleteData,
            "The retained observations do not establish complete content or post-operation state.", new Dictionary<string, JsonElement>()) } : Array.Empty<Warning>();
        var meta = new Meta(DateTimeOffset.UtcNow, "21", name, Meta.Correlate(InvocationJournal.CorrelationId), outcome, execution,
            false, BehaviorPolicy.NotApplicable, completeness, paging, warnings);
        return EngineResult.Wire(Envelope.Create(data, error, meta));
    }
}

internal static class EngineResult
{
    internal static CallToolResult Wire(Envelope envelope)
    {
        var mapped = McpResult.From(envelope);
        return new CallToolResult { IsError = mapped.IsError, StructuredContent = JsonNode.Parse(mapped.StructuredContent.GetRawText()),
            Content = new[] { new TextContentBlock { Text = mapped.Content[0].Text } } };
    }
    internal static CallToolResult Reject(string name, Error error) => Wire(BehaviorCapabilities.Disclose(Envelope.Create((JsonObject?)null, error,
        new Meta(DateTimeOffset.UtcNow, "21", name, Meta.Correlate(TiaOpenness.Shared.AuditInvocation.CurrentRequestId ?? InvocationJournal.CorrelationId),
            Outcome.RejectedBeforeOperation, Execution.NotStarted, error.Code == ErrorCode.SessionResetRequired,
            BehaviorPolicy.NotApplicable, Completeness.None, null, Array.Empty<Warning>()))));
    internal static CallToolResult Body(JsonNode body, bool? error) => new() { IsError = error, StructuredContent = body,
        Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } };
}
