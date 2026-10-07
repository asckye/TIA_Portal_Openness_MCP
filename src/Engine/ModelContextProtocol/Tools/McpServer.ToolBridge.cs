using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcpServer.ModelContextProtocol
{
    // The escape hatch that lets the lite roster be the default without losing anything.
    //
    // The lite roster limits per-turn schema size and stays within host tool-count limits.
    // FindTools + CallTool provide on-demand access to the entire roster: search when a tool is needed,
    // read its signature, then invoke it. Tools outside the advertised list remain discoverable and callable.
    public static partial class McpServer
    {
        private static ToolCatalog? _bridgeCatalog;
        private static Func<bool> _bridgeIsLiteProfile = () => false;
        private static ISet<string> _bridgeLiteToolNames = new HashSet<string>(StringComparer.Ordinal);

        static McpServer() { ConfigureToolBridgeProfile(); }
        static partial void ConfigureToolBridgeProfile();

        internal static void ConfigureToolBridge(ToolCatalog catalog, Func<bool> isLiteProfile, ISet<string> liteToolNames)
        {
            _bridgeCatalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _bridgeIsLiteProfile = isLiteProfile ?? throw new ArgumentNullException(nameof(isLiteProfile));
            _bridgeLiteToolNames = liteToolNames ?? throw new ArgumentNullException(nameof(liteToolNames));
        }

        internal static Dictionary<string, MethodInfo> AllToolMethods(bool includeUnavailable = false)
        {
            return AvailableToolMethods((_bridgeCatalog ?? ToolCatalog.Engine).Methods, includeUnavailable);
        }

        private static Dictionary<string, MethodInfo> AvailableToolMethods(IEnumerable<KeyValuePair<string, MethodInfo>> methods, bool includeUnavailable)
            => methods.Where(kv => includeUnavailable || VersionToolProblem(kv.Key).Length == 0)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        internal static string VersionToolProblem(string name)
            => Siemens.ToolVersionPolicy.ToolProblem(Siemens.EngineRouter.CompiledTiaMajorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), name);

        internal static string VersionCallProblem(string name, Func<string, string?> argument)
            => Siemens.ToolVersionPolicy.CallProblem(Siemens.EngineRouter.CompiledTiaMajorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), name, argument);

        private static string DuplicateArgumentProblem(JsonObject arguments)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in arguments)
                if (!seen.Add(pair.Key)) return "Duplicate argument names differing only by case are ambiguous: " + pair.Key + ". Nothing was executed.";
            return "";
        }

        internal static string ToolDescription(MethodInfo m)
        {
            var d = m.GetCustomAttribute<DescriptionAttribute>();
            return (d == null ? "" : d.Description) + TiaOpenness.Shared.ToolUsageCatalog.Hint(m.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? m.Name);
        }

        /// <summary>Renders one tool's signature the way the model needs to call it through CallTool.</summary>
        // Keep infrastructure parameters out of bridge signatures and argument binding.
        internal static bool IsInfrastructureParameter(Type type)
            => type.Name == "IMcpServer" || (type.IsGenericType && type.GetGenericTypeDefinition().Name.StartsWith("RequestContext", StringComparison.Ordinal))
               || (type.Namespace != null && type.Namespace.StartsWith("ModelContextProtocol", StringComparison.Ordinal));

        private static string RenderSignature(string name, MethodInfo m)
        {
            var parts = new List<string>();
            foreach (var p in m.GetParameters())
            {
                string t = FriendlyTypeName(p.ParameterType);
                // Optional params are what a model most often gets wrong, so show the actual
                // default rather than a bare "?".
                if (!p.HasDefaultValue) { parts.Add(p.Name + ": " + t); continue; }
                string def;
                if (p.DefaultValue == null) def = "null";
                else if (p.DefaultValue is bool) def = ((bool)p.DefaultValue) ? "true" : "false";
                else if (p.DefaultValue is string) def = "\"" + p.DefaultValue + "\"";
                else def = Convert.ToString(p.DefaultValue, System.Globalization.CultureInfo.InvariantCulture) ?? "null";
                parts.Add(p.Name + "?: " + t + " = " + def);
            }
            return name + "(" + string.Join(", ", parts) + ")";
        }

        private static string FriendlyTypeName(Type t)
        {
            var u = Nullable.GetUnderlyingType(t) ?? t;
            if (u == typeof(string)) return "string";
            if (u == typeof(bool)) return "boolean";
            if (u == typeof(int) || u == typeof(long)) return "integer";
            if (u == typeof(double) || u == typeof(float) || u == typeof(decimal)) return "number";
            if (u.IsArray) return FriendlyTypeName(u.GetElementType()!) + "[]";
            return u.Name;
        }

        [McpServerTool(Name = "ListToolCategories"), Description("[L0][Meta][READ] List the tool taxonomy, layers and current full-catalog counts. Use FindTools to browse a category or domain.")]
        public static CallToolResult ListToolCategoriesV4()
            => InfrastructureResult("ListToolCategories", ListToolCategories());

        [Description(
            "[L0][Meta][READ] The tool taxonomy: 7 categories (session, project, plc, plc-online, hardware, hmi, runtime), " +
            "their domains (the [L?][Domain] tag every tool description starts with), the meaning of layers L0/L1/L2, and live tool counts per category/domain/operation. " +
            "Call this first to orient, then FindTools(category=… or domain=…) to browse one area.")]
        public static ResponseStringList ListToolCategories()
        {
            try
            {
                var all = AllToolMethods();
                var parsed = all.Select(kv => (Name: kv.Key, Tag: ToolTaxonomy.For(kv.Key), Op: ToolTaxonomy.OperationOf(kv.Key, ToolDescription(kv.Value)))).ToList();
                var lines = new List<string>();
                var categories = new JsonArray();
                foreach (var c in ToolTaxonomy.Categories)
                {
                    var inCategory = parsed.Where(p => ToolTaxonomy.CategoryOf(p.Tag.Domain) == c.Key).ToList();
                    lines.Add($"{c.Key} — {c.NameZh} / {c.NameEn} ({inCategory.Count} tools): {c.Description}");
                    var domains = new JsonArray();
                    foreach (var d in c.Domains)
                    {
                        var inDomain = inCategory.Where(p => string.Equals(p.Tag.Domain, d, StringComparison.OrdinalIgnoreCase)).ToList();
                        lines.Add($"    [{d}] {inDomain.Count} tools; layers " + string.Join("/", inDomain.GroupBy(p => p.Tag.Layer).OrderBy(g => g.Key).Select(g => g.Key + "=" + g.Count())));
                        domains.Add(new JsonObject { ["domain"] = d, ["tools"] = inDomain.Count,
                            ["operations"] = new JsonObject(inDomain.GroupBy(p => p.Op.Operation).OrderBy(g => g.Key).Select(g => new KeyValuePair<string, JsonNode?>(g.Key, g.Count()))), ["operationsInferred"] = inDomain.Count(p => p.Op.Inferred) });
                    }
                    categories.Add(new JsonObject { ["key"] = c.Key, ["nameZh"] = c.NameZh, ["nameEn"] = c.NameEn, ["description"] = c.Description, ["tools"] = inCategory.Count, ["domains"] = domains });
                }
                var unknown = parsed.Where(p => !ToolTaxonomy.IsKnownDomain(p.Tag.Domain)).Select(p => p.Name + "(" + p.Tag.Domain + ")").ToList();
                var meta = BridgeMeta(true);
                meta["categories"] = categories;
                meta["layers"] = new JsonObject(ToolTaxonomy.LayerMeaning.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value)));
                meta["operations"] = new JsonObject(ToolTaxonomy.OperationMeaning.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value)));
                meta["uncategorized"] = new JsonArray(unknown.Select(u => (JsonNode)u).ToArray());
                meta["toolCount"] = all.Count;
                return new ResponseStringList
                {
                    Message = $"{all.Count} tools in {ToolTaxonomy.Categories.Count} categories" + (unknown.Count > 0 ? $"; {unknown.Count} tool(s) carry an unregistered domain tag: {string.Join(", ", unknown)}" : "") + ". Use FindTools(category=…) or FindTools(domain=…) to list one area.",
                    Items = lines,
                    Meta = meta,
                };
            }
            catch (Exception ex)
            {
                return new ResponseStringList { Message = "ListToolCategories failed: " + ex.Message, Meta = BridgeMeta(false) };
            }
        }

        [McpServerTool(Name = "FindTools"), Description("[L0][Meta][READ] Search the full version-gated catalog, including tools outside lite. Returns names, signatures, descriptions and examples. Use CallTool with an arguments object to invoke a match.")]
        public static CallToolResult FindToolsV4(
            [Description("Space-separated capability words; empty lists all tools.")] string query = "",
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
            response.Items = lines.Skip(offset * 3L > int.MaxValue ? int.MaxValue : offset * 3).Take(limit > int.MaxValue / 3 ? int.MaxValue : limit * 3).ToArray();
            return InfrastructureResult("FindTools", response, OffsetPage(offset, limit, count));
        }

        [Description(
            "[L0][Meta][READ] Search the FULL tool roster, including tools not listed in this session. " +
            "The server ships a small 'lite' roster by default so every host can load it; everything else is reached through this tool plus CallTool. " +
            "USE THIS whenever the visible tools do not cover what you need, before concluding the server cannot do something. " +
            "Search by capability words, not exact names: 'watch table', 'HMI screen', 'download', 'cross reference', 'GSD'. " +
            "Optionally restrict to one category (session/project/plc/plc-online/hardware/hmi/runtime) or one domain tag (e.g. HMI-Unified, PLC-Online) — see ListToolCategories. " +
            "Returns each match's exact name, parameter signature with defaults, and full description; then invoke it with CallTool.")]
        public static ResponseStringList FindTools(
            [Description("query: space-separated words matched against tool names and descriptions, e.g. 'export watch table'. Empty lists the whole roster (or the whole category/domain when one is given).")] string query = "",
            [Description("limit: max tools to return (default 12). Raise it for a broad survey.")] int limit = 12,
            [Description("category: optional category key from ListToolCategories (session, project, plc, plc-online, hardware, hmi, runtime).")] string category = "",
            [Description("domain: optional exact domain tag from ListToolCategories, e.g. 'HMI-Unified' or 'PLC-Software'. Case-insensitive.")] string domain = "")
        {
            try
            {
                var all = AllToolMethods();
                if (limit <= 0) limit = 12;
                if (!string.IsNullOrWhiteSpace(category) && ToolTaxonomy.FindCategory(category) == null)
                    return new ResponseStringList { Message = "Unknown category '" + category + "'. Valid keys: " + string.Join(", ", ToolTaxonomy.Categories.Select(c => c.Key)) + ".", Meta = BridgeMeta(false) };
                if (!string.IsNullOrWhiteSpace(domain) && !ToolTaxonomy.IsKnownDomain(domain))
                    return new ResponseStringList { Message = "Unknown domain '" + domain + "'. Call ListToolCategories for the registered domain tags.", Meta = BridgeMeta(false) };
                if (!string.IsNullOrWhiteSpace(category) || !string.IsNullOrWhiteSpace(domain))
                {
                    var filtered = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in all)
                    {
                        var tag = ToolTaxonomy.For(kv.Key);
                        if (!string.IsNullOrWhiteSpace(domain) && !string.Equals(tag.Domain, domain.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                        if (!string.IsNullOrWhiteSpace(category) && !string.Equals(ToolTaxonomy.CategoryOf(tag.Domain), category.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                        filtered[kv.Key] = kv.Value;
                    }
                    all = filtered;
                }

                var terms = (query ?? "")
                    .Split(new[] { ' ', ',', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim().ToLowerInvariant())
                    .Where(t => t.Length > 0)
                    .ToArray();

                var scored = new List<KeyValuePair<int, string>>();
                foreach (var kv in all)
                {
                    string lname = kv.Key.ToLowerInvariant();
                    string desc = (ToolDescription(kv.Value) + " " + ToolMetadata.SearchAliases(kv.Key)).ToLowerInvariant();
                    int score = 0;
                    if (terms.Length == 0) score = 1;
                    foreach (var t in terms)
                    {
                        // Name hits outrank description hits: a model searching "watch table"
                        // wants ExportPlcWatchTable ahead of every tool that merely mentions it.
                        if (lname == t) score += 100;
                        else if (lname.Contains(t)) score += 20;
                        if (desc.Contains(t)) score += 3;
                    }
                    if (score > 0) scored.Add(new KeyValuePair<int, string>(score, kv.Key));
                }

                if (scored.Count == 0)
                {
                    return new ResponseStringList
                    {
                        Message = "No tool matches '" + query + "'. Try fewer or more general words " +
                                  "(e.g. 'watch table' instead of 'ExportPlcWatchTableToCsv'), " +
                                  "or call FindTools with an empty query to list everything.",
                        Meta = BridgeMeta(true),
                    };
                }

                var hits = scored
                    .OrderByDescending(x => x.Key).ThenBy(x => x.Value, StringComparer.Ordinal)
                    .Take(limit).ToList();

                bool lite = _bridgeIsLiteProfile();
                var lines = new List<string>();
                foreach (var h in hits)
                {
                    var m = all[h.Value];
                    bool listed = !lite || _bridgeLiteToolNames.Contains(h.Value);
                    lines.Add(RenderSignature(h.Value, m)
                              + (listed ? "  [already listed - call it directly]" : "  [call via CallTool]"));
                    lines.Add("    " + ToolDescription(m));
                    // A worked call next to the signature is what stops the guess-and-retry loop.
                    lines.Add("    " + ToolExamples.Render(ToolExamples.FindOrDerive(h.Value, SpecsOf(m))));
                }

                return new ResponseStringList
                {
                    Message = hits.Count + " of " + scored.Count + " matching tools (roster: " + all.Count + " total). " +
                              "Tools marked [call via CallTool] are not in this session's tool list - " +
                              "invoke them with CallTool(name, arguments); PreviewToolCall(name, arguments) checks a planned call without executing it.",
                    Items = lines,
                    Meta = BridgeMeta(true),
                };
            }
            catch (Exception ex)
            {
                return new ResponseStringList { Message = "FindTools failed: " + ex.Message, Meta = BridgeMeta(false) };
            }
        }

        [McpServerTool(Name = "CallTool"), Description("[L0][Meta] Invoke a tool in the full version-gated catalog. arguments must be an object matching the target inputSchema. Returns the target result unchanged; dispatch failures use the V4 envelope. Nested orchestration is refused.")]
        public static CallToolResult CallTool(
            [Description("Exact currently registered tool name from FindTools.")] string name,
            [Description("Target arguments as an object. Omit for a tool with no arguments; strings and null are invalid.")] ToolArguments? arguments = null)
            => CallToolCore(name, arguments);

        private static CallToolResult CallToolCore(string name, ToolArguments? arguments, Func<CallToolResult?>? beforeDispatch = null)
        {
            bool write = AllToolMethods(includeUnavailable: true).TryGetValue(name ?? "", out _) && ApprovalWrite(name ?? "", (arguments ?? EmptyArguments()).Json.GetRawText());
            bool disabled = write && McpApprovalContext.Value && !TiaOpenness.Shared.ApprovalSettings.Load(TiaOpenness.Shared.ApprovalSettings.SettingsPath).Enabled;
            string requestId = Meta.Correlate(InvocationJournal.CorrelationId);
            using var audit = TiaOpenness.Shared.AuditInvocation.Begin(write, "engine", ReleaseKey, name ?? "", requestId);
            var error = BindV4Call(name, arguments ?? EmptyArguments(), out var method, out var call);
            if (error != null)
            {
                var rejected = V4TargetReject("CallTool", error, current: CurrentBehaviorTargets("CallTool", JsonSerializer.SerializeToElement(new { name })));
                RecordCallRejection(name, arguments ?? EmptyArguments(), rejected);
                return AuditBridgeResult(audit, rejected, disabled);
            }
            if (string.Equals(name, "CallTool", StringComparison.OrdinalIgnoreCase))
                return AuditBridgeResult(audit, V4Reject("CallTool", InvalidInput("name")), disabled);
            bool issued = false;
            try { return AuditBridgeResult(audit, ApprovedBridgeCall(name, (arguments ?? EmptyArguments()).Json.GetRawText(),
                () => { issued = true; return ToolResult(InvokeToolMethod(method!, call!)); }, beforeDispatch), disabled); }
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
            var classification = AllToolMethods(includeUnavailable: true).TryGetValue(tool, out var method)
                ? ClassificationOf(method) : ToolMetadata.Find(tool);
            string operation = classification?.Operation ?? ToolTaxonomy.OperationOf(tool, null).Operation;
            bool readOnly = ApprovalPreviewDepth.Value > 0 || operation is "READ" or "OFFLINE" || operation == "SESSION" && classification?.BatchRead == true;
            var kind = TiaOpenness.Shared.HostFailurePolicy.Classify(error, issued, readOnly,
                reportedCode: error is global::ModelContextProtocol.McpException mcp && mcp.ErrorCode == global::ModelContextProtocol.McpErrorCode.InvalidParams ? -32602 : (int?)null,
                nativeRead: InvocationJournal.NativeCallIssued);
            var evidence = HostBehavior.FailureEvidence(error.GetType().Name, error.Message);
            var data = new JsonObject { ["evidence"] = JsonSerializer.SerializeToNode(evidence) };
            foreach (var retained in HostBehavior.RetainedRecoveryEvidence(error)) data[retained.Key] = retained.Value?.DeepClone();
            string? parameter = TiaOpenness.Shared.HostFailurePolicy.Parameter(error);
            string? message = HostBehavior.AdmissionDiagnostic(error);
            return V4Result(tool, data, HostBehavior.FailureError(kind, parameter, evidence, message),
                HostBehavior.OutcomeOf(kind), HostBehavior.ExecutionOf(kind), HostBehavior.CompletenessOf(kind), current: issued);
        }

        internal static void ValidateCallerInputFiles(string tool, string arguments)
            => CallerInputFiles.Validate(tool, JsonNode.Parse(arguments)!.AsObject());

        internal static Error? BindV4Call(string name, ToolArguments arguments, out MethodInfo? method, out object?[]? call)
        {
            method = null; call = null;
            if (string.IsNullOrWhiteSpace(name)) return InvalidInput("name");
            if (!AllToolMethods(includeUnavailable: true).TryGetValue(name, out method)
                || name != (method.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? method.Name))
                return new Error("Tool is not registered in this release.", new ToolNotFoundDetails(name));
            var argumentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in arguments.Json.EnumerateObject())
                if (!argumentNames.Add(property.Name)) return InvalidInput("arguments");
            name = method.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? method.Name;
            var resolved = method;
            string version = VersionCallProblem(name, key => arguments.Json.TryGetProperty(key, out var v) ? v.ToString()
                : resolved.GetParameters().FirstOrDefault(p => p.Name == key)?.DefaultValue?.ToString());
            if (version.Length != 0)
                return new Error(version, new UnsupportedCapabilityDetails(ReleaseKey, name, null));
            var schema = ToolInputSchema(name, method);
            var error = ValidateV4Arguments(method, arguments.Json, schema);
            if (error != null) return error;
            var parameters = method.GetParameters();
            call = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (IsInfrastructureParameter(p.ParameterType)) continue;
                if (!arguments.Json.TryGetProperty(p.Name!, out var value))
                {
                    if (!p.HasDefaultValue) return InvalidInput(p.Name!);
                    call[i] = p.DefaultValue; continue;
                }
                try { call[i] = JsonSerializer.Deserialize(value.GetRawText(), p.ParameterType, V4BindingJson); }
                catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is InvalidOperationException || ex is OverflowException)
                { return InvalidInput(p.Name!); }
            }
            return null;
        }

        internal static string ReleaseKey => Siemens.EngineRouter.CompiledTiaMajorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        internal static ToolArguments EmptyArguments() => new ToolArguments(JsonSerializer.SerializeToElement(new JsonObject()));
        internal static Error InvalidInput(string parameter) => new Error("Input does not satisfy the declared contract.", new InvalidArgumentDetails(parameter, Array.Empty<string>()));
        internal static Paging OffsetPage(int offset, int limit, int total) => new Paging(PagingMode.Offset, offset, limit,
            (long)offset + limit < total ? offset + limit : (int?)null, null, null, total, (long)offset + limit >= total);
        internal static CallToolResult V4Reject(string tool, Error error, JsonObject? data = null)
            => V4Result(tool, data, error, Outcome.RejectedBeforeOperation, Execution.NotStarted, Completeness.None);
        internal static CallToolResult V4TargetReject(string tool, Error error, bool current)
            => V4Result(tool, null, error, Outcome.RejectedBeforeOperation, Execution.NotStarted, Completeness.None, current: current);
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
            return targets.Any(target => BehaviorCapabilities.EntryPolicy(typeof(McpServer).Assembly, ReleaseKey,
                AllToolMethods(true).Keys.FirstOrDefault(n => string.Equals(n, target, StringComparison.OrdinalIgnoreCase)) ?? target,
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
                "Native behavior retains the current policy; V4 native acceptance is pending.", new Dictionary<string, JsonElement>()) } : Array.Empty<Warning>();
            var policy = current ? BehaviorPolicy.Current : BehaviorPolicy.NotApplicable;
            if (BehaviorCapabilities.EntryPolicy(typeof(McpServer).Assembly, ReleaseKey, tool, policy) == BehaviorPolicy.SafeV4)
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
        internal static CallToolResult InfrastructureResult(string tool, ResponseMessage response, Paging? paging = null)
        {
            var data = response.Meta == null ? new JsonObject() : (JsonObject)response.Meta.DeepClone();
            data.Remove("success"); data.Remove("timestamp");
            data["summary"] = response.Message;
            if (response is ResponseStringList list) data["items"] = new JsonArray((list.Items ?? Enumerable.Empty<string>()).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
            return response.Meta?["success"]?.GetValue<bool?>() == true ? V4Result(tool, data, paging: paging)
                : V4Reject(tool, InvalidInput("arguments"), data);
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

        // PreviewToolCall uses the same name resolution and parameter matching as CallTool without invoking anything.
        // The report identifies invalid arguments so callers can correct their plan before sending it to TIA.
        // PreflightLogic builds the report (pure, offline-tested); only session state comes from the engine,
        // through a partial method the offline suite does not implement.

        [McpServerTool(Name = "PreviewToolCall"), Description("[L0][Meta][SESSION] Validate a planned call against the target inputSchema and version gates without executing it. Returns signature, prerequisites and a worked example. No native calls.")]
        public static CallToolResult PreviewToolCall(
            [Description("Exact currently registered tool name.")] string name,
            [Description("Planned arguments as an object; omit for a no-argument tool.")] ToolArguments? arguments = null)
        {
            var args = arguments ?? EmptyArguments();
            var error = BindV4Call(name, args, out _, out _);
            if (error != null) return V4Reject("PreviewToolCall", error);
            var report = PreflightToolCall(name, args.Json.GetRawText());
            if (report.Meta?["prerequisites"]?["satisfied"]?.GetValue<bool?>() == false)
                return V4Reject("PreviewToolCall", new Error("No project is bound.", new ProjectNotBoundDetails()));
            report.Meta!["success"] = true;
            return InfrastructureResult("PreviewToolCall", report);
        }

        public static ResponseStringList PreflightToolCall(string name, string argumentsJson)
        {
            string target = (name ?? "").Trim();
            // envelope: legacy-stamp-then-verdict
            var meta = new JsonObject { ["timestamp"] = DateTime.Now };
            if (target.Length == 0)
                return new ResponseStringList { Message = "PreviewToolCall: 'name' is required.", Meta = BridgeMeta(false) };
            if (AllToolMethods(includeUnavailable: true).ContainsKey(target))
            {
                var unavailable = VersionToolProblem(target);
                if (unavailable.Length != 0)
                {
                    meta["success"] = false; meta["ok"] = false; meta["toolFound"] = true; meta["versionAvailable"] = false;
                    return new ResponseStringList { Message = unavailable, Meta = meta };
                }
            }
            var all = AllToolMethods();
            if (!all.TryGetValue(target, out var method))
            {
                var near = all.Keys
                    .Where(k => k.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0 || target.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(k => k, StringComparer.Ordinal).Take(8).ToList();
                if (near.Count == 0)
                    near = all.Keys.Select(k => new KeyValuePair<int, string>(CommonPrefixLength(k, target), k))
                        .Where(x => x.Key >= 6).OrderByDescending(x => x.Key).ThenBy(x => x.Value, StringComparer.Ordinal).Take(5).Select(x => x.Value).ToList();
                meta["success"] = false; meta["ok"] = false; meta["toolFound"] = false;
                meta["suggestions"] = new JsonArray(near.Select(n => (JsonNode)n).ToArray());
                return new ResponseStringList
                {
                    Message = "No tool named '" + target + "'." + (near.Count > 0 ? " Did you mean: " + string.Join(", ", near) + "? Re-run PreviewToolCall with that name." : " Call FindTools with capability words to find it."),
                    Meta = meta,
                };
            }
            // Canonical spelling (the dictionary is case-insensitive).
            string canonical = all.Keys.First(k => string.Equals(k, target, StringComparison.OrdinalIgnoreCase));

            JsonObject args;
            if (string.IsNullOrWhiteSpace(argumentsJson) || argumentsJson.Trim() == "{}") args = new JsonObject();
            else
            {
                JsonNode? parsed;
                try { parsed = JsonNode.Parse(argumentsJson); }
                catch (JsonException jx)
                {
                    meta["success"] = false; meta["ok"] = false; meta["toolFound"] = true;
                    return new ResponseStringList { Message = "arguments is not valid JSON (" + jx.Message + "). Expected signature: " + RenderSignature(canonical, method), Meta = meta };
                }
                if (!(parsed is JsonObject obj))
                {
                    meta["success"] = false; meta["ok"] = false; meta["toolFound"] = true;
                    return new ResponseStringList { Message = "arguments must be a JSON object. Expected signature: " + RenderSignature(canonical, method), Meta = meta };
                }
                args = obj;
            }

            var duplicate = DuplicateArgumentProblem(args);
            if (duplicate.Length != 0)
            {
                meta["success"] = false; meta["ok"] = false; meta["toolFound"] = true;
                return new ResponseStringList { Message = duplicate, Meta = meta };
            }
            var versionProblem = VersionCallProblem(canonical, key =>
            {
                var pair = args.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
                if (pair.Key != null) return pair.Value?.ToString();
                return method.GetParameters().FirstOrDefault(p => p.Name == key)?.DefaultValue?.ToString();
            });
            if (versionProblem.Length != 0)
            {
                meta["success"] = false; meta["ok"] = false; meta["toolFound"] = true;
                meta["versionAvailable"] = false;
                return new ResponseStringList { Message = versionProblem, Meta = meta };
            }
            var specs = SpecsOf(method);
            var report = PreflightLogic.Analyze(specs, args);

            string description = ToolDescription(method);
            var tag = ToolTaxonomy.For(canonical);
            var op = ToolTaxonomy.OperationOf(canonical, description);
            var precautions = PreflightLogic.Precautions(op.Operation, report.DryRunSupported).ToList();
            var usageNotes = TiaOpenness.Shared.ToolUsageCatalog.Notes(canonical);
            bool nativeParameterRead = (bool?)usageNotes["nativeReadsPossible"] == true;
            if (usageNotes["precaution"] is JsonValue note) precautions.Add(note.GetValue<string>());
            if (usageNotes["nativeReadsPossible"] != null) meta["nativeReadsPossible"] = usageNotes["nativeReadsPossible"]!.DeepClone();
            meta["usageTool"] = new JsonObject { ["name"] = "GetToolUsage", ["arguments"] = new JsonObject { ["toolName"] = canonical } };

            bool? connected = null; string? project = null;
            ReadSessionState(ref connected, ref project);
            bool needsProject = PreflightLogic.NeedsProject(op.Operation, canonical);
            bool projectBound = !string.IsNullOrWhiteSpace(project) && project != "-";
            bool? prerequisitesOk = connected == null ? (bool?)null : !needsProject || (connected == true && projectBound);
            var prerequisites = new List<string>();
            if (needsProject)
            {
                if (connected == false) prerequisites.Add("Not connected to TIA Portal - call ConnectPortal (or AttachOpenProject with the project name) first.");
                else if (connected == true && !projectBound) prerequisites.Add("Connected but no project bound - AttachOpenProject / OpenProject first.");
                else if (connected == null) prerequisites.Add("Session state unknown here; GetSessionState tells whether a project is bound.");
                else prerequisites.Add("Project '" + project + "' is bound.");
            }

            var lines = new List<string> { "Signature: " + RenderSignature(canonical, method), "Class: " + tag.Layer + " " + tag.Domain + " " + op.Operation + (op.Inferred ? " (inferred)" : "") };
            foreach (var m in report.Missing) lines.Add("MISSING required parameter: " + m);
            foreach (var u in report.Unknown) lines.Add("UNKNOWN parameter: " + u);
            foreach (var t in report.TypeProblems) lines.Add("TYPE: " + t);
            foreach (var c in report.CaseFixes) lines.Add("Case: " + c + " (CallTool accepts it; a direct call needs the exact spelling)");
            foreach (var c in report.Coercions) lines.Add("Coercion: " + c);
            foreach (var w in report.Warnings) lines.Add("Warning: " + w);
            if (report.DryRunSupported)
                lines.Add("dryRun: " + (report.DryRunGiven == null ? "not given -> default " + (report.DryRunDefault ? "true (preview only)" : "false (executes)") : report.DryRunGiven == true ? "true (preview only)" : "false (EXECUTES" + (report.ConfirmFlags.Count > 0 ? "; confirm flags set: " + (report.ConfirmFlagsSet.Count > 0 ? string.Join(", ", report.ConfirmFlagsSet) : "none") : "") + ")"));
            foreach (var p in precautions) lines.Add("Precaution: " + p);
            foreach (var p in prerequisites) lines.Add("Prerequisite: " + p);
            var example = ToolExamples.FindOrDerive(canonical, specs);
            lines.Add(ToolExamples.Render(example));
            lines.Add("Description: " + description);

            bool ready = report.Ok && prerequisitesOk != false;
            // envelope: legacy-independent-verdicts
            meta["success"] = ready; meta["ok"] = report.Ok; meta["toolFound"] = true; meta["tool"] = canonical;
            meta["signature"] = RenderSignature(canonical, method);
            meta["layer"] = tag.Layer; meta["domain"] = tag.Domain; meta["operation"] = op.Operation;
            meta["missing"] = new JsonArray(report.Missing.Select(x => (JsonNode)x).ToArray());
            meta["unknown"] = new JsonArray(report.Unknown.Select(x => (JsonNode)x).ToArray());
            meta["caseFixes"] = new JsonArray(report.CaseFixes.Select(x => (JsonNode)x).ToArray());
            meta["typeProblems"] = new JsonArray(report.TypeProblems.Select(x => (JsonNode)x).ToArray());
            meta["coercions"] = new JsonArray(report.Coercions.Select(x => (JsonNode)x).ToArray());
            meta["warnings"] = new JsonArray(report.Warnings.Select(x => (JsonNode)x).ToArray());
            meta["dryRun"] = new JsonObject { ["supported"] = report.DryRunSupported, ["given"] = report.DryRunGiven, ["effective"] = report.Effective, ["confirmFlags"] = new JsonArray(report.ConfirmFlags.Select(x => (JsonNode)x).ToArray()), ["confirmFlagsSet"] = new JsonArray(report.ConfirmFlagsSet.Select(x => (JsonNode)x).ToArray()) };
            meta["precautions"] = new JsonArray(precautions.Select(x => (JsonNode)x).ToArray());
            meta["prerequisites"] = new JsonObject { ["needsProject"] = needsProject, ["connected"] = connected, ["project"] = project, ["satisfied"] = prerequisitesOk };
            meta["example"] = JsonNode.Parse(example.ArgumentsJson);
            meta["exampleDerived"] = example.Note == ToolExamples.DerivedNote;
            string verdict = !report.Ok
                ? "NOT READY: fix " + (report.Missing.Count + report.Unknown.Count + report.TypeProblems.Count) + " problem(s) listed in Items, then preflight again."
                : prerequisitesOk == false ? "Arguments fit; the session prerequisite is not met (see Items)."
                : "READY: " + canonical + " would bind" + (report.CaseFixes.Count + report.Coercions.Count > 0 ? " after " + (report.CaseFixes.Count + report.Coercions.Count) + " automatic correction(s) (CallTool only)" : "") + (nativeParameterRead ? "; native parameter reads may execute even with dryRun=true" : (report.DryRunSupported ? (report.Effective ? "; it EXECUTES (dryRun=false)" : "; it is a preview (dryRun)") : "")) + ".";
            return new ResponseStringList { Message = verdict, Items = lines, Meta = meta };
        }

        /// <summary>The parameter specs of a tool method (infrastructure parameters skipped), the vocabulary PreflightLogic / SchemaHintsLogic / ToolExamples share.</summary>
        internal static List<PreflightLogic.ParameterSpec> SpecsOf(MethodInfo method)
        {
            var specs = new List<PreflightLogic.ParameterSpec>();
            foreach (var p in method.GetParameters())
            {
                if (IsInfrastructureParameter(p.ParameterType)) continue;
                string? def = null;
                if (p.HasDefaultValue)
                    def = p.DefaultValue == null ? "null" : p.DefaultValue is bool b ? (b ? "true" : "false") : p.DefaultValue is string s ? "\"" + s + "\"" : Convert.ToString(p.DefaultValue, System.Globalization.CultureInfo.InvariantCulture);
                var d = p.GetCustomAttribute<DescriptionAttribute>();
                // A parameter without its own [Description] gets the roster-wide vocabulary text (flagged Synthesized).
                string? text = d?.Description;
                bool synthesized = false;
                if (string.IsNullOrEmpty(text)) { text = ParameterVocabulary.Describe(p.Name!); synthesized = text != null; }
                string tool = method.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? method.Name;
                specs.Add(new PreflightLogic.ParameterSpec(p.Name!, FriendlyTypeName(p.ParameterType), !p.HasDefaultValue, def, text ?? "", synthesized,
                    ToolMetadata.Parameter(tool, p.Name!)?.AllowedValues));
            }
            return specs;
        }

        private static ToolMetadata.Classification? ClassificationOf(MethodInfo method)
            => method.GetCustomAttribute<ToolClassificationAttribute>()?.Value
               ?? ToolMetadata.Find(method.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? method.Name);

        /// <summary>Write and online-write tools by their typed classification; descriptions never decide.</summary>
        internal static bool IsWriteTool(MethodInfo method) => IsWrite(ClassificationOf(method));
        internal static bool IsWriteTool(string name) => ToolCatalog.IsWrite(name);
        private static bool IsWrite(ToolMetadata.Classification? classification)
            => classification?.Operation is "WRITE" or "ONLINE-WRITE";

        /// <summary>Build gate: every recipe step must name a real tool and fit its signature.</summary>
        public static IReadOnlyList<string> ValidateToolRecipes()
        {
            var all = AllToolMethods(includeUnavailable: true);
            return ToolRecipes.ValidateAgainst(tool =>
            {
                if (!all.Keys.Any(k => string.Equals(k, tool, StringComparison.Ordinal))) return null;
                return all[tool].GetParameters().Where(p => !IsInfrastructureParameter(p.ParameterType))
                    .Select(p => new KeyValuePair<string, bool>(p.Name!, !p.HasDefaultValue)).ToList();
            });
        }

        // Implemented in McpServer.Maintenance.cs (engine build); absent in the offline suite, where no portal exists.
        static partial void ReadSessionState(ref bool? connected, ref string? project);

        /// <summary>Build gate (HttpTests generate-tools-list mode): every example in ToolExamples must fit a real tool, spelled exactly.</summary>
        public static IReadOnlyList<string> ValidateToolExamples()
        {
            var all = AllToolMethods(includeUnavailable: true);
            var problems = ToolExamples.ValidateAgainst(tool =>
            {
                if (!all.Keys.Any(k => string.Equals(k, tool, StringComparison.Ordinal))) return null;
                return all[tool].GetParameters().Where(p => !IsInfrastructureParameter(p.ParameterType))
                    .Select(p => new KeyValuePair<string, bool>(p.Name!, !p.HasDefaultValue)).ToList();
            }).ToList();
            // Validate the maintained V4 samples and the exact examples served to
            // clients. This gate binds schemas only; it never invokes their targets.
            foreach (var row in TiaOpenness.Shared.ToolUsageCatalog.ProfileEntries(ReleaseKey))
            {
                string name = (string)row!["currentName"]!;
                AssertV4Tool(name);
                var usage = ResultBody(new ToolUsageTools().GetToolUsage(toolName: name));
                if (usage?["ok"]?.GetValue<bool?>() != true) { problems.Add(name + ": usage retrieval failed"); continue; }
                foreach (var args in new[] { BehaviorCapabilities.CandidateExample(ReleaseKey, name) ?? row["arguments"]!, usage["data"]!["example"]!["request"]!["params"]!["arguments"]! })
                {
                    var error = ValidateInfrastructureExample(name, args.AsObject());
                    if (error != null) problems.Add(name + ": " + V4Json.Serialize(error));
                }
            }
            return problems;
        }

        internal static Error? ValidateInfrastructureExample(string name, JsonObject arguments)
        {
            var error = BindV4Call(name, new ToolArguments(JsonSerializer.SerializeToElement(arguments)), out _, out _);
            if (error != null) return error;
            if (name == "CallTool" || name == "PreviewToolCall")
                return BindV4Call((string)arguments["name"]!, arguments["arguments"] == null ? EmptyArguments()
                    : new ToolArguments(JsonSerializer.SerializeToElement(arguments["arguments"])), out _, out _);
            if (name == "RunReadOnlyToolBatch" || name == "PreviewToolBatch")
                return ValidateBatch(JsonSerializer.Deserialize<ToolCall[]>(arguments["operations"]!.ToJsonString(), V4BindingJson)!, name == "PreviewToolBatch", out _);
            return null;
        }

        private static object? InvokeToolMethod(MethodInfo method, object?[] call)
        {
            string id = Guid.NewGuid().ToString("N");
            RecordBridgeEvent(id, method.Name, "BEFORE");
            IDisposable? observation = null;
            StartCallProjection(id, method, call, ref observation);
            bool issued = false;
            string toolName = method.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? method.Name;
            try
            {
                var parameters = method.GetParameters();
                var problem = VersionCallProblem(toolName, key =>
                {
                    int index = Array.FindIndex(parameters, p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
                    return index < 0 ? null : call[index]?.ToString();
                });
                if (problem.Length != 0) throw new NotSupportedException(problem);
                ValidateRuntimeBinding(method);
                object? target = method.IsStatic ? null : EngineServices.Get(method.DeclaringType!);
                issued = true;
                object? result = method.Invoke(target, call);
                if (result is Task task)
                {
                    task.GetAwaiter().GetResult();
                    var resultProperty = task.GetType().GetProperty("Result");
                    result = resultProperty != null && resultProperty.PropertyType.Name != "VoidTaskResult" ? resultProperty.GetValue(task) : null;
                }
                RecordBridgeEvent(id, method.Name, "RETURNED");
                EndCallProjection(observation, result);
                return result;
            }
            catch (Exception ex)
            {
                RecordBridgeEvent(id, method.Name, "THREW");
                var result = TargetFailure(toolName, ex, issued);
                EndCallProjection(observation, result);
                return result;
            }
            finally { observation?.Dispose(); }
        }

        static partial void RecordBridgeEvent(string id, string name, string phase);
        static partial void StartCallProjection(string id, MethodInfo method, object?[] arguments, ref IDisposable? observation);
        static partial void EndCallProjection(IDisposable? observation, object? result);
        static partial void RecordCallRejection(string name, ToolArguments arguments, CallToolResult result);
        static partial void ValidateRuntimeBinding(MethodInfo method);

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
