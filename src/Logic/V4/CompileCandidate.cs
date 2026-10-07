using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts.Candidates;

namespace TiaMcp.Logic.V4
{
    public sealed class CompileSession
    {
        private readonly Dictionary<string, Plan> plans = new Dictionary<string, Plan>(StringComparer.Ordinal);
        private readonly HashSet<string> consumed = new HashSet<string>(StringComparer.Ordinal);
        private readonly byte[] secret = new byte[32];
        public bool RequiresSessionReset { get; private set; }
        public CompileSession() { using var random = RandomNumberGenerator.Create(); random.GetBytes(secret); }
        private string Credential(string password) { using var hmac = new HMACSHA256(secret); return CandidatePrimitives.ByteHash(hmac.ComputeHash(Encoding.UTF8.GetBytes(password))); }
        public Envelope Run(ICompileAdapter adapter, string release, string tool, string id, CompileRequest request, string password = "",
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
        {
            JsonObject? data = null; CompileAttempt? attempt = null; bool dispatched = false;
            try
            {
                if (RequiresSessionReset) Refuse(new SessionResetRequiredDetails("compile-unknown"));
                if (!CompileContract.Entries.Contains(tool) || request.Entry != tool || mode != "preview" && mode != "apply") Refuse(new InvalidArgumentDetails("compile-request", Array.Empty<string>()));
                if (request.PasswordProvided != (password.Length > 0)) Refuse(new InvalidArgumentDetails("password", Array.Empty<string>()));
                if (mode == "apply" && !confirm) Refuse(new ConfirmationRequiredDetails(expectedPlanHash));
                var before = adapter.Observe(); CompilePrimitives.Validate(before);
                data = new JsonObject { ["observedState"] = JsonNode.Parse(V4Json.Serialize(before)), ["attempt"] = null };
                var check = new CompileCheck { Request = request, Before = before }; check.Digest = CompilePrimitives.Digest(check);
                CompilePrimitives.Admission(check);
                var b = before.Binding;
                var identity = new PlanIdentity(b.ProcessId, b.ProcessStartUtc, b.ProjectFile, b.Epoch, null, Array.Empty<PlanFile>());
                string argumentsHash = DeviceCreationSession.Hash(new { request, credential = Credential(password) }), inventoryHash = DeviceCreationSession.Hash(before);
                var calls = new List<string>(); bool login = request.PasswordProvided && before.SafetyPermission != "logged-on";
                if (login) calls.Add("SafetyAdministration.LoginToSafetyOfflineProgram"); calls.Add("ICompilable.Compile");
                if (login) calls.Add("SafetyAdministration.LogoffFromSafetyOfflineProgram");
                var op = new JsonObject { ["targetKind"] = before.TargetKind, ["targetId"] = before.TargetId, ["softwareId"] = before.SoftwareId,
                    ["offlinePolicy"] = "require", ["offlineState"] = before.OfflineState, ["safetyPermission"] = before.SafetyPermission,
                    ["passwordProvided"] = request.PasswordProvided, ["nativeCalls"] = new JsonArray(calls.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()) };
                var operations = new[] { new PlanOperation(tool, before.TargetName, JsonSerializer.SerializeToElement(op)) };
                var inputHashes = new Dictionary<string, string>(); var warnings = Array.Empty<Warning>();
                string hash = DeviceCreationSession.Hash(new { releaseKey = release, tool, argumentsHash, identity, inputHashes, inventoryHash, operations, warnings });
                var plan = new Plan(hash, release, tool, argumentsHash, identity, inputHashes, inventoryHash, operations, warnings);
                data["plan"] = JsonNode.Parse(V4Json.Serialize(plan));
                if (mode == "preview")
                {
                    if (plans.Count >= 128 && !plans.ContainsKey(hash)) Refuse(new LimitExceededDetails("plans", 128, plans.Count + 1));
                    plans[hash] = plan; return Result(release, tool, id, data, null, Outcome.Succeeded, Execution.ReadOnly);
                }
                if (!plans.TryGetValue(expectedPlanHash, out var reviewed) || consumed.Contains(expectedPlanHash)) Refuse(new PlanStaleDetails(expectedPlanHash, "missing-or-consumed-compile-plan"));
                if (DeviceCreationSession.Hash(reviewed!.Identity) != DeviceCreationSession.Hash(identity)
                    || CandidatePrimitives.CanonicalProject(expectedProjectFile) != b.ProjectFile) Refuse(new IdentityMismatchDetails("compile-binding", null, null));
                if (hash != expectedPlanHash) Refuse(new PlanStaleDetails(expectedPlanHash, "changed-compile-plan"));
                consumed.Add(expectedPlanHash); dispatched = true;
                attempt = adapter is ICompileBoundary boundary ? boundary.Execute(check, password) : CandidateExecution.Compile(adapter, check, password);
                dispatched = attempt.LoginIssued || attempt.CompileIssued || attempt.LogoutIssued;
                data["attempt"] = JsonNode.Parse(V4Json.Serialize(attempt)); data["observedState"] = JsonNode.Parse(V4Json.Serialize(attempt.After));
                if (attempt.RequiresSessionReset)
                {
                    RequiresSessionReset = true; adapter.MarkUncertain();
                    return Result(release, tool, id, data, new Error("Compile outcome is unknown. Inspect project and Safety state, reset the session and do not replay.",
                        new OutcomeUnknownDetails(attempt.Stage, Evidence(attempt))), Outcome.Unknown, Execution.Unknown, attempt);
                }
                if (attempt.Fault != null && !dispatched) return Result(release, tool, id, data, Map(attempt.Fault, release, expectedPlanHash), Outcome.RejectedBeforeOperation, Execution.NotStarted, attempt);
                if (attempt.Fault != null || attempt.CleanupFault != null)
                {
                    bool partial = attempt.CleanupFault != null && (attempt.Diagnostics != null && Good(attempt.Diagnostics)
                        || attempt.LoginCreated && attempt.After?.SafetyPermission == "logged-on");
                    var error = partial ? new Error("Safety cleanup failed; inspect the retained compile and permission evidence.", new PartialFailureDetails(0, 0, 0))
                        : new Error("A native compile or Safety step failed; the observed state is retained.", new NativeOperationFailedDetails(null, null, Evidence(attempt)));
                    return Result(release, tool, id, data, error, partial ? Outcome.Partial : Outcome.Failed, partial ? Execution.Partial : Execution.Completed, attempt);
                }
                var d = attempt.Diagnostics ?? throw new InvalidOperationException("Missing compile diagnostics.");
                if (!Good(d)) return Result(release, tool, id, data, CompileResultMapping.Errors(new JsonObject { ["state"] = d.State, ["errorCount"] = d.RootErrorCount, ["warningCount"] = d.RootWarningCount, ["diagnosticMessages"] = JsonNode.Parse(V4Json.Serialize(d.Messages)) }) ?? new Error("Compilation finished with inconsistent root and leaf diagnostic counts.",
                    new NativeOperationFailedDetails(null, null, Evidence(attempt))), Outcome.Failed, Execution.Completed, attempt);
                return Result(release, tool, id, data, null, Outcome.Succeeded, Execution.Completed, attempt);
            }
            catch (Exception error)
            {
                if (dispatched)
                {
                    RequiresSessionReset = true; adapter.MarkUncertain();
                    return Result(release, tool, id, data, new Error("Native compile state is unavailable; reset the session and do not replay.",
                        new OutcomeUnknownDetails("compile-channel-or-readback", Evidence(attempt))), Outcome.Unknown, Execution.Unknown, attempt);
                }
                var refusal = error is SessionCandidateRejection rejected ? rejected.Error : error is CandidateObservationException observed ? Map(observed.Fault, release, expectedPlanHash)
                    : new Error("Compile preflight failed; no native action was issued.", new PreconditionFailedDetails("compile-preflight", null));
                return Result(release, tool, id, data, refusal, Outcome.RejectedBeforeOperation, Execution.NotStarted);
            }
        }
        private static bool Good(CompileDiagnostics d) => d.CountsConsistent && d.RootErrorCount == 0 && d.LeafErrorCount == 0 && (d.State == "Success" || d.State == "Warning");
        private static Dictionary<string, JsonElement> Evidence(CompileAttempt? attempt) => new Dictionary<string, JsonElement> {
            ["attempt"] = JsonSerializer.SerializeToElement(attempt), ["retry"] = JsonSerializer.SerializeToElement("never") };
        public static Error Map(CandidateFault fault, string release, string hash)
        {
            ErrorDetails details = fault.Kind == "offline" ? new OfflineRequiredDetails(new[] { fault.Subject })
                : fault.Kind == "authentication" ? new AuthenticationRequiredDetails(fault.Subject)
                : fault.Kind == "unsupported" ? new UnsupportedCapabilityDetails(release, fault.Subject, "compile")
                : fault.Kind == "identity" ? new IdentityMismatchDetails(fault.Subject, null, null)
                : fault.Kind == "stale" ? new PlanStaleDetails(hash, fault.Subject)
                : fault.Kind == "invalid" ? new InvalidArgumentDetails(fault.Subject, Array.Empty<string>())
                : new PreconditionFailedDetails(fault.Subject, null);
            return new Error("Compile candidate preconditions are not satisfied.", details);
        }
        public static Envelope Result(string release, string tool, string id, JsonObject? data, Error? error, Outcome outcome, Execution execution, CompileAttempt? attempt = null)
        {
            var warnings = attempt?.CleanupFault == null ? Array.Empty<Warning>() : new[] { new Warning(WarningCode.CleanupFailed,
                "Safety cleanup failed; retained evidence describes the observed permission state.", Evidence(attempt)) };
            return Envelope.Create(data, error, new Meta(DateTimeOffset.UtcNow, release, tool, Meta.Correlate(id), outcome, execution,
                outcome == Outcome.Unknown || error?.Code == ErrorCode.SessionResetRequired, BehaviorPolicy.SafeV4,
                outcome == Outcome.Unknown ? Completeness.Unknown : data != null ? Completeness.Complete : Completeness.None, null, warnings));
        }
        private static void Refuse(ErrorDetails details) => throw new SessionCandidateRejection(new Error("Compile candidate preconditions are not satisfied.", details));
    }
}
