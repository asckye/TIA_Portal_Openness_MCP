using System;
using TiaMcp.PlcFoundation;

// Pure policy and fake ownership graph tests; not native Siemens service discovery.
internal static class OfflineCoverageBoundaryTests
{
    private sealed class Item
    {
        internal object? Software;
        internal object Identity=new object();
        internal bool ThrowEquality;
        public override bool Equals(object? other)
        {
            if(ThrowEquality) throw new ApplicationException();
            return other is Item item && Identity.Equals(item.Identity);
        }
        public override int GetHashCode()=>Identity.GetHashCode();
        internal Item[] Children=Array.Empty<Item>();
        internal bool Provider=true;
        internal string? State="Offline";
    }
    private sealed class SoftwareIdentity
    {
        internal readonly int Id;
        internal bool ThrowEquality;
        internal SoftwareIdentity(int id) { Id=id; }
        public override bool Equals(object? other)
        {
            if(ThrowEquality) throw new ApplicationException();
            return other is SoftwareIdentity software && Id==software.Id;
        }
        public override int GetHashCode()=>Id;
    }
    internal static void Run(Action<bool, string> check)
    {
        var plc=new SoftwareIdentity(1);
        var cpu=new Item { Software=plc };
        var rack=new Item { Children=new[]{cpu} };
        int observations=0;
        void Gate(Item[] roots,Item owner,bool rh=false,Func<Item,object?>? readSoftware=null,
            Func<Item,PlcOfflineObservation>? observe=null)
        {
            PlcOfflinePolicy.RequireStandardTarget(roots,owner,plc,n=>n.Children,
                readSoftware ?? (n=>n.Software), observe ?? (n=>
                { observations++; return new PlcOfflineObservation(n.Provider,n.State); }),rh);
        }
        void Fail(Action action,Type expected,string name)
        {
            Exception? failure=null;
            try { action(); } catch(Exception ex) { failure=ex; }
            check(failure?.GetType()==expected,name);
        }
        Gate(new[]{rack},cpu);
        check(observations==1,"Nested unique owner with positive Offline provider admitted");
        var selectedWrapper=new Item { Identity=cpu.Identity,Software=new SoftwareIdentity(1) };
        var actualSoftware=cpu.Software;
        cpu.Software=new SoftwareIdentity(1);
        Gate(new[]{rack},selectedWrapper);
        check(observations==2,"Distinct equal engineering wrappers preserve software and owner identity");
        cpu.Software=actualSoftware;
        cpu.ThrowEquality=true;
        Fail(()=>Gate(new[]{rack},selectedWrapper),typeof(ApplicationException),"Owner equality exception fails closed");
        cpu.ThrowEquality=false;
        cpu.Software=new SoftwareIdentity(1) { ThrowEquality=true };
        Fail(()=>Gate(new[]{rack},cpu),typeof(ApplicationException),"Software equality exception fails closed");
        cpu.Software=actualSoftware;
        Fail(()=>Gate(new[]{cpu,selectedWrapper},cpu),typeof(TiaMcp.Adapters.Contracts.AdapterPreconditionException),"Distinct equal repeated graph wrappers rejected");
        var unrelated=new Item { Software=new object(), State="Online" };
        Gate(new[]{rack,unrelated},cpu);
        check(observations==3,"Other PLC state does not become a target-only XML prerequisite");
        Fail(()=>Gate(Array.Empty<Item>(),cpu),typeof(TiaMcp.Adapters.Contracts.AdapterPreconditionException),"Missing owner rejected");
        Fail(()=>Gate(new[]{rack},new Item { Software=plc }),typeof(TiaMcp.Adapters.Contracts.AdapterPreconditionException),"Mismatched selected owner rejected");
        Fail(()=>Gate(new[]{rack,new Item { Software=plc }} ,cpu),typeof(TiaMcp.Adapters.Contracts.AdapterPreconditionException),"Two software owners rejected");
        Fail(()=>Gate(new[]{cpu,cpu},cpu),typeof(TiaMcp.Adapters.Contracts.AdapterPreconditionException),"Repeated owner graph rejected");
        Fail(()=>Gate(new[]{rack},cpu,true),typeof(TiaMcp.Adapters.Contracts.AdapterPreconditionException),"R/H rejected even with ordinary Offline observation");
        cpu.Provider=false;
        Fail(()=>Gate(new[]{rack},cpu),typeof(TiaMcp.Adapters.Contracts.AdapterPreconditionException),"No standard provider rejected even without R/H provider");
        cpu.Provider=true;
        foreach(var state in new string?[]{null,"Online","Connecting","offline","", "123"})
        {
            cpu.State=state;
            Fail(()=>Gate(new[]{rack},cpu),typeof(TiaMcp.Adapters.Contracts.AdapterPreconditionException),"Nonexact/unknown state rejected: "+state);
        }
        cpu.State="Offline";
        Fail(()=>Gate(new[]{rack},cpu,readSoftware:n=>throw new ApplicationException()),typeof(ApplicationException),"Software service exception fails closed");
        Fail(()=>Gate(new[]{rack},cpu,observe:n=>throw new ApplicationException()),typeof(ApplicationException),"Provider/state exception fails closed");
        Fail(()=>Gate(new[]{rack},cpu,observe:n=>null!),typeof(TiaMcp.Adapters.Contracts.AdapterPreconditionException),"Missing observation rejected");
        cpu.Children=new[]{rack};
        Fail(()=>Gate(new[]{rack},cpu),typeof(TiaMcp.Adapters.Contracts.AdapterPreconditionException),"Cyclic ownership graph rejected");
        cpu.Children=Array.Empty<Item>();
        var missingGraph=new Item { Children=null! };
        Fail(()=>Gate(new[]{rack,missingGraph},cpu),typeof(TiaMcp.Adapters.Contracts.AdapterPreconditionException),"Incomplete device graph rejected");
        var deep=new Item { Software=plc }; var root=deep;
        for(int i=0;i<130;i++) root=new Item { Children=new[]{root} };
        Fail(()=>Gate(new[]{root},deep),typeof(TiaMcp.Adapters.Contracts.AdapterPreconditionException),"Overdeep graph rejected");
        foreach(var release in new[]{"14sp1","15.1","16","17","18","19","20","21"})
            PlcOfflinePolicy.RequireDocumentedRelease(release);
        Fail(()=>PlcOfflinePolicy.RequireDocumentedRelease("15"),typeof(TiaMcp.Adapters.Contracts.AdapterPreconditionException),"Unreviewed release rejected");
        void Reject(string?[] states, bool covered, Type expected, string label)
        {
            Exception? failure = null;
            try { PlcOfflinePolicy.RequireStates(states, covered, "R/H fake evidence"); }
            catch (Exception ex) { failure = ex; }
            check(failure != null && failure.GetType() == expected, label);
        }
        Reject(new[] { "Offline" }, false, typeof(TiaMcp.Adapters.Contracts.AdapterPreconditionException), "Ordinary Offline does not rescue explicitly incomplete R/H coverage");
        Reject(new[] { "Offline", "Offline" }, false, typeof(TiaMcp.Adapters.Contracts.AdapterPreconditionException), "Even two Offline strings cannot override incomplete coverage");
        Reject(Array.Empty<string?>(), true, typeof(TiaMcp.Adapters.Contracts.AdapterPreconditionException), "Empty state evidence is rejected");
        Reject(new string?[] { "Offline", null }, true, typeof(TiaMcp.Adapters.Contracts.AdapterPreconditionException), "Unknown backup is rejected");
        Reject(new string?[] { null, "Offline" }, true, typeof(TiaMcp.Adapters.Contracts.AdapterPreconditionException), "Unknown primary is rejected");
        Reject(new[] { "Offline", "Online" }, true, typeof(TiaMcp.Adapters.Contracts.AdapterPreconditionException), "Online backup is rejected");
        Reject(new[] { "Online", "Offline" }, true, typeof(TiaMcp.Adapters.Contracts.AdapterPreconditionException), "Online primary is rejected");
        PlcOfflinePolicy.RequireStates(new[] { "Offline", "Offline" }, true, "R/H fake evidence");
        check(true, "Complete two-sided Offline evidence accepted");
    }
}
