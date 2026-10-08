using System.Threading;
using Siemens.Engineering;
using TiaMcp.PlcFoundation;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;
using TiaMcpServer.Worker;
using Xunit;

public sealed class SharedLifecycleTests
{
    private static void Owner(Action action)
    {
        Exception? failure=null;
        var thread=new Thread(() => { try { action(); } catch(Exception error) { failure=error; } });
        thread.SetApartmentState(ApartmentState.MTA); thread.Start(); thread.Join();
        if(failure!=null) throw failure;
    }

    private sealed class Fixture
    {
        internal readonly Portal Engine=new();
        internal readonly PlcLifecycleState Foundation=new();
        internal readonly PortalProcessLease Lease=new();
        internal readonly TiaPortal Tia=new();
        internal readonly SharedSessionLifecycle Lifecycle;
        internal ProjectBase? Project;
        internal object? Session;
        internal bool OwnedPortal;
        internal int Transfers;
        internal Fixture(int release)
        {
            Engineering.TiaMajorVersion=release;
            Engine.AdoptFoundationSession(null,null,null,new PlcRuntimeState(),0,null);
            Lifecycle=new SharedSessionLifecycle(() => {
                Engine.BorrowEngineSession((tia,project,session,state,ticks,lease,owned) => {
                    Assert.Equal(123456,ticks); Assert.Same(Lease,lease); Assert.Same(Tia,tia);
                    Foundation.Adopt(state); Project=project; Session=session; OwnedPortal=owned;
                    Transfers++;
                });
                Refresh();
            },Refresh,reason => {
                Foundation.Detached(); Project=null; Session=null; Engine.ClearFoundationSession(reason);
            });
            Engine.UseSharedLifecycle(Lifecycle,() => { Foundation.Detached(); Project=null; Session=null; });
        }
        internal string Path(string name="P") => "C:/fixture/"+name+".ap"+Engineering.TiaMajorVersion;
        internal void Refresh() => Engine.AdoptFoundationSession(Foundation.ProcessId.HasValue ? Tia : null,
            Project,Session,new PlcRuntimeState { ProcessId=Foundation.ProcessId,ProjectFile=Foundation.ProjectFile,
                OwnsProject=Foundation.OwnsProject,IsLocalSession=Foundation.IsLocalSession },
            Foundation.ProcessId.HasValue ? 123456 : 0,Foundation.ProcessId.HasValue ? Lease : null,OwnedPortal);
        internal void Attach() => Lifecycle.Foundation(() => { Foundation.Attached(1234); return true; });
        internal void Bind(bool owns=true,bool local=false) => Lifecycle.Foundation(() => {
            Project=new ProjectBase("P"); Session=local ? new global::Siemens.Engineering.Multiuser.LocalSession() : null;
            Foundation.Bound(Path(),owns,local); return true;
        });
        internal void AssertShared(string? path)
        {
            Assert.Equal(path==null ? null : ProjectBindingIdentity.CanonicalPath(path),Engine.Binding?.ProjectPath);
            Assert.Equal(path==null ? null : MutationIdentityPolicy.AbsoluteFile(path),Foundation.ProjectFile);
            Assert.Equal(Foundation.OwnsProject,Engine.OwnsProject);
            Assert.Equal(Foundation.IsLocalSession,Engine.LocalSession);
            Assert.Same(Project,Engine.ProjectHandle); Assert.Same(Session,Engine.SessionHandle);
            Assert.Same(Tia,Engine.Attachment); Assert.Same(Lease,Engine.Lease);
            Assert.Equal(0,Lease.Disposes); Assert.Equal(0,Tia.Disposes);
        }
    }

    [Theory]
    [InlineData(20,"RetrieveProjectArchive")] [InlineData(21,"RetrieveProjectArchive")]
    [InlineData(20,"SaveProjectCopy")] [InlineData(21,"SaveProjectCopy")]
    [InlineData(20,"ManageMultiuserSession")] [InlineData(21,"ManageMultiuserSession")]
    [InlineData(20,"BuildProjectScaffold")] [InlineData(21,"BuildProjectScaffold")]
    [InlineData(20,"ConnectIsolatedPortal")] [InlineData(21,"ConnectIsolatedPortal")]
    public void Restored_lifecycle_transfers_one_binding_and_lease(int release,string tool) => Owner(() => {
        var f=new Fixture(release);
        if(tool!="ConnectIsolatedPortal") f.Attach();
        if(tool is "SaveProjectCopy" or "ManageMultiuserSession") f.Bind(local:tool=="ManageMultiuserSession");
        string? path=tool=="ConnectIsolatedPortal" || tool=="ManageMultiuserSession" ? null : f.Path(tool=="SaveProjectCopy" ? "Copy" : "P");
        f.Lifecycle.Engine(() => {
            InvocationJournal.NativeCallStarted();
            var project=path==null ? null : tool=="SaveProjectCopy" ? f.Project : new ProjectBase("P");
            f.Engine.NativeBinding(f.Tia,project,path,f.Lease,tool=="ConnectIsolatedPortal");
            return true;
        });
        f.AssertShared(path); Assert.Equal(tool=="ConnectIsolatedPortal",f.OwnedPortal);
        // A subsequent Foundation close observes the restored engine operation.
        if(path!=null) f.Lifecycle.Foundation(() => {
            f.Foundation.RequireClose(false); f.Foundation.Unbound(); f.Project=null; return true;
        });
        f.AssertShared(null);
        f.Lifecycle.Foundation(() => { f.Foundation.Detached(); f.Project=null; return true; });
        Assert.Null(f.Engine.Attachment); Assert.Null(f.Engine.Binding); Assert.Null(f.Engine.Lease);
        Assert.Null(f.Foundation.ProcessId);
    });

    [Theory]
    [InlineData(20,true)] [InlineData(21,true)] [InlineData(20,false)] [InlineData(21,false)]
    public void Save_as_uses_the_captured_native_identity_even_when_the_handle_stays_the_same(int release,bool moves) => Owner(() => {
        var f=new Fixture(release); f.Attach(); f.Bind(); var original=f.Project;
        string path=f.Path(moves ? "Copy" : "P");
        f.Lifecycle.Engine(() => { f.Engine.NativeBinding(f.Tia,original,path,f.Lease); return true; });
        f.AssertShared(path); Assert.Same(original,f.Project); Assert.Equal(1,f.Transfers);
    });

    [Theory]
    [InlineData(20)] [InlineData(21)]
    public void Save_as_retains_the_existing_borrowed_project_ownership(int release) => Owner(() => {
        var f=new Fixture(release); f.Attach(); f.Bind(owns:false);
        f.Lifecycle.Engine(() => { f.Engine.NativeBinding(f.Tia,f.Project,f.Path("Copy"),f.Lease,ownsProject:false); return true; });
        f.AssertShared(f.Path("Copy")); Assert.False(f.Foundation.OwnsProject);
        Assert.Throws<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() => f.Foundation.RequireClose(false));
    });

    [Theory]
    [InlineData(20)] [InlineData(21)]
    public void Scaffold_steps_refresh_between_connect_close_create_and_save(int release) => Owner(() => {
        var f=new Fixture(release); f.Attach(); f.Bind();
        f.Lifecycle.Engine(() => { f.Engine.NativeBinding(f.Tia,null,null,f.Lease); return true; });
        f.AssertShared(null);
        f.Lifecycle.Engine(() => { f.Engine.NativeBinding(f.Tia,new ProjectBase("New"),f.Path("New"),f.Lease); return true; });
        f.AssertShared(f.Path("New"));
        f.Lifecycle.Engine(() => { f.Lifecycle.Engine(() => true); return true; });
        f.AssertShared(f.Path("New")); Assert.Equal(3,f.Transfers);
    });

    [Theory]
    [InlineData(20,true)] [InlineData(21,true)] [InlineData(20,false)] [InlineData(21,false)]
    public void Local_session_switch_and_commit_clear_both_views(int release,bool close) => Owner(() => {
        var f=new Fixture(release); f.Attach(); f.Bind(local:true);
        f.Lifecycle.Engine(() => { f.Engine.NativeBinding(f.Tia,close ? null : new ProjectBase("Other"),
            close ? null : f.Path("Other"),f.Lease,local:!close); return true; });
        f.AssertShared(close ? null : f.Path("Other"));
    });

    [Theory]
    [InlineData(20,"RetrieveProjectArchive")] [InlineData(21,"RetrieveProjectArchive")]
    [InlineData(20,"SaveProjectCopy")] [InlineData(21,"SaveProjectCopy")]
    [InlineData(20,"ManageMultiuserSession")] [InlineData(21,"ManageMultiuserSession")]
    [InlineData(20,"BuildProjectScaffold")] [InlineData(21,"BuildProjectScaffold")]
    [InlineData(20,"ConnectIsolatedPortal")] [InlineData(21,"ConnectIsolatedPortal")]
    public void Unknown_restored_operation_locks_both_sides_without_a_native_retry(int release,string tool) => Owner(() => {
        var f=new Fixture(release); f.Attach(); f.Bind(); int calls=0;
        Assert.Throws<IOException>(() => f.Lifecycle.Engine<bool>(() => {
            calls++; InvocationJournal.NativeCallStarted(); throw new IOException(tool+" lost acknowledgement");
        }));
        Assert.NotNull(f.Lifecycle.Fault); Assert.Equal(f.Lifecycle.Fault,f.Engine.Fault);
        Assert.Null(f.Foundation.ProcessId); Assert.Null(f.Engine.Attachment); Assert.Null(f.Engine.Binding); Assert.Null(f.Engine.Lease);
        Assert.Throws<InvalidOperationException>(() => f.Lifecycle.Engine(() => ++calls));
        Assert.Throws<InvalidOperationException>(() => f.Lifecycle.Foundation(() => ++calls));
        Assert.Equal(1,calls); Assert.Equal(0,f.Tia.Disposes); Assert.Equal(0,f.Lease.Disposes);
    });

    [Theory]
    [InlineData(20)] [InlineData(21)]
    public void Pre_native_refusal_does_not_lock_or_replace_a_binding(int release) => Owner(() => {
        var f=new Fixture(release); f.Attach(); f.Bind(owns:false);
        Assert.Throws<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() => f.Lifecycle.Foundation<bool>(() => {
            f.Foundation.RequireClose(false); return true;
        }));
        Assert.Null(f.Lifecycle.Fault); f.AssertShared(f.Path());
    });

    [Theory]
    [InlineData(20)] [InlineData(21)]
    public void Foundation_unknown_and_failed_transfer_lock_engine_access(int release) => Owner(() => {
        var f=new Fixture(release); f.Attach(); f.Bind();
        Assert.Throws<IOException>(() => f.Lifecycle.Foundation<bool>(() => {
            InvocationJournal.NativeCallStarted(); throw new IOException("foundation unknown");
        }));
        Assert.NotNull(f.Engine.Fault); Assert.Null(f.Foundation.ProjectFile);
        var broken=new Fixture(release); broken.Attach();
        Assert.Throws<InvalidOperationException>(() => broken.Lifecycle.Engine(() => {
            broken.Engine.NativeBinding(broken.Tia,new ProjectBase("P"),null,broken.Lease); return true;
        }));
        Assert.NotNull(broken.Lifecycle.Fault); Assert.Null(broken.Engine.Attachment);
    });

    [Fact]
    public void Lifecycle_operations_and_lock_refuse_another_thread()
    {
        SharedSessionLifecycle? lifecycle=null;
        Owner(() => lifecycle=new SharedSessionLifecycle(() => { },() => { },_ => { }));
        Owner(() => {
            Assert.Throws<InvalidOperationException>(() => lifecycle!.Engine(() => true));
            Assert.Throws<InvalidOperationException>(() => lifecycle!.Foundation(() => true));
            Assert.Throws<InvalidOperationException>(() => lifecycle!.Lock("unknown"));
        });
    }

    [Fact]
    public void Nested_operation_guard_cannot_bypass_the_owner_thread() => Owner(() => {
        var lifecycle=new SharedSessionLifecycle(() => { },() => { },_ => { });
        lifecycle.Engine(() => {
            Assert.True(lifecycle.Executing);
            Owner(() => Assert.Throws<InvalidOperationException>(() => lifecycle.Executing));
            return true;
        });
    });

    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(true,true)] [InlineData(null,true)]
    public void Owned_portal_disposal_requires_explicit_shared_adoption(bool? owned,bool shared) => Owner(() => {
        var disconnect=new PlcDisconnectState(); int disposes=0;
        Assert.Equal(owned==false || owned==true && shared,PlcDisconnectState.Supports(owned,shared));
        if(owned==null || owned==true && !shared) Assert.Throws<NotSupportedException>(() => disconnect.Execute(1234,owned,() => disposes++,shared));
        else {
            var result=disconnect.Execute(1234,owned,() => disposes++,shared);
            Assert.Equal(owned==true ? "owned-shared-portal-dispose" : "non-owning-attachment-only",result.Strategy);
            disconnect.Execute(1234,owned,() => disposes++,shared); Assert.Equal(1,disposes);
        }
    });

    [Theory]
    [InlineData(20)] [InlineData(21)]
    public void Adoption_marks_only_changed_cached_identity_for_candidate_refresh(int release) => Owner(() => {
        var state=new PlcLifecycleState(); string path="C:/fixture/P.ap"+release;
        var binding=new PlcRuntimeState { ProcessId=1234,ProjectFile=path,OwnsProject=true };
        Assert.True(state.Adopt(binding)); Assert.False(state.Adopt(binding));
        binding.ProjectFile="C:/fixture/Copy.ap"+release; Assert.True(state.Adopt(binding));
        binding.OwnsProject=false; Assert.True(state.Adopt(binding));
        binding.IsLocalSession=true; Assert.True(state.Adopt(binding));
        Assert.False(state.Adopt(binding));
        Assert.True(state.Adopt(new PlcRuntimeState())); Assert.Null(state.ProjectFile);
    });

    [Theory]
    [InlineData(20)] [InlineData(21)]
    public void Unknown_owned_disconnect_clears_both_views_without_a_second_dispose(int release) => Owner(() => {
        var f=new Fixture(release); f.Attach(); f.Bind(); var disconnect=new PlcDisconnectState();
        Assert.Throws<IOException>(() => f.Lifecycle.Foundation(() => disconnect.Execute(1234,true,() => {
            InvocationJournal.NativeCallStarted(); f.Tia.Disposes++; throw new IOException("disconnect acknowledgement lost");
        },sharedPortal:true)));
        Assert.Null(f.Engine.Binding); Assert.Null(f.Foundation.ProcessId); Assert.NotNull(f.Lifecycle.Fault);
        Assert.Throws<InvalidOperationException>(() => f.Lifecycle.Foundation(() => disconnect.Execute(1234,true,() => f.Tia.Disposes++,true)));
        Assert.Equal(1,f.Tia.Disposes); Assert.Equal(0,f.Lease.Disposes);
    });
}
