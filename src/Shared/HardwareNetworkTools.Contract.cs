using System.Text.Json;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcpServer.Siemens;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol
{
    internal sealed class HardwareProbeEvidence : List<string>
    {
        internal string? FailureCode { get; set; }
        internal string? Target { get; set; }
    }

    // This boundary is exclusive to the hardware network/service tools. Native work
    // stays in the existing services; validators run before entering those services.
    internal static class HardwareToolContract
    {
        internal static readonly InputContract<AttributeMap<Scalar>> Attributes = new InputContract<AttributeMap<Scalar>>(
            InputSchema.Map(InputSchema.Scalar(), maximum: 50,
                keys: InputSchema.String(minLength: 1, pattern: "^[\\p{L}\\p{Nd}_]+$")), new InputBudget(characters: 32768));

        private sealed class InputFailureException : Exception
        {
            internal Error Error { get; }
            internal InputFailureException(Error error) { Error = error; }
        }

        internal static string Json<T>(T value, string parameter, InputContract<T> contract)
        {
            var result = contract.Validate(value, parameter);
            if (result.Error != null) throw new InputFailureException(result.Error);
            return V4Json.Serialize(result.Value);
        }

        internal static string Path(string[] value, string parameter, bool rootAllowed, bool optional)
            => Json(value ?? (optional ? Array.Empty<string>() : null!), parameter, PathValidator.Create(rootAllowed));

        internal static string Names(string[] value, string parameter, int maximum, bool optional)
            => Json(value ?? (optional ? Array.Empty<string>() : null!), parameter, NameListValidator.Hardware(maximum));

        internal static string Map(AttributeMap<Scalar> value, string parameter, bool optional)
            => Json(value ?? (optional ? new AttributeMap<Scalar>(new Dictionary<string, Scalar>()) : null!), parameter, Attributes);

        // Only value-free argument checks opt into a diagnostic from the validator.
        internal static void Check(Action validation, bool describeArgument = false, string? parameter = null)
        {
            try { validation(); }
            catch (ArgumentException error) /* swallow(privacy): legacy validators can include input values in their messages */
            {
                var refusal = McpServer.InvalidInput(error.ParamName ?? parameter ?? "");
                throw new InputFailureException(describeArgument
                    ? new Error(HostBehavior.SafeDiagnostic(error.Message) ?? refusal.Message, refusal.Details) : refusal);
            }
            catch (NotSupportedException) /* swallow(privacy): keep capability refusals separate from parser diagnostics */
            {
                throw new InputFailureException(new Error("The requested hardware operation is unavailable.",
                            new UnsupportedCapabilityDetails(HardwareContract.ReleaseKey, "hardware", null)));
            }
        }

        internal static void Confirm(bool dryRun, bool confirmed, string? parameter = null)
        {
            if (!dryRun && !confirmed)
                throw new InputFailureException(new Error(parameter == null ? "Explicit confirmation is required for this hardware change."
                    : "Set " + parameter + "=true to confirm this hardware change.", new ConfirmationRequiredDetails(null)));
        }

        internal static CallToolResult Unsupported(string tool, Error error)
            => Result(tool, null, error, Outcome.RejectedBeforeOperation, Execution.NotStarted, Completeness.None, true);

        internal static CallToolResult Invoke(string tool, bool readOnly, bool current, Func<ResponseMessage> operation)
        {
            try { return MapResult(tool, operation(), readOnly, current); }
            catch (InputFailureException failure) {
                return Result(tool, null, failure.Error, Outcome.RejectedBeforeOperation, Execution.NotStarted, Completeness.None, current); }
            catch (Exception error) /* swallow(privacy): an escaped native error has no confirmed write outcome */
            {
                if (HostBehavior.DevicePathRefusal(error) is { } refusal)
                    return Result(tool, null, refusal, Outcome.RejectedBeforeOperation, Execution.NotStarted, Completeness.None, current);
                return Result(tool, null, readOnly ? NativeFailureDiagnostics.FromException(NativeFailure(), error)
                    : UnknownFailure(HostBehavior.WorkerReplyEvidence(tool, error)),
                    readOnly ? Outcome.ReadFailed : Outcome.Unknown,
                    readOnly ? Execution.ReadOnly : Execution.Unknown, Completeness.Unknown, current);
            }
        }

        private static bool? Flag(JsonObject data, string key)
            => data[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : (bool?)null;

        internal static CallToolResult MapResult(string tool, ResponseMessage response, bool readOnly, bool current)
        {
            var evidence = response.Meta == null ? new JsonObject() : (JsonObject)response.Meta.DeepClone();
            if (tool.StartsWith("ProbeHardwareHmiConnection", StringComparison.Ordinal) && evidence["target"] == null)
                evidence.Remove("target");
            var data = response is ResponseJsonReport report && report.Data != null ? (JsonObject)report.Data.DeepClone() : new JsonObject();
            HostBehavior.RemoveResponseCredentials(data);
            HostBehavior.RemoveResponseCredentials(evidence);
            bool? success = evidence.ContainsKey("operationSuccess") ? Flag(evidence, "operationSuccess") : Flag(evidence, "success");
            if (response is ResponseJsonReport verdict) success = verdict.Ok ?? success;
            foreach (var pair in data) if (!evidence.ContainsKey(pair.Key)) evidence[pair.Key] = pair.Value?.DeepClone();
            if (NativeResultState.State(evidence) != null && NativeResultState.EnumType(evidence, tool) != null)
                success = NativeResultState.Succeeded(evidence, tool) && success != false;
            if (tool == "CompileDevice" && !NativeResultState.TryUnsuccessful(evidence, !readOnly, out _, out _, tool)
                && CompileResultMapping.Errors(new JsonObject { ["evidence"] = evidence.DeepClone() }) is Error compileError)
            {
                data["evidence"] = evidence;
                return Result(tool, data, compileError, Outcome.Failed, Execution.Completed, Incomplete(evidence) ? Completeness.Partial : Completeness
                        .Complete, current);
            }

            // The topology lists root, grouped and ungrouped devices; an empty list
            // is still an observed list, while an unbound project has no such list.
            if (tool == "GetProjectTopology" && data["devices"] is JsonArray) success = true;
            if (tool == "SetPlcPutGetAccess" && Flag(evidence, "writeOutcomeKnown") != true
                && Flag(evidence, "mayHaveChanged") == true) success = null;
            if (tool == "CompileDevice" && evidence["effectiveState"] != null)
                success = TiaMcp.Adapters.Contracts.NativeResultStates
                    .Succeeded(TiaMcp.Adapters.Contracts.NativeResultStates.Compiler, evidence["effectiveState"]!.ToString());

            bool issued = Flag(evidence, "mayHaveChanged") == true || Flag(evidence, "mayHaveWrittenFiles") == true;
            bool nestedUnknown = evidence["ensureSubnet"] is JsonObject ensure && Flag(ensure, "mayHaveChanged") == true && Flag(ensure, "success") != true;
            if (!readOnly && (Flag(evidence, "writeOutcomeUnknown") == true)
                && issued) nestedUnknown = true;
            bool nestedCompleted = evidence["ensureSubnet"] is JsonObject completed && Flag(completed, "mayHaveChanged") == true
                && Flag(completed, "success") == true;
            bool knownFailure = tool == "CompileDevice" && Flag(evidence, "nativeCompleted") == true && evidence["effectiveState"]?.ToString() == "Error"
                || tool == "SetPlcPutGetAccess" && Flag(evidence, "writeOutcomeKnown") == true;
            bool knownPartial = nestedCompleted && Flag(evidence, "mayHaveChanged") == false
                || evidence["file"] is JsonObject && Flag(evidence, "mayHaveWrittenFiles") == true && Flag(evidence, "mayHaveChanged") == false;
            string status = evidence["failureCode"]?.ToString() ?? evidence["status"]?.ToString() ?? "";
            bool fileExport = tool == "ManageHardwareUtilities" && (string?)evidence["action"] is "exportOpcUa" or "exportCardReaderPsc";
            Outcome outcome; Execution execution; Error? error = null;
            if (!issued && status == "CONNECTION_UNAVAILABLE" && Flag(evidence, "connectionUnavailable") == true)
            {
                outcome = Outcome.RejectedBeforeOperation; execution = Execution.NotStarted;
                error =
                    new Error("The portal connection was unavailable before the operation started.", new PreconditionFailedDetails("portal-connected", null));
            }
            else if ((HostBehavior.DocumentedRefusal(evidence)
                ?? HostBehavior
                    .KnownRefusal(evidence, success, HardwareContract.ReleaseKey, tool, field => McpServer.MissingTargetDetails(tool, field))) is Error refusal)
            { outcome = Outcome.RejectedBeforeOperation; execution = Execution.NotStarted; error = refusal; }
            else if (HostBehavior.UnchangedNativeFailure(evidence, success, allowWrittenFiles: fileExport))
            { outcome = readOnly ? Outcome.ReadFailed : Outcome.Failed; execution = readOnly ? Execution.ReadOnly : Execution.Completed; error =
                HostBehavior.UnchangedNativeError(evidence, fileExport: fileExport); }
            else if (tool == "ConnectDeviceNodesToProfinetSubnet" && status == "PROJECT_NOT_BOUND" && !issued)
            { outcome = Outcome.RejectedBeforeOperation; execution = Execution.NotStarted; error = new Error("No project is bound.",
                new ProjectNotBoundDetails()); }
            else if (nestedUnknown || !readOnly && success != true && !knownFailure && !knownPartial && issued)
            { outcome = Outcome.Unknown; execution = Execution.Unknown; error = UnknownFailure(HostBehavior.WorkerReplyEvidence(evidence)); }
            else if (!readOnly && success != true && knownPartial)
            {
                outcome = Outcome.Partial; execution = Execution.Partial; error =
                    new Error("A confirmed hardware side effect remains after the operation failed.", new PartialFailureDetails(0, 0, 0));
            }
            else if (!readOnly && success != true && knownFailure)
            { outcome = Outcome.Failed; execution = Execution.Completed; error = NativeFailure(); }
            else if (status == "PROJECT_NOT_BOUND" || status == "InvalidState")
            {
                outcome = Outcome.RejectedBeforeOperation; execution = Execution.NotStarted; error =
                    new Error("No project is bound.", new ProjectNotBoundDetails());
            }
            else if (status == "HmiReadSessionBlocked")
            {
                outcome = readOnly ? Outcome.ReadFailed : Outcome.Unknown;
                execution = readOnly ? Execution.ReadOnly : Execution.Unknown;
                error = HostBehavior.UnavailableCall(!readOnly);
            }
            else if (readOnly && !issued && status is ("NotSupportedOnVersion" or "UNSUPPORTED_CAPABILITY") && (string?)evidence["unsupportedBy"] == "target")
            {
                outcome = Outcome.RejectedBeforeOperation; execution = Execution.NotStarted;
                error = UnsupportedCapabilityDetails.TargetErrorFor(HardwareContract.ReleaseKey, tool, null,
                    HostBehavior.SafeDiagnostic((string?)evidence["nativeMessage"] ?? response.Message));
            }
            else if (status == "NOT_FOUND" || status == "UNSUPPORTED_CAPABILITY" || status == "INVALID_ARGUMENT")
            {
                outcome = Outcome.RejectedBeforeOperation; execution = Execution.NotStarted;
                error = status == "NOT_FOUND" ? new Error("The hardware target was not found.", new NotFoundDetails((string?)evidence["target"]))
                    : status == "UNSUPPORTED_CAPABILITY"
                        ? new Error(HostBehavior.SafeDiagnostic(response.Message) ?? "The hardware capability is unavailable.",
                            new UnsupportedCapabilityDetails(HardwareContract.ReleaseKey, tool, null))
                    : new Error(McpServer.InvalidInput("").Message, McpServer.ArgumentDetails(tool, null, evidence));
            }
            else if (success == true)
            { outcome = Outcome.Succeeded; execution = readOnly ? Execution.ReadOnly : Execution.Completed; }
            else if (readOnly)
            { outcome = Outcome.ReadFailed; execution = Execution.ReadOnly; error = NativeFailure(); }
            else if (Flag(evidence, "mayHaveChanged") == false)
            {
                outcome = Outcome.RejectedBeforeOperation; execution = Execution.NotStarted; error =
                    new Error("Hardware preconditions were not satisfied.", new PreconditionFailedDetails("hardware-before-write", null));
            }
            else
            { outcome = Outcome.Unknown; execution = Execution.Unknown; error = UnknownFailure(HostBehavior.WorkerReplyEvidence(evidence)); }

            if (fileExport && error != null && Flag(evidence, "mayHaveWrittenFiles") == true)
                error = new Error(HostBehavior.PartialExportHint + " " + error.Message, error.Details, error.Innererror);

            if (response is ResponseStringList list) data["items"] =
                new JsonArray((list.Items ?? Enumerable.Empty<string>()).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
            if (response is ResponseNetworkInfo network)
            {
                data["deviceItemName"] = network.DeviceItemName;
                data["attributes"] = JsonNode.Parse(V4Json.Serialize(network.Attributes));
                data["selection"] = "attribute-name heuristic";
            }
            foreach (var pair in evidence)
                if (!data.ContainsKey(pair.Key) && pair.Key != "timestamp" && pair.Key != "tool" && pair.Key != "success") data[pair.Key] = pair.Value?
                    .DeepClone();
            data.Remove("message");
            if (outcome == Outcome.Succeeded) data["summary"] = response.Message;
            bool incomplete = Incomplete(data)
                || response is ResponseNetworkInfo && data["attributes"] is JsonArray networkAttributes
                    && networkAttributes.Any(attribute => attribute?["value"] == null)
                || tool == "GetProjectTopology"
                || tool.StartsWith("ProbeHardwareHmiConnection", StringComparison.Ordinal);
            Paging? paging = null;
            if (data["offset"] is JsonValue offset && offset.TryGetValue<int>(out var start)
                && data["limit"] is JsonValue limit && limit.TryGetValue<int>(out var size)
                && data["expectedCount"] is JsonValue count && count.TryGetValue<int>(out var total) && size > 0 && start >= 0)
                paging = McpServer.OffsetPage(start, size, total);
            foreach (string key in new[] { "timestamp", "tool", "success", "offset", "limit", "nextOffset" }) data.Remove(key);
            Sanitize(data);
            if (NativeResultState.TryUnsuccessful(evidence, !readOnly, out var nativeOutcome, out var nativeError, tool))
                return Result(tool, data, nativeError, nativeOutcome, nativeOutcome == Outcome.Unknown ? Execution.Unknown : readOnly ? Execution.ReadOnly
                        : nativeOutcome == Outcome.Partial ? Execution.Partial : Execution.Completed,
                    nativeOutcome == Outcome.Unknown ? Completeness.Unknown : nativeOutcome == Outcome.Partial ? Completeness.Partial : Completeness.Complete,
                        current);
            var completeness = outcome == Outcome.RejectedBeforeOperation ? Completeness.None
                : outcome == Outcome.Unknown || outcome == Outcome.ReadFailed ? Completeness.Unknown
                : incomplete ? Completeness.Partial : Completeness.Complete;
            return Result(tool, data, error, outcome, execution, completeness, current, paging);
        }

        private static bool Incomplete(JsonNode? node)
        {
            if (node is JsonArray array) return array.Any(Incomplete);
            if (!(node is JsonObject obj)) return false;
            return Flag(obj, "dataComplete") == false || Flag(obj, "fullObjectComplete") == false || Flag(obj, "truncated") == true
                || Flag(obj, "incomplete") == true || Flag(obj, "diagnosticsComplete") == false
                || obj.Any(p => (p.Key == "failures" || p.Key == "excludedComplexProperties") && p.Value is JsonArray failures && failures.Count > 0)
                || obj.Any(p => p.Key.EndsWith("Error", StringComparison.Ordinal) && p.Value != null || Incomplete(p.Value));
        }

        private static void Sanitize(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var item in array) Sanitize(item); return; }
            if (!(node is JsonObject obj)) return;
            foreach (var pair in obj.ToArray())
            {
                if (pair.Value == null) continue;
                if (pair.Key == "error" || pair.Key.EndsWith("Error", StringComparison.Ordinal)) obj[pair.Key] =
                    "Native observation failed; details are available in the local diagnostic log.";
                else if (pair.Key == "nativeMessage") obj[pair.Key] = HostBehavior.SafeDiagnostic(pair.Value?.ToString());
                else if (pair.Key == "messageData" || pair.Key == "detailMessageData" || pair.Key == "lastFailure") obj.Remove(pair.Key);
                else if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
                    obj[pair.Key] = Regex.Replace(text, @"(?m)\r?\n[ \t]+(?:at |\p{Lo} ).*$", "");
                else Sanitize(pair.Value);
            }
        }

        private static Error NativeFailure() => new Error("The hardware operation did not establish a successful result.",
            new NativeOperationFailedDetails(null, null, new Dictionary<string, JsonElement>()));
        private static Error UnknownFailure(IReadOnlyDictionary<string, JsonElement>? evidence = null) =>
            new Error("The hardware write outcome is unknown. Inspect the target before repeating this call. Never replay it automatically. GetSessionState " +
                "lists unsettled request IDs.",
            new OutcomeUnknownDetails("hardware-operation", evidence ?? new Dictionary<string, JsonElement>()));

        private static CallToolResult Result(string tool, JsonObject? data, Error? error, Outcome outcome, Execution execution,
            Completeness completeness, bool current, Paging? paging = null)
        {
            var warnings = new List<Warning>();
            if (data != null) warnings.AddRange(NativeResultState.Warnings(data, tool));
            if (current) warnings
                .Add(new Warning(WarningCode.UnverifiedBehavior, BehaviorCapabilities.CurrentDisclosure,
                        new Dictionary<string, JsonElement>()));
            if (HostBehavior.ReadbackWarning(data, outcome) is Warning warning) warnings.Add(warning);
            else if (completeness == Completeness.Partial) warnings
                .Add(new Warning(WarningCode.IncompleteData, "The hardware observation covers only the reported scope and available fields.",
                        new Dictionary<string, JsonElement>()));
            var meta =
                new Meta(DateTimeOffset.UtcNow, HardwareContract.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), outcome, execution, current
                    ? BehaviorPolicy.Current : BehaviorPolicy.NotApplicable,
                    completeness, paging, warnings);
            var mapped = McpResult.From(BehaviorCapabilities.Disclose(Envelope.Create(data, error, meta)));
            return new CallToolResult { IsError = mapped.IsError, StructuredContent = JsonNode.Parse(mapped.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = mapped.Content[0].Text } } };
        }
    }

}
