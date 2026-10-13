using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters;

namespace TiaMcpServer.Siemens.Services
{
    internal static class UnifiedWorkerCodecs
    {
        internal static void Configure(PlcFoundationEngine engine)
        {
            engine.ReadUnifiedScriptPair = UnifiedScriptFileCodec.ReadPair;
            engine.RequireUnifiedPlainFile = UnifiedScriptFileCodec.RequirePlainFile;
            engine.ValidateUnifiedScript = UnifiedScriptFileCodec.ValidateScript;
            engine.DecodeUnifiedDocument = UnifiedDocumentCodec.Document;
            engine.AnalyzeUnifiedScript = (code, path) => Decode(JavaScriptEvidence.Analyze(code, path))!;
        }
        private static Dictionary<string, object?> Decode(JsonObject value)
            => value.ToDictionary(p => p.Key, p => Value(p.Value), StringComparer.Ordinal);
        private static object? Value(JsonNode? value)
        {
            if (value is JsonObject map) return Decode(map);
            if (value is JsonArray list) return list.Select(Value).ToList();
            if (value == null) return null;
            using var doc = JsonDocument.Parse(value.ToJsonString());
            var element = doc.RootElement;
            return element.ValueKind == JsonValueKind.String ? element.GetString()
                : element.ValueKind == JsonValueKind.True ? (object)true
                : element.ValueKind == JsonValueKind.False ? false
                : element.TryGetInt32(out var integer) ? integer : element.GetDecimal();
        }
    }
}
