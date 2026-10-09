using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TiaMcp.WorkerChannel
{
    public sealed class ChannelClient : IDisposable
    {
        private readonly object gate = new object();
        private readonly Stream input, output;
        private readonly ChannelIdentity identity;
        private readonly ChannelProfile profile;
        private readonly Action<string>? progress;
        private readonly TaskCompletionSource<bool> hello = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly Task receiver;
        private Pending? pending;
        private long sequence, epoch;
        private bool verified, poisoned, recoveryOnly, unknown, disposed;
        private Exception? lastFault;
        public Exception? LastFault { get { lock (gate) return lastFault; } }
        public bool Poisoned { get { lock (gate) return poisoned || recoveryOnly; } }
        public bool CanDisconnect { get { lock (gate) return verified && !poisoned && !disposed && pending == null; } }
        public bool InFlight { get { lock (gate) return pending?.Dispatched == true && !pending.Replied; } }
        public bool OutcomeUnknown { get { lock (gate) return unknown; } }
        public long BindingEpoch { get { lock (gate) return epoch; } }
        public long LastRequestId { get { lock (gate) return sequence; } }
        public void Invalidate(Exception cause) { Poison(cause); }

        public ChannelClient(Stream workerOutput, Stream workerInput, ChannelIdentity expected,
            ChannelProfile profile = ChannelProfile.Foundation, Action<string>? progress = null)
        {
            input = workerOutput; output = workerInput; identity = expected;
            this.profile = profile; this.progress = progress;
            receiver = Task.Run(Receive);
        }

        public async Task ConnectAsync(TimeSpan timeout, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await Bounded(hello.Task, timeout, token).ConfigureAwait(false);
                lock (gate) RequireUsable();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { throw Poison(ex); }
        }

        public async Task<string> CallAsync(string method, string argumentsJson, BindingChange bindingChange,
            bool readOnly, TimeSpan timeout, CancellationToken token = default, bool firstAttach = false, string? correlationId = null)
        {
            token.ThrowIfCancellationRequested();
            Pending call;
            Task<ChannelResponse> exchange;
            byte[] bytes;
            lock (gate)
            {
                RequireUsable(method);
                if (!verified) throw Poison(new IOException("Worker hello has not been verified."));
                if (pending != null) throw Poison(new IOException("Concurrent worker call; session stopped."));
                if (!ChannelCodec.ValidMethod(method, profile)) throw new ArgumentException(profile == ChannelProfile.Foundation
                    ? "Worker method must use adapter.<operation>." : profile == ChannelProfile.Engine ? "Worker method must use engine.<operation>." : "Invalid Studio method.");
                if (correlationId != null && (profile != ChannelProfile.Engine || correlationId.Length == 0 || correlationId.Length > 128))
                    throw new ArgumentException("Correlation is a bounded Engine profile request identity.");
                try { bytes = ChannelCodec.Request(checked(sequence + 1), method, argumentsJson, epoch, correlationId); }
                catch (ChannelLimitException) when (profile == ChannelProfile.Engine) { throw; }
                catch (Exception ex) { throw Poison(ex); }
                // Cancellation here is still an unsent call. No id or epoch is consumed.
                token.ThrowIfCancellationRequested();
                call = new Pending(++sequence, epoch, bindingChange, readOnly);
                pending = call;
                call.Dispatched = true; // Write may partially succeed before it fails.
                exchange = Exchange(bytes, call);
            }
            try
            {
                // One budget covers both writing and receiving; progress never resets it.
                var response = await Bounded(exchange, timeout, token, firstAttach).ConfigureAwait(false);
                lock (gate)
                {
                    if (profile == ChannelProfile.Studio || response.Failure?.Outcome != ChannelOutcome.Unknown) RequireUsable(method);
                }
                if (response.Failure != null) throw response.Failure;
                return response.ResultJson;
            }
            catch (ChannelFailure ex) when (ex.Outcome != ChannelOutcome.Unknown) { throw; }
            catch (ChannelFailure) { throw; } // An answered unknown reply permits only an idle detach.
            catch (Exception ex) { throw Poison(ex); }
            finally { lock (gate) if (ReferenceEquals(pending, call)) pending = null; }
        }

        private async Task<ChannelResponse> Exchange(byte[] bytes, Pending call)
        {
            await output.WriteAsync(bytes, 0, bytes.Length, lifetime.Token).ConfigureAwait(false);
            await output.FlushAsync(lifetime.Token).ConfigureAwait(false);
            return await call.Completion.Task.ConfigureAwait(false);
        }

        private async Task Receive()
        {
            var reader = new LineFraming(input, ChannelCodec.ResponseLimit);
            try
            {
                while (true)
                {
                    var bytes = await reader.ReadAsync(lifetime.Token).ConfigureAwait(false);
                    if (bytes == null) throw new IOException("Worker exited; native outcome is unknown.");
                    using (var document = ChannelCodec.Parse(bytes, ChannelCodec.ResponseLimit))
                    {
                        var root = document.RootElement;
                        string? progressPayload = null;
                        lock (gate)
                        {
                            RequireUsable("adapter.Disconnect");
                            if (!verified)
                            {
                                ChannelCodec.VerifyHello(root, identity);
                                verified = true;
                                if (!reader.HasBufferedData) hello.TrySetResult(true);
                                continue;
                            }
                            ChannelCodec.Version(root);
                            if (root.TryGetProperty("method", out _))
                            {
                                ChannelCodec.Fields(root, "jsonrpc", "method", "params");
                                if (ChannelCodec.Text(root, "method") != "progress") throw new IOException("Duplicate or late worker hello/notification.");
                                var progress = root.GetProperty("params");
                                ChannelCodec.Fields(progress, profile != ChannelProfile.Foundation ? new[] { "requestId", "sequence", "percent", "payload" } : new[] { "requestId", "sequence", "percent" });
                                if (pending == null || pending.Replied || ChannelCodec.Number(progress, "requestId") != pending.Id ||
                                    ChannelCodec.Number(progress, "sequence") != pending.Progress + 1 ||
                                    ChannelCodec.Number(progress, "percent") > 100 || ++pending.Progress > ChannelCodec.ProgressLimit)
                                    throw new IOException("Late, duplicate or unrelated worker progress.");
                                if (progress.TryGetProperty("payload", out var payload))
                                {
                                    if (payload.ValueKind != System.Text.Json.JsonValueKind.Object) throw new IOException("Invalid worker progress payload.");
                                    progressPayload = payload.GetRawText();
                                }
                            }
                            else
                            {
                                ChannelCodec.Fields(root, "jsonrpc", "id", "bindingEpochBefore", "bindingEpochAfter", "result", "error");
                                if (pending == null || pending.Replied || ChannelCodec.Number(root, "id") != pending.Id) throw new IOException("Unknown, old or duplicate worker reply id.");
                                var response = ChannelCodec.Response(root, profile);
                                long before = ChannelCodec.Number(root, "bindingEpochBefore"), after = ChannelCodec.Number(root, "bindingEpochAfter");
                                var change = response.Failure == null ? pending.Change
                                    : response.Failure.Outcome == ChannelOutcome.Unknown && pending.Change != BindingChange.None ? BindingChange.MayAdvance : BindingChange.None;
                                if (before != pending.Before || after < before ||
                                    (change == BindingChange.None && after != before) ||
                                    (change == BindingChange.Advance && after != checked(before + 1)) ||
                                    (change == BindingChange.MayAdvance && after != before && after != checked(before + 1)))
                                    throw new IOException("Unexpected worker binding epoch change.");
                                if (response.Failure?.Outcome == ChannelOutcome.ReadFailed && !pending.ReadOnly) throw new IOException("A write cannot report ReadFailed.");
                                pending.Replied = true;
                                epoch = after;
                                pending.Completion.TrySetResult(response);
                                if (profile != ChannelProfile.Studio && response.Failure?.Outcome == ChannelOutcome.Unknown)
                                {
                                    recoveryOnly = true;
                                    unknown = true;
                                    lastFault ??= new IOException("Worker reported an unknown native outcome.");
                                }
                            }
                        }
                        // Consumer callbacks must not hold the state lock or block timeout/cancel.
                        if (progressPayload != null) progress?.Invoke(progressPayload);
                    }
                }
            }
            catch (Exception ex) { Poison(ex); }
        }

        private void RequireUsable(string? method = null)
        {
            if (poisoned || disposed || recoveryOnly && method != "adapter.Disconnect") throw new ChannelFault("Previous native request has an unknown outcome. Inspect TIA before a new explicit session; requests are never replayed.", unknown);
        }

        private ChannelFault Poison(Exception cause)
        {
            lock (gate)
            {
                poisoned = true;
                lastFault ??= cause;
                unknown |= pending?.Dispatched == true;
                var fault = new ChannelFault(cause.Message, unknown, cause);
                hello.TrySetException(fault);
                pending?.Completion.TrySetException(fault);
                return fault;
            }
        }

        private static async Task<T> Bounded<T>(Task<T> task, TimeSpan timeout, CancellationToken token, bool firstAttach = false)
        {
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var delay = Task.Delay(timeout, cancel.Token);
                if (await Task.WhenAny(task, delay).ConfigureAwait(false) != task)
                {
                    // Observe failures from a pipe operation that completes after the deadline.
                    _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    token.ThrowIfCancellationRequested();
                    throw new TimeoutException(firstAttach
                        ? "Worker timed out; native outcome is unknown. A first attach from a new worker or build may be waiting for TIA Portal Openness access confirmation; choose ‘Yes’ or ‘Yes to all’ on the TIA machine."
                        : "Worker timed out; native outcome is unknown.");
                }
                cancel.Cancel();
                return await task.ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                Poison(new ObjectDisposedException(nameof(ChannelClient)));
            }
            lifetime.Cancel();
            try { output.Dispose(); }
            finally { input.Dispose(); }
            // No process termination and no wait for a potentially running native call.
        }

        private sealed class Pending
        {
            internal readonly long Id, Before;
            internal readonly BindingChange Change;
            internal readonly bool ReadOnly;
            internal bool Dispatched, Replied;
            internal int Progress;
            internal readonly TaskCompletionSource<ChannelResponse> Completion = new TaskCompletionSource<ChannelResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Pending(long id, long before, BindingChange change, bool readOnly) { Id = id; Before = before; Change = change; ReadOnly = readOnly; }
        }
    }
}
