extern alias foundationhost;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using TiaOpenness.Shared;
using Xunit;
using Connection = foundationhost::TiaMcp.FoundationHost.OperatorConnection;
using Endpoint = foundationhost::TiaMcp.FoundationHost.OperatorEndpoint;
using Session = foundationhost::TiaMcp.FoundationHost.HostSession;
using Options = foundationhost::TiaMcp.FoundationHost.HostOptions;
using Registration = foundationhost::TiaMcp.FoundationHost.HostRegistration;

public sealed class OperatorProtocolTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"protocol\":\"tiamcp.operator\",\"version\":1,\"action\":\"list\"}")]
    [InlineData("{\"protocol\":\"approval\",\"version\":1,\"action\":\"list\",\"workbench\":{\"pid\":1,\"version\":\"1\"}}")]
    [InlineData("{\"protocol\":\"tiamcp.operator\",\"version\":0,\"action\":\"list\",\"workbench\":{\"pid\":1,\"version\":\"1\"}}")]
    [InlineData("{\"protocol\":\"tiamcp.operator\",\"version\":1,\"action\":\"approve\",\"workbench\":{\"pid\":1,\"version\":\"1\"}}")]
    [InlineData("{\"protocol\":\"tiamcp.operator\",\"version\":1,\"action\":\"attach\",\"workbench\":{\"pid\":1,\"version\":\"1\"}}")]
    [InlineData("{\"protocol\":\"tiamcp.operator\",\"version\":1,\"action\":\"list\",\"unknown\":true,\"workbench\":{\"pid\":1,\"version\":\"1\"}}")]
    [InlineData("{\"protocol\":\"tiamcp.operator\",\"version\":1,\"version\":1,\"action\":\"list\",\"workbench\":{\"pid\":1,\"version\":\"1\"}}")]
    [InlineData("{\"protocol\":\"tiamcp.operator\",\"version\":1,\"action\":\"list\",\"workbench\":{\"pid\":1,\"pid\":1,\"version\":\"1\"}}")]
    [InlineData("{\"protocol\":\"tiamcp.operator\",\"version\":1,\"action\":\"list\",\"workbench\":{\"pid\":0,\"version\":\"1\"}}")]
    public async Task Handshake_rejects_missing_unknown_duplicate_and_invalid_fields(string json)
    { using var frame = Frame(Encoding.UTF8.GetBytes(json)); await Assert.ThrowsAsync<InvalidDataException>(() => OperatorProtocol.Read<OperatorHandshake>(frame, CancellationToken.None)); }

    [Theory, InlineData(0), InlineData(-1), InlineData(OperatorProtocol.MaximumBytes + 1)]
    public async Task Frame_budget_is_checked_before_payload_allocation(int size)
    { using var frame = new MemoryStream(BitConverter.GetBytes(size)); await Assert.ThrowsAsync<InvalidDataException>(() => OperatorProtocol.Read<OperatorHandshake>(frame, CancellationToken.None)); }

    [Fact]
    public async Task Invalid_utf8_is_rejected_and_read_does_not_consume_mcp_bytes()
    {
        using var bad = Frame([0xff]); await Assert.ThrowsAsync<InvalidDataException>(() => OperatorProtocol.Read<OperatorHandshake>(bad, CancellationToken.None));
        using var good = new MemoryStream();
        await OperatorProtocol.Write(good, Handshake("list"), CancellationToken.None);
        long length = good.Length; good.Write(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\"}\n")); good.Position = 0;
        var read = await OperatorProtocol.Read<OperatorHandshake>(good, CancellationToken.None);
        Assert.Equal("list", read.Action); Assert.Equal(length, good.Position);
    }

    [Theory, InlineData("other-user"), InlineData("wrong-image"), InlineData("remote")]
    public async Task Rejected_identity_never_reads_a_handshake_or_creates_a_session(string reason)
    {
        Assert.NotEmpty(reason); using var fixture = new OperatorSessionTests.Fixture("19"); using var registration = fixture.Register();
        var connection = new Connection(registration, _ => throw new InvalidOperationException("Unexpected create."), _ => true);
        using var forbidden = new NoIo(); await connection.Serve(forbidden, null, CancellationToken.None);
        Assert.Empty(registration.List());
    }

    [Theory, InlineData("create", 1, "CREATE_NOT_ALLOWED"), InlineData("attach", 1, "SESSION_NOT_FOUND"), InlineData("list", 2, "UNSUPPORTED_VERSION")]
    public async Task Ai_host_refuses_create_and_unknown_session_or_version(string action, int version, string error)
    {
        using var fixture = new OperatorSessionTests.Fixture("19"); using var registration = fixture.Register();
        var (host, client) = OperatorSessionTests.Streams(); using (host) using (client)
        {
            var connection = new Connection(registration, _ => throw new InvalidOperationException("Unexpected create."), _ => false);
            var serving = connection.Serve(host, Environment.ProcessId, CancellationToken.None);
            var request = Handshake(action); request.Version = version;
            await OperatorProtocol.Write(client, request, CancellationToken.None);
            var result = await OperatorProtocol.Read<OperatorHandshakeResult>(client, CancellationToken.None);
            Assert.False(result.Accepted); Assert.Equal(error, result.Error); await serving;
        }
    }

    [Theory, InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Owner_creates_a_private_session_and_pipe_close_releases_it(string release)
    {
        using var fixture = new OperatorSessionTests.Fixture(release); using var registration = fixture.Register();
        var panel = await OperatorSessionTests.Peer.Operator(registration, null, "create", true, async _ => await fixture.Create(true));
        string key = panel.Handshake!.SessionKey!;
        Assert.True(registration.Find(key)!.OperatorOwned);
        await panel.Tool("ConnectPortal", new() { ["processId"] = 104 });
        await panel.Tool("GetSessionState"); await panel.DisposeAsync();
        Assert.Null(registration.Find(key)); Assert.Empty(registration.List());
        Assert.Equal("RELEASED\n", File.ReadAllText(Path.Combine(fixture.DirectoryPath, "104-1.lease")));
    }

    [Theory, InlineData("operator", false), InlineData("operator", true), InlineData("http", false)]
    public async Task Slow_owned_session_shutdown_precedes_endpoint_exit_and_http_keeps_serving(string transport, bool holdStream)
    {
        using var fixture = new OperatorSessionTests.Fixture("19");
        var options = new Options { ReleaseKey = "19", WorkerExe = fixture.Options.WorkerExe, ApiDirectory = fixture.Options.ApiDirectory,
            BundleRoot = fixture.Options.BundleRoot, Transport = transport, OperatorOwner = Environment.ProcessId };
        using var registration = new Registration(options, "S-1-5-21-1-2-3-1001", fixture.DirectoryPath, error => throw error);
        using var ai = new Session("19", "fixture AI", false, [], [], () => new JsonObject(), () => { });
        using var aiMembership = transport == "http" ? registration.Register(ai) : null;
        string lease = Path.Combine(fixture.DirectoryPath, "slow-shutdown.lease");
        File.WriteAllText(lease, "BUSY\n");
        var shutdownStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdownCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var elapsed = new Stopwatch();
        var session = new Session("19", "fixture", true, [], [], () => new JsonObject(), () => {
            elapsed.Start(); shutdownStarted.TrySetResult(true);
            Thread.Sleep(TimeSpan.FromSeconds(3));
            File.WriteAllText(lease, "RELEASED\n"); shutdownCompleted.TrySetResult(true);
        });
        var listener = new MemoryListener();
        registration.Publish();
        var owner = Process.GetCurrentProcess();
        // The fixture injects a trusted peer and memory streams; production Start still performs all kernel checks.
        await using var endpoint = new Endpoint(registration, _ => Task.FromResult<foundationhost::TiaMcp.FoundationHost.IHostSession>(session),
            _ => Environment.ProcessId, listener, owner, options.Transport == "operator");
        using var stream = listener.Connect();
        await OperatorProtocol.Write(stream, Handshake("create"), CancellationToken.None);
        Assert.True((await OperatorProtocol.Read<OperatorHandshakeResult>(stream, CancellationToken.None)).Accepted);
        var client = await McpClientFactory.CreateAsync(new StreamClientTransport(stream, stream));
        try
        {
            await client.DisposeAsync(); stream.Dispose();
            await shutdownStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(endpoint.Completion.IsCompleted); Assert.Equal("BUSY\n", File.ReadAllText(lease));
            using var another = holdStream || transport == "http" ? listener.Connect() : null;
            await shutdownCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(3)); Assert.Equal("RELEASED\n", File.ReadAllText(lease));
            if (transport == "operator")
            {
                if (holdStream) { Assert.False(endpoint.Completion.IsCompleted); another!.Dispose(); }
                await endpoint.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(File.Exists(registration.FilePath));
            }
            else
            {
                Assert.False(endpoint.Completion.IsCompleted); Assert.False(owner.HasExited);
                await OperatorProtocol.Write(another!, Handshake("list"), CancellationToken.None);
                var listed = await OperatorProtocol.Read<OperatorHandshakeResult>(another!, CancellationToken.None);
                Assert.True(listed.Accepted); Assert.Equal(ai.SessionKey, Assert.Single(listed.Sessions)["sessionKey"]!.GetValue<string>());
                Assert.False(endpoint.Completion.IsCompleted);
            }
            Assert.False(owner.HasExited);
        }
        finally { endpoint.Stop(); await client.DisposeAsync(); session.Dispose(); }
    }
    private sealed class MemoryListener : foundationhost::TiaMcp.FoundationHost.IOperatorListener
    {
        private readonly Channel<Stream> pending = Channel.CreateUnbounded<Stream>();
        internal Stream Connect()
        {
            var (host, client) = OperatorSessionTests.Streams();
            Assert.True(pending.Writer.TryWrite(host)); return client;
        }
        public Task<Stream> Accept(CancellationToken token) => pending.Reader.ReadAsync(token).AsTask();
        public void Dispose()
        {
            pending.Writer.TryComplete();
            while (pending.Reader.TryRead(out var stream)) stream.Dispose();
        }
    }

    [Fact]
    public async Task Registration_is_atomic_and_session_end_removes_only_its_record()
    {
        using var fixture = new OperatorSessionTests.Fixture("19"); using var registration = fixture.Register(); registration.Publish();
        using var session = await fixture.Create(); var member = registration.Register(session);
        for (int index = 0; index < 10; index++)
        {
            registration.Publish(); var record = JsonNode.Parse(File.ReadAllText(registration.FilePath))!;
            Assert.Equal(1, record["version"]!.GetValue<int>()); Assert.Equal(session.SessionKey, (string?)record["sessions"]?[0]?["sessionKey"]);
        }
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(registration.FilePath)!, "*.tmp"));
        member.Dispose(); Assert.Empty(JsonNode.Parse(File.ReadAllText(registration.FilePath))!["sessions"]!.AsArray());
        registration.Dispose(); Assert.False(File.Exists(registration.FilePath));
    }

    [WindowsWorkbenchTheory, InlineData(false, false), InlineData(true, false), InlineData(false, true)]
    public async Task Windows_peer_process_token_and_exact_image_are_checked_before_read(bool wrongSid, bool wrongImage)
    {
        string sid = LocalPipeSecurity.CurrentSid, name = "operator-security-" + Guid.NewGuid().ToString("N");
        using var server = OperatorPipe.CreateServer(name, sid, true);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        var accepting = server.WaitForConnectionAsync(); await client.ConnectAsync(1000); await accepting;
        int? peer = OperatorPipe.VerifiedClient(server, wrongSid ? "S-1-5-21-1-2-3-1001" : sid,
            wrongImage ? Path.Combine(Path.GetTempPath(), Path.GetFileName(Environment.ProcessPath!)) : Environment.ProcessPath!);
        Assert.Equal(wrongSid || wrongImage ? null : (int?)Environment.ProcessId, peer);
        Assert.True(LocalPipeSecurity.ServerProcessMatches(client, sid, Environment.ProcessPath!, Environment.ProcessId));
        Assert.False(LocalPipeSecurity.ServerProcessMatches(client, sid, Environment.ProcessPath!, Environment.ProcessId + 1));
    }
    [Fact]
    public void Pipe_name_separates_user_data_root_pid_and_start_time()
    {
        string root = Path.GetTempPath(), sid = "S-1-5-21-1-2-3-1001";
        string name = OperatorPipe.Name(sid, root, 1, 1);
        Assert.StartsWith("TiaMcp.Operator.v1.", name); Assert.Equal(name, OperatorPipe.Name(sid, root, 1, 1));
        Assert.NotEqual(name, OperatorPipe.Name(sid + "0", root, 1, 1)); Assert.NotEqual(name, OperatorPipe.Name(sid, root, 2, 1));
        Assert.NotEqual(name, OperatorPipe.Name(sid, root, 1, 2)); Assert.NotEqual(name, OperatorPipe.Name(sid, Path.Combine(root, "other"), 1, 1));
    }
    [Theory, InlineData("--transport", "operator"), InlineData("--operator-owner", "0"), InlineData("--operator-owner", "bad"), InlineData("--operator-owner", "123")]
    public void Startup_refuses_missing_owner_invalid_pid_and_stdio_owner(string option, string value)
    {
        using var fixture = new OperatorSessionTests.Fixture("19");
        Assert.Throws<ArgumentException>(() => foundationhost::TiaMcp.FoundationHost.HostOptions.Parse(
            ["--bundle-root", fixture.Options.BundleRoot, "--release-key", "19", option, value], AppContext.BaseDirectory));
    }
    [WindowsWorkbenchTheory, InlineData("operator"), InlineData("http")]
    public void Startup_accepts_owned_transport_options_and_rejects_the_wrong_owner_image(string transport)
    {
        using var fixture = new OperatorSessionTests.Fixture("19");
        var options = foundationhost::TiaMcp.FoundationHost.HostOptions.Parse(["--bundle-root", fixture.Options.BundleRoot, "--release-key", "19",
            "--transport", transport, "--operator-owner", Environment.ProcessId.ToString(), "--http-api-key", "fixture"], AppContext.BaseDirectory);
        Assert.Equal(Environment.ProcessId, options.OperatorOwner);
        Assert.Throws<UnauthorizedAccessException>(() => foundationhost::TiaMcp.FoundationHost.OperatorEndpoint.Start(options, _ => throw new InvalidOperationException("Unexpected create.")));
    }
    private static OperatorHandshake Handshake(string action) => new() { Action = action,
        SessionKey = action == "attach" ? Guid.NewGuid().ToString("N") : null,
        Workbench = new OperatorWorkbench { Pid = Environment.ProcessId, Version = "fixture" } };
    private static MemoryStream Frame(byte[] bytes) { var stream = new MemoryStream(); stream.Write(BitConverter.GetBytes(bytes.Length)); stream.Write(bytes); stream.Position = 0; return stream; }
    private sealed class NoIo : MemoryStream
    {
        public override Task<int> ReadAsync(byte[] bytes, int offset, int count, CancellationToken token) => throw new InvalidOperationException("Identity failure read a request.");
        public override Task WriteAsync(byte[] bytes, int offset, int count, CancellationToken token) => throw new InvalidOperationException("Identity failure served a response.");
    }
}
