using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Logic.V4;
using TiaMcp.PlcWorker;

namespace TiaMcp.LegacyHost;

internal sealed partial class FoundationTool
{
    internal async ValueTask<Envelope> ResolveSourcePathAsync(string release, string tool, string id, string path, CancellationToken token)
        => await Task.Run(() => FoundationCandidateSession.For(worker).Source(release, tool, id, new SourceRequest { SoftwarePath = path }, "preview", false, "", "", token));
    internal async ValueTask<CallToolResult> InvokeSourceCandidateAsync(IReadOnlyDictionary<string, JsonElement> args, string release, string tool, string id, CancellationToken token)
    {
        string Text(string key, string fallback = "") => args.TryGetValue(key, out var value) ? value.GetString()! : fallback;
        bool Flag(string key) => args.TryGetValue(key, out var value) && value.GetBoolean();
        string action = tool == "ListPlcExternalSources" ? "list" : tool == "GenerateBlocksFromExternalSource" ? "generate" : tool == "DeletePlcExternalSource" ? "delete" : "import";
        string name = Text("sourceName"), file = Text("filePath");
        if (tool == "PlanPlcExternalSourceImport")
        {
            if (Text("allowedFilePath") != file || Text("mode", "preview") != "preview") return FoundationV4Result.ImportCandidate(SourceSession.Result(release, tool, id, null,
                new Error("The plan requires the exact allowed file and preview mode.", new InvalidArgumentDetails("allowedFilePath/mode", Array.Empty<string>())), Outcome.RejectedBeforeOperation, Execution.NotStarted));
            if (name == "") name = Path.GetFileName(file);
        }
        var request = new SourceRequest { SoftwarePath = Text("softwarePath"), ReadGroup = action == "list" ? Text("groupPath") : "", Overwrite = Flag("overwrite"), OnError = Text("onError", "stop"), MissingPolicy = Text("missingPolicy", "reject"),
            Items = action == "list" ? Array.Empty<SourceItem>() : new[] { new SourceItem { Action = action, SourceName = name, GroupPath = Text("groupPath"), FilePath = file } } };
        var result = await Task.Run(() => FoundationCandidateSession.For(worker).Source(release, tool, id, request, Text("mode", "preview"), Flag("confirm"), Text("expectedPlanHash"), Text("expectedProjectFile"), token));
        return FoundationV4Result.ImportCandidate(result);
    }
}
internal sealed partial class FoundationCandidateSession
{
    private readonly SourceSession sources = new();
    internal Envelope Source(string release, string tool, string id, SourceRequest request, string mode, bool confirm, string hash, string project, CancellationToken token)
    {
        lock (serial)
        {
            if (poisoned) return SourceSession.Result(release, tool, id, null, new Error("The candidate session must be rebuilt.", new SessionResetRequiredDetails("source-unknown")), Outcome.RejectedBeforeOperation, Execution.NotStarted);
            var result = sources.Run(new SourceProxy(worker, request, token), release, tool, id, request, mode, confirm, hash, project);
            poisoned |= result.Meta.RequiresSessionReset;
            if (result.Meta.RequiresSessionReset && worker is WorkerClient native) native.InvalidateCandidateSession();
            return result;
        }
    }
    private sealed class SourceProxy(IFoundationWorker worker, SourceRequest request, CancellationToken token) : ISourceAdapter, ISourceBoundary
    {
        private SourceReply Call(SourceCall candidate, string mode)
        {
            if (token.IsCancellationRequested) throw NotSent(token);
            var args = new JsonObject { ["candidate"] = JsonSerializer.SerializeToNode(candidate), ["mode"] = mode };
            var reply = CandidateWire.Source(worker.Call(WorkerOperations.SourceCandidate, args, token).GetAwaiter().GetResult(), args);
            if (reply.Fault?.Kind == "ambiguous") throw new PlcPathException(reply.Fault.Subject, reply.PathCandidates, true);
            return reply;
        }
        public SourceObservation Observe()
        {
            var reply = Call(new() { Request = request }, "preview");
            if (reply.Fault != null) throw new CandidateObservationException(reply.Fault);
            return reply.Observation!;
        }
        public SourceAttempt Execute(SourceCheck check, IDictionary<string, Stream> locks)
        {
            try { var reply = Call(new() { Action = "execute", Request = request, Check = check }, "apply"); return reply.Fault != null ? new() { Fault = reply.Fault } : reply.Attempt!; }
            catch (Exception error)
            { bool issued = Unknown(error); return new() { Issued = issued, RequiresSessionReset = issued, Fault = new() { Kind = "preflight", Subject = "worker-channel" } }; }
        }
        public void BeforeAction(SourceItem item) => throw new InvalidOperationException("Use the synchronous source boundary.");
        public SourceRow Import(SourceItem item, string[] names) => throw new InvalidOperationException("Use the synchronous source boundary.");
        public string[]? Generate(SourceRow row) => throw new InvalidOperationException("Use the synchronous source boundary.");
        public void Delete(SourceRow row) => throw new InvalidOperationException("Use the synchronous source boundary.");
    }
}
