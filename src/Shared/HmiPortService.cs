using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal class HmiPortService
    {
        private readonly Func<string, JsonObject, JsonNode?> call;
        private readonly Func<string> identity;
        protected string? LastNotes { get; private set; }
        protected HmiPortService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.ProjectIdentity) { }
        internal HmiPortService(Func<string, JsonObject, JsonNode?> call, Func<string> identity)
        { this.call = call; this.identity = identity; }
        protected T Invoke<T>(HmiRequest request)
        {
            var unavailable = HmiCapabilities.Unsupported(HardwareContract.ReleaseKey, request.Operation);
            if (unavailable != null) throw new NotSupportedException(unavailable);
            if (request.AttributesJson.Length > 0)
            {
                try
                {
                    var attributes = JsonNode.Parse(request.AttributesJson) as JsonObject
                        ?? throw new ArgumentException("attributesJson must be a JSON object.");
                    foreach (var pair in attributes)
                    {
                        if (pair.Value is JsonArray || pair.Value is JsonObject)
                            throw new ArgumentException("Only scalar attribute values are supported: " + pair.Key);
                        request.Attributes[pair.Key] = pair.Value == null ? new HardwareScalar { Kind = "null" }
                            : pair.Value.GetValueKind() == JsonValueKind.String ? new HardwareScalar { Kind = "string", Text = pair.Value.GetValue<string>() }
                            : pair.Value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? new HardwareScalar {
                                Kind = "boolean", Text = pair.Value.ToJsonString() }
                            : new HardwareScalar { Kind = "number", Text = pair.Value.ToJsonString() };
                    }
                }
                catch (Exception error) when (error is ArgumentException || error is JsonException) { request.AttributesError = error.Message; }
            }
            var arguments = new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request), ["dryRun"] = request.DryRun };
            if (!request.DryRun) { arguments["confirm"] = true; arguments["expectedProjectFile"] = identity(); }
            var reply = call(PortedFamilies.ForTool(request.Operation).OperationPrefix + ".ExecuteHmi", arguments) as JsonObject
                ?? throw new InvalidOperationException("Missing HMI worker reply.");
            LastNotes = (string?)reply["Notes"];
            if (reply["Failure"] is JsonObject failure)
            {
                string code = (string?)failure["Code"] ?? "", message = (string?)failure["Message"] ?? "HMI operation failed.";
                bool unchanged = (bool?)reply["MayHaveChanged"] == false && (bool?)reply["OutcomeUnknown"] == false;
                if (!unchanged) throw new TiaMcp.Logic.V4.HostBehavior.UncertainWorkerFailure(message, reply);
                string? parameter = (string?)failure["Evidence"]?["parameter"];
                Exception error = (string?)failure["Evidence"]?["v4FailureCode"] == "NOT_FOUND"
                    ? AdapterPreconditionException.NotFound(message, parameter)
                    : Enum.TryParse<PortalErrorCode>(code, out var native) ? new PortalException(native, message)
                    : code == nameof(ArgumentException) ? new ArgumentException(message, parameter)
                    : code == nameof(NotSupportedException) ? new NotSupportedException(message)
                    : new InvalidOperationException(message);
                error.Data["foundationKnownNoMutation"] = true;
                throw error;
            }
            return reply["Value"] == null ? default! : JsonSerializer.Deserialize<T>(reply["Value"]!.ToJsonString())!;
        }
    }
}
