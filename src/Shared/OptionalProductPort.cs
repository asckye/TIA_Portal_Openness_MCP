using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.Shared;

internal sealed class OptionalProductPort
{
    private readonly Func<string, JsonObject, JsonNode?> _call;
    private readonly Func<string> _identity;
    internal OptionalProductPort(Func<string, JsonObject, JsonNode?> call, Func<string> identity)
    {
        _call = call;
        _identity = identity;
    }
    internal ResponseMessage Invoke(OptionalProductRequest request, Action prepare)
    {
        try { prepare(); }
        catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is JsonException)
        {
            request.ValidationError = error.Message;
            request.ValidationKind = error.GetType().Name;
        }
        var unavailable = OptionalProductCapabilities.Unsupported(HardwareContract.ReleaseKey, request.Operation, request.Category, request.Kind, request.Action, request.ExecutionMode, request.Password);
        if (unavailable != null) throw new NotSupportedException(unavailable);
        var arguments = new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request), ["dryRun"] = request.DryRun };
        if (!request.DryRun || request.Action == "showInEditor")
        {
            arguments["confirm"] = true;
            arguments["expectedProjectFile"] = _identity();
        }
        string prefix = PortedFamilies.ForTool(request.Operation).OperationPrefix;
        var reply = _call(prefix + ".ExecuteOptionalProduct", arguments) as JsonObject
            ?? throw new InvalidOperationException("Missing optional-product worker reply.");
        return new ResponseMessage { Message = (string?)reply["Message"] ?? "", Meta = (JsonObject)reply["Meta"]!.DeepClone() };
    }
}
