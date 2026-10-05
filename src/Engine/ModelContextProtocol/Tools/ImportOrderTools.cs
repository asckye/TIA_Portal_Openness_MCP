using TiaMcp.Logic.V4;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Construction;
using ModelContextProtocol.Protocol;
using System;
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TiaOpenness.Shared;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class ImportOrderTools
    {
        [McpServerTool(Name = "PlanArtifactImportOrder"), Description("[L2][Validation][READ] Offline dependency-first import order for 1..256 PLC/HMI artifacts, shared across all release profiles. Input is an array of id, target, priority and dependencies. Explicit dependencies override numeric priority (lower first). Reports missing dependencies and cycles with no executable order. Does not infer dependencies from source, inspect TIA, validate native import formats or import files. Reuses the MIT EidoTiaWorkbench planner.")]
        public CallToolResult PlanArtifactImportOrderV4(
            [Description("artifacts: Artifact[]. Supply the structured value directly; exact camelCase fields, no null or JSON string encoding.")] Artifact[] artifacts)
            => OfflineContracts.Run("PlanArtifactImportOrder", () => PlanArtifactImportOrder(OfflineContracts.Domain(artifacts)), writes: false, current: false);

        public ResponseMessage PlanArtifactImportOrder(
            [Description("JSON array, e.g. [{\"Id\":\"UDT_A\"},{\"Id\":\"FB_A\",\"Dependencies\":[\"UDT_A\"]}]. IDs are unique ignoring case; dependency IDs must be included. Optional Target and integer Priority order independent artifacts.")] string artifactsJson)
            => OfflineToolExecution.RunOfflineAnalysisTool("PlanArtifactImportOrder", meta => {
                if (artifactsJson == null || artifactsJson.Length > 1024 * 1024) throw new ArgumentException("Provide at most one MiB of JSON.");
                var plan = ImportDependencyPlanner.Build(JsonSerializer.Deserialize<ImportOrderItem[]>(artifactsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));
                meta["plan"] = JsonSerializer.SerializeToNode(plan);
                return plan.Valid ? "Dependency order calculated; native import remains separate." : "Import order unavailable: resolve the reported missing or cyclic dependencies.";
            });
    }

    // P6-09 boundary only. Existing builders and service methods keep their inputs,
    // execution order and file bytes; no target result is serialized into a message.
    internal static class OfflineContracts
    {
        private sealed class Rejected : Exception
        {
            internal Error Error { get; }
            internal Rejected(Error error) => Error = error;
        }

        // Adapt admitted DTOs to the unchanged legacy builders. Shared admission owns
        // shape, null and budget checks; only the outer kind/type relationship is local.
        internal static string Artifact(string kind, PlcArtifactSpec input, string parameter)
        {
            try
            {
                return ConstructionAdapter.ToBuilderJson(input.Resolve(kind));
            }
            catch (ArgumentException)
            {
                throw new Rejected(McpServer.InvalidInput(parameter));
            }
        }

        internal static string Domain<T>(T? value, string omitted = "") where T : class
        {
            if (value == null) return omitted;
            var json = JsonNode.Parse(V4Json.Serialize(value))!;
            // The V4 field is disabled; the unchanged lint parser calls it disable.
            if (value is LintRules && json is JsonObject rules && rules.ContainsKey("disabled"))
            {
                rules["disable"] = rules["disabled"]!.DeepClone(); rules.Remove("disabled");
            }
            return json.ToJsonString();
        }

        internal static string Names(string[]? input, string? omitted)
            => input == null ? omitted! : V4Json.Serialize(input);

        internal static CallToolResult Run(string tool, Func<ResponseMessage> action, bool writes = false, bool current = false, int? offset = null, int? limit = null)
        {
            try
            {
                var response = action();
                if (offset.HasValue && limit.HasValue)
                {
                    response.Meta ??= new JsonObject();
                    response.Meta["offset"] = offset.Value; response.Meta["limit"] = limit.Value;
                }
                return Map(tool, response, writes, current);
            }
            catch (Exception error) { return Failure(tool, error, writes, current); }
        }

        internal static async Task<CallToolResult> RunAsync(string tool, Func<Task<ResponseMessage>> action, bool writes = false, bool current = false)
        {
            try { return Map(tool, await action().ConfigureAwait(false), writes, current); }
            catch (Exception error) { return Failure(tool, error, writes, current); }
        }

        private static CallToolResult Failure(string tool, Exception error, bool writes, bool current)
        {
            var cause = error.InnerException ?? error;
            if (error is Rejected rejected)
                return Result(tool, null, rejected.Error, Outcome.RejectedBeforeOperation, Completeness.None, current);
            if (!writes && (cause is ArgumentException || cause is JsonException))
                return Result(tool, null, McpServer.InvalidInput("arguments"), Outcome.RejectedBeforeOperation, Completeness.None, current);
            if (!writes && (cause is FileNotFoundException || cause is DirectoryNotFoundException))
                return Result(tool, null, new Error("Input resource was not found.", new NotFoundDetails(null)), Outcome.RejectedBeforeOperation, Completeness.None, current);
            // The legacy writer can throw after its first side effect. A thrown
            // exception supplies no evidence that a write was not dispatched.
            return Result(tool, null, writes ? Unknown(new JsonObject()) : new Error("The operation could not be completed.",
                new InternalErrorDetails(null)), writes ? Outcome.Unknown : Outcome.ReadFailed, Completeness.Unknown, current);
        }

        internal static CallToolResult Map(string tool, ResponseMessage response, bool writes = false, bool current = false)
        {
            var fields = JsonSerializer.SerializeToNode(response, response.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
            var evidence = response.Meta?.DeepClone().AsObject() ?? new JsonObject();
            var data = fields["data"] is JsonObject payload ? payload.DeepClone().AsObject() : new JsonObject();
            foreach (var pair in fields)
                if (pair.Key != "meta" && pair.Key != "message" && pair.Key != "data") data[pair.Key] = pair.Value?.DeepClone();
            foreach (var pair in evidence)
                if (pair.Key != "success" && pair.Key != "timestamp" && pair.Key != "tool" && pair.Key != "error")
                    data[pair.Key] = pair.Value?.DeepClone();
            // Keep native evidence and independent policy verdicts. The legacy
            // executor's error contains exception.ToString(), which must not escape.
            data["summary"] = evidence["error"] == null ? response.Message : "The operation did not complete; inspect the retained execution evidence.";
            bool? success = Flag(evidence, "operationSuccess") ?? (response as ResponseJsonReport)?.Ok ?? Flag(evidence, "success");
            if (Flag(evidence, "success") == false) success = false;
            bool incomplete = Flag(evidence, "dataComplete") == false || Flag(evidence, "truncated") == true
                || Flag(evidence, "contentVerified") == false || evidence.ContainsKey("contentVerified") && evidence["contentVerified"] == null;
            Paging? paging = null;
            if (evidence["offset"] is JsonValue pageOffset && pageOffset.TryGetValue<int>(out var offset)
                && evidence["limit"] is JsonValue pageLimit && pageLimit.TryGetValue<int>(out var limit) && offset >= 0 && limit > 0)
            {
                string? totalField = new[] { "total", "rowCount", "hunkCount", "blockCount" }.FirstOrDefault(k => evidence[k] is JsonValue);
                if (totalField != null)
                {
                    paging = McpServer.OffsetPage(offset, limit, Count(evidence, totalField));
                    incomplete |= !paging.Complete;
                    data.Remove("offset"); data.Remove("limit"); data.Remove("nextOffset");
                }
            }
            int failed = Count(evidence, "filesFailed") + Count(evidence, "failedDocuments");
            int succeeded = 0;
            if (response is ResponsePlcProgramImport import)
            {
                failed += import.Failed?.Count() ?? 0;
                succeeded = (import.ImportedBlocks?.Count() ?? 0) + (import.ImportedTypes?.Count() ?? 0) + (import.ImportedTagTables?.Count() ?? 0);
                if (import.Compile != null && !string.Equals(import.Compile.State, "Success", StringComparison.OrdinalIgnoreCase)) failed++;
                success = failed == 0 && Flag(evidence, "offlineBuildOk") == true;
            }
            if (response is ResponseXmlBuild xml && xml.Xml != null && xml.Xml.Length > 1048576)
                return Result(tool, null, new Error("Constructed XML exceeds its output budget.", new LimitExceededDetails("xml", 1048576, xml.Xml.Length)),
                    Outcome.RejectedBeforeOperation, Completeness.None, current);
            if (data["plan"] is JsonObject plan && (Flag(plan, "valid") == false || Flag(plan, "Valid") == false)) success = false;
            bool mayWrite = Flag(evidence, "mayHaveChanged") == true || Flag(evidence, "mayHaveWrittenFiles") == true
                || Flag(evidence, "mayHaveModifiedRepository") == true;
            if (evidence["files"] is JsonArray files) succeeded += files.Count(f => f is JsonObject row && Flag(row, "written") == true);
            bool unknown = Flag(evidence, "requiresSessionReset") == true || Flag(evidence, "timedOut") == true && writes
                || writes && Flag(evidence, "dispatchStarted") == true && Flag(evidence, "executed") != true;
            if (!unknown && success == true && failed == 0)
                return Result(tool, data, null, Outcome.Succeeded, incomplete ? Completeness.Partial : Completeness.Complete, current, writes, paging);
            if (unknown || writes && success != true && (succeeded == 0 && mayWrite || response is ResponsePlcProgramImport && succeeded == 0))
                return Result(tool, data, Unknown(data), Outcome.Unknown, Completeness.Unknown, current);
            if (succeeded > 0 || success == true && failed > 0)
                return Result(tool, data, new Error("Some items failed or were not completed.", new PartialFailureDetails(Math.Max(1, succeeded), Math.Max(1, failed), 0)),
                    Outcome.Partial, Completeness.Partial, current);
            string? status = (string?)evidence["status"];
            Error? rejection = status == "InvalidParams" ? McpServer.InvalidInput("arguments")
                : status == "NotConnected" || status == "NoProject" ? new Error("No project is bound.", new ProjectNotBoundDetails())
                : status == "InvalidState" ? new Error("The session prerequisite is not satisfied.", new PreconditionFailedDetails("session", null))
                : status == "NotFound" ? new Error("The requested resource was not found.", new NotFoundDetails(null)) : null;
            if (rejection != null && !mayWrite)
                return Result(tool, data, rejection, Outcome.RejectedBeforeOperation, Completeness.None, current);
            if (writes && (mayWrite || success == null)) return Result(tool, data, Unknown(data), Outcome.Unknown, Completeness.Unknown, current);
            return Result(tool, data, new Error("The operation did not satisfy its completion criteria.", new InternalErrorDetails(null)),
                writes ? Outcome.Failed : Outcome.ReadFailed, incomplete ? Completeness.Partial : Completeness.Complete, current);
        }

        private static bool? Flag(JsonObject value, string key) => value[key] is JsonValue v && v.TryGetValue<bool>(out var flag) ? flag : (bool?)null;
        private static int Count(JsonObject value, string key) => value[key] is JsonValue v && v.TryGetValue<int>(out var count) ? count : 0;
        private static Error Unknown(JsonObject evidence) => new Error("The dispatched operation's outcome is unknown; do not replay it.",
            new OutcomeUnknownDetails("operation", new Dictionary<string, JsonElement> { ["result"] = JsonSerializer.SerializeToElement(evidence) }));

        private static CallToolResult Result(string tool, JsonObject? data, Error? error, Outcome outcome, Completeness completeness, bool current, bool writes = false, Paging? paging = null)
        {
            var warnings = new List<Warning>();
            if (current) warnings.Add(new Warning(WarningCode.UnverifiedBehavior, "Native behavior retains the current policy; V4 native acceptance is pending.", new Dictionary<string, JsonElement>()));
            if (completeness == Completeness.Partial) warnings.Add(new Warning(WarningCode.IncompleteData, "The retained result is incomplete; inspect the per-item evidence.", new Dictionary<string, JsonElement>()));
            if (data != null && (data.ContainsKey("xml") || data.ContainsKey("applyDesign")))
                warnings.Add(new Warning(WarningCode.CandidateOnly, "Constructed content is a candidate; native import and program semantics remain unverified.", new Dictionary<string, JsonElement>()));
            var execution = outcome == Outcome.Unknown ? Execution.Unknown : outcome == Outcome.Partial ? Execution.Partial
                : outcome == Outcome.RejectedBeforeOperation ? Execution.NotStarted : outcome == Outcome.ReadFailed || !writes && outcome == Outcome.Succeeded ? Execution.ReadOnly : Execution.Completed;
            var meta = new Meta(DateTimeOffset.UtcNow, McpServer.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), outcome, execution,
                outcome == Outcome.Unknown, current ? BehaviorPolicy.Current : BehaviorPolicy.NotApplicable, completeness, paging, warnings);
            var mapped = McpResult.From(Envelope.Create(data, error, meta));
            return new CallToolResult { IsError = mapped.IsError, StructuredContent = JsonNode.Parse(mapped.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = mapped.Content[0].Text } } };
        }
    }
}
