using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.Generation;
using TiaMcp.Logic.V4;
using TiaOpenness.Shared;

namespace TiaMcpServer.ModelContextProtocol
{
    internal sealed partial class GenerationTools
    {
        [McpServerTool(Name = "PublishGenerationRecord"), Description("[L1][Project][FILE] Publish tiamcp.generation-record/1 beside the bound project file, from durable generation run facts and post-save READ witnesses. Supply exactly one of planId/runId. Preview defaults true and returns the path, previous record hash, new record hash and tool planHash. Apply recomputes everything, requires expectedPlanHash and normal approval, then writes atomically in the same folder. Links/reparse points and changed previous records are refused. A generation run uses one reviewed record step and its single-step grant. No native writes; retain run evidence on file failure.")]
        public CallToolResult PublishGenerationRecord(
            [Description("Exact .tiagen.json path beside the bound .apXX file.")] string recordPath,
            [Description("Generation planId; supply this or runId, exclusively.")] string planId = "",
            [Description("Durable runId; supply this or planId, exclusively.")] string runId = "",
            [Description("true previews; false publishes after approval.")] bool dryRun = true,
            [Description("Exact tool planHash returned by preview; required for apply.")] string expectedPlanHash = "")
            => Execute("PublishGenerationRecord", () => ExecutionSession.Publish(recordPath, planId, runId, dryRun, expectedPlanHash));

        [McpServerTool(Name = "ManageGenerationArtifacts"), Description("[L1][Project][FILE] List retained generation staging evidence or clean one exact artifactId returned by list. Run logs, consumed claims and backups are always retained. Cleanup previews by default, requires the exact fresh expectedPlanHash and normal approval, refuses links and changed files, and retains artifacts referenced by active/interrupted runs. Completed-run artifacts are deleted only by explicit cleanup. No TIA calls.")]
        public CallToolResult ManageGenerationArtifacts(
            [Description("list or cleanup.")] string action,
            [Description("Exact artifactId from list; cleanup only.")] string artifactId = "",
            [Description("true previews; false cleans after approval.")] bool dryRun = true,
            [Description("Exact tool planHash from cleanup preview.")] string expectedPlanHash = "")
            => Execute("ManageGenerationArtifacts", () => ExecutionSession.Artifacts(action, artifactId, dryRun, expectedPlanHash));
    }

    internal sealed partial class GenerationExecutionSession
    {
        internal CallToolResult Artifacts(string action, string id, bool preview, string hash)
        {
            try { return McpServer.V4Result("ManageGenerationArtifacts", new GenerationArtifacts(root()).Manage(action, id, preview, hash), completed: action == "cleanup" && !preview); }
            catch (Exception ex) when (!preview && action == "cleanup" && (ex is IOException || ex is UnauthorizedAccessException))
            {
                int deleted = (ex as GenerationArtifactCleanupException)?.DeletedFiles ?? 0;
                var data = new JsonObject { ["artifactId"] = id, ["diagnostic"] = ex.Message, ["deletedFiles"] = deleted };
                return deleted > 0 ? McpServer.V4Result("ManageGenerationArtifacts", data,
                    new Error("Artifact cleanup stopped after deleting files; inspect retained evidence before retrying.", new PartialFailureDetails(deleted, 1, 0)), Outcome.Partial, Execution.Partial, Completeness.Partial)
                    : McpServer.V4Result("ManageGenerationArtifacts", data, new Error("Artifact cleanup failed before deleting files.", new IoFailedDetails("generation-artifacts", id)), Outcome.RejectedBeforeOperation, Execution.NotStarted, Completeness.None);
            }
        }
        private static readonly JsonSerializerOptions RecordOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private static GenerationRecordObject Copy(GenerationRecordObject item) => JsonSerializer.Deserialize<GenerationRecordObject>(JsonSerializer.Serialize(item, RecordOptions), RecordOptions)!;
        private void LoadOwnership(string project, string release, ProjectModel observed, GenerationPlanningOptions options)
        {
            var previous = GenerationRecordFile.Read(options.RecordPath!);
            options.PreviousRecordHash = previous.PreviousHash;
            var record = previous.Record;
            if (record != null && (!SameProject(record.Project.File, project) || record.Project.Release != release)) record = null;
            var owned = new Dictionary<string, GenerationRecordObject>(StringComparer.OrdinalIgnoreCase);
            if (record != null && SameProject(record.Project.File, project) && record.Project.Release == release)
                foreach (var item in record.Objects) owned[item.Key] = Copy(item);
            var recordedRuns = record?.Runs.Select(r => r.RunId).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
            foreach (var run in Records.ForProject(project, release))
                if (!recordedRuns.Contains((string)run["runId"]!)) Overlay(run, owned);
            options.Ownership = owned.Values.OrderBy(o => o.Key, StringComparer.Ordinal).ToList();
            var present = ProjectModelComparison.Describe(observed).ToDictionary(o => o.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var item in options.Ownership.Where(o => present.ContainsKey(o.Key) && (o.State != "unknown" || options.Adopt.Contains(o.Key, StringComparer.OrdinalIgnoreCase))))
            {
                var current = Witness(item, options.SoftwarePaths, present[item.Key]);
                if (current != null) options.Witnesses[item.Key] = current;
            }
        }
        private static bool SameProject(string first, string second) => string.Equals(first.Replace('\\', '/'), second.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        private static void Overlay(JsonObject run, Dictionary<string, GenerationRecordObject> owned)
        {
            if (run["plan"] is not JsonObject planNode) return;
            var plan = GenerationDocuments.Load<GenerationPlan>(planNode.ToJsonString());
            var witnessed = run["events"]!.AsArray().OfType<JsonObject>().LastOrDefault(e => (string?)e["event"] == "witness")?["objects"];
            if (witnessed is JsonArray objects)
                foreach (var item in objects) { var value = JsonSerializer.Deserialize<GenerationRecordObject>(item!.ToJsonString(), RecordOptions)!; owned[value.Key] = value; }
            foreach (var step in run["steps"]!.AsArray().OfType<JsonObject>().Where(s => (string?)s["status"] is "succeeded" or "unknown" or "effect-diverged"))
            {
                string key = (string)step["key"]!;
                var expected = plan.Objects.SingleOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase));
                if (expected == null || (string?)step["tool"] == "DeletePlcExternalSource") continue;
                bool hasWitness = owned.TryGetValue(key, out var prior) && prior.CreatedBy.RunId == (string)run["runId"]! && prior.Witness != null;
                if (hasWitness && (string?)step["status"] == "succeeded") continue;
                var value = Copy(expected); value.State = "unknown"; value.Witness = null;
                value.CreatedBy = new GenerationObjectOrigin { RunId = (string)run["runId"]!, StepId = (string)step["id"]!, RequestId = (string?)step["requestId"] ?? "" };
                owned[key] = value;
            }
        }
        private List<GenerationRecordObject> CollectRunObjects(JsonObject run, GenerationPlan plan)
        {
            var owned = new Dictionary<string, GenerationRecordObject>(StringComparer.OrdinalIgnoreCase);
            Overlay(run, owned);
            var paths = JsonSerializer.Deserialize<Dictionary<string, string>>(run["softwarePaths"]?.ToJsonString() ?? "{}")!;
            bool saved = run["steps"]!.AsArray().Any(s => (string?)s!["tool"] == "SaveProject" && (string?)s["status"] == "succeeded");
            if (!saved) throw new GenerationCallRejection(new Error("Successful SaveProject is required before collecting generation witnesses.", new PreconditionFailedDetails("generation-save", (string?)run["runId"])));
            foreach (string key in plan.Adopt)
            {
                var item = Copy(plan.Objects.Single(o => o.Key == key)); item.State = "adopted";
                item.CreatedBy = new GenerationObjectOrigin { RunId = (string)run["runId"]!, StepId = "adopt", RequestId = (string)run["parentRequestId"]! }; owned[key] = item;
            }
            foreach (var item in owned.Values)
            {
                if (run["steps"]!.AsArray().Any(s => (string?)s!["key"] == item.Key && (string?)s["status"] is "unknown" or "effect-diverged")) continue;
                item.Witness = Witness(item, paths);
                if (item.Witness != null && item.State != "adopted") item.State = "owned";
            }
            return owned.Values.OrderBy(o => o.Key, StringComparer.Ordinal).ToList();
        }
        private void PersistWitnesses(GenerationRunStore records, string runId, GenerationPlan plan)
        {
            try { records.Append(runId, new JsonObject { ["event"] = "witness", ["objects"] = JsonSerializer.SerializeToNode(CollectRunObjects(records.Read(runId), plan), RecordOptions) }); }
            catch (GenerationCallRejection ex) { records.Append(runId, new JsonObject { ["event"] = "witness-refused", ["error"] = ex.Message }); }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException)
            { records.Append(runId, new JsonObject { ["event"] = "witness-refused", ["error"] = ex.Message }); }
        }
        internal CallToolResult Publish(string recordPath, string planId, string runId, bool preview, string hash)
        {
            if ((planId.Length == 0) == (runId.Length == 0)) throw new ArgumentException("Supply exactly one of planId and runId.", "runId");
            var run = planId.Length != 0 ? Records.ByPlan(planId) : Records.Read(runId);
            var before = Target(); string project = (string)run["project"]!, release = (string)run["release"]!;
            if (!SameProject(before.Project, project) || release != McpServer.ReleaseKey || !SameProject(recordPath, GenerationRecordFile.DefaultPath(project)))
                throw new ArgumentException("Record/run must belong to the bound project and exact default record path.", "recordPath");
            var plan = GenerationDocuments.Load<GenerationPlan>(run["plan"]!.ToJsonString());
            var previous = GenerationRecordFile.Read(recordPath);
            if (previous.Record != null && (!SameProject(previous.Record.Project.File, project) || previous.Record.Project.Release != release))
                throw new ArgumentException("Previous generation record belongs to another project/release.", "recordPath");
            var record = previous.Record ?? new GenerationRecord { Project = new GenerationRecordProject { File = project, Release = release } };
            record.PreviousRecordHash = previous.PreviousHash == "absent" ? null : previous.PreviousHash;
            var objects = record.Objects.ToDictionary(o => o.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var item in CollectRunObjects(run, plan)) objects[item.Key] = item;
            record.Objects = objects.Values.OrderBy(o => o.Key, StringComparer.Ordinal).ToList();
            var events = run["events"]!.AsArray().OfType<JsonObject>().ToArray();
            var preceding = run["steps"]!.AsArray().Where(s => (string?)s!["phase"] != "record").ToArray();
            record.Runs.RemoveAll(r => r.RunId == (string)run["runId"]!);
            record.Runs.Add(new GenerationRecordRun { RunId = (string)run["runId"]!, PlanId = (string)run["planId"]!, PlanHash = plan.PlanHash,
                Package = plan.Package, MachineHash = plan.MachineHash, Status = preceding.All(s => (string?)s!["status"] == "succeeded") ? "succeeded" : "partial",
                StartedUtc = (string)run["utc"]!, EndedUtc = (string?)events.LastOrDefault(e => (string?)e["event"] == "step-end" && (string?)e["stepId"] == plan.Steps.LastOrDefault(s => s.Tool == "SaveProject")?.Id)?["utc"] ?? (string)run["utc"]!,
                ParentRequestId = (string)run["parentRequestId"]!, ApprovalRequestId = (string?)events.FirstOrDefault(e => (string?)e["event"] == "approval")?["requestId"] ?? "",
                Actor = (string?)run["actor"] ?? "mcp" });
            record.RecordHash = GenerationRecord.Hash(record);
            var data = new JsonObject { ["recordPath"] = recordPath, ["previousRecordHash"] = previous.PreviousHash, ["previousFileHash"] = previous.Token,
                ["recordHash"] = record.RecordHash, ["projectFile"] = project, ["release"] = release, ["runId"] = (string)run["runId"]!,
                ["record"] = JsonNode.Parse(GenerationDocuments.Canonical(record)) };
            string planHash = Digest(CanonicalJson.Hash(CanonicalJson.Parse(data.ToJsonString())));
            data["planHash"] = planHash; data["executed"] = false; data["wouldChange"] = true;
            if (Target() != before) throw new ArgumentException("Project binding changed while collecting witnesses.", "expectedPlanHash");
            if (!preview)
            {
                if (hash != planHash) throw new ArgumentException("Generation publication preview is stale; preview again.", "expectedPlanHash");
                try { GenerationRecordFile.Write(recordPath, record, previous.Token); data["executed"] = true; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { data["diagnostic"] = ex.Message; return McpServer.V4Result("PublishGenerationRecord", data, new Error("Generation record publication failed after successful saved run steps; retain the run and preview again.", new PartialFailureDetails(preceding.Count(s => (string?)s!["status"] == "succeeded"), 1, 0)), Outcome.Partial, Execution.Partial, Completeness.Partial); }
            }
            return McpServer.V4Result("PublishGenerationRecord", data, completed: !preview);
        }
        private static JsonNode? Scalar(JsonObject node, params string[] names)
        {
            foreach (string name in names)
                foreach (var candidate in Walk(node))
                {
                    var direct = candidate.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));
                    if (direct.Value is JsonValue) return direct.Value.DeepClone();
                    if (string.Equals((string?)candidate["name"], name, StringComparison.OrdinalIgnoreCase) && candidate["value"] is JsonValue value) return value.DeepClone();
                }
            return null;
        }
        private GenerationWitness? Witness(GenerationRecordObject item, Dictionary<string, string> paths, GenerationRecordObject? present = null)
        {
            string[] parts = item.Key.Split('/'); string station = Uri.UnescapeDataString(parts[0]), kind = parts[1], name = Uri.UnescapeDataString(parts[2]);
            string software = paths.TryGetValue(station, out var exact) ? exact : station;
            var values = new JsonObject { ["path"] = present?.Path ?? item.Path };
            string witnessKind, tool;
            if (kind is "block" or "type")
            {
                if (McpServer.ReleaseKey is "20" or "21")
                {
                    tool = "GetPlcObjectFingerprints"; witnessKind = "plc-object-fingerprints";
                    var data = Read(tool, new JsonObject { ["softwarePath"] = software, ["objectKind"] = kind, ["objectPath"] = present?.Path ?? item.Path });
                    var fingerprints = (data["records"] ?? data["meta"]?["records"]) as JsonArray;
                    if (fingerprints == null || fingerprints.Count == 0) throw new ArgumentException("Complete nonempty PLC fingerprints required.");
                    values["fingerprints"] = new JsonArray(fingerprints.OrderBy(f => (string?)f!["id"], StringComparer.Ordinal).Select(f => f!.DeepClone()).ToArray());
                }
                else
                {
                    tool = kind == "block" ? "GetPlcBlockInfo" : "GetPlcTypeInfo"; witnessKind = "modified-date";
                    var data = Read(tool, new JsonObject { ["softwarePath"] = software, [kind == "block" ? "blockPath" : "typePath"] = present?.Path ?? item.Path });
                    var modified = Walk(data).SelectMany(o => o).Where(p => string.Equals(p.Key, "modifiedDate", StringComparison.OrdinalIgnoreCase)).Select(p => p.Value).ToArray();
                    if (modified.Length != 1 || string.IsNullOrEmpty(modified[0]?.ToString())) throw new ArgumentException("A single readable ModifiedDate is required.");
                    values["modifiedDate"] = modified[0]!.DeepClone();
                }
            }
            else if (kind == "externalSource")
            {
                witnessKind = "external-source"; tool = "ListPlcExternalSources";
                bool exists = Items(Read(tool, new JsonObject { ["softwarePath"] = software })).Any(s => Name(s) == name);
                values["name"] = name; values["contentHash"] = item.Artifact?.Sha256 ?? ""; values["present"] = exists;
            }
            else if (kind == "group")
            {
                witnessKind = "path"; tool = "GetSoftwareTree";
                var tree = Read(tool, new JsonObject { ["softwarePath"] = software });
                var rows = (tree["paths"] ?? tree["meta"]?["paths"]) as JsonArray;
                string expectedKind = name.StartsWith("blocks/", StringComparison.Ordinal) ? "block-group" : "type-group";
                if (rows == null || !rows.Any(r => (string?)r!["kind"] == expectedKind && Uri.UnescapeDataString((string?)r["objectPath"] ?? "") == item.Path)) return null;
            }
            else if (kind is "tag" or "tagTable")
            {
                witnessKind = "content"; tool = "ListPlcTags"; string table = kind == "tag" ? item.Path.Substring(0, item.Path.LastIndexOf('/')) : name;
                var tags = Items(Read(tool, new JsonObject { ["plc"] = software, ["table"] = table })).ToArray();
                var selected = kind == "tag" ? tags.Where(t => Name(t) == name).ToArray() : tags;
                if (kind == "tag" && selected.Length != 1) throw new ArgumentException("Complete exact tag readback required.");
                values["tags"] = new JsonArray(selected.OrderBy(Name, StringComparer.Ordinal).Select(t => (JsonNode?)new JsonObject { ["name"] = Name(t),
                    ["dataType"] = t["dataType"]?.DeepClone(), ["address"] = (t["value"] ?? t["address"])?.DeepClone() }).ToArray());
                values["constants"] = JsonSerializer.SerializeToNode(Items(Read("ReadPlcUserConstants", new JsonObject { ["plc"] = software, ["table"] = table })).OrderBy(Name, StringComparer.Ordinal).ToArray());
            }
            else if (kind == "device")
            {
                witnessKind = "hardware"; tool = "GetProjectTree";
                var candidates = Walk(Read(tool)).Where(n => (string?)n["name"] == name && ((string?)n["kind"] == "device" || n["deviceItems"] != null || n["articleNumber"] != null)).ToArray();
                JsonObject node;
                if (candidates.Length == 1) node = candidates[0];
                else
                {
                    tool = item.Path.Contains('/') ? "GetDeviceItemInfo" : "GetDeviceInfo";
                    node = Read(tool, new JsonObject { [tool == "GetDeviceInfo" ? "devicePath" : "deviceItemPath"] = item.Path });
                }
                values["name"] = Scalar(node, "name");
                values["orderNumber"] = Scalar(node, "articleNumber", "orderNumber", "OrderNumber", "TypeIdentifier");
                values["firmware"] = Scalar(node, "version", "firmware", "FirmwareVersion");
                values["slot"] = Scalar(node, "slot", "positionNumber", "PositionNumber");
                if (values["name"]?.ToString() != name || string.IsNullOrEmpty(values["orderNumber"]?.ToString()) || string.IsNullOrEmpty(values["firmware"]?.ToString())) return null;
            }
            else return null;
            return new GenerationWitness { Kind = witnessKind, Tool = tool, Values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(values.ToJsonString())! };
        }
    }
}
