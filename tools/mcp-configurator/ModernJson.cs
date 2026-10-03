#if UNIFIED_DESKTOP
#nullable disable
using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMcpConfigurator;

// Preserve the existing dictionary/array contract when hosting the configuration
// module in the modern desktop. Existing client files must survive a read/merge/write.
public sealed class JavaScriptSerializer
{
    public int MaxJsonLength { get; set; } = 32 * 1024 * 1024;
    public int RecursionLimit { get; set; } = 256;
    private JsonSerializerOptions Options => new()
    {
        MaxDepth = RecursionLimit,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new ObjectConverter() }
    };

    public string Serialize(object value) => JsonSerializer.Serialize(value, Options);
    public object DeserializeObject(string json) => Deserialize<object>(json);
    public T Deserialize<T>(string json)
    {
        if (json.Length > MaxJsonLength) throw new ArgumentException("JSON exceeds the configured limit.");
        return JsonSerializer.Deserialize<T>(json, Options);
    }

    private sealed class ObjectConverter : JsonConverter<object>
    {
        public override object Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.StartObject => JsonSerializer.Deserialize<Dictionary<string, object>>(ref reader, options),
                JsonTokenType.StartArray => JsonSerializer.Deserialize<object[]>(ref reader, options),
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => reader.TryGetInt32(out var integer) ? (object)integer :
                    reader.TryGetInt64(out var wide) ? (object)wide : reader.TryGetDecimal(out var fraction) ? (object)fraction : reader.GetDouble(),
                JsonTokenType.True => true,
                JsonTokenType.False => false,
                JsonTokenType.Null => null,
                _ => throw new JsonException("Unsupported JSON value.")
            };
        }
        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
#endif
