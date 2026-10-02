using System;
using System.Collections.Generic;

namespace TiaMcp.WorkerProtocol
{
    // Single-flight state machine. Its owner supplies serialization and dispatch;
    // this module contains no automatic retry, process launch or native callback.
    public sealed class RequestGuard
    {
        private readonly EngineIdentity expected;
        private readonly Dictionary<string,OperationPolicy> operations=new Dictionary<string,OperationPolicy>(StringComparer.Ordinal);
        private readonly object sync=new object();
        private BindingSnapshot? binding;
        private RequestIdentity? pending;
        private long sequence;
        private bool sent;
        public bool Faulted { get; private set; }
        public bool OutcomeUnknown { get; private set; }
        public RequestGuard(EngineIdentity expected,IEnumerable<OperationPolicy> policies)
        {
            this.expected=expected ?? throw new ArgumentNullException(nameof(expected));
            foreach(var policy in policies)operations.Add(policy.Name,policy);
        }
        public void AcceptHello(EngineIdentity? actual,BindingSnapshot? actualBinding)
        {
            lock(sync){
                RequireUsable();
                try {
                    IdentityRules.Require(binding==null && pending==null,"DuplicateHello");
                    IdentityRules.Require(expected.Matches(actual),"EngineIdentityMismatch");
                    // New process/session starts explicitly unbound; no inherited project.
                    IdentityRules.Require(actualBinding!=null && !actualBinding.IsBound && actualBinding.Epoch==0,"FreshUnboundHelloRequired");
                    binding=actualBinding;
                } catch { Faulted=true; OutcomeUnknown=sent; throw; }
            }
        }
        public RequestIdentity Begin(string operation,ProjectIdentity? target=null)
        {
            lock(sync){
                RequireUsable();
                IdentityRules.Require(binding!=null,"HelloRequired");
                IdentityRules.Require(pending==null,"RequestAlreadyPending");
                IdentityRules.Require(operations.TryGetValue(operation,out var policy),"UnknownOperation");
                IdentityRules.Require(!policy!.RequiresBinding || binding!.IsBound,"ProjectBindingRequired");
                BindingSnapshot after=binding!;
                if(policy.Effect==BindingEffect.Bind){IdentityRules.Require(target!=null,"BindingTargetRequired"); after=BindingSnapshot.Bound(checked(binding!.Epoch+1),target!);}
                else if(policy.Effect==BindingEffect.Unbind){IdentityRules.Require(target==null,"UnexpectedBindingTarget"); after=BindingSnapshot.Unbound(checked(binding!.Epoch+1));}
                else IdentityRules.Require(target==null,"UnexpectedBindingTarget");
                pending=new RequestIdentity(checked(++sequence),Guid.NewGuid().ToString("N"),expected,operation,binding!,after);
                sent=false;
                return pending;
            }
        }
        public void MarkDispatchAttempt(RequestIdentity request)
        {
            lock(sync){RequireUsable(); IdentityRules.Require(pending!=null && pending.Matches(request) && !sent,"DispatchIdentityMismatch"); sent=true;}
        }
        public void Complete(ReplyIdentity? reply)
        {
            lock(sync){
                RequireUsable();
                try {
                    IdentityRules.Require(pending!=null && sent,"UnexpectedReply");
                    IdentityRules.Require(reply!=null && pending!.Matches(reply.Request),"ReplyIdentityMismatch");
                    IdentityRules.Require(reply!.Outcome!=ReplyOutcome.Unknown,"NativeOutcomeUnknown");
                    IdentityRules.Require(reply.Outcome!=ReplyOutcome.ReadFailed || operations[pending!.Operation].ReadOnly,"WriteOutcomeCannotBeReadFailure");
                    var after=reply.Outcome==ReplyOutcome.Succeeded?pending!.ExpectedAfter:pending!.Before;
                    IdentityRules.Require(after.Matches(reply.ObservedAfter),"BindingIdentityChangedOrLost");
                    binding=reply.ObservedAfter; pending=null; sent=false;
                } catch { Faulted=true; OutcomeUnknown=sent; throw; }
            }
        }
        public void TransportFailed()
        { lock(sync){Faulted=true; OutcomeUnknown=sent; pending=null;} }
        public void CancelBeforeDispatch(RequestIdentity request)
        { lock(sync){RequireUsable(); IdentityRules.Require(pending!=null && pending.Matches(request) && !sent,"PreDispatchCancellationOnly"); pending=null;} }
        private void RequireUsable() { IdentityRules.Require(!Faulted,"SessionFaultedNoReplay"); }
    }
    public static class WorkerRequestValidation
    {
        // Call before entering the native operation and before setting journal context.
        // lastAcceptedId belongs to this exact worker process/session, not to its caller.
        public static void BeforeDispatch(RequestIdentity? request,EngineIdentity actualEngine,BindingSnapshot? actualBinding,long lastAcceptedId,OperationPolicy policy)
        {
            IdentityRules.Require(request!=null,"RequestIdentityRequired");
            IdentityRules.Require(actualEngine.Matches(request!.Engine),"EngineIdentityMismatch");
            IdentityRules.Require(request.RequestId>lastAcceptedId,"DuplicateOrStaleRequest");
            IdentityRules.Require(request.Operation==policy.Name,"OperationIdentityMismatch");
            IdentityRules.Require(request.Before.Matches(actualBinding),"BindingIdentityChangedOrLost");
            IdentityRules.Require(!policy.RequiresBinding || actualBinding!.IsBound,"ProjectBindingRequired");
            if(policy.Effect==BindingEffect.None)IdentityRules.Require(request.Before.Matches(request.ExpectedAfter),"UnexpectedBindingTransition");
            else {
                IdentityRules.Require(request.Before.Epoch<long.MaxValue && request.ExpectedAfter.Epoch==request.Before.Epoch+1,"BindingEpochMismatch");
                IdentityRules.Require(policy.Effect==BindingEffect.Bind?request.ExpectedAfter.IsBound:!request.ExpectedAfter.IsBound,"BindingTransitionMismatch");
            }
        }
    }
    public sealed class WorkerRequestGuard
    {
        private readonly EngineIdentity actual;
        private readonly Dictionary<string,OperationPolicy> operations=new Dictionary<string,OperationPolicy>(StringComparer.Ordinal);
        private RequestIdentity? pending;
        private long lastAcceptedId;
        public bool Faulted { get; private set; }
        public WorkerRequestGuard(EngineIdentity actual,IEnumerable<OperationPolicy> policies)
        { this.actual=actual; foreach(var policy in policies)operations.Add(policy.Name,policy); }
        // Worker owner must invoke on its existing serialized engineering thread.
        public RequestIdentity Accept(RequestIdentity? request,BindingSnapshot? observedBefore)
        {
            IdentityRules.Require(!Faulted,"SessionFaultedNoReplay");
            try {
                IdentityRules.Require(pending==null,"RequestAlreadyPending");
                IdentityRules.Require(request!=null,"RequestIdentityRequired");
                IdentityRules.Require(operations.TryGetValue(request!.Operation,out var policy),"UnknownOperation");
                WorkerRequestValidation.BeforeDispatch(request,actual,observedBefore,lastAcceptedId,policy!);
                lastAcceptedId=request.RequestId; pending=request;
                return request;
            } catch { Faulted=true; throw; }
        }
        public ReplyIdentity Finish(BindingSnapshot? observedAfter,ReplyOutcome outcome)
        {
            IdentityRules.Require(!Faulted,"SessionFaultedNoReplay");
            try {
                IdentityRules.Require(pending!=null && observedAfter!=null,"ReplyIdentityRequired");
                IdentityRules.Require(outcome!=ReplyOutcome.Unknown,"NativeOutcomeUnknown");
                IdentityRules.Require(Enum.IsDefined(typeof(ReplyOutcome),outcome),"ReplyOutcomeRequired");
                IdentityRules.Require(outcome!=ReplyOutcome.ReadFailed || operations[pending!.Operation].ReadOnly,"WriteOutcomeCannotBeReadFailure");
                var expected=outcome==ReplyOutcome.Succeeded?pending!.ExpectedAfter:pending!.Before;
                IdentityRules.Require(expected.Matches(observedAfter),"BindingIdentityChangedOrLost");
                var reply=new ReplyIdentity(pending!,observedAfter!,outcome); pending=null; return reply;
            } catch { Faulted=true; throw; }
        }
        public void Fail(){Faulted=true;pending=null;}
    }
}
