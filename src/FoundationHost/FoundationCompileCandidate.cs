using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Logic.V4;
using TiaMcp.PlcWorker;

namespace TiaMcp.LegacyHost;

internal sealed partial class FoundationTool
{
    internal async ValueTask<CallToolResult> InvokeCompileCandidateAsync(IReadOnlyDictionary<string, JsonElement> args, string release, string tool, string id, CancellationToken token)
    {
        string Text(string key, string fallback = "") => args.TryGetValue(key, out var value) ? value.GetString()! : fallback;
        string password = Text("password");
        var request = new CompileRequest { Entry = tool, SoftwarePath = Text("softwarePath"), OfflinePolicy = Text("offlinePolicy", "require"), PasswordProvided = password.Length > 0 };
        var result = await Task.Run(() => FoundationCandidateSession.For(worker).Compile(release, tool, id, request, password,
            Text("mode", "preview"), args.TryGetValue("confirm", out var confirm) && confirm.GetBoolean(), Text("expectedPlanHash"), Text("expectedProjectFile"), token));
        return FoundationV4Result.ImportCandidate(result);
    }
}
internal sealed partial class FoundationCandidateSession
{
    private readonly CompileSession compile = new();
    internal Envelope Compile(string release, string tool, string id, CompileRequest request, string password, string mode, bool confirm, string hash, string project, CancellationToken token)
    {
        lock (serial)
        {
            if (poisoned) return CompileSession.Result(release, tool, id, null, new Error("The candidate session must be rebuilt.", new SessionResetRequiredDetails("compile-unknown")), Outcome.RejectedBeforeOperation, Execution.NotStarted);
            var result = compile.Run(new CompileProxy(worker, request, token), release, tool, id, request, password, mode, confirm, hash, project);
            poisoned |= result.Meta.RequiresSessionReset;
            if (result.Meta.RequiresSessionReset && worker is WorkerClient native) native.InvalidateCandidateSession();
            return result;
        }
    }
    private sealed class CompileProxy(IFoundationWorker worker, CompileRequest request, CancellationToken token) : ICompileAdapter, ICompileBoundary
    {
        private CompileReply Call(CompileCall call, string mode)
        {
            if (token.IsCancellationRequested) throw NotSent(token);
            var args = new JsonObject { ["candidate"] = JsonSerializer.SerializeToNode(call), ["mode"] = mode };
            return CandidateWire.Compile(worker.Call(WorkerOperations.CompileCandidate, args, token).GetAwaiter().GetResult(), args);
        }
        public CompileObservation Observe()
        {
            var reply = Call(new() { Request = request }, "preview");
            if (reply.Fault != null) throw new CandidateObservationException(reply.Fault);
            return reply.Observation!;
        }
        public CompileAttempt Execute(CompileCheck check, string password)
        {
            try
            {
                var reply = Call(new() { Action = "execute", Request = request, Check = check, Password = password }, "apply");
                return reply.Fault != null ? new() { Fault = reply.Fault } : reply.Attempt!;
            }
            catch (Exception error)
            {
                bool uncertain = Unknown(error);
                return new() { DispatchUncertain = uncertain, RequiresSessionReset = uncertain, Stage = "worker-channel", Fault = new() { Kind = "preflight", Subject = "worker-channel" } };
            }
        }
        public void BeforeAction() => throw new InvalidOperationException("Use the synchronous compile boundary.");
        public void Login(string password) => throw new InvalidOperationException("Use the synchronous compile boundary.");
        public CompileDiagnostics Compile() => throw new InvalidOperationException("Use the synchronous compile boundary.");
        public void Logout() => throw new InvalidOperationException("Use the synchronous compile boundary.");
        public void MarkUncertain() { }
    }
}
