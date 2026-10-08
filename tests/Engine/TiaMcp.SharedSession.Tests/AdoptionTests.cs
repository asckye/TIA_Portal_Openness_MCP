using System.Threading;
using Siemens.Engineering;
using TiaMcp.PlcFoundation;
using TiaMcpServer.Siemens;
using Xunit;

public sealed class AdoptionTests
{
    private static void Owner(Action action, ApartmentState apartment = ApartmentState.MTA)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(apartment); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }

    [Theory]
    [InlineData(20)] [InlineData(21)]
    public void Foundation_attachment_is_the_engine_binding_and_lease(int release) => Owner(() => {
        Engineering.TiaMajorVersion = release;
        var tia = new TiaPortal(); var project = new ProjectBase("P"); var lease = new PortalProcessLease();
        var state = new PlcRuntimeState { ProcessId = 1234, ProjectFile = "C:/fixture/P.ap" + release, IsAttached = true };
        var engine = new Portal(); engine.AdoptFoundationSession(tia, project, null, state, 123456, lease);
        Assert.Same(project, engine.EngineProject()); Assert.Same(tia, engine.Attachment); Assert.Same(lease, engine.Lease);
        Assert.Equal("P", engine.Binding!.ProjectName); Assert.Equal(1234, engine.Binding.ProcessId);
        Assert.Equal(123456, engine.Binding.StartUtcTicks); Assert.Equal(1, project.NameReads);
        var binding = engine.Binding;
        engine.AdoptFoundationSession(tia, project, null, state, 123456, lease);
        Assert.Same(binding, engine.Binding); Assert.Equal(1, project.NameReads);
        engine.ClearFoundationSession(); Assert.Null(engine.Attachment); Assert.Null(engine.Lease); Assert.Null(engine.Binding);
        Assert.Throws<InvalidOperationException>(() => engine.EngineProject());
        Assert.Equal(0, tia.Disposes); Assert.Equal(0, project.Disposes); Assert.Equal(0, lease.Disposes);
    });

    [Theory]
    [InlineData(null)] [InlineData("outcome unknown")]
    public void Close_disconnect_and_unknown_drop_engine_access_without_native_cleanup(string? fault) => Owner(() => {
        var engine = new Portal(); var tia = new TiaPortal(); var project = new ProjectBase("P");
        var state = new PlcRuntimeState { ProcessId = 1234, IsAttached = true, ProjectFile = "C:/fixture/P.ap21" };
        engine.AdoptFoundationSession(tia, project, null, state, 123456, new PortalProcessLease());
        if (fault == null) engine.AdoptFoundationSession(tia, null, null, new PlcRuntimeState { ProcessId = 1234, IsAttached = true }, 123456, engine.Lease);
        else engine.ClearFoundationSession(fault);
        Assert.Null(engine.Binding); Assert.Equal(fault, engine.Fault);
        Assert.Throws<InvalidOperationException>(() => engine.EngineProject());
        Assert.Equal(0, project.Disposes); Assert.Equal(0, tia.Disposes);
    });

    [Fact]
    public void Adoption_and_clearing_refuse_another_owner_thread()
    {
        Portal? engine = null; Owner(() => { engine = new Portal(); engine.AdoptFoundationSession(null, null, null, new PlcRuntimeState(), 0, null); });
        Owner(() => Assert.Throws<InvalidOperationException>(() => engine!.ClearFoundationSession()));
        Owner(() => Assert.Throws<InvalidOperationException>(() => engine!.AdoptFoundationSession(null, null, null, new PlcRuntimeState(), 0, null)));
        Owner(() => Assert.Throws<InvalidOperationException>(() => new Portal().AdoptFoundationSession(null, null, null, new PlcRuntimeState(), 0, null)), ApartmentState.STA);
    }
}
