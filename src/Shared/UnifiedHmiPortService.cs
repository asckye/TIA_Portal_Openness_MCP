using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal class UnifiedHmiPortService
    {
        private readonly Func<string, JsonObject, JsonNode?> _call;
        private readonly Func<string> _identity;

        protected UnifiedHmiPortService()
            : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.ProjectIdentity) { }

        internal UnifiedHmiPortService(Func<string, JsonObject, JsonNode?> call, Func<string> identity)
        {
            _call = call;
            _identity = identity;
        }

        protected T Invoke<T>(UnifiedHmiRequest request)
        {
            foreach (string input in new[] { request.DesignJson, request.PropertiesJson })
            {
                if (string.IsNullOrEmpty(input)) continue;
                try { request.Inputs[input] = Input(JsonNode.Parse(input)); }
                catch (JsonException error)
                {
                    request.Inputs[input] = new UnifiedHmiInput { Kind = "error", Text = error.Message };
                }
            }
            var arguments = new JsonObject
            {
                ["request"] = JsonSerializer.SerializeToNode(request),
                ["dryRun"] = request.IsReadOnly
            };
            if (!request.IsReadOnly)
            {
                arguments["confirm"] = true;
                arguments["expectedProjectFile"] = _identity();
            }
            string operation = request.Operation is "ListUnifiedHmiApiTypes" or "DescribeUnifiedScreenItemType"
                ? "hmi-unified.ExecuteUnifiedHmiMetadata" : "hmi-unified.ExecuteUnifiedHmi";
            var reply = _call(operation, arguments) as JsonObject
                ?? throw new InvalidOperationException("Missing Unified HMI worker reply.");
            if (reply["Failure"] is JsonObject failure)
            {
                string code = (string?)failure["Code"] ?? "";
                string message = (string?)failure["Message"] ?? "Unified HMI operation failed.";
                if (Enum.TryParse<PortalErrorCode>(code, out var native)) throw new PortalException(native, message);
                if (code == nameof(ArgumentException)) throw new ArgumentException(message);
                if (code == nameof(NotSupportedException)) throw new NotSupportedException(message);
                throw new InvalidOperationException(message);
            }
            return reply["Value"] == null ? default! : JsonSerializer.Deserialize<T>(reply["Value"]!.ToJsonString())!;
        }

        private static UnifiedHmiInput Input(JsonNode? node)
        {
            var result = new UnifiedHmiInput();
            if (node is JsonObject fields)
            {
                result.Kind = "object";
                foreach (var pair in fields) result.Properties[pair.Key] = Input(pair.Value);
            }
            else if (node is JsonArray items)
            {
                result.Kind = "array";
                foreach (var item in items) result.Items.Add(Input(item));
            }
            else if (node != null)
            {
                result.Kind = node.GetValueKind() switch
                {
                    JsonValueKind.String => "string",
                    JsonValueKind.True or JsonValueKind.False => "boolean",
                    _ => "number"
                };
                result.Text = result.Kind == "string" ? node.GetValue<string>() : node.ToJsonString();
            }
            return result;
        }
    }
}
