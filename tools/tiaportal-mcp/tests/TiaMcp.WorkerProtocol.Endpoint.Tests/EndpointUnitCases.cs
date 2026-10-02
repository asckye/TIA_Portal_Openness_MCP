using System.Text;
using TiaMcp.WorkerProtocol;
using TiaMcp.WorkerProtocol.Endpoint;
using TiaMcp.WorkerProtocol.JsonLegacy;

static class EndpointUnitCases
{
    public static int Run()
    {
        int count = 0;
        void Check(bool yes, string name) { if (!yes) throw new Exception(name); count++; }
        void Reject(Action act, string name) { try { act(); } catch (IdentityViolation e) { Check(!e.Message.Contains("secret"), name + " sanitized"); return; } throw new Exception("accepted: " + name); }
        var engine = new EngineIdentity(2,"20",new string('a',64),new string('b',64),new string('c',32));
        var binding = BindingSnapshot.Unbound(0);
        var args = JsonPayload.Parse("{}");
        var result = JsonPayload.Parse("{\"count\":3}");
        RequestFrame Request(long id=1, string? nonce=null, EngineIdentity? e=null, WireId? wire=null, BindingSnapshot? before=null, BindingSnapshot? after=null, string op="List")
            => new(wire ?? new WireId(id), new RequestIdentity(id,nonce ?? id.ToString("x32"),e ?? engine,op,before ?? binding,after ?? binding),args);
        WorkerEndpoint Endpoint(Func<JsonPayload,Action<int>,JsonPayload>? callback=null, Func<BindingSnapshot>? observe=null)
        {
            var ep=new WorkerEndpoint(engine,new[]{new ReadOnlyOperation("List",false,callback ?? ((_,_)=>result))},observe ?? (()=>binding));
            ep.CreateHello(); return ep;
        }
        foreach (string name in new[]{"release","workerHash","engineHash","session","wireNumeric","wireString","nonceReplay","requestReplay","unknownOperation","beforeEpoch","beforeProject","afterEpoch","version","duplicate","malformed","frameType"})
        {
            int calls=0; var ep=Endpoint((_,_)=>{calls++;return result;});
            var request=Request();
            if (name=="release") request=Request(e:new EngineIdentity(2,"21",engine.WorkerSha256,engine.EngineSha256,engine.SessionId));
            if (name=="workerHash") request=Request(e:new EngineIdentity(2,"20",new string('d',64),engine.EngineSha256,engine.SessionId));
            if (name=="engineHash") request=Request(e:new EngineIdentity(2,"20",engine.WorkerSha256,new string('d',64),engine.SessionId));
            if (name=="session") request=Request(e:new EngineIdentity(2,"20",engine.WorkerSha256,engine.EngineSha256,new string('d',32)));
            if (name=="wireNumeric") request=Request(wire:new WireId(2));
            if (name=="wireString") request=Request(wire:new WireId("wrong_1"));
            if (name=="unknownOperation") request=Request(op:"Write");
            if (name=="beforeEpoch") request=Request(before:BindingSnapshot.Unbound(1));
            if (name=="beforeProject") request=Request(before:BindingSnapshot.Bound(1,new ProjectIdentity(new string('d',64),42,100)));
            if (name=="afterEpoch") request=Request(after:BindingSnapshot.Unbound(1));
            if (name is "nonceReplay" or "requestReplay")
            {
                ep.Handle(StrictCodec.Encode(Request()),_=>{});
                request=name=="nonceReplay"?Request(2,1.ToString("x32")):Request(1,2.ToString("x32"));
            }
            byte[] bytes=StrictCodec.Encode(request);
            if(name=="version") bytes=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"version\":2","\"version\":1"));
            if(name=="duplicate") bytes=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"version\":2","\"version\":2,\"Version\":2"));
            if(name=="malformed") bytes=new byte[]{255};
            if(name=="frameType") bytes=StrictCodec.Encode(new HelloFrame(engine,binding));
            int beforeCalls=calls;
            Reject(()=>ep.Handle(bytes,_=>{}),name);
            Check(ep.Faulted && calls==beforeCalls,name+" no dispatch");
            Reject(()=>ep.Handle(StrictCodec.Encode(Request(3)),_=>{}),name+" no reuse");
        }
        {
            var ep=Endpoint(); Reject(()=>ep.CreateHello(),"duplicate hello"); Check(ep.Faulted,"duplicate hello terminal");
        }
        {
            var ep=new WorkerEndpoint(engine,new[]{new ReadOnlyOperation("List",false,(_,_)=>result)},()=>binding);
            Reject(()=>ep.Handle(StrictCodec.Encode(Request()),_=>{}),"no hello"); Check(ep.Faulted,"no hello terminal");
        }
        {
            var ep=new WorkerEndpoint(engine,Array.Empty<ReadOnlyOperation>(),()=>BindingSnapshot.Unbound(1));
            Reject(()=>ep.CreateHello(),"nonfresh hello");
        }
        {
            var frames=new List<byte[]>(); var ep=Endpoint((_,_)=>throw new Exception("secret callback data"));
            ep.Handle(StrictCodec.Encode(Request()),frames.Add);
            var reply=(ReplyFrame)StrictCodec.Decode(frames.Single());
            Check(reply.Identity.Outcome==ReplyOutcome.ReadFailed && reply.Result.ToJson()=="{\"code\":\"ReadOperationFailed\"}" && !ep.Faulted,"sanitized stable read failure");
        }
        {
            var ep=Endpoint((_,_)=>null!); Reject(()=>ep.Handle(StrictCodec.Encode(Request()),_=>{}),"null result"); Check(ep.Faulted,"null result terminal");
        }
        {
            var ep=Endpoint(); Reject(()=>ep.Handle(StrictCodec.Encode(Request()),_=>throw new Exception("secret output")),"emit failure"); Check(ep.Faulted,"emit failure terminal");
        }
        {
            WorkerEndpoint? ep=null;
            ep=Endpoint((_,_)=>{try{ep!.Handle(StrictCodec.Encode(Request(2)),_=>{});}catch(IdentityViolation){}return result;});
            Reject(()=>ep.Handle(StrictCodec.Encode(Request()),_=>{}),"reentrant callback"); Check(ep.Faulted,"reentrant terminal");
        }
        foreach(int percent in new[]{-1,101})
        {
            var ep=Endpoint((_,progress)=>{try{progress(percent);}catch(IdentityViolation){}return result;});
            Reject(()=>ep.Handle(StrictCodec.Encode(Request()),_=>{}),"invalid progress"); Check(ep.Faulted,"swallowed invalid progress terminal");
        }
        {
            var ep=Endpoint((_,progress)=>{for(int i=0;i<=WorkerEndpoint.MaximumProgressFrames;i++)progress(50);return result;});
            Reject(()=>ep.Handle(StrictCodec.Encode(Request()),_=>{}),"progress frame limit"); Check(ep.Faulted,"progress cap terminal");
        }
        {
            Action<int>? retained=null; var ep=Endpoint((_,progress)=>{retained=progress;return result;});
            ep.Handle(StrictCodec.Encode(Request()),_=>{});
            Reject(()=>retained!(10),"late progress"); Check(ep.Faulted,"late progress terminal");
        }
        {
            int observes=0;
            var ep=Endpoint(observe:()=>++observes==3?throw new Exception("secret observer"):binding);
            Reject(()=>ep.Handle(StrictCodec.Encode(Request()),_=>{}),"after observer throws"); Check(ep.Faulted,"observer terminal");
        }
        {
            var ep=Endpoint((_,progress)=>{var t=new Thread(()=>{try{progress(10);}catch(IdentityViolation){}});t.Start();t.Join();return result;});
            Reject(()=>ep.Handle(StrictCodec.Encode(Request()),_=>{}),"wrong thread progress"); Check(ep.Faulted,"wrong thread terminal");
        }
        {
            var ep=Endpoint((_,progress)=>{try{progress(10);}catch(IdentityViolation){}return result;});
            Reject(()=>ep.Handle(StrictCodec.Encode(Request()),_=>throw new Exception("secret emit")),"swallowed progress emit failure"); Check(ep.Faulted,"swallowed emit terminal");
        }
        {
            WorkerEndpoint? ep=null; int observed=0; int calls=0;
            ep=Endpoint((_,_)=>{calls++;return result;},()=>{
                if(++observed==2)try{ep!.Handle(StrictCodec.Encode(Request(2)),_=>{});}catch(IdentityViolation){}
                return binding;
            });
            Reject(()=>ep.Handle(StrictCodec.Encode(Request()),_=>{}),"reentrant observer"); Check(ep.Faulted && calls==0,"reentrant observation blocks callback");
        }
        {
            using var started=new ManualResetEventSlim(); using var release=new ManualResetEventSlim();
            var ep=Endpoint((_,_)=>{started.Set();release.Wait();return result;});
            var task=Task.Run(()=>{try{ep.Handle(StrictCodec.Encode(Request()),_=>{});return false;}catch(IdentityViolation){return true;}});
            Check(started.Wait(TimeSpan.FromSeconds(5)),"concurrent callback started");
            try { Reject(()=>ep.Handle(StrictCodec.Encode(Request(2)),_=>{}),"concurrent Handle"); }
            finally { release.Set(); }
            Check(task.GetAwaiter().GetResult() && ep.Faulted,"concurrent endpoint poisoned");
        }
        {
            var ep=Endpoint();
            Reject(()=>ep.Handle(StrictCodec.Encode(Request()),_=>{
                try{ep.Handle(StrictCodec.Encode(Request(2)),_=>{});}catch(IdentityViolation){}
            }),"swallowed reply emission reentry");
            Check(ep.Faulted,"reply emission reentry forbids boundary");
        }
        return count;
    }
}
