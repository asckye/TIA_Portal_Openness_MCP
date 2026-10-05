using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMcp.Logic.V4
{
    // Transport-neutral MCP values; the eventual host only copies them into its SDK result.
    public sealed class McpResult
    {
        [JsonPropertyOrder(0)] public JsonElement StructuredContent { get; }
        [JsonPropertyOrder(1)] public IReadOnlyList<TextContent> Content { get; }
        [JsonPropertyOrder(2)] public bool IsError { get; }

        private McpResult(Envelope envelope)
        {
            string json = V4Json.Serialize(envelope);
            using var document = JsonDocument.Parse(json);
            StructuredContent = document.RootElement.Clone();
            Content = Array.AsReadOnly(new[] { new TextContent(json) });
            IsError = !envelope.Ok;
        }

        public static McpResult From(Envelope envelope) => new McpResult(envelope);
    }

    public sealed class TextContent
    {
        [JsonPropertyOrder(0)] public string Type => "text";
        [JsonPropertyOrder(1)] public string Text { get; }
        internal TextContent(string text) { Text = text; }
    }

    public enum CliFailure { Syntax, ContextCreation }

    public static class CliExitCode
    {
        public static int From(Envelope envelope) => envelope.Meta.Outcome switch
        {
            Outcome.Succeeded => 0,
            Outcome.RejectedBeforeOperation => 2,
            Outcome.ReadFailed => 3,
            Outcome.Failed => 3,
            Outcome.Partial => 4,
            Outcome.Unknown => 5,
            _ => throw new ArgumentOutOfRangeException(nameof(envelope))
        };

        public static int From(CliFailure failure) => failure switch
        {
            CliFailure.Syntax => 64,
            CliFailure.ContextCreation => 70,
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };
    }
}
