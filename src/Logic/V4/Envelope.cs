using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMcp.Logic.V4
{
    public enum Outcome { Succeeded, RejectedBeforeOperation, ReadFailed, Failed, Partial, Unknown }
    public enum Execution { NotStarted, ReadOnly, Completed, Partial, Unknown }
    public enum Completeness { Complete, Partial, None, Unknown }
    public enum BehaviorPolicy { NotApplicable, Current, SafeV4 }
    public enum WarningCode
    {
        IncompleteData, NativeWarning, CandidateOnly, UnverifiedBehavior, CleanupFailed,
        NativeCapabilityLimit, DiagnosticWriteFailed, ApprovalDisabled, RecoveryGuidance, BackupSkipped,
        ApprovalPrecheckRefused, StagingFolderRetained
    }

    // Data is frozen JSON at the host boundary; domain DTOs enter and leave through V4Json.
    public sealed class Envelope
    {
        [JsonPropertyOrder(0)] public int SchemaVersion => 4;
        [JsonPropertyOrder(1)] public bool Ok { get; }
        [JsonPropertyOrder(2)] public JsonElement? Data { get; }
        [JsonPropertyOrder(3)] public Error? Error { get; }
        [JsonPropertyOrder(4)] public Meta Meta { get; }

        [JsonConstructor]
        public Envelope(int schemaVersion, bool ok, JsonElement? data, Error? error, Meta meta)
        {
            Ok = ok;
            Data = data?.Clone();
            Error = error;
            Meta = meta;
            V4Validation.Envelope(this, schemaVersion);
        }

        public static Envelope Create<T>(T data, Error? error, Meta meta)
        {
            var envelope = new Envelope(4, meta.Outcome == Outcome.Succeeded, V4Json.Data(data), error, meta);
            if (data is BatchData batch) V4Validation.Batch(envelope, batch);
            if (data is PreviewData preview) V4Validation.Preview(envelope, preview);
            return envelope;
        }
    }

    public sealed class Meta
    {
        [JsonPropertyOrder(0)] public DateTimeOffset Timestamp { get; }
        [JsonPropertyOrder(1)] public string? ReleaseKey { get; }
        [JsonPropertyOrder(2)] public string Tool { get; }
        [JsonPropertyOrder(3)] public string RequestId { get; }
        [JsonPropertyOrder(4)] public Outcome Outcome { get; }
        [JsonPropertyOrder(5)] public Execution Execution { get; }
        [JsonPropertyOrder(6)] public bool RequiresSessionReset { get; }
        [JsonPropertyOrder(7)] public BehaviorPolicy BehaviorPolicy { get; }
        [JsonPropertyOrder(8)] public Completeness Completeness { get; }
        [JsonPropertyOrder(9)] public Paging? Paging { get; }
        [JsonPropertyOrder(10)] public IReadOnlyList<Warning> Warnings { get; }

        public Meta(DateTimeOffset timestamp, string? releaseKey, string tool, string requestId,
            Outcome outcome, Execution execution, bool requiresSessionReset, BehaviorPolicy behaviorPolicy,
            Completeness completeness, Paging? paging, IReadOnlyList<Warning> warnings)
        {
            Timestamp = timestamp.ToUniversalTime();
            ReleaseKey = releaseKey;
            Tool = tool;
            RequestId = requestId;
            Outcome = outcome;
            Execution = execution;
            RequiresSessionReset = requiresSessionReset;
            BehaviorPolicy = behaviorPolicy;
            Completeness = completeness;
            Paging = paging;
            Warnings = V4Validation.List(warnings);
            V4Validation.Meta(this);
        }

        public static string Correlate(string? upstreamRequestId) =>
            string.IsNullOrWhiteSpace(upstreamRequestId) ? Guid.NewGuid().ToString("N") : upstreamRequestId!;
    }

    public sealed class Warning
    {
        [JsonPropertyOrder(0)] public WarningCode Code { get; }
        [JsonPropertyOrder(1)] public string Message { get; }
        [JsonPropertyOrder(2)] public IReadOnlyDictionary<string, JsonElement> Details { get; }

        public Warning(WarningCode code, string message, IReadOnlyDictionary<string, JsonElement> details)
        {
            Code = code;
            Message = message;
            Details = V4Validation.Object(details);
            V4Validation.Defined(code);
            V4Validation.Text(message, nameof(message));
        }
    }

    public sealed class BatchItem
    {
        [JsonPropertyOrder(0)] public int Index { get; }
        [JsonPropertyOrder(1)] public string? Target { get; }
        [JsonPropertyOrder(2)] public Envelope Result { get; }

        public BatchItem(int index, string? target, Envelope result)
        {
            V4Validation.Require(index >= 0 && result != null, "A batch item needs a nonnegative index and a result.");
            Index = index;
            Target = target;
            Result = result!;
        }
    }

    public sealed class BatchData
    {
        public IReadOnlyList<BatchItem> Items { get; }

        public BatchData(IReadOnlyList<BatchItem> items)
        {
            Items = V4Validation.List(items);
            for (int i = 0; i < Items.Count; i++)
                V4Validation.Require(Items[i].Index == i, "Batch items must retain input order and zero-based indices.");
        }
    }
}
