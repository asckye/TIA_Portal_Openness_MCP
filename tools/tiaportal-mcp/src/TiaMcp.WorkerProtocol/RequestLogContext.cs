using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace TiaMcp.WorkerProtocol
{
    public sealed class RequestLogContext : IDisposable
    {
        private static readonly AsyncLocal<RequestLogContext?> Slot=new AsyncLocal<RequestLogContext?>();
        private readonly RequestIdentity request;
        private BindingSnapshot snapshot;
        private int active=1;
        private RequestLogContext(RequestIdentity request) { this.request=request; snapshot=request.Before; }
        public static RequestLogContext Open(RequestIdentity validatedRequest)
        {
            IdentityRules.Require(validatedRequest!=null,"RequestIdentityRequired");
            IdentityRules.Require(Current==null,"NestedRequestContextRefused");
            var context=new RequestLogContext(validatedRequest!); Slot.Value=context; return context;
        }
        public static RequestLogContext? Current => Slot.Value!=null && Volatile.Read(ref Slot.Value.active)==1?Slot.Value:null;
        public string CorrelationId => request.CorrelationId;
        public void ObserveCompletion(BindingSnapshot observed,ReplyOutcome outcome)
        {
            IdentityRules.Require(Volatile.Read(ref active)==1,"RequestContextExpired");
            IdentityRules.Require(outcome!=ReplyOutcome.Unknown,"NativeOutcomeUnknown");
            IdentityRules.Require(Enum.IsDefined(typeof(ReplyOutcome),outcome),"ReplyOutcomeRequired");
            var expected=outcome==ReplyOutcome.Succeeded?request.ExpectedAfter:request.Before;
            IdentityRules.Require(expected.Matches(observed),"BindingIdentityChangedOrLost");
            snapshot=observed;
        }
        // Only cached immutable identity, never getters on a live engineering object.
        // No arguments, output payload, project path, exception message or credential slot.
        public IReadOnlyDictionary<string,string> SnapshotFields()
        {
            IdentityRules.Require(Volatile.Read(ref active)==1,"RequestContextExpired");
            var copy=snapshot;
            return new Dictionary<string,string>(StringComparer.Ordinal){
                ["correlationId"]=request.CorrelationId,["requestId"]=request.RequestId.ToString(CultureInfo.InvariantCulture),
                ["operation"]=request.Operation,["releaseKey"]=request.Engine.ReleaseKey,["workerSha256"]=request.Engine.WorkerSha256,
                ["engineSha256"]=request.Engine.EngineSha256,["sessionId"]=request.Engine.SessionId,
                ["bindingEpoch"]=copy.Epoch.ToString(CultureInfo.InvariantCulture),["bindingState"]=copy.IsBound?"bound":"unbound",
                ["projectProjectSha256"]=copy.Project?.ProjectSha256??"",["tiaPid"]=(copy.Project?.TiaProcessId??0).ToString(CultureInfo.InvariantCulture),
                ["tiaProcessStartUtcTicks"]=(copy.Project?.TiaProcessStartUtcTicks??0).ToString(CultureInfo.InvariantCulture)
            };
        }
        public void Dispose(){Interlocked.Exchange(ref active,0);if(ReferenceEquals(Slot.Value,this))Slot.Value=null;}
    }
}
