using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    // 2.7.58: the machine-readable part of a parameter description goes into the tool's JSON schema, for every tool.
    // A client that validates against the schema (Claude Code does) then rejects an invalid enum value before the
    // call leaves the model, and the model reads exact alternatives / defaults / example values instead of prose.
    // Zero dependencies; linked into the offline suite.
    public static class SchemaHintsLogic
    {
        public sealed class Result
        {
            public int Enums { get; set; }
            public int Defaults { get; set; }
            public int Examples { get; set; }
            public int Descriptions { get; set; }
            public List<string> EnumProperties { get; } = new List<string>();
            public bool Changed => Enums + Defaults + Examples + Descriptions > 0;
        }

        /// <summary>
        /// Adds "enum" (documented alternatives), "default" (the C# default) and "examples" (the value from the tool's
        /// worked example) to the properties of an input schema. The schema object is modified in place; properties the
        /// SDK did not emit are never invented.
        /// </summary>
        public static Result Augment(JsonObject schema, IReadOnlyList<PreflightLogic.ParameterSpec> specs, JsonObject? example)
        {
            var result = new Result();
            if (!(schema["properties"] is JsonObject properties)) return result;
            foreach (var spec in specs)
            {
                if (!(properties[spec.Name] is JsonObject property)) continue;
                // A parameter without its own [Description] has no "description" in the SDK schema: the vocabulary text fills it.
                if (property["description"] == null && spec.Description.Length > 0)
                {
                    property["description"] = spec.Description; result.Descriptions++;
                }
                if (spec.Kind == "string" && property["enum"] == null)
                {
                    var alternatives = PreflightLogic.Alternatives(spec.Description).ToList();
                    if (alternatives.Count > 0)
                    {
                        // The default must stay valid; "" is only added when it is the default itself.
                        var def = DefaultString(spec.DefaultText);
                        if (def != null && !alternatives.Contains(def, StringComparer.Ordinal)) alternatives.Add(def);
                        property["enum"] = new JsonArray(alternatives.Select(a => (JsonNode)a).ToArray());
                        result.Enums++; result.EnumProperties.Add(spec.Name);
                    }
                }
                if (property["default"] == null && spec.DefaultText != null)
                {
                    var node = DefaultNode(spec);
                    if (node != null) { property["default"] = node; result.Defaults++; }
                }
                if (property["examples"] == null && example != null && example[spec.Name] != null)
                {
                    var value = example[spec.Name]!;
                    // A string parameter shown with an object / array example (the *Json parameters) would invite the object form in a
                    // direct call, which the SDK binder refuses; the schema shows the JSON text instead.
                    JsonNode shown = spec.Kind == "string" && (value is JsonObject || value is JsonArray) ? JsonValue.Create(value.ToJsonString())! : value.DeepClone();
                    property["examples"] = new JsonArray(shown);
                    result.Examples++;
                }
            }
            return result;
        }

        /// <summary>The string default without its quotes ("\"read\"" -> read); null for non-strings / null.</summary>
        internal static string? DefaultString(string? defaultText)
        {
            if (defaultText == null || defaultText == "null") return null;
            if (defaultText.Length >= 2 && defaultText[0] == '"' && defaultText[defaultText.Length - 1] == '"') return defaultText.Substring(1, defaultText.Length - 2);
            return null;
        }

        private static JsonNode? DefaultNode(PreflightLogic.ParameterSpec spec)
        {
            var text = spec.DefaultText;
            if (text == null || text == "null") return null;
            switch (spec.Kind)
            {
                case "string": var s = DefaultString(text); return s == null ? null : JsonValue.Create(s);
                case "boolean": return bool.TryParse(text, out var b) ? JsonValue.Create(b) : null;
                case "integer": return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? JsonValue.Create(l) : null;
                case "number": return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : null;
                default: return null;
            }
        }
    }
}
