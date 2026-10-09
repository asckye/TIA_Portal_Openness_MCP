using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcp.WorkbenchControl.Tests;

public sealed class WindowsPipeFactAttribute : FactAttribute
{
    public WindowsPipeFactAttribute()
    { if (!OperatingSystem.IsWindows()) Skip = "Named pipe SID/ACL/image checks require Windows."; }
}

public sealed class PipeSecurityTests
{
    private static string Name() => "TiaMcp.Workbench.test." + Guid.NewGuid().ToString("N");
    private static async Task Connected(Func<NamedPipeServerStream, NamedPipeClientStream, string, Task> test)
    {
        string sid = LocalPipeSecurity.CurrentSid, name = Name();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server = WorkbenchControlPipe.CreateServer(name, sid, true);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        var accept = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(250, timeout.Token); await accept;
        // Windows impersonation is available only after the server has read client data.
        await client.WriteAsync(new byte[] { 1 }, timeout.Token); var data = new byte[1];
        Assert.Equal(1, await server.ReadAsync(data, timeout.Token));
        await test(server, client, sid);
    }
    [WindowsPipeFact]
    public void ProtectedAclAndFirstInstanceBehaviorMatchApproval()
    {
        if (!OperatingSystem.IsWindows()) return;
        string sid = LocalPipeSecurity.CurrentSid;
        var descriptor = new RawSecurityDescriptor(LocalPipeSecurity.SecurityDescriptor(sid));
        Assert.Equal(sid, descriptor.Owner!.Value);
        Assert.True(descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
        Assert.Single(descriptor.DiscretionaryAcl!.Cast<GenericAce>());
        Assert.Equal(sid, ((CommonAce)descriptor.DiscretionaryAcl![0]).SecurityIdentifier.Value);
        string name = Name();
        using var first = WorkbenchControlPipe.CreateServer(name, sid, true);
        Assert.Throws<System.ComponentModel.Win32Exception>(() => WorkbenchControlPipe.CreateServer(name, sid, true));
        using var next = WorkbenchControlPipe.CreateServer(name, sid, false);
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalPipeSecurity.CreateServer(Name(), sid, true, 0));
        Assert.Equal(8, WorkbenchControlPipe.MaximumInstances);
        Assert.Equal(ApprovalPipe.SecurityDescriptor(sid), LocalPipeSecurity.SecurityDescriptor(sid));
    }
    [WindowsPipeFact]
    public async Task BothEndpointsRequireTheCurrentUser()
        => await Connected((server, client, sid) =>
        {
            Assert.True(LocalPipeSecurity.PeerIsCurrentUser(server, sid));
            Assert.True(LocalPipeSecurity.ServerIsCurrentUser(client, sid));
            Assert.False(LocalPipeSecurity.PeerIsCurrentUser(server, "S-1-5-18"));
            Assert.False(LocalPipeSecurity.ServerIsCurrentUser(client, "S-1-5-18"));
            string image = Process.GetCurrentProcess().MainModule!.FileName;
            Assert.False(WorkbenchControlPipe.PeerMatches(server, "S-1-5-18", image));
            Assert.False(WorkbenchControlPipe.ServerMatches(client, "S-1-5-18", image));
            return Task.CompletedTask;
        });
    [WindowsPipeFact]
    public async Task BothEndpointsRequireTheExactResolvedImage()
        => await Connected((server, client, sid) =>
        {
            string image = Process.GetCurrentProcess().MainModule!.FileName;
            Assert.True(WorkbenchControlPipe.PeerMatches(server, sid, image));
            Assert.True(WorkbenchControlPipe.ServerMatches(client, sid, image));
            Assert.True(WorkbenchControlPipe.ServerMatches(client, sid, image.ToUpperInvariant()));
            foreach (var wrong in new[] { image + ".other", Path.Combine(Path.GetDirectoryName(image)!, "other", Path.GetFileName(image)), "relative.exe", "" })
            {
                Assert.False(WorkbenchControlPipe.PeerMatches(server, sid, wrong));
                Assert.False(WorkbenchControlPipe.ServerMatches(client, sid, wrong));
            }
            return Task.CompletedTask;
        });
}
