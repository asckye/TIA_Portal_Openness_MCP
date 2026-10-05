using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Logic.V4;
using TiaMcp.PlcWorker;

namespace TiaMcp.LegacyHost;

internal sealed partial class FoundationTool
{
    internal async ValueTask<CallToolResult> InvokeSessionCandidateAsync(IReadOnlyDictionary<string, JsonElement> args, string release, string tool, string id, CancellationToken cancellationToken)
    {
        string Text(string key, string fallback = "") => args.TryGetValue(key, out var value) ? value.GetString()! : fallback;
        bool Flag(string key) => args.TryGetValue(key, out var value) && value.GetBoolean();
        try
        {
            var input = new SessionRequest { Action = SessionCandidateContract.Action(tool), ProcessId = args["processId"].GetInt32(),
                ProcessStartUtc = SessionCandidateSession.Start(Text("processStartUtc")), ProjectPath = Text("projectPath"), StartNew = Flag("startNew"),
                ReuseOpen = Flag("reuseOpen"), Upgrade = Text("upgrade", "reject"), CopyPath = Text("copyPath") };
            var result = await Task.Run(() => FoundationCandidateSession.For(worker).Session(release, tool, id, input, Text("mode", "preview"), Flag("confirm"),
                Text("expectedPlanHash"), Text("expectedProjectFile"), Flag("confirmUpgrade"), cancellationToken));
            return FoundationV4Result.ImportCandidate(result);
        }
        catch (SessionCandidateRejection error)
        { return FoundationV4Result.ImportCandidate(SessionCandidateSession.Result(release, tool, id, null, error.Error, Outcome.RejectedBeforeOperation, Execution.NotStarted)); }
    }
}

internal sealed partial class FoundationCandidateSession
{
    private readonly SessionCandidateSession sessions = new();
    internal Envelope Session(string release, string tool, string id, SessionRequest request, string mode, bool confirm, string hash, string project, bool confirmUpgrade, CancellationToken token)
    {
        lock (serial)
        {
            if (poisoned) return SessionCandidateSession.Result(release, tool, id, null,
                new Error("The candidate session must be rebuilt.", new SessionResetRequiredDetails("candidate-session-unknown")), Outcome.RejectedBeforeOperation, Execution.NotStarted);
            var result = sessions.Run(new SessionProxy(worker, token), release, tool, id, request, mode, confirm, hash, project, confirmUpgrade);
            poisoned |= result.Meta.RequiresSessionReset;
            if (result.Meta.RequiresSessionReset && worker is WorkerClient native) native.InvalidateCandidateSession();
            return result;
        }
    }
    private sealed class SessionProxy(IFoundationWorker worker, CancellationToken token) : ISessionCandidateAdapter, ISessionCandidateBoundary
    {
        private SessionCandidateReply Call(SessionCandidateCall candidate, string mode)
        {
            if (token.IsCancellationRequested) throw NotSent(token);
            var args = new JsonObject { ["candidate"] = JsonSerializer.SerializeToNode(candidate), ["mode"] = mode };
            return CandidateWire.Session(worker.Call(WorkerOperations.SessionCandidate, args, token).GetAwaiter().GetResult(), args);
        }
        public SessionObservation Observe()
        {
            var reply = Call(new(), "preview");
            if (reply.Fault != null) throw new CandidateObservationException(reply.Fault);
            return reply.Observation!;
        }
        public SessionAttempt Execute(SessionCheck check)
        {
            try
            {
                var reply = Call(new() { Action = "execute", Check = check }, "apply");
                if (reply.Fault != null) return new() { Fault = reply.Fault };
                return reply.Attempt!;
            }
            catch (Exception error)
            {
                bool issued = Unknown(error);
                return new() { Issued = issued, RequiresSessionReset = issued, Fault = new() { Kind = "preflight", Subject = "worker-channel" },
                    Reason = check.Request.Action == "attach" ? SessionPrimitives.ExceptionReason(error) : null };
            }
        }
        public void BeforeAction() => throw new InvalidOperationException("Use the synchronous session boundary.");
        public void Attach(SessionRequest request) => throw new InvalidOperationException("Use the synchronous session boundary.");
        public void Bind(SessionRequest request) => throw new InvalidOperationException("Use the synchronous session boundary.");
        public void Open(SessionRequest request) => throw new InvalidOperationException("Use the synchronous session boundary.");
        public void MarkUncertain() { }
    }
}
