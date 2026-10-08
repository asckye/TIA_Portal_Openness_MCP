using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.WorkerChannel;

namespace TiaMcpServer.ModelContextProtocol
{
    // Native task continuations keep their existing thread ownership. Only queued
    // notification writes are pumped by the worker's owner while it waits.
    internal sealed class WorkerProgressShim : IMcpServer, IDisposable
    {
        [ThreadStatic] private static WorkerProgressShim? current;
        private readonly WorkerProgressShim? previous;
        private readonly ChannelRequest request;
        private readonly bool enabled;
        private readonly IServiceProvider services;
        private readonly ConcurrentQueue<JsonObject> notifications = new ConcurrentQueue<JsonObject>();
        private readonly AutoResetEvent changed = new AutoResetEvent(false);
        private int disposed;
        internal WorkerProgressShim(ChannelRequest request, bool enabled, IServiceProvider services)
        { this.request = request; this.enabled = enabled; this.services = services; previous = current; current = this; }
        public string? SessionId => null;
        public ClientCapabilities? ClientCapabilities => null;
        public Implementation? ClientInfo => null;
        public McpServerOptions ServerOptions { get; } = new McpServerOptions();
        public IServiceProvider Services => services;
        public LoggingLevel? LoggingLevel => null;
        public Task RunAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            if (enabled && Volatile.Read(ref disposed) == 0 && message is JsonRpcNotification notification && notification.Method == "notifications/progress")
            {
                var payload = JsonSerializer.SerializeToNode(notification.Params, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions)!.AsObject();
                payload.Remove("progressToken");
                notifications.Enqueue(payload); changed.Set();
            }
            return Task.CompletedTask;
        }
        public IAsyncDisposable RegisterNotificationHandler(string method, Func<JsonRpcNotification, CancellationToken, ValueTask> handler) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Dispose(); return default; }
        internal void Bind(MethodInfo method, object?[] values)
        {
            var context = new RequestContext<CallToolRequestParams>(this) {
                Params = JsonSerializer.Deserialize<CallToolRequestParams>(enabled ? "{\"name\":\"worker\",\"_meta\":{\"progressToken\":\"worker\"}}" : "{\"name\":\"worker\"}")
            };
            var parameters = method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].ParameterType == typeof(IMcpServer)) values[i] = this;
                if (parameters[i].ParameterType == typeof(RequestContext<CallToolRequestParams>)) values[i] = context;
            }
        }
        private void Drain()
        {
            while (notifications.TryDequeue(out var payload))
            {
                double progress = (double?)payload["progress"] ?? (double?)payload["Progress"] ?? 0;
                double total = (double?)payload["total"] ?? (double?)payload["Total"] ?? 0;
                int percent = total > 0 ? (int)Math.Max(0, Math.Min(100, progress * 100 / total)) : 0;
                request.ReportProgress(percent, payload.ToJsonString());
            }
        }
        internal static bool Wait(Task task)
        {
            if (current == null) return false;
            while (!task.IsCompleted) { current.Drain(); current.changed.WaitOne(10); }
            current.Drain(); task.GetAwaiter().GetResult(); return true;
        }
        public void Dispose() { Interlocked.Exchange(ref disposed, 1); Drain(); current = previous; changed.Dispose(); }
    }
    public static partial class McpServer
    {
        static partial void WaitToolTask(Task task, ref bool waited) => waited = WorkerProgressShim.Wait(task);
    }
}
