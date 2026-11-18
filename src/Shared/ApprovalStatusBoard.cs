using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace TiaOpenness.Shared
{
    // Weak session keys release history when a host session ends. Only display-safe fields are retained.
    internal static class ApprovalStatusBoard
    {
        private static readonly ConditionalWeakTable<object, Session> Sessions = new ConditionalWeakTable<object, Session>();
        private sealed class Session
        {
            internal readonly object Sync = new object();
            internal readonly List<JsonObject> Pending = new List<JsonObject>();
            internal readonly Queue<JsonObject> Recent = new Queue<JsonObject>();
        }

        internal static JsonObject Snapshot(object? key)
        {
            if (key == null || !Sessions.TryGetValue(key, out var session))
                return new JsonObject { ["pending"] = new JsonArray(), ["recent"] = new JsonArray() };
            lock (session.Sync)
                return new JsonObject {
                    ["pending"] = new JsonArray(session.Pending.Select(row => row.DeepClone()).ToArray()),
                    ["recent"] = new JsonArray(session.Recent.Reverse().Select(row => row.DeepClone()).ToArray()) };
        }

        internal static async Task<ApprovalOutcome> Wait(object? key, PendingApproval request, Func<Task<ApprovalOutcome>> wait)
        {
            if (key == null) return await wait().ConfigureAwait(false);
            var session = Sessions.GetValue(key, _ => new Session());
            var row = new JsonObject { ["requestId"] = request.RequestId, ["tool"] = request.Tool,
                ["risk"] = request.Risk, ["riskAction"] = request.RiskAction, ["deadline"] = request.Deadline.ToString("O"),
                ["startedUtc"] = DateTimeOffset.UtcNow.ToString("O"), ["outcome"] = "pending" };
            lock (session.Sync) session.Pending.Add(row);
            string outcome = "workbench-unavailable";
            bool record = true;
            try
            {
                var result = await wait().ConfigureAwait(false);
                record = !result.Disabled;
                outcome = result.Disabled ? "disabled" : result.Reason == null ? "granted"
                    : result.Reason is "denied" or "timeout" or "workbench-unavailable" or "cancelled" ? result.Reason : "denied";
                return result;
            }
            catch (OperationCanceledException) { outcome = "cancelled"; throw; }
            finally
            {
                lock (session.Sync)
                {
                    session.Pending.Remove(row);
                    row["outcome"] = outcome;
                    row["completedUtc"] = DateTimeOffset.UtcNow.ToString("O");
                    if (record)
                    {
                        session.Recent.Enqueue(row);
                        while (session.Recent.Count > 16) session.Recent.Dequeue();
                    }
                }
            }
        }
    }
}
