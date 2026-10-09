using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Gui.ControlChannel;
using TiaOpenness.Shared;
using Xunit;
using static TiaOpenness.Gui.Tests.WorkbenchControlTests;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class WorkbenchControlPipeTests(WpfContext wpf)
{
    private static NamedPipeClientStream Client(string name) => new(".", name, PipeDirection.InOut,
        PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
    private static async Task<WorkbenchControlResponse> Send(string name, WorkbenchControlRequest request)
    {
        using var client = Client(name); using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        await client.ConnectAsync(1000, limit.Token);
        Assert.True(WorkbenchControlPipe.ServerMatches(client, LocalPipeSecurity.CurrentSid, Environment.ProcessPath!));
        await WorkbenchControlFrames.Write(client, request, limit.Token);
        return await WorkbenchControlFrames.ReadResponse(client, request.RequestId, limit.Token);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public async Task Real_pipe_and_real_window_support_each_operation(int operation)
    {
        var (window, _, client) = wpf.Run(() => Window());
        string name = "tia-workbench-test-" + Guid.NewGuid().ToString("N"), log = Scratch("pipe") + ".jsonl";
        using var server = new WorkbenchControlServer(window.ControlSurface, window.Dispatcher, _ => Environment.ProcessPath!, name, _ => { }, log);
        try
        {
            server.Start();
            var request = Request((WorkbenchControlOperation)operation);
            var response = await Send(name, request);
            Assert.Equal(WorkbenchControlStatus.Done, response.Status); Assert.Empty(client.Calls);
            Assert.Contains(request.RequestId, File.ReadAllText(log));
        }
        finally { server.Dispose(); wpf.Run(window.Close); }
    }

    [Theory]
    [InlineData("image")] [InlineData("pid")] [InlineData("version")] [InlineData("deadline")]
    [InlineData("future")] [InlineData("disabled")] [InlineData("modal")] [InlineData("human")]
    [InlineData("identity")] [InlineData("throttle")]
    public async Task Real_pipe_refuses_identity_protocol_and_UI_guard_failures(string condition)
    {
        var (window, _, client) = wpf.Run(() => Window());
        string name = "tia-workbench-test-" + Guid.NewGuid().ToString("N");
        using var server = new WorkbenchControlServer(window.ControlSurface, window.Dispatcher,
            _ => condition == "image" ? @"D:\wrong-bundle\TiaMcp.FoundationHost.exe" : Environment.ProcessPath!, name, _ => { }, Scratch("refused") + ".jsonl", () => TestNow);
        bool enabled = App.Settings.WorkbenchControlEnabled;
        try
        {
            server.Start(); var request = Request(now: TestNow);
            switch (condition)
            {
                case "pid": request.Origin.HostProcessId++; break;
                case "version": request.Version = 2; break;
                case "deadline": request.DeadlineUtc = TestNow.AddSeconds(-1); break;
                case "future": request.DeadlineUtc = TestNow.AddMinutes(1); break;
                case "disabled": wpf.Run(() => App.Settings.WorkbenchControlEnabled = false); break;
                case "modal": wpf.Run(window.OpenSettings); break;
                case "human": wpf.Run(() => window.ControlGuard = new(() => TestNow, () => true)); break;
                case "identity": request = Request(WorkbenchControlOperation.DisplayBlock, now: TestNow); request.Origin.BoundProjectFile = @"D:\Other.ap21"; break;
                case "throttle": Assert.Equal(WorkbenchControlStatus.Done, (await Send(name, request)).Status); request = Request(now: TestNow); break;
            }
            var response = await Send(name, request);
            Assert.Equal(WorkbenchControlStatus.Refused, response.Status);
            Assert.Equal(condition switch { "image" or "pid" => WorkbenchControlError.AccessDenied,
                "version" => WorkbenchControlError.UnsupportedCapability, "deadline" => WorkbenchControlError.Timeout,
                "future" => WorkbenchControlError.InvalidArgument, "identity" => WorkbenchControlError.IdentityMismatch,
                _ => WorkbenchControlError.PreconditionFailed }, response.Refusal?.Code);
            Assert.Empty(client.Calls);
        }
        finally { server.Dispose(); wpf.Run(() => { App.Settings.WorkbenchControlEnabled = enabled; window.Close(); }); }
    }

    [Theory]
    [InlineData("unknown")] [InlineData("oversize")] [InlineData("duplicate")] [InlineData("approval")]
    public async Task Malformed_first_frame_is_closed_without_impersonating_or_applying(string condition)
    {
        var (window, _, client) = wpf.Run(() => Window()); int resolved = 0;
        string name = "tia-workbench-test-" + Guid.NewGuid().ToString("N");
        using var server = new WorkbenchControlServer(window.ControlSurface, window.Dispatcher,
            _ => { Interlocked.Increment(ref resolved); return Environment.ProcessPath!; }, name, _ => { }, Scratch("invalid") + ".jsonl");
        try
        {
            server.Start(); using var pipe = Client(name); using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await pipe.ConnectAsync(1000, limit.Token);
            string json = JsonSerializer.Serialize(Request(), WorkbenchControlProtocol.Json);
            json = condition switch { "unknown" => json.Replace("display.page", "execute.native"),
                "duplicate" => json.Replace("\"version\":1", "\"version\":1,\"version\":1"), "approval" => "{\"decision\":\"granted\"}", _ => json };
            byte[] bytes = Encoding.UTF8.GetBytes(json), header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, condition == "oversize" ? WorkbenchControlFrames.MaximumBytes + 1 : bytes.Length);
            await pipe.WriteAsync(header, limit.Token);
            if (condition != "oversize") await pipe.WriteAsync(bytes, limit.Token);
            Assert.Equal(0, await pipe.ReadAsync(new byte[1], limit.Token));
            Assert.Equal(0, resolved); Assert.Empty(client.Calls);
        }
        finally { server.Dispose(); wpf.Run(window.Close); }
    }
}
