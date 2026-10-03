using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TiaMcp.PlcFoundation
{
    public enum BindingLifecycleChange { Attach, Bind, Unbind, Detach }

    public interface IBindingProcessObservationSource
    {
        // Null or exception means unknown. Must independently query the OS, never Siemens AcquisitionTime.
        BindingProcessObservation? Read(int processId);
    }
    public interface IBindingProjectObservationSource
    {
        // Fields are bookkeeping only. ReadProject must read the currently bound native object.
        BindingCachedFields ReadFields();
        BindingProjectObservation ReadProject();
    }
    public static class BindingObservationPolicy
    {
        public static BindingObservation Capture(IBindingProjectObservationSource source,
            IBindingProcessObservationSource processes, BindingProcessObservation? attachmentAnchor)
        {
            if(source==null || processes==null) throw new ArgumentNullException();
            try
            {
                var fields=source.ReadFields();
                if(!fields.HasPortal)
                {
                    if(fields.ProcessId.HasValue || fields.HasProject || fields.ProjectFile!=null || fields.IsLocalSession || attachmentAnchor!=null)
                        return BindingObservation.Unknown("InconsistentDetachedFields");
                    if(!fields.Same(source.ReadFields())) return BindingObservation.Unknown("BindingChangedDuringObservation");
                    return new BindingObservation(BindingObservationState.Unbound,BindingIdentityStrength.None,null,null,null,"DetachedFieldsObserved");
                }
                if(!fields.ProcessId.HasValue || fields.ProcessId.Value<=0 || fields.HasProject!=(fields.ProjectFile!=null) || (!fields.HasProject && fields.IsLocalSession))
                    return BindingObservation.Unknown("InconsistentBoundFields");
                int pid=fields.ProcessId.Value;
                var before=processes.Read(pid);
                if(before==null || before.ProcessId!=pid) return BindingObservation.Unknown("OsIdentityUnavailable");
                if(attachmentAnchor!=null && !before.Same(attachmentAnchor)) return BindingObservation.Unknown("AttachmentProcessIdentityChanged");
                string? digest=null;
                if(fields.HasProject)
                {
                    var first=source.ReadProject();
                    var second=source.ReadProject();
                    if(first==null || second==null || first.Name!=second.Name || first.File!=second.File || string.IsNullOrWhiteSpace(first.Name))
                        return BindingObservation.Unknown("ProjectIdentityUnavailableOrChanged");
                    MutationIdentityPolicy.RequireSameProject(fields.ProjectFile!, first.File);
                    digest=ProjectDigest(fields.IsLocalSession,first.File,first.Name);
                }
                var after=processes.Read(pid);
                if(!before.Same(after) || !fields.Same(source.ReadFields())) return BindingObservation.Unknown("BindingChangedDuringObservation");
                return new BindingObservation(fields.HasProject ? BindingObservationState.Bound : BindingObservationState.Unbound,
                    attachmentAnchor==null ? BindingIdentityStrength.WeakPidOnly : BindingIdentityStrength.OsStartTimeAnchored,
                    pid,before.StartUtcTicks,digest,attachmentAnchor==null ? "ExistingPidOnlyAttachment" : "OsIdentityAndBindingObserved");
            }
            catch(Exception) /* swallow(native-fallback): failed native or OS identity reads leave the binding Unknown and ineligible for v2 admission */ { return BindingObservation.Unknown("BindingObservationFailed"); }
        }
        // v1 tuple: length-prefixed UTF-8 fields, Int32 little-endian lengths, Windows path
        // case folded, exact project name, explicit project/local-session kind. Not file contents.
        public static string ProjectDigest(bool localSession, string path, string name)
        {
            if(string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Project name required.");
            path=MutationIdentityPolicy.AbsoluteFile(path).ToUpperInvariant();
            using(var bytes=new MemoryStream())
            {
                using(var writer=new BinaryWriter(bytes,Encoding.UTF8,true))
                    foreach(var part in new[]{"tia-binding-v1",localSession ? "local-session" : "project",path,name})
                    { var value=new UTF8Encoding(false,true).GetBytes(part); writer.Write(value.Length); writer.Write(value); }
                using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes.ToArray())).Replace("-","").ToLowerInvariant();
            }
        }
    }

    // One owner-thread instance per engine/session. All lifecycle attempts must be bracketed,
    // including same-project ABA. No automatic rebase, epoch reset or unknown-to-unbound fallback.
    public sealed class BindingSnapshotTracker
    {
        private long epoch;
        private BindingObservation? accepted;
        private BindingLifecycleChange? pending;
        private bool faulted;
        private bool observing;
        internal bool CanObserve => !faulted && !pending.HasValue && !observing;
        public BindingSnapshotObservation Observe(Func<BindingObservation> capture)
        {
            if(capture==null) throw new ArgumentNullException(nameof(capture));
            if(!CanObserve) return Fail("BindingLifecycleUncertain");
            observing=true;
            try { var current=capture(); observing=false; return Observe(current); }
            catch(Exception) /* swallow(native-fallback): a failed capture faults the tracker instead of accepting an unverified binding */ { return Fail("BindingObservationFailed"); }
            finally { observing=false; }
        }
        public BindingSnapshotObservation Complete(Func<BindingObservation> capture)
        {
            if(capture==null) throw new ArgumentNullException(nameof(capture));
            if(faulted || !pending.HasValue || observing) return Fail("BindingLifecycleCompletionUnknown");
            observing=true;
            try { var current=capture(); observing=false; return Complete(current); }
            catch(Exception) /* swallow(native-fallback): a failed completion capture faults the tracker instead of committing the lifecycle transition */ { return Fail("BindingObservationFailed"); }
            finally { observing=false; }
        }
        internal BindingObservation ReadObservation(Func<BindingObservation> capture)
        {
            if(capture==null) throw new ArgumentNullException(nameof(capture));
            if(!CanObserve) return Fail("BindingLifecycleUncertain").Binding;
            observing=true;
            try
            {
                var current=capture();
                if(faulted || current==null || current.State==BindingObservationState.Unknown)
                    return Fail("BindingObservationFailed").Binding;
                return current;
            }
            catch(Exception) /* swallow(native-fallback): a failed read faults the tracker and returns an Unknown binding */ { return Fail("BindingObservationFailed").Binding; }
            finally { observing=false; }
        }
        public BindingSnapshotObservation Observe(BindingObservation current)
        {
            if(current==null) throw new ArgumentNullException(nameof(current));
            if(faulted || pending.HasValue || observing) return Fail("BindingLifecycleUncertain");
            if(!current.AllowsV2Admission) return Fail(current.Reason);
            if(accepted==null)
            {
                if(current.State!=BindingObservationState.Unbound || current.ProcessId.HasValue) return Fail("FreshDetachedObservationRequired");
                accepted=current;
            }
            else if(!accepted.Same(current)) return Fail("UnannouncedBindingChange");
            return new BindingSnapshotObservation(epoch,current);
        }
        public void Begin(BindingLifecycleChange change)
        {
            if(faulted || pending.HasValue || observing || accepted==null || !Enum.IsDefined(typeof(BindingLifecycleChange),change))
            { faulted=true; throw new InvalidOperationException("Binding lifecycle is not ready."); }
            bool attached=accepted.ProcessId.HasValue, bound=accepted.State==BindingObservationState.Bound;
            bool valid=change==BindingLifecycleChange.Attach ? !attached : change==BindingLifecycleChange.Bind ? attached&&!bound : change==BindingLifecycleChange.Unbind ? bound : attached;
            if(!valid || epoch==long.MaxValue) { faulted=true; throw new InvalidOperationException("Binding lifecycle transition refused."); }
            epoch++; pending=change;
        }
        public BindingSnapshotObservation Complete(BindingObservation current)
        {
            if(current==null) throw new ArgumentNullException(nameof(current));
            if(faulted || !pending.HasValue || observing || !current.AllowsV2Admission) return Fail("BindingLifecycleCompletionUnknown");
            bool attached=current.ProcessId.HasValue, bound=current.State==BindingObservationState.Bound;
            var change=pending.Value;
            bool valid=change==BindingLifecycleChange.Attach ? attached&&!bound : change==BindingLifecycleChange.Bind ? attached&&bound : change==BindingLifecycleChange.Unbind ? attached&&!bound : !attached&&!bound;
            if(change==BindingLifecycleChange.Bind || change==BindingLifecycleChange.Unbind)
                valid=valid && accepted!.ProcessId==current.ProcessId && accepted.ProcessStartUtcTicks==current.ProcessStartUtcTicks;
            if(!valid) return Fail("UnexpectedBindingLifecycleResult");
            accepted=current; pending=null;
            return new BindingSnapshotObservation(epoch,current);
        }
        public BindingSnapshotObservation Fail(string reason)
        { faulted=true; pending=null; return new BindingSnapshotObservation(epoch,BindingObservation.Unknown(reason)); }
    }
}
