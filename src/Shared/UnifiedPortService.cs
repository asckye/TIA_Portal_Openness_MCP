using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal class UnifiedPortService
    {
        private readonly Func<string, JsonObject, JsonNode?> _call;
        private readonly Func<string> _identity;
        protected UnifiedPortService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.ProjectIdentity) { }
        internal UnifiedPortService(Func<string, JsonObject, JsonNode?> call, Func<string> identity)
        { _call = call; _identity = identity; }

        protected ResponseMessage Invoke(UnifiedRequest request, bool dryRun)
        {
            ValidateInputs(request);
            DecodeInputs(request);
            var unavailable = UnifiedCapabilities.Unsupported(HardwareContract.ReleaseKey, request);
            if (unavailable != null) throw new PortalException(PortalErrorCode.NotSupportedOnVersion, unavailable);
            return Dispatch(request, dryRun);
        }

        internal static void ValidateAddress(string text, bool branch = false, bool optional = false)
        {
            string example = branch ? EngineeringInputValidation.BranchPathExample
                : optional ? EngineeringInputValidation.OptionalObjectPathExample + " (the typed DCC/SiVArc root)" : EngineeringInputValidation.ObjectPathExample;
            if (text == null || text.Length > 32768) throw new ArgumentException("Object path exceeds 32 KiB. Expected property steps, e.g. " + example + ".");
            object? value;
            try
            {
                using var document = JsonDocument.Parse(text);
                value = EngineeringInputValidation.Value(UnifiedInputCodec.Decode(document.RootElement));
            }
            catch (Exception error) when (error is JsonException || error is ArgumentException)
            {
                throw new ArgumentException("Invalid path JSON. Expected property steps, e.g. "
                    + example + ". " + error.Message);
            }
            EngineeringInputValidation.Path(text, value, branch, optional);
        }

        internal static void ValidateProperties(string text)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind != JsonValueKind.Object || UnifiedInputCodec.HasDuplicateKeys(document.RootElement))
                    throw new ArgumentException("propertiesJson must be a JSON object with unique keys, e.g. {\"Width\":800}.");
            }
            catch (JsonException error) { throw new ArgumentException("propertiesJson must be a JSON object with unique keys, e.g. {\"Width\":800}.", error); }
        }

        internal static void ValidateInputs(UnifiedRequest request)
        {
            foreach (var text in new[] { request.ObjectPathJson, request.TagPathJson, request.DurationPathJson })
                if (text.Length != 0) ValidateAddress(text);
            if (request.BranchJson.Length != 0) ValidateAddress(request.BranchJson, true);
            foreach (var text in new[] { request.PropertiesJson, request.ScriptPropertiesJson })
                if (text.Length != 0) ValidateProperties(text);
        }

        internal static void DecodeInputs(UnifiedRequest request)
        {
            foreach (var property in typeof(UnifiedRequest).GetProperties().Where(p => p.Name.EndsWith("Json", StringComparison.Ordinal)))
            {
                var text = (string)property.GetValue(request)!;
                if (request.Inputs.ContainsKey(text)) continue;
                try
                {
                    using var document = JsonDocument.Parse(text);
                    request.Inputs[text] = UnifiedInputCodec.Decode(document.RootElement);
                }
                catch (Exception error) when (error is JsonException || error is ArgumentException)
                { request.Inputs[text] = new UnifiedValue { Error = error.Message }; }
            }
        }

        private ResponseMessage Dispatch(UnifiedRequest request, bool dryRun)
        {
            var arguments = new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request), ["dryRun"] = dryRun };
            if (!dryRun) { arguments["confirm"] = true; arguments["expectedProjectFile"] = _identity(); }
            var family = PortedFamilies.ForTool(request.Operation);
            var operation = family.OperationPrefix + ".ExecuteUnified";
            if (request.Operation == "ReleaseUnifiedReadCursor")
            {
                operation = family.OperationPrefix + ".ReleaseUnifiedReadCursor";
                arguments = new JsonObject { ["cursor"] = request.Cursor };
            }
            var reply = _call(operation, arguments) as JsonObject
                ?? throw new InvalidOperationException("Missing Unified worker reply.");
            if (reply["Failure"] is JsonObject failure)
            {
                var code = (string?)failure["Code"] ?? "";
                var message = (string?)failure["Message"] ?? "Unified operation failed.";
                if (Enum.TryParse<PortalErrorCode>(code, out var native)) throw new PortalException(native, message);
                if (code == nameof(ArgumentException)) throw new ArgumentException(message);
                if (code == nameof(NotSupportedException)) throw new NotSupportedException(message);
                throw new InvalidOperationException(message);
            }
            return JsonSerializer.Deserialize<ResponseMessage>(reply["Value"]!.ToJsonString())!;
        }
    }

    internal static class UnifiedInputCodec
    {
        internal static bool HasDuplicateKeys(JsonElement value)
        {
            var pending = new Stack<JsonElement>();
            pending.Push(value);
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                if (node.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in node.EnumerateObject())
                    {
                        if (!names.Add(property.Name)) return true;
                        pending.Push(property.Value);
                    }
                }
                else if (node.ValueKind == JsonValueKind.Array)
                    foreach (var item in node.EnumerateArray()) pending.Push(item);
            }
            return false;
        }

        internal static UnifiedValue Decode(JsonElement value)
        {
            var result = new UnifiedValue();
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    result.Kind = "object";
                    foreach (var property in value.EnumerateObject())
                    {
                        if (result.Properties.ContainsKey(property.Name))
                            throw new ArgumentException("Duplicate JSON key: " + property.Name);
                        result.Properties.Add(property.Name, Decode(property.Value));
                    }
                    break;
                case JsonValueKind.Array:
                    result.Kind = "array";
                    foreach (var item in value.EnumerateArray()) result.Items.Add(Decode(item));
                    break;
                case JsonValueKind.String: result.Kind = "string"; result.Text = value.GetString()!; break;
                case JsonValueKind.True: case JsonValueKind.False:
                    result.Kind = "boolean"; result.Text = value.GetRawText(); break;
                case JsonValueKind.Number: result.Kind = "number"; result.Text = value.GetRawText(); break;
            }
            return result;
        }
    }
}
