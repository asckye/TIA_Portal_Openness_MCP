using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts.Candidates;

namespace TiaMcp.Logic.V4
{
    public sealed class SourceRejection : Exception
    {
        public Error Error { get; }
        public SourceRejection(Error error) : base(error.Message) { Error = error; }
    }
    public sealed class SourceSession
    {
        private readonly Dictionary<string, Plan> plans = new Dictionary<string, Plan>(StringComparer.Ordinal);
        private readonly HashSet<string> consumed = new HashSet<string>(StringComparer.Ordinal);
        public bool RequiresSessionReset { get; private set; }
        public Envelope Run(ISourceAdapter adapter, string release, string tool, string id, SourceRequest request,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
        {
            var locks = new Dictionary<string, Stream>(StringComparer.OrdinalIgnoreCase);
            var children = new List<BatchItem>(); JsonObject? data = null; SourceAttempt? attempt = null;
            bool issued = false, anyIssued = false; int index = 0; Envelope result;
            try
            {
                if (RequiresSessionReset) Refuse(new SessionResetRequiredDetails("external-source-unknown"));
                if (mode != "preview" && mode != "apply") Refuse(new InvalidArgumentDetails("mode", Array.Empty<string>()));
                if (request.Items.Length > 256) Refuse(new LimitExceededDetails("items", 256, request.Items.Length));
                if (request.Overwrite || request.OnError != "stop" || request.MissingPolicy != "reject") Refuse(new UnsupportedCapabilityDetails(release, "P6-SOURCE", "overwrite/onError/missingPolicy"));
                if (mode == "apply" && request.Items.Length == 0) Refuse(new InvalidArgumentDetails("mode", new[] { "preview" }));
                if (mode == "apply" && !confirm) Refuse(new ConfirmationRequiredDetails(null));
                foreach (var i in request.Items)
                {
                    if (!new[] { "import", "generate", "delete" }.Contains(i.Action, StringComparer.Ordinal) || i.SourceName.Length == 0 || i.SourceName.Any(char.IsControl)) Refuse(new InvalidArgumentDetails("items", Array.Empty<string>()));
                    if (i.Action == "import")
                    {
                        if (!Path.IsPathRooted(i.FilePath) || Path.GetFullPath(i.FilePath) != i.FilePath || !new[] { ".scl", ".awl", ".db", ".udt" }.Contains(Path.GetExtension(i.FilePath), StringComparer.OrdinalIgnoreCase)
                            || !string.Equals(Path.GetExtension(i.FilePath), Path.GetExtension(i.SourceName), StringComparison.OrdinalIgnoreCase)) Refuse(new InvalidArgumentDetails("filePath", Array.Empty<string>()));
                        CandidateImportFiles.SafePath(new FileInfo(i.FilePath));
                        if (!locks.ContainsKey(i.FilePath)) locks.Add(i.FilePath, new FileStream(i.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read));
                        CandidatePrimitives.Read(locks[i.FilePath]);
                    }
                    else if (i.FilePath != "") Refuse(new InvalidArgumentDetails("filePath", Array.Empty<string>()));
                }
                var before = adapter.Observe(); CandidateExecution.ValidateSourceObservation(before);
                if (!before.Groups.Contains(request.ReadGroup, StringComparer.Ordinal)) Refuse(new NotFoundDetails(request.ReadGroup));
                if (request.ReadSource != "" && !before.Sources.Any(s => s.GroupPath == request.ReadGroup && s.Name == request.ReadSource)) Refuse(new NotFoundDetails(request.ReadSource));
                foreach (var item in request.Items.Where(i => i.Action == "generate"))
                {
                    var source = CandidateExecution.SourceTarget(before, item);
                    if (source != null && source.FilePath != "")
                    {
                        CandidateImportFiles.SafePath(new FileInfo(source.FilePath));
                        if (!locks.ContainsKey(source.FilePath)) locks.Add(source.FilePath, new FileStream(source.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read));
                        if (CandidatePrimitives.ByteHash(CandidatePrimitives.Read(locks[source.FilePath])) != source.ContentHash) Refuse(new PlanStaleDetails(null, "imported-source-file-changed"));
                    }
                }
                var files = CandidatePrimitives.Files(locks);
                var identity = CandidateHostMapping.Plan(before.Identity);
                var planIdentity = new PlanIdentity(identity.ProcessId, identity.ProcessStartUtc, identity.ProjectFile, identity.BindingEpoch, null, files.Select(f => new PlanFile(f.Path, f.Exists, f.ByteLength, f.Sha256)).ToArray());
                var inputHashes = files.ToDictionary(f => f.Path, f => f.Sha256!, StringComparer.Ordinal);
                var operations = request.Items.Select(i => new PlanOperation(tool, before.SoftwarePath + "/" + i.GroupPath + "/" + i.SourceName,
                    V4Json.Data(new { action = i.Action, sourceName = i.SourceName, groupPath = i.GroupPath, filePath = i.FilePath,
                        sourceIdentity = CandidateExecution.SourceTarget(before, i)?.Id, nativeCall = i.Action == "import" ? "CreateFromFile" : i.Action == "generate" ? "GenerateBlocksFromSource" : "Delete" })!.Value)).ToArray();
                var warnings = Array.Empty<Warning>(); string argumentsHash = DeviceCreationSession.Hash(request), inventoryHash = SourceDigest.Observation(before);
                string hash = DeviceCreationSession.Hash(new { releaseKey = release, tool, argumentsHash, identity = planIdentity, inputHashes, inventoryHash, operations, warnings });
                var plan = new Plan(hash, release, tool, argumentsHash, planIdentity, inputHashes, inventoryHash, operations, warnings);
                data = new JsonObject { ["plan"] = JsonNode.Parse(V4Json.Serialize(plan)), ["sources"] = JsonNode.Parse(V4Json.Serialize(before.Sources.Where(s => request.Items.Length > 0 || s.GroupPath == request.ReadGroup && (request.ReadSource == "" || s.Name == request.ReadSource)).ToArray())),
                    ["softwarePath"] = before.SoftwarePath, ["plcIdentity"] = before.PlcId, ["files"] = JsonNode.Parse(V4Json.Serialize(files)), ["nativeResult"] = null, ["observation"] = null };
                if (mode == "preview")
                {
                    if (plans.Count >= 128 && !plans.ContainsKey(hash)) Refuse(new LimitExceededDetails("plans", 128, plans.Count + 1));
                    var available = before.Sources.ToList(); var objectNames = before.Objects.ToList();
                    foreach (var item in request.Items)
                    {
                        if (!before.Groups.Contains(item.GroupPath, StringComparer.Ordinal)) Refuse(new NotFoundDetails(item.GroupPath));
                        var target = available.SingleOrDefault(s => s.GroupPath == item.GroupPath && s.Name == item.SourceName);
                        if (item.Action == "import")
                        {
                            if (available.Any(s => s.GroupPath == item.GroupPath && string.Equals(s.Name, item.SourceName, StringComparison.OrdinalIgnoreCase))) Refuse(new AlreadyExistsDetails(item.SourceName));
                            available.Add(new SourceRow { Id = "planned:" + available.Count, GroupPath = item.GroupPath, Name = item.SourceName, Declarations = SourceDeclarations.TryRead(CandidatePrimitives.Read(locks[item.FilePath])) });
                        }
                        else
                        {
                            if (target == null) Refuse(new NotFoundDetails(item.SourceName));
                            if (item.Action == "delete") available.Remove(target!);
                            else
                            {
                                if (target!.Declarations.Length == 0) Refuse(new PreconditionFailedDetails("verified-source-declarations", item.SourceName));
                                if (target.Declarations.Any(n => objectNames.Contains(n, StringComparer.OrdinalIgnoreCase))) Refuse(new AlreadyExistsDetails(item.SourceName));
                                objectNames.AddRange(target.Declarations);
                            }
                        }
                    }
                    if (request.Items.Length > 0) plans[hash] = plan; result = Result(release, tool, id, data, null, Outcome.Succeeded, Execution.ReadOnly);
                }
                else
                {
                    if (string.IsNullOrEmpty(expectedProjectFile) || DeviceCreationSession.CanonicalProject(expectedProjectFile) != DeviceCreationSession.CanonicalProject(identity.ProjectFile!)) Refuse(new IdentityMismatchDetails("project-binding", null, null));
                    if (!plans.TryGetValue(expectedPlanHash, out var reviewed) || consumed.Contains(expectedPlanHash)) Refuse(new PlanStaleDetails(expectedPlanHash.Length == 64 ? expectedPlanHash : null, "missing-or-consumed-plan"));
                    if (CandidateDigest.Binding(CandidateHostMapping.Identity(reviewed!.Identity)) != CandidateDigest.Binding(before.Identity)) Refuse(new IdentityMismatchDetails("project-binding", null, null));
                    if (hash != expectedPlanHash) Refuse(new PlanStaleDetails(expectedPlanHash, "source-files-inventory-or-arguments-changed"));
                    consumed.Add(hash); var current = before;
                    for (index = 0; index < request.Items.Length; index++)
                    {
                        var check = new SourceCheck { Release = release, Request = request, Before = current, Files = files, Index = index };
                        check.Digest = SourceDigest.Check(check);
                        attempt = adapter is ISourceBoundary boundary ? boundary.Execute(check, locks) : CandidateExecution.Source(adapter, check, locks);
                        issued = attempt.Issued; anyIssued |= issued;
                        if (attempt.Fault != null) throw new CandidateObservationException(attempt.Fault);
                        CandidateExecution.VerifySourceReadback(check, attempt);
                        current = attempt.After!;
                        children.Add(new BatchItem(index, request.Items[index].SourceName, Result(release, tool, id,
                            new JsonObject { ["source"] = JsonNode.Parse(V4Json.Serialize(CandidateExecution.SourceTarget(current, request.Items[index]))),
                                ["nativeResult"] = attempt.NativeResult == null ? null : JsonNode.Parse(V4Json.Serialize(attempt.NativeResult)),
                                ["observation"] = attempt.Observation == null ? null : new JsonObject { ["source"] = "inventory-delta-v14sp1", ["objects"] = JsonNode.Parse(V4Json.Serialize(attempt.Observation)) },
                                ["verifiedAbsent"] = request.Items[index].Action == "delete", ["nativeCalls"] = 1 }, null, Outcome.Succeeded, Execution.Completed)));
                        issued = false;
                    }
                    data["items"] = JsonNode.Parse(V4Json.Serialize(new BatchData(children)))!["items"]!.DeepClone();
                    result = Result(release, tool, id, data, null, Outcome.Succeeded, Execution.Completed);
                }
            }
            catch (Exception ex)
            {
                Error error = Map(ex, expectedPlanHash);
                if (issued) { RequiresSessionReset = true; error = new Error("External-source outcome is unknown; inspect the project and rebuild the session. Never replay.", new OutcomeUnknownDetails("native-source-or-readback", new Dictionary<string, JsonElement>())); }
                if (mode == "apply" && data != null && index < request.Items.Length)
                {
                    children.Add(new BatchItem(index, request.Items[index].SourceName, Result(release, tool, id,
                        new JsonObject { ["nativeCalls"] = issued ? 1 : 0, ["readback"] = attempt?.After == null ? null : JsonNode.Parse(V4Json.Serialize(attempt.After)) }, error,
                        issued ? Outcome.Unknown : Outcome.RejectedBeforeOperation, issued ? Execution.Unknown : Execution.NotStarted)));
                    for (int i = index + 1; i < request.Items.Length; i++) children.Add(new BatchItem(i, request.Items[i].SourceName, Result(release, tool, id, null,
                        new Error("A previous external-source item stopped the batch.", new NotExecutedDetails(index)), Outcome.RejectedBeforeOperation, Execution.NotStarted)));
                    data["items"] = JsonNode.Parse(V4Json.Serialize(new BatchData(children)))!["items"]!.DeepClone();
                    if (!issued && children.Any(c => c.Result.Ok)) error = new Error("The source batch stopped after verified writes.", new PartialFailureDetails(children.Count(c => c.Result.Ok), 1, request.Items.Length - index - 1));
                }
                var outcome = issued ? Outcome.Unknown : children.Any(c => c.Result.Ok) ? Outcome.Partial : Outcome.RejectedBeforeOperation;
                result = Result(release, tool, id, data, error, outcome, issued ? Execution.Unknown : outcome == Outcome.Partial ? Execution.Partial : Execution.NotStarted);
            }
            foreach (var stream in locks.Values)
                try { stream.Dispose(); }
                catch (Exception) /* swallow(cleanup): expose uncertain completion after a native call or a pre-operation IO failure */
                {
                    RequiresSessionReset |= anyIssued;
                    result = Result(release, tool, id, data, new Error("Source input lock cleanup failed.", anyIssued
                        ? (ErrorDetails)new OutcomeUnknownDetails("source-lock-cleanup", new Dictionary<string, JsonElement>()) : new IoFailedDetails("close-input", null)),
                        anyIssued ? Outcome.Unknown : Outcome.RejectedBeforeOperation, anyIssued ? Execution.Unknown : Execution.NotStarted);
                }
            return result;
        }
        public static Error Map(Exception ex, string hash = "")
        {
            if (ex is SourceRejection r) return r.Error;
            if (ex is PlcPathException p) return new Error(p.Message, p.Ambiguous ? (ErrorDetails)new TargetAmbiguousDetails(p.Path, p.Candidates) : new NotFoundDetails(p.Path));
            if (ex is CandidateObservationException e)
                return new Error("External-source precondition was not established.", e.Fault.Kind == "not-found" ? (ErrorDetails)new NotFoundDetails(e.Fault.Subject)
                    : e.Fault.Kind == "exists" ? new AlreadyExistsDetails(e.Fault.Subject) : e.Fault.Kind == "identity" ? new IdentityMismatchDetails(e.Fault.Subject, null, null)
                    : e.Fault.Kind == "stale" ? new PlanStaleDetails(hash, e.Fault.Subject) : e.Fault.Kind == "unsupported" ? new UnsupportedCapabilityDetails(null, "P6-SOURCE", e.Fault.Subject)
                    : new PreconditionFailedDetails(e.Fault.Subject, null));
            return new Error("The source preflight failed; no native call was issued for this item.", ex is IOException || ex is UnauthorizedAccessException ? (ErrorDetails)new IoFailedDetails("read-source", null) : new PreconditionFailedDetails("source-preflight", null));
        }
        private static void Refuse(ErrorDetails details) => throw new SourceRejection(new Error("The reviewed external-source operation is refused.", details));
        public static Envelope Result(string release, string tool, string id, JsonObject? data, Error? error, Outcome outcome, Execution execution)
            => Envelope.Create(data, error, new Meta(DateTimeOffset.UtcNow, release, tool, Meta.Correlate(id), outcome, execution,
                outcome == Outcome.Unknown || error?.Code == ErrorCode.SessionResetRequired, BehaviorPolicy.SafeV4,
                outcome == Outcome.Unknown ? Completeness.Unknown : outcome == Outcome.Succeeded ? Completeness.Complete : outcome == Outcome.Partial ? Completeness.Partial : Completeness.None, null, Array.Empty<Warning>()));
    }
}
