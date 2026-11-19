using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.V4;
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
            EngineeringInputValidation.Request(request);
            if (UnifiedCapabilities.InvalidRuntimeSettings(request) is string invalidSettings)
                throw new ArgumentException(invalidSettings, request.Operation == "GetUnifiedRuntimeSettings" ? "fields" : "changes");
            var unavailable = UnifiedCapabilities.Unsupported(HardwareContract.ReleaseKey, request);
            if (unavailable != null) throw new PortalException(PortalErrorCode.NotSupportedOnVersion, unavailable);
            return Dispatch(request, dryRun);
        }

        internal static Error RuntimeSettingsError(string parameter, string message, string[] unknownNames)
        {
            string pointer = "/" + parameter;
            var pointers = parameter == "changes" && unknownNames.Length > 0
                ? new[] { pointer }.Concat(unknownNames.Select(key => pointer + "/" + InvalidArgumentDetails.Segment(key))).ToArray() : null;
            return new Error(message, new InvalidArgumentDetails(pointer, Array.Empty<string>(), pointers));
        }

        // Free-map pointers are normally private. For this diagnostic reconstruct only the rejected setting names from the request keys.
        internal static Error? UnknownRuntimeChangeError(string? tool, JsonElement args, Error error)
        {
            if (tool == "SetUnifiedRuntimeSettings" && error.Details is InvalidArgumentDetails { Parameter: "/changes" }
                && args.ValueKind == JsonValueKind.Object && args.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Object
                && UnifiedRuntimeSettingsFields.Invalid(changes.EnumerateObject().Select(p => p.Name), out var unknown) == error.Message
                && unknown.Length > 0)
                return RuntimeSettingsError("changes", error.Message, unknown);
            return null;
        }

        // Direct tools and bridge binding share admission before schema, readiness and approval gates.
        internal static Error? ValidateNavigation(string tool, JsonElement schema, JsonElement args)
        {
            if (args.ValueKind != JsonValueKind.Object) return null;
            if (tool == "SetUnifiedGlobalScript" && args.TryGetProperty("action", out var scriptAction)
                && scriptAction.ValueKind == JsonValueKind.String)
                try { EngineeringInputValidation.Action(scriptAction.GetString()!, "update", "create"); }
                catch (ArgumentException error)
                { return new Error(error.Message, new InvalidArgumentDetails("action", Array.Empty<string>(), expected: "update | create")); }
            if (tool is "GetUnifiedRuntimeSettings" or "SetUnifiedRuntimeSettings")
            {
                string parameter = tool == "GetUnifiedRuntimeSettings" ? "fields" : "changes";
                if (args.TryGetProperty(parameter, out var settings)
                    && (parameter == "fields" ? settings.ValueKind == JsonValueKind.Array
                        && settings.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String) : settings.ValueKind == JsonValueKind.Object))
                {
                    if (UnifiedInputCodec.HasDuplicateKeys(settings))
                        return new Error("Duplicate runtime setting name.", new InvalidArgumentDetails("/" + parameter, Array.Empty<string>()));
                    var input = UnifiedInputCodec.Decode(settings);
                    if (UnifiedRuntimeSettingsFields.Invalid(parameter == "fields" ? input.Items.Select(v => v.Text) : input.Properties.Keys,
                        out var unknownNames) is string invalidSettings) return RuntimeSettingsError(parameter, invalidSettings, unknownNames);
                }
            }
            if (args.TryGetProperty("expectedProject", out var expectedProject) && expectedProject.ValueKind == JsonValueKind.String
                && HostBehavior.ExpectedProjectArgument(expectedProject.GetString()) is { } projectError) return projectError;
            if (tool is "EnsureUnifiedHmiTagTable" or "EnsureUnifiedHmiTag" or "EnsureUnifiedHmiScreen" or "SetUnifiedHmiRuntimeState")
                foreach (var field in new[] { "tagTableName", "screenName" })
                    if (args.TryGetProperty(field, out var name) && name.ValueKind == JsonValueKind.String)
                        try { TiaMcp.Adapters.Contracts.EngineeringInputValidation.NameOrAbsolutePath(name.GetString()!); }
                        catch (ArgumentException error)
                        { return new Error(error.Message,
                            new InvalidArgumentDetails(field, Array.Empty<string>(), expected: "exact name or absolute HMI path")); }
            if (TiaMcp.Adapters.Contracts.PortedFamilies.Contains(tool)
                && TiaMcp.Adapters.Contracts.PortedFamilies.ForTool(tool).Tools.Contains(tool)
                && TiaMcpServer.ModelContextProtocol.ToolMetadata.Find(tool)?.Domain.StartsWith("HMI", StringComparison.Ordinal) == true)
                foreach (var field in new[] { "screenPath", "groupPath", "plantPath", "typePath", "folderPath" })
                    if (args.TryGetProperty(field, out var path) && path.ValueKind == JsonValueKind.String && path.GetString()!.Length != 0)
                        try { EngineeringInputValidation.AbsoluteHmiPath(path.GetString()!, rootAllowed: field == "folderPath"); }
                        catch (ArgumentException error)
                        { return new Error(error.Message, new InvalidArgumentDetails(field, Array.Empty<string>(), expected: "absolute HMI path")); }
            foreach (var field in new[] { "objectPath", "branch", "collectionPath", "tagPath", "durationPath" })
            {
                if (!schema.GetProperty("properties").TryGetProperty(field, out var fieldSchema)
                    || !fieldSchema.TryGetProperty("type", out var fieldType) || fieldType.GetString() != "array"
                    || !args.TryGetProperty(field, out var input)) continue;
                try
                {
                    ValidateAddress(
                        input.ValueKind == JsonValueKind.String ? input.GetString()! : input.GetRawText(), field == "branch",
                        TiaMcp.Adapters.Contracts.PortedFamilies.Contains(tool)
                            && TiaMcp.Adapters.Contracts.PortedFamilies.ForTool(tool).Name is "F29" or "F31");
                }
                catch (ArgumentException error)
                { return new Error(error.Message, new InvalidArgumentDetails(field, Array.Empty<string>(), expected: "array of property steps")); }
            }
            return null;
        }

        internal static void ValidateAddress(string text, bool branch = false, bool optional = false)
        {
            string example = branch ? EngineeringInputValidation.BranchPathExample
                : optional ? EngineeringInputValidation.OptionalObjectPathExample + " (the typed DCC/SiVArc root)" : EngineeringInputValidation
                    .ObjectPathExample;
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
            EngineeringInputValidation.Request(request);
            if (request.Operation is "GetUnifiedScreenBranch" or "GetUnifiedFaceplateInstance")
                EngineeringInputValidation.AbsoluteHmiPath(request.ScreenPath);
            if (request.Operation == "GetUnifiedLibraryType" || request.Operation == "GetUnifiedFaceplateInstance" && request.TypePath.Length != 0)
                EngineeringInputValidation.AbsoluteHmiPath(request.TypePath);
            if (request.Operation == "ListUnifiedLibraryFolderEntries")
                EngineeringInputValidation.AbsoluteHmiPath(request.FolderPath, rootAllowed: true);
            foreach (var text in new[] { request.ObjectPathJson, request.TagPathJson, request.DurationPathJson })
                if (text.Length != 0) ValidateAddress(text);
            if (request.BranchJson.Length != 0) ValidateAddress(request.BranchJson, true);
            foreach (var text in new[] { request.PropertiesJson, request.ScriptPropertiesJson, request.EntryJson })
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

        internal static UnifiedValue Decode(JsonElement value) => TiaOpenness.Shared.CapabilityInput.Unified(value);
    }
}
