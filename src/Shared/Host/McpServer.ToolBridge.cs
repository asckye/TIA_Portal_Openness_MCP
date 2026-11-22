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
using TiaMcp.Adapters.Contracts;
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

        internal const string InvocationHostTag = "foundation";

        internal static string VersionToolProblem(string name)
            => VersionToolProblem(ReleaseKey, name);

        internal static string VersionToolProblem(string release, string name)
            => SessionCatalog() is { } catalog && catalog.Find(name, includeUnavailable: true) == null ? "Tool is not registered in this release."
                : Siemens.ToolVersionPolicy.ToolProblem(release, name);

        // Registration is a per-session catalog fact. Building a registry happens before any MCP session
        // exists; only calls inside a session can be refused as unregistered.
#if TIA_ENGINE_HOST
        private static IToolCatalogView? SessionCatalog() => TiaMcp.FoundationHost.EngineHostConfiguration.CurrentOrNull?.Catalog;
#else
        private static IToolCatalogView? SessionCatalog() => null;
#endif

        internal static string VersionCallProblem(string name, Func<string, string?> argument)
        {
            if (VersionToolProblem(name) is { Length: > 0 } problem) return problem;
            return Siemens.ToolVersionPolicy.CallProblem(ReleaseKey, name, argument);
        }

        private static string DuplicateArgumentProblem(JsonObject arguments)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in arguments)
                if (!seen.Add(pair.Key)) return "Duplicate argument names differing only by case are ambiguous. Nothing was executed.";
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
            var response = FindTools(query, limit, category, domain, offset);
            if (response.Meta?["success"]?.GetValue<bool?>() != true)
                return V4Reject("FindTools", InvalidInput("category/domain"));
            int count = (int)response.Meta!["matchingCount"]!;
            if (offset > count) return V4Reject("FindTools", InvalidInput("offset"));
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
            arguments = TiaMcp.FoundationHost.CanonicalParameterTool.ApplyDefaults(name, arguments ?? EmptyArguments(), out var defaultError);
            if (defaultError != null) return V4Reject(name, defaultError);
            if (TiaMcp.FoundationHost.PresetProjectGuard.Check(name,
                JsonNode.Parse(arguments.Value.Json.GetRawText())!.AsObject()) is { } presetRefusal) return V4Reject(name, presetRefusal);
            if (CatalogView.Find(name)?.Execution == "foundation")
            {
                var input = arguments ?? EmptyArguments();
                bool runtimeChannel = PortedFamilies.Contains(name) && PortedFamilies.ForTool(name).Name == "F04";
                bool bridgeValidates = runtimeChannel || TiaMcp.FoundationHost.ReleaseCatalogData.DeclaredPortInputAndPaging(ReleaseKey)
                    && PortedFamilies.IsMigratedDeclaration(name) && !PortedFamilies.UsesFoundationCandidate(name)
                    && !PortedFamilies.CompatibilityTargets.ContainsKey(name);
                if (bridgeValidates && GenerationFenceRefusal(name, input.Json.GetRawText()) is { } activeRun)
                    return FinishRefusal(() => { RecordCallRejection(name, input, activeRun); return activeRun; });
                var rejected = ToolInvoker.Bind(name, input, out _);
                if (rejected != null)
                {
                    if (bridgeValidates)
                        return FinishRefusal(() => BindingRefusal(name, input, rejected));
                    var fields = TargetInputError(name, input, rejected);
                    if (!ReferenceEquals(fields, rejected))
                        return FinishApproval(V4TargetReject(name, fields, CurrentBehaviorTargets("CallTool", JsonSerializer.SerializeToElement(new { name }))),
                                null);
                    return (await BridgeWait.On(ToolInvoker.InvokeAsync(name, input, ApprovalPreviewDepth.Value > 0)).ConfigureAwait(false)).Result;
                }
                // Runtime-channel declarations admit caller files at CallTool before the target can request approval.
                if (runtimeChannel)
                {
                    try { ValidateCallerInputFiles(name, input.Json.GetRawText()); }
                    catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException fileError)
                    { return FinishRefusal(() => TargetFailure(name, fileError, false, input.Json.GetRawText())); }
                }
                CallToolResult FinishRefusal(Func<CallToolResult> refusal)
                {
                    var json = input.Json.GetRawText();
                    using var rejectedAudit = AuditInvocation.Begin(ApprovalWrite(name, json), InvocationHostTag, ReleaseKey, name,
                        Meta.Correlate(InvocationJournal.CorrelationId));
                    return AuditBridgeResult(rejectedAudit, refusal(), McpApprovalContext.Value && ApprovalDisabled(name, json));
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
            bool disabled = McpApprovalContext.Value && ApprovalDisabled(name ?? "", (arguments ?? EmptyArguments()).Json.GetRawText());
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
                var rejected = BindingRefusal(name, arguments ?? EmptyArguments(), error);
                return AuditBridgeResult(audit, rejected, disabled);
            }
            if (string.Equals(name, "CallTool", StringComparison.Ordinal))
                return AuditBridgeResult(audit, V4Reject("CallTool", new Error("CallTool cannot invoke itself. Pass the name of the target tool.",
                    new InvalidArgumentDetails("name", Array.Empty<string>()))), disabled);
            bool issued = false;
            try { return AuditBridgeResult(audit, await BridgeWait.On(ApprovedBridgeCallAsync(name, (arguments ?? EmptyArguments()).Json.GetRawText(),
                async () =>
                {
                    issued = true;
                    return (await BridgeWait.On(call!.InvokeAsync(ApprovalPreviewDepth.Value > 0)).ConfigureAwait(false)).Result;
                },
                    beforeDispatch)).ConfigureAwait(false), disabled); }
            catch (OperationCanceledException) when (!issued) /* swallow(privacy): typed cancellation before dispatch does not expose exception text */
            { return AuditBridgeResult(audit, V4TargetReject(name, new Error("The request was cancelled before dispatch.",
                new CancelledDetails("tool-queue")), CurrentBehaviorTargets(name, (arguments ?? EmptyArguments()).Json)), disabled); }
            catch (Exception ex)
            { return AuditBridgeResult(audit, TargetFailure(name, ex, issued, (arguments ?? EmptyArguments()).Json.GetRawText()), disabled); }
        }

        private static CallToolResult BindingRefusal(string name, ToolArguments input, Error error)
        {
            error = TargetInputError(name, input, error);
            string target = error.Details is InvalidArgumentDetails { Parameter: not "" } && CatalogView.Find(name) != null ? name : "CallTool";
            var rejected = V4TargetReject(target, error, current: CurrentBehaviorTargets("CallTool", JsonSerializer.SerializeToElement(new { name })));
            RecordCallRejection(name, input, rejected);
            return rejected;
        }

        private static CallToolResult AuditBridgeResult(TiaOpenness.Shared.AuditInvocation? audit, CallToolResult result, bool disabled = false)
        {
            result = FinishApproval(result, null, disabled);
            if (audit != null) audit.Complete(ResultBody(result)?.ToJsonString());
            return result;
        }

        internal static CallToolResult TargetFailure(string tool, Exception error, bool issued, string? arguments = null)
        {
            error = TiaOpenness.Shared.HostFailurePolicy.Unwrap(error);
            if (!issued && error is TiaMcp.Adapters.Contracts.AdapterPreconditionException { Condition: SeedReferenceLayout.Condition } layout)
                return V4Reject(tool, new Error(layout.Message, new PreconditionFailedDetails(layout.Condition, null, layout.ParamName)));
#if TIA_ENGINE_HOST
            if (TiaOpenness.Shared.HostFailurePolicy.Precondition(error) is { Condition: "worker-call-running" } running)
                return V4Reject(tool, new Error(running.Message,
                    new PreconditionFailedDetails(running.Condition, null, running.ParamName)));
#endif
            if (error.Data["workerRequestSent"] is false || error.Data["foundationRequestSent"] is false) issued = false;
            if (!issued && error.Data["workerLimitBytes"] is int workerLimit)
                return V4Reject(tool,
                        new Error("Worker request exceeds its byte limit. Nothing was executed.", new LimitExceededDetails("arguments", workerLimit, null)));
            if (!issued && error is OperationCanceledException)
                return V4Reject(tool, new Error("The request was cancelled before dispatch.", new CancelledDetails("tool-queue")));
            var classification = AllToolDescriptors(includeUnavailable: true).TryGetValue(tool, out var method)
                ? ClassificationOf(method) : null;
            var fallback = classification == null ? ToolMetadata.Find(tool) : null;
            string operation = classification?.Operation ?? fallback?.Operation ?? ToolTaxonomy.OperationOf(tool, null).Operation;
#if TIA_ENGINE_HOST
            if (arguments == null && ProgressRequest.Value?.Params is { } request && request.Name == tool)
                arguments = JsonSerializer.Serialize(request.Arguments ?? new Dictionary<string, JsonElement>());
#endif
            JsonNode? supplied;
            try { supplied = arguments == null ? null : JsonNode.Parse(arguments); }
            catch (JsonException) /* swallow(parse-fallback): malformed input cannot describe an issued tool's preview mode */
            { return V4Reject(tool, InvalidInput("/arguments", "object")); }
            if (arguments != null && supplied is not JsonObject) return V4Reject(tool, InvalidInput("/arguments", "object"));
            bool preview = supplied is JsonObject input && TryDryRunDefault(tool, out bool defaultPreview)
                && HostBehavior.IsPreview(input, defaultPreview);
            bool readOnly = ApprovalPreviewDepth.Value > 0 || preview
                || operation is "READ" or "OFFLINE" || operation == "SESSION"
                && (classification?.BatchRead ?? fallback?.BatchRead) == true;
            var kind = TiaOpenness.Shared.HostFailurePolicy.Classify(error, issued, readOnly,
                reportedCode: error is global::ModelContextProtocol.McpException mcp && mcp.ErrorCode == global::ModelContextProtocol.McpErrorCode
                    .InvalidParams ? -32602 : (int?)null,
                nativeRead: InvocationJournal.NativeCallIssued);
            bool scriptUnknown = tool == "SetUnifiedGlobalScript" && kind == TiaOpenness.Shared.HostFailureKind.Unknown;
            var evidence = scriptUnknown ? HostBehavior.WorkerReplyEvidence(tool, error)! : HostBehavior.FailureEvidence(error.GetType().Name, error.Message);
            var data = new JsonObject { ["evidence"] = JsonSerializer.SerializeToNode(evidence) };
            if (!scriptUnknown)
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
        internal static Error InvalidInput(string parameter, string expected, params string[] allowedValues) =>
            new Error("Input does not satisfy the declared contract.",
                new InvalidArgumentDetails(parameter, allowedValues, expected: expected));
        internal static InvalidArgumentDetails ArgumentDetails(string tool, string? parameter, JsonObject? evidence = null)
        {
            string? Text(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
            parameter ??= Text(evidence?["parameter"]) ?? Text(evidence?["rejectedParameter"]);
            var target = parameter == null ? null : SessionCatalog()?.Find(tool, includeUnavailable: true);
            if (parameter == null || target == null)
                return new InvalidArgumentDetails("", Array.Empty<string>());
            var properties = target.Tool.InputSchema.GetProperty("properties");
            var fields = parameter.Split(new[] { '/', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(field => field.Trim()).Where(field => properties.TryGetProperty(field, out _)).Distinct(StringComparer.Ordinal).ToArray();
            if (fields.Length == 0) return new InvalidArgumentDetails("", Array.Empty<string>());
            parameter = fields[0];
            var member = properties.GetProperty(parameter);
            var allowed = member.TryGetProperty("enum", out var values)
                ? values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray()
                : Array.Empty<string>();
            return new InvalidArgumentDetails(parameter, allowed, fields,
                expected: new InputSchema(target.Tool.InputSchema).ExpectedParameter(parameter));
        }
        internal static NotFoundDetails MissingTargetDetails(string tool, string? parameter = null, JsonObject? evidence = null)
        {
            var pointer = ArgumentDetails(tool, parameter, evidence).Parameter;
            return new NotFoundDetails(pointer.Length == 0 ? null : pointer);
        }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, OnlinePlanLifetime> OnlinePlans =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, OnlinePlanLifetime>();
        private static readonly object DefaultOnlinePlanSession = new object();
        private static readonly System.Threading.AsyncLocal<Func<DateTimeOffset>?> OnlinePlanClock =
            new System.Threading.AsyncLocal<Func<DateTimeOffset>?>();
        internal static Func<DateTimeOffset>? OnlinePlanClockForTests { get => OnlinePlanClock.Value; set => OnlinePlanClock.Value = value; }
        private static DateTimeOffset OnlinePlanNow => OnlinePlanClock.Value?.Invoke() ?? DateTimeOffset.UtcNow;
        private static OnlinePlanLifetime CurrentOnlinePlans => OnlinePlans.GetValue(CurrentApprovalSession() ?? DefaultOnlinePlanSession,
            _ => new OnlinePlanLifetime());
        internal static Error? OnlinePlanRefusal(string tool, JsonObject arguments)
        {
            if (IsReadOnlyApprovalPreview || HostBehavior.IsPreview(arguments, true)
                || ToolTaxonomy.OperationOf(tool, null).Operation != "ONLINE-WRITE"
                || arguments["expectedPlanHash"] is not JsonValue hash || !hash.TryGetValue<string>(out var value)
                || !PendingApproval.IsHash(value)) return null;
            return CurrentOnlinePlans.Refusal(value, OnlinePlanNow);
        }

        internal static CallToolResult RunOnlineNetworkCheck(string tool, string target, Func<CallToolResult> call)
        {
            var network = TiaMcpServer.Siemens.DownloadTargetPolicy.NetworkSnapshot(target);
            var result = call();
            if (ResultBody(result) is not JsonObject body) return result;
            if (body["data"] is not JsonObject data) body["data"] = data = new JsonObject();
            data["localNetwork"] = network;
            result.StructuredContent = body;
            result.Content = new[] { new TextContentBlock { Text = V4Json.Serialize(body) } };
            return result;
        }

        private static JsonObject WritePreviewPlan(string tool, object arguments, bool writing, Func<JsonArray>? plannedFiles)
        {
            var normalized = JsonSerializer.SerializeToNode(arguments)!.AsObject();
            if (plannedFiles != null) CallerInputFiles.RefuseHumanSettingsTargets(normalized);
            var seedFiles = plannedFiles?.Invoke();
            var inputs = CallerInputFiles.Snapshot(tool, normalized);
            if (seedFiles != null) inputs.Add(new JsonObject { ["parameter"] = "referenceDir", ["plannedFiles"] = seedFiles.DeepClone() });
#if TIA_ENGINE_HOST
            var identity = TiaMcp.FoundationHost.EngineHostConfiguration.Current.Worker.Binding?.DeepClone();
#else
            bool? connected = null; string? project = null;
            ReadSessionState(ref connected, ref project);
            var identity = new JsonObject { ["connected"] = connected, ["project"] = project };
#endif
            var plan = new JsonObject { ["plannedChanges"] = TiaOpenness.Shared.CallJournalPayload.RedactArguments(normalized),
                ["planHash"] = PendingApproval
                    .Hash(PendingApproval
                        .Canonical(JsonSerializer.SerializeToNode(new {
                            releaseKey = ReleaseKey, tool,
                            arguments = TiaOpenness.Shared.CallJournalPayload.ProtectPlanArguments(JsonNode.Parse(PendingApproval.Canonical(normalized))),
                            hostLifetime = TiaOpenness.Shared.CallJournalPayload.PlanHostLifetime, inputFiles = inputs, targetIdentity = identity }))),
                ["targetIdentity"] = identity, ["targetVerified"] = false, ["scope"] =
                    "Normalized arguments, input file contents and cached target identity; the target is not verified. Native validation/readback occurs " +
                        "during execution."
                    };
            if (seedFiles != null) plan["plannedFiles"] = seedFiles;
            if (writing)
            {
                bool targetBound = PlanProjectFile(identity) != null;
                plan["targetBound"] = targetBound;
                if (!targetBound) plan["scope"] = "No project is bound, so this preview covers only the arguments and input files and names no target. "
                    + "The target is not verified. Applying it refuses with PROJECT_NOT_BOUND; bind a project and preview again.";
            }
            return plan;
        }

        private static string? PlanProjectFile(JsonNode? targetIdentity)
        {
            string? project = (string?)(targetIdentity?["identity"]?["projectPath"] ?? targetIdentity?["projectFile"]
                ?? targetIdentity?["projectPath"] ?? targetIdentity?["project"]);
            return string.IsNullOrWhiteSpace(project) ? null : project;
        }

        private static Error? WritePlanRefusal(JsonObject plan, string expectedPlanHash, string expectedProjectFile, bool writing)
        {
            string? expected = null;
            if (writing)
            {
                if (string.IsNullOrWhiteSpace(expectedProjectFile) || !System.IO.Path.IsPathRooted(expectedProjectFile)
                    || expectedProjectFile.Length >= 2 && expectedProjectFile[1] == ':'
                        && (expectedProjectFile.Length < 3 || expectedProjectFile[2] is not ('/' or '\\')))
                    return InvalidInput("expectedProjectFile", "absolute file path");
                try { expected = TiaMcp.Adapters.Contracts.Candidates.CandidatePrimitives.CanonicalProject(expectedProjectFile); }
                catch (ArgumentException) /* swallow(parse-fallback): malformed paths are returned as typed input refusals. */
                { return InvalidInput("expectedProjectFile", "absolute file path"); }
                catch (NotSupportedException) /* swallow(parse-fallback): unsupported path syntax is returned as a typed input refusal. */
                { return InvalidInput("expectedProjectFile", "absolute file path"); }
                if (expectedPlanHash.Length == 0) return InvalidInput("expectedPlanHash");
            }
            if ((writing || expectedPlanHash.Length > 0) && expectedPlanHash != (string?)plan["planHash"])
                return new Error("This plan no longer matches the preview: arguments, target, input files or the TIA binding differ. "
                    + "A reconnect or a host restart also changes the binding. Nothing ran; preview again.",
                    new PlanStaleDetails(expectedPlanHash, "arguments-or-target"));
            if (writing)
            {
                string? actual = PlanProjectFile(plan["targetIdentity"]);
                if (actual == null) return new Error("No project is bound.", new ProjectNotBoundDetails());
                if (TiaMcp.Adapters.Contracts.Candidates.CandidatePrimitives.CanonicalProject(actual!) != expected)
                    return new Error("The project binding differs from expectedProjectFile.", new IdentityMismatchDetails("project-binding", null, null));
            }
            return null;
        }
        private static CallToolResult WritePlanResult(CallToolResult result, JsonObject plan, bool dryRun, bool writing)
        {
            var body = ResultBody(result)?.DeepClone() as JsonObject;
            if (body?["data"] is JsonObject data)
            {
                foreach (var pair in plan)
                    if (pair.Key is not ("targetVerified" or "scope")) data[pair.Key] = pair.Value?.DeepClone();
                data["dryRun"] = writing ? false : dryRun;
                if (!writing) data["readOnlyInvocation"] = true;
                result.StructuredContent = body;
                result.Content = new[] { new TextContentBlock { Text = V4Json.Serialize(body) } };
            }
            return result;
        }
        private static Error? WritePreviewArgumentRefusal(string tool, object arguments)
        {
            if (tool != "EnsureSubnet") return null;
            string? subnetType = (string?)JsonSerializer.SerializeToNode(arguments)!["subnetType"];
            return TiaMcp.Adapters.Hardware.SubnetTypePolicy.IsSupported(subnetType) ? null
                : new Error(TiaMcp.Adapters.Hardware.SubnetTypePolicy.Message,
                    new InvalidArgumentDetails("subnetType", TiaMcp.Adapters.Hardware.SubnetTypePolicy.AllowedValues,
                        expected: "Industrial Ethernet/PROFINET subnet type"));
        }
        private static CallToolResult? PrepareWritePreview(string tool, bool dryRun, object arguments, string expectedPlanHash,
            bool writing, out JsonObject plan, Func<JsonArray>? plannedFiles = null)
        {
            plan = null!;
            if (dryRun && WritePreviewArgumentRefusal(tool, arguments) is Error argumentRefusal) return V4Reject(tool, argumentRefusal);
            try { plan = WritePreviewPlan(tool, arguments, writing, plannedFiles); }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException error) when (error.Condition == SeedReferenceLayout.Condition
                || tool == "ImportTechnologyObjectsFromDirectory" && error.ParamName == "regexName")
            { return TargetFailure(tool, error, false); }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException error)
            {
                return !dryRun && expectedPlanHash.Length > 0
                    ? V4Reject(tool,
                            new Error("Input file changed or became unreadable; preview again.", new PlanStaleDetails(expectedPlanHash, "input-changed")))
                    : TargetFailure(tool, error, false);
            }
            if (ToolTaxonomy.OperationOf(tool, null).Operation == "ONLINE-WRITE")
            {
                string fingerprint = (string)plan["planHash"]!;
                if (dryRun && !IsReadOnlyApprovalPreview)
                {
                    var issued = CurrentOnlinePlans.Preview(fingerprint, OnlinePlanNow,
                        ApprovalSettings.Load(ApprovalSettings.SettingsPath).OnlinePlanLifetimeSeconds);
                    plan["planHash"] = issued.Hash;
                    plan["expiresUtc"] = issued.Expires.ToString("O");
                }
                else if (PendingApproval.IsHash(expectedPlanHash))
                {
                    if (!dryRun && CurrentOnlinePlans.Refusal(expectedPlanHash, OnlinePlanNow) is Error expired)
                        return V4Reject(tool, expired);
                    if (CurrentOnlinePlans.Matches(expectedPlanHash, fingerprint)) plan["planHash"] = expectedPlanHash;
                }
            }
            if (tool == "DownloadPlc")
            {
                var target = JsonSerializer.SerializeToNode(arguments);
                string kind = HostBehavior.DownloadTargetKind((string?)target?["targetIpAddress"], (string?)target?["pgPcInterface"]);
                plan["targetKind"] = kind;
                plan["requiresPhysicalConfirmation"] = kind == "physical";
                if (kind == "physical") plan["executionRequirement"] = "Set confirmPhysicalTarget=true in the reviewed preview and execution. "
                    + "A human must approve in the Workbench even when approval is switched off.";
            }
            return null;
        }
        internal static CallToolResult RunWritePreview(string tool, bool dryRun, object arguments, Func<CallToolResult> execute, string expectedPlanHash = "",
                string expectedProjectFile = "", bool writing = true, Func<JsonArray>? plannedFiles = null)
        {
            if (PrepareWritePreview(tool, dryRun, arguments, expectedPlanHash, writing, out var plan, plannedFiles) is CallToolResult rejected) return rejected;
            if (dryRun && writing) { plan["dryRun"] = true; plan["mayHaveChanged"] = false; return V4Result(tool, plan); }
            if (WritePlanRefusal(plan, expectedPlanHash, expectedProjectFile, writing) is Error refusal) return V4Reject(tool, refusal);
            return WritePlanResult(execute(), plan, dryRun, writing);
        }
        internal static async Task<CallToolResult> RunWritePreviewAsync(string tool, bool dryRun, object arguments,
            Func<Task<CallToolResult>> execute, string expectedPlanHash = "", string expectedProjectFile = "", bool writing = true)
        {
            if (PrepareWritePreview(tool, dryRun, arguments, expectedPlanHash, writing, out var plan) is CallToolResult rejected) return rejected;
            if (dryRun && writing) { plan["dryRun"] = true; plan["mayHaveChanged"] = false; return V4Result(tool, plan); }
            if (WritePlanRefusal(plan, expectedPlanHash, expectedProjectFile, writing) is Error refusal) return V4Reject(tool, refusal);
            return WritePlanResult(await execute().ConfigureAwait(false), plan, dryRun, writing);
        }
        internal static CallToolResult RunNativeWritePreview(string tool, bool dryRun, object arguments,
            Func<CallToolResult> preview, Func<CallToolResult> execute, string expectedPlanHash = "", string expectedProjectFile = "")
        {
            if (PrepareWritePreview(tool, dryRun, arguments, expectedPlanHash, writing: true, out var plan) is CallToolResult rejected) return rejected;
            if (!dryRun)
            {
                if (WritePlanRefusal(plan, expectedPlanHash, expectedProjectFile, writing: true) is Error refusal) return V4Reject(tool, refusal);
                return WritePlanResult(execute(), plan, dryRun: false, writing: true);
            }
            var result = preview();
            var body = ResultBody(result) as JsonObject;
            if (body == null || ResultSucceeded(body) != true) return result;
            if (body["data"] is not JsonObject data) body["data"] = data = new JsonObject();
            foreach (string field in new[] { "planHash", "plannedChanges", "targetIdentity", "targetBound" })
                data[field] = plan[field]?.DeepClone();
            if ((bool?)plan["targetBound"] == false) data["scope"] = plan["scope"]?.DeepClone();
            data["dryRun"] = true;
            data["mayHaveChanged"] = false;
            result.StructuredContent = body;
            result.Content = new[] { new TextContentBlock { Text = V4Json.Serialize(body) } };
            return result;
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
                || argument.Parameter != "" && !argument.WholeArgumentValidation) return error;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (arguments.Json.EnumerateObject().Any(p => !names.Add(p.Name))) return error;
            var schema = new InputSchema(target.Tool.InputSchema);
            var fields = schema.ValidateFields(arguments.Json);
            if (fields.Count == 0 || fields.Any(e => e.Details is not InvalidArgumentDetails)) return error;
            return new Error(schema.FieldReport(arguments.Json),
                new InvalidArgumentDetails(argument.Parameter, argument.AllowedValues, argument.Parameters, argument.Expected));
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
            var warnings = current ? new[] { new Warning(WarningCode.UnverifiedBehavior,
                BehaviorCapabilities.CurrentDisclosure, new Dictionary<string, JsonElement>()) } : Array
                    .Empty<Warning>();
            var policy = current ? BehaviorPolicy.Current : BehaviorPolicy.NotApplicable;
            if (ToolBehaviorPolicy(tool, policy) == BehaviorPolicy.SafeV4)
            { policy = BehaviorPolicy.SafeV4; warnings = Array.Empty<Warning>(); }
            if (paging?.Complete == false && completeness == Completeness.Partial)
                warnings = warnings.Append(new Warning(WarningCode.IncompleteData, "More results are available on the next page.",
                    new Dictionary<string, JsonElement>())).ToArray();
            // An audited call answers with its audit id (the journal starts with the same id at dispatch, and
            // CorrelationId would invent a new one for a refusal before dispatch).
            var meta = new Meta(DateTimeOffset.UtcNow, ReleaseKey, tool,
                Meta.Correlate(TiaOpenness.Shared.AuditInvocation.CurrentRequestId ?? InvocationJournal.CorrelationId), outcome, execution, policy,
                    completeness, paging, warnings);
            var mapped = McpResult.From(BehaviorCapabilities.Disclose(Envelope.Create(data, error, meta)));
            return new CallToolResult { IsError = mapped.IsError, StructuredContent = JsonNode.Parse(mapped.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = mapped.Content[0].Text } } };
        }
        internal static CallToolResult InfrastructureResult(string tool, ResponseMessage response, Paging? paging = null, Error? rejection = null)
        {
            var data = response.Meta == null ? new JsonObject() : (JsonObject)response.Meta.DeepClone();
            data.Remove("success"); data.Remove("timestamp");
            data["summary"] = response.Message;
            if (response is ResponseStringList list) data["items"] =
                new JsonArray((list.Items ?? Enumerable.Empty<string>()).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
            if (rejection != null)
                return V4Result(tool, data, rejection, Outcome.RejectedBeforeOperation, Execution.NotStarted, Completeness.None);
            return response.Meta?["success"]?.GetValue<bool?>() == true ? V4Result(tool, data, null, Outcome.Succeeded,
                Execution.ReadOnly, paging?.Complete == false ? Completeness.Partial : Completeness.Complete, paging)
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
