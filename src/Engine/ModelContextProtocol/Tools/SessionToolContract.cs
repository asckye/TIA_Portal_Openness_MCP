using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;

namespace TiaMcpServer.ModelContextProtocol
{
    // Session implementations remain available to CLI/Studio. Only registered
    // entries use this boundary; readiness and diagnostic evidence stay in data.
    internal static class SessionToolContract
    {
        internal static CallToolResult Run(string tool, bool writes, bool current, Func<object> operation)
        {
            try { return Map(tool, operation(), writes, current); }
            catch (Exception error) { return Failure(tool, error, writes, current); }
        }

        internal static async Task<CallToolResult> RunAsync<T>(string tool, bool writes, bool current, Func<Task<T>> operation)
        {
            try { return Map(tool, (await operation())!, writes, current); }
            catch (Exception error) { return Failure(tool, error, writes, current); }
        }

        internal static CallToolResult Failure(string tool, Exception exception, bool writes, bool current)
        {
            if (exception is TiaOpenness.Shared.BundleResourceUnavailableException resource)
                return Result(tool, null, new Error(resource.Message, new ResourceUnavailableDetails(resource.Resource)),
                    Outcome.RejectedBeforeOperation, Completeness.None, writes, current);
            // A thrown lifecycle action can follow an issued native call, including
            // closing an old project. Exception text cannot establish its post-state.
            var evidence = new JsonObject { ["exceptionType"] = exception.GetType().Name };
            string? reason = new[] { "ConnectPortal", "ConnectProject", "AttachOpenProject" }.Contains(tool, StringComparer.Ordinal)
                ? TiaMcp.Adapters.Contracts.Candidates.SessionPrimitives.ExceptionReason(exception) : null;
            var unknown = reason == null ? Unknown(evidence) : new Error(TiaMcp.Adapters.Contracts.Candidates.SessionPrimitives.ConfirmationGuidance,
                new OutcomeUnknownDetails("session operation", Evidence(evidence), reason));
            return Result(tool, new JsonObject { ["evidence"] = evidence },
                writes ? unknown : new Error("The diagnostic read could not be completed.", new InternalErrorDetails(null)),
                writes ? Outcome.Unknown : Outcome.ReadFailed, Completeness.Unknown, writes, current);
        }

        internal static CallToolResult Map(string tool, object response, bool writes, bool current)
        {
            var data = JsonSerializer.SerializeToNode(response, response.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
            var evidence = data["meta"] as JsonObject ?? new JsonObject();
            data.Remove("meta");
            if (tool == "InitializeEnvironment" || tool == "GetPortalInfo")
                data["behaviorCapabilities"] = BehaviorCapabilities.Table(typeof(SessionToolContract).Assembly, McpServer.ReleaseKey);
            evidence.Remove("timestamp");
            if (data.ContainsKey("summary")) data["operationSummary"] = data["message"]?.DeepClone();
            else data["summary"] = data["message"]?.DeepClone();
            data.Remove("message");
            data["evidence"] = evidence;
            Clean(data);

            bool? Flag(string key) => Bool(evidence[key]) ?? Bool(data[key]);
            string? Text(string key) => (evidence[key] ?? data[key])?.ToString();
            bool? success = Flag("operationSuccess") ?? Flag("success") ?? Flag("ok");
            if (Flag("ok") == false || Flag("operationSuccess") == false) success = false;
            bool incomplete = Flag("dataComplete") == false || Flag("incomplete") == true
                || Flag("truncated") == true || HasObservationError(evidence);
            bool unknown = writes && (Flag("verified") == false || Flag("requiresSessionReset") == true
                || Text("outcome") == "unknown");
            bool before = success != true && Flag("mayHaveChanged") == false;
            bool knownFailure = Flag("rollbackCompleted") == true;

            // Read-only probes remain callable while disconnected. A bound-project
            // refusal is identified by the step executor's explicit admission shape.
            if (Text("status") == "InvalidState" && evidence.ContainsKey("tool")
                && !evidence.ContainsKey("apiCallSuccess") && !evidence.ContainsKey("mayHaveChanged"))
                return Result(tool, data, new Error("No project is bound.", new ProjectNotBoundDetails()),
                    Outcome.RejectedBeforeOperation, Completeness.None, writes, current);

            if (tool == "RestartOpennessWorker" && success != true)
                return Result(tool, data, new Error("Worker restart preconditions are not satisfied.",
                    new PreconditionFailedDetails("worker-enabled-and-idle", null)), Outcome.RejectedBeforeOperation, Completeness.None, writes, current);
            if (tool == "EnsureOpennessUserGroup" && success == false) knownFailure = true;
            // A returned report contains both completed file writes, even when
            // the diagnostic verdict recorded in those files is a failure.
            if (tool == "GenerateAcceptanceReport" && data["markdownPath"] != null && data["jsonPath"] != null)
                knownFailure = true;

            if (tool == "RunToolTransaction" && evidence["results"] is JsonArray results)
            {
                var items = new JsonArray(results.Select((row, index) => (JsonNode)new JsonObject {
                    ["index"] = index, ["target"] = row?["name"]?.DeepClone(), ["result"] = row?["result"]?.DeepClone()
                }).ToArray());
                if (evidence["calls"] is JsonArray calls)
                    for (int index = results.Count; index < calls.Count; index++)
                    {
                        var name = (string)calls[index]!["name"]!;
                        var stopped = McpServer.V4Reject(name, new Error("The transaction stopped before this call.",
                            new NotExecutedDetails(results.Count == 0 ? (int?)null : results.Count - 1)));
                        items.Add(new JsonObject { ["index"] = index, ["target"] = name,
                            ["result"] = stopped.StructuredContent!.DeepClone() });
                    }
                data["items"] = items;
                unknown |= results.Any(row => (string?)row?["result"]?["meta"]?["outcome"] == "unknown");
            }
            if (tool == "BuildProjectScaffold" && data["steps"] is JsonArray steps)
            {
                // A collected native failure does not prove the failed write was
                // rolled back, even when earlier scaffold steps returned success.
                unknown |= writes && steps.Any(step => (string?)step?["status"] == "failed");
                before |= !writes && success == false;
            }
            if (unknown || writes && success != true && !knownFailure && !before)
                return Result(tool, data, Unknown(evidence), Outcome.Unknown, Completeness.Unknown, writes, current);
            if (success != true)
            {
                var outcome = before ? Outcome.RejectedBeforeOperation : writes ? Outcome.Failed : Outcome.ReadFailed;
                var error = before ? McpServer.InvalidInput("arguments")
                    : new Error("The operation did not establish success.", new NativeOperationFailedDetails(null, null, Evidence(evidence)));
                return Result(tool, data, error, outcome, incomplete ? Completeness.Partial : Completeness.None, writes, current);
            }
            return Result(tool, data, null, Outcome.Succeeded, incomplete ? Completeness.Partial : Completeness.Complete, writes, current);
        }

        private static bool? Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : (bool?)null;
        private static IReadOnlyDictionary<string, JsonElement> Evidence(JsonObject data)
            => data.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value), StringComparer.Ordinal);
        private static Error Unknown(JsonObject evidence) => new Error("The operation outcome is unconfirmed. Inspect the retained evidence and reset the session before further writes.",
            new OutcomeUnknownDetails("session operation", Evidence(evidence)));

        private static bool HasObservationError(JsonNode? node)
        {
            if (node is JsonObject obj) return obj.Any(pair => pair.Key.EndsWith("Error", StringComparison.Ordinal)
                && pair.Value != null || HasObservationError(pair.Value));
            return node is JsonArray array && array.Any(HasObservationError);
        }

        private static void Clean(JsonNode? node)
        {
            if (node is JsonObject obj)
                foreach (var pair in obj.ToArray())
                {
                    if (pair.Key.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0
                        || pair.Key.IndexOf("stack", StringComparison.OrdinalIgnoreCase) >= 0) obj.Remove(pair.Key);
                    else if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text) && text.Contains("\n   "))
                        obj[pair.Key] = "Diagnostic details retained in the server log.";
                    else Clean(pair.Value);
                }
            else if (node is JsonArray array) foreach (var child in array) Clean(child);
        }

        private static CallToolResult Result(string tool, JsonObject data, Error? error, Outcome outcome,
            Completeness completeness, bool writes, bool current)
        {
            var warnings = new List<Warning>();
            if (current) warnings.Add(new Warning(WarningCode.UnverifiedBehavior,
                "Native behavior retains the current policy; V4 native acceptance is pending.", new Dictionary<string, JsonElement>()));
            if (completeness == Completeness.Partial) warnings.Add(new Warning(WarningCode.IncompleteData,
                "The diagnostic observation is incomplete; inspect the retained evidence.", new Dictionary<string, JsonElement>()));
            var execution = outcome == Outcome.RejectedBeforeOperation ? Execution.NotStarted
                : outcome == Outcome.Unknown ? Execution.Unknown : outcome == Outcome.Partial ? Execution.Partial
                : writes ? Execution.Completed : Execution.ReadOnly;
            var meta = new Meta(DateTimeOffset.UtcNow, McpServer.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId),
                outcome, execution, outcome == Outcome.Unknown, current ? BehaviorPolicy.Current : BehaviorPolicy.NotApplicable,
                completeness, null, warnings);
            var result = McpResult.From(BehaviorCapabilities.Disclose(Envelope.Create(data, error, meta)));
            return new CallToolResult { IsError = result.IsError, StructuredContent = JsonNode.Parse(result.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = result.Content[0].Text } } };
        }
    }
}
