using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

namespace TiaMcp.Logic.V4
{
    // Construction and deserialization use the same invariants. Domain policies still own
    // capability verification, redaction, page-size budgets and native execution evidence.
    internal static class V4Validation
    {
        internal static void Require(bool condition, string message)
        {
            if (!condition) throw new ArgumentException(message);
        }

        internal static void Text(string? value, string name) =>
            Require(!string.IsNullOrWhiteSpace(value), name + " must not be empty.");

        internal static void Defined<T>(T value) where T : struct, Enum =>
            Require(Enum.IsDefined(typeof(T), value), "Unknown " + typeof(T).Name + " value.");

        internal static IReadOnlyList<T> List<T>(IReadOnlyList<T> values)
        {
            Require(values != null, "An array must be present, even when empty.");
            var copy = values!.ToArray();
            Require(copy.All(x => x != null), "Array entries must not be null.");
            return Array.AsReadOnly(copy);
        }

        internal static IReadOnlyDictionary<string, JsonElement> Object(IReadOnlyDictionary<string, JsonElement> values)
        {
            Require(values != null, "An object must be present, even when empty.");
            var copy = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var pair in values!)
            {
                Json(pair.Value);
                copy.Add(pair.Key, pair.Value.Clone());
            }
            return new ReadOnlyDictionary<string, JsonElement>(copy);
        }

        internal static IReadOnlyDictionary<string, string> Hashes(IReadOnlyDictionary<string, string> values)
        {
            Require(values != null, "Input hashes must be present.");
            var copy = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in values!)
            {
                Text(pair.Key, "input path");
                Hash(pair.Value);
                copy.Add(pair.Key, pair.Value);
            }
            return new ReadOnlyDictionary<string, string>(copy);
        }

        internal static void Hash(string value) => Require(value != null && value.Length == 64
            && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'), "SHA-256 must be 64 lowercase hexadecimal characters.");

        internal static void Release(string? value) => Require(value == null ||
            new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }.Contains(value), "Unknown release key.");

        internal static void Json(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Undefined:
                    throw new ArgumentException("Undefined JSON is not a V4 value.");
                case JsonValueKind.Number:
                    Require(value.TryGetDouble(out double number) && !double.IsNaN(number) && !double.IsInfinity(number),
                        "JSON numbers must be finite.");
                    break;
                case JsonValueKind.Object:
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject())
                    {
                        Require(names.Add(property.Name), "Duplicate JSON property: " + property.Name);
                        Json(property.Value);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in value.EnumerateArray()) Json(item);
                    break;
            }
        }

        internal static void Envelope(Envelope value, int schemaVersion)
        {
            Require(schemaVersion == 4, "schemaVersion must be 4.");
            Require(value.Meta != null, "meta is required.");
            if (value.Data.HasValue)
            {
                Require(value.Data.Value.ValueKind == JsonValueKind.Object, "data must be an object or null.");
                Json(value.Data.Value);
            }
            var outcome = value.Meta!.Outcome;
            Require(value.Ok == (outcome == Outcome.Succeeded), "ok must agree with outcome.");
            Require(value.Ok == (value.Error == null), "Only success has a null error.");
            Require((outcome == Outcome.Partial) == (value.Error?.Code == ErrorCode.PartialFailure),
                "partial requires PARTIAL_FAILURE, and PARTIAL_FAILURE requires partial.");
            Require((outcome == Outcome.Unknown) == (value.Error?.Code == ErrorCode.OutcomeUnknown),
                "unknown requires OUTCOME_UNKNOWN, and OUTCOME_UNKNOWN requires unknown.");
            if (value.Error?.Code == ErrorCode.NotExecuted)
                Require(outcome == Outcome.RejectedBeforeOperation, "NOT_EXECUTED must be rejected before operation.");
            if (value.Error?.Code == ErrorCode.SessionResetRequired)
                Require(outcome == Outcome.RejectedBeforeOperation && value.Meta.RequiresSessionReset,
                    "SESSION_RESET_REQUIRED must reject the operation and require a reset.");
            if (value.Error?.Code == ErrorCode.Timeout)
                Require(outcome == Outcome.ReadFailed || outcome == Outcome.RejectedBeforeOperation,
                    "TIMEOUT may only represent a read or an operation that has not started.");
        }

        internal static void Meta(Meta value)
        {
            Release(value.ReleaseKey);
            Text(value.Tool, "tool");
            Text(value.RequestId, "requestId");
            Defined(value.Outcome);
            Defined(value.Execution);
            Defined(value.BehaviorPolicy);
            Defined(value.Completeness);
            var expected = value.Outcome switch
            {
                Outcome.RejectedBeforeOperation => Execution.NotStarted,
                Outcome.ReadFailed => Execution.ReadOnly,
                Outcome.Failed => Execution.Completed,
                Outcome.Partial => Execution.Partial,
                Outcome.Unknown => Execution.Unknown,
                _ => value.Execution
            };
            Require(value.Execution == expected && (value.Outcome != Outcome.Succeeded
                || value.Execution == Execution.ReadOnly || value.Execution == Execution.Completed), "execution must agree with outcome.");
            Require(value.Outcome != Outcome.Unknown || value.RequiresSessionReset, "Unknown writes require a session reset.");
            Require(value.Outcome != Outcome.Succeeded || value.Completeness != Completeness.Partial
                || value.Warnings.Count > 0, "Partial successful observations require a warning.");
            Require(value.BehaviorPolicy != BehaviorPolicy.Current
                || value.Warnings.Any(w => w.Code == WarningCode.UnverifiedBehavior), "Current policy requires UNVERIFIED_BEHAVIOR.");
        }

        internal static void Details(ErrorDetails value)
        {
            switch (value)
            {
                case LimitExceededDetails limit:
                    Require((limit.Limit == null || limit.Limit >= 0) && (limit.Actual == null || limit.Actual >= 0),
                        "Limit and actual must be nonnegative.");
                    break;
                case NotExecutedDetails item:
                    Require(item.CauseIndex == null || item.CauseIndex >= 0, "causeIndex must be nonnegative.");
                    break;
                case PartialFailureDetails partial:
                    Require(partial.Succeeded >= 0 && partial.Failed >= 0 && partial.NotExecuted >= 0,
                        "Partial failure counts must be nonnegative.");
                    // Zero counts are allowed for known partial side effects without item counts.
                    Require(partial.Succeeded == 0 && partial.Failed == 0 && partial.NotExecuted == 0
                        || partial.Succeeded > 0 && (partial.Failed > 0 || partial.NotExecuted > 0),
                        "Item counts must describe both successful and failed or unexecuted items.");
                    break;
                case UnsupportedCapabilityDetails capability:
                    Release(capability.ReleaseKey);
                    break;
                case ConfirmationRequiredDetails confirmation:
                    if (confirmation.PlanHash != null) Hash(confirmation.PlanHash);
                    break;
                case PlanStaleDetails stale:
                    if (stale.PlanHash != null) Hash(stale.PlanHash);
                    break;
            }
        }

        internal static void Paging(Paging value)
        {
            Defined(value.Mode);
            Require(value.Limit > 0 && (value.Total == null || value.Total >= 0), "Invalid page limit or total.");
            if (value.Mode == PagingMode.Offset)
            {
                Require(value.Offset >= 0 && value.Cursor == null && value.NextCursor == null,
                    "Offset paging cannot carry cursor fields.");
                Require(value.Complete ? value.NextOffset == null : value.NextOffset > value.Offset,
                    "An unfinished offset page must advance; a complete page has no next offset.");
                Require(value.Total == null || value.Offset <= value.Total &&
                    (value.NextOffset == null || value.NextOffset < value.Total), "Offset exceeds the snapshot total.");
            }
            else
            {
                Require(value.Offset == null && value.NextOffset == null, "Cursor paging cannot carry offset fields.");
                if (value.Cursor != null) Text(value.Cursor, "cursor");
                Require(value.Complete ? value.NextCursor == null : !string.IsNullOrWhiteSpace(value.NextCursor)
                    && value.NextCursor != value.Cursor, "An unfinished cursor page must advance; a complete page has no next cursor.");
            }
        }

        internal static void Plan(Plan value)
        {
            Hash(value.Hash);
            Hash(value.ArgumentsHash);
            if (value.InventoryHash != null) Hash(value.InventoryHash);
            Release(value.ReleaseKey);
            Text(value.Tool, "tool");
            Require(value.Identity != null, "Plan identity is required.");
        }

        internal static void Identity(PlanIdentity value)
        {
            if (value.WorkspaceRoot != null)
            {
                Text(value.WorkspaceRoot, "workspaceRoot");
                Require(value.ProcessId == null && value.ProcessStartUtc == null && value.ProjectFile == null
                    && value.BindingEpoch == null, "Offline plan identity cannot include a project binding.");
            }
            else
            {
                Require(value.ProcessId > 0 && value.ProcessStartUtc != null && value.BindingEpoch >= 0,
                    "Project identity requires a process start time, PID and binding epoch.");
                Text(value.ProjectFile, "projectFile");
            }
        }

        internal static void Batch(Envelope envelope, BatchData data)
        {
            bool unknown = data.Items.Any(i => i.Result.Meta.Outcome == Outcome.Unknown);
            bool partial = data.Items.Any(i => i.Result.Meta.Outcome == Outcome.Partial)
                || data.Items.Any(i => i.Result.Ok) && data.Items.Any(i => !i.Result.Ok);
            if (unknown) Require(envelope.Meta.Outcome == Outcome.Unknown, "An unknown child makes its batch unknown.");
            else if (partial) Require(envelope.Meta.Outcome == Outcome.Partial, "Known mixed child results make a partial batch.");
            else if (data.Items.All(i => i.Result.Ok)) Require(envelope.Ok, "All successful children require a successful batch.");
            else Require(!envelope.Ok && envelope.Meta.Outcome != Outcome.Unknown && envelope.Meta.Outcome != Outcome.Partial,
                "The batch outcome must preserve its failed children.");
        }

        internal static void Preview(Envelope envelope, PreviewData data)
        {
            Require(envelope.Ok && envelope.Meta.Execution == Execution.ReadOnly, "A generated preview is a read-only success.");
            Require(envelope.Meta.Tool == data.Plan.Tool && envelope.Meta.ReleaseKey == data.Plan.ReleaseKey,
                "Preview plan and envelope must identify the same tool and release.");
        }
    }
}
