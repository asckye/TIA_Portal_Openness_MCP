using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    internal interface IEngineeringTransaction : IDisposable
    {
        bool CanCommit { get; }
        bool CommitRequested { get; }
        bool IsCancellationRequested { get; }
        void Commit();
    }

    internal static class TransactionExecution
    {
        // Reviewed in-project synchronous mutations only. Description tags are not
        // proof that file/online/lifecycle effects can be rolled back by TIA.
        internal static readonly IReadOnlyCollection<string> SupportedTools = Array.AsReadOnly(new[] {
            "CreatePlcTypeGroup", "DeleteEmptyPlcBlockGroup", "ManagePlcUserGroup",
            "ManageDeviceUserGroup", "ManageUnifiedHmiGroup", "DeleteEmptyUnifiedHmiScreenGroup",
            "UpdateUnifiedObjectProperties", "UpdateUnifiedMultilingualProperty"
        });

        internal static void RequireSupported(string name)
        {
            foreach (var supported in SupportedTools)
                if (string.Equals(name, supported, StringComparison.OrdinalIgnoreCase)) return;
            throw new ArgumentException("Tool is not supported inside a project transaction: " + name
                + ". Supported tools: " + string.Join(", ", SupportedTools)
                + ". Compile, online, session, save, file and nested orchestration operations must run separately.");
        }

        internal static bool Run(int count, Func<IEngineeringTransaction> begin, Func<int, bool> invoke, JsonObject meta)
        {
            meta["committed"] = false; meta["rollbackCompleted"] = false;
            bool committed;
            using (var scope = begin())
            {
                bool allOk = true;
                for (int i = 0; i < count; i++)
                {
                    if (scope.IsCancellationRequested) { allOk = false; meta["stoppedIndex"] = i; break; }
                    meta["mayHaveChanged"] = true;
                    if (!invoke(i)) { allOk = false; meta["stoppedIndex"] = i; break; }
                }
                bool canCommit = scope.CanCommit;
                bool cancelled = scope.IsCancellationRequested;
                meta["canCommit"] = canCommit; meta["cancellationRequested"] = cancelled;
                if (allOk && canCommit && !cancelled) scope.Commit();
                bool requested = scope.CommitRequested;
                // CanCommit may become false even when an inner tool caught its own
                // native exception. RETURNED/bridgeSuccess alone cannot prove commit.
                committed = allOk && !cancelled && requested && scope.CanCommit;
                meta["commitRequested"] = requested;
            }
            // A throwing Dispose never reaches this point: outcome stays unconfirmed.
            meta["committed"] = committed; meta["rollbackCompleted"] = !committed;
            return committed;
        }
    }
}
