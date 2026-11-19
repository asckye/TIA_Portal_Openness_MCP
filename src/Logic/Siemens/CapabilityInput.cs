using System;
using System.Linq;
using System.Text.Json;
using TiaMcp.Adapters.Contracts;

namespace TiaOpenness.Shared;

public static class CapabilityInput
{
    public static OptionalEngineeringValue Optional(JsonElement value)
    {
        return value.ValueKind switch {
            JsonValueKind.Object => new OptionalEngineeringValue {
                Kind = "object", Properties = value.EnumerateObject().ToDictionary(p => p.Name, p => Optional(p.Value)) },
            JsonValueKind.Array => new OptionalEngineeringValue { Kind = "array", Items = value.EnumerateArray().Select(Optional).ToArray() },
            JsonValueKind.String => new OptionalEngineeringValue { Kind = "string", Text = value.GetString()! },
            JsonValueKind.Number => new OptionalEngineeringValue { Kind = "number", Text = value.GetRawText() },
            JsonValueKind.True or JsonValueKind.False => new OptionalEngineeringValue { Kind = "boolean", Text = value.GetRawText() },
            _ => new OptionalEngineeringValue()
        };
    }

    public static UnifiedValue Unified(JsonElement value)
    {
        var result = new UnifiedValue();
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                result.Kind = "object";
                foreach (var property in value.EnumerateObject())
                {
                    if (result.Properties.ContainsKey(property.Name))
                        throw new ArgumentException("Duplicate JSON key.");
                    result.Properties.Add(property.Name, Unified(property.Value));
                }
                break;
            case JsonValueKind.Array:
                result.Kind = "array";
                foreach (var item in value.EnumerateArray()) result.Items.Add(Unified(item));
                break;
            case JsonValueKind.String: result.Kind = "string"; result.Text = value.GetString()!; break;
            case JsonValueKind.True: case JsonValueKind.False:
                result.Kind = "boolean"; result.Text = value.GetRawText(); break;
            case JsonValueKind.Number: result.Kind = "number"; result.Text = value.GetRawText(); break;
        }
        return result;
    }
}
