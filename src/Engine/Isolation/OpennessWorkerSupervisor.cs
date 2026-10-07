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
using TiaMcp.Logic.V4;

namespace TiaMcpServer.Isolation
{
    internal sealed class WorkerCallException : Exception
    {
        internal bool OutcomeUnknown { get; }
        internal bool Dispatched { get; }
        internal ErrorDetails Details { get; }
        internal WorkerCallException(string reason, bool dispatched, ErrorDetails? details = null) : base(reason)
        {
            Dispatched = dispatched;
            Data[SessionToolContract.WorkerDispatchedExceptionDataKey] = dispatched;
            OutcomeUnknown = dispatched;
            Details = details ?? new ResourceUnavailableDetails("openness-worker");
        }
    }

    internal sealed class OpennessWorkerSupervisor : IDisposable
    {
        private readonly object sync = new object();
        // The child takes the exclusive portal lane after approval. The parent
        // serializes only short pipe writes and startup, so local worker tools
        // (including worker-owned exports) can complete during a native call.
        private readonly SemaphoreSlim gate = new SemaphoreSlim(8, 8);
        private readonly SemaphoreSlim localGate = new SemaphoreSlim(8, 8);
        private readonly SemaphoreSlim startup = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, string> active = new Dictionary<string, string>();
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
        private bool attachAttempted;
        private const int QueueLimit = 16;
        private const string FirstAttachTimeoutHint = " A first attach from a new worker or build may be waiting for TIA Portal Openness access confirmation; choose ‘Yes’ or ‘Yes to all’ on the TIA machine.";

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
                state = "NotStarted"; fault = null; bindingRequired = true; attachAttempted = false; epoch++;
            }
            old?.Dispose();
            InvocationJournal.Write(Guid.NewGuid().ToString("N"), "worker:restart", "RESET");
            return new JsonObject { ["success"] = true, ["dryRun"] = false, ["worker"] = Snapshot(),
                ["requiresExplicitProjectBinding"] = true, ["previousNativeOutcome"] = "Unknown after a fault; no operation was replayed or saved." };
        }

        internal async Task<JsonObject> CallAsync(JsonObject parameters, Action<JsonObject>? progress, CancellationToken cancellation)
        {
            string effectiveName;
            JsonObject? effectiveArguments = parameters["arguments"] as JsonObject;
            try
            {
                effectiveName = parameters["name"]?.GetValue<string>() ?? "";
                if (string.Equals(effectiveName, "CallTool", StringComparison.OrdinalIgnoreCase))
                {
                    effectiveName = effectiveArguments?["name"]?.GetValue<string>()?.Trim() ?? "";
                    effectiveArguments = effectiveArguments?["arguments"] as JsonObject;
                }
            }
            catch (InvalidOperationException)
            { throw new WorkerCallException("Worker request arguments are invalid; no operation was dispatched.", false,
                new InvalidArgumentDetails("arguments", Array.Empty<string>())); }
            bool binding = string.Equals(effectiveName, "ConnectProject", StringComparison.OrdinalIgnoreCase) || string.Equals(effectiveName, "ConnectIsolatedPortal", StringComparison.OrdinalIgnoreCase) ||
                ((string.Equals(effectiveName, "ConnectPortal", StringComparison.OrdinalIgnoreCase) || string.Equals(effectiveName, "AttachOpenProject", StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrWhiteSpace(effectiveArguments?["projectName"]?.ToString()));
            var diagnostic = new[] { "GetSessionState", "InitializeEnvironment", "FindTools", "GetToolSchema", "ListToolCategories", "GetToolUsage", "PreviewToolCall", "GetExportContent", "ListExportHandles" }
                .Contains(effectiveName, StringComparer.OrdinalIgnoreCase);
            long ticket;
            lock (sync)
            {
                if (disposed) throw new WorkerCallException("Worker is stopped; no operation was dispatched.", false);
                if (state == "Faulted") throw new WorkerCallException("Worker unavailable; explicit restart and project rebind required.", false,
                    new SessionResetRequiredDetails("openness-worker-fault"));
                if (bindingRequired && !binding && !diagnostic) throw new WorkerCallException("Recovery requires an explicit ConnectProject, named ConnectPortal/AttachOpenProject or ConnectIsolatedPortal before other tools. No call was dispatched.", false,
                    new ProjectNotBoundDetails());
                if (admitted >= QueueLimit) throw new WorkerCallException("Worker queue is full; request was not dispatched.", false,
                    new LimitExceededDetails("workerQueue", QueueLimit, admitted));
                admitted++; ticket = epoch;
            }
            bool entered = false, dispatched = false, firstAttach = false, startingWorker = false;
            var elapsed = Stopwatch.StartNew();
            string id = Guid.NewGuid().ToString("N");
            string name = parameters["name"]?.GetValue<string>() ?? "unknown";
            var lane = ToolTaxonomy.UsesOpennessLane(effectiveName) ? gate : localGate;
            try
            {
                entered = await lane.WaitAsync(deadline, cancellation).ConfigureAwait(false);
                if (!entered) throw new WorkerCallException("Request expired while queued; not dispatched.", false, new TimeoutDetails("worker-queue"));
                lock (sync)
                {
                    if (disposed || state == "Faulted" || ticket != epoch) throw new WorkerCallException("Worker generation changed while queued; not dispatched.", false,
                        new SessionResetRequiredDetails("openness-worker-generation"));
                    active[id] = name; activeTool = name;
                }
                cancellation.ThrowIfCancellationRequested();
                await startup.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                    lock (sync) startingWorker = state == "NotStarted";
                    await EnsureStarted(elapsed, cancellation).ConfigureAwait(false);
                }
                finally { startup.Release(); }
                cancellation.ThrowIfCancellationRequested();
                if (elapsed.Elapsed >= deadline) throw new WorkerCallException("Request expired before dispatch.", false, new TimeoutDetails("worker-dispatch"));
                WorkerConnection worker;
                lock (sync)
                {
                    if (state != "Ready" || ticket != epoch) throw new WorkerCallException("Worker is not ready; request was not dispatched.", false);
                    worker = connection!;
                }
                if (parameters.ToJsonString().Length > WorkerConnection.MaxFrameChars - 1024)
                    throw new WorkerCallException("Request exceeds the worker frame limit; not dispatched.", false,
                        new LimitExceededDetails("workerFrame", WorkerConnection.MaxFrameChars - 1024, parameters.ToJsonString().Length));
                if (IsAttachRequest(effectiveName, effectiveArguments))
                {
                    lock (sync) { firstAttach = !attachAttempted; attachAttempted = true; }
                }
                // Own correlation data; never rely on caller-supplied metadata for the dispatch record.
                var meta = parameters["_meta"] as JsonObject;
                if (meta == null) parameters["_meta"] = meta = new JsonObject();
                meta["tiaMcpWorkerCorrelation"] = id;
                meta["tiaMcpWorkerGeneration"] = ticket;
                InvocationJournal.Write(id, "worker:" + name, "BEFORE");
                dispatched = true; // A partially written pipe request also has an unknown outcome.
                InvocationJournal.NativeCallStarted(); // The host scope must include native calls executed in this worker.
                var approvalWait = new ApprovalWaitBudget();
                Action<JsonObject> notifications = frame =>
                {
                    if (frame["params"] is JsonObject details && details["tiaApprovalWait"] is JsonValue phase
                        && phase.TryGetValue<string>(out var signal) && details["seconds"] is JsonValue seconds && seconds.TryGetValue<int>(out var count))
                        approvalWait.Signal(signal, count);
                    else progress?.Invoke(frame);
                };
                var result = await Within(worker.RequestAsync("tools/call", parameters, notifications), elapsed, cancellation, approvalWait).ConfigureAwait(false);
                if (result["error"] == null && result["result"]?["content"] is not JsonArray)
                    throw new IOException("Malformed worker tool result.");
                // Validate SDK content before releasing the gate. A malformed typed result
                // must invalidate this generation before another queued call can execute.
                if (result["error"] == null) ValidateResult(result);
                else if (result["error"]?["code"]?.GetValue<int>() != -32602 && result["error"]?["code"]?.GetValue<int>() != -32601)
                    throw new IOException("Worker failed without a tool outcome.");
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
                else if (startingWorker && state == "Starting") Fault("StartupCancelled");
                throw new WorkerCallException(dispatched ? "Caller cancelled after dispatch; native outcome unknown. No replay." : "Cancelled before dispatch.", dispatched,
                    new CancelledDetails("worker-dispatch"));
            }
            catch (Exception ex)
            {
                Fault(ex is TimeoutException ? "DeadlineExceeded" : "WorkerFailure");
                string timeoutHint = firstAttach && ex is TimeoutException ? FirstAttachTimeoutHint : "";
                throw new WorkerCallException(dispatched ? "Worker failed or exceeded its deadline after dispatch; native outcome unknown. No replay." + timeoutHint : "Worker could not start/validate; tool was not dispatched.", dispatched,
                    ex is TimeoutException ? (ErrorDetails)new TimeoutDetails(firstAttach ? "first-attach-openness-confirmation" : "worker-startup") : new ResourceUnavailableDetails("openness-worker"));
            }
            finally
            {
                lock (sync) { admitted--; active.Remove(id); activeTool = active.Values.LastOrDefault(); }
                if (entered) lane.Release();
            }
        }

        private static bool IsAttachRequest(string name, JsonObject? arguments)
        {
            if (name is "ConnectPortal" or "ConnectProject" or "ConnectIsolatedPortal" or "AttachOpenProject" or "OpenProject" or "CreateProject") return true;
            return name == "SessionCandidate" && (string?)arguments?["candidate"]?["Check"]?["Request"]?["Action"] == "attach";
        }

        private async Task<T> Within<T>(Task<T> task, Stopwatch elapsed, CancellationToken cancellation, ApprovalWaitBudget? approvalWait = null)
        {
            _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            while (!task.IsCompleted)
            {
                cancellation.ThrowIfCancellationRequested();
                var remaining = deadline - elapsed.Elapsed + (approvalWait?.Excluded ?? TimeSpan.Zero);
                if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                var delay = Task.Delay(approvalWait == null || remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100), timer.Token);
                var done = await Task.WhenAny(task, delay).ConfigureAwait(false);
                if (done == task) break;
            }
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
        private static Envelope ValidateResult(JsonObject reply)
        {
            var result = JsonSerializer.Deserialize<CallToolResult>(reply["result"]!.ToJsonString(), McpJsonUtilities.DefaultOptions)
                ?? throw new IOException("Missing worker tool result.");
            if (result.Content.Count != 1 || result.Content[0] is not TextContentBlock text || result.StructuredContent == null
                || !JsonNode.DeepEquals(JsonNode.Parse(text.Text), result.StructuredContent))
                throw new IOException("Worker did not return matching V4 content.");
            var envelope = V4Json.Deserialize<Envelope>(text.Text);
            if (result.IsError != !envelope.Ok) throw new IOException("Worker tool status disagrees with its V4 envelope.");
            return envelope;
        }
        private static bool SuccessfulBinding(JsonObject envelope)
        {
            try
            {
                // CallTool returns the target's own result, just like a direct call.
                return envelope["error"] == null && ValidateResult(envelope).Ok;
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
