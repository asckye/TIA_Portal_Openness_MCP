using TiaMcp.PlcFoundation;

internal static class BindingSnapshotTests
{
    static int checks;
    static void Check(bool value,string name) { if(!value) throw new Exception(name); checks++; }
    static readonly BindingProcessObservation Anchor=new(321,638000000000000000);
    static FakeProject Detached()=>new(new(false,false,null,null,false));
    static FakeProject Attached()=>new(new(true,false,321,null,false));
    static FakeProject Bound(bool local=false)=>new(new(true,true,321,@"C:\work\Line.ap20",local));
    static BindingObservation Capture(FakeProject source,BindingProcessObservation? anchor=null,FakeProcess? processes=null)=>BindingObservationPolicy.Capture(source,processes??new FakeProcess(),anchor);
    static void Unknown(BindingObservation observation,string name)=>Check(observation.State==BindingObservationState.Unknown&&!observation.AllowsV2Admission,name);
    static void Throws(Action action,string name) { try { action(); } catch(InvalidOperationException) { checks++; return; } throw new Exception(name); }
    public static void Main()
    {
        var detached=Capture(Detached()); Check(detached.AllowsV2Admission&&detached.State==BindingObservationState.Unbound,"fresh detached known unbound");
        var project=Bound(); var weak=Capture(project); Check(weak.State==BindingObservationState.Bound && weak.Strength==BindingIdentityStrength.WeakPidOnly && !weak.AllowsV2Admission,"existing PID stays weak even with OS samples");
        Check(project.ProjectReads==2,"native project sampled twice");
        var bound=Capture(Bound(),Anchor); Check(bound.AllowsV2Admission&&bound.Strength==BindingIdentityStrength.OsStartTimeAnchored,"OS-anchored bound observation");
        Check(bound.ProjectSha256?.Length==64,"digest exists");
        var p=Bound(); p.ThrowProject=true; Unknown(Capture(p,Anchor),"native project failure unknown");
        p=Bound(); p.ThrowFields=true; Unknown(Capture(p,Anchor),"cached source failure unknown");
        p=Bound(); p.Project=new("",@"C:\work\Line.ap20"); Unknown(Capture(p,Anchor),"empty name unknown");
        p=Bound(); p.Project=new("Line",@"C:\other\Line.ap20"); Unknown(Capture(p,Anchor),"path mismatch unknown");
        p=Bound(); p.Project=new("Line","relative.ap20"); Unknown(Capture(p,Anchor),"relative path unknown");
        p=Bound(); p.NextProject=new("Other",@"C:\work\Line.ap20"); Unknown(Capture(p,Anchor),"rename during observation unknown");
        p=Bound(); p.NextFields=new(true,false,321,null,false); Unknown(Capture(p,Anchor),"binding change during observation unknown");
        Unknown(Capture(Bound(),Anchor,new FakeProcess{Throw=true}),"OS permission failure unknown");
        Unknown(Capture(Bound(),Anchor,new FakeProcess{First=null}),"process missing unknown");
        Unknown(Capture(Bound(),Anchor,new FakeProcess{Second=null}),"process exits during observation unknown");
        Unknown(Capture(Bound(),Anchor,new FakeProcess{Second=new(321,Anchor.StartUtcTicks+1)}),"PID reuse between observations unknown");
        Unknown(Capture(Bound(),new(321,Anchor.StartUtcTicks-1)),"PID reused since attach unknown");
        Unknown(Capture(Bound(),Anchor,new FakeProcess{First=new(999,Anchor.StartUtcTicks)}),"wrong process returned unknown");
        Unknown(Capture(new(new(false,true,321,@"C:\work\Line.ap20",false))),"detached inconsistent unknown");
        Unknown(Capture(new(new(true,true,null,@"C:\work\Line.ap20",false))),"portal missing cached pid unknown");
        Unknown(Capture(new(new(true,false,321,@"C:\work\Line.ap20",false))),"cache/object mismatch unknown");
        Unknown(Capture(new(new(true,false,321,null,true))),"session flag without project unknown");
        Unknown(Capture(Detached(),Anchor),"stale anchor on detach unknown");
        Check(Capture(Attached(),Anchor).AllowsV2Admission,"anchored attached unbound observed");
        Check(!Capture(Attached()).AllowsV2Admission,"PID-only attached unbound denied too");
        var digest=BindingObservationPolicy.ProjectDigest(false,@"c:/work/./Line.ap20","Line");
        Check(digest=="162e12bb6cdc061171636ab946174d43147a49ea5518cff7d58e06a91ede3042","versioned digest fixed vector");
        Check(digest==bound.ProjectSha256,"digest canonicalizes path separators dots and case");
        Check(digest!=BindingObservationPolicy.ProjectDigest(true,@"C:\work\Line.ap20","Line"),"digest separates session kind");
        Check(digest!=BindingObservationPolicy.ProjectDigest(false,@"C:\work\Line.ap20","line"),"digest preserves exact project name");
        Check(digest!=BindingObservationPolicy.ProjectDigest(false,@"C:\work\Line2.ap20","Line"),"digest separates file");
        var tracker=new BindingSnapshotTracker();
        Check(tracker.Observe(detached).Epoch==0,"fresh epoch zero");
        Check(tracker.Observe(detached).Epoch==0,"observing does not advance epoch");
        tracker.Begin(BindingLifecycleChange.Attach);
        Check(tracker.Complete(Capture(Attached(),Anchor)).Epoch==1,"attach epoch one");
        tracker.Begin(BindingLifecycleChange.Bind);
        Check(tracker.Complete(bound).Epoch==2,"bind epoch two");
        Check(tracker.Observe(bound).Epoch==2,"read stable epoch");
        tracker.Begin(BindingLifecycleChange.Unbind);
        Check(tracker.Complete(Capture(Attached(),Anchor)).Epoch==3,"unbind epoch three");
        tracker.Begin(BindingLifecycleChange.Bind);
        Check(tracker.Complete(bound).Epoch==4,"same project ABA gets new epoch");
        tracker.Begin(BindingLifecycleChange.Detach);
        Check(tracker.Complete(detached).Epoch==5,"detach epoch five");
        Check(tracker.Observe(detached).AllowsV2Admission,"detached admitted after explicit completion");
        var fault=new BindingSnapshotTracker(); Unknown(fault.Observe(weak).Binding,"weak cannot initialize v2");
        Unknown(fault.Observe(detached).Binding,"fault sticky cannot recover silently");
        fault=new(); fault.Observe(detached); Unknown(fault.Observe(bound).Binding,"unannounced binding fault");
        fault=new(); fault.Observe(detached); fault.Begin(BindingLifecycleChange.Attach);
        Unknown(fault.Observe(detached).Binding,"read during lifecycle uncertain");
        Unknown(fault.Complete(Capture(Attached(),Anchor)).Binding,"late completion cannot revive fault");
        fault=new(); fault.Observe(detached); fault.Begin(BindingLifecycleChange.Attach);
        Unknown(fault.Complete(bound).Binding,"attach cannot secretly bind project");
        fault=new(); fault.Observe(detached); Throws(()=>fault.Begin(BindingLifecycleChange.Bind),"bind before attach refused");
        fault=new(); fault.Observe(detached); Throws(()=>fault.Begin((BindingLifecycleChange)100),"invalid transition refused");
        fault=new(); Unknown(fault.Complete(detached).Binding,"completion without begin denied");
        fault=new(); fault.Observe(detached); fault.Begin(BindingLifecycleChange.Attach); fault.Complete(Capture(Attached(),Anchor)); fault.Begin(BindingLifecycleChange.Bind);
        var otherAnchor=new BindingProcessObservation(321,Anchor.StartUtcTicks+2);
        Unknown(fault.Complete(Capture(Bound(),otherAnchor,new FakeProcess{First=otherAnchor,Second=otherAnchor})).Binding,"bind cannot change process anchor");
        int callbacks=0;
        fault=new(); fault.Observe(detached); fault.Begin(BindingLifecycleChange.Attach);
        Unknown(fault.Observe(()=>{callbacks++; return detached;}).Binding,"pending refuses lazy capture");
        Check(callbacks==0,"pending observation performs no native capture");
        Unknown(fault.Complete(()=>{callbacks++; return detached;}).Binding,"faulted refuses completion capture");
        Check(callbacks==0,"faulted completion performs no native capture");
        fault=new(); Unknown(fault.Complete(()=>{callbacks++; return detached;}).Binding,"missing transition refuses capture");
        Check(callbacks==0,"missing transition performs no native capture");
        fault=new(); fault.Observe(detached); fault.Fail("test");
        Unknown(fault.Observe(()=>{callbacks++; return detached;}).Binding,"faulted refuses lazy observation");
        Check(callbacks==0,"faulted observation performs no native capture");
        fault=new(); Unknown(fault.Observe(()=>throw new UnauthorizedAccessException()).Binding,"lazy capture exception faults");
        p=Bound(); Unknown(Capture(p,new(321,Anchor.StartUtcTicks-1)),"stale anchor rejected before project read");
        Check(p.ProjectReads==0,"stale anchor causes no native project reads");
        p=Bound(); Unknown(Capture(p,Anchor,new FakeProcess{First=null}),"missing OS rejected before project read");
        Check(p.ProjectReads==0,"missing process causes no native project reads");
        fault=new(); callbacks=0;
        Unknown(fault.Observe(()=>{
            var nested=fault.Observe(()=>{callbacks++; return detached;});
            return detached;
        }).Binding,"observation reentry cannot overwrite nested fault");
        Check(callbacks==0,"nested observation callback never executes");
        fault=new(); fault.Observe(detached); fault.Begin(BindingLifecycleChange.Attach);
        Unknown(fault.Complete(()=>{
            var nested=fault.Complete(()=>{callbacks++; return Capture(Attached(),Anchor);});
            return Capture(Attached(),Anchor);
        }).Binding,"completion reentry cannot overwrite nested fault");
        Check(callbacks==0,"nested completion callback never executes");
        fault=new(); Unknown(fault.ReadObservation(()=>{
            fault.ReadObservation(()=>{callbacks++; return detached;});
            return detached;
        }),"diagnostic reentry faults without nested native capture");
        Check(callbacks==0,"diagnostic nested callback never executes");
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name!.StartsWith("Siemens.",StringComparison.Ordinal)),"no Siemens assembly loaded");
        Console.WriteLine($"Binding snapshot checks passed: {checks}; native SDK compilation and runtime acceptance not performed.");
    }
    sealed class FakeProcess : IBindingProcessObservationSource
    {
        public BindingProcessObservation? First=Anchor,Second=Anchor;
        public bool Throw;
        int reads;
        public BindingProcessObservation? Read(int processId) { if(Throw) throw new UnauthorizedAccessException(); return ++reads==1 ? First : Second; }
    }
    sealed class FakeProject : IBindingProjectObservationSource
    {
        readonly BindingCachedFields fields;
        int fieldReads;
        public int ProjectReads;
        public bool ThrowFields,ThrowProject;
        public BindingCachedFields? NextFields;
        public BindingProjectObservation Project=new("Line",@"C:\work\Line.ap20");
        public BindingProjectObservation? NextProject;
        public FakeProject(BindingCachedFields fields) { this.fields=fields; }
        public BindingCachedFields ReadFields() { if(ThrowFields) throw new InvalidOperationException(); return ++fieldReads>1 ? NextFields??fields : fields; }
        public BindingProjectObservation ReadProject() { if(ThrowProject) throw new InvalidOperationException(); return ++ProjectReads>1 ? NextProject??Project : Project; }
    }
}
