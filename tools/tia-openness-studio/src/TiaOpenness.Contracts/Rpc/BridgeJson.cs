using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaOpenness.Contracts.Rpc
{
    /// <summary>The Studio DTO codec, shared by the desktop and Framework bridge.</summary>
    public static class BridgeJson
    {
        private static readonly JsonSerializerOptions Options = CreateOptions();
        private static readonly JsonSerializerOptions ClientOptions = CreateOptions(clientDates: true);
        private static readonly JsonSerializerOptions IndentedOptions = CreateOptions(indented: true);

        private static JsonSerializerOptions CreateOptions(bool clientDates = false, bool indented = false)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = null,
                DictionaryKeyPolicy = null,
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
                IncludeFields = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = indented,
            };
            options.Converters.Add(new JsonStringEnumConverter());
            if (clientDates)
            {
                options.Converters.Add(new ClientDateTimeOffsetConverter());
                options.Converters.Add(new ClientStringConverter());
            }
            return options;
        }

        public static string Serialize(object value, bool indented = false)
            => JsonSerializer.Serialize(value, indented ? IndentedOptions : Options);

        public static JsonElement ToElement(object value) => JsonSerializer.SerializeToElement(value, Options);

        public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

        public static T Deserialize<T>(JsonElement json) => json.Deserialize<T>(Options);

        public static T DeserializeClient<T>(JsonElement json) => json.Deserialize<T>(ClientOptions);

        public static bool IsDateToken(JsonElement json)
        {
            if (json.ValueKind != JsonValueKind.String) return false;
            var text = json.GetString();
            return text.Length >= 19 && text[10] == 'T' && json.TryGetDateTimeOffset(out _);
        }

        public static string DiagnosticText(JsonElement? data)
        {
            if (!data.HasValue) return null;
            switch (data.Value.ValueKind)
            {
                case JsonValueKind.Null: return string.Empty;
                case JsonValueKind.String: return IsDateToken(data.Value)
                    ? data.Value.GetDateTime().ToString(CultureInfo.InvariantCulture) : data.Value.GetString();
                case JsonValueKind.True: return bool.TrueString;
                case JsonValueKind.False: return bool.FalseString;
                default: return Serialize(DiagnosticValue(data.Value), indented: true);
            }
        }

        private static object DiagnosticValue(JsonElement value)
        {
            if (IsDateToken(value)) return value.GetDateTime();
            if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Select(DiagnosticValue).ToArray();
            if (value.ValueKind == JsonValueKind.Object)
            {
                var properties = new Dictionary<string, object>();
                foreach (var property in value.EnumerateObject()) properties[property.Name] = DiagnosticValue(property.Value);
                return properties;
            }
            return value;
        }

        // An absent optional RPC property and an explicit JSON null are different:
        // success(null) must still emit result, and null error data displayed as "".
        internal sealed class PresentJsonElementConverter : JsonConverter<JsonElement?>
        {
            public override bool HandleNull => true;

            public override JsonElement? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            {
                using (var document = JsonDocument.ParseValue(ref reader)) return document.RootElement.Clone();
            }

            public override void Write(Utf8JsonWriter writer, JsonElement? value, JsonSerializerOptions options)
            {
                if (value.HasValue) value.Value.WriteTo(writer);
                else writer.WriteNullValue();
            }
        }

        // JObject.Parse used DateTime tokens before the client converted them to DTOs.
        // Preserve that conversion (including local offsets), without changing direct
        // DTO deserialization, which retains the offset supplied by the bridge.
        private sealed class ClientDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
        {
            public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
                => new DateTimeOffset(reader.GetDateTime());

            public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
                => writer.WriteStringValue(value);
        }

        private sealed class ClientStringConverter : JsonConverter<string>
        {
            public override string Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    var text = reader.GetString();
                    return text.Length >= 19 && text[10] == 'T' && reader.TryGetDateTime(out var date)
                        ? date.ToString(CultureInfo.InvariantCulture) : text;
                }
                using (var document = JsonDocument.ParseValue(ref reader))
                {
                    return document.RootElement.ToString();
                }
            }

            public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
                => writer.WriteStringValue(value);
        }
    }
}
