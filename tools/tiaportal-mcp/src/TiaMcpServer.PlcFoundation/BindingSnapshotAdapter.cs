using System;

namespace TiaMcp.PlcFoundation
{
    public sealed partial class PlcFoundationEngine
    {
        private readonly BindingSnapshotTracker bindingSnapshots=new BindingSnapshotTracker();
        private readonly IBindingProcessObservationSource bindingProcesses=new BindingSnapshotProcessSource();
        private BindingProcessObservation? bindingAttachmentAnchor;
        private BindingProcessObservation? bindingPendingAnchor;
        private BindingLifecycleChange? bindingPendingChange;

        // Candidate hook only. No existing lifecycle method calls it, and no v2 dispatch is enabled.
        public BindingSnapshotObservation ObserveBindingSnapshot()
        {
            Check();
            try { return bindingSnapshots.Observe(()=>CaptureBindingObservation(bindingAttachmentAnchor)); }
            catch(Exception) /* swallow(native-fallback): failed binding observation faults the snapshot so v2 admission remains blocked */ { return bindingSnapshots.Fail("BindingObservationFailed"); }
        }
        public BindingObservation ReadBindingObservation()
        {
            Check();
            try { return bindingSnapshots.ReadObservation(()=>CaptureBindingObservation(bindingAttachmentAnchor)); }
            catch(Exception) /* swallow(native-fallback): failed binding reads return Unknown instead of claiming a verified project identity */ { return BindingObservation.Unknown("BindingObservationFailed"); }
        }
        private BindingObservation CaptureBindingObservation(BindingProcessObservation? anchor) =>
            BindingObservationPolicy.Capture(new BoundProjectObservationSource(this),bindingProcesses,anchor);

        // Future serialized lifecycle integration must call before an actual non-preview attempt.
        // This method only observes; it does not attach, mutate, launch, close or dispose anything.
        public void BeginBindingSnapshotTransition(BindingLifecycleChange change, int? attachingProcessId=null)
        {
            Check();
            if(!ObserveBindingSnapshot().AllowsV2Admission) throw new InvalidOperationException("Binding snapshot unavailable.");
            bindingSnapshots.Begin(change);
            bindingPendingChange=change;
            bindingPendingAnchor=null;
            if(change==BindingLifecycleChange.Attach)
            {
                if(!attachingProcessId.HasValue || attachingProcessId.Value<=0)
                { bindingSnapshots.Fail("ExplicitAttachmentPidRequired"); throw new ArgumentException("Explicit attachment PID required."); }
                bindingPendingAnchor=bindingProcesses.Read(attachingProcessId.Value);
                if(bindingPendingAnchor==null)
                { bindingSnapshots.Fail("AttachmentOsIdentityUnavailable"); throw new InvalidOperationException("Attachment OS identity unavailable."); }
            }
            else if(attachingProcessId.HasValue)
            { bindingSnapshots.Fail("UnexpectedAttachmentPid"); throw new ArgumentException("PID applies only to attachment observation."); }
        }
        public BindingSnapshotObservation CompleteBindingSnapshotTransition()
        {
            Check();
            try
            {
                var anchor=bindingPendingChange==BindingLifecycleChange.Attach ? bindingPendingAnchor :
                    bindingPendingChange==BindingLifecycleChange.Detach ? null : bindingAttachmentAnchor;
                var result=bindingSnapshots.Complete(()=>CaptureBindingObservation(anchor));
                if(result.AllowsV2Admission) bindingAttachmentAnchor=anchor;
                bindingPendingAnchor=null; bindingPendingChange=null;
                return result;
            }
            catch(Exception) /* swallow(native-fallback): failed lifecycle observation clears pending anchors and faults the binding snapshot */ { return FailBindingSnapshotTransition(); }
        }
        public BindingSnapshotObservation FailBindingSnapshotTransition()
        {
            Check(); bindingPendingAnchor=null; bindingPendingChange=null;
            return bindingSnapshots.Fail("BindingLifecycleAttemptFailed");
        }
        private sealed class BoundProjectObservationSource : IBindingProjectObservationSource
        {
            private readonly PlcFoundationEngine owner;
            internal BoundProjectObservationSource(PlcFoundationEngine owner) { this.owner=owner; }
            public BindingCachedFields ReadFields()
            {
                owner.Check();
                return new BindingCachedFields(owner.portal!=null,owner.project!=null,owner.lifecycle.ProcessId,
                    owner.lifecycle.ProjectFile,owner.lifecycle.IsLocalSession);
            }
            public BindingProjectObservation ReadProject()
            {
                owner.Check();
                var current=owner.project ?? throw new InvalidOperationException("No bound native project.");
                // Already used by ProjectDetails/BindProject: typed properties only, no SDK shape invention.
                return new BindingProjectObservation(current.Name,current.Path.FullName);
            }
        }
    }
}
