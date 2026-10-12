using System;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class ProgressHeartbeat
    {
        private static readonly AsyncLocal<Scope?> Active = new AsyncLocal<Scope?>();
        internal static IDisposable Begin(RequestContext<CallToolRequestParams>? request)
        {
            if (Active.Value != null || request?.Params?.ProgressToken == null || request.Server == null) return new EmptyScope();
            var token = System.Text.Json.JsonSerializer.SerializeToNode(request.Params.ProgressToken,
                global::ModelContextProtocol.McpJsonUtilities.DefaultOptions)!;
            var scope = new Scope(token, (payload, cancellation) => request.Server.SendNotificationAsync("notifications/progress", payload,
                cancellationToken: cancellation), TimeSpan.FromSeconds(15), request.Params.Name);
            Active.Value = scope;
            return scope;
        }
        internal static Func<JsonObject, Task>? CaptureSender()
        {
            var scope = Active.Value;
            return scope == null ? null : payload => scope.Report(payload, CancellationToken.None);
        }
        internal static IDisposable ForTest(Func<JsonObject, CancellationToken, Task> send, TimeSpan interval)
        {
            var scope = new Scope(JsonValue.Create("test-token")!, send, interval, "ProgressHeartbeat");
            Active.Value = scope;
            return scope;
        }
        private sealed class EmptyScope : IDisposable { public void Dispose() { } }
        private sealed class Scope : IDisposable
        {
            private readonly CancellationTokenSource stop = new CancellationTokenSource();
            private readonly JsonNode token;
            private readonly Func<JsonObject, CancellationToken, Task> send;
            private readonly TimeSpan interval;
            private readonly string tool;
            private readonly Task loop;
            private readonly SemaphoreSlim delivery = new SemaphoreSlim(1, 1);
            private long progress;
            internal Scope(JsonNode token, Func<JsonObject, CancellationToken, Task> send, TimeSpan interval, string tool)
            { this.token = token; this.send = send; this.interval = interval; this.tool = tool; loop = Run(); }
            internal long Next() => Interlocked.Increment(ref progress);
            internal async Task Report(JsonObject payload, CancellationToken cancellation)
            {
                await delivery.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                    payload["progress"] = Next();
                    payload.Remove("total");
                    await send(payload, cancellation).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellation.IsCancellationRequested)
                { InvocationJournal.Write(InvocationJournal.CorrelationId, tool, "PROGRESS_DELIVERY_FAILED",
                    details: new JsonObject { ["exceptionType"] = error.GetType().Name }); }
                finally { delivery.Release(); }
            }
            private async Task Run()
            {
                while (true)
                {
                    await Task.Delay(interval, stop.Token).ConfigureAwait(false);
                    var payload = new JsonObject { ["progressToken"] = token.DeepClone(),
                        ["message"] = "The tool request is still active; waiting for approval or operation completion." };
                    await Report(payload, stop.Token).ConfigureAwait(false);
                }
            }
            public void Dispose()
            {
                stop.Cancel();
                loop.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).GetAwaiter().GetResult();
                stop.Dispose();
                Active.Value = null;
            }
        }
    }
}
