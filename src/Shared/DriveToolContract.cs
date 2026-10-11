using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4.Domain;
using ModelContextProtocol.Protocol;
using TiaMcpServer.Siemens;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol
{
    internal static class DriveToolContract
    {
        private sealed class InputFailure : Exception
        {
            internal Error Error { get; }
            internal InputFailure(Error error) => Error = error;
        }

        private static T Input<T>(InputContract<T> contract, T value, string parameter)
        {
            var result = contract.Validate(value, parameter);
            if (result.Error != null) throw new InputFailure(result.Error);
            return result.Value!;
        }

        internal static string Path(string[] value, string parameter, bool optional)
            => V4Json.Serialize(Input(PathValidator.Create(parameter == "itemPath" || optional),
                value ?? (optional ? Array.Empty<string>() : null!), parameter));

        internal static string Attributes(AttributeMap<Scalar>? value)
            => V4Json.Serialize(value ?? new AttributeMap<Scalar>(new Dictionary<string, Scalar>()));

        internal static string ParameterValue(NativeValue value, string action)
        {
            if (action == "write") value = Input(NativeValueValidator.Create(NativeValueValidator.DriveParameter()), value, "value");
            return value.Kind == JsonValueKind.Undefined ? "null" : V4Json.Serialize(value);
        }

        internal static string FunctionValue(NativeValue value, string action)
        {
            if (value.Kind == JsonValueKind.Undefined) value = V4Json.Deserialize<NativeValue>("{}");
            NativeValuePolicy? policy = null;
            Check(() => policy = DriveFunctionPolicy.Create(action));
            return V4Json.Serialize(Input(NativeValueValidator.Create(policy!), value, "value"));
        }

        internal static void Check(Action validation)
        {
            try { validation(); }
            catch (ArgumentException) /* swallow(privacy): parser messages may contain client values or credentials */
            { throw new InputFailure(McpServer.InvalidInput("arguments")); }
        }

        internal static CallToolResult Run(string tool, bool writes, bool current, Func<ResponseMessage> operation,
            int? offset = null, int? limit = null)
        {
            try { return Map(tool, operation(), writes, current, offset, limit); }
            catch (InputFailure ex)
            { return Result(tool, null, ex.Error, Outcome.RejectedBeforeOperation, Completeness.None, writes, current); }
            catch (Exception) /* swallow(privacy): an escaped exception cannot establish the outcome of an issued write */
            { return Result(tool, null, writes ? Unknown() : NativeFailure(), writes ? Outcome.Unknown : Outcome.ReadFailed, Completeness.Unknown, writes, current); }
        }

        private static bool? Flag(JsonObject value, string key)
            => value[key] is JsonValue node && node.TryGetValue<bool>(out var flag) ? flag : (bool?)null;

        internal static CallToolResult Map(string tool, ResponseMessage response, bool writes, bool current,
            int? offset = null, int? limit = null)
        {
            var evidence = response.Meta == null ? new JsonObject() : (JsonObject)response.Meta.DeepClone();
            bool? success = Flag(evidence, "operationSuccess") ?? Flag(evidence, "success");
            if (Flag(evidence, "success") == false || Flag(evidence, "result") == false) success = false;
            bool issued = Flag(evidence, "mayHaveChanged") == true || Flag(evidence, "mayHaveWrittenFiles") == true;
            bool reset = Flag(evidence, "requiresExplicitRebind") == true || Flag(evidence, "requiresSessionReset") == true;
            bool knownFalse = Flag(evidence, "result") == false && Flag(evidence, "apiCallSuccess") != false;
            bool unknown = writes && (reset && issued || Flag(evidence, "verified") == false || Flag(evidence, "verifiedAbsent") == false);
            int applied = (evidence["appliedEntries"] as JsonArray)?.Count ?? 0;
            string status = evidence["status"]?.ToString() ?? "";
            bool projectMissing = status == "InvalidState" && !evidence.ContainsKey("apiCallSuccess") && !issued;
            Outcome outcome; Error? error = null;
            if (unknown || writes && issued && success != true && !knownFalse)
            { outcome = Outcome.Unknown; error = Unknown(); }
            else if (writes && knownFalse)
            {
                outcome = applied > 0 ? Outcome.Partial : Outcome.Failed;
                error = applied > 0 ? new Error("Configuration entries were applied before the native operation returned false.",
                    new PartialFailureDetails(applied, 1, 0)) : NativeFailure();
            }
            else if (projectMissing)
            { outcome = Outcome.RejectedBeforeOperation; error = new Error("No project is bound.", new ProjectNotBoundDetails()); }
            else if (!issued && status == "HmiReadSessionBlocked")
            { outcome = Outcome.RejectedBeforeOperation; error = new Error("The session requires an explicit reset.", new SessionResetRequiredDetails("option-package-session")); }
            else if (!issued && (status == "NotFound" || status == "NotSupportedOnVersion" || status == "InvalidParams" || status == "InvalidState"))
            {
                outcome = Outcome.RejectedBeforeOperation;
                ErrorDetails details = status == "NotFound" ? new NotFoundDetails(null)
                    : status == "NotSupportedOnVersion" ? new UnsupportedCapabilityDetails(McpServer.ReleaseKey, tool, null)
                    : status == "InvalidParams" ? new InvalidArgumentDetails("arguments", Array.Empty<string>())
                    : new PreconditionFailedDetails("option-package-prerequisites", null);
                error = new Error("The option-package request was rejected before execution.", details);
            }
            else if (success == true) outcome = Outcome.Succeeded;
            else if (!writes) { outcome = Outcome.ReadFailed; error = NativeFailure(); }
            else if (Flag(evidence, "mayHaveChanged") == false && Flag(evidence, "mayHaveWrittenFiles") != true)
            { outcome = Outcome.RejectedBeforeOperation; error = new Error("The write prerequisites were not satisfied.", new PreconditionFailedDetails("option-package-before-write", null)); }
            else { outcome = Outcome.Unknown; error = Unknown(); }

            bool incomplete = Incomplete(evidence);
            var completeness = outcome == Outcome.RejectedBeforeOperation ? Completeness.None
                : outcome == Outcome.Unknown || outcome == Outcome.ReadFailed ? Completeness.Unknown
                : outcome == Outcome.Partial || incomplete ? Completeness.Partial : Completeness.Complete;
            Paging? paging = null;
            if (offset.HasValue && limit.HasValue && outcome == Outcome.Succeeded && evidence["records"] is JsonArray rows)
            {
                int? total = evidence["expectedCount"] is JsonValue count && count.TryGetValue<int>(out var size) ? size : (int?)null;
                int? next = evidence["nextOffset"] is JsonValue nextNode && nextNode.TryGetValue<int>(out var nextOffset) ? nextOffset : (int?)null;
                paging = new Paging(PagingMode.Offset, offset, limit.Value, next, null, null, total, !next.HasValue);
            }
            evidence.Remove("timestamp"); evidence.Remove("tool");
            Clean(evidence);
            var data = new JsonObject { ["evidence"] = evidence };
            if (evidence["records"] is JsonArray records) data["items"] = records.DeepClone();
            if (outcome == Outcome.Succeeded) data["summary"] = response.Message;
            return Result(tool, data, error, outcome, completeness, writes, current, paging, reset);
        }

        private static bool Incomplete(JsonNode? value)
        {
            if (value is JsonArray array) return array.Any(Incomplete);
            if (!(value is JsonObject obj)) return false;
            bool MissingRows(string countKey, string rowsKey) => obj[countKey] is JsonValue count && count.TryGetValue<int>(out var total)
                && total > ((obj[rowsKey] as JsonArray)?.Count ?? 0);
            bool Capped(string key, int limit) => obj[key] is JsonArray rows && rows.Count == limit;
            return Flag(obj, "dataComplete") == false || Flag(obj, "truncated") == true || Flag(obj, "incomplete") == true
                || Flag(obj, "fullObjectComplete") == false
                || MissingRows("expectedCount", "records") || MissingRows("pinCount", "pins") || MissingRows("blockCount", "blocks")
                || MissingRows("subchartCount", "subcharts") || MissingRows("interfaceCount", "chartInterfaces")
                || Capped("sequence", 1000) || Capped("runSequence", 1000) || Capped("chartSequence", 1000)
                || Capped("bits", 64) || Capped("parameters", 200) || Capped("revisionIds", 500) || Capped("values", 500)
                || Capped("requiredEntries", 500) || Capped("optionalEntries", 500) || Capped("encoderTypes", 500)
                || obj["enumValueList"] is JsonObject enums && enums.Count == 500
                || obj.Any(p => (p.Key == "notFound" || p.Key == "excludedComplexProperties" || p.Key == "failures") && p.Value is JsonArray missing && missing.Count > 0)
                || obj.Any(p => p.Key.EndsWith("Error", StringComparison.Ordinal) && p.Value != null || Incomplete(p.Value));
        }

        private static void Clean(JsonNode? value)
        {
            if (value is JsonArray array) { foreach (var item in array) Clean(item); return; }
            if (!(value is JsonObject obj)) return;
            foreach (var pair in obj.ToArray())
            {
                if (pair.Key == "messageData" || pair.Key == "detailMessageData" || pair.Key == "lastFailure"
                    || pair.Key.IndexOf("stack", StringComparison.OrdinalIgnoreCase) >= 0
                    || pair.Key.Equals("password", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("token", StringComparison.OrdinalIgnoreCase)) obj.Remove(pair.Key);
                else if (pair.Key == "error" || pair.Key == "message" || pair.Key.EndsWith("Error", StringComparison.Ordinal))
                    obj[pair.Key] = "Native diagnostic details are retained in the server log.";
                else Clean(pair.Value);
            }
        }

        private static Error NativeFailure() => new Error("The option-package operation did not establish success.",
            new NativeOperationFailedDetails(null, null, new Dictionary<string, JsonElement>()));
        private static Error Unknown() => new Error("The write outcome is unknown. Inspect the retained evidence and reset the session before further writes.",
            new OutcomeUnknownDetails("option-package-operation", new Dictionary<string, JsonElement>()));

        private static CallToolResult Result(string tool, JsonObject? data, Error? error, Outcome outcome, Completeness completeness,
            bool writes, bool current, Paging? paging = null, bool reset = false)
        {
            var warnings = new List<Warning>();
            if (current) warnings.Add(new Warning(WarningCode.UnverifiedBehavior, "Native behavior retains the current policy; V4 native acceptance is pending.", new Dictionary<string, JsonElement>()));
            if (completeness == Completeness.Partial) warnings.Add(new Warning(WarningCode.IncompleteData, "The observation covers only the reported scope and available fields.", new Dictionary<string, JsonElement>()));
            Execution execution = outcome == Outcome.RejectedBeforeOperation ? Execution.NotStarted : outcome == Outcome.Unknown ? Execution.Unknown
                : outcome == Outcome.Partial ? Execution.Partial : outcome == Outcome.ReadFailed || outcome == Outcome.Succeeded && !writes ? Execution.ReadOnly : Execution.Completed;
            var meta = new Meta(DateTimeOffset.UtcNow, McpServer.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), outcome,
                execution, reset || outcome == Outcome.Unknown, current ? BehaviorPolicy.Current : BehaviorPolicy.NotApplicable, completeness, paging, warnings);
            var result = McpResult.From(Envelope.Create(data, error, meta));
            return new CallToolResult { IsError = result.IsError, StructuredContent = JsonNode.Parse(result.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = result.Content[0].Text } } };
        }
    }

}
