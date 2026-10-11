using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace TiaOpenness.Shared
{
    internal sealed class AuditInvocation : IDisposable
    {
        private readonly AuditLog log;
        private readonly string requestId, host, release, tool, actor;
        private readonly AuditInvocation? previous;
        private bool completed, requestWritten, started;
        private static readonly AsyncLocal<AuditInvocation?> Current = new AsyncLocal<AuditInvocation?>();
        private static readonly AsyncLocal<AuditLog?> LogOverride = new AsyncLocal<AuditLog?>();
        private static readonly AsyncLocal<int> PreviewDepth = new AsyncLocal<int>();
        private static readonly AsyncLocal<string[]?> Generation = new AsyncLocal<string[]?>();
        internal static IDisposable GenerationStep(string planId, string stepId, string runId, string parentRequestId, string? toolHash, string requestId)
        {
            var previous = Generation.Value;
            Generation.Value = new[] { planId, stepId, runId, parentRequestId, toolHash ?? "", requestId };
            return new GenerationScope(() => Generation.Value = previous);
        }
        private sealed class GenerationScope : IDisposable
        {
            private readonly Action restore;
            internal GenerationScope(Action restore) { this.restore = restore; }
            public void Dispose() => restore();
        }
        internal static bool IsReadOnlyPreview => PreviewDepth.Value > 0;
        internal static IDisposable ReadOnlyPreview() => new PreviewScope();
        private sealed class PreviewScope : IDisposable
        {
            private readonly int previous = PreviewDepth.Value;
            internal PreviewScope() { PreviewDepth.Value = previous + 1; }
            public void Dispose() => PreviewDepth.Value = previous;
        }
        private AuditInvocation(AuditLog log, string requestId, string host, string release, string tool, string actor)
        { this.log = log; this.requestId = requestId; this.host = host; this.release = release; this.tool = tool; this.actor = actor; previous = Current.Value; Current.Value = this; }
        internal string RequestId => requestId;
        internal static string? CurrentRequestId => Generation.Value?[5] ?? Current.Value?.requestId;
        internal static AuditLog? CurrentLog => Current.Value?.log ?? LogOverride.Value;
        internal static AuditInvocation? Begin(bool write, string host, string release, string tool, string? requestId = null, AuditLog? log = null, string? actor = null)
        {
            if (!write || PreviewDepth.Value > 0) return null;
            try { return new AuditInvocation(log ?? CurrentLog ?? AuditLog.Current, requestId ?? Guid.NewGuid().ToString("N"), host, release, tool, actor ?? ActorScope.Actor); }
            catch (Exception ex) { ReportFailure(ex); return null; }
        }
        internal static IDisposable UseLog(AuditLog log)
        {
            var previous = LogOverride.Value;
            LogOverride.Value = log ?? throw new ArgumentNullException(nameof(log));
            return new LogScope(previous);
        }
        private sealed class LogScope : IDisposable
        {
            private readonly AuditLog? previous;
            private bool disposed;
            internal LogScope(AuditLog? previous) { this.previous = previous; }
            public void Dispose() { if (!disposed) { disposed = true; LogOverride.Value = previous; } }
        }
        internal void RecordRequest(string? planHash = null)
        {
            if (requestWritten) return;
            requestWritten = true;
            Emit("request", planHash: planHash);
        }
        internal static void RecordCurrentRequest(string? planHash = null) { if (PreviewDepth.Value == 0) Current.Value?.RecordRequest(planHash); }
        internal void Start()
        {
            if (started || completed) return;
            RecordRequest(); started = true; Emit("start");
        }
        internal static void StartCurrent() { if (PreviewDepth.Value == 0) Current.Value?.Start(); }
        internal void Complete(string? json)
        {
            string outcome = "unknown";
            try
            {
                if (json != null)
                    using (var document = JsonDocument.Parse(json))
                    {
                        var root = document.RootElement;
                        if (root.TryGetProperty("schemaVersion", out var schema) && schema.GetInt32() == 4 && root.TryGetProperty("meta", out var meta)
                            && meta.TryGetProperty("outcome", out var value) && value.ValueKind == JsonValueKind.String)
                            outcome = value.GetString()!;
                    }
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is FormatException)
            { Trace.TraceInformation("Audit result has no readable V4 outcome: " + ex.GetType().Name); }
            if (outcome != "succeeded" && outcome != "rejected-before-operation" && outcome != "failed" && outcome != "read-failed" && outcome != "partial" && outcome != "unknown") outcome = "unknown";
            RecordRequest(); End(outcome);
        }
        internal void End(string outcome) { if (completed) return; RecordRequest(); completed = true; Emit("end", outcome); }
        private void Emit(string kind, string? outcome = null, string? planHash = null)
        {
            try
            {
                var generation = Generation.Value;
                string? recordedHash = generation == null ? planHash : kind == "request" && generation[4].Length != 0 ? generation[4] : null;
                log.Append(kind, requestId, host, release, tool, outcome, recordedHash, actor: actor,
                    planId: generation?[0], stepId: generation?[1], runId: generation?[2], parentRequestId: generation?[3]);
            }
            catch (Exception ex) { ReportFailure(ex); }
        }
        private static void ReportFailure(Exception ex)
            => DataLocations.ReportFailure("DIAGNOSTIC_WRITE_FAILED", ex);
        public void Dispose()
        {
            End("unknown");
            if (ReferenceEquals(Current.Value, this)) Current.Value = previous;
        }
    }
}
