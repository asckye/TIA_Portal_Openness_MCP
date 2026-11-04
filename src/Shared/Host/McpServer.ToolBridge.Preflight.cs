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

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {

        // PreviewToolCall uses the same name resolution and parameter matching as CallTool without invoking anything.
        // The report identifies invalid arguments so callers can correct their plan before sending it to TIA.
        // PreflightLogic builds the report (pure, offline-tested); only session state comes from the engine,
        // through a partial method the offline suite does not implement.

        [McpServerTool(Name = "PreviewToolCall"),
                Description("[L0][Meta][SESSION] Validate a planned call against the target inputSchema and version gates without executing it. Returns " +
                "signature, prerequisites and a worked example. No native calls.")]
        [AsyncDispatch(nameof(PreviewToolCallAsync))]
        public static CallToolResult PreviewToolCall(
            [Description("Exact currently registered tool name.")] string name,
            [Description("Planned arguments as an object; omit for a no-argument tool.")] ToolArguments? arguments = null)
            => PreviewToolCallAsync(name, arguments).GetAwaiter().GetResult();

        internal static async Task<CallToolResult> PreviewToolCallAsync(string name, ToolArguments? arguments = null)
        {
            var args = arguments ?? EmptyArguments();
            var error = ToolInvoker.Bind(name, args, out _);
            if (error != null) error = TargetInputError(name, args, error);
            if (error != null && (error.Code != ErrorCode.InvalidArgument || CatalogView.Find(name) == null)) return V4Reject("PreviewToolCall", error);
            var report = await BridgeWait.On(PreflightToolCallAsync(name, args.Json.GetRawText())).ConfigureAwait(false);
            if (error != null)
            {
                report.Meta!["validationError"] = JsonSerializer.SerializeToNode(error, V4BindingJson);
                var fields = new InputSchema(CatalogView.Find(name)!.Tool.InputSchema).ValidateFields(args.Json);
                report.Meta["validationErrors"] = JsonSerializer.SerializeToNode(fields.Count == 0 ? new[] { error } : fields, V4BindingJson);
                report.Meta["ok"] = false;
                report.Message = "NOT READY: " + error.Message;
                return InfrastructureResult("PreviewToolCall", report, reportOnly: true);
            }
            if (report.Meta?["prerequisites"]?["satisfied"]?.GetValue<bool?>() == false)
                return V4Reject("PreviewToolCall", new Error("No project is bound.", new ProjectNotBoundDetails()));
            report.Meta!["success"] = true;
            return InfrastructureResult("PreviewToolCall", report);
        }

        public static ResponseStringList PreflightToolCall(string name, string argumentsJson)
            => PreflightToolCallAsync(name, argumentsJson).GetAwaiter().GetResult();

        internal static async Task<ResponseStringList> PreflightToolCallAsync(string name, string argumentsJson)
        {
            string target = (name ?? "").Trim();
            // envelope: legacy-stamp-then-verdict
            var meta = new JsonObject { ["timestamp"] = DateTime.Now };
            if (target.Length == 0)
                return new ResponseStringList { Message = "PreviewToolCall: 'name' is required.", Meta = BridgeMeta(false) };
            if (AllToolDescriptors(includeUnavailable: true).ContainsKey(target))
            {
                var unavailable = VersionToolProblem(target);
                if (unavailable.Length != 0)
                {
                    meta["success"] = false; meta["ok"] = false; meta["toolFound"] = true; meta["versionAvailable"] = false;
                    return new ResponseStringList { Message = unavailable, Meta = meta };
                }
            }
            var all = AllToolDescriptors();
            if (!all.TryGetValue(target, out var method))
            {
                var near = all.Keys
                    .Where(k => k.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0 || target.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(k => k, StringComparer.Ordinal).Take(8).ToList();
                if (near.Count == 0)
                    near = all.Keys.Select(k => new KeyValuePair<int, string>(CommonPrefixLength(k, target), k))
                        .Where(x => x.Key >= 6).OrderByDescending(x => x.Key).ThenBy(x => x.Value, StringComparer.Ordinal).Take(5).Select(x => x.Value)
                            .ToList();
                meta["success"] = false; meta["ok"] = false; meta["toolFound"] = false;
                meta["suggestions"] = new JsonArray(near.Select(n => (JsonNode)n).ToArray());
                return new ResponseStringList
                {
                    Message = "No tool named '" + target + "'."
                        + (near.Count > 0 ? " Did you mean: " + string.Join(", ", near) + "? Re-run PreviewToolCall with that name."
                            : " Call FindTools with capability words to find it."),
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
                    return new ResponseStringList {
                        Message = "arguments is not valid JSON (" + jx.Message + "). Expected signature: " + method.Signature, Meta = meta };
                }
                if (!(parsed is JsonObject obj))
                {
                    meta["success"] = false; meta["ok"] = false; meta["toolFound"] = true;
                    return new ResponseStringList { Message = "arguments must be a JSON object. Expected signature: " + method.Signature, Meta = meta };
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
                return method.Parameters.FirstOrDefault(p => p.Name == key)?.DefaultText;
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

            var (connected, project) = await BridgeWait.On(BridgeSessionStateAsync()).ConfigureAwait(false);
            bool needsProject = PreflightLogic.NeedsProject(op.Operation, canonical);
            bool projectBound = !string.IsNullOrWhiteSpace(project) && project != "-";
            bool? prerequisitesOk = connected == null ? (bool?)null : !needsProject || (connected == true && projectBound);
            var prerequisites = new List<string>();
            if (needsProject)
            {
                if (connected == false) prerequisites
                    .Add("Not connected to TIA Portal - call ConnectPortal (or AttachOpenProject with the project name) first.");
                else if (connected == true && !projectBound) prerequisites.Add("Connected but no project bound - AttachOpenProject / OpenProject first.");
                else if (connected == null) prerequisites.Add("Session state unknown here; GetSessionState tells whether a project is bound.");
                else prerequisites.Add("Project '" + project + "' is bound.");
            }

            var lines = new List<string> {
                "Signature: " + method.Signature, "Class: " + tag.Layer + " " + tag.Domain + " " + op.Operation + (op.Inferred ? " (inferred)" : "") };
            foreach (var m in report.Missing) lines.Add("MISSING required parameter: " + m);
            foreach (var u in report.Unknown) lines.Add("UNKNOWN parameter: " + u);
            foreach (var t in report.TypeProblems) lines.Add("TYPE: " + t);
            foreach (var c in report.CaseFixes) lines.Add("Case: " + c + " (CallTool accepts it; a direct call needs the exact spelling)");
            foreach (var c in report.Coercions) lines.Add("Coercion: " + c);
            foreach (var w in report.Warnings) lines.Add("Warning: " + w);
            if (report.DryRunSupported)
                lines
                    .Add("dryRun: "
                        + (report.DryRunGiven == null ? "not given -> default " + (report.DryRunDefault ? "true (preview only)" : "false (executes)") : report
                            .DryRunGiven == true ? "true (preview only)" : "false (EXECUTES"
                            + (report.ConfirmFlags.Count > 0 ? "; confirm flags set: "
                                + (report.ConfirmFlagsSet.Count > 0 ? string.Join(", ", report.ConfirmFlagsSet) : "none") : "")
                            + ")"));
            foreach (var p in precautions) lines.Add("Precaution: " + p);
            foreach (var p in prerequisites) lines.Add("Prerequisite: " + p);
            var example = ToolExamples.FindOrDerive(canonical, specs);
            lines.Add(ToolExamples.Render(example));
            lines.Add("Description: " + description);

            bool ready = report.Ok && prerequisitesOk != false;
            // envelope: legacy-independent-verdicts
            meta["success"] = ready; meta["ok"] = report.Ok; meta["toolFound"] = true; meta["tool"] = canonical;
            meta["signature"] = method.Signature;
            meta["layer"] = tag.Layer; meta["domain"] = tag.Domain; meta["operation"] = op.Operation;
            meta["missing"] = new JsonArray(report.Missing.Select(x => (JsonNode)x).ToArray());
            meta["unknown"] = new JsonArray(report.Unknown.Select(x => (JsonNode)x).ToArray());
            meta["caseFixes"] = new JsonArray(report.CaseFixes.Select(x => (JsonNode)x).ToArray());
            meta["typeProblems"] = new JsonArray(report.TypeProblems.Select(x => (JsonNode)x).ToArray());
            meta["coercions"] = new JsonArray(report.Coercions.Select(x => (JsonNode)x).ToArray());
            meta["warnings"] = new JsonArray(report.Warnings.Select(x => (JsonNode)x).ToArray());
            meta["dryRun"] = new JsonObject {
                ["supported"] = report.DryRunSupported, ["given"] = report.DryRunGiven, ["effective"] = report.Effective, ["confirmFlags"] =
                    new JsonArray(report.ConfirmFlags.Select(x => (JsonNode)x).ToArray()), ["confirmFlagsSet"] =
                    new JsonArray(report.ConfirmFlagsSet.Select(x => (JsonNode)x).ToArray())
            };
            meta["precautions"] = new JsonArray(precautions.Select(x => (JsonNode)x).ToArray());
            meta["prerequisites"] = new JsonObject {
                ["needsProject"] = needsProject, ["connected"] = connected, ["project"] = project, ["satisfied"] = prerequisitesOk };
            meta["example"] = JsonNode.Parse(example.ArgumentsJson);
            meta["exampleDerived"] = example.Note == ToolExamples.DerivedNote;
            string verdict = !report.Ok
                ? "NOT READY: fix " + (report.Missing.Count + report.Unknown.Count + report.TypeProblems.Count)
                    + " problem(s) listed in Items, then preflight again."
                : prerequisitesOk == false ? "Arguments fit; the session prerequisite is not met (see Items)."
                : "READY: " + canonical + " would bind"
                    + (report.CaseFixes.Count + report.Coercions.Count > 0 ? " after " + (report.CaseFixes.Count + report.Coercions.Count)
                        + " automatic correction(s) (CallTool only)" : "")
                    + (nativeParameterRead ? "; native parameter reads may execute even with dryRun=true"
                        : (report.DryRunSupported ? (report.Effective ? "; it EXECUTES (dryRun=false)" : "; it is a preview (dryRun)") : ""))
                    + ".";
            return new ResponseStringList { Message = verdict, Items = lines, Meta = meta };
        }

        internal static bool IsWriteTool(string name) => ToolTaxonomy.OperationOf(name, null).Operation is "WRITE" or "ONLINE-WRITE"
            || name == "StageImportFiles" || name == "CleanupStagedImportFiles";
        private static bool IsWrite(ToolMetadata.Classification? classification)
            => classification?.Operation is "WRITE" or "ONLINE-WRITE";

        /// <summary>Build gate: every recipe step must name a real tool and fit its signature.</summary>
        public static IReadOnlyList<string> ValidateToolRecipes()
        {
            var all = AllToolDescriptors(includeUnavailable: true);
            return ToolRecipes.ValidateAgainst(tool =>
            {
                if (!all.Keys.Any(k => string.Equals(k, tool, StringComparison.Ordinal))) return null;
                return all[tool].Parameters.Select(p => new KeyValuePair<string, bool>(p.Name, p.Required)).ToList();
            });
        }

        private static Task<(bool? Connected, string? Project)> BridgeSessionStateAsync()
        {
            Task<(bool? Connected, string? Project)>? pending = null;
            ReadSessionStateAsync(ref pending);
            if (pending != null) return pending;
            bool? connected = null; string? project = null;
            ReadSessionState(ref connected, ref project);
            return Task.FromResult((connected, project));
        }

        /// <summary>Build gate (TiaMcp.Engine.Harness generate-tools-list mode): every example in ToolExamples must fit a real tool, spelled exactly.</summary>
        public static IReadOnlyList<string> ValidateToolExamples()
        {
            var all = AllToolDescriptors(includeUnavailable: true);
            var problems = ToolExamples.ValidateAgainst(tool =>
            {
                if (!all.Keys.Any(k => string.Equals(k, tool, StringComparison.Ordinal))) return null;
                return all[tool].Parameters.Select(p => new KeyValuePair<string, bool>(p.Name, p.Required)).ToList();
            }).ToList();
            // Validate the maintained V4 samples and the exact examples served to
            // clients. This gate binds schemas only; it never invokes their targets.
            foreach (var row in RuntimeProfileEntries())
            {
                string name = (string)row!["currentName"]!;
                AssertV4Tool(name);
                var usage = ResultBody(new ToolUsageTools().GetToolUsage(toolName: name));
                if (usage?["ok"]?.GetValue<bool?>() != true) { problems.Add(name + ": usage retrieval failed"); continue; }
                foreach (var args in new[] { row["arguments"]!, usage["data"]!["example"]!["request"]!["params"]!["arguments"]! })
                {
                    var error = ValidateInfrastructureExample(name, args.AsObject());
                    if (error != null) problems.Add(name + ": " + V4Json.Serialize(error));
                }
            }
            return problems;
        }

        internal static Error? ValidateInfrastructureExample(string name, JsonObject arguments)
        {
            var error = ToolInvoker.Bind(name, new ToolArguments(JsonSerializer.SerializeToElement(arguments)), out _);
            if (error != null) return error;
            if (name == "CallTool" || name == "PreviewToolCall")
                return ToolInvoker.Bind((string)arguments["name"]!, arguments["arguments"] == null ? EmptyArguments()
                    : new ToolArguments(JsonSerializer.SerializeToElement(arguments["arguments"])), out _);
            if (name == "RunReadOnlyToolBatch" || name == "PreviewToolBatch")
                return ValidateBatch(JsonSerializer.Deserialize<ToolCall[]>(arguments["operations"]!.ToJsonString(), V4BindingJson)!,
                        name == "PreviewToolBatch", out _);

            return null;
        }
    }
}
