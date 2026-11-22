using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4.Domain;
using CpuSettings = TiaMcp.Logic.V4.Domain.CpuSettings;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    // This boundary belongs only to the device/AML/module/address contract group.
    internal static class HardwareContract
    {
        private static readonly AsyncLocal<string?> Release = new AsyncLocal<string?>();
        internal static string ReleaseKey => Release.Value ?? McpServer.ReleaseKey;
        internal static IDisposable UseRelease(string release)
        {
            var previous = Release.Value; Release.Value = release;
            return new ReleaseScope(previous);
        }
        private sealed class ReleaseScope : IDisposable
        {
            private readonly string? previous;
            internal ReleaseScope(string? previous) { this.previous = previous; }
            public void Dispose() { Release.Value = previous; }
        }
        internal sealed class InputFailureException : Exception
        {
            internal Error Error { get; }
            internal InputFailureException(Error error) { Error = error; }
        }

        internal static AttributeMap<Scalar> EmptyAttributes() => new AttributeMap<Scalar>(new Dictionary<string, Scalar>());
        internal static InputContract<AttributeMap<Scalar>> Attributes(AttributeMap<Scalar>? values)
            => AttributeMapValidator.Hardware((values ?? EmptyAttributes()).Keys.ToDictionary(k => k,
                k => new AttributeRule(InputSchema.Scalar()), StringComparer.Ordinal));
        internal static string Cpu(CpuSettings settings)
        {
            // The shared Domain.CpuSettings contract owns the object shape. Hardware
            // key/count limits remain local, ahead of service lookup and native work.
            string json = V4Json.Serialize(settings);
            try { new InputBudget(characters: 32768).Check(V4Json.ParseInput(json)); }
            catch (InputRejectionException rejection) { throw new InputFailureException(rejection.ToError("settings")); }
            var values = new AttributeMap<Scalar>(settings.ExactAttributes);
            Input(Attributes(values), values, "settings.exactAttributes");
            return json;
        }
        internal static InputContract<AttributeMap<Scalar>> AddressProperties()
            => AttributeMapValidator.Hardware(new Dictionary<string, AttributeRule> {
                ["StartAddress"] = new AttributeRule(InputSchema.Integer(0)),
                ["Length"] = new AttributeRule(InputSchema.Integer(0)) });

        internal static string Input<T>(InputContract<T> contract, T value, string parameter)
        {
            var result = contract.Validate(value, parameter);
            if (!result.IsValid) throw new InputFailureException(result.Error!);
            return V4Json.Serialize(result.Value);
        }

        internal static void RequireProject(bool available)
        {
            if (!available) throw new InputFailureException(new Error("No project is bound.", new ProjectNotBoundDetails()));
        }

        internal static void Management(string action, string[] devicePath, string[] itemPath, string[] destinationDevicePath, int position,
            string typeIdentifier, string subnetName)
        {
            if (!new[] { "deleteDevice", "deleteItem", "moveItem", "copyItem", "changeType", "createSubnet", "deleteSubnet" }.Contains(action))
                throw new InputFailureException(McpServer.InvalidInput("action"));
            bool subnet = action == "createSubnet" || action == "deleteSubnet";
            if (subnet != (devicePath.Length == 0))
                throw new InputFailureException(McpServer.InvalidInput("devicePath"));
            if ((action == "deleteDevice" || subnet) != (itemPath.Length == 0))
                throw new InputFailureException(McpServer.InvalidInput("itemPath"));
            if ((action == "changeType" || action == "createSubnet") && string.IsNullOrWhiteSpace(typeIdentifier))
                throw new InputFailureException(McpServer.InvalidInput("typeIdentifier"));
            if (subnet && string.IsNullOrWhiteSpace(subnetName))
                throw new InputFailureException(McpServer.InvalidInput("subnetName"));
            if ((action == "moveItem" || action == "copyItem") && (destinationDevicePath.Length == 0 || position < 0))
                throw new InputFailureException(McpServer.InvalidInput("destinationDevicePath/position"));
        }

        internal static Error? AddressError(string ioType, int startAddress, bool allowNone)
        {
            var allowed = allowNone ? new[] { "None", "Input", "Output", "Diagnosis", "Substitute" }
                : new[] { "Input", "Output", "Diagnosis", "Substitute" };
            if (startAddress < 0) return McpServer.InvalidInput("startAddress");
            return allowed.Contains(allowNone ? ioType : ioType?.Trim(), allowNone ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
                ? null : McpServer.InvalidInput("ioType");
        }

        internal static void Address(string ioType, int startAddress, bool allowNone)
        {
            if (AddressError(ioType, startAddress, allowNone) is Error error) throw new InputFailureException(error);
        }

        internal static CallToolResult RejectAddress(Error error)
            => Result("SetDeviceItemIoAddress", null, error, Outcome.RejectedBeforeOperation, current: false);

        internal static CallToolResult Run(string tool, Func<ResponseMessage> action, bool write, bool current)
        {
            try { return Map(tool, action(), write, current); }
            catch (InputFailureException ex) { return Result(tool, null, ex.Error, Outcome.RejectedBeforeOperation, current); }
            catch (Exception ex)
            {
                if (HostBehavior.DevicePathRefusal(ex) is { } deviceRefusal)
                    return Result(tool, null, deviceRefusal, Outcome.RejectedBeforeOperation, current);
                if (TiaOpenness.Shared.HostFailurePolicy.Precondition(ex) is { } refusal)
                    return Result(tool, null, new Error(refusal.Message, refusal.IsArgument
                        ? McpServer.ArgumentDetails(tool, refusal.ParamName)
                        : refusal.Condition == "not-found" ? McpServer.MissingTargetDetails(tool, refusal.ParamName)
                            : new PreconditionFailedDetails("native-admission", null, refusal.ParamName)), Outcome.RejectedBeforeOperation, current);
                var cause = ex;
                while (cause.InnerException != null) cause = cause.InnerException;
                Error? error = cause is ArgumentException || cause is JsonException
                    ? new Error(McpServer.InvalidInput("").Message, McpServer.ArgumentDetails(tool, (cause as ArgumentException)?.ParamName))
                    : cause is PortalException portal && portal.Code == PortalErrorCode.NotFound
                        ? new Error(HostBehavior.SafeDiagnostic(portal.Message) ?? "The requested hardware object was not found.", new NotFoundDetails(null))
                    : cause is PortalException unavailable && unavailable.Code == PortalErrorCode.InvalidState && tool == "SearchHardwareCatalog"
                        ? new Error("The hardware catalog is unavailable.", new ResourceUnavailableDetails("HardwareCatalog"))
                    : cause is PortalException invalid && invalid.Code == PortalErrorCode.InvalidParams
                        ? new Error(McpServer.InvalidInput("").Message,
                            McpServer.ArgumentDetails(tool, TiaOpenness.Shared.HostFailurePolicy.Parameter(invalid)))
                    : cause is global::ModelContextProtocol.McpException { ErrorCode: global::ModelContextProtocol.McpErrorCode.InvalidParams } invalidArguments
                        ? new Error(HostBehavior.SafeDiagnostic(invalidArguments.Message) ?? "The hardware arguments are invalid.",
                            new InvalidArgumentDetails("", Array.Empty<string>()))
                    : cause is NotSupportedException ? UnsupportedCapabilityDetails.ErrorFor(ReleaseKey, tool, null, HostBehavior.SafeDiagnostic(cause.Message),
                        cause.Data["unsupportedBy"] as string)
                    : null;
                if (error != null && !write) return Result(tool, null, error, Outcome.RejectedBeforeOperation, current);
                if (tool == "ImportDeviceAml" && write && HostBehavior.WorkerReplyEvidence(tool, ex) is { } retained)
                    return Result(tool, null, AmlImportFailure(retained), Outcome.Unknown, current, write);
                return Result(tool, null, NativeFailureDiagnostics.FromException(Failure(write, new JsonObject()), ex),
                    write ? Outcome.Unknown : Outcome.ReadFailed, current);
            }
        }

        internal static CallToolResult Unsupported(string tool, Error error)
            => Result(tool, null, error, Outcome.RejectedBeforeOperation, current: true);

        internal static CallToolResult Map(string tool, ResponseMessage response, bool write, bool current)
        {
            var body = JsonNode.Parse(JsonSerializer.Serialize(response, response.GetType(),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }))!.AsObject();
            var evidence = body["meta"] as JsonObject ?? new JsonObject();
            body.Remove("meta");
            if (tool == "ImportDeviceAml" && Bool(evidence, "operationSuccess") == false) HostBehavior.SanitizeAmlDiagnostics(evidence);
            bool? verdict = Bool(evidence, "operationSuccess") ?? Bool(evidence, "success");
            if (body.ContainsKey("ok")) verdict = Bool(body, "ok");
            if (body.ContainsKey("success")) verdict = Bool(body, "success");
            if (Bool(evidence, "verified") == true || Bool(evidence, "feasible") == true) verdict = true;
            if (tool == "GetDevicePlugLocations" || tool == "GetDeviceItemIoAddresses") verdict = true;
            if (tool == "ExportDeviceAml" && ((int?)body["errorCount"] > 0 || (string?)body["state"] == "Error")) verdict = false;

            if (NativeResultState.State(evidence) != null && NativeResultState.EnumType(evidence, tool) != null)
                verdict = NativeResultState.Succeeded(evidence, tool) && verdict != false;
            bool unknown = write
                && (Bool(evidence, "writeOutcomeUnknown") == true || Bool(evidence, "verified") == false && Bool(evidence, "mayHaveChanged") != false
                    && Bool(evidence, "postStateKnown") != true
                || (string?)body["state"] == "Unknown" || (string?)evidence["reason"] == "VerifyFailed");
            int applied = (evidence["applied"] as JsonArray)?.Count ?? 0;
            int rejected = (evidence["rejected"] as JsonArray)?.Count ?? 0;
            bool issued = Bool(evidence, "mayHaveChanged") == true || Bool(evidence, "mayHaveWrittenFiles") == true;
            bool? started = Bool(evidence, "mayHaveChanged") ?? Bool(evidence, "mayHaveWrittenFiles");
            string? status = (string?)evidence["status"];
            bool incomplete = Bool(evidence, "dataComplete") == false || Bool(evidence, "readbackComplete") == false
                || tool == "GetDeviceAttributes"; // Attribute enumeration is explicitly best effort.

            // Legacy exceptions contain stack traces. Keep domain evidence, never their diagnostic rendering.
            Sanitize(body); Sanitize(evidence);
            body.Remove("message"); body.Remove("ok"); body.Remove("success");
            body["summary"] = verdict == true && !unknown ? response.Message : "Hardware operation did not establish a complete successful result.";
            evidence.Remove("timestamp"); evidence.Remove("tool");
            body["evidence"] = evidence;
            if (body["data"] is JsonObject nested)
            {
                body.Remove("data");
                foreach (var pair in nested) body[pair.Key] = pair.Value?.DeepClone();
            }
            if ((evidence["items"] ?? evidence["records"]) is JsonArray items) body["items"] = items.DeepClone();

            Outcome outcome;
            Error? error = null;
            if ((HostBehavior.DocumentedRefusal(evidence)
                ?? HostBehavior.KnownRefusal(evidence, verdict, ReleaseKey, tool, field => McpServer.MissingTargetDetails(tool, field))) is Error refusal)
            { outcome = Outcome.RejectedBeforeOperation; error = refusal; }
            else if (HostBehavior.UnchangedNativeFailure(evidence, verdict))
            { outcome = write ? Outcome.Failed : Outcome.ReadFailed; error = HostBehavior.UnchangedNativeError(evidence); }
            else if (unknown || write && verdict != true && (issued || !started.HasValue) && applied == 0
                && Bool(evidence, "postStateKnown") != true && status != "InvalidState" && status != "HmiReadSessionBlocked")
            {
                outcome = Outcome.Unknown;
                error = tool == "ImportDeviceAml"
                    ? AmlImportFailure(HostBehavior.WorkerReplyEvidence(evidence)) : Failure(true, evidence);
            }
            else if (write && applied > 0 && verdict != true)
            {
                outcome = Outcome.Partial; error =
                    new Error("CPU writes completed with rejected attributes or incomplete follow-up observations.",
                        new PartialFailureDetails(applied, Math.Max(1, rejected), 0));
            }
            else if (verdict == true) outcome = Outcome.Succeeded;
            else if (status == "InvalidState")
            { outcome = Outcome.RejectedBeforeOperation; error = new Error("No project is bound.", new ProjectNotBoundDetails()); }
            else if (status == "HmiReadSessionBlocked")
            { outcome = write ? Outcome.Unknown : Outcome.ReadFailed; error = HostBehavior.UnavailableCall(write); }
            else if (status == "NotFound" && !issued)
            { outcome = Outcome.RejectedBeforeOperation;
                error = new Error(HostBehavior.SafeDiagnostic(evidence["nativeMessage"]?.ToString()) ?? "The requested object was not found.",
                    new NotFoundDetails(evidence["missingDeviceTarget"]?.ToString())); }
            else if (status == "Ambiguous" && !issued)
            { outcome = Outcome.RejectedBeforeOperation;
                error = new Error(HostBehavior.SafeDiagnostic(evidence["nativeMessage"]?.ToString()) ?? "The device path is ambiguous.",
                    new TargetAmbiguousDetails(evidence["target"]?.ToString(),
                        (evidence["candidates"] as JsonArray)?.Select(c => c!.GetValue<string>()).ToArray() ?? Array.Empty<string>())); }
            else if (status == "InvalidParams" && started == false)
            { outcome = Outcome.RejectedBeforeOperation;
                error = new Error(McpServer.InvalidInput("").Message, McpServer.ArgumentDetails(tool, null, evidence)); }
            else if (write && started == false)
            {
                string? missing = tool == "SetDeviceItemAttribute" && evidence["availableAttributes"] is JsonArray attributes
                    && evidence["attributeName"] is JsonValue value && value.TryGetValue<string>(out var name)
                    && !attributes.Any(attribute => (string?)attribute == name) ? name : null;
                string message = missing == null ? "Hardware preconditions were not met."
                    : HostBehavior.SafeDiagnostic($"Attribute '{missing}' is absent from the device item's attribute list.") ?? "Device item attribute is missing.";
                outcome = Outcome.RejectedBeforeOperation;
                error = new Error(message, new PreconditionFailedDetails("hardware-admission", (string?)evidence["deviceItemPath"]));
            }
            else
            { outcome = write ? Outcome.Failed : Outcome.ReadFailed; error = Failure(false, evidence); }

            if (NativeResultState.TryUnsuccessful(evidence, write, out var nativeOutcome, out var nativeError, tool))
                return Result(tool, body, nativeError, nativeOutcome, current, write, nativeOutcome == Outcome.Unknown ? Completeness.Unknown
                        : nativeOutcome == Outcome.Partial || tool == "ImportDeviceAml" && nativeOutcome == Outcome.Failed && incomplete
                            ? Completeness.Partial : Completeness.Complete);
            Paging? paging = null;
            if (evidence["offset"] != null && evidence["limit"] != null && (evidence["total"] ?? evidence["expectedCount"]) != null)
                paging = McpServer.OffsetPage((int)evidence["offset"]!, (int)evidence["limit"]!, (int)(evidence["total"] ?? evidence["expectedCount"])!);
            if (tool == "SearchHardwareCatalog" && evidence["offset"] != null && evidence["limit"] != null)
            {
                int? next = (int?)evidence["nextOffset"];
                paging = new Paging(PagingMode.Offset, (int)evidence["offset"]!, (int)evidence["limit"]!, next, null, null, null, next == null);
            }
            return Result(tool, body, error, outcome, current, write,
                outcome == Outcome.Unknown ? Completeness.Unknown : outcome == Outcome.RejectedBeforeOperation ? Completeness.None
                : incomplete || outcome == Outcome.Partial ? Completeness.Partial : outcome == Outcome.Succeeded ? Completeness.Complete : Completeness.None,
                paging, incomplete, tool == "BuildDeviceAmlDocument" && verdict == true);
        }

        private static bool? Bool(JsonObject value, string name)
            => value[name] is JsonValue node && node.TryGetValue<bool>(out var result) ? result : (bool?)null;
        private static void Sanitize(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                foreach (string key in new[] { "error", "exception", "stackTrace", "lastFailure", "exceptionMessageData" }) obj.Remove(key);
                foreach (var pair in obj.ToArray()) if (pair.Value != null) Sanitize(pair.Value);
            }
            else if (node is JsonArray array) foreach (var item in array) if (item != null) Sanitize(item);
        }
        private static Error AmlImportFailure(IReadOnlyDictionary<string, JsonElement> evidence)
        {
            var retained = JsonSerializer.SerializeToNode(evidence)!.AsObject();
            HostBehavior.SanitizeEvidence(retained);
            return new Error("Inspect the device and network configuration before repeating the AML import.",
                new OutcomeUnknownDetails("hardware-operation", retained.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value))));
        }

        private static Error Failure(bool unknown, JsonObject evidence)
        {
            var details = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(evidence.ToJsonString())!;
            return unknown
                ? new Error("The write outcome cannot be confirmed. Rebuild the session and inspect the target before continuing.",
                    new OutcomeUnknownDetails("hardware-operation", details.Where(p => p.Key is not ("nativeMessage" or "workerMessage" or "readError"))
                        .ToDictionary(p => p.Key, p => p.Value)))
                : new Error("The hardware operation did not succeed.", new NativeOperationFailedDetails(null, null, details));
        }

        private static CallToolResult Result(string tool, JsonObject? data, Error? error, Outcome outcome, bool current,
            bool write = false, Completeness? completeness = null, Paging? paging = null, bool incomplete = false, bool candidate = false)
        {
            var warnings = new List<Warning>();
            var empty = new Dictionary<string, JsonElement>();
            var policy = current ? BehaviorCapabilities.EntryPolicy(typeof(HardwareContract).Assembly, ReleaseKey, tool, BehaviorPolicy.Current)
                : BehaviorPolicy.NotApplicable;
            if (data?["evidence"] is JsonObject nativeEvidence) warnings.AddRange(NativeResultState.Warnings(nativeEvidence, tool));
            if (policy == BehaviorPolicy.Current) warnings
                .Add(new Warning(WarningCode.UnverifiedBehavior, BehaviorCapabilities.CurrentDisclosure, empty));
            if (incomplete) warnings
                .Add(new Warning(WarningCode.IncompleteData, "The returned observation is incomplete; consult the retained evidence.", empty));
            if (candidate) warnings
                .Add(new Warning(WarningCode.CandidateOnly, "The AML document is a candidate; successful import has not been verified.", empty));
            var execution = outcome == Outcome.RejectedBeforeOperation ? Execution.NotStarted : outcome == Outcome.Unknown ? Execution.Unknown
                : outcome == Outcome.Partial ? Execution.Partial : write ? Execution.Completed : Execution.ReadOnly;
            var meta =
                new Meta(DateTimeOffset.UtcNow, ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), outcome, execution, policy, completeness
                    ?? (outcome == Outcome.Unknown ? Completeness.Unknown : Completeness.None), paging, warnings);
            var result = McpResult.From(BehaviorCapabilities.Disclose(Envelope.Create(data, error, meta)));
            return new CallToolResult { IsError = result.IsError, StructuredContent = JsonNode.Parse(result.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = result.Content[0].Text } } };
        }
    }
}
