using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens;
using static TiaMcpServer.ModelContextProtocol.McpServer;
#if TIA_ENGINE_HOST
using ExportStore = TiaMcp.FoundationHost.SessionExportStore;
#endif

namespace TiaMcpServer.ModelContextProtocol
{
#if !TIA_ENGINE_PORTED
    [McpServerToolType]
    internal sealed class ExportTools
    {
        [McpServerTool(Name = "GetExportContent"), Description("[L1][Exports][READ] Read a character slice of a parked response. Concatenate data.text pages before parsing. Use meta.paging.nextOffset until complete. data.export identifies the full content and its SHA-256. Handles last 24 hours in this engine session.")]
        public CallToolResult GetExport(
            [Description("Opaque export handle from this engine session.")] string exportId,
            [Description("Zero-based character offset.")] int offset = 0,
            [Description("Requested characters; 0 uses the response limit, capped at 20000.")] int length = 0)
        {
            const string tool = "GetExportContent";
            if (offset < 0 || length < 0) return PlcExchangeContract.Reject(tool, InvalidInput("offset/length"), current: false);
            var entry = ExportStore.Get(exportId);
            if (entry == null) return Missing(tool, exportId);
            if (offset > entry.Length) return PlcExchangeContract.Reject(tool, InvalidInput("offset"), current: false);
            if (length == 0) length = ResolvedMaxResponseChars();
            length = length <= 0 ? ExportStore.MaxSliceChars : Math.Min(length, ExportStore.MaxSliceChars);
            var slice = ExportStore.Slice(exportId, offset, length);
            if (slice.Error != null) return Missing(tool, exportId);
            return PlcExchangeContract.Result(tool, new JsonObject { ["text"] = slice.Text, ["export"] = Describe(entry) },
                paging: OffsetPage(offset, length, slice.TotalLength));
        }

        [McpServerTool(Name = "ListExportHandles"), Description("[L1][Exports][READ] List this engine session's parked responses with content identity and SHA-256. Empty is a complete result. limit caps the returned handles.")]
        public CallToolResult ListExports(string? tool = null, int limit = 20)
        {
            if (limit < 1) return PlcExchangeContract.Reject("ListExportHandles", InvalidInput("limit"), current: false);
            var matches = ExportStore.List(tool, int.MaxValue);
            var entries = matches.Take(limit).ToArray();
            var (count, chars) = ExportStore.Stats();
            return PlcExchangeContract.Result("ListExportHandles", new JsonObject {
                ["items"] = new JsonArray(entries.Select(e => (JsonNode)new JsonObject { ["export"] = Describe(e) }).ToArray()),
                ["count"] = count, ["totalCharacters"] = chars, ["matchingCount"] = matches.Count,
                ["returnedCount"] = entries.Length }, completeness: entries.Length < matches.Count ? Completeness.Partial : Completeness.Complete);
        }

        [McpServerTool(Name = "SaveExportContent"), Description("[L1][Exports][FILE] Save a parked response as UTF-8 with BOM. raw=true preserves its full text; otherwise write its payload. Existing files require overwrite=true. A failed write can leave partial file content. V4 envelope; export behavior remains current.")]
        public CallToolResult SaveExport(string exportId, string outputPath, bool raw = false, bool overwrite = false)
        {
            const string tool = "SaveExportContent";
            var entry = ExportStore.Get(exportId);
            if (entry == null) return Missing(tool, exportId, current: true);
            if (string.IsNullOrWhiteSpace(outputPath)) return PlcExchangeContract.Reject(tool, InvalidInput("outputPath"));
            string full;
            try { full = Path.GetFullPath(outputPath.Trim()); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            { return PlcExchangeContract.Reject(tool, InvalidInput("outputPath")); }
            if (File.Exists(full) && !overwrite) return PlcExchangeContract.Reject(tool,
                new Error("The destination already exists.", new AlreadyExistsDetails(full)));
            bool unwrapped = false;
            string content = raw ? entry.Content : Payload(entry.Content, out unwrapped);
            bool issued = false;
            try
            {
                var directory = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory!);
                issued = true;
                File.WriteAllText(full, content, new UTF8Encoding(true));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                var data = new JsonObject { ["export"] = Describe(entry), ["path"] = full, ["writeIssued"] = issued };
                return issued ? PlcExchangeContract.Result(tool, data, new Error("File write outcome is unknown; inspect the destination before retrying.",
                    new OutcomeUnknownDetails("file-write", PlcExchangeContract.Evidence(data))), Outcome.Unknown, Execution.Unknown, Completeness.Unknown, current: true)
                    : PlcExchangeContract.Reject(tool, new Error("Cannot prepare the destination directory.", new IoFailedDetails("create-directory", full)), data);
            }
            return PlcExchangeContract.Result(tool, new JsonObject { ["export"] = Describe(entry), ["path"] = full,
                ["writtenLength"] = content.Length, ["unwrapped"] = unwrapped }, execution: Execution.Completed, current: true);
        }

        [McpServerTool(Name = "DeleteExportHandle"), Description("[L1][Exports][WRITE] Delete one parked response from this engine session. A missing or expired handle returns NOT_FOUND.")]
        public CallToolResult DeleteExport(string exportId)
        {
            var entry = ExportStore.Get(exportId);
            if (entry == null || !ExportStore.Delete(exportId)) return Missing("DeleteExportHandle", exportId);
            return PlcExchangeContract.Result("DeleteExportHandle", new JsonObject { ["export"] = Describe(entry) }, execution: Execution.Completed);
        }

        [McpServerTool(Name = "ClearExportHandles"), Description("[L1][Exports][WRITE] Delete parked responses at least olderThanHours old. Zero clears all; the default 24 generally removes none because handles expire automatically at 24 hours.")]
        public CallToolResult ClearExports(int olderThanHours = 24)
        {
            if (olderThanHours < 0) return PlcExchangeContract.Reject("ClearExportHandles", InvalidInput("olderThanHours"), current: false);
            int deleted = ExportStore.Clear(olderThanHours);
            var (count, chars) = ExportStore.Stats();
            return PlcExchangeContract.Result("ClearExportHandles", new JsonObject { ["deleted"] = deleted,
                ["remaining"] = count, ["remainingCharacters"] = chars }, execution: Execution.Completed);
        }

        private static CallToolResult Missing(string tool, string id, bool current = false) => PlcExchangeContract.Reject(tool,
            new Error("Export handle is missing, expired or evicted in this engine session.", new NotFoundDetails(id)), current: current);
        private static JsonObject Describe(ExportEntry entry)
        {
            using var hash = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(entry.Content);
            return new JsonObject { ["id"] = entry.Id, ["tool"] = entry.Tool, ["target"] = entry.Target,
                ["createdUtc"] = entry.CreatedUtc.ToUniversalTime().ToString("O"), ["totalLength"] = entry.Length,
                ["mediaType"] = "text/plain", ["byteLength"] = (long)bytes.Length,
                ["expiresUtc"] = entry.CreatedUtc.ToUniversalTime().AddHours(ExportStore.DefaultTtlHours).ToString("O"),
                ["sha256"] = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() };
        }
        private static string Payload(string content, out bool unwrapped)
        {
            try
            {
                var body = JsonNode.Parse(content);
                if (body is JsonObject obj && obj["schemaVersion"] is JsonValue version && version.TryGetValue<int>(out var number)
                    && number == 4 && obj["data"] is JsonObject data)
                {
                    unwrapped = true;
                    if (data["text"] is JsonValue text && text.TryGetValue<string>(out var value)) return value;
                    return data.ToJsonString();
                }
            }
            catch (JsonException) /* swallow(parse-fallback): plain text exports retain the existing payload extraction */ { }
            return ResponseGuardTool.UnwrapPayload(content, out unwrapped);
        }
    }
#endif


    // This adapter belongs only to the PLC exchange group. Native methods retain
    // their original calls; the scoped observations record already-known stages.
    internal static class PlcExchangeContract
    {
        internal sealed class Observation
        {
            internal int Issued, Confirmed;
            internal bool Changed;
            internal string Stage = "admission";
            internal JsonObject Fields = new JsonObject();
        }
        private static readonly AsyncLocal<Observation?> Active = new AsyncLocal<Observation?>();
        internal static void StartWrite(string stage)
        { if (Active.Value is Observation value) { value.Issued++; value.Stage = stage; } }
        internal static void ConfirmWrite()
        { if (Active.Value is Observation value) value.Confirmed++; }
        internal static void Changed()
        { if (Active.Value is Observation value) value.Changed = true; }
        internal static void ObserveNativeResult(string? state, bool changesProject, JsonNode? messages, string directory, string name)
        {
            if (Active.Value is not Observation observation) return;
            // Retain the first explicit failure when later files in a batch succeed.
            if (NativeResultState.TryFailure(observation.Fields, true, out _, out _)) return;
            NativeResultState.Record(observation.Fields, state, changesProject, messages: messages);
            observation.Fields["nativeStateType"] = TiaMcp.Adapters.Contracts.NativeResultStates.Documents;
            observation.Fields["targetFiles"] = new JsonArray(new[] { ".s7dcl", ".s7res" }.Select(ext => (JsonNode)NativeResultState.FileRow(Path.Combine(directory, name + ext))).ToArray());
        }
        internal static void Observe(string key, JsonNode? value)
        { if (Active.Value is Observation observation) observation.Fields[key] = value?.DeepClone(); }

        internal static readonly InputContract<string[]> Paths = NameListValidator.Create(new NameListPolicy(
            minimum: 1, maximum: 500, unique: true, ignoreCase: true, budget: new InputBudget(characters: 65536)));
        internal static InputContract<AttributeMap<Scalar>> TagProperties(string kind)
        {
            var fields = new Dictionary<string, AttributeRule> {
                ["ExternalAccessible"] = new AttributeRule(InputSchema.Boolean()), ["ExternalVisible"] = new AttributeRule(InputSchema.Boolean()),
                ["ExternalWritable"] = new AttributeRule(InputSchema.Boolean()), ["DataTypeName"] = new AttributeRule(InputSchema.String()),
                ["Comment"] = new AttributeRule(InputSchema.String()) };
            fields[kind == "constant" ? "Value" : "LogicalAddress"] = new AttributeRule(InputSchema.String());
            return AttributeMapValidator.Create(fields, new InputBudget(), maximum: 50);
        }

        internal static CallToolResult Run(string tool, Func<ResponseMessage> action, bool write, bool current)
        {
            var previous = Active.Value;
            var observation = new Observation(); Active.Value = observation;
            try { return Map(tool, action(), write, current, observation); }
            catch (Exception ex) { return Failure(tool, ex, write, current, observation); }
            finally { Active.Value = previous; }
        }
        internal static async Task<CallToolResult> RunAsync<T>(string tool, Func<Task<T>> action, bool write, bool current) where T : ResponseMessage
        {
            var previous = Active.Value;
            var observation = new Observation(); Active.Value = observation;
            try { return Map(tool, await action(), write, current, observation); }
            catch (Exception ex) { return Failure(tool, ex, write, current, observation); }
            finally { Active.Value = previous; }
        }
        internal static Dictionary<string, JsonElement> Evidence(JsonObject data) => data.ToDictionary(p => p.Key,
            p => JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal);
        private static bool? Flag(JsonObject data, string key) => data[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : (bool?)null;
        private static int Count(JsonObject data, string key) => data[key] is JsonValue value && value.TryGetValue<int>(out var count) ? count : 0;

        internal static CallToolResult Map(string tool, ResponseMessage response, bool write, bool current, Observation? observation = null)
        {
            var data = response.Meta == null ? new JsonObject() : (JsonObject)response.Meta.DeepClone();
            bool? success = Flag(data, "operationSuccess") ?? Flag(data, "success");
            var json = JsonSerializer.SerializeToNode(response, response.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
            foreach (var pair in json.Where(p => p.Key != "meta" && p.Key != "message")) data[pair.Key] = pair.Value?.DeepClone();
            data.Remove("timestamp"); data.Remove("success"); data.Remove("error"); // legacy error includes a stack trace
            if (data["lastFailure"] is JsonObject lastFailure) lastFailure.Remove("error");
            data["summary"] = response.Message;
            if (observation != null)
            {
                foreach (var field in observation.Fields) data[field.Key] = field.Value?.DeepClone();
                data["executionEvidence"] = new JsonObject { ["issued"] = observation.Issued, ["confirmed"] = observation.Confirmed,
                    ["knownSideEffects"] = observation.Changed, ["stage"] = observation.Stage };
            }
            if (NativeResultState.TryUnsuccessful(data, write, out var nativeOutcome, out var nativeError, tool))
                return Result(tool, data, nativeError, nativeOutcome, nativeOutcome == Outcome.Unknown ? Execution.Unknown : write ? Execution.Completed : Execution.ReadOnly,
                    nativeOutcome == Outcome.Unknown ? Completeness.Unknown : Completeness.Complete, current: current);
            bool changed = Flag(data, "mayHaveChanged") == true || Flag(data, "mayHaveWrittenFiles") == true;
            bool unknown = observation != null && observation.Issued > observation.Confirmed
                || (string?)data["outcome"] == "unknown" || Flag(data, "outcomeUnknown") == true
                || write && success == null
                || write && success != true && (Count(data, "attemptedFiles") > Count(data, "succeededFiles")
                    || changed && (observation == null || observation.Issued == 0));
            if (unknown) return Result(tool, data, new Error("An issued write could not be confirmed; do not replay it.", new OutcomeUnknownDetails(observation?.Stage ?? "native-call", Evidence(data))),
                Outcome.Unknown, Execution.Unknown, Completeness.Unknown, current: current);
            int succeeded = Count(data, "exportedBlocks") + Count(data, "succeededFiles");
            int failed = Count(data, "skippedBlocks");
            int skipped = Count(data, "notAttemptedFiles");
            if ((string?)data["outcome"] == "partial" || write && (succeeded > 0 && (failed > 0 || skipped > 0 || success == false) || observation?.Changed == true && success != true))
                return Result(tool, data, new Error("The operation has confirmed partial results.",
                    succeeded > 0 && failed + skipped > 0 ? new PartialFailureDetails(succeeded, failed, skipped) : new PartialFailureDetails(0, 0, 0)),
                    Outcome.Partial, Execution.Partial, Completeness.Partial, current: current);
            if (success == true)
            {
                bool incomplete = Flag(data, "dataComplete") == false || Flag(data, "complete") == false || ContainsTruncation(data);
                bool unverified = data.ContainsKey("contentVerified") && Flag(data, "contentVerified") != true;
                return Result(tool, data, execution: write ? Execution.Completed : Execution.ReadOnly,
                    completeness: incomplete ? Completeness.Partial : unverified ? Completeness.Unknown : Completeness.Complete, current: current);
            }
            if (Flag(data, "requiresExplicitRebind") == true) return Reject(tool,
                new Error("The session must be rebound before another operation.", new SessionResetRequiredDetails("native-session")), data, current);
            if (!changed && new[] { "InvalidState", "InvalidParams", "NotFound", "NotSupportedOnVersion" }.Contains((string?)data["status"]))
                return Reject(tool, StatusError(data), data, current);
            if (Flag(data, "queried") == false || write && !changed && (observation == null || observation.Issued == 0))
                return Reject(tool, StatusError(data), data, current);
            return Result(tool, data, new Error("The native operation did not complete successfully.",
                new NativeOperationFailedDetails((string?)data["status"], null, Evidence(data))), write ? Outcome.Failed : Outcome.ReadFailed,
                write ? Execution.Completed : Execution.ReadOnly, Completeness.None, current: current);
        }
        private static bool ContainsTruncation(JsonNode? node)
        {
            if (node is JsonArray array) return array.Any(ContainsTruncation);
            if (!(node is JsonObject obj)) return false;
            if (Flag(obj, "dataComplete") == false || Flag(obj, "groupsTruncated") == true || Count(obj, "blockCount") > (obj["blocks"] as JsonArray)?.Count
                || Count(obj, "typeCount") > (obj["types"] as JsonArray)?.Count) return true;
            return obj.Any(p => ContainsTruncation(p.Value));
        }
        private static Error StatusError(JsonObject data)
        {
            switch ((string?)data["status"])
            {
                case "InvalidState": return new Error("No project is bound.", new ProjectNotBoundDetails());
                case "InvalidParams": return InvalidInput("arguments");
                case "NotFound": return new Error("The exact target was not found.", new NotFoundDetails(null));
                case "NotSupportedOnVersion": return new Error("This capability is unavailable.", new UnsupportedCapabilityDetails(ReleaseKey, null, null));
                default: return new Error("The operation did not succeed; inspect the retained evidence.", new PreconditionFailedDetails("native-exchange", null));
            }
        }
        private static CallToolResult Failure(string tool, Exception exception, bool write, bool current, Observation observation)
        {
            var data = new JsonObject { ["executionEvidence"] = new JsonObject { ["issued"] = observation.Issued,
                ["confirmed"] = observation.Confirmed, ["knownSideEffects"] = observation.Changed, ["stage"] = observation.Stage } };
            foreach (var pair in observation.Fields) data[pair.Key] = pair.Value?.DeepClone();
            if (NativeResultState.TryUnsuccessful(data, write, out var nativeOutcome, out var nativeError, tool))
                return Result(tool, data, nativeError, nativeOutcome, nativeOutcome == Outcome.Unknown ? Execution.Unknown : write ? Execution.Completed : Execution.ReadOnly,
                    nativeOutcome == Outcome.Unknown ? Completeness.Unknown : Completeness.Complete, current: current);
            if (observation.Issued > observation.Confirmed) return Result(tool, data,
                new Error("An issued write outcome is unknown; inspect state before another write.", new OutcomeUnknownDetails(observation.Stage, Evidence(data))),
                Outcome.Unknown, Execution.Unknown, Completeness.Unknown, current: current);
            if (observation.Changed || observation.Confirmed > 0) return Result(tool, data,
                new Error("Confirmed effects remain, but result reporting did not complete.", new PartialFailureDetails(0, 0, 0)),
                Outcome.Partial, Execution.Partial, Completeness.Partial, current: current);
            if (exception is TiaMcp.Adapters.Contracts.AdapterPreconditionException admission)
            {
                var kind = TiaOpenness.Shared.HostFailurePolicy.Classify(admission, false, !write);
                return Reject(tool, HostBehavior.FailureError(kind, admission.ParamName, Evidence(data),
                    HostBehavior.AdmissionDiagnostic(admission)), data, current);
            }
            Error? error = null;
            for (Exception? cause = exception; cause != null; cause = cause.InnerException)
            {
                if (cause is TiaMcp.Adapters.Contracts.Candidates.PlcPathException path) error = SourceSession.Map(path);
                if (cause is PortalException portal) error = StatusError(new JsonObject { ["status"] = portal.Code.ToString() });
                if (cause is ArgumentException) error = InvalidInput("arguments");
                if (cause is global::ModelContextProtocol.McpException mcp && mcp.ErrorCode == global::ModelContextProtocol.McpErrorCode.InvalidParams) error = InvalidInput("arguments");
                if (cause is NotSupportedException) error = new Error("This capability is unavailable.", new UnsupportedCapabilityDetails(ReleaseKey, tool, null));
                if (cause is FileNotFoundException || cause is DirectoryNotFoundException) error = new Error("The input file or directory was not found.", new NotFoundDetails(null));
            }
            if (error != null || write) return Reject(tool, error ?? new Error("The operation was rejected before its write.", new PreconditionFailedDetails("native-exchange", null)), data, current);
            return Result(tool, data, new Error("The native read did not complete.", new NativeOperationFailedDetails(null, null, Evidence(data))),
                Outcome.ReadFailed, Execution.ReadOnly, Completeness.None, current: current);
        }
        internal static CallToolResult Reject(string tool, Error error, JsonObject? data = null, bool current = true)
            => Result(tool, data, error, Outcome.RejectedBeforeOperation, Execution.NotStarted, Completeness.None, current: current);
        internal static CallToolResult Result(string tool, JsonObject? data, Error? error = null, Outcome? outcome = null,
            Execution? execution = null, Completeness? completeness = null, Paging? paging = null, bool current = false)
        {
            var warnings = new List<Warning>();
            if (data != null) warnings.AddRange(NativeResultState.Warnings(data, tool));
            if (current) warnings.Add(new Warning(WarningCode.UnverifiedBehavior, "Native behavior retains the current policy; family acceptance is pending.", new Dictionary<string, JsonElement>()));
            if (completeness == Completeness.Partial || completeness == Completeness.Unknown) warnings.Add(new Warning(WarningCode.IncompleteData,
                "The retained observations do not establish complete content or post-operation state.", new Dictionary<string, JsonElement>()));
            if ((data?["warnings"] as JsonArray)?.Count > 0 || data?["warning"] != null
                || outcome == null && (data?["failures"] as JsonArray)?.Count > 0)
                warnings.Add(new Warning(WarningCode.NativeWarning, "Native or precheck diagnostics are retained in data.", new Dictionary<string, JsonElement>()));
            var meta = new Meta(DateTimeOffset.UtcNow, ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), outcome ?? Outcome.Succeeded, execution ?? Execution.ReadOnly,
                outcome == Outcome.Unknown || error?.Code == ErrorCode.SessionResetRequired, current ? BehaviorPolicy.Current : BehaviorPolicy.NotApplicable, completeness ?? Completeness.Complete, paging, warnings);
            var mapped = McpResult.From(Envelope.Create(data, error, meta));
            return new CallToolResult { IsError = mapped.IsError, StructuredContent = JsonNode.Parse(mapped.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = mapped.Content[0].Text } } };
        }
    }
}
