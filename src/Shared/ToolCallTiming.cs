using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;

namespace TiaOpenness.Shared
{
    // A tools/call can span several worker requests and Workbench decisions.
    internal sealed class ToolCallTiming : IDisposable
    {
        private static readonly AsyncLocal<ToolCallTiming?> Current = new();
        internal static ToolCallTiming? Active => Current.Value;
        private readonly ToolCallTiming? previous = Current.Value;
        private readonly Func<TimeSpan> elapsed;
        private readonly object gate = new();
        private TimeSpan? approvalTime;
        private TimeSpan waitStarted;
        private int activeWaits;
        private long? workerMs;
        private bool missingWorkerTime;
        private HashSet<(string? Tool, string? RequestId)>? retrievedResults;

        internal ToolCallTiming(Func<TimeSpan> elapsed)
        {
            this.elapsed = elapsed;
            Current.Value = this;
        }

        // Leave space when fitting a page before the outer host stamps its final times.
        internal static int ReservedJsonChars => Current.Value == null ? 0 :
            ",\"elapsedMs\":,\"approvalWaitMs\":,\"workerElapsedMs\":".Length + 3 * 19;

        internal static JsonObject? Meta(JsonNode? body) => body is JsonObject envelope && envelope["schemaVersion"] is JsonValue version
            && version.TryGetValue<int>(out var number) && number == 4 ? envelope["meta"] as JsonObject : null;

        internal static IDisposable? BeginApprovalWait() => Current.Value is { } timing ? new ApprovalWait(timing) : null;

        private sealed class ApprovalWait : IDisposable
        {
            private readonly ToolCallTiming timing;
            internal ApprovalWait(ToolCallTiming timing)
            {
                this.timing = timing;
                lock (timing.gate)
                    if (timing.activeWaits++ == 0) timing.waitStarted = timing.elapsed();
            }
            public void Dispose()
            {
                lock (timing.gate)
                    if (--timing.activeWaits == 0)
                        timing.approvalTime = timing.approvalTime.GetValueOrDefault() + (timing.elapsed() - timing.waitStarted);
            }
        }

        internal static void RecordWorkerReply(long? milliseconds)
        {
            if (Current.Value is not { } timing) return;
            lock (timing.gate)
            {
                if (milliseconds is long ms && ms <= long.MaxValue - timing.workerMs.GetValueOrDefault())
                    timing.workerMs = timing.workerMs.GetValueOrDefault() + ms;
                else timing.missingWorkerTime = true;
            }
        }

        // Lookup can return a copy, a running snapshot or an evicted summary, including through CallTool.
        internal void PreserveRetrievedResult(JsonNode? body)
        {
            if (Meta(body) is not { } meta) return;
            lock (gate) (retrievedResults ??= new()).Add(((string?)meta["tool"], (string?)meta["requestId"]));
        }

        internal bool TryWrite(JsonObject meta)
        {
            lock (gate)
            {
                if (meta.ContainsKey("elapsedMs") || retrievedResults?.Contains(((string?)meta["tool"], (string?)meta["requestId"])) == true)
                    return false;
                var total = elapsed();
                var approval = activeWaits == 0 ? approvalTime : approvalTime.GetValueOrDefault() + (total - waitStarted);
                meta["elapsedMs"] = (total - approval.GetValueOrDefault()).Ticks / TimeSpan.TicksPerMillisecond;
                if (approval.HasValue) meta["approvalWaitMs"] = approval.Value.Ticks / TimeSpan.TicksPerMillisecond;
                else meta.Remove("approvalWaitMs");
                if (workerMs.HasValue && !missingWorkerTime) meta["workerElapsedMs"] = workerMs.Value;
                else meta.Remove("workerElapsedMs");
                return true;
            }
        }

        public void Dispose() => Current.Value = previous;
    }
}
