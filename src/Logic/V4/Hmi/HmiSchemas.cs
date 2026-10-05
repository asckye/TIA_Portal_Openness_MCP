using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4.Inputs;
using ContractSchema = TiaMcp.Logic.V4.Inputs.InputSchema;

namespace TiaMcp.Logic.V4.Hmi
{
    public static class HmiSchemas
    {
        public static JsonObject For<T>() where T : HmiObject => For(typeof(T));

        public static JsonObject For(Type type) => JsonNode.Parse(V4Json.Serialize(Contract(type).Json))!.AsObject();

        internal static ContractSchema Contract(Type type)
        {
            V4Validation.Require(typeof(HmiObject).IsAssignableFrom(type) && type != typeof(HmiObject), "An HMI DTO type is required.");
            var definitions = new JsonObject();
            var root = Describe(type, definitions);
            root["$schema"] = "https://json-schema.org/draft/2020-12/schema";
            root["$defs"] = definitions;
            return new ContractSchema(V4Json.ParseInput(root.ToJsonString()));
        }

        // Later registration uses this as inputSchema, adding the tool's other fields
        // and required entries. Definitions stay at the document root: embedding For<T>()
        // under properties without hoisting $defs would break its local references.
        public static JsonObject InputSchema<T>(string parameter) where T : HmiObject
        {
            V4Validation.Text(parameter, nameof(parameter));
            var definitions = new JsonObject();
            var root = new JsonObject
            {
                ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
                ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JsonObject { [parameter] = Describe(typeof(T), definitions) },
                ["required"] = new JsonArray(parameter), ["$defs"] = definitions
            };
            return JsonNode.Parse(V4Json.Serialize(new ContractSchema(V4Json.ParseInput(root.ToJsonString())).Json))!.AsObject();
        }

        private static JsonObject Describe(Type type, JsonObject definitions)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(string)) return new JsonObject { ["type"] = "string" };
            if (type == typeof(int)) return new JsonObject { ["type"] = "integer", ["minimum"] = int.MinValue, ["maximum"] = int.MaxValue };
            if (type == typeof(double)) return new JsonObject { ["type"] = "number" };
            if (type == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
            if (type == typeof(Scalar)) return new JsonObject { ["type"] = new JsonArray("string", "number", "boolean", "null") };
            if (type == typeof(ClassicText)) return new JsonObject { ["oneOf"] = new JsonArray(
                new JsonObject { ["type"] = "string" },
                new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" } }) };
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                return new JsonObject { ["type"] = "array", ["items"] = Describe(type.GenericTypeArguments[0], definitions) };
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
                return new JsonObject { ["type"] = "object", ["additionalProperties"] = Describe(type.GenericTypeArguments[1], definitions) };
            if (!definitions.ContainsKey(type.Name))
            {
                definitions[type.Name] = new JsonObject();
                var union = HmiContracts.Union(type);
                if (union != null)
                    definitions[type.Name] = new JsonObject { ["oneOf"] = new JsonArray(union.Values.Select(t => (JsonNode)Describe(t, definitions)).ToArray()) };
                else
                {
                    var properties = new JsonObject();
                    var required = new JsonArray();
                    string? control = HmiContracts.ControlName(type);
                    if (control != null)
                    {
                        properties["type"] = new JsonObject { ["type"] = "string", ["const"] = control };
                        required.Add("type");
                    }
                    foreach (var field in HmiContracts.Constructor(type).GetParameters())
                    {
                        var schema = Describe(field.ParameterType, definitions);
                        Constrain(type, field.Name!, schema);
                        properties[field.Name!] = schema;
                        if (!field.HasDefaultValue) required.Add(field.Name!);
                    }
                    var schemaObject = new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
                        ["properties"] = properties, ["required"] = required };
                    CrossFieldConstraints(type, schemaObject);
                    definitions[type.Name] = schemaObject;
                }
            }
            return new JsonObject { ["$ref"] = "#/$defs/" + type.Name };
        }

        private static void Constrain(Type owner, string field, JsonObject schema)
        {
            if (field == "name" && owner != typeof(ClassicScreen) && owner != typeof(UnifiedScreen)
                && owner != typeof(ClassicPackageSpec) && owner != typeof(UnifiedThemeSpec) && owner != typeof(AmlNodeSpec))
                schema["pattern"] = "\\S";
            if (owner == typeof(AmlDeviceSpec) && field == "typeIdentifier") schema["pattern"] = "\\S";
            if (owner == typeof(ClassicScreen) && (field == "width" || field == "height")) schema["minimum"] = field == "width" ? 320 : 240;
            if (typeof(ClassicScreenItem).IsAssignableFrom(owner))
            {
                if (field == "width" || field == "height") schema["minimum"] = 1;
                if (field == "left" || field == "top") schema["minimum"] = 0;
            }
            if (owner == typeof(ClassicTagTableSpec) && field == "tags" || owner == typeof(DeviceAmlSpec) && field == "devices") schema["minItems"] = 1;
            if (field == "deviceItems") schema["maxItems"] = 5000;
            if (owner == typeof(DeviceItemSpec) && field == "role")
                schema["pattern"] = "^\\s*(Rack|DeviceItem|CommunicationInterface|CommunicationPort)\\s*$";
            if (field == "textProperty") schema["enum"] = new JsonArray("Text", "AlternateText", "ToolTipText");
            if (owner == typeof(ClassicAction))
            {
                if (field == "actionKind") schema["enum"] = new JsonArray("SetBit", "ResetBit");
                else schema["pattern"] = "\\S";
            }
            if (owner == typeof(LayoutItem) && field == "type") schema["enum"] = Strings(HmiContracts.Unified.Keys);
            if (owner == typeof(UnifiedThemeSpec) && field == "palette")
            {
                var colors = new JsonObject();
                var aliases = new JsonArray();
                foreach (string name in HmiRules.PaletteColors.Take(5))
                {
                    colors[name] = new JsonObject { ["type"] = "string", ["pattern"] = "^0x[0-9a-fA-F]{8}$" };
                    string camel = char.ToLowerInvariant(name[0]) + name.Substring(1);
                    aliases.Add(new JsonObject { ["if"] = new JsonObject { ["not"] = new JsonObject { ["required"] = new JsonArray(name) } },
                        ["then"] = new JsonObject { ["properties"] = new JsonObject { [camel] = new JsonObject { ["pattern"] = "^0x[0-9a-fA-F]{8}$" } } } });
                }
                schema["properties"] = colors;
                schema["allOf"] = aliases;
            }
            if (field == "properties" || field == "font" || field == "content" || field == "padding")
            {
                if (!schema.ContainsKey("type")) return; // Classic properties are closed DTO references.
                schema["propertyNames"] = new JsonObject { ["pattern"] = "\\S" };
                if (owner == typeof(UnifiedIoFieldItem) && field == "properties") schema["propertyNames"] = new JsonObject { ["enum"] = Strings(HmiRules.IoProperties) };
                if (owner == typeof(UnifiedRectangleItem) && field == "properties") schema["propertyNames"] = RectangleNames();
                if (owner == typeof(UnifiedScreen)) schema["propertyNames"] = new JsonObject { ["pattern"] = "\\S",
                    ["not"] = new JsonObject { ["enum"] = new JsonArray("Name", "Width", "Height") } };
            }
        }

        private static JsonObject RectangleNames() => new JsonObject { ["pattern"] = "\\S",
            ["not"] = new JsonObject { ["pattern"] = "^\\s*([Ff][Oo][Rr][Ee][Cc][Oo][Ll][Oo][Rr]|[Tt][Ee][Xx][Tt]|[Ff][Oo][Nn][Tt]|[Cc][Oo][Nn][Tt][Ee][Nn][Tt]|[Pp][Aa][Dd][Dd][Ii][Nn][Gg])\\s*$" } };

        private static void CrossFieldConstraints(Type type, JsonObject schema)
        {
            if (type == typeof(ClassicScreenSpec)) schema["$comment"] = "Runtime also enforces case-insensitive unique names and containment in the selected screen (default 640x480).";
            if (type == typeof(ClassicTagTableSpec)) schema["$comment"] = "Runtime also enforces case-insensitive unique tag names.";
            if (type == typeof(ClassicTag)) schema["$comment"] = "Runtime requires connection and controllerTag to be either both nonblank or both blank/absent.";
            if (type == typeof(DeviceAmlSpec)) schema["$comment"] = "Runtime enforces at most 5000 device items across all devices and item depth 0..8. JSON depth remains 64; current H parsers have no character/string-length cap.";
            if (type == typeof(DeviceItemSpec))
                schema["anyOf"] = new JsonArray(
                    new JsonObject { ["required"] = new JsonArray("builtIn"), ["properties"] = new JsonObject { ["builtIn"] = new JsonObject { ["const"] = true } } },
                    new JsonObject { ["required"] = new JsonArray("role"), ["properties"] = new JsonObject { ["role"] = new JsonObject { ["pattern"] = "^\\s*(CommunicationInterface|CommunicationPort)\\s*$" } } },
                    new JsonObject { ["required"] = new JsonArray("typeIdentifier"), ["properties"] = new JsonObject { ["typeIdentifier"] = new JsonObject { ["pattern"] = "\\S" } } });
            if (type == typeof(AmlNodeSpec)) schema["$comment"] = "networkAddress: empty, IPAddress.TryParse-compatible address or digits; preserved by the runtime validator.";
            if (type == typeof(LayoutItem))
            {
                schema["allOf"] = new JsonArray(
                    new JsonObject { ["if"] = new JsonObject { ["required"] = new JsonArray("type"), ["properties"] = new JsonObject { ["type"] = new JsonObject { ["const"] = "IOField" } } },
                        ["then"] = new JsonObject { ["properties"] = new JsonObject { ["properties"] = new JsonObject { ["propertyNames"] = new JsonObject { ["enum"] = Strings(HmiRules.IoProperties) } } } } },
                    new JsonObject { ["if"] = new JsonObject { ["properties"] = new JsonObject { ["type"] = new JsonObject { ["const"] = "Rectangle" } } },
                        ["then"] = new JsonObject { ["properties"] = new JsonObject { ["text"] = false, ["font"] = false, ["content"] = false, ["padding"] = false,
                            ["properties"] = new JsonObject { ["propertyNames"] = RectangleNames() } } } });
            }
        }

        private static JsonArray Strings(IEnumerable<string> values) => new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
    }
}
