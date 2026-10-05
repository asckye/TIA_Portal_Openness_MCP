using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TiaMcp.Logic.V4.Inputs
{
    // A deliberately closed JSON Schema vocabulary. Unsupported keywords fail at
    // construction, never silently skip a target tool's constraint. No remote refs.
    public sealed class InputSchema
    {
        private static readonly string[] Keywords = { "type", "enum", "const", "properties", "required", "additionalProperties",
            "items", "minItems", "maxItems", "uniqueItems", "minProperties", "maxProperties", "propertyNames", "minLength", "maxLength",
            "pattern", "minimum", "maximum", "anyOf", "oneOf", "allOf", "not", "title", "description", "default", "examples", "$schema" };
        public JsonElement Json { get; }
        public InputSchema(JsonElement schema)
        {
            // Bound the document iteratively before any recursive schema traversal.
            new InputBudget().Check(schema);
            Verify(schema);
            Json = V4Json.Deserialize<JsonElement>(V4Json.Serialize(schema));
        }
        private static InputSchema Build(Dictionary<string, object?> schema) => new InputSchema(V4Json.Data(schema)!.Value);
        public static InputSchema String(int? maxLength = null, int minLength = 0, string[]? allowed = null, string? pattern = null)
        {
            var schema = new Dictionary<string, object?> { ["type"] = "string", ["minLength"] = minLength };
            if (maxLength.HasValue) schema["maxLength"] = maxLength.Value;
            if (allowed != null) schema["enum"] = allowed;
            if (pattern != null) schema["pattern"] = pattern;
            return Build(schema);
        }
        public static InputSchema Integer(long minimum = int.MinValue, long maximum = int.MaxValue) =>
            Build(new Dictionary<string, object?> { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum });
        public static InputSchema Number(decimal? minimum = null, decimal? maximum = null)
        {
            var schema = new Dictionary<string, object?> { ["type"] = "number" };
            if (minimum.HasValue) schema["minimum"] = minimum.Value;
            if (maximum.HasValue) schema["maximum"] = maximum.Value;
            return Build(schema);
        }
        public static InputSchema Boolean() => Build(new Dictionary<string, object?> { ["type"] = "boolean" });
        public static InputSchema Null() => Build(new Dictionary<string, object?> { ["type"] = "null" });
        public static InputSchema Scalar(bool nullable = true) => Union(nullable
            ? new[] { String(), Number(), Boolean(), Null() } : new[] { String(), Number(), Boolean() });
        public static InputSchema Union(params InputSchema[] branches) =>
            Build(new Dictionary<string, object?> { ["anyOf"] = branches.Select(b => b.Json).ToArray() });
        public static InputSchema Array(InputSchema items, int minimum = 0, int? maximum = null, bool unique = false)
        {
            var schema = new Dictionary<string, object?> { ["type"] = "array", ["items"] = items.Json, ["minItems"] = minimum };
            if (maximum.HasValue) schema["maxItems"] = maximum;
            if (unique) schema["uniqueItems"] = true;
            return Build(schema);
        }
        public static InputSchema Object(IReadOnlyDictionary<string, InputSchema> properties, string[]? required = null, int minimum = 0, int? maximum = null)
        {
            var schema = new Dictionary<string, object?> { ["type"] = "object", ["properties"] = properties.ToDictionary(p => p.Key, p => p.Value.Json, StringComparer.Ordinal),
                ["required"] = required ?? System.Array.Empty<string>(), ["additionalProperties"] = false, ["minProperties"] = minimum };
            if (maximum.HasValue) schema["maxProperties"] = maximum;
            return Build(schema);
        }
        public static InputSchema Map(InputSchema values, int minimum = 0, int? maximum = null, InputSchema? keys = null)
        {
            var schema = new Dictionary<string, object?> { ["type"] = "object", ["additionalProperties"] = values.Json, ["minProperties"] = minimum };
            if (maximum.HasValue) schema["maxProperties"] = maximum;
            if (keys != null) schema["propertyNames"] = keys.Json;
            return Build(schema);
        }

        public Error? Validate(JsonElement value, string parameter)
        {
            try { new InputBudget().Check(value); Check(value); return null; }
            catch (InputRejection ex) { return ex.ToError(parameter); }
            catch (Exception ex) when (ex is ArgumentException || ex is JsonException || ex is InvalidOperationException || ex is RegexMatchTimeoutException)
            { return InputGuard.Invalid(parameter); }
        }
        internal void Check(JsonElement value) => Check(Json, value);

        private static void Verify(JsonElement schema)
        {
            if (schema.ValueKind == JsonValueKind.True || schema.ValueKind == JsonValueKind.False) return;
            if (schema.ValueKind != JsonValueKind.Object) throw new ArgumentException("A target schema must be an object or boolean.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in schema.EnumerateObject())
            {
                if (!names.Add(field.Name) || !Keywords.Contains(field.Name, StringComparer.Ordinal))
                    throw new ArgumentException("Unsupported or duplicate target-schema keyword.");
                switch (field.Name)
                {
                    case "properties": foreach (var property in field.Value.EnumerateObject()) Verify(property.Value); break;
                    case "items": case "additionalProperties": case "propertyNames": case "not": Verify(field.Value); break;
                    case "anyOf": case "oneOf": case "allOf":
                        if (field.Value.GetArrayLength() == 0) throw new ArgumentException("Empty schema union.");
                        foreach (var branch in field.Value.EnumerateArray()) Verify(branch);
                        break;
                    case "pattern": _ = new Regex(field.Value.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); break;
                    case "type":
                        var types = field.Value.ValueKind == JsonValueKind.Array ? field.Value.EnumerateArray().ToArray() : new[] { field.Value };
                        if (types.Any(t => !new[] { "object", "array", "string", "number", "integer", "boolean", "null" }.Contains(t.GetString(), StringComparer.Ordinal)))
                            throw new ArgumentException("Unsupported schema type.");
                        break;
                }
            }
        }

        private static void Check(JsonElement schema, JsonElement value)
        {
            if (schema.ValueKind == JsonValueKind.True) return;
            InputGuard.Require(schema.ValueKind != JsonValueKind.False && value.ValueKind != JsonValueKind.Undefined);
            if (schema.TryGetProperty("type", out var type))
                InputGuard.Require(type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Any(t => IsType(value, t.GetString()!)) : IsType(value, type.GetString()!));
            if (schema.TryGetProperty("enum", out var allowed))
                InputGuard.Require(allowed.EnumerateArray().Any(a => Equal(a, value)), allowed.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).ToArray());
            if (schema.TryGetProperty("const", out var constant)) InputGuard.Require(Equal(constant, value));
            foreach (string keyword in new[] { "anyOf", "oneOf", "allOf" })
            {
                if (!schema.TryGetProperty(keyword, out var branches)) continue;
                if (keyword == "allOf") { foreach (var branch in branches.EnumerateArray()) Check(branch, value); continue; }
                int matches = 0;
                InputRejection? budgetFailure = null;
                foreach (var branch in branches.EnumerateArray())
                {
                    try { Check(branch, value); matches++; }
                    catch (InputRejection rejection) { if (rejection.IsLimit) budgetFailure = rejection; }
                }
                if (matches == 0 && budgetFailure != null) throw budgetFailure;
                InputGuard.Require(keyword == "oneOf" ? matches == 1 : matches > 0);
            }
            if (schema.TryGetProperty("not", out var excluded)) InputGuard.Require(!Matches(excluded, value));
            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()!;
                Size(schema, text.Length, "minLength", "maxLength");
                if (schema.TryGetProperty("pattern", out var pattern))
                    InputGuard.Require(Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
            }
            if (value.ValueKind == JsonValueKind.Number)
            {
                InputGuard.Require(value.TryGetDouble(out double number) && !double.IsInfinity(number) && !double.IsNaN(number));
                if (schema.TryGetProperty("minimum", out var min)) InputGuard.Require(CompareNumber(value, min) >= 0);
                if (schema.TryGetProperty("maximum", out var max)) InputGuard.Require(CompareNumber(value, max) <= 0);
            }
            if (value.ValueKind == JsonValueKind.Array)
            {
                var entries = value.EnumerateArray().ToArray();
                Size(schema, entries.Length, "minItems", "maxItems");
                if (schema.TryGetProperty("items", out var items)) foreach (var entry in entries) Check(items, entry);
                if (schema.TryGetProperty("uniqueItems", out var unique) && unique.GetBoolean())
                    for (int i = 0; i < entries.Length; i++) for (int j = 0; j < i; j++) InputGuard.Require(!Equal(entries[i], entries[j]));
            }
            if (value.ValueKind == JsonValueKind.Object)
            {
                var properties = value.EnumerateObject().ToArray();
                Size(schema, properties.Length, "minProperties", "maxProperties");
                var names = new HashSet<string>(StringComparer.Ordinal);
                schema.TryGetProperty("properties", out var declared);
                foreach (var property in properties)
                {
                    InputGuard.Require(names.Add(property.Name));
                    if (schema.TryGetProperty("propertyNames", out var keys)) Check(keys, V4Json.Deserialize<JsonElement>(V4Json.Serialize(property.Name)));
                    if (declared.ValueKind == JsonValueKind.Object && declared.TryGetProperty(property.Name, out var member)) Check(member, property.Value);
                    else if (schema.TryGetProperty("additionalProperties", out var additional)) Check(additional, property.Value);
                }
                if (schema.TryGetProperty("required", out var required))
                    foreach (var key in required.EnumerateArray()) InputGuard.Require(names.Contains(key.GetString()!));
            }
        }

        private static void Size(JsonElement schema, int actual, string min, string max)
        {
            if (schema.TryGetProperty(min, out var minimum)) InputGuard.Require(actual >= minimum.GetInt32());
            if (schema.TryGetProperty(max, out var maximum)) InputGuard.Limit(actual, maximum.GetInt32());
        }
        private static bool Matches(JsonElement schema, JsonElement value)
        {
            try { Check(schema, value); return true; }
            catch (InputRejection) /* swallow(parse-fallback): a failing branch is the expected result of a schema not predicate */
            { return false; }
        }
        private static bool IsType(JsonElement value, string type) => type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            // Declared ranges narrow integer widths (including uint SDK attributes).
            // Lexical fractions/exponents remain invalid, as in the native parsers.
            "integer" => value.ValueKind == JsonValueKind.Number && (value.TryGetInt64(out _) || value.TryGetUInt64(out _)),
            "boolean" => value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            _ => false
        };
        private static int CompareNumber(JsonElement left, JsonElement right) => left.TryGetDecimal(out var l) && right.TryGetDecimal(out var r)
            ? l.CompareTo(r) : left.GetDouble().CompareTo(right.GetDouble());
        private static bool Equal(JsonElement left, JsonElement right)
        {
            if (left.ValueKind != right.ValueKind) return false;
            if (left.ValueKind == JsonValueKind.Number) return CompareNumber(left, right) == 0;
            if (left.ValueKind == JsonValueKind.String) return left.GetString() == right.GetString();
            if (left.ValueKind == JsonValueKind.Array) return left.GetArrayLength() == right.GetArrayLength()
                && left.EnumerateArray().Zip(right.EnumerateArray(), Equal).All(x => x);
            if (left.ValueKind == JsonValueKind.Object) return left.EnumerateObject().Count() == right.EnumerateObject().Count()
                && left.EnumerateObject().All(p => right.TryGetProperty(p.Name, out var v) && Equal(p.Value, v));
            return true;
        }
    }
}
