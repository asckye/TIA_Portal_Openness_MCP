using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMcp.Logic.V4.Inputs
{
    [JsonConverter(typeof(CompositeAttributeValueConverter))]
    public readonly struct CompositeAttributeValue
    {
        public JsonElement Json { get; }
        public CompositeAttributeValue(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                // Inspect leaves before recursive serialization, even for a JsonElement
                // parsed elsewhere with a more permissive depth option.
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in value.EnumerateObject())
                {
                    InputGuard.Require(names.Add(field.Name));
                    _ = new Scalar(field.Value);
                }
            }
            else _ = new Scalar(value);
            Json = value.Clone();
        }
    }

    [JsonConverter(typeof(CompositeAttributeMapConverter))]
    public sealed class CompositeAttributeMap : IReadOnlyDictionary<string, CompositeAttributeValue>
    {
        private readonly AttributeMap<CompositeAttributeValue> values;
        public CompositeAttributeMap(IEnumerable<KeyValuePair<string, CompositeAttributeValue>> values)
        { this.values = new AttributeMap<CompositeAttributeValue>(values); }
        public CompositeAttributeValue this[string key] => values[key];
        public IEnumerable<string> Keys => values.Keys;
        public IEnumerable<CompositeAttributeValue> Values => values.Values;
        public int Count => values.Count;
        public bool ContainsKey(string key) => values.ContainsKey(key);
        public bool TryGetValue(string key, out CompositeAttributeValue value) => values.TryGetValue(key, out value);
        public IEnumerator<KeyValuePair<string, CompositeAttributeValue>> GetEnumerator() => values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public static class CompositeAttributeMapValidator
    {
        private static readonly string[] Excluded = { "Name", "Parent", "Owner", "Project", "Portal", "Site", "Container", "Device", "AssignedHmiDevice" };
        private static Dictionary<string, InputSchema> Fields(IReadOnlyDictionary<string, AttributeRule> attributes) =>
            AttributeMapValidator.ScalarFields(attributes).Where(p => !Excluded.Contains(p.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        // Context-free shape for tool registration and shared admission: a value is a Scalar or one
        // level of named Scalar parts. The owning tool still applies ScreenItem with the host's rules.
        public static InputContract<CompositeAttributeMap> Generic() => new InputContract<CompositeAttributeMap>(
            InputSchema.Map(InputSchema.Union(InputSchema.Scalar(), InputSchema.Map(InputSchema.Scalar(), maximum: 50)), maximum: 50),
            new InputBudget(characters: 65536, depth: 2), CountLeaves);

        private static CompositeAttributeMap CountLeaves(CompositeAttributeMap values)
        {
            int leaves = 0;
            foreach (var value in values.Values)
            {
                leaves += value.Json.ValueKind == JsonValueKind.Object ? value.Json.EnumerateObject().Count() : 1;
                InputGuard.Limit(leaves, 50);
            }
            return values;
        }

        // UnifiedScreenItemLogic.cs:36-40 and UnifiedUiModelLogic.cs:117-138. The host
        // supplies admitted public readable, non-indexer parts (never collections or
        // backlinks); a get-only part can have writable leaves. Rules are snapshotted.
        public static InputContract<CompositeAttributeMap> ScreenItem(IReadOnlyDictionary<string, AttributeRule> attributes,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, AttributeRule>> parts, int minimum = 0)
        {
            var fields = Fields(attributes);
            foreach (var part in parts)
                if (!Excluded.Contains(part.Key, StringComparer.OrdinalIgnoreCase))
                    fields.Add(part.Key, InputSchema.Object(Fields(part.Value), maximum: 50));
            return new InputContract<CompositeAttributeMap>(InputSchema.Object(fields, minimum: minimum),
                new InputBudget(characters: 65536, depth: 2), CountLeaves);
        }
    }

    public sealed class CompositeAttributeValueConverter : JsonConverter<CompositeAttributeValue>
    {
        public override bool HandleNull => true;
        public override CompositeAttributeValue Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        { using var document = JsonDocument.ParseValue(ref reader); return new CompositeAttributeValue(document.RootElement); }
        public override void Write(Utf8JsonWriter writer, CompositeAttributeValue value, JsonSerializerOptions options)
        { V4Validation.Json(value.Json); value.Json.WriteTo(writer); }
    }

    public sealed class CompositeAttributeMapConverter : JsonConverter<CompositeAttributeMap>
    {
        public override CompositeAttributeMap Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            new CompositeAttributeMap(JsonSerializer.Deserialize<AttributeMap<CompositeAttributeValue>>(ref reader, options)
                ?? throw new JsonException("A composite map must be an object."));
        public override void Write(Utf8JsonWriter writer, CompositeAttributeMap value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new AttributeMap<CompositeAttributeValue>(value), options);
    }
}
