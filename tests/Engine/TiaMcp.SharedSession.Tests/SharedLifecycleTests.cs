using System.Threading;
using Siemens.Engineering;
using TiaMcp.Adapters;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;
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
}
