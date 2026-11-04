using TiaMcpServer.Siemens.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcpServer.Siemens;


namespace TiaMcpServer.ModelContextProtocol
{
    // Interpret this family's execution evidence without inferring success from message text.
    internal static class HmiExchangeContract
    {
        internal static CallToolResult Run(string tool, bool writes, bool current, Func<object> operation)
        {
            try { return Map(tool, operation(), writes, current); }
            catch (Exception ex)
            {
                Exception cause = ex;
                while (cause.InnerException != null) cause = cause.InnerException;
                var evidence = new JsonObject { ["exceptionType"] = cause.GetType().Name };
                var rejection = cause is PortalException portal ? Rejection(portal.Code.ToString(), portal.Message)
                    : cause is NotSupportedException ? Rejection("NotSupportedOnVersion", cause.Message)
                    : cause is ArgumentException ? Rejection("InvalidParams") : null;
                var outcome = rejection != null ? Outcome.RejectedBeforeOperation : writes ? Outcome.Unknown : Outcome.ReadFailed;
                return Result(tool, new JsonObject { ["evidence"] = evidence }, rejection ?? Failure(outcome, evidence),
                    outcome, outcome == Outcome.RejectedBeforeOperation ? Completeness.None : Completeness.Unknown, writes, current);
            }
        }

        private static Error? Rejection(string? status, string? message = null)
        {
            ErrorDetails? details = status == "InvalidState" && message?.StartsWith("HMI import target already exists:", StringComparison.Ordinal) == true
                ? new PreconditionFailedDetails(message, null)
                : status == "InvalidState" ? new ProjectNotBoundDetails()
                : status == "InvalidParams" ? new InvalidArgumentDetails("arguments", Array.Empty<string>())
                : status == "ImportTargetExists" ? new PreconditionFailedDetails("HMI import target already exists; set overwrite=true explicitly.", null)
                : status == "NotFound" ? new NotFoundDetails(null)
                : status == "NotSupportedOnVersion" ? new UnsupportedCapabilityDetails(McpServer.ReleaseKey, "HMI exchange", null)
                : status == "HmiReadSessionBlocked" ? new SessionResetRequiredDetails("HMI read session is blocked.") : null;
            return details == null ? null
                : new Error(status == "NotSupportedOnVersion" && message != null ? message : "The HMI request could not start.", details);
        }

        internal static CallToolResult Map(string tool, object response, bool writes, bool current)
        {
            var data = JsonSerializer.SerializeToNode(response, response.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
            var evidence = data["meta"] as JsonObject ?? new JsonObject();
            data.Remove("meta");
            evidence.Remove("timestamp");
            data["summary"] = data["message"]?.DeepClone();
            data.Remove("message");
            data["evidence"] = evidence;
            Clean(data);

            bool? Flag(string key) => Bool(evidence[key]);
            string? status = (string?)evidence["status"];
            bool? success = Flag("operationSuccess") ?? Flag("success");
            if (Flag("success") == false || Flag("nativeSuccess") == false) success = false;
            bool changed = Flag("mayHaveChanged") == true;
            bool files = Flag("mayHaveWrittenFiles") == true || Flag("partialArtifactExists") == true;
            bool incomplete = Flag("dataComplete") == false || Flag("fullContentVerified") == false
                || evidence["nodeIdError"] != null || tool == "ListHmiTagTables" || tool == "ListHmiTags" || tool == "ListHmiConnections"
                || tool == "ExchangeUnifiedScriptModules" && Flag("nativeSuccess") == true
                || (evidence["modules"] as JsonArray)?.Count == 500 || (evidence["modulesAfter"] as JsonArray)?.Count == 500
                || (evidence["directoryListing"] as JsonArray)?.Count == 500 || (evidence["importCandidates"] as JsonArray)?.Count == 200
                || (evidence["displayNames"] as JsonArray)?.Count == 2000 || (evidence["displayNamesAfter"] as JsonArray)?.Count == 2000;
            bool unknownWrite = writes && (Flag("writeOutcomeUnknown") == true || Flag("requiresSessionReset") == true
                || Flag("verifiedAbsent") == false || (success != true && changed && Flag("nativeSuccess") != true)
                || (success != true && files && Flag("apiCallSuccess") != true && Flag("partialArtifactExists") != true));
            var rejection = Flag("importOverwriteRefused") == true ? Rejection("ImportTargetExists") : Rejection(status);
            Outcome outcome;
            Error? error = null;
            if (status == "HmiReadSessionBlocked" || rejection != null && !changed && !files)
            {
                outcome = Outcome.RejectedBeforeOperation;
                error = rejection;
            }
            else if (unknownWrite)
                outcome = Outcome.Unknown;
            else if (success != true)
            {
                bool step = tool == "DeleteHmiTag" || tool == "ExchangeUnifiedTags" || tool == "ExchangeUnifiedScriptModules"
                    || tool == "ImportUnifiedOpcUaAlarms";
                outcome = files ? Outcome.Partial
                    : step && evidence["tool"] != null && success == false && !changed && (writes || Flag("readStarted") != true) ? Outcome
                        .RejectedBeforeOperation
                    : !writes ? Outcome.ReadFailed
                    : changed && Flag("nativeSuccess") == true ? ((int?)evidence["actualCount"] > 0 ? Outcome.Partial : Outcome.Failed)
                    : evidence["capability"] != null ? Outcome.Failed : Outcome.Unknown;
                if ((string?)evidence["capability"] == "unsupported")
                {
                    outcome = Outcome.RejectedBeforeOperation;
                    error = Rejection("NotSupportedOnVersion");
                }
                if (outcome == Outcome.RejectedBeforeOperation && error == null)
                    error = new Error("The HMI request was rejected before the operation.", new PreconditionFailedDetails("HMI operation prerequisites", null));
            }
            else outcome = Outcome.Succeeded;

            if (evidence["items"] is JsonArray rows)
            {
                evidence.Remove("items");
                var items = new JsonArray();
                foreach (var row in rows)
                {
                    var item = row!.AsObject();
                    var child = Map(tool, new ResponseMessage { Meta = item["evidence"]!.DeepClone().AsObject() }, writes, current);
                    items
                        .Add(new JsonObject {
                            ["index"] = items.Count, ["target"] = item["target"]?.DeepClone(), ["result"] = child.StructuredContent!.DeepClone() });
                }
                data["items"] = items;
                var outcomes = items.Select(i => (string)i!["result"]!["meta"]!["outcome"]!).ToArray();
                int succeeded = outcomes.Count(o => o == "succeeded"), failed = outcomes.Length - succeeded;
                if (unknownWrite || outcomes.Contains("unknown")) outcome = Outcome.Unknown;
                else if (outcomes.Contains("partial") || succeeded > 0 && failed > 0)
                {
                    outcome = Outcome.Partial;
                    error = new Error("Some HMI operations did not complete.", succeeded > 0 && failed > 0
                        ? new PartialFailureDetails(succeeded, failed, 0) : new PartialFailureDetails(0, 0, 0));
                }
                else if (failed > 0) outcome = outcomes.All(o => o == "rejected-before-operation") ? Outcome.RejectedBeforeOperation : Outcome.Failed;
                if (failed > 0 || items.Any(i => (string?)i!["result"]!["meta"]!["completeness"] == "partial")) incomplete = true;
            }
            else if ((data["failed"] as JsonArray)?.Count > 0)
                outcome = writes ? Outcome.Unknown : Outcome.ReadFailed;

            if (outcome == Outcome.Unknown || outcome != Outcome.Succeeded && error == null) error = Failure(outcome, evidence);
            if (outcome == Outcome.Succeeded) error = null;
            var completeness = outcome == Outcome.RejectedBeforeOperation ? Completeness.None
                : outcome == Outcome.Unknown || outcome == Outcome.ReadFailed ? Completeness.Unknown
                : incomplete || outcome == Outcome.Partial ? Completeness.Partial : Completeness.Complete;
            return Result(tool, data, error, outcome, completeness, writes, current);
        }

        private static bool? Bool(JsonNode? value) => value is JsonValue v && v.TryGetValue<bool>(out var b) ? b : (bool?)null;
        private static IReadOnlyDictionary<string, JsonElement> Evidence(JsonObject evidence)
            => evidence.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal);
        private static Error Failure(Outcome outcome, JsonObject evidence)
            => outcome == Outcome.Unknown
                ? new Error("The HMI write outcome is unconfirmed; inspect the evidence and reset the session before further writes.",
                    new OutcomeUnknownDetails("HMI exchange", Evidence(evidence)))
                : outcome == Outcome.Partial ? new Error("The HMI operation left partial results.", new PartialFailureDetails(0, 0, 0))
                : outcome == Outcome.RejectedBeforeOperation
                    ? new Error("The HMI operation did not start.", new PreconditionFailedDetails("HMI operation prerequisites", null))
                : new Error("The HMI operation did not establish success.", new NativeOperationFailedDetails(null, null, Evidence(evidence)));

        private static void Clean(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var child in array) Clean(child); return; }
            if (!(node is JsonObject obj)) return;
            foreach (var pair in obj.ToArray())
            {
                if (pair.Key.IndexOf("stack", StringComparison.OrdinalIgnoreCase) >= 0 || pair.Key.Equals("password", StringComparison.OrdinalIgnoreCase)) obj
                    .Remove(pair.Key);
                else if (pair.Value is JsonValue v && v.TryGetValue<string>(out var text) && text.Contains("\n   "))
                    obj[pair.Key] = "Diagnostic details retained in the server log.";
                else Clean(pair.Value);
            }
        }

        private static CallToolResult Result(string tool, JsonObject data, Error? error, Outcome outcome, Completeness completeness, bool writes, bool current)
        {
            var warnings = new List<Warning>();
            if (current) warnings
                .Add(new Warning(WarningCode.UnverifiedBehavior, "Native behavior retains the current policy; V4 native acceptance is pending.",
                        new Dictionary<string, JsonElement>()));
            if (completeness == Completeness.Partial) warnings
                .Add(new Warning(WarningCode.IncompleteData, "The observation is incomplete; inspect the retained evidence.", new Dictionary<string,
                        JsonElement>()));
            var execution = outcome == Outcome.RejectedBeforeOperation ? Execution.NotStarted : outcome == Outcome.Unknown ? Execution.Unknown
                : outcome == Outcome.Partial ? Execution.Partial : outcome == Outcome.ReadFailed || outcome == Outcome.Succeeded && !writes ? Execution
                    .ReadOnly : Execution.Completed;
            var meta = new Meta(DateTimeOffset.UtcNow, McpServer.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), outcome, execution,
                outcome == Outcome.Unknown || error?.Code == ErrorCode.SessionResetRequired, current ? BehaviorPolicy.Current : BehaviorPolicy.NotApplicable,
                    completeness, null, warnings);
            var result = McpResult.From(Envelope.Create(data, error, meta));
            return new CallToolResult {
                IsError = result.IsError, StructuredContent = JsonNode.Parse(result.StructuredContent.GetRawText()), Content = new[] {
                    new TextContentBlock { Text = result.Content[0].Text } }
            };
        }
    }
}
