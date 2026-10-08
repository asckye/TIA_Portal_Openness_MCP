using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;

[assembly: InternalsVisibleTo("TiaMcp.FoundationHost")]
[assembly: InternalsVisibleTo("TiaMcp.EngineHost.Tests")]
[assembly: InternalsVisibleTo("TiaMcpServer.LegacyHostTests")]

namespace TiaMcp.LegacyHost
{
    public sealed record EngineReply(CallToolResult Result, bool NativeCallIssued, JsonNode? Binding, JsonNode? Session, string? NativeFault);

    public interface IEngineWorker : IDisposable
    {
        bool Faulted { get; }
        JsonNode? Binding { get; }
        object SessionKey { get; }
        JsonObject Snapshot();
        Task<JsonObject> Status(CancellationToken token);
        Task<JsonObject> Restart(bool confirmed, CancellationToken token);
        Task<IDisposable> Acquire(CancellationToken token);
        Task<EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token);
    }
    public interface IEngineWorkerProgress
    {
        IDisposable UseProgress(Action<string>? report);
    }

    internal static class EngineHostConfiguration
    {
        internal static string ReleaseKey = "21";
        private static readonly AsyncLocal<EngineSessionContext?> Session = new();
        internal static EngineSessionContext Current => Session.Value is { Ended: false } context ? context
            : throw new InvalidOperationException("The MCP engine session is unavailable; initialize a new session.");
        internal static IEngineWorker Worker { get => Current.Worker; set => Session.Value = new EngineSessionContext(value); }
        internal static Func<TiaMcp.Logic.ModelContextProtocol.ImportStagingSession> StagingOwner { get => Current.StagingOwner!; set => Current.StagingOwner = value; }
        internal static IDisposable Enter(EngineSessionContext context)
        {
            var previous = Session.Value;
            Session.Value = context;
            return new SessionScope(() => Session.Value = previous);
        }
        private sealed class SessionScope(Action restore) : IDisposable { public void Dispose() => restore(); }
    }

    internal sealed class EngineSessionContext(IEngineWorker worker)
    {
        internal IEngineWorker Worker { get; } = worker;
        internal Func<TiaMcp.Logic.ModelContextProtocol.ImportStagingSession>? StagingOwner;
        internal IToolCatalogView? Catalog;
        internal IToolInvoker? Invoker;
        internal Func<bool> IsLiteProfile = () => false;
        internal ISet<string> LiteToolNames = new HashSet<string>(StringComparer.Ordinal);
        private readonly ConditionalWeakTable<object, BatchPlanStore> batchPlans = new();
        internal BatchPlanStore BatchPlans => batchPlans.GetValue(Worker.SessionKey, _ => new BatchPlanStore());
        internal SessionExports Exports { get; } = new();
        internal string? HttpSessionId;
        internal volatile bool Ended;
    }

    public sealed class EngineHostPipeline : IDisposable
    {
        private readonly ImportStagingHostLifetime stagingLifetime = new();
        private readonly EngineSessionContext context;
        public string Instructions { get; }
        public JsonArray BehaviorCapabilities { get; }
        public IList<McpServerTool> Tools { get; }
        public IReadOnlyList<McpServerTool> AllTools { get; }

        public EngineHostPipeline(string path, string workerPath, string releaseKey, IEngineWorker worker, string profile,
            IReadOnlyList<McpServerTool>? sharedTools = null, ISet<string>? sharedEssentials = null)
        {
            EngineHostConfiguration.ReleaseKey = releaseKey;
            context = new EngineSessionContext(worker);
            using var session = EnterSession();
            EngineHostConfiguration.StagingOwner = () => stagingLifetime.Session;
            var catalog = new EngineCatalog(path, workerPath, releaseKey);
            IToolCatalogView published = sharedTools == null ? catalog : new SharedToolCatalog(catalog, sharedTools, sharedEssentials!);
            Instructions = catalog.Instructions;
            BehaviorCapabilities = catalog.BehaviorCapabilities;
            McpServer.SetProfileOverride(profile);
            McpServer.ConfigureToolBridge(published, new WorkerToolInvoker(published, worker, stagingLifetime, sharedTools), McpServer.IsLiteProfile,
                new HashSet<string>(published.Lite.Select(t => t.Name), StringComparer.Ordinal));
            InvocationJournal.BindingSnapshot = () => EngineHostConfiguration.Worker.Binding as JsonObject;
            var selected = McpServer.IsLiteProfile() ? published.Lite : published.All.Values.ToArray();
            Tools = selected.SelectMany(t => t.Execution == "foundation"
                ? new[] { sharedTools!.Single(s => s.ProtocolTool.Name == t.Name) }
                : McpServer.WrapTools(new[] { McpServer.ToolInvoker.CreateTool(t) })).Select(t => (McpServerTool)new SessionTool(this, t)).ToList();
            AllTools = published.All.Values.Select(t => t.Execution == "foundation"
                ? sharedTools!.Single(s => s.ProtocolTool.Name == t.Name) : McpServer.ToolInvoker.CreateTool(t))
                .Select(t => (McpServerTool)new SessionTool(this, t)).ToArray();
        }

        internal IDisposable EnterSession()
        {
            if (context.Ended) throw new InvalidOperationException("The MCP engine session has ended; initialize a new session.");
            return EngineHostConfiguration.Enter(context);
        }
        internal bool SessionLocked
        {
            get { using var session = EnterSession(); return McpServer.SessionPrecheckRefusal("GetSessionState") != null; }
        }
        private sealed class SessionTool(EngineHostPipeline owner, McpServerTool inner) : McpServerTool
        {
            public override Tool ProtocolTool => inner.ProtocolTool;
            public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
            {
                if (owner.context.HttpSessionId != null && request.Server.SessionId != owner.context.HttpSessionId)
                    throw new InvalidOperationException("The tool belongs to a different MCP engine session.");
                using var session = owner.EnterSession();
                return await inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }
        public IDisposable RegisterHttpSession(string id)
        {
            ImportStagingTools.HttpTransport = true;
            context.HttpSessionId = id;
            var session = new TiaMcp.Logic.ModelContextProtocol.ImportStagingSession(id);
            context.StagingOwner = () => session;
            ImportStagingTools.RegisterHttpSession(session);
            return new HttpSession(session);
        }
        private sealed class HttpSession(TiaMcp.Logic.ModelContextProtocol.ImportStagingSession session) : IDisposable
        {
            public void Dispose() { ImportStagingTools.RemoveHttpSession(session); session.Dispose(); }
        }
        public void Dispose() { context.Ended = true; context.Exports.Dispose(); stagingLifetime.Dispose(); }

        public void RegisterHandlers(IMcpServerBuilder builder)
        {
            McpPromptRegistration.Configure(builder);
            builder.WithListResourcesHandler((_, token) => { token.ThrowIfCancellationRequested(); return new ValueTask<ListResourcesResult>(new ListResourcesResult { Resources = Array.Empty<Resource>() }); });
            builder.WithListResourceTemplatesHandler((_, token) => { token.ThrowIfCancellationRequested(); return new ValueTask<ListResourceTemplatesResult>(new ListResourceTemplatesResult { ResourceTemplates = Array.Empty<ResourceTemplate>() }); });
        }
    }
}

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        internal static string ReleaseKey => TiaMcp.LegacyHost.EngineHostConfiguration.ReleaseKey;
        internal static TiaMcp.LegacyHost.IEngineWorker Worker => TiaMcp.LegacyHost.EngineHostConfiguration.Worker;
        internal static CancellationToken WorkerDispatchCancellation => McpDispatchCancellation.Value;
        internal static IMcpServer? CurrentCallServer => ProgressRequest.Value?.Server;
        internal static Action<string>? WorkerProgressRelay()
        {
            var request = ProgressRequest.Value;
            var token = request?.Params?.ProgressToken;
            if (token == null) return null;
            var progressToken = JsonSerializer.SerializeToNode(token, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions);
            return json => {
                try
                {
                    var payload = JsonNode.Parse(json)!.AsObject();
                    payload["progressToken"] = progressToken!.DeepClone();
                    // A cancelled/disconnected MCP client cannot poison the native pipe.
                    _ = request!.Server.SendNotificationAsync("notifications/progress", payload, cancellationToken: CancellationToken.None)
                        .ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                }
                catch (Exception error)
                { InvocationJournal.Write(InvocationJournal.CorrelationId, request!.Params!.Name, "PROGRESS_DELIVERY_FAILED", details: new JsonObject { ["exceptionType"] = error.GetType().Name }); }
            };
        }
        internal static void MarkSharedSessionUncertain() => SessionFaults.GetValue(Worker.SessionKey, _ => new SessionFault()).Unknown = true;
        internal static TiaMcp.Logic.ModelContextProtocol.ImportStagingSession SharedStagingOwner => TiaMcp.LegacyHost.EngineHostConfiguration.StagingOwner();
        static partial void RecordBridgeEvent(string id, string name, string phase);
        static partial void RecordCallRejection(string name, TiaMcp.Logic.V4.Inputs.ToolArguments arguments, CallToolResult result);
        static partial void ApprovalSessionKey(ref object? key)
        {
            key = Worker.SessionKey;
            if (Worker.Faulted) SessionFaults.GetValue(key, _ => new SessionFault()).Unknown = true;
        }
        internal static void ObserveWorkerReply(string name, TiaMcp.LegacyHost.EngineReply reply)
        {
            if (reply.NativeCallIssued && (bool?)reply.Result.StructuredContent?["data"]?["nativeOutcomeUnknown"] == true
                && ToolTaxonomy.UsesOpennessLane(name))
                SessionFaults.GetValue(Worker.SessionKey, _ => new SessionFault()).Unknown = true;
        }
        static partial void IsolationParent(ref bool parent) => parent = false;
        static partial void ApprovalBindingIdentity(ref string? identity)
        {
            _ = Worker.Status(WorkerDispatchCancellation).GetAwaiter().GetResult();
            identity = Worker.Binding?.ToJsonString();
        }
        internal static void ValidateRuntimeTool(string name, string? description, string? operation = null)
        {
            if (ToolTaxonomy.UsesOpennessLane(name)) _ = Worker.Status(WorkerDispatchCancellation).GetAwaiter().GetResult();
        }
        static partial void ReadSessionState(ref bool? connected, ref string? project)
        {
            var status = Worker.Status(CancellationToken.None).GetAwaiter().GetResult();
            connected = (bool?)status["session"]?["isConnected"];
            project = (string?)status["session"]?["project"];
        }
    }
}
