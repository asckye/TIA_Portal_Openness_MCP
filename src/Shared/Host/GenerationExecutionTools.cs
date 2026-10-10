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
using TiaMcp.Logic.V4.Inputs;
using TiaOpenness.Shared;

namespace TiaMcpServer.ModelContextProtocol
{
    internal sealed partial class GenerationTools
    {
        private GenerationExecutionSession ExecutionSession => TiaMcp.FoundationHost.EngineHostConfiguration.Current.Generation;

        [McpServerTool(Name = "PlanProjectGeneration"), Description("[L1][Project][READ] Expand an exact standard package and structured machine against the bound project's complete read-tool inventory. Session release must match. Returns deterministic tiamcp.plan/1 with fixed existing-tool arguments, digests, release availability, conflicts and staged artifact hashes. No native writes. Unprovable readback conflicts; unavailable phases are skipped. Plans belong to this MCP session and binding; inspect before apply.")]
        public CallToolResult PlanProjectGeneration(
            [Description("Exact installed package id.")] string packageId,
            [Description("Exact installed SemVer version.")] string version,
            [Description("Structured tiamcp.machine/1, existing bound project; projectIdentity must be its exact project file.")] ToolArguments machine)
            => Execute("PlanProjectGeneration", () => ExecutionSession.Plan(store().Effective(store().Load(packageId, version)), machine.Json.GetRawText()));

        [McpServerTool(Name = "ApplyGenerationPlan"), Description("[L1][Project][WRITE] Preview a stored generation plan by default. Execute with dryRun=false, exact fresh expectedPlanHash and expectedProject (bound project file). One Workbench plan approval binds all selected steps; each uses existing admission, precheck, version gate, audit and worker lane. Recheck binding before every write, stop on refusal/failure/unknown, retain completed writes and append-only run evidence. Never retry; replan against observed state. Optional phases must include all dependencies. Unavailable phases are skipped by default.")]
        public CallToolResult ApplyGenerationPlan(
            [Description("planId returned in this MCP session.")] string planId,
            [Description("Exact planHash from planning; required even for preview.")] string expectedPlanHash,
            [Description("Exact bound project file recorded by the plan.")] string expectedProject,
            [Description("Optional phase names; dependencies must be selected too.")] string[]? phases = null,
            [Description("true previews without approval/write audit; false executes once.")] bool dryRun = true)
            => Execute("ApplyGenerationPlan", () => ExecutionSession.Apply(planId, expectedPlanHash, expectedProject, phases, dryRun));

        [McpServerTool(Name = "GetGenerationRun"), Description("[L1][Project][READ] Read an append-only tiamcp.run/1 generation record under the host data folder, including steps, outcomes, evidence, audit requestIds and stopping reason. Survives host restarts; an interrupted step is unknown and never replayed. This reads local records only, without a bound project.")]
        public CallToolResult GetGenerationRun([Description("Exact runId returned by ApplyGenerationPlan.")] string runId)
            => Execute("GetGenerationRun", () => ExecutionSession.ReadRun(runId));

        private CallToolResult Execute(string name, Func<CallToolResult> operation)
        {
            try { return operation(); }
            catch (GenerationCallRejection ex) { return McpServer.V4Reject(name, ex.Error, ex.Evidence); }
            catch (GenerationValidationException ex) { return McpServer.V4Reject(name, new Error(ex.Message, new InvalidArgumentDetails(ex.Errors.First().Path, Array.Empty<string>()))); }
            catch (ArgumentException ex) { return McpServer.V4Reject(name, new Error(ex.Message, new InvalidArgumentDetails(ex.ParamName ?? "arguments", Array.Empty<string>()))); }
            catch (GenerationPersistenceUnknown ex)
            { return McpServer.V4Result(name, new JsonObject { ["runId"] = ex.RunId, ["rollbackPerformed"] = false },
                new Error(ex.Message, new OutcomeUnknownDetails("generation-record", new Dictionary<string, JsonElement>())),
                Outcome.Unknown, Execution.Unknown, Completeness.Unknown, current: true); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { return McpServer.V4Reject(name, new Error("Generation record/staging I/O failed; inspect retained evidence before replanning.", new IoFailedDetails("generation", null))); }
        }
    }

    internal sealed class GenerationCallRejection : Exception
    {
        internal Error Error { get; }
        internal JsonObject? Evidence { get; }
        internal GenerationCallRejection(Error error, JsonObject? evidence = null) : base(error.Message) { Error = error; Evidence = evidence; }
    }

    internal sealed class GenerationPersistenceUnknown : IOException
    {
        internal string RunId { get; }
        internal GenerationPersistenceUnknown(string runId, Exception inner)
            : base("Generation evidence could not be persisted after dispatch; inspect run " + runId + ", reset the session and replan. Never replay.", inner) { RunId = runId; }
    }

    internal sealed class GenerationExecutionSession
    {
        private sealed record Stored(GenerationPlanningResult Result, string Binding, string Project, string Release, DateTimeOffset Expires);
        private readonly Dictionary<string, Stored> plans = new(StringComparer.Ordinal);
        private readonly object sync = new();
        private readonly Func<string> root;
        private readonly Func<string, JsonObject, Func<CallToolResult?>?, CallToolResult> call;
        private readonly Func<string, ToolDescriptor?> descriptor;
        private readonly Func<PendingApproval, ApprovalSettings, System.Threading.CancellationToken, System.Threading.Tasks.Task<ApprovalOutcome>> wait;
        internal GenerationExecutionSession(Func<string>? root = null,
            Func<string, JsonObject, Func<CallToolResult?>?, CallToolResult>? call = null,
            Func<string, ToolDescriptor?>? descriptor = null,
            Func<PendingApproval, ApprovalSettings, System.Threading.CancellationToken, System.Threading.Tasks.Task<ApprovalOutcome>>? wait = null)
        {
            this.root = root ?? (() => Path.Combine(DataLocations.Current.Root ?? throw new IOException("Data directory unavailable."), "generation"));
            this.call = call ?? ((name, args, check) => McpServer.DispatchGenerationStep(name, new ToolArguments(JsonSerializer.SerializeToElement(args)), check ?? (() => null)));
            this.descriptor = descriptor ?? (name => McpServer.CatalogView.Find(name));
            this.wait = wait ?? ((pending, settings, token) => McpServer.ApprovalWaitOverrideForTests?.Invoke(pending, settings, token) ?? ApprovalClient.Wait(pending, settings, token));
        }
        private GenerationRunStore Records => new(Path.Combine(root(), "runs"));
        private JsonObject Read(string name, JsonObject? arguments = null, bool requireComplete = true)
        {
            var result = call(name, arguments ?? new JsonObject(), null);
            var body = Body(result);
            if ((bool?)body?["ok"] != true || requireComplete && (string?)body?["meta"]?["completeness"] != "complete"
                || body?["meta"]?["paging"] is JsonObject paging && (bool?)paging["complete"] == false)
                throw new GenerationCallRejection(new Error("Complete project readback is required from " + name + ".", new PreconditionFailedDetails("generation-readback", name)),
                    new JsonObject { ["readTool"] = name, ["result"] = body?.DeepClone() });
            return body!["data"]!.AsObject();
        }
        private (string Binding, string Project) Target()
        {
            Read("GetSessionState", requireComplete: false);
            var data = Read("GetOpennessWorkerStatus");
            var identity = data["worker"]?["binding"]?["identity"] as JsonObject;
            string? project = (string?)identity?["projectPath"];
            if (identity?["processId"] == null || string.IsNullOrEmpty(project))
                throw new GenerationCallRejection(new Error(SessionBehavior.PortalRequired, new PreconditionFailedDetails("portal-connected", "ConnectPortal")));
            if (identity?["processStartUtc"] == null || identity?["generation"] == null)
                throw new GenerationCallRejection(new Error("Verified process and project binding identity are required.", new PreconditionFailedDetails("generation-identity", project)));
            return (PendingApproval.Canonical(identity), project!);
        }
        internal CallToolResult Plan(StandardPackage package, string machineJson)
        {
            var machine = package.ValidateMachine(machineJson);
            string release = McpServer.ReleaseKey;
            if (machine.Target.Release != release) throw new ArgumentException("Machine release must match the session release.", "machine.target.release");
            var before = Target();
            if (machine.Target.Project.Mode != "existing" || machine.Target.Project.ProjectIdentity != before.Project)
                throw new GenerationCallRejection(new Error("Bind the intended existing project first and supply its exact project file.", new PreconditionFailedDetails("generation-project", before.Project)));
            var observed = Observe(machine, before.Project);
            var options = new GenerationPlanningOptions { ArtifactRoot = Path.Combine(root(), "artifacts") };
            foreach (var station in machine.Stations) options.SoftwarePaths[station.Id] = machine.Target.Project.SoftwarePath ?? station.Id;
            var result = GenerationPlanner.Build(package, machineJson, observed, options);
            PrepareArguments(result.Plan, before.Project);
            result.Plan.PlanId = "gen-" + Digest(CanonicalJson.Hash(JsonSerializer.SerializeToElement(new { plannerId = result.Plan.PlanId, binding = before.Binding })));
            if (Target() != before) throw new GenerationCallRejection(new Error("Project binding changed during readback.", new PlanStaleDetails(Digest(result.Plan.PlanHash), "identity")));
            result.Plan.PlanHash = GenerationDocuments.PlanHash(GenerationDocuments.Canonical(result.Plan));
            result.StageArtifacts();
            foreach (var backup in result.Plan.Steps.Where(s => s.Tool == "ArchiveSavedProject"))
                Directory.CreateDirectory(Path.GetDirectoryName(backup.Arguments["archivePath"].GetString()!)!);
            var pending = Pending(result.Plan, result.Plan.Steps, before.Binding, "preview", new ApprovalSettings(true, 120));
            if (JsonSerializer.SerializeToUtf8Bytes(pending).Length > ApprovalFrames.MaximumBytes)
                throw new ArgumentException("Plan exceeds the Workbench approval frame budget; split the machine.", "machine");
            lock (sync)
            {
                foreach (var id in plans.Where(p => p.Value.Expires < DateTimeOffset.UtcNow).Select(p => p.Key).ToArray()) plans.Remove(id);
                if (plans.Count >= 32 && !plans.ContainsKey(result.Plan.PlanId)) throw new ArgumentException("Session plan limit reached; begin a new session.", "machine");
                plans[result.Plan.PlanId] = new Stored(result, before.Binding, before.Project, release, DateTimeOffset.UtcNow.AddMinutes(10));
            }
            return McpServer.V4Result("PlanProjectGeneration", new JsonObject { ["plan"] = JsonNode.Parse(result.CanonicalPlan), ["expectedProject"] = before.Project,
                ["expiresUtc"] = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O") });
        }
        private void PrepareArguments(GenerationPlan plan, string project)
        {
            var unavailable = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            void Unavailable(string phase, string reason)
            {
                if (!unavailable.TryGetValue(phase, out var reasons)) unavailable[phase] = reasons = new SortedSet<string>(StringComparer.Ordinal);
                reasons.Add(reason);
            }
            foreach (var step in plan.Steps)
            {
                var tool = descriptor(step.Tool);
                if (tool == null) { step.Availability.Tool = "absent"; Unavailable(step.Phase, "Tool " + step.Tool + " is not registered on this release."); continue; }
                step.Availability.Tool = "present";
                var schema = tool.Tool.InputSchema.GetProperty("properties");
                if (schema.TryGetProperty("expectedProjectFile", out _)) step.Arguments["expectedProjectFile"] = JsonSerializer.SerializeToElement(project);
                if (schema.TryGetProperty("confirm", out _)) step.Arguments["confirm"] = JsonSerializer.SerializeToElement(true);
                if (schema.TryGetProperty("plc", out _) && step.Arguments.TryGetValue("softwarePath", out var plc))
                { step.Arguments.Remove("softwarePath"); step.Arguments["plc"] = plc; }
                if (schema.TryGetProperty("group", out _) && step.Arguments.TryGetValue("groupPath", out var group))
                { step.Arguments.Remove("groupPath"); step.Arguments["group"] = group; }
                if (schema.TryGetProperty("mode", out _)) { step.Arguments.Remove("dryRun"); step.Arguments["mode"] = JsonSerializer.SerializeToElement("apply"); }
                if (schema.TryGetProperty("expectedPlanHash", out _) && !step.Arguments.ContainsKey("expectedPlanHash"))
                    Unavailable(step.Phase, "Tool " + step.Tool + " requires a tool-specific preview hash that cannot be inferred from read tools. Complete its reviewed prerequisite separately, then replan.");
                var error = new InputSchema(tool.Tool.InputSchema).Validate(JsonSerializer.SerializeToElement(step.Arguments), "arguments");
                if (error != null) Unavailable(step.Phase, "Tool " + step.Tool + " cannot bind the planner arguments: " + error.Message);
                step.ArgumentDigest = CanonicalJson.Hash(JsonSerializer.SerializeToElement(step.Arguments));
            }
            bool changed;
            do
            {
                changed = false;
                foreach (var step in plan.Steps.Where(s => !unavailable.ContainsKey(s.Phase)))
                    if (step.DependsOn.Any(id => plan.Steps.Any(s => s.Id == id && unavailable.ContainsKey(s.Phase))))
                    { Unavailable(step.Phase, "A required phase is unavailable; replan after its prerequisites are complete."); changed = true; }
            } while (changed);
            foreach (var pair in unavailable) plan.Unavailable.Add(new GenerationPlanUnavailableItem { Phase = pair.Key, Reason = string.Join(" ", pair.Value) });
            plan.Steps.RemoveAll(s => unavailable.ContainsKey(s.Phase));
            // Final actions must not create the impression of compiling/saving an otherwise empty generation.
            if (plan.Steps.All(s => s.Phase == "compile" || s.Phase == "save" || s.Key == "project/backup")) plan.Steps.Clear();
        }
        private ProjectModel Observe(MachineDescription machine, string identity)
        {
            var observed = new ProjectModel { ProjectIdentity = identity };
            var tree = Read("GetProjectTree");
            foreach (var station in machine.Stations)
            {
                var nodes = Walk(tree).Where(n => (string?)n["name"] == station.Id).ToArray();
                foreach (var node in nodes.Where(n => (string?)n["kind"] == "device" || n["deviceItems"] != null))
                    observed.Devices.Add(new ProjectDevice { Station = station.Id, Name = station.Id,
                        Article = (string?)node["articleNumber"] ?? "", Firmware = (string?)node["version"] ?? "" });
                // The Foundation tree is text. Presence prevents duplicate creation, while
                // unverified hardware attributes remain a conflict rather than a guessed match.
                string? textTree = (string?)tree["tree"] ?? (string?)tree["value"];
                if (nodes.Length == 0 && textTree != null && textTree.Split('\n').Any(line => line.Trim().TrimEnd('\r') == "Device: " + station.Id))
                    observed.Devices.Add(new ProjectDevice { Station = station.Id, Name = station.Id });
                if (!machine.Devices.Any(d => d.Station == station.Id)) continue;
                string software = machine.Target.Project.SoftwarePath ?? station.Id;
                var softwareTree = Read("GetSoftwareTree", new JsonObject { ["softwarePath"] = software });
                var paths = (softwareTree["paths"] ?? softwareTree["meta"]?["paths"]) as JsonArray;
                if (paths == null) throw new GenerationCallRejection(new Error("Software readback has no complete canonical path inventory.", new PreconditionFailedDetails("generation-readback", software)));
                foreach (var row in paths.OfType<JsonObject>())
                {
                    string name = (string)row["name"]!;
                    string path = (string)row["objectPath"]!;
                    string group = path.Contains('/') ? path.Substring(0, path.LastIndexOf('/')) : "";
                    switch ((string?)row["kind"])
                    {
                        case "block": observed.Blocks.Add(new ProjectBlock { Station = station.Id, Name = name, Group = group }); break;
                        case "type": observed.Types.Add(new ProjectType { Station = station.Id, Name = name, Group = group }); break;
                        case "block-group": if (path.Length != 0) observed.Groups.Add(new ProjectGroup { Station = station.Id, Name = path, Kind = "blocks" }); break;
                        case "type-group": if (path.Length != 0) observed.Groups.Add(new ProjectGroup { Station = station.Id, Name = path, Kind = "types" }); break;
                        default: throw new ArgumentException("Unknown software tree row; complete readback required.", "machine");
                    }
                }
                foreach (var item in Items(Read("ListPlcExternalSources", new JsonObject { ["softwarePath"] = software })))
                    observed.ExternalSources.Add(new ProjectExternalSource { Station = station.Id, Name = Name(item) });
                foreach (var item in Items(Read("ListPlcTagTables", new JsonObject { ["softwarePath"] = software })))
                {
                    string table = Name(item); observed.TagTables.Add(new ProjectTagTable { Station = station.Id, Name = table });
                    foreach (var tag in Items(Read("ListPlcTags", new JsonObject { ["plc"] = software, ["table"] = table })))
                        observed.Tags.Add(new ProjectTag { Station = station.Id, Table = table, Name = Name(tag), DataType = (string?)tag?["dataType"] ?? throw new ArgumentException("Missing tag type."), Address = (string?)tag?["value"] ?? throw new ArgumentException("Missing tag address.") });
                }
            }
            return observed;
        }
        private static IEnumerable<JsonObject> Walk(JsonNode? node)
        {
            if (node is JsonObject obj) { yield return obj; foreach (var pair in obj) foreach (var child in Walk(pair.Value)) yield return child; }
            else if (node is JsonArray array) foreach (var item in array) foreach (var child in Walk(item)) yield return child;
        }
        private static JsonArray Items(JsonObject data) => data["items"] as JsonArray ?? throw new ArgumentException("Complete item inventory is missing.");
        private static string Name(JsonNode? item) => item is JsonValue value ? value.GetValue<string>() : (string?)item?["name"] ?? throw new ArgumentException("Inventory name is missing.");
        private static JsonObject? Body(CallToolResult result) => (result.StructuredContent ?? JsonNode.Parse((result.Content.FirstOrDefault() as TextContentBlock)?.Text ?? "null")) as JsonObject;
        internal CallToolResult ReadRun(string runId) => McpServer.V4Result("GetGenerationRun", Records.Read(runId));
        internal CallToolResult Apply(string planId, string hash, string expectedProject, string[]? phases, bool preview)
        {
            Stored stored;
            lock (sync) if (!plans.TryGetValue(planId, out stored!)) throw new GenerationCallRejection(new Error("Plan is not in this MCP session; replan.", new PreconditionFailedDetails("generation-plan-session", planId)));
            var plan = stored.Result.Plan;
            if (stored.Release != McpServer.ReleaseKey || stored.Expires < DateTimeOffset.UtcNow || hash != plan.PlanHash
                || GenerationDocuments.PlanHash(stored.Result.CanonicalPlan) != hash || expectedProject != stored.Project)
                throw new GenerationCallRejection(new Error("Generation plan/hash/target is stale; replan.", new PlanStaleDetails(Digest(plan.PlanHash), "plan-hash")));
            var selected = phases == null || phases.Length == 0 ? plan.Phases.Except(plan.Unavailable.Select(p => p.Phase)).ToHashSet(StringComparer.Ordinal) : phases.ToHashSet(StringComparer.Ordinal);
            if (selected.Any(p => !plan.Phases.Contains(p) || plan.Unavailable.Any(u => u.Phase == p))) throw new ArgumentException("Selected phase is unknown or unavailable.", "phases");
            var steps = plan.Steps.Where(s => selected.Contains(s.Phase)).ToArray();
            if (steps.Any(s => s.DependsOn.Any(id => !steps.Any(d => d.Id == id)))) throw new ArgumentException("Select every dependency phase.", "phases");
            Check(stored);
            VerifyArtifacts(stored);
            if (preview || steps.Length == 0) return McpServer.V4Result("ApplyGenerationPlan", new JsonObject { ["dryRun"] = true, ["plan"] = JsonNode.Parse(stored.Result.CanonicalPlan),
                ["selectedSteps"] = JsonSerializer.SerializeToNode(steps.Select(s => s.Id)), ["wouldChange"] = steps.Length > 0 });
            string parentId = AuditInvocation.CurrentRequestId ?? Guid.NewGuid().ToString("N");
            var settings = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
            var pending = Pending(plan, steps, stored.Binding, parentId, settings);
            pending.Validate();
            AuditInvocation.RecordCurrentRequest(plan.PlanHash);
            var records = Records;
            string runId = records.Begin(new JsonObject { ["planId"] = planId, ["planHash"] = hash, ["release"] = stored.Release, ["parentRequestId"] = parentId,
                ["project"] = stored.Project, ["steps"] = JsonSerializer.SerializeToNode(steps.Select(s => new { s.Id, s.Phase, s.Key, s.Tool, s.ArgumentDigest }), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
                ["plan"] = JsonNode.Parse(stored.Result.CanonicalPlan) });
            ApprovalOutcome? approval = null;
            int succeeded = 0; bool dispatched = false; string status = "rejected-before-operation", stopping = "approval"; JsonObject? last = null;
            try
            {
                approval = wait(pending, settings, McpServer.WorkerDispatchCancellation).GetAwaiter().GetResult();
                records.Append(runId, new JsonObject { ["event"] = "approval", ["disabled"] = approval.Disabled, ["reason"] = approval.Reason });
                if (approval.Reason != null) throw new GenerationCallRejection(HostBehavior.Confirmation(approval.Reason, Digest(hash), parentId));
                Check(stored); VerifyArtifacts(stored);
                using var claim = records.Claim(planId);
                AuditInvocation.StartCurrent();
                foreach (var step in steps)
                {
                    Check(stored);
                    if (descriptor(step.Tool) == null || step.Availability.Tool != "present") throw new GenerationCallRejection(new Error("Step is unavailable on this release.", new UnsupportedCapabilityDetails(stored.Release, step.Tool, null)));
                    if (CanonicalJson.Hash(JsonSerializer.SerializeToElement(step.Arguments)) != step.ArgumentDigest) throw new GenerationCallRejection(new Error("Approved step arguments changed.", new PlanStaleDetails(Digest(hash), "argument-digest")));
                    string requestId = Guid.NewGuid().ToString("N");
                    using var correlation = InvocationJournal.UseCorrelation(requestId);
                    using var association = AuditInvocation.GenerationStep(planId, step.Id, runId, parentId, hash, requestId);
                    using var granted = GenerationStepApproval.Enter(approval, step.Tool, JsonSerializer.Serialize(step.Arguments));
                    records.Append(runId, new JsonObject { ["event"] = "step-start", ["stepId"] = step.Id, ["requestId"] = requestId });
                    try
                    {
                        dispatched = true;
                        var result = call(step.Tool, JsonSerializer.SerializeToNode(step.Arguments)!.AsObject(), () => {
                            try { Check(stored); VerifyArtifacts(stored); return null; }
                            catch (GenerationCallRejection ex) { return McpServer.V4Reject(step.Tool, ex.Error); }
                        });
                        last = Body(result);
                        string outcome = (string?)last?["meta"]?["outcome"] ?? "unknown";
                        if (outcome == "succeeded" && (bool?)last?["ok"] != true) outcome = "unknown";
                        records.Append(runId, new JsonObject { ["event"] = "step-end", ["stepId"] = step.Id, ["requestId"] = requestId, ["status"] = outcome, ["result"] = last?.DeepClone() });
                        if (outcome != "succeeded")
                        {
                            if (outcome == "unknown") McpServer.MarkSharedSessionUncertain();
                            status = outcome; stopping = step.Id; break;
                        }
                        succeeded++;
                    }
                    catch (Exception ex)
                    {
                        status = "unknown"; stopping = step.Id;
                        McpServer.MarkSharedSessionUncertain();
                        records.Append(runId, new JsonObject { ["event"] = "step-end", ["stepId"] = step.Id, ["requestId"] = requestId, ["status"] = "unknown",
                            ["error"] = ex.GetType().Name + ": " + ex.Message });
                        break;
                    }
                }
                if (succeeded == steps.Length) { status = "succeeded"; stopping = "complete"; }
            }
            catch (GenerationCallRejection ex) { last = new JsonObject { ["error"] = JsonSerializer.SerializeToNode(ex.Error) }; stopping = ex.Error.Message; }
            catch (ArgumentException ex) { stopping = ex.Message; }
            catch (OperationCanceledException ex) { stopping = "cancelled-before-dispatch: " + ex.Message; }
            finally
            {
                try { records.Append(runId, new JsonObject { ["event"] = "end", ["status"] = status, ["stoppingReason"] = stopping, ["error"] = last?["error"]?.DeepClone() }); }
                catch (Exception ex) when (dispatched && (ex is IOException || ex is UnauthorizedAccessException))
                { McpServer.MarkSharedSessionUncertain(); throw new GenerationPersistenceUnknown(runId, ex); }
                finally { if (approval != null) ApprovalClient.Complete(approval, status).GetAwaiter().GetResult(); }
            }
            JsonObject data;
            try { data = records.Read(runId); }
            catch (Exception ex) when (dispatched && (ex is IOException || ex is UnauthorizedAccessException))
            { McpServer.MarkSharedSessionUncertain(); throw new GenerationPersistenceUnknown(runId, ex); }
            data["rollbackPerformed"] = false;
            if (status == "succeeded") return McpServer.V4Result("ApplyGenerationPlan", data, completed: true, current: true);
            if (status == "unknown") return McpServer.V4Result("ApplyGenerationPlan", data, new Error("Generation outcome unknown; inspect the project, reset the session and replan. Never replay this run.", new OutcomeUnknownDetails("generation", new Dictionary<string, JsonElement>())), Outcome.Unknown, Execution.Unknown, Completeness.Unknown, current: true);
            if (succeeded > 0) return McpServer.V4Result("ApplyGenerationPlan", data, new Error("Generation stopped; completed writes remain. Replan after inspecting the evidence.", new PartialFailureDetails(succeeded, 1, steps.Length - succeeded - 1)), Outcome.Partial, Execution.Partial, Completeness.Partial, current: true);
            if (status == "failed") return McpServer.V4Result("ApplyGenerationPlan", data,
                new Error("The first generation step failed; inspect its retained evidence before replanning.", new PreconditionFailedDetails("generation-step", stopping)),
                Outcome.Failed, Execution.Completed, Completeness.None, current: true);
            return McpServer.V4Reject("ApplyGenerationPlan", approval?.Reason != null ? HostBehavior.Confirmation(approval.Reason, Digest(hash), parentId)
                : new Error("Generation stopped before any successful step: " + stopping, new PreconditionFailedDetails("generation", stored.Project)), data);
        }
        private void Check(Stored stored)
        {
            if (stored.Expires < DateTimeOffset.UtcNow) throw new GenerationCallRejection(new Error("Generation plan expired; replan.", new PlanStaleDetails(Digest(stored.Result.Plan.PlanHash), "expired")));
            if (Target() != (stored.Binding, stored.Project)) throw new GenerationCallRejection(new Error("Project/session identity changed; replan.", new PlanStaleDetails(Digest(stored.Result.Plan.PlanHash), "identity")));
        }
        private void VerifyArtifacts(Stored stored)
        {
            foreach (var artifact in stored.Result.Plan.Artifacts)
            {
                string path = Path.Combine(root(), "artifacts", artifact.Path.Replace('/', Path.DirectorySeparatorChar));
                for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory != null; directory = directory.Parent)
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Artifact links refused.");
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || CanonicalJson.HashBytes(File.ReadAllBytes(path)) != artifact.Sha256)
                    throw new GenerationCallRejection(new Error("Staged artifact changed; replan.", new PlanStaleDetails(Digest(stored.Result.Plan.PlanHash), "artifact-hash")));
            }
        }
        private static PendingApproval Pending(GenerationPlan plan, IEnumerable<GenerationPlanStepsItem> steps, string binding, string id, ApprovalSettings settings)
        {
            var selected = steps.ToArray();
            var summary = new JsonObject { ["planId"] = plan.PlanId, ["expectedPlanHash"] = plan.PlanHash,
                ["phaseCounts"] = JsonSerializer.SerializeToNode(selected.GroupBy(s => s.Phase).ToDictionary(g => g.Key, g => g.Count())),
                ["conflicts"] = JsonSerializer.SerializeToNode(plan.Conflicts), ["skipped"] = JsonSerializer.SerializeToNode(plan.Skipped),
                ["unavailable"] = JsonSerializer.SerializeToNode(plan.Unavailable), ["artifacts"] = JsonSerializer.SerializeToNode(plan.Artifacts) };
            var pending = PendingApproval.Create("foundation", plan.Target.Release, "ApplyGenerationPlan", summary.ToJsonString(), binding, settings.TimeoutSeconds, id);
            pending.Kind = "plan"; pending.PlanHash = plan.PlanHash; pending.PlanJson = GenerationDocuments.Canonical(plan);
            pending.Operations = selected.Select(s => new ApprovalAction { Tool = s.Tool, Action = s.Phase + "/" + s.Op, Target = s.Key }).ToArray();
            return pending;
        }
        private static string Digest(string hash) => hash.StartsWith("sha256:", StringComparison.Ordinal) ? hash.Substring(7) : hash;
    }
}
