using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts.Candidates;

namespace TiaMcp.Logic.V4
{
    public sealed class SaveCloseSession
    {
        private readonly Dictionary<string, Plan> plans = new Dictionary<string, Plan>(StringComparer.Ordinal);
        private readonly HashSet<string> consumed = new HashSet<string>(StringComparer.Ordinal);
        public bool RequiresSessionReset { get; private set; }
        public Envelope Run(ISaveCloseAdapter adapter, string release, string tool, string id, SaveCloseRequest request,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "", bool confirmDiscard = false)
        {
            JsonObject? data = null; SaveCloseAttempt? attempt = null; bool dispatched = false;
            try
            {
                if (RequiresSessionReset) Refuse(new SessionResetRequiredDetails("save-close-unknown"));
                if (mode != "preview" && mode != "apply") Invalid("mode");
                if (!SaveCloseContract.Entries.Contains(tool) || request.Action != SaveCloseContract.Action(tool)) Invalid("action");
                if (request.SaveChanges) Refuse(new PreconditionFailedDetails("save-explicitly-before-close", null));
                if (request.Action != "close" && request.DiscardChanges) Invalid("discardChanges");
                if (request.Action != "save-copy" && request.NewProjectPath.Length > 0) Invalid("newProjectPath");
                if (request.Action == "save-copy")
                {
                    request.NewProjectPath = SessionCandidateSession.PathValue(request.NewProjectPath);
                    if (Directory.Exists(request.NewProjectPath) || File.Exists(request.NewProjectPath)) Refuse(new AlreadyExistsDetails(request.NewProjectPath));
                    var parent = new DirectoryInfo(Path.GetDirectoryName(request.NewProjectPath)!);
                    if (!parent.Exists) Refuse(new NotFoundDetails(parent.FullName));
                    for (var d = parent; d != null; d = d.Parent)
                        if ((d.Attributes & FileAttributes.ReparsePoint) != 0) Invalid("newProjectPath");
                }
                if (mode == "apply" && !confirm) Refuse(new ConfirmationRequiredDetails(expectedPlanHash.Length == 64 ? expectedPlanHash : null));
                if (mode == "apply" && request.DiscardChanges && !confirmDiscard)
                    Refuse(new ConfirmationRequiredDetails(expectedPlanHash.Length == 64 ? expectedPlanHash : null), "Discard needs its own confirmDiscard=true confirmation.");
                if (mode == "apply" && (expectedPlanHash.Length != 64 || expectedPlanHash.Any(c => !"0123456789abcdef".Contains(c)))) Invalid("expectedPlanHash");
                var before = adapter.Observe(); ValidateObservation(before);
                data = new JsonObject { ["observedState"] = JsonNode.Parse(V4Json.Serialize(before)), ["nativeIssued"] = false };
                var binding = before.Binding;
                if (!binding.ProcessId.HasValue || !binding.ProcessStartUtc.HasValue) Refuse(new ProjectNotBoundDetails());
                if (binding.Ownership == "unknown") Refuse(new PreconditionFailedDetails("known-ownership", binding.ProjectFile));
                if (request.Action == "disconnect")
                {
                    if (!before.DisconnectSupported) Refuse(new UnsupportedCapabilityDetails(release, "non-owning-portal-detach", "disconnect"));
                    if (binding.ProjectFile != null && binding.Ownership == "owned") Refuse(new PreconditionFailedDetails("explicit-close-before-disconnect", binding.ProjectFile));
                }
                else
                {
                    if (binding.ProjectFile == null) Refuse(new ProjectNotBoundDetails());
                    if (before.ObjectValidity != "valid" || !before.Dirty.HasValue) Refuse(new PreconditionFailedDetails("known-project-validity-and-dirty-state", binding.ProjectFile));
                    if (request.Action == "save-copy" && before.LocalSession) Refuse(new UnsupportedCapabilityDetails(release, "local-session-copy", "save-copy"));
                    if (request.Action == "close")
                    {
                        if (binding.Ownership != "owned") Refuse(new PreconditionFailedDetails("owned-project-for-close", binding.ProjectFile));
                        if (before.Dirty == true && !request.DiscardChanges) Refuse(new PreconditionFailedDetails("save-first-or-explicit-discard", binding.ProjectFile));
                    }
                }
                var check = new SaveCloseCheck { Request = request, Before = before }; check.Digest = SaveClosePrimitives.Digest(check);
                var identity = new PlanIdentity(binding.ProcessId, binding.ProcessStartUtc, binding.ProjectFile, binding.Epoch, null, Array.Empty<PlanFile>());
                var args = JsonSerializer.SerializeToElement(JsonNode.Parse(V4Json.Serialize(request)));
                string argumentsHash = DeviceCreationSession.Hash(args), inventoryHash = DeviceCreationSession.Hash(before);
                string native = request.Action == "disconnect" ? "TiaPortal.Dispose" : request.Action == "save-copy" ? "Project.SaveAs"
                    : (before.LocalSession ? "LocalSession." : "Project.") + (request.Action == "save" ? "Save" : "Close");
                var operationArgs = new JsonObject { ["nativeCall"] = native, ["ownership"] = binding.Ownership, ["dirty"] = before.Dirty,
                    ["discardChanges"] = request.DiscardChanges, ["saveChanges"] = false, ["newProjectPath"] = request.NewProjectPath };
                var operations = new[] { new PlanOperation(tool, binding.ProjectFile, JsonSerializer.SerializeToElement(operationArgs)) };
                var warnings = Array.Empty<Warning>(); var inputHashes = new Dictionary<string, string>();
                string hash = DeviceCreationSession.Hash(new { releaseKey = release, tool, argumentsHash, identity, inputHashes, inventoryHash, operations, warnings });
                var plan = new Plan(hash, release, tool, argumentsHash, identity, inputHashes, inventoryHash, operations, warnings);
                data["plan"] = JsonNode.Parse(V4Json.Serialize(plan));
                if (mode == "preview")
                {
                    if (plans.Count >= 128 && !plans.ContainsKey(hash)) Refuse(new LimitExceededDetails("plans", 128, plans.Count + 1));
                    plans[hash] = plan; return Result(release, tool, id, data, null, Outcome.Succeeded, Execution.ReadOnly);
                }
                if (!plans.TryGetValue(expectedPlanHash, out var reviewed) || consumed.Contains(expectedPlanHash)) Stale(expectedPlanHash);
                if (DeviceCreationSession.Hash(reviewed!.Identity) != DeviceCreationSession.Hash(identity)) Mismatch();
                if ((binding.ProjectFile == null ? expectedProjectFile : SessionCandidateSession.PathValue(expectedProjectFile)) != (binding.ProjectFile ?? "")) Mismatch();
                if (hash != expectedPlanHash) Stale(expectedPlanHash);
                consumed.Add(expectedPlanHash); dispatched = true;
                attempt = adapter is ISaveCloseBoundary boundary ? boundary.Execute(check) : CandidateExecution.SaveClose(adapter, check);
                dispatched = attempt.Issued; data["nativeIssued"] = attempt.Issued;
                data["observedState"] = ObservationData(attempt.After);
                if (attempt.Fault != null)
                {
                    if (attempt.Issued || attempt.RequiresSessionReset) throw new IOException("Unconfirmed native save/close.");
                    if (attempt.Fault.Kind == "identity") Mismatch();
                    if (attempt.Fault.Kind == "stale") Stale(expectedPlanHash);
                    Refuse(new PreconditionFailedDetails(attempt.Fault.Subject, binding.ProjectFile));
                }
                CandidateExecution.VerifySaveCloseReadback(check, attempt.After!);
                return Result(release, tool, id, data, null, Outcome.Succeeded, Execution.Completed);
            }
            catch (Exception error)
            {
                if (dispatched || attempt?.RequiresSessionReset == true)
                {
                    RequiresSessionReset = true; adapter.MarkUncertain();
                    if (data == null) data = new JsonObject();
                    data["observedState"] = ObservationData(attempt?.After);
                    var evidence = new Dictionary<string, JsonElement> { ["observedState"] = JsonSerializer.SerializeToElement(data["observedState"]),
                        ["nativeCalls"] = JsonSerializer.SerializeToElement(1), ["retry"] = JsonSerializer.SerializeToElement("never") };
                    return Result(release, tool, id, data, new Error("Save/close outcome is unknown. Inspect binding, object validity and worker cleanup, then reset the session; do not replay.",
                        new OutcomeUnknownDetails("save-close-native-or-readback", evidence)), Outcome.Unknown, Execution.Unknown);
                }
                var rejection = error is SessionCandidateRejection rejected ? rejected.Error
                    : error is CandidateObservationException observed ? new Error("Save/close observation refused.", observed.Fault.Kind == "identity" ? (ErrorDetails)new IdentityMismatchDetails("save-close-binding", null, null) : new PreconditionFailedDetails(observed.Fault.Subject, null))
                    : new Error("Save/close preflight failed; no native action was issued.", new PreconditionFailedDetails("save-close-preflight", null));
                if (data == null) data = new JsonObject { ["nativeIssued"] = false, ["observedState"] = ObservationData(null) };
                return Result(release, tool, id, data, rejection, Outcome.RejectedBeforeOperation, Execution.NotStarted);
            }
        }
        private static JsonNode ObservationData(SaveCloseObservation? observation) => observation == null
            ? new JsonObject { ["binding"] = null, ["dirty"] = null, ["objectValidity"] = "unavailable", ["workerCleanup"] = "unavailable" }
            : JsonNode.Parse(V4Json.Serialize(observation))!;
        public static void ValidateObservation(SaveCloseObservation observation)
        {
            if (observation == null || observation.Binding == null || observation.Binding.Epoch < 0 || observation.Binding.WorkerEpoch < 0
                || !new[] { "none", "owned", "borrowed", "unknown" }.Contains(observation.Binding.Ownership)
                || !new[] { "valid", "invalid", "absent", "unavailable" }.Contains(observation.ObjectValidity)
                || !new[] { "not-requested", "deferred-until-channel-close", "detached", "unavailable" }.Contains(observation.WorkerCleanup)
                || observation.Binding.ProcessId.HasValue != observation.Binding.ProcessStartUtc.HasValue
                || observation.Binding.ProcessId <= 0 || observation.Binding.ProcessStartUtc?.Offset != null && observation.Binding.ProcessStartUtc.Value.Offset != TimeSpan.Zero)
                throw new InvalidDataException("Incomplete save/close observation.");
            if (observation.Binding.ProjectFile != null) observation.Binding.ProjectFile = SessionCandidateSession.PathValue(observation.Binding.ProjectFile);
        }
        public static Envelope Result(string release, string tool, string id, JsonObject? data, Error? error, Outcome outcome, Execution execution)
            => DeviceCreationSession.Result(release, tool, id, data, error, outcome, execution);
        private static void Invalid(string parameter) => Refuse(new InvalidArgumentDetails(parameter, Array.Empty<string>()));
        private static void Mismatch() => Refuse(new IdentityMismatchDetails("save-close-binding", null, null));
        private static void Stale(string hash) => Refuse(new PlanStaleDetails(hash, "missing-consumed-or-changed-save-close-plan"));
        private static void Refuse(ErrorDetails details, string message = "Save/close candidate preconditions are not satisfied.") => throw new SessionCandidateRejection(new Error(message, details));
    }
}
