using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TiaMcpServer
{
    /// <summary>
    /// Owns the only reader of the MCP response stream. HTTP deadlines remove
    /// waiters, never readers. Unique wire IDs isolate retries and HTTP sessions.
    /// </summary>
    internal sealed class McpHttpResponseRouter : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly StreamReader _reader;
        private readonly McpBlockingStream _requests;
        private readonly McpBlockingStream _responses;
        private readonly SemaphoreSlim _requestGate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _ended = new CancellationTokenSource();
        private readonly object _stateLock = new object();
        private readonly Dictionary<string, TaskCompletionSource<string>> _pending
            = new Dictionary<string, TaskCompletionSource<string>>(StringComparer.Ordinal);
        private Exception? _terminalError;
        private long _nextId;

        internal Task Completion { get; }

        internal McpHttpResponseRouter(McpBlockingStream requests, McpBlockingStream responses)
        {
            _requests = requests;
            _responses = responses;
            _writer = new StreamWriter(requests, new UTF8Encoding(false), 1024, leaveOpen: true)
                { NewLine = "\n", AutoFlush = true };
            _reader = new StreamReader(responses, new UTF8Encoding(false));
            Completion = Task.Factory.StartNew(ReadResponses, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        // Retain serialized dispatch. The deadline includes time spent queued;
        // a request that expires in the queue must not reach TIA later.
        internal async Task<string?> SendAsync(string body, TimeSpan timeout)
        {
            var request = JsonNode.Parse(body) as JsonObject
                ?? throw new ArgumentException("Expected a JSON-RPC object.", nameof(body));
            bool hasId = request.ContainsKey("id");
            var originalId = request["id"]?.DeepClone();
            var elapsed = Stopwatch.StartNew();
            try
            {
                if (!await _requestGate.WaitAsync(timeout, _ended.Token).ConfigureAwait(false))
                    throw new TimeoutException("MCP request expired while queued.");
            }
            catch (OperationCanceledException)
            {
                lock (_stateLock)
                    throw new IOException("MCP response stream is unavailable.", _terminalError);
            }

            string? wireId = null;
            TaskCompletionSource<string>? waiter = null;
            try
            {
                lock (_stateLock)
                {
                    if (_terminalError != null)
                        throw new IOException("MCP response stream is unavailable.", _terminalError);
                    if (elapsed.Elapsed >= timeout)
                        throw new TimeoutException("MCP request expired while queued.");
                    if (hasId)
                    {
                        wireId = "http_" + (++_nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _pending.Add(wireId, waiter);
                        request["id"] = wireId;
                    }
                    // Closing the writer and checking terminal state share this lock.
                    try { _writer.WriteLine(request.ToJsonString()); }
                    catch (Exception ex) when (ex is IOException || ex is InvalidOperationException)
                    {
                        // This waiter has not yet been awaited; do not fault an orphan task.
                        if (wireId != null) _pending.Remove(wireId);
                        FailPending(ex);
                        throw new IOException("MCP request stream is unavailable.", ex);
                    }
                }
                if (waiter == null) return null;

                using (var timerCancellation = new CancellationTokenSource())
                {
                    var remaining = timeout - elapsed.Elapsed;
                    var delay = Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, timerCancellation.Token);
                    try
                    {
                        var finished = await Task.WhenAny(waiter.Task, delay).ConfigureAwait(false);
                        if (finished != waiter.Task && !waiter.Task.IsCompleted)
                            throw new TimeoutException("MCP response deadline exceeded.");
                        var response = JsonNode.Parse(await waiter.Task.ConfigureAwait(false)) as JsonObject
                            ?? throw new IOException("Expected a JSON-RPC response object.");
                        response["id"] = originalId;
                        return response.ToJsonString();
                    }
                    finally { timerCancellation.Cancel(); }
                }
            }
            finally
            {
                if (wireId != null)
                {
                    lock (_stateLock) _pending.Remove(wireId);
                }
                _requestGate.Release();
            }
        }

        private void ReadResponses()
        {
            Exception ended = new EndOfStreamException("MCP response stream closed.");
            try
            {
                string? line;
                while ((line = _reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    string? wireId;
                    try
                    {
                        var response = JsonNode.Parse(line) as JsonObject;
                        if (response == null || response["method"] != null) continue;
                        wireId = response["id"]?.GetValue<string>();
                    }
                    catch { continue; }
                    if (wireId == null) continue;

                    lock (_stateLock)
                    {
                        if (!_pending.TryGetValue(wireId, out var waiter)) continue;
                        _pending.Remove(wireId);
                        waiter.TrySetResult(line);
                    }
                }
            }
            catch (Exception ex) { ended = ex; }
            finally
            {
                FailPending(ended);
                _reader.Dispose();
            }
        }

        private void FailPending(Exception error)
        {
            lock (_stateLock)
            {
                if (_terminalError != null) return;
                _terminalError = error;
                foreach (var waiter in _pending.Values) waiter.TrySetException(error);
                _pending.Clear();
                try { _writer.Dispose(); }
                catch (IOException) { }
                catch (InvalidOperationException) { }
                _requests.CompleteWriting();
            }
            _ended.Cancel(); // Also release callers still waiting for the dispatch gate.
            _responses.CompleteWriting();
        }

        public void Dispose()
        {
            FailPending(new ObjectDisposedException(nameof(McpHttpResponseRouter)));
            _responses.CompleteWriting(); // Unblock the lifetime reader before awaiting Completion.
        }
    }
}
