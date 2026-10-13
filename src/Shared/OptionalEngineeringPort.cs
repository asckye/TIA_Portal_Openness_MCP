using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services;

internal sealed class OptionalEngineeringPort
{
    private readonly Func<string, JsonObject, JsonNode?> _call;
    private readonly Func<string> _identity;
    internal OptionalEngineeringPort(Func<string, JsonObject, JsonNode?> call, Func<string> identity)
    {
        _call = call;
        _identity = identity;
    }
    internal static void Input(OptionalEngineeringRequest request, string raw)
    {
        if (raw == null || request.Inputs.ContainsKey(raw) || request.InputErrors.ContainsKey(raw)) return;
        try
        {
            using var document = JsonDocument.Parse(raw);
            request.Inputs[raw] = Value(document.RootElement);
        }
        catch (JsonException error) { request.InputErrors[raw] = error.Message; }
    }
    private static OptionalEngineeringValue Value(JsonElement value)
    {
        return value.ValueKind switch {
            JsonValueKind.Object => new OptionalEngineeringValue { Kind = "object", Properties = value.EnumerateObject().ToDictionary(p => p.Name, p => Value(p.Value)) },
            JsonValueKind.Array => new OptionalEngineeringValue { Kind = "array", Items = value.EnumerateArray().Select(Value).ToArray() },
            JsonValueKind.String => new OptionalEngineeringValue { Kind = "string", Text = value.GetString()! },
            JsonValueKind.Number => new OptionalEngineeringValue { Kind = "number", Text = value.GetRawText() },
            JsonValueKind.True or JsonValueKind.False => new OptionalEngineeringValue { Kind = "boolean", Text = value.GetRawText() },
            _ => new OptionalEngineeringValue()
        };
    }
    internal ResponseMessage Invoke(string module, OptionalEngineeringRequest request)
    {
        request.IsWrite = OptionalEngineeringMutations.Writes(request);
        if (OptionalEngineeringCapabilities.Unsupported(HardwareContract.ReleaseKey, request) is string unavailable)
            return new ResponseMessage { Message = unavailable,
                Meta = JsonSerializer.SerializeToNode(OptionalEngineeringCapabilities.Refusal(unavailable).Meta)!.AsObject() };
        foreach (var raw in new[] { "{}", "[]", "null" }) Input(request, raw);
        var arguments = new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request), ["dryRun"] = !request.IsWrite };
        if (request.IsWrite)
        {
            arguments["confirm"] = true;
            arguments["expectedProjectFile"] = _identity();
        }
        var reply = _call(module, arguments) ?? throw new InvalidOperationException("Missing optional-product worker reply.");
        return JsonSerializer.Deserialize<ResponseMessage>(reply.ToJsonString())!;
    }
}
