using Siemens.Engineering;
using TiaMcp.Adapters;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class WorkerLaunchTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string key in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        foreach (bool ui in new[] { false, true }) yield return new object[] { key, ui };
    }
    [Theory, MemberData(nameof(Cases))]
    public void Explicit_launch_mode_preserves_native_order_and_default_headless(string key, bool ui)
    {
        var engine = new PlcFoundationEngine(key);
        TiaPortal? created = null;
        Assert.False(engine.ProjectLaunchWithUserInterface);
        engine.ProjectLaunchWithUserInterface = ui;
        engine.ProjectClearConnectError = () => TiaMcpServer.ModelContextProtocol.InvocationJournal.Calls.Add("clear");
        engine.ProjectConnectionCreated = portal => { created = portal; TiaMcpServer.ModelContextProtocol.InvocationJournal.Calls.Add("adopt"); };
        engine.ClearCalls();
        var reply = engine.ConnectIsolatedPortal();
        Assert.NotNull(created);
        Assert.Equal(ui ? TiaPortalMode.WithUserInterface : TiaPortalMode.WithoutUserInterface, created.Mode);
        Assert.Equal(new[] { "clear", "TiaPortal.CreateIsolated", "adopt" }, engine.NativeCalls);
        Assert.Contains(ui ? "with user interface" : "headless", reply.Message, StringComparison.Ordinal);
    }
}
