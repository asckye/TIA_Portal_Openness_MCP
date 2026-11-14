#if TIA_FOUNDATION_TEST_HOST
extern alias enginehost;
using AuditCallTool = enginehost::TiaMcpServer.ModelContextProtocol.AuditCallTool;
using VersionPolicyTool = enginehost::TiaMcpServer.ModelContextProtocol.VersionPolicyTool;
using BatchPlanStore = TiaMcpServer.ModelContextProtocol.BatchPlanStore;
using ReleaseCatalogData = enginehost::TiaMcp.FoundationHost.ReleaseCatalogData;
#else
using AuditCallTool = TiaMcpServer.ModelContextProtocol.AuditCallTool;
using VersionPolicyTool = TiaMcpServer.ModelContextProtocol.VersionPolicyTool;
using BatchPlanStore = TiaMcpServer.ModelContextProtocol.BatchPlanStore;
#endif
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaOpenness.Shared;

namespace TiaMcp.FoundationHost;

internal static class CanonicalBatchTools
{
    internal static readonly AsyncLocal<string?> CurrentExportRoot = new();
    internal static readonly string[] Names = {
        "RepairAndReimportPlcBlock", "ImportPlcTagTablesFromDirectory", "ExportPlcWatchTablesToDirectory", "ExportTechnologyObjectsToDirectory" };
    internal static void Apply(List<McpServerTool> tools, string release, Func<JsonObject?> identity)
    {
        var roster = tools.ToDictionary(t => t.ProtocolTool.Name, StringComparer.Ordinal);
        for (int i = 0; i < tools.Count; i++)
            if (Names.Contains(tools[i].ProtocolTool.Name, StringComparer.Ordinal))
                tools[i] = new AuditCallTool(new VersionPolicyTool(new BatchTool(tools[i].ProtocolTool, roster, release, identity)));
    }

    internal sealed class BatchTool : McpServerTool
    {
        private readonly Tool tool;
        private readonly InputSchema schema;
        private readonly IReadOnlyDictionary<string, McpServerTool> roster;
        private readonly string release;
        private readonly Func<JsonObject?> identity;
        internal Func<PendingApproval, ApprovalSettings, CancellationToken, Task<ApprovalOutcome>> Approve = (pending, settings, token) => ApprovalClient
            .Wait(pending, settings, token);
        internal Func<ApprovalOutcome, string, Task> CompleteApproval = (approved, outcome) => ApprovalClient.Complete(approved, outcome);

        internal BatchTool(Tool declaration, IReadOnlyDictionary<string, McpServerTool> roster, string release, Func<JsonObject?> identity)
        {
            this.roster = roster; this.release = release; this.identity = identity;
            var input = JsonNode.Parse(declaration.InputSchema.GetRawText())!.AsObject();
            var properties = input["properties"]!.AsObject();
            foreach (var field in new[] { "dryRun", "confirm", "expectedPlanHash", "expectedProjectFile" })
                properties[field] = new JsonObject { ["type"] = field is "dryRun" or "confirm" ? "boolean" : "string",
                    ["default"] = field == "dryRun" ? JsonValue.Create(true) : field == "confirm" ? JsonValue.Create(false) : JsonValue.Create(""),
                    ["description"] = CompatibilityTools.PreviewParameterDescriptions[field] };
            tool = new Tool { Name = declaration.Name, Title = declaration.Title,
                Description = declaration.Description
                    + " Ordered batch: preview lists the selected items and their export/import plan under one batch plan hash. One batch approval " +
                    "executes those exact items; stop at the first failure and report completed/not-started items. Default dryRun=true. No best effort, " +
                    "retry or rollback. Native behaviorPolicy=current; V4 native acceptance is pending.",
                InputSchema = JsonSerializer.SerializeToElement(input), OutputSchema = FoundationV4Result.Schema, Annotations = declaration.Annotations };
            schema = new InputSchema(tool.InputSchema);
        }
        public override Tool ProtocolTool => tool;

        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken =
                default)
        {
            var args = JsonSerializer.SerializeToNode(request.Params?.Arguments ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal))!.AsObject();
            string id = Guid.NewGuid().ToString("N");
            var invalid = schema.Validate(JsonSerializer.SerializeToElement(args), "arguments");
            if (invalid != null) return Reject(invalid);
            bool dry = (bool?)args["dryRun"] ?? true;
            try
            {
                var binding = identity();
                if (binding == null || string.IsNullOrWhiteSpace((string?)binding["projectPath"]))
                    return Reject(new Error("Connect to a TIA Portal project first.", new PreconditionFailedDetails("connect-portal", null)));
                string project = (string?)binding["projectPath"] ?? throw new InvalidOperationException("No exact project file is bound.");
                if (!dry && ((bool?)args["confirm"] != true || (string?)args["expectedProjectFile"] != project))
                    return Reject(new Error("Batch execution requires confirm=true and the preview's exact expectedProjectFile.",
                                new InvalidArgumentDetails("expectedProjectFile", Array.Empty<string>())));
                var plan = await Preview(args, binding, request, cancellationToken);
                if (dry || !(bool)plan["canApply"]!) return Result(plan, false);
                string hash = (string)plan["planHash"]!;
                if ((string?)args["expectedPlanHash"] != hash)
                    return Reject(new Error("This batch plan no longer matches the preview: its items, their input files or the TIA binding differ. "
                                + "A reconnect or a host restart also changes the binding. Nothing ran; preview the whole batch again.",
                                new PlanStaleDetails(null, "batch-plan", (string?)args["expectedPlanHash"])));
                var settings = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
                var approvalArguments = args.DeepClone().AsObject(); approvalArguments["batchPlan"] = plan.DeepClone();
                var pending = PendingApproval.Create(ReleaseCatalogData.Releases[release].FoundationInvocationTag, release, tool.Name,
                    approvalArguments.ToJsonString(), BatchPlanStore.Stable(binding), settings.TimeoutSeconds, id);
                using var audit = AuditInvocation.Begin(true, ReleaseCatalogData.Releases[release].FoundationInvocationTag, release, tool.Name, id);
                audit?.RecordRequest(pending.PlanHash);
                var approved = await Approve(pending, settings, cancellationToken);
                if (approved.Reason != null)
                    return Reject(new Error("Batch approval was not granted.", new ConfirmationRequiredDetails(approved.Reason, pending.PlanHash, id)));
                CallToolResult? final = null;
                try
                {
                    var current = identity() ?? throw new InvalidOperationException("Project binding ended.");
                    var checkedPlan = await Preview(args, current, request, cancellationToken);
                    if ((string?)checkedPlan["planHash"] != hash || (bool?)checkedPlan["canApply"] != true)
                        return final =
                            Reject(new Error("Batch identity or item previews changed while awaiting approval.",
                                    new PlanStaleDetails(null, "batch-plan", hash)));
                    var rows = (JsonArray)plan["items"]!;
                    bool stopped = false;
                    foreach (var node in rows)
                    {
                        var item = node!.AsObject();
                        if (stopped) { item["status"] = "not-started"; continue; }
                        if (cancellationToken.IsCancellationRequested) { item["status"] = "not-started"; stopped = true; continue; }
                        var values = item["arguments"]!.DeepClone().AsObject(); values["dryRun"] = false;
                        var target = roster[(string)item["tool"]!];
                        var targetProperties = target.ProtocolTool.InputSchema.GetProperty("properties");
                        if (targetProperties.TryGetProperty("confirm", out _)) values["confirm"] = true;
                        if (targetProperties.TryGetProperty("expectedProjectFile", out _)) values["expectedProjectFile"] = project;
                        if (targetProperties.TryGetProperty("expectedPlanHash", out _)) values["expectedPlanHash"] = item["canonicalPlanHash"]!.DeepClone();
                        try
                        {
                            if (item["sourceSha256"] is JsonValue sourceHash && sourceHash.GetValue<string>() != Convert
                                    .ToHexStringLower(SHA256.HashData(File.ReadAllBytes((string)values["importPath"]!))))
                            {
                                item["result"] = Body(FoundationV4Result.Reject(release, (string)item["tool"]!, id,
                                    new Error("The approved import file changed before this item started.",
                                            new PlanStaleDetails(null, "batch-item", (string?)item["canonicalPlanHash"]))));
                                item["status"] = "failed"; stopped = true; continue;
                            }
                            using var itemApproval = GenerationStepApproval
                                .EnterBatchItem(approved, item["index"]!.GetValue<int>(), (string)item["tool"]!, values);
                            var child = await Invoke(target, values, request, cancellationToken, ExportRoot(args));
                            item["result"] = Body(child); stopped = child.IsError == true || (bool?)item["result"]?["ok"] != true;
                            item["status"] = stopped ? "failed" : "completed";
                        }
                        catch (Exception error)
                        {
                            item["status"] = "failed"; item["result"] =
                                Body(FoundationV4Result.Failure(release, (string)item["tool"]!, id, true, true, null, error)); stopped = true;
                        }
                    }
                    plan["executed"] = true;
                    final = Result(plan, true);
                    audit?.Complete(final.StructuredContent?.ToJsonString());
                    return final;
                }
                finally { await CompleteApproval(approved, (string?)final?.StructuredContent?["meta"]?["outcome"] ?? "unknown"); }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { return FoundationV4Result.Failure(release, tool.Name, id, false, false, null, error); }

            CallToolResult Reject(Error error) => FoundationV4Result.Reject(release, tool.Name, id, error, true);
        }

        private async Task<JsonObject> Preview(JsonObject args, JsonObject binding, RequestContext<CallToolRequestParams> request, CancellationToken token)
        {
            string Text(string key) => (string?)args[key] ?? "";
            var calls = new List<(string Tool, JsonObject Args)>();
            string? exportRoot = ExportRoot(args);
            JsonObject Common() => new() { ["softwarePath"] = Text("softwarePath"), ["dryRun"] = true };
            void Import(string target, string file, string groupKey, string group)
            {
                var values = Common(); values["importPath"] = Path.GetFullPath(file); values[groupKey] = group; values["overwrite"] = (bool?)args["overwrite"]
                    ?? false;
                if (target == "ImportPlcTagTable")
                    foreach (var field in new[] { "unitName", "swImportOptions", "importOptions" })
                        if (args[field] is { } value) values[field] = value.DeepClone();
                calls.Add((target, values));
            }
            if (tool.Name == "RepairAndReimportPlcBlock")
            {
                Import("ImportPlcBlock", Text("importPath"), "groupPath", Text("groupPath"));
                if ((bool?)args["compileAfter"] ?? true) calls.Add(("CompilePlcDiagnostics", Common()));
            }
            else if (tool.Name == "ImportPlcTagTablesFromDirectory")
            {
                var regex = Text("regexName");
                foreach (string file in Directory.EnumerateFiles(Path.GetFullPath(Text("dir")), "*.xml").Order(StringComparer.Ordinal))
                    if (regex.Length == 0 || Regex.IsMatch(Path.GetFileNameWithoutExtension(file), regex, RegexOptions.IgnoreCase))
                        Import("ImportPlcTagTable", file, "folderPath", Text("folderPath"));
            }
            else if (tool.Name == "ExportTechnologyObjectsToDirectory")
            {
                calls.Add((tool.Name, new JsonObject { ["softwarePath"] = Text("softwarePath"), ["exportDir"] = Text("exportDir"),
                    ["regexName"] = Text("regexName"), ["dryRun"] = true }));
            }
            else
            {
                const string reader = "ListPlcWatchTables";
                var listed = await Invoke(roster[reader], new JsonObject { ["softwarePath"] = Text("softwarePath") }, request, token);
                var body = Body(listed);
                if ((bool?)body["ok"] != true) throw new InvalidOperationException("Canonical inventory failed: " + body.ToJsonString());
                var inventory = body["data"]?["items"] as JsonArray ?? throw new InvalidDataException("Canonical inventory has no items.");
                foreach (var entry in inventory)
                {
                    string path = entry!.GetValue<string>();
                    if (Text("regexName").Length != 0 && !Regex.IsMatch(Uri.UnescapeDataString(path.Split('/').Last()), Text("regexName"),
                        RegexOptions.IgnoreCase)) continue;
                    var values = Common(); values["watchTableName"] = path;
                    values["exportPath"] = ExportPath(Text("dir"), path);
                    calls.Add(("ExportPlcWatchTable", values));
                }
            }
            var rows = new JsonArray(); bool ready = true;
            foreach (var call in calls)
            {
                token.ThrowIfCancellationRequested();
                var child = roster.TryGetValue(call.Tool, out var target)
                    ? await Invoke(target, call.Args, request, token, exportRoot)
                    : FoundationV4Result
                        .Reject(release, tool.Name, Guid.NewGuid().ToString("N"),
                            new Error("The canonical item tool is unavailable on this release.",
                                new UnsupportedCapabilityDetails(release, call.Tool, "batch-item")));
                var body = Body(child); bool ok = child.IsError != true && (bool?)body["ok"] == true && HostBehavior.PreviewApplyRefusal(body) == null;
                ready &= ok;
                var data = body["data"];
                string itemHash = (string?)data?["planHash"] ?? (string?)data?["evidence"]?["planHash"] ?? Hash(data);
                string? file = (string?)call.Args["importPath"];
                rows.Add(new JsonObject { ["index"] = rows.Count, ["tool"] = call.Tool, ["arguments"] = call.Args,
                    ["sourceSha256"] = file == null || !File.Exists(file) ? null : Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))),
                    ["preview"] = body, ["canonicalPlanHash"] = itemHash, ["status"] = ok ? "planned" : "preview-failed" });
            }
            var hashRows = new JsonArray(rows.OfType<JsonObject>().Select(row => (JsonNode)new JsonObject {
                ["tool"] = row["tool"]!.DeepClone(), ["arguments"] = row["arguments"]!.DeepClone(), ["sourceSha256"] = row["sourceSha256"]?.DeepClone(),
                ["previewData"] = row["preview"]?["data"]?.DeepClone(), ["canonicalPlanHash"] = row["canonicalPlanHash"]!.DeepClone(), ["status"] =
                    row["status"]!.DeepClone() }).ToArray());
            var selector = args.DeepClone().AsObject();
            foreach (var field in new[] { "dryRun", "confirm", "expectedPlanHash", "expectedProjectFile" }) selector.Remove(field);
            foreach (var property in tool.InputSchema.GetProperty("properties").EnumerateObject())
                if (property.Name is not ("dryRun" or "confirm" or "expectedPlanHash" or "expectedProjectFile") && !selector.ContainsKey(property.Name)
                        && property.Value.TryGetProperty("default", out var fallback))
                    selector[property.Name] = JsonNode.Parse(fallback.GetRawText());
            return new JsonObject {
                ["planHash"] = Hash(new JsonObject { ["tool"] = tool.Name, ["arguments"] = selector, ["binding"] = binding.DeepClone(), ["items"] = hashRows }),
                ["expectedProjectFile"] = binding["projectPath"]!.DeepClone(), ["canApply"] = ready, ["executed"] = false, ["items"] = rows };
        }

        private CallToolResult Result(JsonObject plan, bool executed)
        {
            string requestId = NativeFailureDiagnostics.RequestId ?? Guid.NewGuid().ToString("N");
            var rows = plan["items"]!.AsArray(); int completed = rows.Count(r => (string?)r!["status"] == "completed");
            foreach (var row in rows)
                foreach (string member in new[] { "preview", "result" })
                    if (row?[member] is JsonObject child && (string?)child["error"]?["code"] == "NATIVE_OPERATION_FAILED")
                    {
                        var error = V4Json.Deserialize<Error>(child["error"]!.ToJsonString());
                        child["error"] = JsonNode.Parse(V4Json.Serialize(NativeFailureDiagnostics.Complete(error,
                            JsonSerializer.SerializeToElement(child["data"]), (string?)child["meta"]?["requestId"] ?? requestId)));
                    }
            int failed = rows.Count(r => (string?)r!["status"] is "failed" or "preview-failed");
            int pending = rows.Count(r => (string?)r!["status"] == "not-started");
            var outcome = failed + pending == 0 ? Outcome.Succeeded : completed > 0 ? Outcome.Partial : executed ? Outcome.Failed : Outcome
                .RejectedBeforeOperation;
            bool reset = rows.Any(row => (bool?)row?["result"]?["meta"]?["requiresSessionReset"] == true);
            if (rows.Any(row => (string?)row?["result"]?["meta"]?["outcome"] == "unknown")) outcome = Outcome.Unknown;
            return FoundationV4Result.Composite(release, tool.Name, requestId, plan, outcome,
                executed ? outcome == Outcome.Unknown ? Execution.Unknown : outcome == Outcome.Partial ? Execution.Partial : Execution.Completed
                    : outcome == Outcome.RejectedBeforeOperation ? Execution.NotStarted : Execution.ReadOnly,
                outcome == Outcome.Unknown
                    ? new Error("A canonical batch item has an unknown outcome.",
                        new OutcomeUnknownDetails("batch-item", new Dictionary<string, JsonElement>(StringComparer.Ordinal)))
                    : failed + pending == 0 ? null : outcome == Outcome.Partial
                        ? new Error("Batch stopped; inspect each canonical item result.", new PartialFailureDetails(completed, failed, pending))
                        : outcome == Outcome.RejectedBeforeOperation
                            ? new Error("A canonical preview blocks batch execution.", new PreconditionFailedDetails("batch-preview", null))
                            : new Error("The batch stopped before any item completed.",
                                    new NativeOperationFailedDetails(null, null, new Dictionary<string, JsonElement>(StringComparer.Ordinal))),
                                reset);
        }
        private string? ExportRoot(JsonObject args) => tool.Name switch {
            "ExportPlcWatchTablesToDirectory" => Path.GetFullPath((string?)args["dir"] ?? ""),
            _ => null
        };
        private static string ExportPath(string directory, string path)
        {
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var parts = path.Split('/').Select(Uri.UnescapeDataString).ToArray();
            if (parts.Any(part => part is "" or "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || part.Contains('/') || part.Contains('\\')))
                throw new ArgumentException("Export group and object names must be ordinary path components.");
            string output = Path.GetFullPath(Path.Combine(root, Path.Combine(parts) + ".xml"));
            if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Export escapes its directory.");
            return output;
        }
        private static string Hash(JsonNode? value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(BatchPlanStore.Stable(value))));
        private static JsonObject Body(CallToolResult result) => (result.StructuredContent?.DeepClone()
            ?? JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text))!.AsObject();
        private static async ValueTask<CallToolResult> Invoke(McpServerTool target, JsonObject args, RequestContext<CallToolRequestParams> request,
                CancellationToken token, string? exportRoot = null)
        {
            var previousRoot = CurrentExportRoot.Value; CurrentExportRoot.Value = exportRoot;
            var saved = request.Params;
            request.Params = new CallToolRequestParams {
                Name = target.ProtocolTool.Name, Arguments = args.Deserialize<Dictionary<string, JsonElement>>(), ProgressToken = saved?.ProgressToken };
            try { return await target.InvokeAsync(request, token); }
            finally { request.Params = saved; CurrentExportRoot.Value = previousRoot; }
        }
    }
}
