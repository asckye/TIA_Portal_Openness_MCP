using System.Text.Json;
using TiaMcp.WorkerProtocol;
using TiaMcp.WorkerProtocol.JsonV2;

namespace TiaMcp.WorkerProtocol.AsyncPreview;

// Additive v2-only, single-flight async seam. No production launcher/wire integration.
public sealed class AsyncJsonSession
{
    readonly RequestGuard guard;
    readonly OuterIdStyle style;
    readonly Dictionary<string, OperationPolicy> policies;
    readonly TimeProvider clock;
    int active;
    public bool Faulted => guard.Faulted;
    public bool OutcomeUnknown => guard.OutcomeUnknown;

    public AsyncJsonSession(EngineIdentity selected, IEnumerable<OperationPolicy> policies,
        OuterIdStyle style, TimeProvider? clock = null)
    {
        Require(Enum.IsDefined(style), "OuterIdStyleInvalid");
        this.policies = policies.ToDictionary(p => p.Name, StringComparer.Ordinal);
        guard = new RequestGuard(selected, this.policies.Values);
        this.style = style; this.clock = clock ?? TimeProvider.System;
    }
    public void AcceptHello(ReadOnlyMemory<byte> frame)
    {
        Enter();
        try
        {
            try
            {
                var decoded = StrictCodec.Decode(frame);
                Require(decoded is HelloFrame, "HelloRequired");
                var hello = (HelloFrame)decoded;
                guard.AcceptHello(hello.Engine, hello.Binding);
            }
            catch { guard.TransportFailed(); throw new IdentityViolation("V2HandshakeRejected"); }
        }
        finally { Volatile.Write(ref active, 0); }
    }

    // timeout is one total monotonic budget, including dispatch, end-of-exchange,
    // enumerator/exchange disposal, result validation and commit. Validation must be
    // prompt and side-effect-free; it cannot be forcibly interrupted midway through.
    // Before dispatch failure leaves ownership with caller and guard usable. After the
    // first possible write every failure faults the guard and forbids automatic replay.
    public async Task<ValidatedReply> CallAsync(string operation, JsonElement arguments,
        IAsyncV2Exchange exchange, Action<JsonElement> validateResult, TimeSpan timeout,
        CancellationToken cancellationToken = default, ProjectIdentity? target = null,
        FrameLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(exchange); ArgumentNullException.ThrowIfNull(validateResult);
        Require(timeout > TimeSpan.Zero && timeout <= TimeSpan.FromHours(1), "DeadlineInvalid");
        limits ??= new FrameLimits();
        Enter();
        try
        {
            using var deadline = new Deadline(clock, timeout, cancellationToken);
            deadline.Check();
            var request = guard.Begin(operation, target);
            WireId id = style == OuterIdStyle.Numeric ? new WireId(request.RequestId) :
                new WireId("worker_" + request.RequestId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            byte[] bytes;
            try
            {
                bytes = StrictCodec.Encode(new RequestFrame(id, request, arguments));
                deadline.Check();
            }
            catch
            {
                guard.CancelBeforeDispatch(request);
                throw new IdentityViolation("V2RequestNotDispatched");
            }
            guard.MarkDispatchAttempt(request);
            IAsyncEnumerator<ReadOnlyMemory<byte>>? enumerator = null;
            bool enumeratorDisposeStarted = false, exchangeDisposeStarted = false;
            try
            {
                await deadline.Wait(exchange.DispatchAsync(bytes, deadline.Token)).ConfigureAwait(false);
                enumerator = exchange.ReadFramesAsync(limits, deadline.Token).GetAsyncEnumerator(deadline.Token);
                ReplyFrame? reply = null; long progressSequence = 0;
                int count = 0, total = 0;
                while (await deadline.Wait(enumerator.MoveNextAsync()).ConfigureAwait(false))
                {
                    var frame = enumerator.Current;
                    Require(reply == null, "TrailingFrameRejected");
                    Require(++count <= limits.MaxFrames && frame.Length > 0 && frame.Length <= limits.MaxFrameBytes &&
                        frame.Length <= limits.MaxExchangeBytes - total - 4, "ExchangeBoundsExceeded");
                    total += frame.Length + 4;
                    switch (StrictCodec.Decode(frame))
                    {
                        case ProgressFrame p:
                            Require(p.Id == id && request.Matches(p.Identity) && p.Sequence > progressSequence, "ProgressIdentityMismatch");
                            progressSequence = p.Sequence;
                            break;
                        case ReplyFrame r:
                            Require(r.Id == id, "OuterIdMismatch"); reply = r; break;
                        default: throw new IdentityViolation("UnexpectedFrame");
                    }
                    deadline.Check();
                }
                // Dispose BEFORE exposing the result or committing, so disposal failures
                // cannot turn an already-committed binding into an ambiguous success.
                enumeratorDisposeStarted = true;
                await deadline.Wait(enumerator.DisposeAsync()).ConfigureAwait(false);
                exchangeDisposeStarted = true;
                await deadline.Wait(exchange.DisposeAsync()).ConfigureAwait(false);
                Require(reply != null, "ReplyRequired");
                // Same pure preflight as reviewed sync JsonSession; authoritative state
                // transition remains RequestGuard.Complete, after result validation.
                Require(request.Matches(reply!.Identity.Request), "ReplyIdentityMismatch");
                Require(reply.Identity.Outcome != ReplyOutcome.Unknown, "NativeOutcomeUnknown");
                Require(reply.Identity.Outcome != ReplyOutcome.ReadFailed || policies[operation].ReadOnly, "WriteOutcomeCannotBeReadFailure");
                var expected = reply.Identity.Outcome == ReplyOutcome.Succeeded ? request.ExpectedAfter : request.Before;
                Require(expected.Matches(reply.Identity.ObservedAfter), "BindingIdentityChangedOrLost");
                deadline.Check();
                validateResult(reply.Result);
                deadline.Check();
                guard.Complete(reply.Identity);
                return new ValidatedReply(reply.Identity.Outcome, reply.Result);
            }
            catch
            {
                guard.TransportFailed();
                try { exchange.Abort(); } catch { /* Adapter violated contract; still terminal. */ }
                // Invoke cleanup once even when the budget expired. Never extend the
                // caller's deadline to wait for an uncooperative adapter's cleanup.
                if (enumerator != null && !enumeratorDisposeStarted)
                    try { await deadline.Wait(enumerator.DisposeAsync()).ConfigureAwait(false); } catch { }
                if (!exchangeDisposeStarted)
                    try { await deadline.Wait(exchange.DisposeAsync()).ConfigureAwait(false); } catch { }
                throw new IdentityViolation("V2AsyncExchangeFailedNoReplay");
            }
        }
        finally { Volatile.Write(ref active, 0); }
    }
    void Enter() => Require(Interlocked.CompareExchange(ref active, 1, 0) == 0, "RequestAlreadyPending");
    static void Require(bool condition, string code) { if (!condition) throw new IdentityViolation(code); }

    sealed class Deadline : IDisposable
    {
        readonly TimeProvider clock;
        readonly TimeSpan timeout;
        readonly long started;
        readonly CancellationTokenSource timer;
        readonly CancellationTokenSource linked;
        TimeSpan lastElapsed;
        public CancellationToken Token => linked.Token;
        public Deadline(TimeProvider clock, TimeSpan timeout, CancellationToken cancellation)
        {
            this.clock = clock; this.timeout = timeout; started = clock.GetTimestamp();
            timer = new CancellationTokenSource(timeout, clock);
            try { linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timer.Token); }
            catch { timer.Dispose(); throw; }
        }
        public void Check()
        {
            Token.ThrowIfCancellationRequested();
            var elapsed = clock.GetElapsedTime(started);
            Require(elapsed >= TimeSpan.Zero && elapsed >= lastElapsed, "MonotonicClockInvalid");
            lastElapsed = elapsed;
            Require(elapsed < timeout, "RequestDeadlineExceeded");
        }
        public async Task Wait(ValueTask value)
        {
            var task = value.AsTask(); Observe(task);
            Check(); await task.WaitAsync(Token).ConfigureAwait(false); Check();
        }
        public async Task<T> Wait<T>(ValueTask<T> value)
        {
            var task = value.AsTask(); Observe(task);
            Check(); var result = await task.WaitAsync(Token).ConfigureAwait(false); Check(); return result;
        }
        static void Observe(Task task) => _ = task.ContinueWith(t => { _ = t.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        public void Dispose() { linked.Dispose(); timer.Dispose(); }
    }
}
