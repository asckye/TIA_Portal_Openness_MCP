using System;
using System.Collections.Generic;
using System.Globalization;

namespace TiaMcp.WorkerProtocol
{
    // Transport-neutral v2 candidate. No Siemens, JSON, process or filesystem dependencies.
    public sealed class IdentityViolation : InvalidOperationException
    {
        public string Code { get; }
        public IdentityViolation(string code) : base(code) { Code=code; }
    }
    internal static class IdentityRules
    {
        internal static void Require(bool value,string code) { if(!value) throw new IdentityViolation(code); }
        internal static bool Hex(string? value,int length)
        {
            if(value==null || value.Length!=length)return false;
            foreach(char c in value)if(!(c>='0' && c<='9') && !(c>='a' && c<='f'))return false;
            return true;
        }
        internal static bool Release(string? key) => key=="14sp1" || key=="15.1" || key=="16" || key=="17" || key=="18" || key=="19" || key=="20" || key=="21";
        internal static bool Operation(string value)
        {
            if(value==null || value.Length==0 || value.Length>100)return false;
            foreach(char c in value)if(!(c>='A' && c<='Z') && !(c>='a' && c<='z') && !(c>='0' && c<='9') && c!='_')return false;
            return true;
        }
    }
    public sealed class EngineIdentity
    {
        public const int ProtocolVersion=2;
        public string ReleaseKey { get; }
        public string WorkerSha256 { get; }
        public string EngineSha256 { get; }
        public string SessionId { get; }
        public EngineIdentity(int protocol,string releaseKey,string workerSha256,string engineSha256,string sessionId)
        {
            IdentityRules.Require(protocol==ProtocolVersion,"ProtocolVersionMismatch");
            IdentityRules.Require(IdentityRules.Release(releaseKey),"ExactReleaseRequired");
            IdentityRules.Require(IdentityRules.Hex(workerSha256,64) && IdentityRules.Hex(engineSha256,64),"BinaryIdentityRequired");
            IdentityRules.Require(IdentityRules.Hex(sessionId,32),"SessionIdentityRequired");
            ReleaseKey=releaseKey; WorkerSha256=workerSha256; EngineSha256=engineSha256; SessionId=sessionId;
        }
        public bool Matches(EngineIdentity? other) => other!=null && ReleaseKey==other.ReleaseKey && WorkerSha256==other.WorkerSha256 && EngineSha256==other.EngineSha256 && SessionId==other.SessionId;
    }
    public sealed class ProjectIdentity
    {
        // Digest a versioned, length-delimited tuple: project kind, canonical absolute
        // path and exact project name. The worker derives it from the actual binding,
        // not the request. PID/start time are checked separately below. Codec and
        // native binding capture are intentionally not wired in this candidate.
        public string ProjectSha256 { get; }
        public int TiaProcessId { get; }
        public long TiaProcessStartUtcTicks { get; }
        public ProjectIdentity(string projectSha256,int processId,long processStartUtcTicks)
        {
            IdentityRules.Require(IdentityRules.Hex(projectSha256,64),"ProjectIdentityRequired");
            IdentityRules.Require(processId>0 && processStartUtcTicks>0 && processStartUtcTicks<=DateTime.MaxValue.Ticks,"TiaProcessIdentityRequired");
            ProjectSha256=projectSha256; TiaProcessId=processId; TiaProcessStartUtcTicks=processStartUtcTicks;
        }
        public bool Matches(ProjectIdentity? other) => other!=null && ProjectSha256==other.ProjectSha256 && TiaProcessId==other.TiaProcessId && TiaProcessStartUtcTicks==other.TiaProcessStartUtcTicks;
    }
    public sealed class BindingSnapshot
    {
        public long Epoch { get; }
        public ProjectIdentity? Project { get; }
        public bool IsBound => Project!=null;
        private BindingSnapshot(long epoch,ProjectIdentity? project)
        { IdentityRules.Require(epoch>=0,"BindingEpochRequired"); Epoch=epoch; Project=project; }
        public static BindingSnapshot Unbound(long epoch) => new BindingSnapshot(epoch,null);
        public static BindingSnapshot Bound(long epoch,ProjectIdentity project)
        { IdentityRules.Require(epoch>0 && project!=null,"BoundIdentityRequired"); return new BindingSnapshot(epoch,project); }
        public bool Matches(BindingSnapshot? other) => other!=null && Epoch==other.Epoch && IsBound==other.IsBound && (!IsBound || Project!.Matches(other.Project));
    }
    public enum BindingEffect { None, Bind, Unbind }
    public enum ReplyOutcome { Succeeded, RejectedBeforeOperation, ReadFailed, Unknown }
    public sealed class OperationPolicy
    {
        public string Name { get; }
        public bool RequiresBinding { get; }
        public BindingEffect Effect { get; }
        public bool ReadOnly { get; }
        public OperationPolicy(string name,bool requiresBinding,BindingEffect effect=BindingEffect.None,bool readOnly=false)
        {
            IdentityRules.Require(IdentityRules.Operation(name),"RegisteredOperationRequired");
            IdentityRules.Require(Enum.IsDefined(typeof(BindingEffect),effect),"BindingEffectInvalid");
            IdentityRules.Require(!readOnly || effect==BindingEffect.None,"ReadOnlyTransitionInvalid");
            Name=name; RequiresBinding=requiresBinding; Effect=effect; ReadOnly=readOnly;
        }
    }
    public sealed class RequestIdentity
    {
        public long RequestId { get; }
        public string CorrelationId { get; }
        public EngineIdentity Engine { get; }
        public string Operation { get; }
        public BindingSnapshot Before { get; }
        public BindingSnapshot ExpectedAfter { get; }
        public RequestIdentity(long requestId,string correlationId,EngineIdentity engine,string operation,BindingSnapshot before,BindingSnapshot expectedAfter)
        {
            IdentityRules.Require(requestId>0,"RequestIdRequired");
            IdentityRules.Require(IdentityRules.Hex(correlationId,32),"CorrelationRequired");
            IdentityRules.Require(engine!=null && before!=null && expectedAfter!=null,"RequestIdentityRequired");
            IdentityRules.Require(IdentityRules.Operation(operation),"RegisteredOperationRequired");
            RequestId=requestId; CorrelationId=correlationId; Engine=engine!; Operation=operation; Before=before!; ExpectedAfter=expectedAfter!;
        }
        public bool Matches(RequestIdentity? other) => other!=null && RequestId==other.RequestId && CorrelationId==other.CorrelationId && Engine.Matches(other.Engine) && Operation==other.Operation && Before.Matches(other.Before) && ExpectedAfter.Matches(other.ExpectedAfter);
    }
    public sealed class ReplyIdentity
    {
        public RequestIdentity Request { get; }
        public BindingSnapshot ObservedAfter { get; }
        public ReplyOutcome Outcome { get; }
        public ReplyIdentity(RequestIdentity request,BindingSnapshot observedAfter,ReplyOutcome outcome)
        {
            IdentityRules.Require(request!=null && observedAfter!=null,"ReplyIdentityRequired");
            IdentityRules.Require(Enum.IsDefined(typeof(ReplyOutcome),outcome),"ReplyOutcomeRequired");
            Request=request!; ObservedAfter=observedAfter!; Outcome=outcome;
        }
    }
}
