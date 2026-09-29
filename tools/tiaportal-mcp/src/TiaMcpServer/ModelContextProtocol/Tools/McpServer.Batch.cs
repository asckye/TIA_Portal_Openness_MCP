using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        private static readonly BatchPlanStore BatchPlans = new BatchPlanStore();

        [McpServerTool(Name = "ReadToolBatch"), Description("[L2][Meta][READ] Sequentially invoke 1..50 tools explicitly declared [READ], or GetState. operationsJson is [{name,arguments:{...}}]. No inferred name classification and no native GetCrossReferences. Rejects nested orchestration. Returns every result with succeeded/failed/unknown status; unknown is not success. Optional expectedProject requires exact bound project. Not a consistent project snapshot; external TIA edits can occur.")]
        public static ResponseMessage ReadToolBatch([Description("Ordered JSON array of {name,arguments:{...}}; 1..50 operations.")] string operationsJson, [Description("Exact project name required to bind this batch to the intended open project.")] string expectedProject = "")
            => BatchResult(meta =>
            {
                var operations = ValidateBatch(operationsJson, false);
                var rows = new JsonArray(); bool complete = true;
                foreach (var op in operations.OfType<JsonObject>())
                {
                    if (expectedProject.Length > 0) BatchState(expectedProject);
                    var result = CallTool(op["name"]!.GetValue<string>(), op["arguments"]!.ToJsonString());
                    bool success = result.Meta?["operationSuccess"]?.GetValue<bool?>() == true;
                    complete &= success;
                    rows.Add(new JsonObject { ["name"] = op["name"]!.DeepClone(), ["result"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(result)), ["status"] = result.Meta?["operationStatus"]?.DeepClone() });
                }
                meta["results"] = rows; meta["success"] = complete; meta["dataComplete"] = complete;
                return "Read batch completed; inspect per-item status.";
            });

        [McpServerTool(Name = "PreviewToolBatch"), Description("[L2][Meta][READ] Preview 1..50 project writes as [{name,arguments:{...}}]. Only tools explicitly tagged [WRITE] with a bool dryRun parameter are supported. Forces dryRun=true for previews; preserves explicit confirmation flags for later execution. expectedProject must exactly match the connected project. Successful previews yield a single-use 10-minute token bound to stored ordered calls, connection identity and preview results. No writes executed. No promise of atomic rollback or complete native state coverage.")]
        public static ResponseMessage PreviewToolBatch([Description("Ordered JSON array of {name,arguments:{...}}; 1..50 operations.")] string operationsJson, [Description("Exact project name required to bind this batch to the intended open project.")] string expectedProject)
            => BatchResult(meta =>
            {
                var plan = new BatchPlanStore.Plan { Project = expectedProject, State = BatchState(expectedProject), Operations = ValidateBatch(operationsJson, true) };
                foreach (var op in plan.Operations.OfType<JsonObject>()) plan.Previews.Add(BatchPreview(op));
                if (BatchState(expectedProject) != plan.State) throw new InvalidOperationException("Connection changed during preview.");
                meta["token"] = BatchPlans.Add(plan, DateTime.UtcNow); meta["expiresUtc"] = plan.Expires.ToString("O");
                meta["operations"] = plan.Operations.DeepClone(); meta["previews"] = plan.Previews.DeepClone(); meta["success"] = true; meta["executed"] = false;
                meta["scope"] = "Revalidates native preview results, not all engineering state. Another TIA client can still edit between calls.";
                return "Batch preview ready.";
            });

        [McpServerTool(Name = "ApplyToolBatch"), Description("[L2][Meta][WRITE] Consume a PreviewToolBatch token once. Recheck project/session/process identity and re-run every stored preview before the first write; changed previews abort. Executes the exact stored ordered operations with dryRun=false. Stops on first failure or unknown result and marks the rest skipped. Prior writes remain: this is NOT a transaction and has NO automatic rollback. No additional save/compile/download is performed. Token cannot change operations or be reused.")]
        public static ResponseMessage ApplyToolBatch([Description("Single-use token from PreviewToolBatch; expires after 10 minutes.")] string token)
            => BatchResult(meta =>
            {
                var plan = BatchPlans.Take(token, DateTime.UtcNow);
                if (BatchState(plan.Project) != plan.State) throw new InvalidOperationException("Project/session/process changed; preview again.");
                int i = 0;
                foreach (var op in plan.Operations.OfType<JsonObject>())
                    if (BatchPlanStore.Stable(BatchPreview(op)) != BatchPlanStore.Stable(plan.Previews[i++])) throw new InvalidOperationException("A native preview changed; preview the batch again.");
                var rows = new JsonArray(); meta["results"] = rows; bool stopped = false;
                foreach (var op in plan.Operations.OfType<JsonObject>())
                {
                    var row = new JsonObject { ["name"] = op["name"]!.DeepClone(), ["status"] = "skipped" }; rows.Add(row);
                    if (stopped) continue;
                    try
                    {
                        if (BatchState(plan.Project) != plan.State) throw new InvalidOperationException("Connection changed.");
                        var args = (JsonObject)op["arguments"]!.DeepClone(); args["dryRun"] = false;
                        meta["mayHaveModifiedProject"] = true;
                        var result = CallTool(op["name"]!.GetValue<string>(), args.ToJsonString());
                        row["result"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(result)); row["status"] = result.Meta?["operationStatus"]?.DeepClone() ?? (JsonNode)"unknown";
                        stopped = result.Meta?["operationSuccess"]?.GetValue<bool?>() != true;
                    }
                    catch (Exception ex) { row["status"] = "failed"; row["error"] = ex.Message; stopped = true; }
                }
                meta["success"] = !stopped; meta["rollbackPerformed"] = false; meta["dataComplete"] = !stopped;
                return stopped ? "Batch stopped. Earlier writes may remain; inspect results before recovery." : "Batch calls succeeded; native readback remains tool-specific.";
            });

        private static JsonArray ValidateBatch(string text, bool write)
        {
            var array = JsonNode.Parse(text) as JsonArray ?? throw new ArgumentException("operationsJson must be an array.");
            if (array.Count < 1 || array.Count > 50) throw new ArgumentException("Use 1..50 operations.");
            var methods = AllToolMethods(); var result = new JsonArray();
            foreach (var node in array)
            {
                var op = node as JsonObject ?? throw new ArgumentException("Each operation must be an object.");
                if (op.Any(p => p.Key != "name" && p.Key != "arguments")) throw new ArgumentException("Only name and arguments are accepted per operation.");
                string name = op["name"]?.GetValue<string>() ?? "";
                if (!methods.TryGetValue(name, out var method)) throw new ArgumentException("Unknown tool: " + name);
                if (method.Name.Contains("Batch") || method.Name == "CallTool" || method.Name == "RunToolsInTransaction" || method.Name == "GetCrossReferences") throw new ArgumentException("Nested orchestration/native cross-reference query is not supported in batches.");
                string description = ToolDescription(method);
                if (write ? !description.Contains("[WRITE]") || !method.GetParameters().Any(p => p.Name == "dryRun" && p.ParameterType == typeof(bool)) : !description.Contains("[READ]") && method.Name != "GetState")
                    throw new ArgumentException("Tool is outside the explicit " + (write ? "previewable write" : "read") + " batch contract: " + name);
                var args = op["arguments"] == null ? new JsonObject() : op["arguments"] as JsonObject ?? throw new ArgumentException("arguments must be an object.");
                args = (JsonObject)args.DeepClone();
                if (args.Any(p => p.Key.Equals("dryRun", StringComparison.OrdinalIgnoreCase) && p.Key != "dryRun")) throw new ArgumentException("Use exact parameter spelling dryRun.");
                if (write) args["dryRun"] = true;
                var preflight = PreflightToolCall(name, args.ToJsonString());
                if (preflight.Meta?["ok"]?.GetValue<bool?>() != true) throw new ArgumentException("Preflight failed for " + name + ": " + preflight.Message);
                result.Add(new JsonObject { ["name"] = name, ["arguments"] = args });
            }
            return result;
        }
        private static JsonNode BatchPreview(JsonObject op)
        {
            var result = CallTool(op["name"]!.GetValue<string>(), op["arguments"]!.ToJsonString());
            if (result.Meta?["operationSuccess"]?.GetValue<bool?>() != true) throw new InvalidOperationException("Preview failed or returned unknown: " + op["name"] + ": " + result.Message);
            return JsonNode.Parse(result.Message ?? "null") ?? throw new InvalidOperationException("Preview payload missing.");
        }
        private static string BatchState(string project)
        {
            if (string.IsNullOrWhiteSpace(project)) throw new ArgumentException("expectedProject is required.");
            var state = GetState();
            if (state.IsConnected != true || !string.Equals(state.Project, project, StringComparison.Ordinal)) throw new InvalidOperationException("Expected project is not connected: " + project);
            var health = Portal.GetPortalProcessHealth();
            if (health["boundProcessId"] == null || health["processAlive"]?.GetValue<bool?>() != true) throw new InvalidOperationException("Bound TIA process identity is unavailable.");
            return project + "|" + state.Session + "|" + health["boundProcessId"];
        }
        private static ResponseMessage BatchResult(Func<JsonObject, string> action)
        {
            var meta = new JsonObject { ["success"] = false, ["timestamp"] = DateTime.UtcNow };
            try { return new ResponseMessage { Message = action(meta), Meta = meta }; }
            catch (Exception ex) { meta["error"] = ex.Message; return new ResponseMessage { Message = "Batch refused/failed: " + ex.Message, Meta = meta }; }
        }
    }
}
