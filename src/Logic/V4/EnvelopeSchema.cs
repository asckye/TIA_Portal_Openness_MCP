using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Logic.V4
{
    // One schema for every served V4 descriptor. Producers retain closed validation;
    // consumers may accept additional fields and diagnostic codes within version 4.
    public static class EnvelopeSchema
    {
        public static readonly JsonElement Value = Build();

        private static JsonElement Build()
        {
            var errors = new JsonArray(new JsonObject { ["type"] = "null" });
            var knownCodes = new List<string>();
            foreach (var type in typeof(ErrorDetails).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(ErrorDetails)) && !t.IsAbstract).OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                string code = JsonNamingPolicy.SnakeCaseUpper.ConvertName(type.Name.Replace("Details", ""));
                knownCodes.Add(code);
                errors.Add(Object(("code", new JsonObject { ["const"] = code }), ("message", Text()), ("details", Describe(type))));
            }
            errors.Add(Object(("code", new JsonObject { ["type"] = "string", ["minLength"] = 1,
                    ["not"] = new JsonObject { ["enum"] = new JsonArray(knownCodes.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()) } }),
                ("message", Text()), ("details", new JsonObject { ["type"] = "object" })));
            var warning = Object(("code", Text()), ("message", Text()), ("details", new JsonObject { ["type"] = "object" }));
            var paging = Describe(typeof(Paging));
            paging["type"] = new JsonArray("object", "null");
            var meta = Object(("timestamp", new JsonObject { ["type"] = "string", ["format"] = "date-time" }),
                ("releaseKey", NullableText()), ("tool", Text()), ("requestId", Text()),
                ("outcome", Choices("succeeded", "rejected-before-operation", "read-failed", "failed", "partial", "unknown")),
                ("execution", Choices("not-started", "read-only", "completed", "partial", "unknown")),
                ("requiresSessionReset", new JsonObject { ["type"] = "boolean" }),
                ("behaviorPolicy", Choices("not-applicable", "current", "safe-v4")),
                ("completeness", Choices("complete", "partial", "none", "unknown")),
                ("paging", paging), ("warnings", new JsonObject { ["type"] = "array", ["items"] = warning }));
            var root = Object(("schemaVersion", new JsonObject { ["type"] = "integer", ["const"] = 4 }),
                ("ok", new JsonObject { ["type"] = "boolean" }), ("data", new JsonObject { ["type"] = new JsonArray("object", "null") }),
                ("error", new JsonObject { ["oneOf"] = errors }), ("meta", meta));
            return JsonSerializer.SerializeToElement(root);
        }

        private static JsonObject Describe(Type type)
        {
            if (type == typeof(string)) return NullableText();
            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable != null)
                return new JsonObject { ["anyOf"] = new JsonArray(Describe(nullable), new JsonObject { ["type"] = "null" }) };
            if (type == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
            if (type == typeof(int) || type == typeof(long)) return new JsonObject { ["type"] = "integer", ["minimum"] = 0 };
            if (type.IsEnum)
                return Choices(Enum.GetNames(type).Select(n => JsonNamingPolicy.CamelCase.ConvertName(n)).ToArray());
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
                return new JsonObject { ["type"] = "object" };
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                return new JsonObject { ["type"] = "array", ["items"] = Describe(type.GetGenericArguments()[0]) };
            return Object(type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => (JsonNamingPolicy.CamelCase.ConvertName(p.Name), (JsonNode)Describe(p.PropertyType))).ToArray());
        }

        private static JsonObject Object(params (string Name, JsonNode Schema)[] fields)
        {
            var properties = new JsonObject();
            foreach (var field in fields) properties.Add(field.Name, field.Schema);
            return new JsonObject { ["type"] = "object", ["required"] = new JsonArray(fields.Select(f => (JsonNode)JsonValue.Create(f.Name)!).ToArray()),
                ["properties"] = properties };
        }
        private static JsonObject Text() => new JsonObject { ["type"] = "string", ["minLength"] = 1 };
        private static JsonObject NullableText() => new JsonObject { ["type"] = new JsonArray("string", "null") };
        private static JsonObject Choices(params string[] values) => new JsonObject { ["type"] = "string",
            ["enum"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()) };
    }
}
