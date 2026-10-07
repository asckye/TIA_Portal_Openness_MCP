using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Logic.V4
{
    public static class PlcBatchImportResultMapping
    {
        public static Envelope Result(JsonObject data, string release, string tool, string id, bool preview)
        {
            var evidence = new Dictionary<string, JsonElement>();
            if (data["recoveryDirectory"] is JsonValue directory) evidence["recoveryDirectory"] = JsonSerializer.SerializeToElement(directory.GetValue<string>());
            Error Unknown() => new Error("Import outcome is unknown; inspect the recovery directory and remaining changes before establishing a new session.", new OutcomeUnknownDetails("batch-import", evidence));
            Error Failed() => new Error("Batch import stopped; inspect restoration and remaining-change evidence.", new NativeOperationFailedDetails(null, null, evidence));
            var items = data["items"]!.AsArray(); var mapped = new JsonArray();
            var blockers = items.OfType<JsonObject>().Where(x => (string?)x["status"] == "replace-blocked").ToArray();
            Error Blocked() => new Error("Batch replacement backup is blocked: " + string.Join(", ", blockers.Select(x => (string?)x["planned"]?["name"] + " (" + (string?)x["failure"] + ")"))
                + ". Run CompilePlcSoftware first and resolve protection before previewing overwrite again.", new PreconditionFailedDetails("replacement-backup", string.Join(", ", blockers.Select(x => (string?)x["planned"]?["name"]))));
            int succeeded = 0, failed = 0, skipped = 0, rolledBack = 0; int? cause = null;
            bool unknown = (bool?)data["nativeOutcomeUnknown"] == true;
            bool backupFailed = !string.IsNullOrEmpty((string?)data["recoveryFailure"]);
            foreach (var item in items.OfType<JsonObject>())
            {
                string? state = (string?)item["status"];
                bool restored = (string?)item["restoreStatus"] == "restored";
                if (restored) rolledBack++;
                var outcome = blockers.Length > 0 ? Outcome.RejectedBeforeOperation : state == "not-attempted" || state == "failed" && (bool?)item["attempted"] == false ? Outcome.RejectedBeforeOperation
                    : state == "failed" && (string?)item["failure"] == "native-outcome-uncertain" ? Outcome.Unknown
                    : state == "failed" || restored ? Outcome.Failed : Outcome.Succeeded;
                if (outcome == Outcome.Succeeded) succeeded++; else if (state == "not-attempted") skipped++; else { failed++; cause ??= mapped.Count; }
                unknown |= outcome == Outcome.Unknown;
                Error? error = blockers.Length > 0 ? Blocked() : outcome == Outcome.Unknown ? Unknown() : restored ? new Error("Replacement was rolled back from its recovery export.", new NativeOperationFailedDetails(null, null, evidence))
                    : backupFailed && state == "failed" ? new Error("Recovery failed for this replacement before any import.", new PreconditionFailedDetails("recovery-export", (string?)item["relativePath"]))
                    : state == "not-attempted" ? new Error("Not executed after a batch admission failure.", new NotExecutedDetails(cause)) : outcome != Outcome.Succeeded ? Failed() : null;
                Execution execution = outcome == Outcome.RejectedBeforeOperation ? Execution.NotStarted : outcome == Outcome.Unknown ? Execution.Unknown : preview ? Execution.ReadOnly : Execution.Completed;
                var child = Envelope.Create(item, error, new Meta(DateTimeOffset.UtcNow, release, tool, id, outcome, execution, outcome == Outcome.Unknown,
                    BehaviorPolicy.Current, outcome == Outcome.Unknown ? Completeness.Unknown : Completeness.Complete, null, new[] { new Warning(WarningCode.UnverifiedBehavior, "Native batch acceptance is pending.", new Dictionary<string, JsonElement>()) }));
                mapped.Add(new JsonObject { ["index"] = mapped.Count, ["target"] = item["relativePath"]?.DeepClone(), ["result"] = JsonNode.Parse(V4Json.Serialize(child)) });
            }
            data["items"] = mapped;
            var aggregate = blockers.Length > 0 ? Outcome.RejectedBeforeOperation : unknown ? Outcome.Unknown : backupFailed ? Outcome.RejectedBeforeOperation : failed + skipped == 0 ? Outcome.Succeeded
                : succeeded > 0 ? Outcome.Partial : failed > 0 && items.OfType<JsonObject>().Any(x => (bool?)x["attempted"] == true) ? Outcome.Failed : Outcome.RejectedBeforeOperation;
            string finalState = "Kept imports: " + succeeded + "; rolled back replacements: " + rolledBack + "; still changed: " + ((data["remainingChanged"] as JsonArray)?.Count ?? 0) + ".";
            Error? fault = blockers.Length > 0 ? Blocked() : unknown ? Unknown() : backupFailed ? new Error("Recovery export or validation failed before any import; inspect retained files.", new PreconditionFailedDetails("recovery-export", (string?)data["recoveryDirectory"]))
                : aggregate == Outcome.Partial ? new Error(finalState, new PartialFailureDetails(succeeded, failed, skipped))
                : aggregate != Outcome.Succeeded ? new Error(finalState, new NativeOperationFailedDetails(null, null, evidence)) : null;
            var executionAggregate = aggregate == Outcome.Unknown ? Execution.Unknown : aggregate == Outcome.RejectedBeforeOperation ? Execution.NotStarted
                : aggregate == Outcome.Partial ? Execution.Partial : preview ? Execution.ReadOnly : Execution.Completed;
            return Envelope.Create(data, fault, new Meta(DateTimeOffset.UtcNow, release, tool, id, aggregate, executionAggregate,
                unknown || (bool?)data["requiresSessionReset"] == true, BehaviorPolicy.Current,
                unknown ? Completeness.Unknown : aggregate == Outcome.Partial ? Completeness.Partial : Completeness.Complete, null,
                new[] { new Warning(WarningCode.UnverifiedBehavior, "Native acceptance is pending; recovery exports and restoration evidence must be reviewed.", new Dictionary<string, JsonElement>()) }));
        }
    }
}
