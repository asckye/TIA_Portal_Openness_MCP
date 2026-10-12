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
        private GenerationExecutionSession ExecutionSession => executionSession ?? TiaMcp.FoundationHost.EngineHostConfiguration.Current.Generation;

        [McpServerTool(Name = "PlanProjectGeneration"), Description("[L1][Project][READ] Expand an exact standard package and structured machine against the bound project's complete READ inventory, or plan a new project in a connected unbound portal. Session release must match. Returns deterministic tiamcp.plan/1 with deciding arguments, observed inventories, approved preview projections, forbidden objects, effect declarations and artifact hashes. Only expectedPlanHash is filled later from each tool's own matched preview. External sources use the root; generated blocks move to declared groups. No native writes. Unprovable readback conflicts; unavailable phases are skipped. Plans belong to this MCP session and binding; inspect before apply.")]
        public CallToolResult PlanProjectGeneration(
            [Description("Exact installed package id.")] string packageId,
            [Description("Exact installed SemVer version.")] string version,
            [Description("Structured tiamcp.machine/1; existing mode supplies the bound project file, new mode supplies an absolute directory and project name.")] ToolArguments machine,
            [Description("Exact existing unknown-outcome object keys personally verified for adoption; never permits overwriting.")] string[]? adopt = null)
            => Execute("PlanProjectGeneration", () => ExecutionSession.Plan(store().Effective(store().Load(packageId, version)), machine.Json.GetRawText(), adopt));

        [McpServerTool(Name = "ApplyGenerationPlan"), Description("[L1][Project][WRITE] Preview a stored generation plan by default. Execute with dryRun=false, exact fresh expectedPlanHash and expectedProject. One Workbench plan approval covers selected steps. Active runs fence all other session writes, including attached panel writes. Cancellation stops between steps; earlier writes remain. New projects rebind within the run only after verified process, path, epoch and empty-tree readback. Source steps use their own preview, compare approved deciding fields, inventories and witness constraints, fill only expectedPlanHash, then use existing admission/precheck/hash checks/audit/worker lane and verify effects. Stop on refusal, failure, unknown or divergence; retain completed writes and run facts, never retry. Replan to approve changes. Freshness is checked before approval and the first write; approved runs have 4-hour/10000-step caps. Optional phases must include dependencies.")]
        public CallToolResult ApplyGenerationPlan(
            [Description("planId returned in this MCP session.")] string planId,
            [Description("Exact planHash from planning; required even for preview.")] string expectedPlanHash,
            [Description("Exact existing or predicted new project file recorded by the plan.")] string expectedProject,
            [Description("Optional phase names; dependencies must be selected too.")] string[]? phases = null,
            [Description("true previews without approval/write audit; false executes the reviewed plan.")] bool dryRun = true)
            => Execute("ApplyGenerationPlan", () => ExecutionSession.Apply(planId, expectedPlanHash, expectedProject, phases, dryRun));

        [McpServerTool(Name = "GetGenerationRun"), Description("[L1][Project][READ] Read an append-only tiamcp.run/1 generation record under the host data folder, including steps, outcomes, evidence, audit requestIds and stopping reason. Survives host restarts; an interrupted step is unknown and never replayed. This reads local records only, without a bound project.")]
        public CallToolResult GetGenerationRun([Description("Exact runId returned by ApplyGenerationPlan.")] string runId)
            => Execute("GetGenerationRun", () => ExecutionSession.ReadRun(runId));

        private CallToolResult Execute(string name, Func<CallToolResult> operation)
        {
            try { return operation(); }
            catch (GenerationCallRejectionException ex) { return McpServer.V4Reject(name, ex.Error, ex.Evidence); }
            catch (GenerationValidationException ex) { return McpServer.V4Reject(name, new Error(ex.Message, new InvalidArgumentDetails(ex.Errors.First().Path, Array.Empty<string>()))); }
            catch (ArgumentException ex) { return McpServer.V4Reject(name, new Error(ex.Message, new InvalidArgumentDetails(ex.ParamName ?? "arguments", Array.Empty<string>()))); }
            catch (GenerationPersistenceUnknownException ex)
            { return McpServer.V4Result(name, new JsonObject { ["runId"] = ex.RunId, ["rollbackPerformed"] = false },
                new Error(ex.Message, new OutcomeUnknownDetails("generation-record", new Dictionary<string, JsonElement>())),
                Outcome.Unknown, Execution.Unknown, Completeness.Unknown, current: true); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { return McpServer.V4Reject(name, new Error("Generation record/staging I/O failed; inspect retained evidence before replanning.", new IoFailedDetails("generation", null))); }
        }
    }

    internal sealed class GenerationCallRejectionException : Exception
    {
        internal Error Error { get; }
        internal JsonObject? Evidence { get; }
        internal GenerationCallRejectionException(Error error, JsonObject? evidence = null) : base(error.Message) { Error = error; Evidence = evidence; }
    }

    internal sealed class GenerationPersistenceUnknownException : IOException
    {
        internal string RunId { get; }
        internal GenerationPersistenceUnknownException(string runId, Exception inner)
            : base("Generation evidence could not be persisted after dispatch; inspect run " + runId + ", reset the session and replan. Never replay.", inner) { RunId = runId; }
    }

    internal sealed partial class GenerationExecutionSession
    {
        private sealed record Stored(GenerationPlanningResult Result, string Binding, string Project, string Release, DateTimeOffset Expires, string BoundProject, Dictionary<string, string> SoftwarePaths);
        private readonly Dictionary<string, Stored> plans = new(StringComparer.Ordinal);
        private readonly object sync = new();
        private readonly Func<string> root;
        private readonly Func<string, JsonObject, Func<CallToolResult?>?, CallToolResult> call;
        private readonly Func<string, ToolDescriptor?> descriptor;
        private readonly Func<PendingApproval, ApprovalSettings, System.Threading.CancellationToken, System.Threading.Tasks.Task<ApprovalOutcome>> wait;
        private readonly Func<DateTimeOffset> now;
        private readonly Func<bool> cancelled;
        private readonly TimeSpan maximumDuration;
        private readonly int maximumSteps;
        internal GenerationExecutionSession(Func<string>? root = null,
            Func<string, JsonObject, Func<CallToolResult?>?, CallToolResult>? call = null,
            Func<string, ToolDescriptor?>? descriptor = null,
            Func<PendingApproval, ApprovalSettings, System.Threading.CancellationToken, System.Threading.Tasks.Task<ApprovalOutcome>>? wait = null,
            Func<DateTimeOffset>? now = null, Func<bool>? cancelled = null, TimeSpan? maximumDuration = null, int maximumSteps = 10000)
        {
            this.root = root ?? (() => Path.Combine(DataLocations.Current.Root ?? throw new IOException("Data directory unavailable."), "generation"));
            this.call = call ?? ((name, args, check) => McpServer.DispatchGenerationStep(name, new ToolArguments(JsonSerializer.SerializeToElement(args)), check ?? (() => null)));
            this.descriptor = descriptor ?? (name => McpServer.CatalogView.Find(name));
            this.wait = wait ?? ((pending, settings, token) => McpServer.ApprovalWaitOverrideForTests?.Invoke(pending, settings, token) ?? ApprovalClient.Wait(pending, settings, token));
            this.now = now ?? (() => DateTimeOffset.UtcNow);
            this.cancelled = cancelled ?? (() => false);
            this.maximumDuration = maximumDuration ?? TimeSpan.FromHours(4);
            if (this.maximumDuration <= TimeSpan.Zero || maximumSteps < 1 || maximumSteps > 10000) throw new ArgumentException("Invalid generation run caps.");
            this.maximumSteps = maximumSteps;
        }
        private GenerationRunStore Records => new(Path.Combine(root(), "runs"));
        private JsonObject Read(string name, JsonObject? arguments = null, bool requireComplete = true)
        {
            var result = call(name, arguments ?? new JsonObject(), null);
            var body = Body(result);
            if ((bool?)body?["ok"] != true || requireComplete && (string?)body?["meta"]?["completeness"] != "complete"
                || body?["meta"]?["paging"] is JsonObject paging && (bool?)paging["complete"] == false)
                throw new GenerationCallRejectionException(new Error("Complete project readback is required from " + name + ".", new PreconditionFailedDetails("generation-readback", name)),
                    new JsonObject { ["readTool"] = name, ["result"] = body?.DeepClone() });
            return body!["data"]!.AsObject();
        }
        private (string Binding, string Project) Target(bool allowUnbound = false)
        {
            Read("GetSessionState", requireComplete: false);
            var data = Read("GetOpennessWorkerStatus");
            var identity = data["worker"]?["binding"]?["identity"] as JsonObject;
            string? project = (string?)identity?["projectPath"];
            if (identity?["processId"] == null || !allowUnbound && string.IsNullOrEmpty(project))
                throw new GenerationCallRejectionException(new Error(SessionBehavior.PortalRequired, new PreconditionFailedDetails("portal-connected", "ConnectPortal")));
            if (identity?["processStartUtc"] == null || identity?["generation"] == null)
                throw new GenerationCallRejectionException(new Error("Verified process and project binding identity are required.", new PreconditionFailedDetails("generation-identity", project)));
            return (PendingApproval.Canonical(identity), project ?? "");
        }
        internal CallToolResult Plan(StandardPackage package, string machineJson, string[]? adopt = null)
        {
            var machine = package.ValidateMachine(machineJson);
            new GenerationArtifacts(root()).ExpireUnreferenced(now());
            string release = McpServer.ReleaseKey;
            if (machine.Target.Release != release) throw new ArgumentException("Machine release must match the session release.", "machine.target.release");
            bool newProject = machine.Target.Project.Mode == "new";
            var before = Target(allowUnbound: newProject);
            if (newProject ? before.Project.Length != 0 : machine.Target.Project.ProjectIdentity != before.Project)
                throw new GenerationCallRejectionException(new Error(newProject ? "New-project planning requires a connected portal without a bound project." : "Bind the intended existing project first and supply its exact project file.", new PreconditionFailedDetails("generation-project", before.Project)));
            string project = newProject ? GenerationProjectPath.CreationFile(release, machine.Target.Project.Directory!, machine.Target.Project.Name!) : before.Project;
            var options = new GenerationPlanningOptions { ArtifactRoot = Path.Combine(root(), "artifacts"), Lineage = Records.TryGetLineage(project, release, out var lineage) ? lineage : null,
                RecordPath = GenerationRecordFile.DefaultPath(project), Adopt = (adopt ?? Array.Empty<string>()).ToList() };
            foreach (var station in machine.Stations) options.SoftwarePaths[station.Id] = machine.Target.Project.SoftwarePath ?? station.Id;
            var observed = newProject ? new ProjectModel { ProjectIdentity = project } : Observe(machine, before.Project, options);
            LoadOwnership(project, release, observed, options);
            var result = GenerationPlanner.Build(package, machineJson, observed, options);
            result.Plan.PlanId = "gen-" + Digest(CanonicalJson.Hash(JsonSerializer.SerializeToElement(new { plannerId = result.Plan.PlanId, binding = before.Binding })));
            foreach (var record in result.Plan.Steps.Where(s => s.Tool == "PublishGenerationRecord")) record.Arguments["planId"] = JsonSerializer.SerializeToElement(result.Plan.PlanId);
            PrepareArguments(result.Plan, project);
            if (Target(allowUnbound: newProject) != before) throw new GenerationCallRejectionException(new Error("Project binding changed during readback.", new PlanStaleDetails(Digest(result.Plan.PlanHash), "identity")));
            result.Plan.PlanHash = GenerationDocuments.PlanHash(GenerationDocuments.Canonical(result.Plan));
            result.StageArtifacts();
            foreach (var backup in result.Plan.Steps.Where(s => s.Tool == "ArchiveSavedProject"))
                Directory.CreateDirectory(Path.GetDirectoryName(backup.Arguments["archivePath"].GetString()!)!);
            var pending = Pending(result.Plan, result.Plan.Steps, before.Binding, "preview", new ApprovalSettings(true, 120));
            if (JsonSerializer.SerializeToUtf8Bytes(pending).Length > ApprovalFrames.MaximumBytes)
                throw new ArgumentException("Plan exceeds the Workbench approval frame budget; split the machine.", "machine");
            lock (sync)
            {
                foreach (var id in plans.Where(p => p.Value.Expires < now()).Select(p => p.Key).ToArray()) plans.Remove(id);
                if (plans.Count >= 32 && !plans.ContainsKey(result.Plan.PlanId)) throw new ArgumentException("Session plan limit reached; begin a new session.", "machine");
                plans[result.Plan.PlanId] = new Stored(result, before.Binding, project, release, now().AddMinutes(10), before.Project, options.SoftwarePaths);
            }
            return McpServer.V4Result("PlanProjectGeneration", new JsonObject { ["plan"] = JsonNode.Parse(result.CanonicalPlan), ["expectedProject"] = project,
                ["expiresUtc"] = now().AddMinutes(10).ToString("O") });
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
                    if (!StepReviewRules.Supports(step) || schema.TryGetProperty("mode", out _))
                        Unavailable(step.Phase, "Tool " + step.Tool + " has no reviewed current-policy preview rule; replan after its prerequisites are complete.");
                var validationArguments = JsonSerializer.SerializeToNode(step.Arguments)!.AsObject();
                if (StepReviewRules.Supports(step)) validationArguments["expectedPlanHash"] = new string('0', 64);
                var error = new InputSchema(tool.Tool.InputSchema).Validate(JsonSerializer.SerializeToElement(validationArguments), "arguments");
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
            if (plan.Adopt.Count == 0 && plan.Steps.All(s => s.Phase == "compile" || s.Phase == "save" || s.Phase == "record" || s.Key == "project/backup")) plan.Steps.Clear();
        }
        private ProjectModel Observe(MachineDescription machine, string identity, GenerationPlanningOptions options)
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
                string exactSoftware = (string?)softwareTree["meta"]?["softwarePath"] ?? (string?)softwareTree["softwarePath"] ?? software;
                options.SoftwarePaths[station.Id] = exactSoftware;
                software = exactSoftware;
                var paths = (softwareTree["paths"] ?? softwareTree["meta"]?["paths"]) as JsonArray;
                if (paths == null) throw new GenerationCallRejectionException(new Error("Software readback has no complete canonical path inventory.", new PreconditionFailedDetails("generation-readback", software)));
                foreach (var row in paths.OfType<JsonObject>())
                {
                    string name = (string)row["name"]!;
                    string path = Uri.UnescapeDataString((string)row["objectPath"]!);
                    string group = path.Contains('/') ? path.Substring(0, path.LastIndexOf('/')) : "";
                    switch ((string?)row["kind"])
                    {
                        case "block": observed.Blocks.Add(new ProjectBlock { Station = station.Id, Name = name, Group = group }); break;
                        case "type": observed.Types.Add(new ProjectType { Station = station.Id, Name = name, Group = group }); break;
                        case "block-group": if (path.Length != 0) observed.Groups.Add(new ProjectGroup { Station = station.Id, Name = path, Kind = "blocks" }); break;
                        case "type-group": if (path.Length != 0) observed.Groups.Add(new ProjectGroup { Station = station.Id, Name = path, Kind = "types" }); break;
                        default: throw new ArgumentException("Unknown software tree row; complete readback required.", nameof(machine));
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
        private static string Name(JsonNode? item) => item is JsonValue value ? value.GetValue<string>() : (string?)(item?["name"] ?? item?["Name"]) ?? throw new ArgumentException("Inventory name is missing.");
        private static JsonObject? Body(CallToolResult result) => (result.StructuredContent ?? JsonNode.Parse((result.Content.FirstOrDefault() as TextContentBlock)?.Text ?? "null")) as JsonObject;
        internal CallToolResult ReadRun(string runId) => McpServer.V4Result("GetGenerationRun", Records.Read(runId));
        internal CallToolResult Apply(string planId, string hash, string expectedProject, string[]? phases, bool preview)
        {
            Stored stored;
            lock (sync) if (!plans.TryGetValue(planId, out stored!)) throw new GenerationCallRejectionException(new Error("Plan is not in this MCP session; replan.", new PreconditionFailedDetails("generation-plan-session", planId)));
            var plan = stored.Result.Plan;
            if (stored.Release != McpServer.ReleaseKey || stored.Expires < now() || hash != plan.PlanHash
                || GenerationDocuments.PlanHash(stored.Result.CanonicalPlan) != hash || expectedProject != stored.Project)
                throw new GenerationCallRejectionException(new Error("Generation plan/hash/target is stale; replan.", new PlanStaleDetails(Digest(plan.PlanHash), "plan-hash")));
            var selected = phases == null || phases.Length == 0 ? plan.Phases.Except(plan.Unavailable.Select(p => p.Phase)).ToHashSet(StringComparer.Ordinal) : phases.ToHashSet(StringComparer.Ordinal);
            if (selected.Any(p => !plan.Phases.Contains(p) || plan.Unavailable.Any(u => u.Phase == p))) throw new ArgumentException("Selected phase is unknown or unavailable.", nameof(phases));
            var steps = plan.Steps.Where(s => selected.Contains(s.Phase)).ToArray();
            if (steps.Any(s => s.DependsOn.Any(id => !steps.Any(d => d.Id == id)))) throw new ArgumentException("Select every dependency phase.", nameof(phases));
            Check(stored, fresh: true);
            VerifyArtifacts(stored);
            if (preview || steps.Length == 0) return McpServer.V4Result("ApplyGenerationPlan", new JsonObject { ["dryRun"] = true, ["plan"] = JsonNode.Parse(stored.Result.CanonicalPlan),
                ["selectedSteps"] = JsonSerializer.SerializeToNode(steps.Select(s => s.Id)), ["wouldChange"] = steps.Length > 0 });
            string parentId = AuditInvocation.CurrentRequestId ?? Guid.NewGuid().ToString("N");
            var settings = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
            var pending = Pending(plan, steps, stored.Binding, parentId, settings);
            pending.Validate();
            AuditInvocation.RecordCurrentRequest(plan.PlanHash);
            var records = Records;
            var run = new JsonObject { ["planId"] = planId, ["planHash"] = hash, ["release"] = stored.Release, ["parentRequestId"] = parentId,
                ["softwarePaths"] = JsonSerializer.SerializeToNode(stored.SoftwarePaths), ["actor"] = ActorScope.Actor,
                ["project"] = stored.Project, ["steps"] = JsonSerializer.SerializeToNode(steps.Select(s => new { s.Id, s.Phase, s.Key, s.Tool, s.ArgumentDigest }), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
                ["plan"] = JsonNode.Parse(stored.Result.CanonicalPlan),
                ["caps"] = new JsonObject { ["maximumSteps"] = maximumSteps, ["maximumSeconds"] = maximumDuration.TotalSeconds } };
            var backup = steps.SingleOrDefault(s => s.Tool == "ArchiveSavedProject");
            if (backup != null) run["backupPath"] = backup.Arguments["archivePath"].GetString();
            string runId = records.Begin(run);
            ApprovalOutcome? approval = null;
            int succeeded = 0; bool dispatched = false; string status = "rejected-before-operation", stopping = "approval", activePhase = ""; JsonObject? last = null;
            var facts = new RunFacts(plan);
            object sessionKey = TiaMcp.FoundationHost.EngineHostConfiguration.Current.Worker.SessionKey;
            var requestCancellation = McpServer.WorkerDispatchCancellation;
            bool Cancelled() => cancelled() || requestCancellation.IsCancellationRequested || GenerationWriteFence.Ended(sessionKey);
            IDisposable? fence = null;
            DateTimeOffset started = now();
            try
            {
                approval = wait(pending, settings, McpServer.WorkerDispatchCancellation).GetAwaiter().GetResult();
                records.Append(runId, new JsonObject { ["event"] = "approval", ["disabled"] = approval.Disabled, ["reason"] = approval.Reason, ["requestId"] = pending.RequestId });
                if (approval.Reason != null) throw new GenerationCallRejectionException(HostBehavior.Confirmation(approval.Reason, Digest(hash), parentId));
                started = now();
                if (McpServer.GenerationFenceRefusal("ApplyGenerationPlan", "{\"dryRun\":false}") is { } active)
                    throw new GenerationCallRejectionException(new Error("A generation run is active in this session.", new PreconditionFailedDetails("generation-run-active", (string?)active.StructuredContent?["data"]?["runId"] ?? "")));
                fence = GenerationWriteFence.Acquire(sessionKey, runId);
                records.Append(runId, new JsonObject { ["event"] = "fence", ["action"] = "acquire", ["runId"] = runId });
                Check(stored, fresh: true); VerifyArtifacts(stored);
                using var claim = records.Claim(planId);
                AuditInvocation.StartCurrent();
                int count = 0;
                foreach (var step in steps)
                {
                    activePhase = step.Phase;
                    if (Cancelled()) { status = "cancelled"; stopping = "cancelled-between-steps"; records.Append(runId, new JsonObject { ["event"] = "cancel", ["stepId"] = step.Id }); break; }
                    if (now() - started >= maximumDuration || count >= maximumSteps) { status = "cap"; stopping = "run-cap"; break; }
                    using var deferredCancellation = McpServer.DeferGenerationCancellation();
                    Check(stored);
                    if (descriptor(step.Tool) == null || step.Availability.Tool != "present") throw new GenerationCallRejectionException(new Error("Step is unavailable on this release.", new UnsupportedCapabilityDetails(stored.Release, step.Tool, null)));
                    if (CanonicalJson.Hash(JsonSerializer.SerializeToElement(step.Arguments)) != step.ArgumentDigest) throw new GenerationCallRejectionException(new Error("Approved step arguments changed.", new PlanStaleDetails(Digest(hash), "argument-digest")));
                    string requestId = Guid.NewGuid().ToString("N");
                    string? previewId = null, previewHash = null;
                    var finalArguments = JsonSerializer.SerializeToNode(step.Arguments)!.AsObject();
                    if (step.Review?.Mode == "tool-preview")
                    {
                        previewId = Guid.NewGuid().ToString("N");
                        JsonObject? previewBody;
                        using (InvocationJournal.UseCorrelation(previewId))
                        using (AuditInvocation.ReadOnlyPreview())
                            previewBody = Body(call(step.Tool, StepReviewRules.PreviewArguments(step), null));
                        if ((bool?)previewBody?["ok"] != true)
                        {
                            status = "rejected-before-operation"; stopping = "preview-refused"; last = previewBody;
                            records.Append(runId, new JsonObject { ["event"] = "step-preview", ["stepId"] = step.Id, ["requestId"] = previewId, ["verdict"] = stopping, ["result"] = previewBody?.DeepClone() });
                            records.Append(runId, new JsonObject { ["event"] = "step-end", ["stepId"] = step.Id, ["requestId"] = previewId, ["status"] = status, ["result"] = previewBody?.DeepClone() }); break;
                        }
                        var projection = StepReviewRules.Project(previewBody!["data"]!.AsObject());
                        string software = step.Arguments.TryGetValue("softwarePath", out var softwareArgument) ? softwareArgument.GetString()! : "";
                        // Import's current result omits the before inventory. Use the existing READ surface after preview.
                        if (step.Review.Rule == StepReviewRules.Import || step.Review.Rule == StepReviewRules.Delete)
                            projection["sourcesBefore"] = SourceNames(software);
                        int processId = JsonNode.Parse(stored.Binding)!["processId"]!.GetValue<int>();
                        var verdict = StepReviewRules.Compare(plan, step, projection, facts, processId);
                        records.Append(runId, new JsonObject { ["event"] = "step-preview", ["stepId"] = step.Id, ["requestId"] = previewId,
                            ["projection"] = projection.DeepClone(), ["toolHash"] = projection["planHash"]?.DeepClone(), ["verdict"] = verdict.Matches ? "matched" : "diverged" });
                        if (!verdict.Matches)
                        {
                            status = "diverged"; stopping = "preview-diverged";
                            records.Append(runId, new JsonObject { ["event"] = "divergence", ["stepId"] = step.Id, ["type"] = stopping, ["differences"] = verdict.Differences.DeepClone() });
                            records.Append(runId, new JsonObject { ["event"] = "step-end", ["stepId"] = step.Id, ["requestId"] = previewId, ["status"] = status }); break;
                        }
                        finalArguments = StepReviewRules.FinalArguments(step, projection); previewHash = (string)projection["planHash"]!;
                        var captured = facts.CapturePreview(step, projection);
                        if (captured.Count > 0) records.Append(runId, new JsonObject { ["event"] = "fact", ["stepId"] = step.Id, ["requestId"] = previewId, ["values"] = captured });
                    }
                    // Expiry guards approval and the first write only; every later write still checks identity/artifacts.
                    Check(stored, fresh: !dispatched);
                    if (now() - started >= maximumDuration) { status = "cap"; stopping = "stopped-before-write"; break; }
                    using var correlation = InvocationJournal.UseCorrelation(requestId);
                    using var association = AuditInvocation.GenerationStep(planId, step.Id, runId, parentId, previewHash, requestId);
                    using var granted = GenerationStepApproval.Enter(approval, step, finalArguments, previewId, previewHash, sessionKey, runId);
                    records.Append(runId, new JsonObject { ["event"] = "step-start", ["stepId"] = step.Id, ["requestId"] = requestId });
                    try
                    {
                        dispatched = true;
                        count++;
                        var result = call(step.Tool, finalArguments, () => {
                            try { Check(stored, fresh: count == 1); VerifyArtifacts(stored); return null; }
                            catch (GenerationCallRejectionException ex) { return McpServer.V4Reject(step.Tool, ex.Error); }
                        });
                        last = Body(result);
                        string outcome = (string?)last?["meta"]?["outcome"] ?? "unknown";
                        if (outcome == "succeeded" && (bool?)last?["ok"] != true) outcome = "unknown";
                        if (outcome == "succeeded" && step.Phase == "compile" && Walk(last?["data"]).Any(o => o.Any(p =>
                            string.Equals(p.Key, "errorCount", StringComparison.OrdinalIgnoreCase) && int.TryParse(p.Value?.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var errors) && errors > 0))) outcome = "failed";
                        if (outcome != "rejected-before-operation") claim.Consume();
                        if (outcome == "succeeded" && step.Review?.Mode == "tool-preview")
                        {
                            var effect = last!["data"]!.DeepClone().AsObject();
                            if (step.Review.Rule == StepReviewRules.Delete) effect["sourcesAfter"] = SourceNames(step.Arguments["softwarePath"].GetString()!);
                            var verdict = StepReviewRules.VerifyEffect(plan, step, effect, facts);
                            if (!verdict.Matches)
                            {
                                status = "effect-diverged"; stopping = step.Id; succeeded++;
                                records.Append(runId, new JsonObject { ["event"] = "divergence", ["stepId"] = step.Id, ["type"] = status, ["differences"] = verdict.Differences.DeepClone() });
                                records.Append(runId, new JsonObject { ["event"] = "step-end", ["stepId"] = step.Id, ["requestId"] = requestId, ["status"] = status, ["result"] = last.DeepClone() }); break;
                            }
                        }
                        if (outcome == "succeeded" && step.Review?.Mode == "transition")
                        {
                            // Creation is a completed write even if subsequent READ verification refuses or diverges.
                            succeeded++;
                            var before = JsonNode.Parse(stored.Binding)!.AsObject();
                            JsonObject? after = null, tree = null;
                            var verdict = new StepReviewVerdict();
                            try
                            {
                                var target = Target(allowUnbound: true);
                                after = JsonNode.Parse(target.Binding)!.AsObject();
                                tree = Read("GetProjectTree");
                                verdict = StepReviewRules.Transition(before, after, stored.Project, tree);
                                if (Target(allowUnbound: true) != target)
                                    verdict.Differences.Add(new JsonObject { ["field"] = "bindingDuringReadback", ["expected"] = target.Binding });
                                if (verdict.Matches) stored = stored with { Binding = target.Binding, BoundProject = target.Project };
                            }
                            catch (Exception ex)
                            { verdict.Differences.Add(new JsonObject { ["field"] = "transitionReadback", ["error"] = ex.GetType().Name + ": " + ex.Message }); }
                            records.Append(runId, new JsonObject { ["event"] = "transition", ["stepId"] = step.Id, ["requestId"] = requestId,
                                ["before"] = before, ["after"] = after?.DeepClone(), ["predictedProject"] = stored.Project,
                                ["createdProject"] = string.IsNullOrWhiteSpace((string?)after?["projectPath"]) ? stored.Project : (string?)after?["projectPath"], ["tree"] = tree?.DeepClone(),
                                ["verdict"] = verdict.Matches ? "matched" : "transition-diverged", ["differences"] = verdict.Differences.DeepClone() });
                            records.Append(runId, new JsonObject { ["event"] = "step-end", ["stepId"] = step.Id, ["requestId"] = requestId,
                                ["status"] = verdict.Matches ? "succeeded" : "diverged", ["result"] = last?.DeepClone() });
                            if (!verdict.Matches) { status = "diverged"; stopping = "transition-diverged"; break; }
                            continue;
                        }
                        records.Append(runId, new JsonObject { ["event"] = "step-end", ["stepId"] = step.Id, ["requestId"] = requestId, ["status"] = outcome, ["result"] = last?.DeepClone() });
                        if (outcome != "succeeded")
                        {
                            if (outcome == "unknown") McpServer.MarkSharedSessionUncertain();
                            status = outcome; stopping = step.Id; break;
                        }
                        succeeded++;
                        var accepted = facts.Accept(step, last!["data"]!.AsObject());
                        if (accepted.Count > 0) records.Append(runId, new JsonObject { ["event"] = "fact", ["stepId"] = step.Id, ["requestId"] = requestId, ["values"] = accepted });
                        if (step.Tool == "SaveProject") PersistWitnesses(records, runId, plan);
                    }
                    catch (Exception ex)
                    {
                        status = step.Phase == "record" ? "partial" : "unknown"; stopping = step.Id;
                        claim.Consume();
                        if (step.Phase != "record") McpServer.MarkSharedSessionUncertain();
                        records.Append(runId, new JsonObject { ["event"] = "step-end", ["stepId"] = step.Id, ["requestId"] = requestId, ["status"] = status,
                            ["error"] = ex.GetType().Name + ": " + ex.Message });
                        break;
                    }
                }
                if (succeeded == steps.Length && status != "effect-diverged" && stopping != "transition-diverged") { status = "succeeded"; stopping = "complete"; }
            }
            catch (GenerationCallRejectionException ex) { last = new JsonObject { ["error"] = JsonSerializer.SerializeToNode(ex.Error) }; stopping = ex.Error.Message; }
            catch (ArgumentException ex) { stopping = ex.Message; }
            catch (OperationCanceledException ex) { status = "cancelled"; stopping = "cancelled-before-dispatch: " + ex.Message; }
            catch (Exception ex)
            {
                // These failures precede the pending dispatch; completed earlier steps remain recorded.
                stopping = "before-dispatch: " + ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                // Release even when evidence I/O or approval completion fails. Shutdown releases independently.
                if (activePhase == "record" && succeeded > 0 && status != "succeeded") status = "partial";
                fence?.Dispose();
                try
                {
                    if (fence != null) records.Append(runId, new JsonObject { ["event"] = "fence", ["action"] = "release", ["runId"] = runId });
                    records.Append(runId, new JsonObject { ["event"] = "end", ["status"] = status, ["stoppingReason"] = stopping, ["error"] = last?["error"]?.DeepClone() });
                }
                catch (Exception ex) when (dispatched && (ex is IOException || ex is UnauthorizedAccessException))
                { McpServer.MarkSharedSessionUncertain(); throw new GenerationPersistenceUnknownException(runId, ex); }
                finally { if (approval != null) ApprovalClient.Complete(approval, status).GetAwaiter().GetResult(); }
            }
            JsonObject data;
            try { data = records.Read(runId); }
            catch (Exception ex) when (dispatched && (ex is IOException || ex is UnauthorizedAccessException))
            { McpServer.MarkSharedSessionUncertain(); throw new GenerationPersistenceUnknownException(runId, ex); }
            data["rollbackPerformed"] = false;
            data["nextAction"] = status == "succeeded" ? "inspect" : "replan";
            if (status == "succeeded") return McpServer.V4Result("ApplyGenerationPlan", data, completed: true, current: true);
            if (status == "unknown") return McpServer.V4Result("ApplyGenerationPlan", data, new Error("Generation outcome unknown; inspect the project, reset the session and replan. Never replay this run.", new OutcomeUnknownDetails("generation", new Dictionary<string, JsonElement>())), Outcome.Unknown, Execution.Unknown, Completeness.Unknown, current: true);
            if (succeeded > 0) return McpServer.V4Result("ApplyGenerationPlan", data, new Error("Generation stopped; completed writes remain. Replan after inspecting the evidence.", new PartialFailureDetails(succeeded, status == "effect-diverged" ? 0 : 1, Math.Max(0, steps.Length - succeeded - (status == "effect-diverged" ? 0 : 1)))), Outcome.Partial, Execution.Partial, Completeness.Partial, current: true);
            if (status == "failed") return McpServer.V4Result("ApplyGenerationPlan", data,
                new Error("The first generation step failed; inspect its retained evidence before replanning.", new PreconditionFailedDetails("generation-step", stopping)),
                Outcome.Failed, Execution.Completed, Completeness.None, current: true);
            return McpServer.V4Reject("ApplyGenerationPlan", approval?.Reason != null ? HostBehavior.Confirmation(approval.Reason, Digest(hash), parentId)
                : new Error("Generation stopped before any successful step: " + stopping, new PreconditionFailedDetails("generation", stored.Project)), data);
        }
        private JsonArray SourceNames(string software) => new(Items(Read("ListPlcExternalSources", new JsonObject { ["softwarePath"] = software }))
            .Select(item => (JsonNode?)JsonValue.Create(Name(item))).ToArray());
        private void Check(Stored stored, bool fresh = false)
        {
            if (fresh && stored.Expires < now()) throw new GenerationCallRejectionException(new Error("Generation plan expired; replan.", new PlanStaleDetails(Digest(stored.Result.Plan.PlanHash), "expired")));
            if (Target(allowUnbound: stored.BoundProject.Length == 0) != (stored.Binding, stored.BoundProject)) throw new GenerationCallRejectionException(new Error("Project/session identity changed; replan.", new PlanStaleDetails(Digest(stored.Result.Plan.PlanHash), "identity")));
        }
        private void VerifyArtifacts(Stored stored)
        {
            foreach (var artifact in stored.Result.Plan.Artifacts)
            {
                string path = Path.Combine(root(), "artifacts", artifact.Path.Replace('/', Path.DirectorySeparatorChar));
                for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory != null; directory = directory.Parent)
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Artifact links refused.");
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || CanonicalJson.HashBytes(File.ReadAllBytes(path)) != artifact.Sha256)
                    throw new GenerationCallRejectionException(new Error("Staged artifact changed; replan.", new PlanStaleDetails(Digest(stored.Result.Plan.PlanHash), "artifact-hash")));
            }
        }
        private PendingApproval Pending(GenerationPlan plan, IEnumerable<GenerationPlanStepsItem> steps, string binding, string id, ApprovalSettings settings)
        {
            var selected = steps.ToArray();
            var summary = new JsonObject { ["planId"] = plan.PlanId, ["expectedPlanHash"] = plan.PlanHash,
                ["phaseCounts"] = JsonSerializer.SerializeToNode(selected.GroupBy(s => s.Phase).ToDictionary(g => g.Key, g => g.Count())),
                ["conflicts"] = JsonSerializer.SerializeToNode(plan.Conflicts), ["skipped"] = JsonSerializer.SerializeToNode(plan.Skipped),
                ["unavailable"] = JsonSerializer.SerializeToNode(plan.Unavailable), ["artifacts"] = JsonSerializer.SerializeToNode(plan.Artifacts),
                ["caps"] = new JsonObject { ["maximumSteps"] = maximumSteps, ["maximumSeconds"] = maximumDuration.TotalSeconds } };
            var pending = PendingApproval.Create("foundation", plan.Target.Release, "ApplyGenerationPlan", summary.ToJsonString(), binding, settings.TimeoutSeconds, id);
            pending.Kind = "plan"; pending.PlanHash = plan.PlanHash; pending.PlanJson = GenerationDocuments.Canonical(plan);
            pending.Operations = selected.Select(s => new ApprovalAction { Tool = s.Tool, Action = s.Phase + "/" + s.Op, Target = s.Key }).ToArray();
            return pending;
        }
        private static string Digest(string hash) => hash.StartsWith("sha256:", StringComparison.Ordinal) ? hash.Substring(7) : hash;
    }
}
