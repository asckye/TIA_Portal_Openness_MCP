using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts.Candidates;

namespace TiaMcp.Logic.V4
{
    public sealed class SessionCandidateRejection : Exception
    {
        public Error Error { get; }
        public SessionCandidateRejection(Error error) : base(error.Message) { Error = error; }
    }
    // The host owns plans and confirmation; native adapters own only handles and observations.
    public sealed class SessionCandidateSession
    {
        private readonly Dictionary<string, Plan> plans = new Dictionary<string, Plan>(StringComparer.Ordinal);
        private readonly HashSet<string> consumed = new HashSet<string>(StringComparer.Ordinal);
        public bool RequiresSessionReset { get; private set; }
        public Envelope Run(ISessionCandidateAdapter adapter, string release, string tool, string id, SessionRequest request,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "", bool confirmUpgrade = false)
        {
            JsonObject? data = null; SessionAttempt? attempt = null; bool dispatched = false;
            try
            {
                if (RequiresSessionReset) Refuse(new SessionResetRequiredDetails("session-action-unknown"));
                if (mode != "preview" && mode != "apply") Invalid("mode");
                if (request.ProcessId <= 0 || request.ProcessStartUtc == default) Invalid("processId/processStartUtc");
                if (request.StartNew) Refuse(new UnsupportedCapabilityDetails(release, "explicit-existing-process", "start"));
                if (request.Upgrade != "reject" && request.Upgrade != "allow") Invalid("upgrade");
                if (request.Action != "attach" && request.Action != "bind" && request.Action != "open") Invalid("action");
                request.ProcessStartUtc = request.ProcessStartUtc.ToUniversalTime();
                if (request.ProjectPath.Length > 0) request.ProjectPath = PathValue(request.ProjectPath);
                if (request.CopyPath.Length > 0) request.CopyPath = PathValue(request.CopyPath);
                if (request.Action == "attach" && (request.ProjectPath.Length > 0 || request.ReuseOpen || request.Upgrade != "reject" || request.CopyPath.Length > 0)) Invalid("attach-project-options");
                if (request.Action != "attach" && request.ProjectPath.Length == 0) Invalid("projectPath");
                if (request.Action != "open" && request.Upgrade != "reject" || request.Upgrade == "reject" && request.CopyPath.Length > 0) Invalid("upgrade/copyPath");
                if (request.Upgrade == "allow" && request.CopyPath.Length == 0) Invalid("copyPath");
                request.OpenedProjectFile = request.Action == "open" ? request.Upgrade == "allow"
                    ? PathValue(Path.ChangeExtension(request.CopyPath, ".ap" + (release == "14sp1" ? "14" : release == "15.1" ? "15_1" : release))) : request.ProjectPath : "";
                if (mode == "apply" && !confirm) Refuse(new ConfirmationRequiredDetails(expectedPlanHash.Length == 64 ? expectedPlanHash : null));
                if (mode == "apply" && request.Upgrade == "allow" && !confirmUpgrade) Refuse(new ConfirmationRequiredDetails(expectedPlanHash.Length == 64 ? expectedPlanHash : null), "Upgrade needs its own confirmUpgrade=true confirmation.");
                if (mode == "apply" && (expectedPlanHash.Length != 64 || expectedPlanHash.Any(c => !"0123456789abcdef".Contains(c)))) Invalid("expectedPlanHash");
                var before = adapter.Observe();
                ValidateObservation(before);
                data = new JsonObject { ["candidateProcesses"] = JsonNode.Parse(V4Json.Serialize(before.Processes)), ["observedState"] = JsonNode.Parse(V4Json.Serialize(before.State)), ["nativeIssued"] = false };
                var selected = before.Processes.SingleOrDefault(p => p.ProcessId == request.ProcessId);
                if (selected == null) Refuse(new NotFoundDetails("processId"));
                if (!selected!.Complete || selected.ProcessStartUtc != request.ProcessStartUtc) Mismatch();
                if (before.State.Ownership == "unknown") Refuse(new PreconditionFailedDetails("known-project-ownership", before.State.ProjectFile));
                if (before.State.ProjectFile != null) Refuse(new PreconditionFailedDetails("explicitly-unbound-session", before.State.ProjectFile),
                    before.State.Ownership == "borrowed" ? "A borrowed project/session is already open and bound; OpenProject cannot replace it. Disconnect and reconnect without attaching before opening another project."
                    : "A project/session is already open and bound; close it explicitly before opening another.");
                if (request.Action == "attach")
                { if (before.State.ProcessId != null) Refuse(new PreconditionFailedDetails("detached-session", null)); }
                else
                {
                    if (before.State.ProcessId != request.ProcessId || before.State.ProcessStartUtc != request.ProcessStartUtc) Mismatch();
                    if (request.Action == "bind")
                    {
                        if (!request.ReuseOpen) Refuse(new PreconditionFailedDetails("reuseOpen=true-for-explicit-borrowing", request.ProjectPath));
                        if (!selected.ProjectFiles.Contains(request.ProjectPath, StringComparer.OrdinalIgnoreCase)) Refuse(new NotFoundDetails(request.ProjectPath));
                    }
                    else
                    {
                        if (selected.ProjectFiles.Length > 0) Refuse(new PreconditionFailedDetails("empty-selected-process-before-open", request.ProjectPath), "A project/session is already open in the selected TIA Portal process; use AttachOpenProject to borrow it explicitly.");
                        string suffix = ".ap" + (release == "14sp1" ? "14" : release == "15.1" ? "15_1" : release);
                        bool local = Path.GetExtension(request.ProjectPath).StartsWith(".als", StringComparison.OrdinalIgnoreCase);
                        if (local && (!before.LocalSessionOpenSupported || release == "14sp1" || release == "15.1" || release == "16" || request.Upgrade == "allow"))
                            Refuse(new UnsupportedCapabilityDetails(release, "local-session", "open/upgrade"));
                        if (request.Upgrade == "reject" && !Path.GetExtension(request.ProjectPath).Equals(local ? suffix.Replace(".ap", ".als") : suffix, StringComparison.OrdinalIgnoreCase))
                            Refuse(new UnsupportedCapabilityDetails(release, "exact-project-version", "upgrade-rejected"));
                        if (request.Upgrade == "allow")
                        {
                            if (!before.UpgradeSupported) Refuse(new UnsupportedCapabilityDetails(release, "native-project-upgrade", "open"));
                            if (request.CopyPath.Length == 0) Invalid("copyPath");
                            if (!Path.GetExtension(request.ProjectPath).StartsWith(".ap", StringComparison.OrdinalIgnoreCase)
                                || !Path.GetFileName(request.ProjectPath).Equals(Path.GetFileName(request.CopyPath), StringComparison.OrdinalIgnoreCase)) Invalid("projectPath/copyPath");
                            string source = Path.GetDirectoryName(request.ProjectPath)! + "\\", copy = Path.GetDirectoryName(request.CopyPath)! + "\\";
                            if (source.StartsWith(copy, StringComparison.OrdinalIgnoreCase) || copy.StartsWith(source, StringComparison.OrdinalIgnoreCase)) Invalid("copyPath");
                            var target = Path.ChangeExtension(request.CopyPath, suffix);
                            if (File.Exists(target) && target != request.CopyPath) Refuse(new AlreadyExistsDetails(target));
                        }
                    }
                }
                var files = request.Action == "open" ? SessionPrimitives.Files(request.ProjectPath, request.CopyPath, request.Upgrade == "allow") : Array.Empty<CandidateFile>();
                if (request.Upgrade == "allow") VerifyCopy(request, files);
                var check = new SessionCheck { Request = request, Before = before, Files = files };
                check.Digest = SessionPrimitives.Digest(check);
                var identity = new PlanIdentity(request.ProcessId, request.ProcessStartUtc, before.State.ProjectFile, before.State.Epoch, null,
                    files.Select(f => new PlanFile(f.Path, f.Exists, f.ByteLength, f.Sha256)).ToArray());
                var args = JsonSerializer.SerializeToElement(JsonNode.Parse(V4Json.Serialize(request)));
                var argumentsHash = DeviceCreationSession.Hash(args);
                var inputHashes = files.ToDictionary(f => f.Path, f => f.Sha256!, StringComparer.Ordinal);
                var inventoryHash = DeviceCreationSession.Hash(before);
                var operations = new[] { new PlanOperation(tool, request.Action == "attach" ? null : request.Upgrade == "allow" ? request.CopyPath : request.ProjectPath, args) };
                var warnings = Array.Empty<Warning>();
                var hash = DeviceCreationSession.Hash(new { releaseKey = release, tool, argumentsHash, identity, inputHashes, inventoryHash, operations, warnings });
                var plan = new Plan(hash, release, tool, argumentsHash, identity, inputHashes, inventoryHash, operations, warnings);
                data["plan"] = JsonNode.Parse(V4Json.Serialize(plan));
                if (mode == "preview")
                {
                    if (plans.Count >= 128 && !plans.ContainsKey(hash)) Refuse(new LimitExceededDetails("plans", 128, plans.Count + 1));
                    plans[hash] = plan; return Result(release, tool, id, data, null, Outcome.Succeeded, Execution.ReadOnly);
                }
                if (!plans.TryGetValue(expectedPlanHash, out var reviewed) || consumed.Contains(expectedPlanHash)) Stale(expectedPlanHash);
                if (DeviceCreationSession.Hash(reviewed!.Identity) != DeviceCreationSession.Hash(identity)) Mismatch();
                string expected = request.Action == "attach" ? "" : request.Upgrade == "allow"
                    ? Path.ChangeExtension(request.CopyPath, ".ap" + (release == "14sp1" ? "14" : release == "15.1" ? "15_1" : release)) : request.ProjectPath;
                if ((expected.Length == 0 ? expectedProjectFile : PathValue(expectedProjectFile)) != (expected.Length == 0 ? expected : PathValue(expected))) Mismatch();
                if (hash != expectedPlanHash) Stale(expectedPlanHash);
                consumed.Add(expectedPlanHash); dispatched = true;
                attempt = adapter is ISessionCandidateBoundary boundary ? boundary.Execute(check) : CandidateExecution.Session(adapter, check);
                dispatched = attempt.Issued;
                data["nativeIssued"] = attempt.Issued;
                data["observedState"] = attempt.After == null ? new JsonObject { ["status"] = "unavailable" } : JsonNode.Parse(V4Json.Serialize(attempt.After));
                if (attempt.Fault != null)
                {
                    if (attempt.Issued) throw new IOException("Native session outcome is unconfirmed.");
                    if (attempt.Fault.Kind == "identity") Mismatch();
                    if (attempt.Fault.Kind == "stale") Stale(expectedPlanHash);
                    Refuse(new PreconditionFailedDetails(attempt.Fault.Subject, null));
                }
                CandidateExecution.VerifySessionReadback(check, attempt.After!);
                if (request.Upgrade == "allow")
                {
                    var original = SessionPrimitives.Files(request.ProjectPath, "", true);
                    if (CandidateDigest.Files(original) != CandidateDigest.Files(files.Where(f => f.Path.StartsWith(Path.GetDirectoryName(request.ProjectPath)! + "\\", StringComparison.OrdinalIgnoreCase))))
                        throw new IOException("Original project state changed during copy-only upgrade.");
                }
                return Result(release, tool, id, data, null, Outcome.Succeeded, Execution.Completed);
            }
            catch (Exception error)
            {
                if (dispatched || attempt?.RequiresSessionReset == true)
                {
                    RequiresSessionReset = true;
                    adapter.MarkUncertain();
                    var evidence = new Dictionary<string, JsonElement> { ["observedState"] = JsonSerializer.SerializeToElement(data?["observedState"]), ["nativeCalls"] = JsonSerializer.SerializeToElement(1) };
                    string? reason = attempt?.Reason ?? SessionPrimitives.ExceptionReason(error);
                    return Result(release, tool, id, data, new Error(reason == null ? "Session outcome is unknown. Inspect the observed state and reset the session; do not replay." : SessionPrimitives.ConfirmationGuidance,
                        new OutcomeUnknownDetails("session-native-or-readback", evidence, reason)), Outcome.Unknown, Execution.Unknown);
                }
                var rejection = error is SessionCandidateRejection rejected ? rejected.Error
                    : error is CandidateObservationException observed ? new Error("Session observation refused.", observed.Fault.Kind == "identity" ? (ErrorDetails)new IdentityMismatchDetails("session", null, null) : new PreconditionFailedDetails(observed.Fault.Subject, null))
                    : new Error("Session preflight failed; no native action was issued.", new PreconditionFailedDetails("session-preflight", null));
                return Result(release, tool, id, data, rejection, Outcome.RejectedBeforeOperation, Execution.NotStarted);
            }
        }
        public static string PathValue(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Any(char.IsControl) || !Path.IsPathRooted(path)
                || path.StartsWith("\\\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0 || path.Contains("..")) Invalid("projectPath");
            return CandidatePrimitives.CanonicalProject(path);
        }
        private static void VerifyCopy(SessionRequest request, CandidateFile[] files)
        {
            string source = Path.GetDirectoryName(request.ProjectPath)! + "\\", copy = Path.GetDirectoryName(request.CopyPath)! + "\\";
            var a = files.Where(f => f.Path.StartsWith(source, StringComparison.OrdinalIgnoreCase)).Select(f => new { path = f.Path.Substring(source.Length), f.ByteLength, f.Sha256 }).ToArray();
            var b = files.Where(f => f.Path.StartsWith(copy, StringComparison.OrdinalIgnoreCase)).Select(f => new { path = f.Path.Substring(copy.Length), f.ByteLength, f.Sha256 }).ToArray();
            if (a.Length == 0 || !files.Any(f => f.Path == request.ProjectPath) || !files.Any(f => f.Path == request.CopyPath) || DeviceCreationSession.Hash(a) != DeviceCreationSession.Hash(b))
                Refuse(new PreconditionFailedDetails("complete-identical-authorized-copy", request.CopyPath));
        }
        public static void ValidateObservation(SessionObservation observation)
        {
            if (observation == null || observation.State == null || observation.Processes == null || observation.Processes.Length > 1024
                || observation.Processes.Any(p => p == null || p.ProcessId <= 0 || p.ProjectFiles == null || p.ProjectFiles.Length > 256)
                || observation.Processes.Select(p => p.ProcessId).Distinct().Count() != observation.Processes.Length
                || !new[] { "none", "owned", "borrowed", "unknown" }.Contains(observation.State.Ownership)
                || observation.State.Epoch < 0 || observation.State.WorkerEpoch < 0) Refuse(new PreconditionFailedDetails("complete-session-observation", null));
            foreach (var p in observation!.Processes) p.ProjectFiles = p.ProjectFiles.Select(PathValue).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            observation.Processes = observation.Processes.OrderBy(p => p.ProcessId).ToArray();
            if (observation.State.ProjectFile != null) observation.State.ProjectFile = PathValue(observation.State.ProjectFile);
        }
        public static Envelope Result(string release, string tool, string id, JsonObject? data, Error? error, Outcome outcome, Execution execution)
            => DeviceCreationSession.Result(release, tool, id, data, error, outcome, execution);
        public static DateTimeOffset Start(string value)
        {
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
                || time.Offset != TimeSpan.Zero || !value.EndsWith("Z", StringComparison.Ordinal) && !value.EndsWith("+00:00", StringComparison.Ordinal)) Invalid("processStartUtc");
            return time;
        }
        private static void Invalid(string parameter) => Refuse(new InvalidArgumentDetails(parameter, Array.Empty<string>()));
        private static void Mismatch() => Refuse(new IdentityMismatchDetails("process-project-session", null, null));
        private static void Stale(string hash) => Refuse(new PlanStaleDetails(hash, "missing-consumed-or-changed-session-plan"));
        private static void Refuse(ErrorDetails details, string message = "Session candidate preconditions are not satisfied.") => throw new SessionCandidateRejection(new Error(message, details));
    }
}
