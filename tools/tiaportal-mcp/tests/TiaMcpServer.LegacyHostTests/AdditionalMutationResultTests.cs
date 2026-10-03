using System;
using System.IO;
using System.Text.Json.Nodes;
using TiaMcp.LegacyHost;

// Integration: copy into LegacyHostTests and call Run(Check) from its runner.
// Pure protocol tests: never constructs WorkerClient or a Siemens object.
internal static class AdditionalMutationResultTests
{
    internal static void Run(Action<bool, string> check)
    {
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tia-review-project", "Expected.ap17"));
        var other = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tia-review-project", "Other.ap17"));
        JsonObject Args(bool preview = false) => new() { ["dryRun"] = preview, ["expectedProjectFile"] = expected };
        JsonObject Result(bool executed = true) => new() { ["Executed"] = executed, ["ProjectFile"] = expected };

        void Reject(string operation, string label, JsonNode? payload, bool preview = false)
        {
            Exception? failure = null;
            try { WorkerProtocol.ValidateExchangeResult(operation, Args(preview), payload); }
            catch (Exception ex) { failure = ex; }
            check(failure is IOException, operation + ": " + label + " must reject with IOException");
            if (failure == null) return;
            var state = new WorkerOutcomeState();
            state.Failed(true, failure);
            check(state.Poisoned, operation + ": " + label + " poisons a dispatched session");
            bool blocked = false;
            try { state.RequireUsable(); } catch (InvalidOperationException) { blocked = true; }
            check(blocked, operation + ": " + label + " prevents a subsequent request");
        }

        foreach (var operation in new[] { "CreateTagTable", "CreateTag", "CreateUserConstant" })
        {
            WorkerProtocol.ValidateExchangeResult(operation, Args(), Result());
            check(true, operation + ": matching execution accepted");
            WorkerProtocol.ValidateExchangeResult(operation, Args(true), Result(false));
            check(true, operation + ": matching preview accepted");
            Reject(operation, "missing result", null);
            Reject(operation, "missing fields", new JsonObject());
            var wrongProject = Result(); wrongProject["ProjectFile"] = other;
            Reject(operation, "wrong project", wrongProject);
            var noProject = Result(); noProject.Remove("ProjectFile");
            Reject(operation, "missing project", noProject);
            Reject(operation, "Executed=false for execution", Result(false));
            Reject(operation, "Executed=true for preview", Result(), true);
            var unknown = Result(); unknown["Executed"] = "unknown";
            Reject(operation, "unknown execution marker", unknown);
            var missingOutcome = Result(); missingOutcome.Remove("Executed");
            Reject(operation, "missing execution marker", missingOutcome);

            var unknownFailure = new WorkerOperationException("native result unknown", -32603, "unknown");
            check(!unknownFailure.KnownNoMutation, operation + ": unknown outcome preserved");
            var state = new WorkerOutcomeState(); state.Failed(true, unknownFailure);
            check(state.Poisoned, operation + ": unknown native outcome poisons session");
            bool blocked = false;
            try { state.RequireUsable(); } catch (InvalidOperationException) { blocked = true; }
            check(blocked, operation + ": unknown native outcome cannot be replayed");
        }
    }
}
