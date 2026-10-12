using System;
using System.Text.Json;

namespace TiaMcp.Logic.V4
{
    // The consumer view keeps unfamiliar codes and fields as evidence. It never
    // serializes a producer envelope or authorizes a write from diagnostic text.
    public sealed class ConsumerEnvelope
    {
        public JsonElement Value { get; }
        public bool Ok => Value.GetProperty("ok").GetBoolean();
        public string Outcome => Value.GetProperty("meta").GetProperty("outcome").GetString()!;
        public string? ErrorCode => Value.GetProperty("error").ValueKind == JsonValueKind.Null
            ? null : Value.GetProperty("error").GetProperty("code").GetString();

        internal ConsumerEnvelope(JsonElement value)
        {
            V4Validation.Json(value);
            V4Validation.Require(value.GetProperty("schemaVersion").GetInt32() == 4, "schemaVersion must be 4.");
            Value = value.Clone();
            V4Validation.Require(Ok == (Outcome == "succeeded") && Ok == (ErrorCode == null), "ok must agree with outcome and error.");
        }
    }
}
