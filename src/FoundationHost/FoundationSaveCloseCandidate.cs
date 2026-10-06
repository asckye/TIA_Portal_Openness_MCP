using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Logic.V4;
using TiaMcp.PlcWorker;

namespace TiaMcp.LegacyHost;

internal sealed partial class FoundationTool
{
    internal async ValueTask<CallToolResult> InvokeSaveCloseCandidateAsync(IReadOnlyDictionary<string, JsonElement> args, string release, string tool, string id, CancellationToken token)
    {
        string Text(string key, string fallback = "") => args.TryGetValue(key, out var value) ? value.GetString()! : fallback;
        bool Flag(string key) => args.TryGetValue(key, out var value) && value.GetBoolean();
        var request = new SaveCloseRequest { Action = SaveCloseContract.Action(tool), SaveChanges = Flag("saveChanges"), DiscardChanges = Flag("discardChanges"), NewProjectPath = Text("newProjectPath") };
        var result = await Task.Run(() => FoundationCandidateSession.For(worker).SaveClose(release, tool, id, request,
            Text("mode", "preview"), Flag("confirm"), Text("expectedPlanHash"), Text("expectedProjectFile"), Flag("confirmDiscard"), token));
        return FoundationV4Result.ImportCandidate(result);
    }
}
internal sealed partial class FoundationCandidateSession
{
    private readonly SaveCloseSession saveClose = new();
    internal Envelope SaveClose(string release, string tool, string id, SaveCloseRequest request, string mode, bool confirm, string hash, string project, bool confirmDiscard, CancellationToken token)
    {
        lock (serial)
        {
            if (poisoned) return SaveCloseSession.Result(release, tool, id, null,
                new Error("The candidate session must be rebuilt.", new SessionResetRequiredDetails("save-close-unknown")), Outcome.RejectedBeforeOperation, Execution.NotStarted);
            var result = saveClose.Run(new SaveCloseProxy(worker, token), release, tool, id, request, mode, confirm, hash, project, confirmDiscard);
            poisoned |= result.Meta.RequiresSessionReset;
            if (result.Meta.RequiresSessionReset && worker is WorkerClient native) native.InvalidateCandidateSession();
            return result;
        }
    }
    private sealed class SaveCloseProxy(IFoundationWorker worker, CancellationToken token) : ISaveCloseAdapter, ISaveCloseBoundary
    {
        private SaveCloseReply Call(SaveCloseCall candidate, string mode)
        {
            if (token.IsCancellationRequested) throw NotSent(token);
            var args = new JsonObject { ["candidate"] = JsonSerializer.SerializeToNode(candidate), ["mode"] = mode };
            return CandidateWire.SaveClose(worker.Call(WorkerOperations.SaveCloseCandidate, args, token).GetAwaiter().GetResult(), args);
        }
        public SaveCloseObservation Observe()
        {
            var reply = Call(new(), "preview");
            if (reply.Fault != null) throw new CandidateObservationException(reply.Fault);
            return reply.Observation!;
        }
        public SaveCloseAttempt Execute(SaveCloseCheck check)
        {
            try
            {
                var reply = Call(new() { Action = "execute", Check = check }, "apply");
                return reply.Fault != null ? new() { Fault = reply.Fault } : reply.Attempt!;
            }
            catch (Exception error)
            {
                bool issued = Unknown(error);
                return new() { Issued = issued, RequiresSessionReset = issued, Fault = new() { Kind = "preflight", Subject = "worker-channel" } };
            }
        }
        public void BeforeAction() => throw new InvalidOperationException("Use the synchronous save/close boundary.");
        public void Save() => throw new InvalidOperationException("Use the synchronous save/close boundary.");
        public void SaveCopy(string directory) => throw new InvalidOperationException("Use the synchronous save/close boundary.");
        public void Close() => throw new InvalidOperationException("Use the synchronous save/close boundary.");
        public void Disconnect() => throw new InvalidOperationException("Use the synchronous save/close boundary.");
        public void MarkUncertain() { }
    }
}
