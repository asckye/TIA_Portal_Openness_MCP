using System;
using TiaMcp.Adapters.Contracts.Candidates;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Logic.V4
{
    public sealed class PlcImportRejection : Exception
    {
        public Error Error { get; }
        public PlcImportRejection(Error error) : base(error.Message) { Error = error; }
    }

    // The owning native thread serializes this session. Plans cannot cross sessions or be replayed.
    public sealed class PlcImportSession
    {
        private readonly Dictionary<string, Plan> plans = new Dictionary<string, Plan>(StringComparer.Ordinal);
        private readonly HashSet<string> consumed = new HashSet<string>(StringComparer.Ordinal);
        private bool nativeAttempted;
        public bool RequiresSessionReset { get; private set; }

        public Envelope Run(IPlcImportAdapter adapter, string release, string tool, string id, PlcImportRequest request,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
        {
            var locks = new Dictionary<string, Stream>(StringComparer.OrdinalIgnoreCase);
            nativeAttempted = false;
            Envelope? result = null;
            Exception? failure = null;
            try { result = RunCore(adapter, release, tool, id, request, mode, confirm, expectedPlanHash, expectedProjectFile, locks); }
            catch (Exception ex) { failure = ex; }
            foreach (var stream in locks.Values)
                try { stream.Dispose(); } catch (Exception ex) { failure ??= ex; }
            if (failure == null) return result!;
            var data = result?.Data == null ? new JsonObject() : JsonNode.Parse(result.Data.Value.GetRawText())!.AsObject();
            data["lockCleanup"] = "failed";
            if (nativeAttempted)
            {
                RequiresSessionReset = true;
                data["residueCheck"] ??= new JsonObject { ["status"] = "unavailable", ["reason"] = "completion-or-lock-cleanup-failed" };
                return Result(release, tool, id, data, new Error("Import completion is unknown; inspect residue and rebuild the session.",
                    new OutcomeUnknownDetails("completion-or-lock-cleanup", new Dictionary<string, JsonElement> { ["residueCheck"] = V4Json.Data(data["residueCheck"]!.AsObject())!.Value })), Outcome.Unknown, Execution.Unknown);
            }
            return Result(release, tool, id, data, new Error("Input lock cleanup failed before a native import.", new IoFailedDetails("close-input", null)), Outcome.RejectedBeforeOperation, Execution.NotStarted);
        }

        private Envelope RunCore(IPlcImportAdapter adapter, string release, string tool, string id, PlcImportRequest request,
            string mode, bool confirm, string expectedPlanHash, string expectedProjectFile, IDictionary<string, Stream> locks)
        {
            var children = new List<BatchItem>();
            PlcImportInput[] inputs = Array.Empty<PlcImportInput>();
            PlcImportObject[]? inventory = null;
            PlanIdentity? identity = null;
            JsonObject? data = null;
            bool issued = false;
            PlcImportAttempt? attempt = null;
            int index = 0;
            try
            {
                if (RequiresSessionReset) Refuse("Inspect the import residue and establish a new session.", new SessionResetRequiredDetails("plc-import-unknown"));
                if (!PlcImportContract.Entries.Contains(tool, StringComparer.Ordinal)) Invalid("tool");
                if (mode != "preview" && mode != "apply") Invalid("mode");
                if (request.VersionPolicy != "exact") Unsupported(release, "versionPolicy");
                if (request.OnError != "stop") Unsupported(release, "onError");
                if (request.CompileAfter) Unsupported(release, "compileAfter");
                if (request.TechnologyFolderPath != "") Unsupported(release, "technology-objects");
                int limit = PlcImportContract.MaximumItems(tool, release);
                if (request.MaxItems < 1 || request.MaxItems > limit)
                    Refuse("The import item budget is outside this release's limit.", new LimitExceededDetails("maxItems", limit, request.MaxItems));
                if (string.IsNullOrWhiteSpace(request.SoftwarePath)) Invalid("softwarePath");
                foreach (var group in new[] { request.BlockGroupPath, request.TypeGroupPath, request.TagFolderPath }) Group(group);
                if (mode == "apply" && !confirm) Refuse("Apply requires explicit confirmation.", new ConfirmationRequiredDetails(expectedPlanHash.Length == 64 && expectedPlanHash.All(c => "0123456789abcdef".Contains(c)) ? expectedPlanHash : null));
                if (mode == "apply" && (expectedPlanHash.Length != 64 || expectedPlanHash.Any(c => !"0123456789abcdef".Contains(c)))) Invalid("expectedPlanHash");
                if (mode == "apply") DeviceCreationSession.CanonicalProject(expectedProjectFile);
                identity = CandidateHostMapping.Plan(adapter.ReadIdentity());
                if (mode == "apply")
                {
                    if (DeviceCreationSession.CanonicalProject(identity.ProjectFile!) != DeviceCreationSession.CanonicalProject(expectedProjectFile)) IdentityMismatch();
                    if (!plans.TryGetValue(expectedPlanHash, out var reviewed) || consumed.Contains(expectedPlanHash)) Stale(expectedPlanHash, "missing-or-consumed-plan");
                    if (BindingHash(identity) != BindingHash(reviewed!.Identity)) IdentityMismatch();
                }
                inputs = adapter.ReadInputs(release, tool, request, locks).ToArray();
                if (inputs.Length < 1 || inputs.Length > request.MaxItems) Refuse("Selected imports exceed the item budget or are empty.", new LimitExceededDetails("inputs", request.MaxItems, inputs.Length));
                var files = FileIdentities(locks);
                if (inputs.SelectMany(i => i.Files).Distinct(StringComparer.OrdinalIgnoreCase).Count() != locks.Count
                    || inputs.SelectMany(i => i.Files).Any(p => !locks.ContainsKey(p))) Invalid("input-files");
                if (request.Overwrite && inputs.Any(i => !adapter.SupportsOverwrite(i))) Unsupported(release, "overwrite");
                inventory = Inventory(adapter.ReadInventory());
                var targets = inputs.Select(i => new { target = i.Target, groupIdentity = adapter.TargetGroupIdentity(i.Target), overwriteSupported = adapter.SupportsOverwrite(i) }).ToArray();
                if (targets.Any(t => string.IsNullOrEmpty(t.groupIdentity))) Refuse("The exact target group is unavailable.", new NotFoundDetails("target-group"));
                var conflicts = inputs.Where(i => inventory.Any(o => Collision(o, i.Target))).Select(i => i.Target.Name).ToArray();
                if (inputs.Where((input, index) => inputs.Take(index).Any(prior => Collision(prior.Target, input.Target))).Any()) Invalid("duplicate-input-objects");
                if (request.Overwrite && inputs.Any(input => inventory.Any(existing => Collision(existing, input.Target) && !SameTarget(existing, input.Target))))
                    Refuse("Overwrite cannot replace an object outside the exact reviewed group and identity.", new AlreadyExistsDetails("overwrite-collision"));
                var planIdentity = new PlanIdentity(identity.ProcessId, identity.ProcessStartUtc, identity.ProjectFile, identity.BindingEpoch, null, files);
                var argsHash = DeviceCreationSession.Hash(request);
                var inventoryHash = DeviceCreationSession.Hash(new { inventory, targets });
                var inputHashes = files.ToDictionary(f => f.Path, f => f.Sha256!, StringComparer.Ordinal);
                var operations = inputs.Select(i => new PlanOperation(tool, i.Target.GroupPath + "/" + i.Target.Name,
                    V4Json.Data(new { inputPath = i.Path, target = i.Target, files = i.Files, overwrite = request.Overwrite })!.Value)).ToArray();
                var warnings = Array.Empty<Warning>();
                string hash = DeviceCreationSession.Hash(new { releaseKey = release, tool, argumentsHash = argsHash, identity = planIdentity, inputHashes, inventoryHash, operations, warnings });
                var plan = new Plan(hash, release, tool, argsHash, planIdentity, inputHashes, inventoryHash, operations, warnings);
                data = new JsonObject { ["plan"] = JsonNode.Parse(V4Json.Serialize(plan)), ["files"] = JsonNode.Parse(V4Json.Serialize(files)),
                    ["targets"] = JsonNode.Parse(V4Json.Serialize(targets)), ["inventory"] = JsonNode.Parse(V4Json.Serialize(inventory)),
                    ["conflicts"] = JsonNode.Parse(V4Json.Serialize(conflicts)), ["importIssued"] = false, ["dependencyStatus"] = "unverified-caller-order-required" };
                if (mode == "preview")
                {
                    if (plans.Count >= 128 && !plans.ContainsKey(hash)) Refuse("The session plan budget is exhausted.", new LimitExceededDetails("plans", 128, plans.Count + 1));
                    plans[hash] = plan;
                    return Result(release, tool, id, data, null, Outcome.Succeeded, Execution.ReadOnly);
                }
                if (hash != expectedPlanHash) Stale(expectedPlanHash, "files-inventory-target-or-arguments-changed");
                if (!request.Overwrite && conflicts.Length > 0) Refuse("An imported object already exists.", new AlreadyExistsDetails(conflicts[0]));
                consumed.Add(expectedPlanHash);
                var current = inventory;
                for (index = 0; index < inputs.Length; index++)
                {
                    var input = inputs[index];
                    var check = new PlcImportCheck { Release = release, Tool = tool, Request = request, Identity = CandidateHostMapping.Identity(identity),
                        Inputs = inputs, Files = CandidateHostMapping.Files(files), InitialInventory = inventory, CurrentInventory = current,
                        Index = index, GroupIdentity = targets[index].groupIdentity, OverwriteSupported = targets[index].overwriteSupported };
                    check.Digest = CandidateDigest.ImportObservation(check);
                    attempt = adapter is IImportCandidateBoundary boundary ? boundary.Execute(check, locks) : CandidateExecution.Import(adapter, check, locks);
                    issued = attempt.Issued;
                    nativeAttempted |= issued;
                    if (issued) data["importIssued"] = true;
                    if (attempt.Fault != null) throw CandidateHostMapping.Import(attempt.Fault, expectedPlanHash);
                    var imported = attempt.Imported!;
                    string contentHash = attempt.ContentHash;
                    current = attempt.After;
                    children.Add(new BatchItem(index, input.Path, Result(release, tool, id,
                        new JsonObject { ["input"] = JsonNode.Parse(V4Json.Serialize(input)), ["returnedObject"] = JsonNode.Parse(V4Json.Serialize(imported)),
                            ["contentVerified"] = true, ["contentSha256"] = contentHash, ["nativeImportCalls"] = 1 }, null, Outcome.Succeeded, Execution.Completed)));
                    issued = false;
                }
                data["items"] = JsonNode.Parse(V4Json.Serialize(new BatchData(children)))!["items"]!.DeepClone();
                data["residueCheck"] = Residue(inventory, current);
                data["succeeded"] = children.Count; data["failed"] = 0; data["notExecuted"] = 0;
                return Result(release, tool, id, data, null, Outcome.Succeeded, Execution.Completed);
            }
            catch (Exception ex)
            {
                if (ex is CandidateObservationException observed) ex = CandidateHostMapping.Import(observed.Fault);
                if (mode == "apply" && data == null && identity != null && (ex is not PlcImportRejection rejected
                    || rejected.Error.Code != ErrorCode.IdentityMismatch && rejected.Error.Code != ErrorCode.PlanStale))
                    ex = new PlcImportRejection(new Error("The reviewed input can no longer be read or admitted.", new PlanStaleDetails(expectedPlanHash, "input-unavailable-or-changed")));
                Error error = ex is PlcImportRejection refused ? refused.Error
                    : new Error("Import preflight could not establish the reviewed target; no native import was issued for this item.",
                        ex is IOException || ex is UnauthorizedAccessException ? (ErrorDetails)new IoFailedDetails("read", null) : new PreconditionFailedDetails("plc-import-preflight", null));
                if (issued)
                {
                    RequiresSessionReset = true;
                    var residue = attempt == null ? new JsonObject { ["status"] = "unavailable", ["reason"] = "residue-read-failed" }
                        : CandidateHostMapping.ImportResidue(attempt.Residue);
                    data ??= new JsonObject(); data["residueCheck"] = residue;
                    error = new Error("Import outcome is unknown. Inspect the residue and rebuild the session; never replay or roll back automatically.",
                        new OutcomeUnknownDetails("native-import-or-content-readback", new Dictionary<string, JsonElement> { ["residueCheck"] = V4Json.Data(residue)!.Value }));
                }
                if (data != null && inputs.Length > 0 && mode == "apply")
                {
                    if (index < inputs.Length) children.Add(new BatchItem(index, inputs[index].Path,
                        Result(release, tool, id, new JsonObject { ["nativeImportCalls"] = issued ? 1 : 0 }, error,
                            issued ? Outcome.Unknown : Outcome.RejectedBeforeOperation, issued ? Execution.Unknown : Execution.NotStarted)));
                    for (int i = index + 1; i < inputs.Length; i++) children.Add(new BatchItem(i, inputs[i].Path,
                        Result(release, tool, id, null, new Error("A previous import stopped this batch.", new NotExecutedDetails(Math.Min(index, inputs.Length - 1))), Outcome.RejectedBeforeOperation, Execution.NotStarted)));
                    var batch = new BatchData(children);
                    data["items"] = JsonNode.Parse(V4Json.Serialize(batch))!["items"]!.DeepClone();
                    int succeeded = children.Count(c => c.Result.Ok);
                    data["succeeded"] = succeeded; data["failed"] = index < inputs.Length ? 1 : 0; data["notExecuted"] = Math.Max(0, inputs.Length - index - 1);
                    if (!issued && succeeded > 0) error = new Error("Verified imports succeeded before the batch stopped.", new PartialFailureDetails(succeeded, index < inputs.Length ? 1 : 0, Math.Max(0, inputs.Length - index - 1)));
                    return Result(release, tool, id, data, error, issued ? Outcome.Unknown : succeeded > 0 ? Outcome.Partial : Outcome.RejectedBeforeOperation,
                        issued ? Execution.Unknown : succeeded > 0 ? Execution.Partial : Execution.NotStarted);
                }
                return Result(release, tool, id, data, error, issued ? Outcome.Unknown : Outcome.RejectedBeforeOperation, issued ? Execution.Unknown : Execution.NotStarted);
            }
        }

        public static string ByteHash(byte[] bytes)
        { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }

        public static byte[] Read(Stream stream)
        {
            if (!stream.CanRead || !stream.CanSeek || stream.Length < 1 || stream.Length > 16 * 1024 * 1024) Invalid("file-size");
            long length = stream.Length; stream.Position = 0;
            using var memory = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            { if (memory.Length + count > 16 * 1024 * 1024) Invalid("file-size"); memory.Write(buffer, 0, count); }
            if (length != memory.Length || stream.Length != length) throw new IOException("Input changed while locked.");
            return memory.ToArray();
        }

        private static PlanFile[] FileIdentities(IDictionary<string, Stream> locks)
        {
            var files = locks.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => { var bytes = Read(p.Value); return new PlanFile(p.Key, true, bytes.LongLength, ByteHash(bytes)); }).ToArray();
            if (files.Sum(f => f.ByteLength!.Value) > 128 * 1024 * 1024) Refuse("The aggregate file budget is exceeded.", new LimitExceededDetails("inputBytes", 128 * 1024 * 1024, files.Sum(f => f.ByteLength!.Value)));
            return files;
        }

        private static string BindingHash(PlanIdentity i) => DeviceCreationSession.Hash(new { i.ProcessId, i.ProcessStartUtc, projectFile = DeviceCreationSession.CanonicalProject(i.ProjectFile!), i.BindingEpoch });
        private static string Space(string kind) => kind == "UDT" ? "type" : kind == "TagTable" ? "tag" : kind.StartsWith("group-", StringComparison.Ordinal) ? kind : "block";
        private static bool Collision(PlcImportObject a, PlcImportObject b) => Space(a.Kind) == Space(b.Kind) && (string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
            || b.Number.HasValue && a.Number == b.Number && (a.Kind == b.Kind || new[] { "GlobalDB", "InstanceDB" }.Contains(a.Kind) && new[] { "GlobalDB", "InstanceDB" }.Contains(b.Kind)));
        private static bool SameTarget(PlcImportObject a, PlcImportObject b) => a.Name == b.Name && a.Kind == b.Kind && a.GroupPath == b.GroupPath && (!a.Number.HasValue || a.Number == b.Number);
        private static PlcImportObject[] Inventory(IReadOnlyList<PlcImportObject> rows)
        {
            if (rows == null || rows.Count > 4096 || rows.Any(r => r == null || string.IsNullOrEmpty(r.Id) || string.IsNullOrEmpty(r.Name) || string.IsNullOrEmpty(r.Kind))
                || rows.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != rows.Count) Refuse("The complete PLC inventory is unavailable.", new PreconditionFailedDetails("complete-inventory", null));
            return rows!.OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
        }
        private static void VerifyDelta(PlcImportObject[] before, PlcImportObject[] after, PlcImportObject target, PlcImportObject actual, bool overwrite)
        {
            var replaced = overwrite ? before.Where(i => SameTarget(target, i)).ToArray() : Array.Empty<PlcImportObject>();
            var retained = before.Except(replaced).ToArray();
            if (after.Length != retained.Length + 1 || !after.Any(i => i.Id == actual.Id && SameTarget(actual, i))
                || retained.Any(i => !after.Any(j => DeviceCreationSession.Hash(i) == DeviceCreationSession.Hash(j)))) throw new InvalidDataException("Import inventory delta is not the reviewed addition/replacement.");
        }
        private static JsonObject Residue(PlcImportObject[] before, PlcImportObject[] after) => new JsonObject { ["status"] = "checked",
            ["added"] = JsonNode.Parse(V4Json.Serialize(after.Where(i => !before.Any(j => i.Id == j.Id)).ToArray())),
            ["removed"] = JsonNode.Parse(V4Json.Serialize(before.Where(i => !after.Any(j => i.Id == j.Id)).ToArray())),
            ["changed"] = JsonNode.Parse(V4Json.Serialize(after.Where(i => before.Any(j => i.Id == j.Id && DeviceCreationSession.Hash(i) != DeviceCreationSession.Hash(j))).ToArray())) };

        public static Envelope Result(string release, string tool, string id, JsonObject? data, Error? error, Outcome outcome, Execution execution)
            => Envelope.Create(data, error, new Meta(DateTimeOffset.UtcNow, release, tool, Meta.Correlate(id), outcome, execution,
                outcome == Outcome.Unknown || error?.Code == ErrorCode.SessionResetRequired, BehaviorPolicy.SafeV4,
                outcome == Outcome.Unknown ? Completeness.Unknown : outcome == Outcome.Succeeded ? Completeness.Complete : outcome == Outcome.Partial ? Completeness.Partial : Completeness.None,
                null, Array.Empty<Warning>()));
        public static void Group(string value)
        { if (value != "" && (value.Trim() != value || value.Split('/').Any(p => p == "" || p == "." || p == ".." || p.Any(char.IsControl) || p.IndexOfAny("\\:*?\"<>|".ToCharArray()) >= 0))) Invalid("groupPath"); }
        public static void Invalid(string parameter) => Refuse("Invalid PLC import argument.", new InvalidArgumentDetails(parameter, Array.Empty<string>()));
        public static void Unsupported(string release, string action) => Refuse("The release/object does not support the requested import capability.", new UnsupportedCapabilityDetails(release, "P6-IMPORT", action));
        public static void Stale(string hash, string reason) => Refuse("The import plan changed since preview.", new PlanStaleDetails(hash, reason));
        public static void IdentityMismatch() => Refuse("The process or project binding changed since preview.", new IdentityMismatchDetails("project-binding", null, null));
        public static void Refuse(string message, ErrorDetails details) => throw new PlcImportRejection(new Error(message, details));
    }
}
