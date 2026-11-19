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
            request.Inputs[raw] = TiaOpenness.Shared.CapabilityInput.Optional(document.RootElement);
        }
        catch (JsonException error) { request.InputErrors[raw] = error.Message; }
    }
    internal ResponseMessage Invoke(string module, OptionalEngineeringRequest request)
    {
        foreach (var text in new[] { request.ObjectPathJson, request.CollectionPathJson })
            if (text.Length != 0) UnifiedPortService.ValidateAddress(text, optional: true);
        if (request.PropertiesJson.Length != 0) UnifiedPortService.ValidateProperties(request.PropertiesJson);
        request.IsWrite = OptionalEngineeringMutations.Writes(request);
        if (TiaOpenness.Shared.ReleaseCapabilityQuery.OptionalProblem(HardwareContract.ReleaseKey, request) is string reason)
        {
            var meta = JsonSerializer.SerializeToNode(OptionalEngineeringCapabilities.Refusal(reason).Meta)!.AsObject();
            meta["action"] = new[] { request.Action, request.Kind, request.Source }.FirstOrDefault(value => !string.IsNullOrEmpty(value));
            return new ResponseMessage { Message = reason, Meta = meta };
        }
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
