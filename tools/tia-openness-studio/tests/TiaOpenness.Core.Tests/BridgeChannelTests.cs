using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TiaMcp.WorkerChannel;
using TiaOpenness.Client;
using TiaOpenness.Contracts.Rpc;
using TiaOpenness.Core.Abstractions;
using TiaOpenness.Core.Mock;
using TiaOpenness.Core.Rpc;
using Xunit;

namespace TiaOpenness.Core.Tests;

public sealed class BridgeChannelTests
{
    private static string BridgeExe => Path.Combine(AppContext.BaseDirectory, "bridge", "TiaOpenness.Bridge.exe");
    private static TimeSpan Budget => TimeSpan.FromSeconds(10);
    private static ChannelClient Channel(BridgeClient client) => (ChannelClient)typeof(BridgeClient)
        .GetField("_channel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
    private static Process Child(BridgeClient client) => (Process)typeof(BridgeClient)
        .GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;

    [Theory]
    [InlineData("release")]
    [InlineData("bridge-hash")]
    [InlineData("adapter-hash")]
    [InlineData("nonce")]
    public async Task Mock_hello_refuses_wrong_identity_without_sending_a_call(string wrong)
    {
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var start = new ProcessStartInfo(BridgeExe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in new[] { "--mock", "--openness-version", "21", "--nonce", nonce }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var diagnostics = process.StandardError.ReadToEndAsync();
        using var writes = new CountWrites(process.StandardInput.BaseStream);
        using var channel = new ChannelClient(process.StandardOutput.BaseStream, writes,
            new ChannelIdentity(wrong == "release" ? "16" : "21",
                wrong == "bridge-hash" ? new string('0', 64) : BridgeChannel.Hash(BridgeExe),
                wrong == "adapter-hash" ? new string('0', 64) : BridgeChannel.Hash(BridgeChannel.AdapterPath(Path.GetDirectoryName(BridgeExe)!, "21", true)),
                process.Id, wrong == "nonce" ? new string('0', 64) : nonce), ChannelProfile.Studio);
        try
        {
            await Assert.ThrowsAsync<ChannelFault>(() => channel.ConnectAsync(Budget));
            await Assert.ThrowsAsync<ChannelFault>(() => channel.CallAsync("session.connect", "{}", BindingChange.Advance, false, Budget));
            Assert.Equal(0, writes.Count);
            Assert.False(channel.OutcomeUnknown);
        }
        finally
        {
            channel.Dispose();
            if (!process.WaitForExit(5000)) { process.Kill(); process.WaitForExit(); }
            await diagnostics;
        }
    }

    [Theory]
    [InlineData(-32000, "device.list")]
    [InlineData(-32001, "project.info")]
    [InlineData(-32002, "tag.list")]
    [InlineData(-32003, "session.connect")]
    [InlineData(-32004, "vc.workspaces")]
    public async Task Every_Studio_error_keeps_its_code_message_and_diagnostics(int code, string method)
    {
        // An explicit nonexistent SDK directory makes the unavailable case independent
        // of installed TIA versions. It never attempts to attach or start TIA.
        using var bridge = new BridgeClient(new[] { "--public-api", Path.Combine(AppContext.BaseDirectory, "absent-sdk") });
        bridge.Start(BridgeExe, forceMock: code != -32003, opennessVersion: "21");
        using var old = new RpcDispatcher(() => code == -32003
            ? (ITiaSessionFactory)new UnavailableSessionFactory("No supported V14 SP1, V15.1 or V16-V21 Openness installation was found. Run Doctor.")
            : new MockTiaSessionFactory(), null);
        if (code != -32000 && code != -32003)
        {
            await bridge.CallAsync<object>("session.connect");
            old.Handle(new RpcRequest { Method = "session.connect" });
        }
        if (code == -32002)
        {
            await bridge.CallAsync<object>("project.open", new { path = "Synthetic.ap21" });
            old.Handle(new RpcRequest { Method = "project.open", Params = new JObject { ["path"] = "Synthetic.ap21" } });
        }
        var parameters = new JObject { ["deviceId"] = "missing-device" };
        var previous = old.Handle(new RpcRequest { Method = method, Params = parameters });
        var previousWire = JObject.Parse(JsonConvert.SerializeObject(previous, BridgeJson.Settings))
            .ToObject<RpcResponse>(JsonSerializer.Create(BridgeJson.Settings))!;
        var expected = new BridgeRpcException(method, previousWire.Error);
        var actual = await Assert.ThrowsAsync<BridgeRpcException>(() => bridge.CallAsync<object>(method, parameters));
        Assert.Equal(code, actual.Code);
        Assert.Equal(expected.Message, actual.Message);
        Assert.Equal(expected.Method, actual.Method);
        if (code == -32002)
        {
            var data = JObject.Parse(actual.Data2);
            Assert.Equal("System.Collections.Generic.KeyNotFoundException", data.Value<string>("type"));
            Assert.Contains("MockTiaSession.RequireDevice", data.Value<string>("stack"));
        }
        else Assert.Equal(expected.Data2, actual.Data2);
        Assert.False(Channel(bridge).Poisoned);
        await bridge.CallAsync<object>("session.state");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_before_dispatch_does_not_send_or_consume_an_id(bool duringSerialization)
    {
        using var bridge = new BridgeClient(Array.Empty<string>());
        bridge.Start(BridgeExe, forceMock: true);
        using var cancelled = new CancellationTokenSource();
        if (!duringSerialization) cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.CallAsync<object>("session.connect",
            duringSerialization ? new CancelWhileSerializing(cancelled) : null, cancelled.Token));
        Assert.Equal(0, Channel(bridge).LastRequestId);
        Assert.False(Channel(bridge).Poisoned);
        var state = await bridge.CallRawAsync("session.state");
        Assert.Equal("1", state.Id);
        Assert.False(state.Result.Value<bool>("Connected"));
    }

    [Fact]
    public async Task Mock_result_keeps_the_previous_Newtonsoft_date_tokens_and_DTO_codec()
    {
        using var bridge = new BridgeClient(Array.Empty<string>());
        bridge.Start(BridgeExe, forceMock: true);
        using var old = new RpcDispatcher(() => new MockTiaSessionFactory(), null);
        await bridge.CallAsync<object>("session.connect");
        old.Handle(new RpcRequest { Method = "session.connect" });
        var parameters = new JObject { ["path"] = "Synthetic.ap21" };
        var previous = old.Handle(new RpcRequest { Method = "project.open", Params = parameters });
        var expected = JObject.Parse(JsonConvert.SerializeObject(previous, BridgeJson.Settings))
            .ToObject<RpcResponse>(JsonSerializer.Create(BridgeJson.Settings))!;
        var actual = await bridge.CallRawAsync("project.open", parameters);
        // Each mock samples its own modification time; the fixture's creation date
        // and all other fields are deterministic and must retain their old token types.
        expected.Result["LastModified"] = actual.Result["LastModified"];
        Assert.True(JToken.DeepEquals(expected.Result, actual.Result));
        Assert.IsType<DateTime>(((JValue)actual.Result["CreationTime"]!).Value);
        var serializer = JsonSerializer.Create(BridgeJson.Settings);
        var oldDto = expected.Result.ToObject<TiaOpenness.Contracts.Models.ProjectInfo>(serializer)!;
        var newDto = actual.Result.ToObject<TiaOpenness.Contracts.Models.ProjectInfo>(serializer)!;
        Assert.Equal(oldDto.CreationTime!.Value.ToString("O"), newDto.CreationTime!.Value.ToString("O"));
    }

    [Fact]
    public async Task Killed_bridge_is_never_restarted_or_replayed()
    {
        using var bridge = new BridgeClient(Array.Empty<string>());
        bridge.Start(BridgeExe, forceMock: true);
        await bridge.CallAsync<object>("session.connect");
        var process = Child(bridge);
        process.Kill();
        await process.WaitForExitAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.CallAsync<object>("project.save"));
        Assert.Throws<InvalidOperationException>(() => bridge.Start(BridgeExe, forceMock: true));
        Assert.Equal(1, Channel(bridge).LastRequestId);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("concurrent")]
    [InlineData("cancel")]
    public async Task Failure_after_dispatch_faults_the_session_without_replay(string failure)
    {
        using var bridge = new BridgeClient(Array.Empty<string>());
        Assert.Equal(TimeSpan.FromMinutes(10), bridge.DefaultTimeout);
        bridge.Start(BridgeExe, forceMock: true);
        await bridge.CallAsync<object>("session.connect");
        await bridge.CallAsync<object>("project.open", new { path = "Synthetic.ap21" });
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var stopped = new ManualResetEventSlim();
        bridge.Progress += (_, _) => { entered.Set(); release.Wait(Budget); stopped.Set(); };
        using var cancellation = new CancellationTokenSource();
        bridge.DefaultTimeout = failure == "timeout" ? TimeSpan.FromMilliseconds(500) : Budget;
        var call = bridge.CallAsync<object>("block.import", new { deviceId = "PLC_1", files = new[] { "missing.scl" } }, cancellation.Token);
        try
        {
            Assert.True(entered.Wait(Budget));
            if (failure == "concurrent")
            {
                await Assert.ThrowsAsync<ChannelFault>(() => bridge.CallAsync<object>("session.state"));
                await Assert.ThrowsAsync<ChannelFault>(() => call);
            }
            else if (failure == "cancel")
            {
                cancellation.Cancel();
                var error = await Assert.ThrowsAsync<OperationCanceledException>(() => call);
                Assert.Equal(cancellation.Token, error.CancellationToken);
            }
            else
            {
                var error = await Assert.ThrowsAsync<TimeoutException>(() => call);
                Assert.Equal("The bridge did not answer 'block.import' within " + bridge.DefaultTimeout + ".", error.Message);
            }
        }
        finally { release.Set(); Assert.True(stopped.Wait(Budget)); }
        Assert.True(Channel(bridge).Poisoned);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.CallAsync<object>("session.state"));
    }

    [Fact]
    public async Task Bridge_killed_during_mock_import_is_not_replayed()
    {
        using var bridge = new BridgeClient(Array.Empty<string>());
        bridge.Start(BridgeExe, forceMock: true);
        await bridge.CallAsync<object>("session.connect");
        await bridge.CallAsync<object>("project.open", new { path = "Synthetic.ap21" });
        int killed = 0;
        bridge.Progress += (_, _) =>
        {
            if (Interlocked.Exchange(ref killed, 1) != 0) return;
            Child(bridge).Kill();
            Assert.True(Child(bridge).WaitForExit(5000));
        };
        // The bounded pipe fills with progress while the client kills the child,
        // so a complete reply cannot race ahead of the kill.
        var files = Enumerable.Range(0, 1000).Select(i => "missing-" + i + ".scl").ToArray();
        await Assert.ThrowsAsync<IOException>(() => bridge.CallAsync<object>("block.import", new { deviceId = "PLC_1", files }));
        Assert.Equal(1, killed);
        Assert.True(Channel(bridge).OutcomeUnknown);
        Assert.Equal(3, Channel(bridge).LastRequestId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.CallAsync<object>("block.import", new { deviceId = "PLC_1", files }));
        Assert.Equal(3, Channel(bridge).LastRequestId);
        Assert.Throws<InvalidOperationException>(() => bridge.Start(BridgeExe, forceMock: true));
    }

    [Fact]
    public void Dispose_ends_the_owned_mock_bridge()
    {
        var bridge = new BridgeClient(Array.Empty<string>());
        bridge.Start(BridgeExe, forceMock: true);
        using var process = Process.GetProcessById(Child(bridge).Id);
        bridge.Dispose();
        Assert.True(process.WaitForExit(5000));
        Assert.False(bridge.IsRunning);
        bridge.Dispose();
    }

    [Theory]
    [InlineData("project.save", ChannelOutcome.Unknown)]
    [InlineData("project.info", ChannelOutcome.ReadFailed)]
    public async Task Handled_Studio_errors_preserve_the_outcome_and_keep_the_same_bridge_usable(string method, ChannelOutcome outcome)
    {
        using var bridge = new BridgeClient(Array.Empty<string>());
        bridge.Start(BridgeExe, forceMock: true);
        await bridge.CallAsync<object>("session.connect");
        var process = Child(bridge);
        var channel = Channel(bridge);
        long epoch = channel.BindingEpoch;
        var failure = await Assert.ThrowsAsync<ChannelFailure>(() => channel.CallAsync(method, "{}",
            BridgeChannel.BindingChangeFor(method), BridgeChannel.IsReadOnly(method), Budget));
        Assert.Equal(outcome, failure.Outcome);
        var rpc = JObject.Parse(failure.RpcErrorJson);
        Assert.Equal(-32001, rpc.Value<int>("code"));
        Assert.Equal("No project is open. Call project.open first.", rpc.Value<string>("message"));
        Assert.Equal(2, channel.LastRequestId);
        Assert.Equal(epoch, channel.BindingEpoch);
        Assert.False(channel.Poisoned);
        Assert.Equal("3", (await bridge.CallRawAsync("session.state")).Id);

        var error = await Assert.ThrowsAsync<BridgeRpcException>(() => bridge.CallAsync<object>(method));
        Assert.Equal(-32001, error.Code);
        Assert.Equal(method + " failed (-32001): No project is open. Call project.open first.", error.Message);
        Assert.Equal(4, channel.LastRequestId);
        Assert.Equal(epoch, channel.BindingEpoch);
        Assert.False(channel.Poisoned);
        Assert.False(channel.OutcomeUnknown);
        var opened = await bridge.CallRawAsync("project.open", new { path = "Synthetic.ap21" });
        Assert.Null(opened.Error);
        Assert.Equal("5", opened.Id);
        var retried = await bridge.CallRawAsync(method);
        Assert.Null(retried.Error);
        Assert.Equal("6", retried.Id);
        Assert.Equal(epoch + 1, channel.BindingEpoch);
        Assert.Same(process, Child(bridge));
        Assert.False(process.HasExited);
    }

    [Fact]
    public void Raw_error_data_and_progress_payload_survive_the_channel_envelope()
    {
        const string rpc = "{\"code\":-32002,\"message\":\"故障\",\"data\":{\"type\":\"NativeFailure\",\"stack\":\"original stack\",\"inner\":\"inner message\"}}";
        var identity = new ChannelIdentity("21", new string('a', 64), new string('b', 64), 1, new string('c', 64));
        using var output = new MemoryStream();
        Action<int, string?>? retained = null;
        var server = new ChannelServer(Stream.Null, output, identity, () => new ChannelBinding(0, false), request =>
        {
            retained = request.ReportProgress;
            request.ReportProgress(50, "{\"operation\":\"导出\",\"current\":1,\"total\":2,\"message\":\"块\"}");
            return ChannelResponse.Error(new ChannelFailure("故障", -32603, ChannelOutcome.ReadFailed, rpcErrorJson: rpc));
        }, ChannelProfile.Studio);
        server.WriteHello();
        server.Handle(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tag.list\",\"params\":{},\"bindingEpoch\":0}"));
        var frames = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(JObject.Parse).ToArray();
        Assert.True(JToken.DeepEquals(JObject.Parse(rpc), frames[2]["error"]!["data"]!["rpc"]));
        Assert.Equal("ReadFailed", frames[2]["error"]!["data"]!.Value<string>("outcome"));
        Assert.Equal("导出", frames[1]["params"]!["payload"]!.Value<string>("operation"));
        Assert.Throws<ChannelFault>(() => retained!(100, "{}"));
        Assert.True(server.Poisoned);
    }

    private sealed class CancelWhileSerializing(CancellationTokenSource source)
    {
        public bool withUserInterface { get { source.Cancel(); return false; } }
    }

    private sealed class CountWrites(Stream stream) : Stream
    {
        public int Count { get; private set; }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        { Count++; return stream.WriteAsync(buffer, offset, count, token); }
        public override void Write(byte[] buffer, int offset, int count) { Count++; stream.Write(buffer, offset, count); }
        public override void Flush() => stream.Flush();
        public override Task FlushAsync(CancellationToken token) => stream.FlushAsync(token);
        protected override void Dispose(bool disposing) { if (disposing) stream.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
