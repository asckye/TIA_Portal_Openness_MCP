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
    public sealed class FallbackSession
    {
        private readonly Dictionary<string, Plan> plans = new Dictionary<string, Plan>(StringComparer.Ordinal);
        private readonly HashSet<string> consumed = new HashSet<string>(StringComparer.Ordinal);
        private readonly byte[] secret = new byte[32];
        public bool RequiresSessionReset { get; private set; }
        public FallbackSession() { using var random = RandomNumberGenerator.Create(); random.GetBytes(secret); }
        private string Credential(string value) { using var hmac = new HMACSHA256(secret); return CandidatePrimitives.ByteHash(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))); }
        public Envelope Run(IFallbackAdapter adapter, string release, string tool, string id, FallbackRequest request, string credential = "",
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
        {
            JsonObject? data = null; FallbackAttempt? attempt = null; bool dispatched = false, boundaryEntered = false;
            try
            {
                if (RequiresSessionReset) Refuse(new SessionResetRequiredDetails("fallback-unknown"));
                if (!FallbackContract.Entries.Contains(tool) || request.Entry != tool || mode != "preview" && mode != "apply" || request.RetryPolicy != "never")
                    Refuse(new InvalidArgumentDetails("fallback-request", Array.Empty<string>()));
                if (mode == "apply" && !confirm) Refuse(new ConfirmationRequiredDetails(expectedPlanHash));
                int refreshes = 0; FallbackObservation before;
                try { before = adapter.Observe(); }
                catch (ReadHandleStaleException stale) when (request.RefreshReadHandle && stale.ReadOnlyObject && stale.NothingExecuted)
                { refreshes = 1; adapter.RefreshReadHandle(); before = adapter.Observe(); }
                FallbackPrimitives.Validate(before);
                data = new JsonObject { ["observedState"] = JsonNode.Parse(V4Json.Serialize(before)), ["attempt"] = null, ["readHandleRefreshes"] = refreshes };
                var check = new FallbackCheck { Request = request, Before = before, InitialReadRefreshes = refreshes }; check.Digest = FallbackPrimitives.Digest(check);
                if (adapter.RequiresOffline && before.OfflineState == "online") Refuse(new OfflineRequiredDetails(before.OfflineTargets));
                if (adapter.RequiresOffline && before.OfflineState != "offline") Refuse(new PreconditionFailedDetails("positive-offline-evidence", null));
                // Empty-route preview discovers exact routes without applying configuration or issuing an operation.
                if (request.Route.Length == 0 && mode == "preview") return Result(release, tool, id, data, null, Outcome.Succeeded, Execution.ReadOnly);
                FallbackPrimitives.Admission(adapter, check); var b = before.Binding;
                var identity = new PlanIdentity(b.ProcessId, b.ProcessStartUtc, b.ProjectFile, b.Epoch, null, Array.Empty<PlanFile>());
                string argumentsHash = DeviceCreationSession.Hash(new { request, credential = Credential(credential) }), inventoryHash = DeviceCreationSession.Hash(before);
                var route = before.Routes.Single(r => r.Id == request.Route);
                var operations = new[] { new PlanOperation(tool, route.TargetId, JsonSerializer.SerializeToElement(new { route = route.Id, retryPolicy = "never",
                    refreshReadHandle = request.RefreshReadHandle, nativeCalls = route.NativeCalls })) };
                var inputHashes = new Dictionary<string, string>(); var warnings = Array.Empty<Warning>();
                string hash = DeviceCreationSession.Hash(new { releaseKey = release, tool, argumentsHash, identity, inputHashes, inventoryHash, operations, warnings });
                var plan = new Plan(hash, release, tool, argumentsHash, identity, inputHashes, inventoryHash, operations, warnings);
                data["plan"] = JsonNode.Parse(V4Json.Serialize(plan));
                if (mode == "preview")
                {
                    if (plans.Count >= 128 && !plans.ContainsKey(hash)) Refuse(new LimitExceededDetails("plans", 128, plans.Count + 1));
                    plans[hash] = plan; return Result(release, tool, id, data, null, Outcome.Succeeded, Execution.ReadOnly);
                }
                if (!plans.TryGetValue(expectedPlanHash, out var reviewed) || consumed.Contains(expectedPlanHash)) Refuse(new PlanStaleDetails(expectedPlanHash, "missing-or-consumed-fallback-plan"));
                if (DeviceCreationSession.Hash(reviewed!.Identity) != DeviceCreationSession.Hash(identity) || CandidatePrimitives.CanonicalProject(expectedProjectFile) != b.ProjectFile)
                    Refuse(new IdentityMismatchDetails("fallback-binding", null, null));
                if (hash != expectedPlanHash) Refuse(new PlanStaleDetails(expectedPlanHash, "changed-fallback-plan"));
                consumed.Add(expectedPlanHash); dispatched = adapter.Writes || adapter.NeedsConfiguration;
                boundaryEntered = true;
                attempt = adapter is IFallbackBoundary boundary ? boundary.Execute(check) : CandidateExecution.Fallback(adapter, check);
                // Transport replies must agree with the exact planned operation and retained native evidence.
                if (attempt == null || attempt.HandleRefreshes < 0 || attempt.HandleRefreshes > 1
                    || attempt.HandleRefreshes < check.InitialReadRefreshes
                    || attempt.HandleRefreshes != 0 && (!request.RefreshReadHandle || !attempt.ReadOnlyStaleObject || !attempt.NothingExecuted || attempt.ConfigurationIssued && check.InitialReadRefreshes != 1)
                    || attempt.ConfigurationResult.HasValue && !attempt.ConfigurationIssued
                    || attempt.ConfigurationIssued && !adapter.NeedsConfiguration || attempt.OperationIssued && adapter.NeedsConfiguration && attempt.ConfigurationResult != true
                    || attempt.ConfigurationIssued && !attempt.WriteIssued || attempt.WriteIssued && !attempt.ConfigurationIssued && !adapter.Writes
                    || attempt.Fault == null && (!attempt.OperationIssued || attempt.NativeResult == null || attempt.After == null)
                    || attempt.RequiresSessionReset && !attempt.WriteIssued)
                    throw new InvalidOperationException("Invalid fallback attempt evidence.");
                dispatched = attempt.WriteIssued; data["attempt"] = JsonNode.Parse(V4Json.Serialize(attempt));
                if (attempt.After != null) FallbackPrimitives.Verify(check, attempt.After, true);
                if (attempt.RequiresSessionReset)
                {
                    RequiresSessionReset = true; adapter.MarkUncertain();
                    return Result(release, tool, id, data, Unknown(attempt), Outcome.Unknown, Execution.Unknown);
                }
                if (attempt.Fault != null)
                {
                    if (!attempt.ConfigurationIssued && !attempt.OperationIssued) return Result(release, tool, id, data, Map(attempt.Fault, expectedPlanHash), Outcome.RejectedBeforeOperation, Execution.NotStarted);
                    return Result(release, tool, id, data, new Error("The native operation failed; no fallback or replay was attempted.",
                        new NativeOperationFailedDetails(null, null, Evidence(attempt))), adapter.Writes || attempt.ConfigurationIssued ? Outcome.Failed : Outcome.ReadFailed,
                        adapter.Writes || attempt.ConfigurationIssued ? Execution.Completed : Execution.ReadOnly);
                }
                bool success = attempt.NativeResult!.Success;
                return Result(release, tool, id, data, success ? null : new Error("Native result reported failure.", new NativeOperationFailedDetails(null, null, Evidence(attempt))),
                    success ? Outcome.Succeeded : adapter.Writes ? Outcome.Failed : Outcome.ReadFailed, adapter.Writes ? Execution.Completed : Execution.ReadOnly);
            }
            catch (Exception error)
            {
                if (error is FallbackNotDispatchedException)
                {
                    dispatched = false; boundaryEntered = false;
                    attempt = new FallbackAttempt { Stage = "channel-not-dispatched", Fault = new CandidateFault { Kind = "precondition", Subject = "channel-not-dispatched" } };
                    if (data != null) data["attempt"] = JsonNode.Parse(V4Json.Serialize(attempt));
                }
                if (dispatched)
                {
                    RequiresSessionReset = true; adapter.MarkUncertain();
                    return Result(release, tool, id, data, Unknown(attempt), Outcome.Unknown, Execution.Unknown);
                }
                if (boundaryEntered)
                    return Result(release, tool, id, data, new Error("The read boundary did not return valid native evidence.", new InternalErrorDetails(null)), Outcome.ReadFailed, Execution.ReadOnly);
                var refusal = error is SessionCandidateRejection rejected ? rejected.Error : error is CandidateObservationException observed ? Map(observed.Fault, expectedPlanHash)
                    : new Error("Fallback preflight could not establish the native state.", new PreconditionFailedDetails("fallback-preflight", null));
                return Result(release, tool, id, data, refusal, Outcome.RejectedBeforeOperation, Execution.NotStarted);
            }
        }
        private static Dictionary<string, JsonElement> Evidence(FallbackAttempt? a) => new Dictionary<string, JsonElement> { ["attempt"] = JsonSerializer.SerializeToElement(a), ["retryPolicy"] = JsonSerializer.SerializeToElement("never") };
        private static Error Unknown(FallbackAttempt? a) => new Error("Write outcome is unknown. Inspect the actual state and rebuild the session before further work; never replay automatically.", new OutcomeUnknownDetails(a?.Stage ?? "channel", Evidence(a)));
        public static Error Map(CandidateFault f, string hash) => new Error("Fallback prerequisites are not satisfied.",
            f.Kind == "offline" ? new OfflineRequiredDetails(new[] { f.Subject }) : f.Kind == "identity" ? new IdentityMismatchDetails(f.Subject, null, null)
            : f.Kind == "stale" ? new PlanStaleDetails(hash, f.Subject) : f.Kind == "invalid" ? new InvalidArgumentDetails(f.Subject, Array.Empty<string>())
            : new PreconditionFailedDetails(f.Subject, null));
        public static Envelope Result(string release, string tool, string id, JsonObject? data, Error? error, Outcome outcome, Execution execution)
            => Envelope.Create(data, error, new Meta(DateTimeOffset.UtcNow, release, tool, Meta.Correlate(id), outcome, execution,
                outcome == Outcome.Unknown || error?.Code == ErrorCode.SessionResetRequired, BehaviorPolicy.SafeV4,
                outcome == Outcome.Unknown ? Completeness.Unknown : data != null ? Completeness.Complete : Completeness.None, null, Array.Empty<Warning>()));
        private static void Refuse(ErrorDetails d) => throw new SessionCandidateRejection(new Error("Fallback prerequisites are not satisfied.", d));
    }
}
