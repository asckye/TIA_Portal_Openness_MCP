using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaOpenness.Shared;

namespace TiaMcpServer.ModelContextProtocol
{
    // The escape hatch that lets the lite roster be the default without losing anything.
    //
    // The lite roster limits per-turn schema size and stays within host tool-count limits.
    // FindTools + CallTool provide on-demand access to the entire roster: search when a tool is needed,
    // read its signature, then invoke it. Tools outside the advertised list remain discoverable and callable.
    public static partial class McpServer
    {
#if TIA_ENGINE_HOST
        private static IToolCatalogView? BridgeCatalog {
            get => TiaMcp.FoundationHost.EngineHostConfiguration.Current.Catalog; set => TiaMcp.FoundationHost.EngineHostConfiguration.Current.Catalog = value;
        }
        private static IToolInvoker? BridgeInvoker {
            get => TiaMcp.FoundationHost.EngineHostConfiguration.Current.Invoker; set => TiaMcp.FoundationHost.EngineHostConfiguration.Current.Invoker = value;
        }
        private static Func<bool> BridgeIsLiteProfile {
            get => TiaMcp.FoundationHost.EngineHostConfiguration.Current.IsLiteProfile; set => TiaMcp.FoundationHost.EngineHostConfiguration.Current
                .IsLiteProfile = value;
        }
        private static ISet<string> BridgeLiteToolNames {
            get => TiaMcp.FoundationHost.EngineHostConfiguration.Current.LiteToolNames; set => TiaMcp.FoundationHost.EngineHostConfiguration.Current
                .LiteToolNames = value;
        }
#else
        private static IToolCatalogView? BridgeCatalog;
        private static IToolInvoker? BridgeInvoker;
        private static Func<bool> BridgeIsLiteProfile = () => false;
        private static ISet<string> BridgeLiteToolNames = new HashSet<string>(StringComparer.Ordinal);
#endif

        static McpServer() { ConfigureToolBridgeProfile(); }
        // Optional partial hooks must stay erasable in assemblies without an implementation.
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0040",
            Justification = "Optional partial hooks require implicit accessibility to remain erasable.")]
        static partial void ConfigureToolBridgeProfile();

        internal static void ConfigureToolBridge(IToolCatalogView catalog, IToolInvoker invoker, Func<bool> isLiteProfile, ISet<string> liteToolNames)
        {
            BridgeCatalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            BridgeInvoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
            BridgeIsLiteProfile = isLiteProfile ?? throw new ArgumentNullException(nameof(isLiteProfile));
            BridgeLiteToolNames = liteToolNames ?? throw new ArgumentNullException(nameof(liteToolNames));
        }

        internal static string InvocationHostTag
#if TIA_ENGINE_HOST
            => TiaMcp.FoundationHost.ReleaseCatalogData.Releases[ReleaseKey].HostInvocationTag;
#else
            => "engine";
#endif

        private static bool UseDeclaredReleasePolicy
            => UsesDeclaredReleasePolicy(ReleaseKey);

        private static bool UsesDeclaredReleasePolicy(string release)
#if TIA_ENGINE_HOST
            => TiaMcp.FoundationHost.ReleaseCatalogData.Releases[release].WrapPortedTools;
#else
            => TiaMcp.Versioning.TiaVersionCatalog.Get(release).EngineOutputDirectory != null;
#endif

        internal static string VersionToolProblem(string name)
            => VersionToolProblem(ReleaseKey, name);

        internal static string VersionToolProblem(string release, string name)
            => UsesDeclaredReleasePolicy(release)
                ? Siemens.ToolVersionPolicy.ToolProblem(release, name)
                : SessionCatalog() is { } catalog && catalog.Find(name) == null ? "Tool is not registered in this release." : "";

        // Old-release registration is a per-session catalog fact. Building a registry happens before any MCP session
        // exists; only calls inside a session can be refused as unregistered.
#if TIA_ENGINE_HOST
        private static IToolCatalogView? SessionCatalog() => TiaMcp.FoundationHost.EngineHostConfiguration.CurrentOrNull?.Catalog;
#else
        private static IToolCatalogView? SessionCatalog() => CatalogView;
#endif

        internal static string VersionCallProblem(string name, Func<string, string?> argument)
        {
            if (UseDeclaredReleasePolicy)
                return Siemens.ToolVersionPolicy.CallProblem(ReleaseKey, name, argument);
            if (VersionToolProblem(name) is { Length: > 0 } problem) return problem;
            if (name is "RenderPlcBlockDocument" or "ComparePlcBlockDocuments"
                && new[] { "blockPath", "leftBlockPath", "rightBlockPath" }.Any(p => !string.IsNullOrWhiteSpace(argument(p))))
                return "Native block-document analysis is not enabled on this release; provide exported file paths.";
            if (ReleaseKey is "14sp1" or "15.1" or "16" && name is "CompilePlcSoftware" or "CompilePlcDiagnostics"
                && !string.IsNullOrEmpty(argument("password")))
                return "Safety compilation with a password requires V17 or later.";
            return "";
        }

        private static string DuplicateArgumentProblem(JsonObject arguments)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in arguments)
                if (!seen.Add(pair.Key)) return "Duplicate argument names differing only by case are ambiguous: " + pair.Key + ". Nothing was executed.";
            return "";
        }

        [McpServerTool(Name = "ListToolCategories"),
                Description("[L0][Meta][READ] List the tool taxonomy, layers and current full-catalog counts. Use FindTools to browse a category or domain.")]
        public static CallToolResult ListToolCategoriesV4()
            => InfrastructureResult("ListToolCategories", ListToolCategories());

        [McpServerTool(Name = "FindTools"),
                Description("[L0][Meta][READ] Search the full version-gated catalog, including tools outside lite. Returns names, signatures, descriptions " +
                "and examples. Use CallTool with an arguments object to invoke a match.")]
        public static CallToolResult FindToolsV4(
            [Description("All capability words must match. Single words also match descriptions, ranked below tool names and aliases. Empty browses the " +
                "catalog.")] string query =
                "",
            [Description("Positive page size; default 12.")] int limit = 12,
            [Description("Optional category key from ListToolCategories.")] string category = "",
            [Description("Optional exact domain tag from ListToolCategories.")] string domain = "",
            [Description("Zero-based match offset.")] int offset = 0)
        {
            if (offset < 0 || limit < 1) return V4Reject("FindTools", InvalidInput("offset/limit"));
            var response = FindTools(query, int.MaxValue, category, domain);
            if (response.Meta?["success"]?.GetValue<bool?>() != true)
                return V4Reject("FindTools", InvalidInput("category/domain"));
            var lines = (response.Items ?? Enumerable.Empty<string>()).ToArray();
            int count = lines.Length / 3;
            if (offset > count) return V4Reject("FindTools", InvalidInput("offset"));
            response.Items = lines.Skip(offset * 3L > int.MaxValue ? int.MaxValue : offset * 3).Take(limit > int.MaxValue / 3 ? int.MaxValue : limit * 3)
                .ToArray();
            return InfrastructureResult("FindTools", response, OffsetPage(offset, limit, count));
        }

        [McpServerTool(Name = "CallTool"),
                Description("[L0][Meta] Invoke a tool in the full version-gated catalog. arguments must be an object matching the target inputSchema. Large " +
                "results return data.text, data.export and data.continuation; follow GetExportContent with meta.paging.nextOffset and concatenate text " +
                "before parsing. Dispatch failures use the V4 envelope. Nested orchestration is refused.")]
        [AsyncDispatch(nameof(CallToolAsync))]
        public static CallToolResult CallTool(
            [Description("Exact currently registered tool name from FindTools.")] string name,
            [Description("Target arguments as an object. Omit for a tool with no arguments; strings and null are invalid.")] ToolArguments? arguments = null)
            => CallToolAsync(name, arguments).GetAwaiter().GetResult();

        internal static Task<CallToolResult> CallToolAsync(string name, ToolArguments? arguments = null)
            => CallToolCoreAsync(name, arguments);

        internal static CallToolResult DispatchNestedTool(string name, ToolArguments? arguments) => CallToolCore(name, arguments);
        internal static CallToolResult DispatchGenerationStep(string name, ToolArguments arguments, Func<CallToolResult?> beforeDispatch)
            => CallToolCore(name, arguments, beforeDispatch);

        private static CallToolResult CallToolCore(string name, ToolArguments? arguments, Func<CallToolResult?>? beforeDispatch = null)
            => CallToolCoreAsync(name, arguments, beforeDispatch == null ? null : () => Task.FromResult(beforeDispatch())).GetAwaiter().GetResult();

        private static async Task<CallToolResult> CallToolCoreAsync(string name, ToolArguments? arguments, Func<Task<CallToolResult?>>? beforeDispatch = null)
        {
#if TIA_ENGINE_HOST
            if (CatalogView.Find(name)?.Execution == "foundation" && (!TiaMcp.Adapters.Contracts.PortedFamilies.IsMigratedDeclaration(name)
                || TiaMcp.Adapters.Contracts.PortedFamilies.UsesFoundationCandidate(name)
                || TiaMcp.Adapters.Contracts.PortedFamilies.CompatibilityTargets.ContainsKey(name)
                || !UseDeclaredReleasePolicy
                    && TiaMcp.Adapters.Contracts.PortedFamilies.ForTool(name).Name != "F04"))
            {

                var input = arguments ?? EmptyArguments();
                var rejected = ToolInvoker.Bind(name, input, out _);
                if (rejected != null)
                {
                    var fields = TargetInputError(name, input, rejected);
                    if (!ReferenceEquals(fields, rejected))
                        return FinishApproval(V4TargetReject(name, fields, CurrentBehaviorTargets("CallTool", JsonSerializer.SerializeToElement(new { name }))),
                                null);
                    return (await BridgeWait.On(ToolInvoker.InvokeAsync(name, input, ApprovalPreviewDepth.Value > 0)).ConfigureAwait(false)).Result;
                }
                var previous = FoundationBeforeDispatch;
                FoundationBeforeDispatch = beforeDispatch;
                try {
                    return (await BridgeWait.On(ToolInvoker.InvokeAsync(name, arguments ?? EmptyArguments(), ApprovalPreviewDepth.Value > 0))
                            .ConfigureAwait(false))
                        .Result;
                }
                finally { FoundationBeforeDispatch = previous; }
            }
#endif
            bool write = AllToolDescriptors(includeUnavailable: true).TryGetValue(name ?? "", out _)
                && ApprovalWrite(name ?? "", (arguments ?? EmptyArguments()).Json.GetRawText());
            bool disabled = write && McpApprovalContext.Value && !TiaOpenness.Shared.ApprovalSettings.Load(TiaOpenness.Shared.ApprovalSettings.SettingsPath)
                .Enabled;
            string requestId = Meta.Correlate(InvocationJournal.CorrelationId);
            using var audit = TiaOpenness.Shared.AuditInvocation.Begin(write, McpServer.InvocationHostTag, ReleaseKey, name ?? "", requestId);
            var fenced = write ? GenerationFenceRefusal(name ?? "", (arguments ?? EmptyArguments()).Json.GetRawText()) : null;
            if (fenced != null)
            {
                RecordCallRejection(name ?? "", arguments ?? EmptyArguments(), fenced);
                return AuditBridgeResult(audit, fenced, disabled);
            }
            var error = ToolInvoker.Bind(name, arguments ?? EmptyArguments(), out var call);
            if (error != null)
            {
                error = TargetInputError(name, arguments ?? EmptyArguments(), error);
                string target = error.Details is InvalidArgumentDetails { Parameter: not "arguments" } && CatalogView.Find(name) != null ? name : "CallTool";
                var rejected = V4TargetReject(target, error, current: CurrentBehaviorTargets("CallTool", JsonSerializer.SerializeToElement(new { name })));
                RecordCallRejection(name, arguments ?? EmptyArguments(), rejected);
                return AuditBridgeResult(audit, rejected, disabled);
            }
            if (string.Equals(name, "CallTool", StringComparison.Ordinal))
                return AuditBridgeResult(audit, V4Reject("CallTool", InvalidInput("name")), disabled);
            bool issued = false;
            try { return AuditBridgeResult(audit, await BridgeWait.On(ApprovedBridgeCallAsync(name, (arguments ?? EmptyArguments()).Json.GetRawText(),
                async () => { issued = true; return (await BridgeWait.On(call!.InvokeAsync(ApprovalPreviewDepth.Value > 0)).ConfigureAwait(false)).Result; },
                    beforeDispatch)).ConfigureAwait(false), disabled); }
            catch (OperationCanceledException) when (!issued) /* swallow(privacy): typed cancellation before dispatch does not expose exception text */
            { return AuditBridgeResult(audit, V4TargetReject(name, new Error("The request was cancelled before dispatch.",
                new CancelledDetails("tool-queue")), CurrentBehaviorTargets(name, (arguments ?? EmptyArguments()).Json)), disabled); }
            catch (Exception ex)
            { return AuditBridgeResult(audit, TargetFailure(name, ex, issued), disabled); }
        }

        private static CallToolResult AuditBridgeResult(TiaOpenness.Shared.AuditInvocation? audit, CallToolResult result, bool disabled = false)
        {
            result = FinishApproval(result, null, disabled);
            if (audit != null) audit.Complete(ResultBody(result)?.ToJsonString());
            return result;
        }

        internal static CallToolResult TargetFailure(string tool, Exception error, bool issued)
        {
            error = TiaOpenness.Shared.HostFailurePolicy.Unwrap(error);
            if (error.Data["workerRequestSent"] is false) issued = false;
            if (!issued && error.Data["workerLimitBytes"] is int workerLimit)
                return V4Reject(tool,
                        new Error("Worker request exceeds its byte limit. Nothing was executed.", new LimitExceededDetails("arguments", workerLimit, null)));
            if (!issued && error is OperationCanceledException)
                return V4Reject(tool, new Error("The request was cancelled before dispatch.", new CancelledDetails("tool-queue")));
            var classification = AllToolDescriptors(includeUnavailable: true).TryGetValue(tool, out var method)
                ? ClassificationOf(method) : null;
            var fallback = classification == null ? ToolMetadata.Find(tool) : null;
            string operation = classification?.Operation ?? fallback?.Operation ?? ToolTaxonomy.OperationOf(tool, null).Operation;
            bool readOnly = ApprovalPreviewDepth.Value > 0 || operation is "READ" or "OFFLINE" || operation == "SESSION"
                && (classification?.BatchRead ?? fallback?.BatchRead) == true;
            var kind = TiaOpenness.Shared.HostFailurePolicy.Classify(error, issued, readOnly,
                reportedCode: error is global::ModelContextProtocol.McpException mcp && mcp.ErrorCode == global::ModelContextProtocol.McpErrorCode
                    .InvalidParams ? -32602 : (int?)null,
                nativeRead: InvocationJournal.NativeCallIssued);
            var evidence = HostBehavior.FailureEvidence(error.GetType().Name, error.Message);
            var data = new JsonObject { ["evidence"] = JsonSerializer.SerializeToNode(evidence) };
            foreach (var retained in HostBehavior.RetainedRecoveryEvidence(error)) data[retained.Key] = retained.Value?.DeepClone();
            string? parameter = TiaOpenness.Shared.HostFailurePolicy.Parameter(error);
            string? message = HostBehavior.AdmissionDiagnostic(error);
            bool readCancelled = issued && readOnly && kind == TiaOpenness.Shared.HostFailureKind.Cancelled;
            return V4Result(tool, data, HostBehavior.FailureError(kind, parameter, evidence, message, stage: issued ? "tool-call" : "tool-queue"),
                readCancelled ? Outcome.ReadFailed : HostBehavior.OutcomeOf(kind), readCancelled ? Execution.ReadOnly : HostBehavior.ExecutionOf(kind),
                HostBehavior.CompletenessOf(kind), current: issued);
        }

#if TIA_ENGINE_HOST
        private static readonly System.Threading.AsyncLocal<Func<Task<CallToolResult?>>?> FoundationChecks = new();
        internal static Func<Task<CallToolResult?>>? FoundationBeforeDispatch { get => FoundationChecks.Value; set => FoundationChecks.Value = value; }
#endif

        internal static void ValidateCallerInputFiles(string tool, string arguments)
            => CallerInputFiles.Validate(tool, JsonNode.Parse(arguments)!.AsObject());

#if !TIA_ENGINE_HOST
        internal static string ReleaseKey => Siemens.EngineRouter.CompiledTiaMajorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
#endif
        internal static ToolArguments EmptyArguments() => new ToolArguments(JsonSerializer.SerializeToElement(new JsonObject()));
        internal static Error InvalidInput(string parameter) =>
            new Error("Input does not satisfy the declared contract.", new InvalidArgumentDetails(parameter, Array.Empty<string>()));
        private static JsonObject WritePreviewPlan(string tool, object arguments)
        {
            var normalized = JsonSerializer.SerializeToNode(arguments)!.AsObject();
            var inputs = CallerInputFiles.Snapshot(tool, normalized);
#if TIA_ENGINE_HOST
            var identity = TiaMcp.FoundationHost.EngineHostConfiguration.Current.Worker.Binding?.DeepClone();
#else
            bool? connected = null; string? project = null;
            ReadSessionState(ref connected, ref project);
            var identity = new JsonObject { ["connected"] = connected, ["project"] = project };
#endif
            return new JsonObject { ["plannedChanges"] = TiaOpenness.Shared.CallJournalPayload.RedactArguments(normalized),
                ["planHash"] = PendingApproval
                    .Hash(PendingApproval
                        .Canonical(JsonSerializer.SerializeToNode(new {
                            releaseKey = ReleaseKey, tool,
                            arguments = TiaOpenness.Shared.CallJournalPayload.ProtectPlanArguments(JsonNode.Parse(PendingApproval.Canonical(normalized))),
                            hostLifetime = TiaOpenness.Shared.CallJournalPayload.PlanHostLifetime, inputFiles = inputs, targetIdentity = identity }))),
                ["targetIdentity"] = identity, ["scope"] = "Normalized arguments, input file contents and target identity; native validation/readback occurs during execution." };
        }
        private static Error? WritePlanRefusal(JsonObject plan, string expectedPlanHash, string expectedProjectFile, bool writing)
        {
            string? expected = null;
            if (writing)
            {
                if (string.IsNullOrWhiteSpace(expectedProjectFile) || !System.IO.Path.IsPathRooted(expectedProjectFile)
                    || expectedProjectFile.Length >= 2 && expectedProjectFile[1] == ':'
                        && (expectedProjectFile.Length < 3 || expectedProjectFile[2] is not ('/' or '\\')))
                    return InvalidInput("expectedProjectFile");
                try { expected = TiaMcp.Adapters.Contracts.Candidates.CandidatePrimitives.CanonicalProject(expectedProjectFile); }
                catch (ArgumentException) /* swallow(parse-fallback): malformed paths are returned as typed input refusals. */
                { return InvalidInput("expectedProjectFile"); }
                catch (NotSupportedException) /* swallow(parse-fallback): unsupported path syntax is returned as a typed input refusal. */
                { return InvalidInput("expectedProjectFile"); }
                if (expectedPlanHash.Length == 0) return InvalidInput("expectedPlanHash");
            }
            if ((writing || expectedPlanHash.Length > 0) && expectedPlanHash != (string?)plan["planHash"])
                return new Error("Arguments, target, or input file changed; preview again.", new PlanStaleDetails(expectedPlanHash, "arguments-or-target"));
            if (writing)
            {
                string? actual =
                    (string?)(plan["targetIdentity"]?["identity"]?["projectPath"] ?? plan["targetIdentity"]?["projectFile"] ?? plan["targetIdentity"]
                        ?["projectPath"] ?? plan["targetIdentity"]?["project"]);
                if (string.IsNullOrWhiteSpace(actual)) return new Error("No project is bound.", new ProjectNotBoundDetails());
                if (TiaMcp.Adapters.Contracts.Candidates.CandidatePrimitives.CanonicalProject(actual!) != expected)
                    return new Error("The project binding differs from expectedProjectFile.", new IdentityMismatchDetails("project-binding", null, null));
            }
            return null;
        }
        private static CallToolResult WritePlanResult(CallToolResult result, JsonObject plan)
        {
            var body = ResultBody(result)?.DeepClone() as JsonObject;
            if (body?["data"] is JsonObject data)
            {
                foreach (var pair in plan) data[pair.Key] = pair.Value?.DeepClone();
                data["dryRun"] = false;
                result.StructuredContent = body;
                result.Content = new[] { new TextContentBlock { Text = V4Json.Serialize(body) } };
            }
            return result;
        }
        internal static CallToolResult RunWritePreview(string tool, bool dryRun, object arguments, Func<CallToolResult> execute, string expectedPlanHash = "",
                string expectedProjectFile = "", bool writing = true)
        {
            JsonObject plan;
            try { plan = WritePreviewPlan(tool, arguments); }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException error)
            {
                return !dryRun && expectedPlanHash.Length > 0
                    ? V4Reject(tool, new Error("Input file changed or became unreadable; preview again.", new PlanStaleDetails(expectedPlanHash, "input-changed")))
                    : TargetFailure(tool, error, false);
            }
            if (dryRun) { plan["dryRun"] = true; plan["mayHaveChanged"] = false; return V4Result(tool, plan); }
            if (WritePlanRefusal(plan, expectedPlanHash, expectedProjectFile, writing) is Error refusal) return V4Reject(tool, refusal);
            return WritePlanResult(execute(), plan);
        }
        internal static async Task<CallToolResult> RunWritePreviewAsync(string tool, bool dryRun, object arguments,
            Func<Task<CallToolResult>> execute, string expectedPlanHash = "", string expectedProjectFile = "", bool writing = true)
        {
            JsonObject plan;
            try { plan = WritePreviewPlan(tool, arguments); }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException error)
            {
                return !dryRun && expectedPlanHash.Length > 0
                    ? V4Reject(tool, new Error("Input file changed or became unreadable; preview again.", new PlanStaleDetails(expectedPlanHash, "input-changed")))
                    : TargetFailure(tool, error, false);
            }
            if (dryRun) { plan["dryRun"] = true; plan["mayHaveChanged"] = false; return V4Result(tool, plan); }
            if (WritePlanRefusal(plan, expectedPlanHash, expectedProjectFile, writing) is Error refusal) return V4Reject(tool, refusal);
            return WritePlanResult(await execute().ConfigureAwait(false), plan);
        }
        internal static Paging OffsetPage(int offset, int limit, int total) => new Paging(PagingMode.Offset, offset, limit,
            (long)offset + limit < total ? offset + limit : (int?)null, null, null, total, (long)offset + limit >= total);
        internal static CallToolResult V4Reject(string tool, Error error, JsonObject? data = null)
            => V4Result(tool, data, error, Outcome.RejectedBeforeOperation, Execution.NotStarted, Completeness.None);
        internal static CallToolResult V4TargetReject(string tool, Error error, bool current)
            => V4Result(tool, null, error, Outcome.RejectedBeforeOperation, Execution.NotStarted, Completeness.None, current: current);
        internal static Error TargetInputError(string name, ToolArguments arguments, Error error)
        {
            var target = CatalogView.Find(name);
            if (target == null || error.Details is not InvalidArgumentDetails argument
                || argument.Parameter != "arguments" && !argument.WholeArgumentValidation) return error;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (arguments.Json.EnumerateObject().Any(p => !names.Add(p.Name))) return error;
            var fields = new InputSchema(target.Tool.InputSchema).ValidateFields(arguments.Json);
            if (fields.Count == 0 || fields.Any(e => e.Details is not InvalidArgumentDetails)) return error;
            string parameters = string.Join(", ", fields.Select(e => ((InvalidArgumentDetails)e.Details).Parameter));
            return new Error("Missing or invalid target field(s): " + parameters + ".", new InvalidArgumentDetails(parameters, Array.Empty<string>()));
        }
        internal static bool CurrentBehaviorTargets(string tool, JsonElement arguments)
        {
            var targets = new List<string>();
            if (tool == "CallTool" || tool == "PreviewToolCall")
            {
                if (arguments.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) targets.Add(name.GetString()!);
            }
            else if (tool == "RunReadOnlyToolBatch" || tool == "PreviewToolBatch")
            {
                if (arguments.TryGetProperty("operations", out var operations) && operations.ValueKind == JsonValueKind.Array)
                    foreach (var operation in operations.EnumerateArray())
                        if (operation.ValueKind == JsonValueKind.Object && operation.TryGetProperty("name", out var name)
                            && name.ValueKind == JsonValueKind.String) targets.Add(name.GetString()!);
            }
            return targets.Any(target => ToolBehaviorPolicy(
                AllToolDescriptors(true).Keys.FirstOrDefault(n => string.Equals(n, target, StringComparison.Ordinal)) ?? target,
                BehaviorPolicy.NotApplicable) == BehaviorPolicy.Current);
        }
        internal static CallToolResult DiscloseTargets(CallToolResult result, string tool, JsonElement arguments)
        {
            var envelope = V4Json.Deserialize<Envelope>(((TextContentBlock)result.Content.Single()).Text);
            var disclosed = BehaviorCapabilities.Disclose(envelope, CurrentBehaviorTargets(tool, arguments));
            if (ReferenceEquals(envelope, disclosed)) return result;
            var mapped = McpResult.From(disclosed);
            return new CallToolResult { IsError = mapped.IsError, StructuredContent = JsonNode.Parse(mapped.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = mapped.Content[0].Text } } };
        }
        internal static CallToolResult V4Result(string tool, JsonObject? data, Paging? paging = null, bool current = false, bool completed = false)
            => V4Result(tool, data, null, Outcome.Succeeded, completed ? Execution.Completed : Execution.ReadOnly, Completeness.Complete, paging, current);

        internal static CallToolResult V4Result(string tool, JsonObject? data, Error? error,
            Outcome outcome, Execution execution, Completeness completeness, Paging? paging = null, bool current = false)
        {
#if TIA_ENGINE_HOST
            if (TiaMcp.FoundationHost.ReleaseCatalogData.Releases[ReleaseKey].DisclosePortedBehavior && TiaMcp.Adapters.Contracts.PortedFamilies.Contains(tool))
                current = true;
#endif
            var warnings = current ? new[] { new Warning(WarningCode.UnverifiedBehavior,
                "Native behavior retains the current policy; V4 native acceptance is pending.", new Dictionary<string, JsonElement>()) } : Array
                    .Empty<Warning>();
            var policy = current ? BehaviorPolicy.Current : BehaviorPolicy.NotApplicable;
            if (ToolBehaviorPolicy(tool, policy) == BehaviorPolicy.SafeV4)
            { policy = BehaviorPolicy.SafeV4; warnings = Array.Empty<Warning>(); }
            // An audited call answers with its audit id (the journal starts with the same id at dispatch, and
            // CorrelationId would invent a new one for a refusal before dispatch).
            var meta = new Meta(DateTimeOffset.UtcNow, ReleaseKey, tool,
                Meta.Correlate(TiaOpenness.Shared.AuditInvocation.CurrentRequestId ?? InvocationJournal.CorrelationId), outcome, execution,
                outcome == Outcome.Unknown || error?.Code == ErrorCode.SessionResetRequired, policy, completeness, paging, warnings);
            var mapped = McpResult.From(BehaviorCapabilities.Disclose(Envelope.Create(data, error, meta)));
            return new CallToolResult { IsError = mapped.IsError, StructuredContent = JsonNode.Parse(mapped.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = mapped.Content[0].Text } } };
        }
        internal static CallToolResult InfrastructureResult(string tool, ResponseMessage response, Paging? paging = null, bool reportOnly = false)
        {
            var data = response.Meta == null ? new JsonObject() : (JsonObject)response.Meta.DeepClone();
            data.Remove("success"); data.Remove("timestamp");
            data["summary"] = response.Message;
            if (response is ResponseStringList list) data["items"] =
                new JsonArray((list.Items ?? Enumerable.Empty<string>()).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
            return reportOnly || response.Meta?["success"]?.GetValue<bool?>() == true ? V4Result(tool, data, paging: paging)
                : V4Result(tool, data, new Error("Input does not satisfy the declared contract.",
                    new InternalErrorDetails(null)), Outcome.ReadFailed, Execution.ReadOnly, Completeness.None);
        }
        internal static CallToolResult ToolResult(object? result)
        {
            if (result is CallToolResult protocol)
            {
                if (protocol.Content.Count != 1 || protocol.Content[0] is not TextContentBlock text || protocol.StructuredContent == null
                    || !JsonNode.DeepEquals(JsonNode.Parse(text.Text), protocol.StructuredContent))
                    throw new System.IO.InvalidDataException("Tool did not return matching V4 content.");
                var envelope = V4Json.Deserialize<Envelope>(text.Text);
                if (protocol.IsError != !envelope.Ok) throw new System.IO.InvalidDataException("Tool status disagrees with its V4 envelope.");
                return protocol;
            }
            string json = JsonSerializer.Serialize(result, result?.GetType() ?? typeof(object), V4BindingJson);
            var mapped = McpResult.From(V4Json.Deserialize<Envelope>(json));
            return new CallToolResult { IsError = mapped.IsError, StructuredContent = JsonNode.Parse(mapped.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = mapped.Content[0].Text } } };
        }
        internal static JsonNode? ResultBody(CallToolResult result)
        {
            if (result.StructuredContent != null) return result.StructuredContent.DeepClone();
            if (result.Content.Count != 1 || !(result.Content.Single() is TextContentBlock text))
                return JsonSerializer.SerializeToNode(result, V4BindingJson);
            try { return JsonNode.Parse(text.Text); }
            catch (JsonException) /* swallow(parse-fallback): a plain-text target error keeps its complete MCP result in the batch */
            { return JsonSerializer.SerializeToNode(result, V4BindingJson); }
        }
        private static readonly JsonSerializerOptions V4BindingJson = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { PropertyNameCaseInsensitive = false };

        // Implemented in McpServer.Maintenance.cs (engine build); absent in the offline suite, where no portal exists.
        // Optional partial hooks must stay erasable in assemblies without an implementation.
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0040",
            Justification = "Optional partial hooks require implicit accessibility to remain erasable.")]
        static partial void ReadSessionState(ref bool? connected, ref string? project);

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0040",
            Justification = "Optional partial hooks require implicit accessibility to remain erasable.")]
        static partial void ReadSessionStateAsync(ref Task<(bool? Connected, string? Project)>? pending);

        internal static bool? ResultSucceeded(JsonNode? body)
        {
            if (body is not JsonObject obj) return null;
            if (obj["schemaVersion"]?.GetValue<int?>() == 4) return obj["ok"]?.GetValue<bool?>();
            return null;
        }
        private static int CommonPrefixLength(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length), i = 0;
            while (i < n && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i])) i++;
            return i;
        }

        private static JsonObject BridgeMeta(bool success)
        {
            return success ? ResponseMeta.Basic(true)
                : ToolBridgeStatus.Create(false);
        }

    }
}
