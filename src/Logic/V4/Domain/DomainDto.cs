using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.Logic.V4.Domain
{
    // Frozen storage retains omitted fields, explicit scalar nulls and dictionary spelling.
    // Constructors are internal; integrations use DomainValidation.Contract<T>.
    internal abstract class DomainDto
    {
        internal JsonElement Json { get; }
        protected static readonly Scalar NullScalar = new Scalar(V4Json.ParseInput("null"));
        internal DomainDto(JsonElement json) { DomainValidation.Budget(GetType()).Check(json); Json = json.Clone(); }
        protected T Required<T>(string name) => V4Json.Deserialize<T>(Json.GetProperty(name).GetRawText());
        protected T Optional<T>(string name, T fallback) => Json.TryGetProperty(name, out var value)
            && value.ValueKind != JsonValueKind.Null ? V4Json.Deserialize<T>(value.GetRawText()) : fallback;
        protected IReadOnlyList<T> Items<T>(string name) => Array.AsReadOnly(Required<T[]>(name));
        protected AttributeMap<T> Map<T>(string name) => Required<AttributeMap<T>>(name);
    }

    internal sealed class DomainDtoConverter : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => typeof(DomainDto).IsAssignableFrom(type);
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(type))!;

        private sealed class Converter<T> : JsonConverter<T> where T : DomainDto
        {
            public override bool HandleNull => true;
            public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            {
                using var document = JsonDocument.ParseValue(ref reader);
                var value = document.RootElement;
                if (type == typeof(SivarcReference) && value.ValueKind == JsonValueKind.Null) return null!;
                DomainValidation.Check(type, value);
                var concrete = DomainSchemas.Concrete(type, value);
                return (T)Activator.CreateInstance(concrete, BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new object[] { value }, null)!;
            }
            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            {
                if (value == null && typeof(T) == typeof(SivarcReference)) { writer.WriteNullValue(); return; }
                if (value == null) throw new JsonException("Domain DTOs cannot be null.");
                value.Json.WriteTo(writer);
            }
        }
    }
}
