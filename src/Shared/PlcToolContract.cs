using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcpServer.ModelContextProtocol
{
    public class ResponseBlockLogic : ResponseMessage
    {
        public string? BlockPath { get; set; }
        public string? Language { get; set; }
        public string? Readable { get; set; }
    }


    // This boundary preserves the legacy implementation for unconverted internal callers.
    // It consumes explicit verdicts and evidence, never success words in a message.
    internal static class PlcToolContract
    {
        private sealed class Rejection : Exception
        {
            internal Error Error { get; }
            internal Rejection(Error error) => Error = error;
        }

        internal static T Input<T>(InputContract<T> contract, T value, string parameter)
        {
            var result = contract.Validate(value, parameter);
            if (result.Error != null) throw new Rejection(result.Error);
            return result.Value!;
        }

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
            if (BehaviorCapabilities.Select(typeof(PlcToolContract).Assembly, McpServer.ReleaseKey, "P6-FALLBACK") == BehaviorPolicy.SafeV4
                && (tool == "CompilePlcSoftware" || tool == "ExportPlcBlockDocuments" || tool == "ImportPlcBlockDocuments"))
                for (Exception? error = exception; error != null; error = error.InnerException)
                    if (error is TiaMcp.Adapters.Contracts.Candidates.CandidateObservationException observed)
                    {
                        var envelope = FallbackSession.Result(McpServer.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), null,
                            FallbackSession.Map(observed.Fault, ""), Outcome.RejectedBeforeOperation, Execution.NotStarted);
                        var wire = McpResult.From(envelope);
                        return new CallToolResult { IsError = wire.IsError, StructuredContent = JsonNode.Parse(wire.StructuredContent.GetRawText()),
                            Content = new[] { new TextContentBlock { Text = wire.Content[0].Text } } };
                    }
            for (Exception? cause = exception; cause != null; cause = cause.InnerException)
                if (cause.Data["nativeResultEvidence"] is JsonObject nativeEvidence && NativeResultState.TryUnsuccessful(nativeEvidence, writes, out var nativeOutcome, out var nativeError, tool))
                    return Result(tool, new JsonObject { ["evidence"] = nativeEvidence.DeepClone() }, nativeError, nativeOutcome, Completeness.Unknown, current, writes);
            if (exception is Rejection rejection)
                return Result(tool, null, rejection.Error, Outcome.RejectedBeforeOperation, Completeness.None, current);
            #if !TIA_ENGINE_HOST
            if (exception is PlcBlockVerificationException)
                return Result(tool, new JsonObject { ["verification"] = "mismatch", ["importReturned"] = true },
                    new Error("The imported block does not match the declared attributes.", new NativeOperationFailedDetails(null, null, new Dictionary<string, JsonElement>())),
                    Outcome.Failed, Completeness.Partial, current);
            #endif
            // Exceptions after a write may have been issued cannot establish its outcome.
            // The original exception remains in the existing diagnostic log, not the wire data.
            var kind = TiaOpenness.Shared.HostFailurePolicy.Classify(exception, true, !writes, nativeRead: InvocationJournal.NativeCallIssued);
            var evidence = HostBehavior.FailureEvidence(exception.GetType().Name, exception.Message);
            string? parameter = TiaOpenness.Shared.HostFailurePolicy.Parameter(exception);
            return Result(tool, new JsonObject { ["evidence"] = JsonSerializer.SerializeToNode(evidence) },
                HostBehavior.FailureError(kind, parameter, evidence, HostBehavior.AdmissionDiagnostic(exception)),
                HostBehavior.OutcomeOf(kind), HostBehavior.CompletenessOf(kind), current, writes);
        }

        internal static CallToolResult Map(string tool, object response, bool writes, bool current)
        {
            var root = JsonSerializer.SerializeToNode(response, response.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
            var evidence = root["meta"] as JsonObject ?? new JsonObject();
            root.Remove("meta");
            evidence.Remove("timestamp");
            var data = root["data"] as JsonObject;
            if (data != null) { root.Remove("data"); foreach (var pair in data.ToArray()) root[pair.Key] = pair.Value?.DeepClone(); }
            var summary = root["message"]?.DeepClone();
            root.Remove("message");
            root["summary"] = summary;
            root["evidence"] = evidence;
            HostBehavior.ExportPreview(root);
            Clean(root);

            if (NativeResultState.TryUnsuccessful(evidence, writes, out var nativeOutcome, out var nativeError, tool))
                return Result(tool, root, nativeError, nativeOutcome, nativeOutcome == Outcome.Unknown ? Completeness.Unknown : Completeness.Complete, current, writes);
            bool? Flag(string key) => Bool(evidence[key]) ?? Bool(root[key]);
            string? Text(string key) => (evidence[key] ?? root[key])?.ToString();
            bool incomplete = Flag("dataComplete") == false || Flag("incomplete") == true || Flag("truncated") == true
                || Flag("diagnosticsComplete") == false || Flag("coverageComplete") == false
                || Flag("crossReferenceAvailable") == false || Flag("apiCallSuccess") == true && Flag("dataComplete") == false;
            if (writes && tool is "CompilePlcSoftware" or "CompilePlcDiagnostics" or "CompileDevice" or "CompileHmiDiagnostics"
                && CompileResultMapping.Errors(root) is Error compileError)
                return Result(tool, root, compileError, Outcome.Failed, incomplete ? Completeness.Partial : Completeness.Complete, current, true);
            bool preview = !writes;
            bool? success = Flag("operationSuccess") ?? Flag("success") ?? Flag("ok");
            if (NativeResultState.State(evidence) != null && NativeResultState.EnumType(evidence, tool) != null)
                success = NativeResultState.Succeeded(evidence, tool) && success != false;
            // A positive outer wrapper cannot override an explicit negative domain verdict.
            if (Flag("ok") == false || Flag("operationSuccess") == false) success = false;
            int succeeded = Count(root, "imported") + Count(root, "importedTypes") + Count(root, "importedTagTables")
                + Count(root, "importedTechnologyObjects") + Count(root, "importedBlocks");
            int failed = Count(root, "failed");
            bool unknown = writes && (Flag("verified") == false || Flag("verifiedAbsent") == false
                || Text("effectiveState") == "Unknown" || Text("state") == "Unknown"
                || Flag("requiresSessionReset") == true || Text("outcome") == "unknown");
            bool knownFailure = Text("status") == "DocumentReadbackMismatch" || Text("effectiveState") == "Error"
                || Text("state") == "Error" || Text("outcome") == "failed";
            bool before = Flag("mayHaveChanged") == false && success != true;
            // RunHmiStepTool's project admission branch has not entered its action and
            // has none of the Failed() execution fields. A caught InvalidState does.
            if (Text("status") == "InvalidState" && evidence.ContainsKey("tool")
                && !evidence.ContainsKey("apiCallSuccess") && !evidence.ContainsKey("mayHaveChanged"))
                evidence["v4Rejection"] = "PROJECT_NOT_BOUND";
            if (Text("v4Rejection") != null)
            {
                var details = Text("v4Rejection") == "PROJECT_NOT_BOUND" ? (ErrorDetails)new ProjectNotBoundDetails()
                    : Text("v4Rejection") == "NOT_FOUND" ? new NotFoundDetails(null)
                    : new InvalidArgumentDetails("arguments", Array.Empty<string>());
                return Result(tool, root, new Error("The PLC request was rejected before execution.", details), Outcome.RejectedBeforeOperation, Completeness.None, current);
            }
            var compile = root["compile"] as JsonObject;
            var compileMeta = compile?["meta"] as JsonObject;
            if (Bool(compileMeta?["success"]) == false)
            {
                success = false;
                string? compileState = (compileMeta?["effectiveState"] ?? compile?["state"])?.ToString();
                unknown |= writes && compileState != "Error";
                knownFailure |= compileState == "Error";
                if (Bool(root["imported"]) == true) { succeeded = 1; failed++; }
            }
            // Returned batches retain their exact successful and failed item evidence.
            // Failed native writes without post-state evidence take precedence over partial.
            if (failed > 0) { success = false; unknown |= writes && !knownFailure && !before; }
            int total = Int(evidence["totalBlocks"]) ?? Int(evidence["totalTypes"]) ?? 0;
            int exported = Int(evidence["exportedBlocks"]) ?? Int(evidence["exportedTypes"]) ?? total;
            if (exported < total) { success = false; succeeded = exported; failed += total - exported; knownFailure = true; incomplete = true; }
            if (unknown || (writes && success != true && !knownFailure && !before))
                return Result(tool, root, Unknown(evidence), Outcome.Unknown, Completeness.Unknown, current);
            if (success != true)
            {
                Outcome outcome = HostBehavior.OperationOutcome(false, false, before || tool.StartsWith("PlanOnline", StringComparison.Ordinal),
                    !preview, succeeded > 0 && failed > 0);
                Error error = outcome == Outcome.RejectedBeforeOperation ? McpServer.InvalidInput("arguments")
                    : outcome == Outcome.Partial ? new Error("Some PLC operations did not complete.", new PartialFailureDetails(succeeded, failed, 0))
                    : new Error("The PLC operation did not establish success.", new NativeOperationFailedDetails(null, null, Evidence(evidence)));
                return Result(tool, root, error, outcome, incomplete ? Completeness.Partial : Completeness.None, current);
            }
            return Result(tool, root, null, Outcome.Succeeded, incomplete ? Completeness.Partial : Completeness.Complete, current, writes);
        }

        private static bool? Bool(JsonNode? value) => value is JsonValue v && v.TryGetValue<bool>(out var b) ? b : (bool?)null;
        private static int? Int(JsonNode? value) => value is JsonValue v && v.TryGetValue<int>(out var n) ? n : (int?)null;
        private static int Count(JsonObject obj, string key) => (obj[key] as JsonArray)?.Count ?? 0;
        private static IReadOnlyDictionary<string, JsonElement> Evidence(JsonObject data)
            => data.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal);
        private static Error Unknown(JsonObject evidence) => new Error("The write outcome is unconfirmed. Inspect the retained evidence and reset the session before further writes.",
            new OutcomeUnknownDetails("PLC operation", Evidence(evidence)));

        private static void Clean(JsonNode? node)
        {
            if (node is JsonObject obj)
                foreach (var pair in obj.ToArray())
                {
                    if (pair.Key.IndexOf("stack", StringComparison.OrdinalIgnoreCase) >= 0 || pair.Key.Equals("password", StringComparison.OrdinalIgnoreCase))
                        obj.Remove(pair.Key);
                    else if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text)
                        && text.Contains("\n   "))
                        obj[pair.Key] = "Diagnostic details retained in the server log.";
                    else Clean(pair.Value);
                }
            else if (node is JsonArray array) foreach (var child in array) Clean(child);
        }

        private static CallToolResult Result(string tool, JsonObject? data, Error? error, Outcome outcome,
            Completeness completeness, bool current, bool writes = false)
        {
            var warnings = new List<Warning>();
            if (data?["evidence"] is JsonObject nativeEvidence) warnings.AddRange(NativeResultState.Warnings(nativeEvidence, tool));
            if (current) warnings.Add(new Warning(WarningCode.UnverifiedBehavior,
                "Native behavior retains the current policy; V4 native acceptance is pending.", new Dictionary<string, JsonElement>()));
            if (completeness == Completeness.Partial) warnings.Add(new Warning(WarningCode.IncompleteData,
                "The returned observation is incomplete; inspect the retained evidence.", new Dictionary<string, JsonElement>()));
            Execution execution = HostBehavior.ExecutionOf(outcome, writes);
            Paging? paging = null;
            var page = (data?["evidence"]?["data"] ?? data?["evidence"]?["result"] ?? data) as JsonObject;
            int? offset = Int(page?["offset"]), limit = Int(page?["limit"]), total = Int(page?["total"]);
            if (offset >= 0 && limit > 0 && total >= offset)
            {
                int? next = (long)offset.Value + limit.Value < total.Value ? offset.Value + limit.Value : (int?)null;
                paging = new Paging(PagingMode.Offset, offset.Value, limit.Value, next, null, null, total.Value, next == null);
            }
            var meta = new Meta(DateTimeOffset.UtcNow, McpServer.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId),
                outcome, execution, outcome == Outcome.Unknown, current ? BehaviorPolicy.Current : BehaviorPolicy.NotApplicable,
                completeness, paging, warnings);
            var mapped = McpResult.From(Envelope.Create(data, error, meta));
            return new CallToolResult { IsError = mapped.IsError, StructuredContent = JsonNode.Parse(mapped.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = mapped.Content[0].Text } } };
        }
    }
}
