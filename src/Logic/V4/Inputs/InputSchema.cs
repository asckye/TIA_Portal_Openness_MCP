using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TiaMcp.Logic.V4.Inputs
{
    // A deliberately closed JSON Schema vocabulary. Unsupported keywords fail at
    // construction, never silently skip a target tool's constraint. References are
    // restricted to this document's root definitions; nothing is fetched remotely.
    public sealed class InputSchema
    {
        private static readonly string[] Keywords = { "type", "enum", "const", "properties", "required", "additionalProperties",
            "items", "minItems", "maxItems", "uniqueItems", "minProperties", "maxProperties", "propertyNames", "minLength", "maxLength",
            "pattern", "minimum", "maximum", "anyOf", "oneOf", "allOf", "not", "if", "then", "else",
            "title", "description", "default", "examples", "$schema", "$comment", "$defs", "$ref", "x-maxUtf8Bytes", "x-maxDepth" };
        public JsonElement Json { get; }
        public InputSchema(JsonElement schema)
        {
            // Bound the document iteratively before any recursive schema traversal.
            new InputBudget().Check(schema);
            Verify(schema, schema);
            if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("$defs", out var definitions))
            {
                var depths = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var definition in definitions.EnumerateObject())
                    ReferenceDepth(definition.Name, schema, new HashSet<string>(StringComparer.Ordinal), depths);
            }
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

        public IReadOnlyList<Error> ValidateFields(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) return new[] { InputGuard.Invalid("arguments") };
            var errors = new List<Error>();
            Json.TryGetProperty("properties", out var declared);
            if (Json.TryGetProperty("required", out var required))
                foreach (var key in required.EnumerateArray())
                    if (!value.TryGetProperty(key.GetString()!, out _)) errors.Add(InputGuard.Invalid(key.GetString()!));
            foreach (var property in value.EnumerateObject())
            {
                try
                {
                    new InputBudget().Check(property.Value);
                    if (declared.ValueKind == JsonValueKind.Object && declared.TryGetProperty(property.Name, out var member)) Check(member, property.Value, Json);
                    else if (Json.TryGetProperty("additionalProperties", out var additional)) Check(additional, property.Value, Json);
                }
                catch (InputRejection rejection) { errors.Add(rejection.ToError(property.Name)); }
            }
            return errors;
        }

        public Error? Validate(JsonElement value, string parameter)
        {
            try { new InputBudget().Check(value); Check(value); return null; }
            catch (InputRejection ex) { return ex.ToError(parameter); }
            catch (Exception ex) when (ex is ArgumentException || ex is JsonException || ex is InvalidOperationException || ex is RegexMatchTimeoutException)
            { return InputGuard.Invalid(parameter); }
        }
        internal void Check(JsonElement value) => Check(Json, value, Json);

        private static void Verify(JsonElement schema, JsonElement root)
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
                    case "properties": case "$defs":
                        foreach (var property in field.Value.EnumerateObject()) Verify(property.Value, root);
                        break;
                    case "$ref": _ = Definition(root, ReferenceName(field.Value)); break;
                    case "x-maxUtf8Bytes": case "x-maxDepth":
                        if (!field.Value.TryGetInt32(out int limit) || limit < (field.Name == "x-maxDepth" ? 1 : 0)
                            || field.Name == "x-maxDepth" && limit > V4Json.MaximumInputDepth)
                            throw new ArgumentException("Invalid schema budget.");
                        break;
                    case "items": case "additionalProperties": case "propertyNames": case "not":
                    case "if": case "then": case "else": Verify(field.Value, root); break;
                    case "anyOf": case "oneOf": case "allOf":
                        if (field.Value.GetArrayLength() == 0) throw new ArgumentException("Empty schema union.");
                        foreach (var branch in field.Value.EnumerateArray()) Verify(branch, root);
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

        private static string ReferenceName(JsonElement reference)
        {
            const string prefix = "#/$defs/";
            if (reference.ValueKind != JsonValueKind.String || !reference.GetString()!.StartsWith(prefix, StringComparison.Ordinal))
                throw new ArgumentException("Only root definition references are supported.");
            string name = reference.GetString()!.Substring(prefix.Length);
            if (name.Contains('/') || name.Contains('%')
                || Regex.IsMatch(name, "~(?![01])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                throw new ArgumentException("A reference must name one root definition.");
            return name.Replace("~1", "/").Replace("~0", "~");
        }

        private static JsonElement Definition(JsonElement root, string name)
        {
            if (!root.TryGetProperty("$defs", out var definitions) || definitions.ValueKind != JsonValueKind.Object
                || !definitions.TryGetProperty(name, out var definition))
                throw new ArgumentException("Missing local schema definition.");
            return definition;
        }

        // Recursive item/property definitions consume input depth. Alias/combinator
        // cycles do not, so reject those (and unbounded alias chains) at construction.
        private static int ReferenceDepth(string name, JsonElement root, HashSet<string> active, Dictionary<string, int> depths)
        {
            if (depths.TryGetValue(name, out int known)) return known;
            if (!active.Add(name) || active.Count > V4Json.MaximumInputDepth)
                throw new ArgumentException("Non-consuming or excessive schema reference chain.");
            int depth = 1;
            foreach (string next in SameValueReferences(Definition(root, name)))
                depth = Math.Max(depth, 1 + ReferenceDepth(next, root, active, depths));
            active.Remove(name);
            if (depth > V4Json.MaximumInputDepth) throw new ArgumentException("Excessive schema reference chain.");
            depths.Add(name, depth);
            return depth;
        }

        private static IEnumerable<string> SameValueReferences(JsonElement schema)
        {
            if (schema.ValueKind != JsonValueKind.Object) yield break;
            if (schema.TryGetProperty("$ref", out var reference)) yield return ReferenceName(reference);
            foreach (string key in new[] { "not", "if", "then", "else", "anyOf", "oneOf", "allOf" })
            {
                if (!schema.TryGetProperty(key, out var child)) continue;
                var branches = child.ValueKind == JsonValueKind.Array ? child.EnumerateArray().ToArray() : new[] { child };
                foreach (var branch in branches)
                    foreach (string name in SameValueReferences(branch)) yield return name;
            }
        }

        private static void Check(JsonElement schema, JsonElement value, JsonElement root)
        {
            if (schema.ValueKind == JsonValueKind.True) return;
            InputGuard.Require(schema.ValueKind != JsonValueKind.False && value.ValueKind != JsonValueKind.Undefined);
            if (schema.TryGetProperty("$ref", out var reference)) Check(Definition(root, ReferenceName(reference)), value, root);
            if (schema.TryGetProperty("x-maxUtf8Bytes", out var bytes) || schema.TryGetProperty("x-maxDepth", out _))
                new InputBudget(depth: schema.TryGetProperty("x-maxDepth", out var depth) ? depth.GetInt32() : V4Json.MaximumInputDepth,
                    utf8Bytes: bytes.ValueKind == JsonValueKind.Number ? bytes.GetInt32() : (int?)null).Check(value);
            if (schema.TryGetProperty("type", out var type))
                InputGuard.Require(type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Any(t => IsType(value, t.GetString()!)) : IsType(value, type.GetString()!));
            if (schema.TryGetProperty("enum", out var allowed))
                InputGuard.Require(allowed.EnumerateArray().Any(a => Equal(a, value)), allowed.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).ToArray());
            if (schema.TryGetProperty("const", out var constant)) InputGuard.Require(Equal(constant, value));
            foreach (string keyword in new[] { "anyOf", "oneOf", "allOf" })
            {
                if (!schema.TryGetProperty(keyword, out var branches)) continue;
                if (keyword == "allOf") { foreach (var branch in branches.EnumerateArray()) Check(branch, value, root); continue; }
                int matches = 0;
                InputRejection? budgetFailure = null;
                foreach (var branch in branches.EnumerateArray())
                {
                    try { Check(branch, value, root); matches++; }
                    catch (InputRejection rejection) { if (rejection.IsLimit) budgetFailure = rejection; }
                }
                if (matches == 0 && budgetFailure != null) throw budgetFailure;
                InputGuard.Require(keyword == "oneOf" ? matches == 1 : matches > 0);
            }
            if (schema.TryGetProperty("not", out var excluded)) InputGuard.Require(!Matches(excluded, value, root));
            if (schema.TryGetProperty("if", out var condition)
                && schema.TryGetProperty(Matches(condition, value, root) ? "then" : "else", out var consequent))
                Check(consequent, value, root);
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
                if (schema.TryGetProperty("items", out var items)) foreach (var entry in entries) Check(items, entry, root);
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
                    if (schema.TryGetProperty("propertyNames", out var keys)) Check(keys, V4Json.Deserialize<JsonElement>(V4Json.Serialize(property.Name)), root);
                    if (declared.ValueKind == JsonValueKind.Object && declared.TryGetProperty(property.Name, out var member)) Check(member, property.Value, root);
                    else if (schema.TryGetProperty("additionalProperties", out var additional)) Check(additional, property.Value, root);
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
        private static bool Matches(JsonElement schema, JsonElement value, JsonElement root)
        {
            try { Check(schema, value, root); return true; }
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
