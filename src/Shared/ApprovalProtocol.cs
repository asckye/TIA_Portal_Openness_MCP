#if !TIA_GENERATION_PLANNER
extern alias PlcRendering;
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
#if TIA_APPROVAL_PRIVATE_JOURNAL
using ApprovalDisplayPayload = TiaOpenness.Shared.ApprovalJournalPayload;
#else
using ApprovalDisplayPayload = TiaOpenness.Shared.CallJournalPayload;
#endif

namespace TiaOpenness.Shared
{
    [System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
    internal sealed class PendingApproval
    {
        public int Version { get; set; } = 1;
        public string Kind { get; set; } = "request";
        public string? Outcome { get; set; }
        public string RequestId { get; set; } = "";
        public string Host { get; set; } = "";
        public string ReleaseKey { get; set; } = "";
        public string Tool { get; set; } = "";
        public string Risk { get; set; } = "write";
        public string RiskAction { get; set; } = "execute";
        public bool OverwritesExisting { get; set; }
        public string PlanHash { get; set; } = "";
        public string ArgumentDigest { get; set; } = "";
        public string ProjectIdentity { get; set; } = "{}";
        public string ParametersJson { get; set; } = "{}";
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? PlanJson { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        internal string InvocationArgumentsDigest { get; set; } = "";
        [System.Text.Json.Serialization.JsonIgnore]
        internal JsonObject? BatchPlan { get; set; }
        public ApprovalAction[] Operations { get; set; } = Array.Empty<ApprovalAction>();
        public int TimeoutSeconds { get; set; } = 120;
        public DateTimeOffset Deadline { get; set; }

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? Actor { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? OperatorCallId { get; set; }
        internal string EffectiveActor => Version == 1 ? ActorScope.Mcp : Actor!;

        internal static PendingApproval Create(string host, string release, string tool, string arguments,
            string? identity, int seconds, string? requestId = null)
        {
            var original = JsonNode.Parse(arguments)!.AsObject();
            var outputs = ApprovalOutputTargets.Normalize(tool, original);
            var args = ReviewArguments(tool, original);
            var target = JsonNode.Parse(identity ?? "{}")!.AsObject();
            foreach (string key in new[] { "expectedProjectFile", "projectFile", "projectPath", "projectName", "processId", "processStartUtc", "workspaceRoot"
                })
                if (original[key] != null) target["requested:" + key] = original[key]!.DeepClone();
            string digest = Hash(Canonical(new JsonObject { ["releaseKey"] = release, ["tool"] = tool,
                ["identity"] = ApprovalDisplayPayload.RedactArguments(target), ["arguments"] = args.DeepClone() }));
            string? plan = args["expectedPlanHash"] is JsonValue value && value.TryGetValue<string>(out var hash) && IsHash(hash) ? hash : null;
            var display = Display(original, tool);
            foreach (var output in outputs) display[output.Key] = output.Value;
            if (Encoding.UTF8.GetByteCount(display.ToJsonString()) > 64 * 1024)
            {
                display = new JsonObject { ["displayTruncated"] = true };
                foreach (var output in outputs) display[output.Key] = output.Value;
            }
            if (Encoding.UTF8.GetByteCount(display.ToJsonString()) > 64 * 1024)
                throw new ArgumentException("Filesystem targets exceed the approval display limit.", nameof(arguments));
            var objects = display.Where(p => p.Key.EndsWith("Path", StringComparison.Ordinal) || p.Key.EndsWith("Name", StringComparison.Ordinal)
                || p.Key.EndsWith("Directory", StringComparison.Ordinal) || p.Key.EndsWith("Dir", StringComparison.Ordinal)
                || p.Key == "dir" || p.Key == "directory" || p.Key == "plc" || p.Key == "target")
                .Select(p => p.Key + "=" + p.Value?.ToJsonString()).ToArray();
            string risk = ClassifyRisk(tool, original);
            return new PendingApproval { RequestId = requestId ?? Guid.NewGuid().ToString("N"), Host = host, ReleaseKey = release,
                Tool = tool, Risk = risk, RiskAction = risk == "write" ? "execute" : Deletes(tool, original) ? "delete" : "online",
                OverwritesExisting = Flag(original, "overwrite"), PlanHash = plan ?? digest, ArgumentDigest = digest, ProjectIdentity =
                    Display(target, "identity").ToJsonString(),
                ParametersJson = display.ToJsonString(), TimeoutSeconds = seconds,
                BatchPlan = original["batchPlan"]?.DeepClone() as JsonObject,
                InvocationArgumentsDigest = Hash(Canonical(args)),
                Version = ActorScope.Actor == ActorScope.Workbench ? 2 : 1,
                Actor = ActorScope.Actor == ActorScope.Workbench ? ActorScope.Workbench : null,

                OperatorCallId = ActorScope.Actor == ActorScope.Workbench ? ActorScope.OperatorCallId : null,
                Deadline = DateTimeOffset.UtcNow.AddSeconds(seconds),
                Operations = outputs.Length == 0
                    ? new[] { new ApprovalAction { Tool = tool, Action = display["action"]?.ToString() ?? tool, Target = string.Join(" · ", objects) } }
                    : outputs.Select(output => new ApprovalAction { Tool = tool, Action = display["action"]?.ToString() ?? tool,
                        Target = output.Value }).ToArray() };
        }
        // This is host policy, independent of MCP destructiveHint (which also covers ordinary overwrites).
        internal static string ClassifyRisk(string tool, JsonObject arguments)
        {
#if TIA_GENERATION_PLANNER
            var operation = TiaMcpServer.ModelContextProtocol.ToolTaxonomy.OperationOf(tool, null).Operation;
#else
            var operation = PlcRendering::TiaMcpServer.ModelContextProtocol.ToolTaxonomy.OperationOf(tool, null).Operation;
#endif
            return operation == "ONLINE-WRITE" || Deletes(tool, arguments)
                ? "destructive" : "write";
        }
        internal static bool Deletes(string tool, JsonObject arguments) => tool.StartsWith("Delete", StringComparison.OrdinalIgnoreCase)
            || Flag(arguments, "confirmDelete") || Flag(arguments, "deleteAtTarget");
        internal static bool Flag(JsonObject arguments, string name) => arguments.Any(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)
            && p.Value is JsonValue flag && flag.TryGetValue<bool>(out var enabled) && enabled);
        internal static JsonObject ReviewArguments(string tool, JsonObject original)
        {
            var args = ApprovalDisplayPayload.RedactArguments(original)!.AsObject();
            foreach (var output in ApprovalOutputTargets.Normalize(tool, original)) args[output.Key] = output.Value;
            return args;
        }
        // The digest binds full output paths and redacted non-path arguments. Human review never carries
        // file contents, and every display is bounded independently of the transport.
        private static JsonObject Display(JsonObject args, string tool)
        {
#if TIA_GENERATION_PLANNER
            var original = args;
#endif
            try { args = JsonNode.Parse(ApprovalDisplayPayload.Sanitize(args.ToJsonString(), boundedText: true))!.AsObject(); }
            catch (Exception error) when (error is JsonException || error is InvalidOperationException)
            { return new JsonObject { ["displayUnavailable"] = true }; }
            JsonNode? Summarize(JsonNode? node, string key = "", int depth = 0)
            {
                if (depth > 16) return JsonValue.Create("<display depth limit>");
                if (node is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    if (key.EndsWith("content", StringComparison.OrdinalIgnoreCase) || key.EndsWith("text", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("base64", StringComparison.OrdinalIgnoreCase) || text.Length > 4096)
                        return new JsonObject { ["byteLength"] = Encoding.UTF8.GetByteCount(text), ["sha256"] = Hash(text) };
                    return JsonValue.Create(text);
                }
                if (node is JsonArray array) return new JsonArray(array.Take(128).Select(n => Summarize(n, key, depth + 1)).ToArray());
                if (node is JsonObject obj)
                {
                    var result = new JsonObject();
                    foreach (var pair in obj.Take(128)) result[pair.Key.Length > 128 ? Hash(pair.Key) : pair.Key] = Summarize(pair.Value, pair.Key, depth + 1);
                    return result;
                }
                return node?.DeepClone();
            }
            var compact = Summarize(args)!.AsObject();
            if (tool == "StageImportFiles")
            {
                var files = new JsonArray();
                foreach (var file in (args["files"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Take(128))
                {
                    string content = (string?)file["content"] ?? "";
                    files.Add(new JsonObject { ["fileName"] = Summarize(file["fileName"]), ["kind"] = Summarize(file["kind"]),
                        ["byteLength"] = Encoding.UTF8.GetByteCount(content), ["sha256"] = Hash(content) });
                }
                compact["files"] = files;
                try { compact["stagingLocation"] = Path.Combine(BundleLayout.RequireRoot(AppContext.BaseDirectory), "staging"); }
                catch (IOException) /* swallow(privacy): the staging precheck reports the unavailable bundle before approval */
                { compact["stagingLocation"] = Path.Combine(AppContext.BaseDirectory, "staging"); }
            }
#if TIA_GENERATION_PLANNER
            // Download and upload prompts are answered during the native call; the approver sees every answer the call will give.
            if (TiaMcpServer.Siemens.DownloadPromptPolicy.ApprovalPlan(tool, original) is JsonObject plan) compact["promptAnswerPlan"] = plan;
#endif
            string json = compact.ToJsonString();
            if (Encoding.UTF8.GetByteCount(json) > 64 * 1024)
                compact = new JsonObject { ["displayTruncated"] = true, ["argumentByteLength"] = Encoding.UTF8.GetByteCount(args.ToJsonString()) };
            try { return JsonNode.Parse(ApprovalDisplayPayload.Sanitize(compact.ToJsonString(), boundedText: true))!.AsObject(); }
            catch (Exception error) when (error is JsonException || error is InvalidOperationException)
            { return new JsonObject { ["displayUnavailable"] = true }; }
        }
        internal void Validate()
        {
            ValidateAttribution();
            if (string.IsNullOrWhiteSpace(RequestId) || string.IsNullOrWhiteSpace(Host)
                || string.IsNullOrWhiteSpace(ReleaseKey) || string.IsNullOrWhiteSpace(Tool)
                || !IsHash(PlanHash) || !IsHash(ArgumentDigest) || Risk is not ("write" or "destructive")
                || RiskAction is not ("execute" or "delete" or "online")
                || TimeoutSeconds < 1 || TimeoutSeconds > 3600 || Deadline <= DateTimeOffset.UtcNow
                || Deadline > DateTimeOffset.UtcNow.AddSeconds(TimeoutSeconds + 5) || Operations.Length == 0 || Operations
                    .Length > (Kind == "plan" ? 10000 : 50)
                || Kind == "plan" && (Tool != "ApplyGenerationPlan" || PlanJson == null))
                throw new InvalidDataException("Invalid approval request.");
            using (var project = JsonDocument.Parse(ProjectIdentity))
            using (var args = JsonDocument.Parse(ParametersJson))
                if (project.RootElement.ValueKind != JsonValueKind.Object || args.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Approval identity and arguments must be objects.");
            if (Kind == "plan")
            {
                var plan = JsonNode.Parse(PlanJson!)!.AsObject();
                if ((string?)plan["planHash"] != PlanHash) throw new InvalidDataException("Approval plan hash differs.");
#if TIA_GENERATION_PLANNER
                if (TiaMcp.Logic.Generation.GenerationDocuments.PlanHash(PlanJson!) != PlanHash)
                    throw new InvalidDataException("Approval plan content differs.");
#else
                if (PlcRendering::TiaMcp.Logic.Generation.GenerationDocuments.PlanHash(PlanJson!) != PlanHash)
                    throw new InvalidDataException("Approval plan content differs.");
#endif
            }
        }
        internal void ValidateAttribution()
        {
            if (Version == 1 ? Actor != null || OperatorCallId != null
                : Version != 2 || !ActorScope.IsValid(Actor)
                    || OperatorCallId != null && !Guid.TryParseExact(OperatorCallId, "N", out _))
                throw new InvalidDataException("Invalid approval attribution.");
        }
        internal static bool IsHash(string? hash)
        {
            return hash != null && hash.Length == 64 && hash.All(c => "0123456789abcdef".Contains(c));
        }
        internal static string Hash(string value)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }
        internal static string Canonical(JsonNode? node)
        {
            if (node is JsonObject obj) return "{" + string.Join(",", obj.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value))) + "}";
            if (node is JsonArray array) return "[" + string.Join(",", array.Select(Canonical)) + "]";
            return node?.ToJsonString() ?? "null";
        }
    }
    // Caller output paths are shared by admission and approval display; other values retain normal redaction.
    internal static class ApprovalOutputTargets
    {
        private static readonly HashSet<string> FileSystemKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "allowedFilePath", "apiPath", "archivePath", "csvPath", "destinationDirectory", "destinationPath", "dir", "directory",
            "directoryPath", "evidenceDirectory", "exportDir", "exportPath", "exportFile", "exportDirectory", "sourceDirectory",
            "filePath", "importPath", "inputPath", "leftFilePath",
            "libraryPath", "logFilePath", "mappingFilePath", "newProjectPath", "offlineReleaseSuiteJsonPath", "outputDirectory", "outputPath",
            "plcXmlPath", "projectPath", "recordPath", "referenceAmlPath", "referenceDir", "referenceGlobalLibraryPath", "referenceProjectPath",
            "reportDirectory", "reportPath", "repositoryPath", "rightFilePath", "schemaDirectory", "sourceDir", "templateDirectory",
            "templatePath", "workingDirectory", "xmlPath"
        };

        private static IEnumerable<KeyValuePair<string, string>> ReviewTargets(string tool, JsonObject arguments)
        {
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in arguments)
            {
                bool fileSystem = FileSystemKeys.Contains(pair.Key)
                    || pair.Key == "folderPath" && tool == "CreateVersionControlWorkspace"
                    || pair.Key == "sourcePath" && tool is "ManageStandardPackage" or "ValidateStandardPackage";
                if (fileSystem && pair.Value is JsonValue value && value.TryGetValue<string>(out var path) && path.Length != 0)
                    paths[pair.Key] = path;
            }
            foreach (var pair in Arguments(tool, arguments))
                if (!paths.ContainsKey(pair.Key)) paths[pair.Key] = pair.Value;
            return paths;
        }

        internal static void ValidateWhitespace(string tool, JsonObject arguments)
        {
            foreach (var pair in ReviewTargets(tool, arguments))
                if (!string.Equals(pair.Value, pair.Value.Trim(), StringComparison.Ordinal))
                    throw new ArgumentException("Filesystem targets must not have leading or trailing whitespace.", pair.Key);
        }

        internal static IEnumerable<KeyValuePair<string, string>> Arguments(string tool, JsonObject arguments)
        {
            foreach (string canonical in new[] { "exportFile", "exportDirectory" })
                if (arguments[canonical] is JsonValue value && value.TryGetValue<string>(out var canonicalPath) && canonicalPath.Length != 0)
                    yield return new KeyValuePair<string, string>(canonical, canonicalPath);
            string action = TextArgument(arguments, "action");
            string? parameter = tool switch
            {
                "ExportAlarmClasses" or "ExportAlarmInstanceTexts" or "ExportAlarmTextLists" or "ExportDeviceAml"
                    or "ExportHmiConnection" or "ExportHmiScreen" or "ExportHmiTagTable" or "ExportOpcUaInterface"
                    or "ExportTechnologyObject" or "ExportPlcBlock" or "ExportPlcBlockDocuments" or "ExportPlcBlocks" or "ExportPlcBlocksDocuments"
                    or "ExportPlcTagTable" or "ExportPlcType" or "ExportPlcTypes" or "ExportPlcWatchTable" => "exportPath",
                "ExportProjectTexts" or "ExportSafetyPrintout" or "GeneratePlcSourceFromBlocks"
                    or "ExportScadaData" or "GeneratePlcLoadableFile" => "filePath",
                "ExchangeSystemDiagnosticsSettings" when action == "export" => "filePath",
                "ManageHardwareUtilities" when action is "exportCardReaderPsc" or "exportOpcUa" => "filePath",
                "GenerateOpcUaModelledInterface" or "GeneratePlcDocumentation" or "PatchPlcBlockDocument" or "RenderPlcBlock"
                    or "RenderPlcBlockDocument" or "RenderPlcProgramAtlas" or "RenderPlcVisualDiff" or "SaveExportContent"
                    or "WritePlcSclSourceFile" or "BuildDeviceAmlDocument" => "outputPath",
                "ManageUnifiedCwcPackage" when action == "build" => "outputPath",
                "BuildReleaseHandoffArtifacts" or "GenerateAcceptanceReport" or "GenerateErrorReport" or "InstantiatePlcTemplates"
                    or "WriteClassicHmiMinimalPackageFiles" => "outputDirectory",
                "ExportHmiProgram" or "ExportTechnologyObjectsToDirectory" => "exportDir",
                "DownloadPlcToFolder" or "ExportUnifiedEngineeringList" => "destinationDirectory",
                "ExchangeUnifiedTags" or "ExchangeUnifiedScriptModules" when action == "export" => "directory",
                "ArchiveSavedProject" => "archivePath",
                "AuditEngineeringExports" => "reportPath",
                "ScanPlcSourceAnnotations" => "csvPath",
                "ExportPlcProDiagInfo" => "directoryPath",
                "ExportPlcWatchTablesToDirectory" => "dir",
                "ManagePlcGitRepository" when action is "stage" or "commit" => "repositoryPath",
                "RunClassicHmiOfflineValidationSuite" or "RunClassicHmiTemporaryImportPreflight"
                    or "RunHmiTemplatePlcSyncPrecheckSuite" or "RunOfflineReleaseValidationSuite" => "reportDirectory",
                "ExchangePlcAlarmTextLists" or "ExchangePlcSupervisions" or "ManageSivarcScreenLayout"
                    or "ManageClassicHmiCycle" or "ManageClassicHmiTextGraphicList" when action == "export" => "filePath",
                "ExchangeCfcCharts" when action != "import" => "filePath",
                "ExchangeMotionCamData" when action.StartsWith("export", StringComparison.Ordinal) => "filePath",
                _ => null
            };
            if (parameter == null) yield break;
            string path = TextArgument(arguments, parameter);
            if (string.IsNullOrWhiteSpace(path) && tool == "BuildReleaseHandoffArtifacts")
            {
                string input = TextArgument(arguments, "offlineReleaseSuiteJsonPath");
                if (!string.IsNullOrWhiteSpace(input))
                {
                    try { path = Path.GetDirectoryName(Path.GetFullPath(input))!; }
                    catch (Exception error) when (error is ArgumentException or IOException)
                    {
                        /* swallow(env-probe): invalid derived paths reach normal argument rejection before approval */
                        path = input;
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(path)) yield return new KeyValuePair<string, string>(parameter, path);
        }

        internal static KeyValuePair<string, string>[] Normalize(string tool, JsonObject arguments)
        {
            ValidateWhitespace(tool, arguments);
            var normalized = ReviewTargets(tool, arguments).Select(pair =>
            {
                string fullPath;
                try { fullPath = Path.GetFullPath(pair.Value); }
                catch (PathTooLongException error) { throw new ArgumentException("Output target is too long to display.", pair.Key, error); }
                if (fullPath.Length > 4096) throw new ArgumentException("Output target is too long to display.", pair.Key);
                return new KeyValuePair<string, string>(pair.Key, fullPath);
            }).ToArray();
            var display = new JsonObject { ["displayTruncated"] = true };
            foreach (var pair in normalized) display[pair.Key] = pair.Value;
            if (Encoding.UTF8.GetByteCount(display.ToJsonString()) > 64 * 1024)
                throw new ArgumentException("Filesystem targets exceed the approval display limit.", nameof(arguments));
            return normalized;
        }

        private static string TextArgument(JsonObject arguments, string name)
            => arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    }

    // An in-process, single-step grant. It is never accepted from MCP arguments or pipe metadata.
    internal static class GenerationStepApproval
    {
        private sealed class Grant
        {
            internal string Release = "", Tool = "", Digest = "";
            internal object? SessionKey;
            internal string? RunId;
            internal ApprovalOutcome Approval = null!;
            internal bool Used;
        }
        private static readonly AsyncLocal<Grant?> Current = new AsyncLocal<Grant?>();
        internal static IDisposable? EnterForwardedTool(string source, JsonObject arguments, string target, JsonObject forwardedArguments)
        {
            var previous = Current.Value;
            if (previous == null) return null;
            string Digest(JsonObject value) => PendingApproval.Hash(PendingApproval.Canonical(ApprovalDisplayPayload.RedactArguments(value)));
            if (previous.Used || previous.Tool != source || previous.Digest != Digest(arguments))
                throw new InvalidOperationException("Compatibility arguments differ from the approved step.");
            var forwarded = new Grant { Release = previous.Release, Tool = target, Digest = Digest(forwardedArguments),
                Approval = previous.Approval, SessionKey = previous.SessionKey, RunId = previous.RunId };
            Current.Value = forwarded;
            return new Scope(() => { previous.Used |= forwarded.Used; Current.Value = previous; });
        }
        internal static IDisposable EnterBatchItem(ApprovalOutcome approval, int index, string tool, JsonObject arguments)
        {
            if (approval.Reason != null || Current.Value != null) throw new InvalidOperationException("A granted non-nested batch is required.");
            var plan = approval.Request.BatchPlan ?? throw new InvalidOperationException("Approved batch plan missing.");
            var item = plan["items"]![index]!;
            if ((string?)item["tool"] != tool) throw new InvalidOperationException("Batch item differs from approval.");
            var expected = item["arguments"]!.DeepClone().AsObject(); expected["dryRun"] = false;
            if (arguments.ContainsKey("confirm")) expected["confirm"] = true;
            if (arguments.ContainsKey("expectedProjectFile")) expected["expectedProjectFile"] = plan["expectedProjectFile"]!.DeepClone();
            if (arguments.ContainsKey("expectedPlanHash")) expected["expectedPlanHash"] = item["canonicalPlanHash"]!.DeepClone();
            if (PendingApproval.Canonical(ApprovalDisplayPayload.RedactArguments(expected)) != PendingApproval
                    .Canonical(ApprovalDisplayPayload.RedactArguments(arguments)))
                throw new InvalidOperationException("Canonical arguments differ from the approved batch item.");
            var previous = Current.Value;
            Current.Value = new Grant { Release = approval.Request.ReleaseKey, Tool = tool,
                Digest = PendingApproval.Hash(PendingApproval.Canonical(ApprovalDisplayPayload.RedactArguments(arguments))), Approval = approval };
            return new Scope(() => Current.Value = previous);
        }
#if TIA_GENERATION_PLANNER
        internal static IDisposable Enter(ApprovalOutcome approval, TiaMcp.Logic.Generation.GenerationPlanStepsItem step,
            JsonObject arguments, string? previewRequestId = null, string? previewHash = null, object? sessionKey = null, string? runId = null)

        {
            if (approval.Reason != null || approval.Request.Kind != "plan") throw new InvalidOperationException("A granted plan is required.");
            if (Current.Value != null) throw new InvalidOperationException("Nested generation grants are refused.");
            var approved = JsonNode.Parse(approval.Request.PlanJson ?? throw new InvalidOperationException("Approved plan missing."))!["steps"]!.AsArray()
                .Single(s => (string?)s!["id"] == step.Id)!;
            var fixedArguments = arguments.DeepClone().AsObject();
            var late = step.Review?.LateArguments ?? new System.Collections.Generic.List<string>();
            if (late.Count != 0)
            {
                if (!late.SequenceEqual(new[] { "expectedPlanHash" }) || string.IsNullOrEmpty(previewRequestId) || !PendingApproval.IsHash(previewHash)
                    || (string?)arguments["expectedPlanHash"] != previewHash || approved["arguments"]!.AsObject().ContainsKey("expectedPlanHash"))
                    throw new InvalidOperationException("Only this step's preview hash may fill the declared late slot.");
                fixedArguments.Remove("expectedPlanHash");
            }
            var declared = JsonSerializer.SerializeToNode(step, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            declared!["arguments"] = ApprovalDisplayPayload.RedactArguments(declared["arguments"]);
            if (!JsonNode.DeepEquals(approved, declared)
                || PendingApproval.Canonical(ApprovalDisplayPayload.RedactArguments(fixedArguments)) != PendingApproval.Canonical(approved["arguments"])
                || TiaMcp.Logic.Generation.CanonicalJson.ArgumentHash(JsonSerializer.SerializeToElement(fixedArguments)) != step.ArgumentDigest)
                throw new InvalidOperationException("Final arguments or review differ from the approved step.");
            var previous = Current.Value;
            Current.Value = new Grant { Release = approval.Request.ReleaseKey, Tool = step.Tool,
                Digest = PendingApproval.Hash(PendingApproval.Canonical(PendingApproval.ReviewArguments(step.Tool, arguments))),
                Approval = approval, SessionKey = sessionKey, RunId = runId };
            return new Scope(() => Current.Value = previous);
        }
#endif
        internal static bool Allows(object sessionKey, string runId, string release, string tool, string arguments, bool dispatched)
        {
            var grant = Current.Value;
            return grant != null && (!grant.Used || dispatched) && ReferenceEquals(grant.SessionKey, sessionKey)
                && grant.RunId == runId && grant.Release == release && grant.Tool == tool
                && grant.Digest == PendingApproval.Hash(PendingApproval.Canonical(
                    PendingApproval.ReviewArguments(tool, JsonNode.Parse(arguments)!.AsObject())));
        }
        internal static ApprovalOutcome? Match(PendingApproval request)
        {
            var grant = Current.Value;
            if (grant == null) return null;
            bool matches = !grant.Used && grant.Release == request.ReleaseKey && grant.Tool == request.Tool && grant.Digest == request
                .InvocationArgumentsDigest;
            grant.Used = true;
            return new ApprovalOutcome(request, grant.Approval.Disabled, matches ? null : "denied", true);
        }
        private sealed class Scope : IDisposable
        {
            private readonly Action restore;
            internal Scope(Action restore) { this.restore = restore; }
            public void Dispose() => restore();
        }
    }
    // Shared by both host boundaries and attached operator calls; never keyed by a transport or actor.
    internal static class GenerationWriteFence
    {
        private sealed class State { internal string? RunId; internal bool Ended; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, State> Sessions = new();
        internal static string? RefusedRun(object key, string release, string tool, string arguments, bool dispatched = false)
        {
            var state = Sessions.GetValue(key, _ => new State());
            lock (state) return state.RunId is { } run && !GenerationStepApproval.Allows(key, run, release, tool, arguments, dispatched) ? run : null;
        }
        internal static IDisposable Acquire(object key, string runId)
        {
            var state = Sessions.GetValue(key, _ => new State());
            lock (state)
            {
                if (state.Ended || state.RunId != null) throw new InvalidOperationException("Generation session ended or already has an active run.");
                state.RunId = runId;
            }
            return new Lease(() => { lock (state) if (state.RunId == runId) state.RunId = null; });
        }
        internal static void Shutdown(object key)
        {
            var state = Sessions.GetValue(key, _ => new State());
            lock (state) { state.Ended = true; state.RunId = null; }
        }
        internal static bool Ended(object key)
        {
            var state = Sessions.GetValue(key, _ => new State());
            lock (state) return state.Ended;
        }
        private sealed class Lease(Action release) : IDisposable { public void Dispose() => release(); }
    }
    [System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ApprovalAction
    {
        public string Tool { get; set; } = "";
        public string Action { get; set; } = "";
        public string Target { get; set; } = "";
    }
    [System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ApprovalDecision
    {
        public int Version { get; set; } = 1;
        public string RequestId { get; set; } = "";
        public string PlanHash { get; set; } = "";
        public string ArgumentDigest { get; set; } = "";
        public string Decision { get; set; } = "denied";
        internal bool Matches(PendingApproval request) => Version == 1 && RequestId == request.RequestId
            && PlanHash == request.PlanHash && ArgumentDigest == request.ArgumentDigest && (Decision == "granted" || Decision == "denied");
    }
    // Length-prefixed UTF-8, bounded independently of tool/worker transports.
    internal static class ApprovalFrames
    {
        internal const int MaximumBytes = 1024 * 1024;
        internal static async Task Write<T>(Stream stream, T frame, CancellationToken token)
        {
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(frame);
            if (data.Length > MaximumBytes) throw new InvalidDataException("Approval frame exceeds its budget.");
            byte[] header = BitConverter.GetBytes(data.Length);
            await stream.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
            await stream.WriteAsync(data, 0, data.Length, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        internal static async Task<T> Read<T>(Stream stream, CancellationToken token)
        {
            byte[] header = new byte[4]; await Exact(stream, header, token).ConfigureAwait(false);
            int size = BitConverter.ToInt32(header, 0);
            if (size < 1 || size > MaximumBytes) throw new InvalidDataException("Invalid approval frame length.");
            byte[] data = new byte[size]; await Exact(stream, data, token).ConfigureAwait(false);
            using (var doc = JsonDocument.Parse(data))
            {
                var fields = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in doc.RootElement.EnumerateObject())
                    if (!fields.Add(field.Name)) throw new InvalidDataException("Duplicate approval field.");
                string[] required = typeof(T) == typeof(ApprovalDecision)
                    ? new[] { "Version", "RequestId", "PlanHash", "ArgumentDigest", "Decision" }
                    : new[] {
                        "Version", "Kind", "RequestId", "Host", "ReleaseKey", "Tool", "Risk", "PlanHash", "ArgumentDigest", "ProjectIdentity", "ParametersJson",
                            "Operations", "TimeoutSeconds", "Deadline"
                    };
                if (required.Any(field => !fields.Contains(field))) throw new InvalidDataException("Missing approval field.");
                if (typeof(T) == typeof(PendingApproval))
                {
                    int version = doc.RootElement.GetProperty("Version").GetInt32();
                    if (version == 1 && (fields.Contains("Actor") || fields.Contains("OperatorCallId"))
                        || version == 2 && !fields.Contains("Actor"))
                        throw new InvalidDataException("Invalid approval attribution fields.");
                }
            }
            var frame = JsonSerializer.Deserialize<T>(data) ?? throw new InvalidDataException("Missing approval frame.");
            if (frame is PendingApproval pending) pending.ValidateAttribution();
            return frame;
        }
        private static async Task Exact(Stream stream, byte[] data, CancellationToken token)
        {
            int offset = 0;
            while (offset < data.Length)
            {
                int read = await stream.ReadAsync(data, offset, data.Length - offset, token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
        }
    }
}
