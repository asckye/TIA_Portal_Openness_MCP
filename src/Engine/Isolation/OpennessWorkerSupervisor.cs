using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Isolation
{
    internal sealed class WorkerCallException : Exception
    {
        internal bool OutcomeUnknown { get; }
        internal WorkerCallException(string reason, bool unknown) : base(reason) { OutcomeUnknown = unknown; }
    }

    internal sealed class OpennessWorkerSupervisor : IDisposable
    {
        private readonly object sync = new object();
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly Func<ProcessStartInfo> start;
        private readonly int major;
        private readonly string hash;
        private readonly string[] tools;
        private readonly TimeSpan deadline;
        private WorkerConnection? connection;
        private string state = "NotStarted";
        private string? fault;
        private string? activeTool;
        private long epoch = 1;
        private int admitted;
        private bool disposed;
        private bool bindingRequired;
        private const int QueueLimit = 16;

        internal OpennessWorkerSupervisor(Func<ProcessStartInfo> start, int major, string hash, IEnumerable<string> tools, TimeSpan deadline)
        {
            this.start = start; this.major = major; this.hash = hash;
            this.tools = tools.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            this.deadline = deadline;
        }

        internal JsonObject Snapshot()
        {
            lock (sync) return new JsonObject {
                ["enabled"] = true, ["state"] = state, ["fault"] = fault, ["workerPid"] = connection?.Pid,
                ["generation"] = epoch, ["activeTool"] = activeTool, ["admittedCalls"] = admitted,
                ["explicitBindingRequired"] = bindingRequired,
                ["queueLimit"] = QueueLimit, ["timeoutSeconds"] = deadline.TotalSeconds,
                ["nativeAcceptance"] = "Not established by local fault tests", ["automaticReplay"] = false,
                ["recovery"] = "After a fault, explicitly restart the worker and explicitly bind the intended project again. Old exports, plans and object handles are invalid."
            };
        }

        private void Fault(string reason, long? expectedEpoch = null)
        {
            WorkerConnection? old;
            lock (sync)
            {
                if (disposed || state == "Faulted" || (expectedEpoch.HasValue && expectedEpoch.Value != epoch)) return;
                state = "Faulted"; fault = reason; epoch++;
                bindingRequired = true;
                old = connection;
            }
            InvocationJournal.Write(Guid.NewGuid().ToString("N"), "worker:" + reason, "FAULTED");
            old?.Dispose();
        }

        internal JsonObject Restart(bool confirm)
        {
            WorkerConnection? old;
            lock (sync)
            {
                if (disposed) throw new InvalidOperationException("Supervisor is stopped.");
                if (!confirm) return new JsonObject { ["success"] = true, ["dryRun"] = true, ["worker"] = Snapshot(), ["requiresExplicitProjectBinding"] = true };
                if (admitted != 0) return new JsonObject { ["success"] = false, ["reason"] = "Calls are active or queued; wait for their results before restarting." };
                old = connection; connection = null;
                state = "NotStarted"; fault = null; bindingRequired = true; epoch++;
            }
            old?.Dispose();
            InvocationJournal.Write(Guid.NewGuid().ToString("N"), "worker:restart", "RESET");
            return new JsonObject { ["success"] = true, ["dryRun"] = false, ["worker"] = Snapshot(),
                ["requiresExplicitProjectBinding"] = true, ["previousNativeOutcome"] = "Unknown after a fault; no operation was replayed or saved." };
        }

        internal async Task<JsonObject> CallAsync(JsonObject parameters, Action<JsonObject>? progress, CancellationToken cancellation)
        {
            string effectiveName = parameters["name"]?.GetValue<string>() ?? "";
            JsonObject? effectiveArguments = parameters["arguments"] as JsonObject;
            if (string.Equals(effectiveName, "CallTool", StringComparison.OrdinalIgnoreCase))
            {
                effectiveName = effectiveArguments?["name"]?.GetValue<string>()?.Trim() ?? "";
                effectiveArguments = effectiveArguments?["arguments"] as JsonObject;
            }
            bool binding = string.Equals(effectiveName, "ConnectToProject", StringComparison.OrdinalIgnoreCase) || string.Equals(effectiveName, "ConnectIsolated", StringComparison.OrdinalIgnoreCase) ||
                ((string.Equals(effectiveName, "Connect", StringComparison.OrdinalIgnoreCase) || string.Equals(effectiveName, "AttachToOpenProject", StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrWhiteSpace(effectiveArguments?["projectName"]?.ToString()));
            var diagnostic = new[] { "GetState", "Bootstrap", "FindTools", "GetToolSchema", "ListToolCategories", "GetToolUsage", "PreviewToolCall", "GetExportContent", "ListExportHandles" }
                .Contains(effectiveName, StringComparer.OrdinalIgnoreCase);
            long ticket;
            lock (sync)
            {
                if (disposed || state == "Faulted") throw new WorkerCallException("Worker unavailable; explicit restart and project rebind required.", false);
                if (bindingRequired && !binding && !diagnostic) throw new WorkerCallException("Recovery requires an explicit ConnectToProject, named Connect/AttachToOpenProject or ConnectIsolated before other tools. No call was dispatched.", false);
                if (admitted >= QueueLimit) throw new WorkerCallException("Worker queue is full; request was not dispatched.", false);
                admitted++; ticket = epoch;
            }
            bool entered = false, dispatched = false;
            var elapsed = Stopwatch.StartNew();
            string id = Guid.NewGuid().ToString("N");
            string name = parameters["name"]?.GetValue<string>() ?? "unknown";
            try
            {
                entered = await gate.WaitAsync(deadline, cancellation).ConfigureAwait(false);
                if (!entered) throw new WorkerCallException("Request expired while queued; not dispatched.", false);
                lock (sync)
                {
                    if (disposed || state == "Faulted" || ticket != epoch) throw new WorkerCallException("Worker generation changed while queued; not dispatched.", false);
                    activeTool = name;
                }
                cancellation.ThrowIfCancellationRequested();
                await EnsureStarted(elapsed, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                if (elapsed.Elapsed >= deadline) throw new WorkerCallException("Request expired before dispatch.", false);
                WorkerConnection worker;
                lock (sync)
                {
                    if (state != "Ready" || ticket != epoch) throw new WorkerCallException("Worker is not ready; request was not dispatched.", false);
                    worker = connection!;
                }
                if (parameters.ToJsonString().Length > WorkerConnection.MaxFrameChars - 1024)
                    throw new WorkerCallException("Request exceeds the worker frame limit; not dispatched.", false);
                // Own correlation data; never rely on caller-supplied metadata for the dispatch record.
                var meta = parameters["_meta"] as JsonObject;
                if (meta == null) parameters["_meta"] = meta = new JsonObject();
                meta["tiaMcpWorkerCorrelation"] = id;
                meta["tiaMcpWorkerGeneration"] = ticket;
                InvocationJournal.Write(id, "worker:" + name, "BEFORE");
                dispatched = true; // A partially written pipe request also has an unknown outcome.
                var result = await Within(worker.RequestAsync("tools/call", parameters, progress), elapsed, cancellation).ConfigureAwait(false);
                if (result["error"] == null && result["result"]?["content"] is not JsonArray)
                    throw new IOException("Malformed worker tool result.");
                // Validate SDK content before releasing the gate. A malformed typed result
                // must invalidate this generation before another queued call can execute.
                if (result["error"] == null && JsonSerializer.Deserialize<CallToolResult>(
                    result["result"]!.ToJsonString(), McpJsonUtilities.DefaultOptions) == null)
                    throw new IOException("Missing worker tool result.");
                lock (sync)
                    if (state != "Ready" || epoch != ticket) throw new IOException("Worker failed before the result was accepted.");
                InvocationJournal.Write(id, "worker:" + name, "RETURNED");
                if (binding && SuccessfulBinding(result)) lock (sync) { if (epoch == ticket && state == "Ready") bindingRequired = false; }
                return result;
            }
            catch (WorkerCallException) { throw; }
            catch (OperationCanceledException)
            {
                if (dispatched) Fault("CancelledAfterDispatch");
                else if (entered && state == "Starting") Fault("StartupCancelled");
                throw new WorkerCallException(dispatched ? "Caller cancelled after dispatch; native outcome unknown. No replay." : "Cancelled before dispatch.", dispatched);
            }
            catch (Exception ex)
            {
                Fault(ex is TimeoutException ? "DeadlineExceeded" : "WorkerFailure");
                throw new WorkerCallException(dispatched ? "Worker failed or exceeded its deadline after dispatch; native outcome unknown. No replay." : "Worker could not start/validate; tool was not dispatched.", dispatched);
            }
            finally
            {
                lock (sync) { admitted--; if (entered) activeTool = null; }
                if (entered) gate.Release();
            }
        }

        private async Task<T> Within<T>(Task<T> task, Stopwatch elapsed, CancellationToken cancellation)
        {
            _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var remaining = deadline - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException();
            var delay = Task.Delay(remaining, timer.Token);
            var done = await Task.WhenAny(task, delay).ConfigureAwait(false);
            if (done != task && !task.IsCompleted) { cancellation.ThrowIfCancellationRequested(); throw new TimeoutException(); }
            timer.Cancel();
            return await task.ConfigureAwait(false);
        }

        private async Task EnsureStarted(Stopwatch elapsed, CancellationToken cancellation)
        {
            WorkerConnection worker;
            lock (sync)
            {
                if (state == "Ready") return;
                if (state != "NotStarted") throw new IOException("Worker cannot start from " + state);
                state = "Starting";
                // StartProcess is local and contains no native calls. Failed callbacks are generation-bound.
                long generation = epoch;
                connection = worker = new WorkerConnection(start(), reason => Fault(reason, generation));
            }
            var hello = await Within(worker.Hello, elapsed, cancellation).ConfigureAwait(false);
            if (hello["kind"]?.GetValue<string>() != "tia-openness-worker" || hello["protocol"]?.GetValue<int>() != 1 ||
                hello["engineMajor"]?.GetValue<int>() != major || hello["engineSha256"]?.GetValue<string>() != hash || hello["pid"]?.GetValue<int>() != worker.Pid)
                throw new IOException("Worker identity mismatch.");
            var init = await Within(worker.RequestAsync("initialize", new JsonObject {
                ["protocolVersion"] = "2024-11-05", ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "TIA MCP isolation host", ["version"] = "1" }
            }), elapsed, cancellation).ConfigureAwait(false);
            if (init["result"]?["protocolVersion"]?.GetValue<string>() != "2024-11-05" || init["result"]?["capabilities"]?["tools"] == null)
                throw new IOException("Worker initialization mismatch.");
            await Within(AsResult(worker.NotifyInitializedAsync()), elapsed, cancellation).ConfigureAwait(false);
            var names = new List<string>();
            string? cursor = null;
            for (int page = 0; page < 50; page++)
            {
                var args = new JsonObject(); if (cursor != null) args["cursor"] = cursor;
                var list = await Within(worker.RequestAsync("tools/list", args), elapsed, cancellation).ConfigureAwait(false);
                var items = list["result"]?["tools"] as JsonArray ?? throw new IOException("Worker tool catalog missing.");
                names.AddRange(items.Select(item => item!["name"]!.GetValue<string>()));
                cursor = list["result"]?["nextCursor"]?.GetValue<string>();
                if (string.IsNullOrEmpty(cursor)) break;
            }
            if (!string.IsNullOrEmpty(cursor) || !names.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(tools))
                throw new IOException("Worker capabilities differ from host.");
            lock (sync)
            {
                if (state != "Starting") throw new IOException("Worker failed during startup.");
                state = "Ready";
            }
        }

        private static async Task<bool> AsResult(Task task) { await task.ConfigureAwait(false); return true; }
        private static bool SuccessfulBinding(JsonObject envelope)
        {
            try
            {
                if (envelope["error"] != null || envelope["result"]?["isError"]?.GetValue<bool>() == true) return false;
                var content = envelope["result"]?["content"] as JsonArray;
                if (content == null || content.Count != 1) return false;
                var payload = JsonNode.Parse(content[0]!["text"]!.GetValue<string>()) as JsonObject;
                var meta = payload?["meta"] ?? payload?["Meta"];
                // CallTool returns the target's own result, just like a direct call.
                return payload?["schemaVersion"]?.GetValue<int?>() == 4
                    ? payload["ok"]?.GetValue<bool>() == true
                    : meta?["success"]?.GetValue<bool>() == true;
            }
            catch /* swallow(parse-fallback): an unreadable binding response cannot establish successful project binding */ { return false; }
        }
        public void Dispose()
        {
            WorkerConnection? old;
            lock (sync) { if (disposed) return; disposed = true; state = "Stopped"; epoch++; old = connection; }
            old?.Dispose();
        }
    }
}
