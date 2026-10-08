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

    internal static class EngineHostConfiguration
    {
        internal static string ReleaseKey = "21";
        internal static IEngineWorker Worker = null!;
    }

    public sealed class EngineHostPipeline : IDisposable
    {
        private readonly ImportStagingHostLifetime stagingLifetime = new();
        public string Instructions { get; }
        public JsonArray BehaviorCapabilities { get; }
        public IList<McpServerTool> Tools { get; }

        public EngineHostPipeline(string path, string workerPath, string releaseKey, IEngineWorker worker, string profile)
        {
            EngineHostConfiguration.ReleaseKey = releaseKey;
            EngineHostConfiguration.Worker = worker;
            var catalog = new EngineCatalog(path, workerPath, releaseKey);
            Instructions = catalog.Instructions;
            BehaviorCapabilities = catalog.BehaviorCapabilities;
            McpServer.SetProfileOverride(profile);
            McpServer.ConfigureToolBridge(catalog, new WorkerToolInvoker(catalog, worker, stagingLifetime), McpServer.IsLiteProfile,
                new HashSet<string>(catalog.Lite.Select(t => t.Name), StringComparer.Ordinal));
            InvocationJournal.BindingSnapshot = () => worker.Binding as JsonObject;
            Tools = McpServer.WrapTools(McpServer.IsLiteProfile() ? McpServer.GetLiteTools() : McpServer.GetAllTools());
        }

        public IDisposable RegisterHttpSession(string id)
        {
            ImportStagingTools.HttpTransport = true;
            var session = new TiaMcp.Logic.ModelContextProtocol.ImportStagingSession(id);
            ImportStagingTools.RegisterHttpSession(session);
            return new HttpSession(session);
        }
        private sealed class HttpSession(TiaMcp.Logic.ModelContextProtocol.ImportStagingSession session) : IDisposable
        {
            public void Dispose() { ImportStagingTools.RemoveHttpSession(session); session.Dispose(); }
        }
        public void Dispose() => stagingLifetime.Dispose();

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
