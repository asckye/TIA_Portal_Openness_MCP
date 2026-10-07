using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TiaMcpServer.Isolation
{
    // One reader, bounded outstanding requests, strict IDs. A broken channel is never reused.
    // The owner kills only this process, never the TIA process or its process tree.
    internal sealed class WorkerConnection : IDisposable
    {
        internal const int MaxFrameChars = 16 * 1024 * 1024;
        private readonly object sync = new object();
        private readonly Process process;
        private readonly StreamWriter stdin;
        private readonly Action<string> failed;
        private readonly TaskCompletionSource<JsonObject> hello = NewCompletion();
        private readonly Dictionary<string, (TaskCompletionSource<JsonObject> Completion, Action<JsonObject>? Notification)> pending =
            new Dictionary<string, (TaskCompletionSource<JsonObject>, Action<JsonObject>?)>();
        private readonly SemaphoreSlim writes = new SemaphoreSlim(1, 1);
        private bool disposed;
        private string? error;
        private long nextId;
        internal Task<JsonObject> Hello => hello.Task;
        internal int Pid { get; }
        internal Task ReaderCompletion { get; }

        private static TaskCompletionSource<JsonObject> NewCompletion() => new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);

        internal WorkerConnection(ProcessStartInfo start, Action<string> onFailure)
        {
            failed = onFailure;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
            start.StandardOutputEncoding = start.StandardErrorEncoding = new UTF8Encoding(false, true);
            process = new Process { StartInfo = start };
            try { if (!TiaOpenness.Shared.ChildProcessInput.Start(process)) throw new IOException("Worker start failed."); }
            catch { process.Dispose(); throw; }
            Pid = process.Id;
            // Framework Process.Dispose/Close only nulls the stream fields, so leaving
            // its original writer untouched cannot flush it during process cleanup.
            stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            // Observe faults even if a startup failure occurs before the caller awaits Hello.
            _ = hello.Task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            ReaderCompletion = Task.Factory.StartNew(Read, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            _ = Task.Factory.StartNew(() =>
            {
                try { var buffer = new char[2048]; while (process.StandardError.Read(buffer, 0, buffer.Length) > 0) { } }
                catch (Exception) /* swallow(privacy): worker stderr is drained without retention because it can contain tool input */ { /* Output is intentionally not retained: it can contain tool input. */ }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        internal async Task<JsonObject> RequestAsync(string method, JsonObject parameters, Action<JsonObject>? onNotification = null)
        {
            TaskCompletionSource<JsonObject> completion;
            string line;
            lock (sync)
            {
                if (error != null || disposed) throw new IOException("Worker channel is unavailable.");
                string id = "worker_" + (++nextId);
                if (method == "tools/call")
                {
                    var meta = parameters["_meta"] as JsonObject ?? new JsonObject();
                    parameters["_meta"] = meta;
                    meta["tiaMcpWorkerRequestId"] = id;
                }
                line = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }.ToJsonString();
                if (line.Length > MaxFrameChars) throw new ArgumentException("Worker request exceeds the frame limit.");
                completion = NewCompletion();
                pending.Add(id, (completion, onNotification));
                _ = completion.Task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            }
            try { await WriteLine(line).ConfigureAwait(false); }
            catch { Fail("RequestPipeFailed"); throw new IOException("Worker request pipe failed; outcome may be unknown."); }
            return await completion.Task.ConfigureAwait(false);
        }

        internal Task NotifyInitializedAsync() => WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        private async Task WriteLine(string line)
        {
            await writes.WaitAsync().ConfigureAwait(false);
            try { await stdin.WriteLineAsync(line).ConfigureAwait(false); }
            finally { writes.Release(); }
        }

        private static string? ReadLine(TextReader reader)
        {
            var buffer = new StringBuilder();
            while (true)
            {
                int ch = reader.Read();
                if (ch < 0)
                {
                    if (buffer.Length == 0) return null;
                    throw new IOException("Partial worker frame at EOF.");
                }
                if (ch == '\n') return buffer.ToString().TrimEnd('\r');
                if (buffer.Length >= MaxFrameChars) throw new IOException("Worker frame limit exceeded.");
                buffer.Append((char)ch);
            }
        }

        private void Read()
        {
            try
            {
                string? first = ReadLine(process.StandardOutput);
                if (first == null) throw new IOException("Worker exited before hello.");
                var greeting = JsonNode.Parse(first) as JsonObject ?? throw new IOException("Invalid worker hello.");
                hello.TrySetResult(greeting);
                string? line;
                while ((line = ReadLine(process.StandardOutput)) != null)
                {
                    var frame = JsonNode.Parse(line) as JsonObject ?? throw new IOException("Invalid worker frame.");
                    Action<JsonObject>? progress = null;
                    lock (sync)
                    {
                        if (disposed) return;
                        if (frame["jsonrpc"]?.GetValue<string>() != "2.0") throw new IOException("Invalid worker protocol.");
                        if (frame["method"] != null)
                        {
                            if (frame.ContainsKey("id")) throw new IOException("Unexpected worker-to-host request.");
                            // Only progress is forwarded; no child-originated sampling, requests or control actions.
                            if (frame["method"]!.GetValue<string>() == "notifications/progress")
                            {
                                string? requestId = (string?)frame["params"]?["tiaMcpWorkerRequestId"];
                                if (requestId != null && pending.TryGetValue(requestId, out var target)) progress = target.Notification;
                                else
                                {
                                    // Older/test workers send uncorrelated progress. Keep that
                                    // protocol compatible, without holding the reader lock during callbacks.
                                    var callbacks = pending.Values.Select(p => p.Notification).Where(p => p != null).ToArray();
                                    progress = value => { foreach (var callback in callbacks) callback!(value); };
                                }
                            }
                        }
                        else
                        {
                            string? id = frame["id"]?.GetValue<string>();
                            if (id == null || !pending.TryGetValue(id, out var waiter) ||
                                frame.ContainsKey("result") == frame.ContainsKey("error"))
                                throw new IOException("Worker response ID or envelope mismatch.");
                            pending.Remove(id);
                            waiter.Completion.TrySetResult(frame);
                        }
                    }
                    if (progress != null) progress(frame);
                }
                Fail("WorkerExited");
            }
            catch (Exception) /* swallow(privacy): fail pending calls with the fixed protocol error without exposing worker payloads */ { Fail("WorkerProtocolOrPipeFailure"); }
        }

        private void Fail(string reason)
        {
            lock (sync)
            {
                if (disposed || error != null) return;
                error = reason;
                hello.TrySetException(new IOException(reason));
                foreach (var waiter in pending.Values) waiter.Completion.TrySetException(new IOException(reason));
                pending.Clear();
            }
            failed(reason);
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                hello.TrySetException(new ObjectDisposedException(nameof(WorkerConnection)));
                foreach (var waiter in pending.Values) waiter.Completion.TrySetException(new ObjectDisposedException(nameof(WorkerConnection)));
                pending.Clear();
            }
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) /* swallow(teardown): the owned worker may exit before disposal reaches the kill request */ { } catch (System.ComponentModel.Win32Exception) /* swallow(teardown): failure to kill the owned worker must not replace the already terminated request outcome */ { }
            // Do not wait for TIA or dispose a writer while another thread is blocked in a large write.
            // Killing this owned process closes its pipe ends and releases both reader tasks.
            _ = ReaderCompletion.ContinueWith(_ => process.Dispose(), TaskScheduler.Default);
        }
    }
}
