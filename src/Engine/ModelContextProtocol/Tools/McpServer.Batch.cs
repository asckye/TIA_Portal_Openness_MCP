using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        private static readonly BatchPlanStore BatchPlans = new BatchPlanStore();

        [McpServerTool(Name = "RunReadOnlyToolBatch"), Description("[L2][Meta][READ] Sequentially invoke 1..50 explicit [READ] tools or GetSessionState. Rejects nested orchestration and native cross-reference queries. Returns the target results in input order. An optional expectedProject binds each call to the exact project. External edits can occur; this is not a consistent native snapshot.")]
        public static CallToolResult ReadToolBatch(
            [Description("Ordered calls with name and an arguments object; 1..50 operations.")] ToolCall[] operations,
            [Description("Optional exact bound project name.")] string expectedProject = "")
        {
            const string tool = "RunReadOnlyToolBatch";
            var error = ValidateBatch(operations, false, out var validated);
            if (error != null) return V4Reject(tool, error);
            var rows = new JsonArray();
            foreach (var call in validated!)
            {
                CallToolResult result;
                try
                {
                    if (expectedProject.Length > 0) BatchState(expectedProject);
                    result = CallTool(call.Name, call.Arguments);
                }
                catch (Exception) /* swallow(privacy): preserve the explicit batch stage and outcome without exposing native exception details */ { result = V4Reject(tool, new Error("Batch project identity is unavailable or changed.", new PreconditionFailedDetails("batch-identity", expectedProject))); }
                rows.Add(BatchRow(rows.Count, call.Name, result));
            }
            return BatchResult(tool, rows, false);
        }

        [McpServerTool(Name = "PreviewToolBatch"), Description("[L2][Meta][READ] Preview 1..50 explicit [WRITE] tools with a bool dryRun parameter. Forces dryRun=true and preserves confirmation flags. expectedProject must exactly match the connected project. Returns a single-use 10-minute token bound to ordered calls, connection identity and previews. No writes, atomic rollback or complete native state coverage.")]
        public static CallToolResult PreviewToolBatch(
            [Description("Ordered calls with name and an arguments object; 1..50 operations.")] ToolCall[] operations,
            [Description("Exact bound project name.")] string expectedProject)
        {
            const string tool = "PreviewToolBatch";
            var error = ValidateBatch(operations, true, out var validated);
            if (error != null) return V4Reject(tool, error);
            try
            {
                var plan = new BatchPlanStore.Plan { Project = expectedProject, State = BatchState(expectedProject),
                    Operations = JsonNode.Parse(V4Json.Serialize(validated))!.AsArray() };
                var rows = new JsonArray();
                foreach (var call in validated!)
                {
                    var result = CallTool(call.Name, call.Arguments);
                    rows.Add(BatchRow(rows.Count, call.Name, result));
                    if (ResultSucceeded(ResultBody(result)) != true)
                    {
                        int causeIndex = rows.Count - 1;
                        foreach (var pending in validated.Skip(rows.Count))
                            rows.Add(BatchRow(rows.Count, pending.Name, V4Reject(pending.Name,
                                new Error("An earlier preview stopped the batch.", new NotExecutedDetails(causeIndex)))));
                        return BatchResult(tool, rows, false);
                    }
                    plan.Previews.Add(ResultBody(result));
                }
                if (BatchState(expectedProject) != plan.State)
                    return V4Reject(tool, new Error("Connection changed during preview.", new PreconditionFailedDetails("batch-identity", expectedProject)));
                string token = BatchPlans.Add(plan, DateTime.UtcNow);
                return V4Result(tool, new JsonObject { ["token"] = token, ["expiresUtc"] = plan.Expires.ToString("O"),
                    ["operations"] = plan.Operations.DeepClone(), ["items"] = rows,
                    ["executed"] = false, ["scope"] = "Revalidates native previews, not all engineering state. External edits can occur between calls." });
            }
            catch (Exception) /* swallow(privacy): preserve the explicit batch stage and outcome without exposing native exception details */ { return V4Reject(tool, new Error("Batch preview requires a stable bound project and available preview capacity.", new PreconditionFailedDetails("batch-preview", expectedProject))); }
        }

        [McpServerTool(Name = "ApplyToolBatch"), Description("[L2][Meta][WRITE] Consume a PreviewToolBatch token once. Recheck project/session/process identity and all stored previews before the first write. Executes exact stored ordered calls with dryRun=false; stops on failure or unknown and marks the rest NOT_EXECUTED. Earlier writes remain. No transaction, rollback or extra save/compile/download.")]
        public static CallToolResult ApplyToolBatch([Description("Single-use token from PreviewToolBatch; expires after 10 minutes.")] string token)
        {
            const string tool = "ApplyToolBatch";
            BatchPlanStore.Plan plan;
            try { plan = BatchPlans.Take(token, DateTime.UtcNow); }
            catch (Exception) /* swallow(privacy): preserve the explicit batch stage and outcome without exposing native exception details */ { return V4Reject(tool, new Error("Preview token is missing, expired or already consumed.", new NotFoundDetails(token))); }
            try
            {
                if (BatchState(plan.Project) != plan.State) return V4Reject(tool, new Error("Batch identity changed.", new PlanStaleDetails(token, "identity")));
                int index = 0;
                foreach (var op in plan.Operations.OfType<JsonObject>())
                {
                    var result = CallTool((string)op["name"]!, new ToolArguments(JsonSerializer.SerializeToElement(op["arguments"])));
                    if (ResultSucceeded(ResultBody(result)) != true || StablePreview(ResultBody(result)) != StablePreview(plan.Previews[index++]))
                        return V4Reject(tool, new Error("A stored preview changed.", new PlanStaleDetails(token, "preview")));
                }
            }
            catch (Exception) /* swallow(privacy): preserve the explicit batch stage and outcome without exposing native exception details */ { return V4Reject(tool, new Error("Cannot revalidate batch identity and previews.", new PreconditionFailedDetails("batch-revalidation", plan.Project))); }
            var rows = new JsonArray();
            int? cause = null;
            foreach (var op in plan.Operations.OfType<JsonObject>())
            {
                string name = (string)op["name"]!;
                CallToolResult result;
                bool issued = false;
                if (cause.HasValue) result = V4Reject(name, new Error("An earlier batch item stopped execution.", new NotExecutedDetails(cause)));
                else
                {
                    try
                    {
                        if (BatchState(plan.Project) != plan.State) throw new InvalidOperationException();
                        var args = (JsonObject)op["arguments"]!.DeepClone(); args["dryRun"] = false;
                        issued = true;
                        result = CallTool(name, new ToolArguments(JsonSerializer.SerializeToElement(args)));
                        if (ResultSucceeded(ResultBody(result)) != true) cause = rows.Count;
                    }
                    catch (Exception) /* swallow(privacy): preserve the explicit batch stage and outcome without exposing native exception details */
                    {
                        result = issued ? V4Result(name, null, new Error("The issued write outcome is unknown.", new OutcomeUnknownDetails("batch-call", new Dictionary<string, JsonElement>())),
                            Outcome.Unknown, Execution.Unknown, Completeness.Unknown, current: true)
                            : V4Reject(name, new Error("Batch identity changed before this write.", new PreconditionFailedDetails("batch-identity", plan.Project)));
                        cause = rows.Count;
                    }
                }
                var row = BatchRow(rows.Count, name, result);
                // An unreadable target result must never make the parent successful.
                if (issued && ResultSucceeded(row["result"]) == null) row["outcomeUnknown"] = true;
                rows.Add(row);
            }
            return BatchResult(tool, rows, true);
        }

        internal static Error? ValidateBatch(ToolCall[] operations, bool write, out ToolCall[]? validated)
        {
            validated = null;
            if (operations == null || operations.Length == 0) return InvalidInput("operations");
            if (operations.Length > 50) return new Error("Batch count exceeds its limit.", new LimitExceededDetails("operations", 50, operations.Length));
            var targets = new Dictionary<string, ToolTarget>(StringComparer.Ordinal);
            foreach (var call in operations)
            {
                if (call == null) return InvalidInput("operations");
                var args = JsonNode.Parse(call.Arguments.Json.GetRawText())!.AsObject();
                if (write) args["dryRun"] = true;
                var error = BindV4Call(call.Name, new ToolArguments(JsonSerializer.SerializeToElement(args)), out var method, out _);
                if (error != null) return error;
                var targetMethod = method!;
                string name = targetMethod.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? targetMethod.Name;
                var classification = ClassificationOf(targetMethod);
                bool orchestration = IsBatchOrchestration(targetMethod);
                bool read = classification?.BatchRead == true;
                bool preview = classification?.BatchWrite == true && targetMethod.GetParameters().Any(p => p.Name == "dryRun" && p.ParameterType == typeof(bool));
                if (orchestration || (write ? !preview : !read)) return InvalidInput("operations");
                targets[call.Name] = new ToolTarget(call.Name, new InputSchema(ToolInputSchema(call.Name, targetMethod)), new InputBudget(), read, preview, orchestration: orchestration);
            }
            var result = ToolCallValidator.Create(write ? ToolCallMode.PreviewBatch : ToolCallMode.ReadBatch, targets.Values.ToArray()).Validate(operations, "operations");
            validated = result.Value;
            return result.Error;
        }

        internal static bool IsBatchOrchestration(MethodInfo method) => method.Name.Contains("Batch")
            || method.Name == "CallTool" || method.GetCustomAttribute<McpServerToolAttribute>()?.Name == "RunToolTransaction"
            || method.GetCustomAttribute<McpServerToolAttribute>()?.Name == "GetPlcCrossReferences";

        private static string StablePreview(JsonNode? value)
        {
            var copy = value?.DeepClone();
            if (copy?["schemaVersion"]?.GetValue<int?>() == 4) copy["meta"]!.AsObject().Remove("requestId");
            return BatchPlanStore.Stable(copy);
        }
        internal static bool? ResultSucceeded(JsonNode? body)
        {
            if (body is not JsonObject obj) return null;
            if (obj["schemaVersion"]?.GetValue<int?>() == 4) return obj["ok"]?.GetValue<bool?>();
            return null;
        }
        private static JsonObject BatchRow(int index, string target, CallToolResult result) => new JsonObject
        { ["index"] = index, ["target"] = target, ["result"] = ResultBody(result) };

        private static CallToolResult BatchResult(string tool, JsonArray rows, bool write)
        {
            int succeeded = rows.Count(row => ResultSucceeded(row!["result"]) == true);
            int skipped = rows.Count(row => (string?)row!["result"]?["error"]?["code"] == "NOT_EXECUTED");
            int failed = rows.Count - succeeded - skipped;
            bool unknown = rows.Any(row => (bool?)row!["outcomeUnknown"] == true || (string?)row["result"]?["meta"]?["outcome"] == "unknown");
            bool partial = rows.Any(row => (string?)row!["result"]?["meta"]?["outcome"] == "partial");
            var data = new JsonObject { ["items"] = rows, ["rollbackPerformed"] = false };
            if (unknown) return V4Result(tool, data, new Error("A batch write outcome is unknown.", new OutcomeUnknownDetails("batch", new Dictionary<string, JsonElement>())), Outcome.Unknown, Execution.Unknown, Completeness.Unknown, current: write);
            if (succeeded == rows.Count) return V4Result(tool, data, completed: write, current: write);
            if (partial || succeeded > 0) return V4Result(tool, data, new Error("The batch contains partial results.",
                succeeded > 0 ? new PartialFailureDetails(succeeded, failed, skipped) : new PartialFailureDetails(0, 0, 0)), Outcome.Partial, Execution.Partial, Completeness.Partial, current: write);
            if (rows.All(row => (string?)row!["result"]?["meta"]?["execution"] == "not-started"))
                return V4Reject(tool, new Error("Every batch item was rejected before execution.", new PreconditionFailedDetails("batch-results", null)), data);
            return V4Result(tool, data, new Error("No batch item succeeded; inspect the retained target results.", new PreconditionFailedDetails("batch-results", null)),
                write ? Outcome.Failed : Outcome.ReadFailed, write ? Execution.Completed : Execution.ReadOnly, Completeness.None, current: write);
        }
        private static string BatchState(string project)
        {
            if (string.IsNullOrWhiteSpace(project)) throw new ArgumentException("expectedProject is required.");
            EngineServices.Get<Siemens.Portal>().EnsureBoundProjectUnchanged("Batch identity");
            var state = EngineServices.Get<SessionTools>().GetState();
            if (state.IsConnected != true || !string.Equals(state.Project, project, StringComparison.Ordinal)) throw new InvalidOperationException("Expected project is not connected: " + project);
            var health = EngineServices.Get<Siemens.Portal>().GetPortalProcessHealth();
            if (health["boundProcessId"] == null || health["processAlive"]?.GetValue<bool?>() != true) throw new InvalidOperationException("Bound TIA process identity is unavailable.");
            return BatchPlanStore.BindingState(EngineServices.Get<Siemens.Portal>().GetBindingIdentity());
        }
    }
}
