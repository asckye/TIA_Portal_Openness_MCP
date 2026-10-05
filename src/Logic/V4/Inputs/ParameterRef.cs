using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMcp.Logic.V4.Inputs
{
    [JsonConverter(typeof(ParameterRefConverter))]
    public sealed class ParameterRef
    {
        public int Number { get; }
        public int? ArrayIndex { get; }
        public ParameterRef(int number, int? arrayIndex = null)
        {
            V4Validation.Require(number >= 0 && number <= 65535, "Parameter number must be 0..65535.");
            V4Validation.Require(arrayIndex == null || arrayIndex >= -1 && arrayIndex <= 32767, "Array index must be -1..32767.");
            Number = number; ArrayIndex = arrayIndex;
        }
    }

    public sealed class ParameterRefConverter : JsonConverter<ParameterRef>
    {
        public override ParameterRef Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var value = document.RootElement;
            InputGuard.Closed(value, new[] { "number" }, "arrayIndex");
            int? arrayIndex = value.TryGetProperty("arrayIndex", out var index) ? index.GetInt32() : (int?)null;
            return new ParameterRef(value.GetProperty("number").GetInt32(), arrayIndex);
        }
        public override void Write(Utf8JsonWriter writer, ParameterRef value, JsonSerializerOptions options)
        {
            writer.WriteStartObject(); writer.WriteNumber("number", value.Number);
            if (value.ArrayIndex.HasValue) writer.WriteNumber("arrayIndex", value.ArrayIndex.Value);
            writer.WriteEndObject();
        }
    }
}
