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
        private static string ReleaseKey => Release.Value ?? McpServer.ReleaseKey;
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
        private sealed class InputFailure : Exception
        {
            internal Error Error { get; }
            internal InputFailure(Error error) { Error = error; }
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
            catch (InputRejection rejection) { throw new InputFailure(rejection.ToError("settings")); }
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
            if (!result.IsValid) throw new InputFailure(result.Error!);
            return V4Json.Serialize(result.Value);
        }

        internal static void RequireProject(bool available)
        {
            if (!available) throw new InputFailure(new Error("No project is bound.", new ProjectNotBoundDetails()));
        }

        internal static void Management(string action, string[] itemPath, string[] destinationDevicePath, int position)
        {
            if (!new[] { "deleteDevice", "deleteItem", "moveItem", "copyItem" }.Contains(action))
                throw new InputFailure(McpServer.InvalidInput("action"));
            if ((action == "deleteDevice") != (itemPath.Length == 0))
                throw new InputFailure(McpServer.InvalidInput("itemPath"));
            if ((action == "moveItem" || action == "copyItem") && (destinationDevicePath.Length == 0 || position < 0))
                throw new InputFailure(McpServer.InvalidInput("destinationDevicePath/position"));
        }

        internal static void Address(string ioType, int startAddress, bool allowNone)
        {
            var allowed = allowNone ? new[] { "None", "Input", "Output", "Diagnosis", "Substitute" }
                : new[] { "Input", "Output", "Diagnosis", "Substitute" };
            if (startAddress < 0 || !allowed.Contains(allowNone ? ioType : ioType?.Trim(),
                allowNone ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase))
                throw new InputFailure(McpServer.InvalidInput("ioType/startAddress"));
        }

        internal static CallToolResult Run(string tool, Func<ResponseMessage> action, bool write, bool current)
        {
            try { return Map(tool, action(), write, current); }
            catch (InputFailure ex) { return Result(tool, null, ex.Error, Outcome.RejectedBeforeOperation, current); }
            catch (Exception ex)
            {
                if (TiaOpenness.Shared.HostFailurePolicy.Precondition(ex) is { } refusal)
                    return Result(tool, null, new Error(refusal.Message, refusal.IsArgument
                        ? new InvalidArgumentDetails(refusal.ParamName ?? "arguments", Array.Empty<string>())
                        : new PreconditionFailedDetails("native-admission", null, refusal.ParamName)), Outcome.RejectedBeforeOperation, current);
                var cause = ex;
                while (cause.InnerException != null) cause = cause.InnerException;
                Error? error = cause is ArgumentException || cause is JsonException ? McpServer.InvalidInput("arguments")
                    : cause is PortalException portal && portal.Code == PortalErrorCode.NotFound
                        ? new Error("The requested hardware object was not found.", new NotFoundDetails(null))
                    : cause is PortalException unavailable && unavailable.Code == PortalErrorCode.InvalidState && tool == "SearchHardwareCatalog"
                        ? new Error("The hardware catalog is unavailable.", new ResourceUnavailableDetails("HardwareCatalog"))
                    : cause is PortalException invalid && invalid.Code == PortalErrorCode.InvalidParams ? McpServer.InvalidInput("arguments")
                    : cause is NotSupportedException ? new Error("This capability is unavailable.", new UnsupportedCapabilityDetails(ReleaseKey, tool, null))
                    : null;
                if (error != null && !write) return Result(tool, null, error, Outcome.RejectedBeforeOperation, current);
                return Result(tool, null, Failure(write, new JsonObject()), write ? Outcome.Unknown : Outcome.ReadFailed, current);
            }
        }

        internal static CallToolResult Map(string tool, ResponseMessage response, bool write, bool current)
        {
            var body = JsonNode.Parse(JsonSerializer.Serialize(response, response.GetType(),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }))!.AsObject();
            var evidence = body["meta"] as JsonObject ?? new JsonObject();
            body.Remove("meta");
            bool? verdict = Bool(evidence, "operationSuccess") ?? Bool(evidence, "success");
            if (body.ContainsKey("ok")) verdict = Bool(body, "ok");
            if (body.ContainsKey("success")) verdict = Bool(body, "success");
            if (Bool(evidence, "verified") == true || Bool(evidence, "feasible") == true) verdict = true;
            if (tool == "GetDevicePlugLocations" || tool == "GetDeviceItemIoAddresses") verdict = true;
            if (tool == "ExportDeviceAml" && ((int?)body["errorCount"] > 0 || (string?)body["state"] == "Error")) verdict = false;

            bool unknown = write && (Bool(evidence, "writeOutcomeUnknown") == true || Bool(evidence, "verified") == false && Bool(evidence, "mayHaveChanged") != false && Bool(evidence, "postStateKnown") != true
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
            body["evidence"] = evidence.DeepClone();
            if (body["data"] is JsonObject nested)
            {
                body.Remove("data");
                foreach (var pair in nested) body[pair.Key] = pair.Value?.DeepClone();
            }
            if ((evidence["items"] ?? evidence["records"]) is JsonArray items) body["items"] = items.DeepClone();

            Outcome outcome;
            Error? error = null;
            if (unknown || write && verdict != true && (issued || !started.HasValue) && applied == 0
                && Bool(evidence, "postStateKnown") != true && status != "InvalidState" && status != "HmiReadSessionBlocked" && status != "NotFound")
            { outcome = Outcome.Unknown; error = Failure(true, evidence); }
            else if (write && applied > 0 && verdict != true)
            { outcome = Outcome.Partial; error = new Error("CPU writes completed with rejected attributes or incomplete follow-up observations.", new PartialFailureDetails(applied, Math.Max(1, rejected), 0)); }
            else if (verdict == true) outcome = Outcome.Succeeded;
            else if (status == "InvalidState")
            { outcome = Outcome.RejectedBeforeOperation; error = new Error("No project is bound.", new ProjectNotBoundDetails()); }
            else if (status == "HmiReadSessionBlocked")
            { outcome = Outcome.RejectedBeforeOperation; error = new Error("The session requires an explicit reset.", new SessionResetRequiredDetails("hardware-read-blocked")); }
            else if (status == "NotFound")
            { outcome = Outcome.RejectedBeforeOperation; error = new Error("The requested object was not found.", new NotFoundDetails(null)); }
            else if (status == "InvalidParams" && started == false)
            { outcome = Outcome.RejectedBeforeOperation; error = McpServer.InvalidInput("arguments"); }
            else if (write && started == false)
            { outcome = Outcome.RejectedBeforeOperation; error = new Error("Hardware preconditions were not met.", new PreconditionFailedDetails("hardware-admission", null)); }
            else
            { outcome = write ? Outcome.Failed : Outcome.ReadFailed; error = Failure(false, evidence); }

            if (NativeResultState.TryFailure(evidence, write, out var nativeOutcome, out var nativeError))
                return Result(tool, body, nativeError, nativeOutcome, current, write, nativeOutcome == Outcome.Unknown ? Completeness.Unknown : Completeness.Complete);
            Paging? paging = null;
            if (evidence["offset"] != null && evidence["limit"] != null && (evidence["total"] ?? evidence["expectedCount"]) != null)
                paging = McpServer.OffsetPage((int)evidence["offset"]!, (int)evidence["limit"]!, (int)(evidence["total"] ?? evidence["expectedCount"])!);
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
        private static Error Failure(bool unknown, JsonObject evidence)
        {
            var details = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(evidence.ToJsonString())!;
            return unknown ? new Error("The write outcome cannot be confirmed. Rebuild the session and inspect the target before continuing.", new OutcomeUnknownDetails("hardware-operation", details))
                : new Error("The hardware operation did not succeed.", new NativeOperationFailedDetails(null, null, details));
        }

        private static CallToolResult Result(string tool, JsonObject? data, Error? error, Outcome outcome, bool current,
            bool write = false, Completeness? completeness = null, Paging? paging = null, bool incomplete = false, bool candidate = false)
        {
            var warnings = new List<Warning>();
            var empty = new Dictionary<string, JsonElement>();
            var policy = current ? BehaviorCapabilities.EntryPolicy(typeof(HardwareContract).Assembly, ReleaseKey, tool, BehaviorPolicy.Current) : BehaviorPolicy.NotApplicable;
            if (policy == BehaviorPolicy.Current) warnings.Add(new Warning(WarningCode.UnverifiedBehavior, "Native behavior retains the current policy; V4 native acceptance is pending.", empty));
            if (incomplete) warnings.Add(new Warning(WarningCode.IncompleteData, "The returned observation is incomplete; consult the retained evidence.", empty));
            if (candidate) warnings.Add(new Warning(WarningCode.CandidateOnly, "The AML document is a candidate; successful import has not been verified.", empty));
            var execution = outcome == Outcome.RejectedBeforeOperation ? Execution.NotStarted : outcome == Outcome.Unknown ? Execution.Unknown
                : outcome == Outcome.Partial ? Execution.Partial : write ? Execution.Completed : Execution.ReadOnly;
            var meta = new Meta(DateTimeOffset.UtcNow, ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), outcome, execution,
                outcome == Outcome.Unknown, policy, completeness ?? (outcome == Outcome.Unknown ? Completeness.Unknown : Completeness.None), paging, warnings);
            var result = McpResult.From(Envelope.Create(data, error, meta));
            return new CallToolResult { IsError = result.IsError, StructuredContent = JsonNode.Parse(result.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = result.Content[0].Text } } };
        }
    }
}
