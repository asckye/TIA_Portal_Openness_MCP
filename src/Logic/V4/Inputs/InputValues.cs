using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMcp.Logic.V4.Inputs
{
    [JsonConverter(typeof(ScalarConverter))]
    public readonly struct Scalar
    {
        public JsonElement Json { get; }
        public JsonValueKind Kind => Json.ValueKind;
        public string? String => Kind == JsonValueKind.String ? Json.GetString() : null;
        public bool? Boolean => Kind == JsonValueKind.True || Kind == JsonValueKind.False ? Json.GetBoolean() : (bool?)null;
        public double? Number => Kind == JsonValueKind.Number ? Json.GetDouble() : (double?)null;
        public override string ToString() => Kind == JsonValueKind.Null ? "null" : Json.ToString();
        public Scalar(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object || value.ValueKind == JsonValueKind.Array)
                throw new ArgumentException("A scalar cannot contain a collection.");
            V4Validation.Json(value);
            Json = value.Clone();
        }
    }

    [JsonConverter(typeof(NativeValueConverter))]
    public readonly struct NativeValue
    {
        public JsonElement Json { get; }
        public JsonValueKind Kind => Json.ValueKind;
        public Scalar Scalar => new Scalar(Json);
        public IReadOnlyList<NativeValue> Items => Array.AsReadOnly(Json.EnumerateArray().Select(value => new NativeValue(value)).ToArray());
        public IReadOnlyDictionary<string, NativeValue> Properties => new AttributeMap<NativeValue>(
            Json.EnumerateObject().Select(p => new KeyValuePair<string, NativeValue>(p.Name, new NativeValue(p.Value))));
        public NativeValue(JsonElement value) { new InputBudget().Check(value); Json = value.Clone(); }
    }

    [JsonConverter(typeof(ToolArgumentsConverter))]
    public readonly struct ToolArguments
    {
        public JsonElement Json { get; }
        public ToolArguments(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Tool arguments must be an object.");
            new InputBudget().Check(value); Json = value.Clone();
        }
    }

    [JsonConverter(typeof(AttributeMapConverter))]
    public sealed class AttributeMap<T> : IReadOnlyDictionary<string, T>
    {
        private readonly IReadOnlyDictionary<string, T> values;
        public AttributeMap(IEnumerable<KeyValuePair<string, T>> values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            var copy = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (var pair in values) copy.Add(pair.Key, pair.Value);
            this.values = new ReadOnlyDictionary<string, T>(copy);
        }
        public T this[string key] => values[key];
        public IEnumerable<string> Keys => values.Keys;
        public IEnumerable<T> Values => values.Values;
        public int Count => values.Count;
        public bool ContainsKey(string key) => values.ContainsKey(key);
        public bool TryGetValue(string key, out T value) => values.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<string, T>> GetEnumerator() => values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [JsonConverter(typeof(PropertyStepConverter))]
    public sealed class PropertyStep
    {
        public string Property { get; }
        public string? Name { get; }
        public int? Index { get; }
        public PropertyStep(string property, string? name = null, int? index = null)
        {
            V4Validation.Text(property, nameof(property));
            V4Validation.Require(name == null || !string.IsNullOrWhiteSpace(name), "A selector name must be nonempty.");
            V4Validation.Require(index == null || index >= 0, "A selector index must be nonnegative.");
            V4Validation.Require(name == null || index == null, "Name and index selectors are mutually exclusive.");
            Property = property; Name = name; Index = index;
        }
    }

    public sealed class ToolCall
    {
        [JsonPropertyOrder(0)] public string Name { get; }
        [JsonPropertyOrder(1)] public ToolArguments Arguments { get; }
        public ToolCall(string name, ToolArguments arguments)
        {
            V4Validation.Text(name, nameof(name));
            V4Validation.Require(arguments.Json.ValueKind == JsonValueKind.Object, "Tool arguments must be present.");
            Name = name; Arguments = arguments;
        }
    }

    public sealed class WriteValue
    {
        [JsonPropertyOrder(0)] public string Name { get; }
        [JsonPropertyOrder(1)] public NativeValue Value { get; }
        public WriteValue(string name, NativeValue value)
        {
            V4Validation.Text(name, nameof(name));
            V4Validation.Json(value.Json);
            Name = name; Value = value;
        }
    }

    // Attribute converters use the options supplied by V4Json; none creates another
    // serializer configuration or applies a naming policy to SDK dictionary keys.
    public sealed class ScalarConverter : JsonConverter<Scalar>
    {
        public override bool HandleNull => true;
        public override Scalar Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        { using var document = JsonDocument.ParseValue(ref reader); return new Scalar(document.RootElement); }
        public override void Write(Utf8JsonWriter writer, Scalar value, JsonSerializerOptions options)
        { V4Validation.Json(value.Json); value.Json.WriteTo(writer); }
    }
    public sealed class NativeValueConverter : JsonConverter<NativeValue>
    {
        public override bool HandleNull => true;
        public override NativeValue Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        { using var document = JsonDocument.ParseValue(ref reader); return new NativeValue(document.RootElement); }
        public override void Write(Utf8JsonWriter writer, NativeValue value, JsonSerializerOptions options)
        { V4Validation.Json(value.Json); value.Json.WriteTo(writer); }
    }
    public sealed class ToolArgumentsConverter : JsonConverter<ToolArguments>
    {
        public override ToolArguments Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        { using var document = JsonDocument.ParseValue(ref reader); return new ToolArguments(document.RootElement); }
        public override void Write(Utf8JsonWriter writer, ToolArguments value, JsonSerializerOptions options)
        { V4Validation.Require(value.Json.ValueKind == JsonValueKind.Object, "Arguments must be an object."); value.Json.WriteTo(writer); }
    }
    public sealed class PropertyStepConverter : JsonConverter<PropertyStep>
    {
        public override PropertyStep Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var value = document.RootElement;
            InputGuard.Closed(value, new[] { "property" }, "name", "index");
            string? name = null;
            int? index = null;
            if (value.TryGetProperty("name", out var nameJson))
            { InputGuard.Require(nameJson.ValueKind == JsonValueKind.String); name = nameJson.GetString(); }
            if (value.TryGetProperty("index", out var indexJson)) index = indexJson.GetInt32();
            return new PropertyStep(value.GetProperty("property").GetString()!, name, index);
        }
        public override void Write(Utf8JsonWriter writer, PropertyStep value, JsonSerializerOptions options)
        {
            writer.WriteStartObject(); writer.WriteString("property", value.Property);
            if (value.Name != null) writer.WriteString("name", value.Name);
            if (value.Index.HasValue) writer.WriteNumber("index", value.Index.Value);
            writer.WriteEndObject();
        }
    }
    public sealed class AttributeMapConverter : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(AttributeMap<>);
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(MapConverter<>).MakeGenericType(type.GetGenericArguments()))!;
        private sealed class MapConverter<T> : JsonConverter<AttributeMap<T>>
        {
            public override AttributeMap<T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            {
                using var document = JsonDocument.ParseValue(ref reader);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("A map must be an object.");
                var entries = new Dictionary<string, T>(StringComparer.Ordinal);
                foreach (var property in document.RootElement.EnumerateObject()) entries.Add(property.Name, property.Value.Deserialize<T>(options)!);
                return new AttributeMap<T>(entries);
            }
            public override void Write(Utf8JsonWriter writer, AttributeMap<T> value, JsonSerializerOptions options)
            {
                writer.WriteStartObject();
                foreach (var pair in value) { writer.WritePropertyName(pair.Key); JsonSerializer.Serialize(writer, pair.Value, options); }
                writer.WriteEndObject();
            }
        }
    }
}
