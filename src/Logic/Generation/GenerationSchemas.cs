using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TiaMcp.Logic.Generation
{
    public static class GenerationSchemas
    {
        private static readonly IReadOnlyDictionary<string, JsonElement> Schemas = Load();
        public static IReadOnlyCollection<string> Names => Schemas.Keys.ToArray();

        public static string GetJson(string name) => Get(name).GetRawText();

        public static IReadOnlyList<GenerationValidationError> Validate(string name, JsonElement value)
        {
            // Enforce duplicate, Unicode and number rules even for caller-created JsonElements.
            CanonicalJson.Encode(value);
            return ValidateSchema(Get(name), value, name + ".schema.json");
        }

        public static void RequireValid(string name, JsonElement value)
        {
            var errors = Validate(name, value);
            if (errors.Count != 0) throw new GenerationValidationException(errors);
        }

        internal static IReadOnlyList<GenerationValidationError> ValidateSchema(JsonElement schema, JsonElement value, string file)
        {
            var errors = new List<GenerationValidationError>();
            Visit(schema, value, file, "", errors, 0);
            return errors.AsReadOnly();
        }

        private static JsonElement Get(string name) => Schemas.TryGetValue(name, out var schema) ? schema :
            throw new ArgumentException("Unknown generation schema: " + name, nameof(name));

        private static IReadOnlyDictionary<string, JsonElement> Load()
        {
            var schemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var assembly = typeof(GenerationSchemas).Assembly;
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("TiaMcp.Generation.Schemas.", StringComparison.Ordinal)))
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var document = JsonDocument.Parse(stream);
                schemas.Add(name.Substring("TiaMcp.Generation.Schemas.".Length).Replace(".schema.json", ""), document.RootElement.Clone());
            }
            if (schemas.Count != 14) throw new InvalidOperationException("Generation schema resources are missing.");
            return schemas;
        }

        private static void Visit(JsonElement schema, JsonElement value, string file, string path, List<GenerationValidationError> errors, int depth)
        {
            if (errors.Count >= 100) return;
            void Error(string rule, string message) => errors.Add(new GenerationValidationError(path, rule, message));
            if (depth > 128) { Error("depth", "Schema evaluation depth exceeded."); return; }
            if (schema.ValueKind == JsonValueKind.True) return;
            if (schema.ValueKind == JsonValueKind.False) { Error("false", "Value is prohibited."); return; }
            if (schema.TryGetProperty("$ref", out var reference))
            {
                var parts = reference.GetString()!.Split('#');
                var targetFile = parts[0].Length == 0 ? file : parts[0];
                if (!targetFile.EndsWith(".schema.json", StringComparison.Ordinal) || targetFile.IndexOf('/') >= 0 || targetFile.IndexOf('\\') >= 0)
                    throw CanonicalJson.Failure(path, "$ref", "Only bundled schema references are allowed.");
                var target = Get(targetFile.Replace(".schema.json", ""));
                if (parts.Length > 1 && parts[1].Length > 0)
                    foreach (var part in parts[1].TrimStart('/').Split('/')) target = target.GetProperty(part.Replace("~1", "/").Replace("~0", "~"));
                Visit(target, value, targetFile, path, errors, depth + 1);
            }
            if (schema.TryGetProperty("type", out var type))
            {
                var matches = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Any(t => IsType(value, t.GetString()!)) : IsType(value, type.GetString()!);
                if (!matches) { Error("type", "Expected " + type.GetRawText() + "."); return; }
            }
            if (schema.TryGetProperty("const", out var constant) && !Equal(value, constant)) Error("const", "Unexpected constant.");
            if (schema.TryGetProperty("enum", out var enumeration) && !enumeration.EnumerateArray().Any(item => Equal(value, item))) Error("enum", "Value is outside the allowed set.");
            foreach (var keyword in new[] { "allOf", "anyOf", "oneOf" })
            {
                if (!schema.TryGetProperty(keyword, out var branches)) continue;
                var results = branches.EnumerateArray().Select(branch =>
                {
                    var result = new List<GenerationValidationError>();
                    Visit(branch, value, file, path, result, depth + 1);
                    return result;
                }).ToArray();
                if (keyword == "allOf") foreach (var result in results) errors.AddRange(result);
                else if (keyword == "anyOf" && results.All(r => r.Count > 0) || keyword == "oneOf" && results.Count(r => r.Count == 0) != 1)
                    Error(keyword, "Value does not match the required schema alternatives.");
            }
            if (schema.TryGetProperty("if", out var condition))
            {
                var conditionErrors = new List<GenerationValidationError>();
                Visit(condition, value, file, path, conditionErrors, depth + 1);
                if (schema.TryGetProperty(conditionErrors.Count == 0 ? "then" : "else", out var branch)) Visit(branch, value, file, path, errors, depth + 1);
            }
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (schema.TryGetProperty("required", out var required))
                    foreach (var item in required.EnumerateArray())
                        if (!value.TryGetProperty(item.GetString()!, out _)) errors.Add(new GenerationValidationError(path + "/" + CanonicalJson.Pointer(item.GetString()!), "required", "Required property is missing."));
                schema.TryGetProperty("properties", out var properties);
                schema.TryGetProperty("patternProperties", out var patterns);
                foreach (var property in value.EnumerateObject())
                {
                    var child = path + "/" + CanonicalJson.Pointer(property.Name);
                    if (schema.TryGetProperty("propertyNames", out var names)) Visit(names, JsonSerializer.SerializeToElement(property.Name), file, child, errors, depth + 1);
                    var matched = false;
                    if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var item))
                    { matched = true; Visit(item, property.Value, file, child, errors, depth + 1); }
                    if (patterns.ValueKind == JsonValueKind.Object)
                        foreach (var pattern in patterns.EnumerateObject())
                            if (Matches(property.Name, pattern.Name, child)) { matched = true; Visit(pattern.Value, property.Value, file, child, errors, depth + 1); }
                    if (!matched && schema.TryGetProperty("additionalProperties", out var additional))
                    {
                        if (additional.ValueKind == JsonValueKind.False) errors.Add(new GenerationValidationError(child, "additionalProperties", "Unknown property."));
                        else Visit(additional, property.Value, file, child, errors, depth + 1);
                    }
                }
                Bounds(schema, "Properties", value.EnumerateObject().Count(), path, errors);
            }
            if (value.ValueKind == JsonValueKind.Array)
            {
                Bounds(schema, "Items", value.GetArrayLength(), path, errors);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var unique = schema.TryGetProperty("uniqueItems", out var distinct) && distinct.GetBoolean();
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    var child = path + "/" + index.ToString(CultureInfo.InvariantCulture);
                    if (unique && !seen.Add(Encoding.UTF8.GetString(CanonicalJson.Encode(item)))) errors.Add(new GenerationValidationError(child, "uniqueItems", "Duplicate array value."));
                    if (schema.TryGetProperty("items", out var itemSchema)) Visit(itemSchema, item, file, child, errors, depth + 1);
                    index++;
                }
            }
            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()!;
                Bounds(schema, "Length", text.Length - text.Count(char.IsLowSurrogate), path, errors);
                if (schema.TryGetProperty("pattern", out var pattern) && !Matches(text, pattern.GetString()!, path)) Error("pattern", "String does not match the required pattern.");
            }
            if (value.ValueKind == JsonValueKind.Number)
            {
                if (schema.TryGetProperty("minimum", out var min) && CanonicalJson.CompareNumbers(value.GetRawText(), min.GetRawText()) < 0) Error("minimum", "Number is below the minimum.");
                if (schema.TryGetProperty("maximum", out var max) && CanonicalJson.CompareNumbers(value.GetRawText(), max.GetRawText()) > 0) Error("maximum", "Number is above the maximum.");
            }
        }

        internal static bool Matches(string text, string pattern, string path)
        {
            try { return Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
            catch (Exception ex) when (ex is ArgumentException || ex is RegexMatchTimeoutException)
            { throw CanonicalJson.Failure(path, "pattern", "Invalid or excessively expensive regular expression."); }
        }

        private static void Bounds(JsonElement schema, string suffix, int count, string path, List<GenerationValidationError> errors)
        {
            foreach (var prefix in new[] { "min", "max" })
            {
                var keyword = prefix + suffix;
                if (schema.TryGetProperty(keyword, out var bound) && (prefix == "min"
                    ? CanonicalJson.CompareNumbers(count.ToString(CultureInfo.InvariantCulture), bound.GetRawText()) < 0
                    : CanonicalJson.CompareNumbers(count.ToString(CultureInfo.InvariantCulture), bound.GetRawText()) > 0))
                    errors.Add(new GenerationValidationError(path, keyword, "Collection or string length is outside the permitted bounds."));
            }
        }

        private static bool Equal(JsonElement first, JsonElement second) => CanonicalJson.Encode(first).SequenceEqual(CanonicalJson.Encode(second));
        private static bool IsType(JsonElement value, string type) => type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && IsInteger(value.GetRawText()),
            "boolean" => value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => throw new InvalidOperationException("Unsupported schema type: " + type)
        };

        private static bool IsInteger(string raw)
        {
            var canonical = CanonicalJson.Number(raw);
            var exponent = canonical.IndexOf('e');
            return exponent < 0 || canonical[exponent + 1] != '-';
        }
    }
}
