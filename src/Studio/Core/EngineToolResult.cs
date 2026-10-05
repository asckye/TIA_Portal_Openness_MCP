using System;
using System.Collections.Generic;
using System.Text.Json;
using TiaMcp.Logic.V4;

namespace TiaOpenness.Core
{
    /// <summary>Engine results only; the native Studio bridge has its own DTO contract.</summary>
    public sealed class EngineToolResult
    {
        public Envelope Envelope { get; }
        public JsonElement? Data => Envelope.Data;
        public Error Error => Envelope.Error;
        public Meta Meta => Envelope.Meta;
        public IReadOnlyList<Warning> Warnings => Meta.Warnings;
        public Paging Paging => Meta.Paging;
        public bool IsCompleteSuccess => Envelope.Ok && Meta.Outcome == Outcome.Succeeded
            && (Meta.Execution == Execution.ReadOnly || Meta.Execution == Execution.Completed)
            && Meta.Completeness == Completeness.Complete;

        public string DisplayOutcome => Meta.Outcome == Outcome.Unknown || Meta.Completeness == Completeness.Unknown ? "unknown"
            : Meta.Outcome == Outcome.Partial || Meta.Completeness == Completeness.Partial ? "partial"
            : Meta.Outcome == Outcome.Succeeded && !IsCompleteSuccess ? "read-failed"
            : WireName(Meta.Outcome);

        public string ErrorCode => Error == null ? null : WireName(Error.Code);
        public JsonElement? ErrorDetails => Error == null ? (JsonElement?)null
            : V4Json.Deserialize<JsonElement>(V4Json.Serialize(Error)).GetProperty("details").Clone();

        private EngineToolResult(Envelope envelope) { Envelope = envelope; }

        public static EngineToolResult Read(string json) => new EngineToolResult(V4Json.Deserialize<Envelope>(json));

        public static EngineToolResult ReadMcpResult(string json)
        {
            using var document = JsonDocument.Parse(json);
            var result = document.RootElement;
            EngineToolResult parsed = null;
            if (result.TryGetProperty("structuredContent", out var structured)) parsed = Read(structured.GetRawText());
            if (result.TryGetProperty("content", out var content))
            {
                if (content.ValueKind != JsonValueKind.Array || content.GetArrayLength() != 1
                    || content[0].GetProperty("type").GetString() != "text")
                    throw new JsonException("Expected one V4 text content block.");
                var text = Read(content[0].GetProperty("text").GetString());
                if (parsed != null && !JsonElement.DeepEquals(V4Json.Deserialize<JsonElement>(V4Json.Serialize(parsed.Envelope)),
                    V4Json.Deserialize<JsonElement>(V4Json.Serialize(text.Envelope))))
                    throw new JsonException("V4 structured content and text disagree.");
                parsed = parsed ?? text;
            }
            if (parsed == null) throw new JsonException("The engine result has no V4 envelope.");
            if (!result.TryGetProperty("isError", out var isError) || isError.GetBoolean() == parsed.Envelope.Ok)
                throw new JsonException("The MCP error flag disagrees with the V4 envelope.");
            return parsed;
        }

        private static string WireName<T>(T value) => V4Json.Deserialize<string>(V4Json.Serialize(value));
    }
}
