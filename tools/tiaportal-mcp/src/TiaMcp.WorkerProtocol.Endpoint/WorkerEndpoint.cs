using System.Globalization;
using System.Threading;
using TiaMcp.WorkerProtocol.JsonLegacy;

namespace TiaMcp.WorkerProtocol.Endpoint;

/// <summary>An explicitly registered read-only callback. No reflection or fallback dispatch.</summary>
public sealed class ReadOnlyOperation
{
    internal OperationPolicy Policy { get; }
    internal Func<JsonPayload, Action<int>, JsonPayload> Dispatch { get; }
    public ReadOnlyOperation(string name, bool requiresBinding, Func<JsonPayload, Action<int>, JsonPayload> dispatch)
    {
        Policy = new OperationPolicy(name, requiresBinding, readOnly: true);
        Dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
    }
}

/// <summary>
/// Optional managed v2 worker seam. Its owner supplies serialized engineering-thread
/// execution, independently observed binding, bounded transport and process lifetime.
/// No production registration, native reference, automatic replay or binding operation.
/// </summary>
public sealed class WorkerEndpoint
{
    public const int MaximumRequests = 65536;
    public const int MaximumProgressFrames = 256;
    readonly EngineIdentity engine;
    readonly Func<BindingSnapshot> observeBinding;
    readonly Dictionary<string, ReadOnlyOperation> operations;
    readonly WorkerRequestGuard guard;
    readonly HashSet<string> nonces = new(StringComparer.Ordinal);
    int entered;
    volatile bool faulted;
    bool helloSent;
    public bool Faulted => faulted;

    public WorkerEndpoint(EngineIdentity engine, IEnumerable<ReadOnlyOperation> operations, Func<BindingSnapshot> observeBinding)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.observeBinding = observeBinding ?? throw new ArgumentNullException(nameof(observeBinding));
        if (operations == null) throw new ArgumentNullException(nameof(operations));
        this.operations = operations.ToDictionary(o => o.Policy.Name, StringComparer.Ordinal);
        guard = new WorkerRequestGuard(engine, this.operations.Values.Select(o => o.Policy));
    }

    public byte[] CreateHello()
    {
        Enter();
        try
        {
            Require(!helloSent, "DuplicateHello");
            BindingSnapshot observed = Observe();
            Require(!observed.IsBound && observed.Epoch == 0, "FreshUnboundHelloRequired");
            byte[] result = StrictCodec.Encode(new HelloFrame(engine, observed));
            Require(!faulted, "WorkerSessionFaulted");
            helloSent = true;
            return result;
        }
        catch { faulted = true; guard.Fail(); throw new IdentityViolation("WorkerHelloRejected"); }
        finally { Volatile.Write(ref entered, 0); }
    }

    public void Handle(byte[] request, Action<byte[]> emit)
    {
        if (emit == null) throw new ArgumentNullException(nameof(emit));
        Enter();
        try
        {
            Require(helloSent, "HelloRequired");
            var frame = StrictCodec.Decode(request) as RequestFrame;
            Require(frame != null, "RequestRequired");
            var identity = frame!.Identity;
            string modernId = "worker_" + identity.RequestId.ToString(CultureInfo.InvariantCulture);
            Require(frame.Id.Number == identity.RequestId || frame.Id.Text == modernId, "OuterIdMismatch");
            Require(operations.TryGetValue(identity.Operation, out var operation), "UnknownOperation");
            Require(nonces.Count < MaximumRequests && nonces.Add(identity.CorrelationId), "RequestNonceRejected");
            guard.Accept(identity, Observe()); // Independent observation before any callback.
            int thread = Thread.CurrentThread.ManagedThreadId;
            int active = 1;
            int sequence = 0;
            bool failed = false;
            JsonPayload? result = null;
            try
            {
                result = operation!.Dispatch(frame.Arguments, percent =>
                {
                    try
                    {
                        Require(Volatile.Read(ref active) == 1 && Thread.CurrentThread.ManagedThreadId == thread && !faulted, "ProgressOutsideDispatch");
                        Require(percent >= 0 && percent <= 100 && sequence < MaximumProgressFrames, "ProgressLimitExceeded");
                        emit(StrictCodec.Encode(new ProgressFrame(frame.Id, identity, ++sequence, percent)));
                    }
                    catch { faulted = true; throw new IdentityViolation("WorkerProgressRejected"); }
                });
            }
            catch { failed = true; }
            finally { Volatile.Write(ref active, 0); }
            // Even a declared read-only callback can throw or lose/change its binding.
            // Never report ReadFailed/success until independent post-observation matches.
            var after = Observe();
            Require(!faulted, "WorkerSessionFaulted");
            Require(after.Matches(identity.Before), "BindingIdentityChangedOrLost");
            if (failed) result = JsonPayload.Parse("{\"code\":\"ReadOperationFailed\"}");
            Require(result != null, "ResultRequired");
            var reply = guard.Finish(after, failed ? ReplyOutcome.ReadFailed : ReplyOutcome.Succeeded);
            emit(StrictCodec.Encode(new ReplyFrame(frame.Id, reply, result!)));
            Require(!faulted, "WorkerSessionFaulted");
        }
        catch
        {
            faulted = true;
            guard.Fail();
            // No callback exceptions, arguments, results or native details escape.
            throw new IdentityViolation("WorkerRequestRejectedNoReplay");
        }
        finally { Volatile.Write(ref entered, 0); }
    }

    BindingSnapshot Observe()
    {
        var observed = observeBinding();
        Require(observed != null, "BindingObservationRequired");
        Require(!faulted, "WorkerSessionFaulted");
        return observed!;
    }
    void Enter()
    {
        if (faulted) throw new IdentityViolation("WorkerSessionFaultedNoReplay");
        if (Interlocked.CompareExchange(ref entered, 1, 0) != 0)
        {
            faulted = true;
            throw new IdentityViolation("WorkerConcurrentEntryRejected");
        }
        if (faulted) { Volatile.Write(ref entered, 0); throw new IdentityViolation("WorkerSessionFaultedNoReplay"); }
    }
    static void Require(bool condition, string code) { if (!condition) throw new IdentityViolation(code); }
}
