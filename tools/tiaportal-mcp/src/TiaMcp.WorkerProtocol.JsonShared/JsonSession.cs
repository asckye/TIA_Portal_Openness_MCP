#if TIA_JSON_LEGACY
using Payload = TiaMcp.WorkerProtocol.JsonLegacy.JsonPayload;
using FrameBuffer = byte[];
using ValueKind = TiaMcp.WorkerProtocol.JsonLegacy.PayloadKind;
using Newtonsoft.Json;
#else
using Payload = System.Text.Json.JsonElement;
using FrameBuffer = System.ReadOnlyMemory<byte>;
using ValueKind = System.Text.Json.JsonValueKind;
using System.Text.Json;
#endif
using TiaMcp.WorkerProtocol;

#if TIA_JSON_LEGACY
namespace TiaMcp.WorkerProtocol.JsonLegacy;
#else
namespace TiaMcp.WorkerProtocol.JsonV2;
#endif

public sealed record ValidatedReply(ReplyOutcome Outcome, Payload Result);

public enum OuterIdStyle { Numeric, ModernString }
// Implementations must cap frame reads, bound time/cancellation, and release resources.
// The enumerable belongs to this exchange only and ends immediately after its reply.
public interface IV2Exchange { IEnumerable<FrameBuffer> Exchange(FrameBuffer request); }

// Explicit v2-only seam; no native callbacks, process creation, fallback or replay.
// Owner invokes on one serialized engineering thread. Do not share across threads.
public sealed class JsonSession
{
    readonly RequestGuard guard;
    readonly OuterIdStyle style;
    readonly Dictionary<string, OperationPolicy> policies;
    public bool Faulted => guard.Faulted;
    public bool OutcomeUnknown => guard.OutcomeUnknown;
    public JsonSession(EngineIdentity selected, IEnumerable<OperationPolicy> policies, OuterIdStyle style)
    {
        StrictCodec.Require(Enum.IsDefined(typeof(OuterIdStyle), style), "OuterIdStyleInvalid");
        this.policies = policies.ToDictionary(p => p.Name, StringComparer.Ordinal);
        guard = new RequestGuard(selected, this.policies.Values); this.style = style;
    }
    public void AcceptHello(FrameBuffer frame)
    {
        try
        {
            var decoded = StrictCodec.Decode(frame);
            StrictCodec.Require(decoded is HelloFrame, "HelloRequired");
            var hello = (HelloFrame)decoded;
            guard.AcceptHello(hello.Engine, hello.Binding);
        }
        catch { guard.TransportFailed(); throw new IdentityViolation("V2HandshakeRejected"); }
    }
    public ValidatedReply Call(string operation, Payload arguments, IV2Exchange transport,
        Action<Payload> validateResult, ProjectIdentity? target = null)
    {
        if (transport == null) throw new ArgumentNullException(nameof(transport));
        if (validateResult == null) throw new ArgumentNullException(nameof(validateResult));
        var request = guard.Begin(operation, target);
        WireId id = style == OuterIdStyle.Numeric ? new WireId(request.RequestId) : new WireId("worker_" + request.RequestId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        byte[] bytes;
        try { bytes = StrictCodec.Encode(new RequestFrame(id, request, arguments)); }
        catch { guard.CancelBeforeDispatch(request); throw new IdentityViolation("V2RequestEncodingRejected"); }
        guard.MarkDispatchAttempt(request); // Mark BEFORE first possible write, even a partial write.
        try
        {
            ReplyFrame? reply = null; long progressSequence = 0;
            foreach (var frame in transport.Exchange(bytes))
            {
                StrictCodec.Require(reply == null, "TrailingFrameRejected");
                switch (StrictCodec.Decode(frame))
                {
                    case ProgressFrame p:
                        StrictCodec.Require(p.Id == id && request.Matches(p.Identity) && p.Sequence > progressSequence, "ProgressIdentityMismatch");
                        progressSequence = p.Sequence;
                        break;
                    case ReplyFrame r:
                        StrictCodec.Require(r.Id == id, "OuterIdMismatch");
                        reply = r;
                        break;
                    default: throw new IdentityViolation("UnexpectedFrame");
                }
            }
            StrictCodec.Require(reply != null, "ReplyRequired");
            // Identity and result both must pass before the binding/session is advanced.
            StrictCodec.Require(request.Matches(reply!.Identity.Request), "ReplyIdentityMismatch");
            // Pure completion preflight before the caller observes any result.
            StrictCodec.Require(reply.Identity.Outcome != ReplyOutcome.Unknown, "NativeOutcomeUnknown");
            StrictCodec.Require(reply.Identity.Outcome != ReplyOutcome.ReadFailed || policies[operation].ReadOnly, "WriteOutcomeCannotBeReadFailure");
            var expectedBinding = reply.Identity.Outcome == ReplyOutcome.Succeeded ? request.ExpectedAfter : request.Before;
            StrictCodec.Require(expectedBinding.Matches(reply.Identity.ObservedAfter), "BindingIdentityChangedOrLost");
            validateResult(reply.Result);
            guard.Complete(reply.Identity);
            return new ValidatedReply(reply.Identity.Outcome, reply.Result);
        }
        catch
        {
            guard.TransportFailed();
            // Never expose transport exception bodies or malformed frame contents.
            throw new IdentityViolation("V2ExchangeFailedNoReplay");
        }
    }
}
