using System.Net;
using System.Net.Sockets;
using System.Text;
using Workstation.ServiceModel.Ua;
using Workstation.ServiceModel.Ua.Channels;
using Xunit;
using Xunit.Abstractions;

public sealed class OpcUaEndpointAuthorityTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Runtime_reader_keeps_the_listed_authority_after_a_redirecting_discovery_reply()
    {
        await using var server = new DiscoveryFixture();
        using var settings = new OnlineTargetSettingsScope("target=127.0.0.1");
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("21");
        using var session = fixture.EnterSession();
        var body = (await fixture.Call("GetPlcLiveValuesOpcUa", new {
            endpointUrl = server.CheckedUrl, nodeIds = new[] { "i=2258" }, timeoutMs = 120000 })).StructuredContent!;
        await server.Completion;
        Assert.DoesNotContain("online-target-", body.ToJsonString());
        Assert.Equal(1, server.DiscoveryReplies);
        Assert.Equal(2, server.CheckedConnections);
        Assert.Equal(new[] { server.CheckedUrl, server.CheckedUrl }, server.HelloUrls);
        Assert.False(server.RedirectListener.Pending());
        Assert.Null(TiaMcp.Adapters.Contracts.OnlineTargetPolicy.RuntimeRefusal("GetPlcLiveValuesOpcUa", server.CheckedUrl,
            TiaMcp.Adapters.Contracts.OnlineTargetPolicy.Parse("target=127.0.0.1")));
        Assert.NotNull(TiaMcp.Adapters.Contracts.OnlineTargetPolicy.RuntimeRefusal("GetPlcLiveValuesOpcUa", server.AdvertisedUrl,
            TiaMcp.Adapters.Contracts.OnlineTargetPolicy.Parse("target=127.0.0.1")));
        output.WriteLine("Runtime GetPlcLiveValuesOpcUa: list permits 127.0.0.1; advertised {0}; both HELs used {1}; no redirect connection.",
            server.AdvertisedUrl, server.CheckedUrl);
    }

    [Fact]
    public async Task Discovery_response_cannot_redirect_the_string_constructor_to_another_host_or_port()
    {
        await using var server = new DiscoveryFixture();
        var channel = new ClientSessionChannel(new ApplicationDescription { ApplicationName = "Offline authority fixture" },
            null, new AnonymousIdentity(), server.CheckedUrl, SecurityPolicyUris.None);
        try
        {
            var failure = await Record.ExceptionAsync(() => channel.OpenAsync(server.Token));
            Assert.NotNull(failure);
            await server.Completion;
            Assert.Equal(1, server.DiscoveryReplies);
            Assert.Equal(2, server.CheckedConnections);
            Assert.Equal(server.CheckedUrl, channel.RemoteEndpoint.EndpointUrl);
            Assert.Equal(new[] { server.CheckedUrl, server.CheckedUrl }, server.HelloUrls);
            Assert.False(server.RedirectListener.Pending());
            output.WriteLine("Workstation.UaClient {0}: GetEndpoints advertised {1}; both TCP HELs used {2}; redirect listener accepted none.",
                typeof(ClientSessionChannel).Assembly.GetName().Version, server.AdvertisedUrl, server.CheckedUrl);
            output.WriteLine("The fixture intentionally returns UA ERR on the second checked connection, before any session/read.");
        }
        finally { await channel.AbortAsync(); }
    }

    // Minimal real UA TCP/UASC None discovery server. The second HEL proves the chosen connection authority;
    // rejecting that connection avoids implementing sessions or reading any device data.
    private sealed class DiscoveryFixture : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        internal readonly TcpListener RedirectListener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(120));
        private readonly EncodingContext context = new();
        internal CancellationToken Token => stop.Token;
        internal string CheckedUrl { get; }
        internal string AdvertisedUrl { get; }
        internal int CheckedConnections;
        internal int DiscoveryReplies;
        internal readonly List<string> HelloUrls = new();
        internal Task Completion { get; }

        internal DiscoveryFixture()
        {
            listener.Start();
            RedirectListener.Start();
            CheckedUrl = "opc.tcp://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/checked";
            AdvertisedUrl = "opc.tcp://localhost:" + ((IPEndPoint)RedirectListener.LocalEndpoint).Port + "/redirect";
            Completion = Serve();
        }

        private async Task Serve()
        {
            for (int connection = 0; connection < 2; connection++)
            {
                using var client = await listener.AcceptTcpClientAsync(Token);
                CheckedConnections++;
                using var stream = client.GetStream();
                var hello = await ReadFrame(stream);
                Assert.Equal("HELF", Encoding.ASCII.GetString(hello, 0, 4));
                using (var decoder = Decode(hello, 8))
                {
                    for (int index = 0; index < 5; index++) decoder.ReadUInt32(null);
                    HelloUrls.Add(Assert.IsType<string>(decoder.ReadString(null)));
                }
                if (connection == 1)
                {
                    await Send(stream, "ERRF", encoder => {
                        encoder.WriteStatusCode(null, StatusCodes.BadTcpEndpointUrlInvalid);
                        encoder.WriteString(null, "Offline fixture: checked authority observed; session deliberately refused.");
                    });
                    return;
                }
                await Send(stream, "ACKF", encoder => {
                    encoder.WriteUInt32(null, 0);
                    encoder.WriteUInt32(null, 65536);
                    encoder.WriteUInt32(null, 65536);
                    encoder.WriteUInt32(null, 0);
                    encoder.WriteUInt32(null, 0);
                });
                var open = await ReadFrame(stream);
                Assert.Equal("OPNF", Encoding.ASCII.GetString(open, 0, 4));
                uint openRequest;
                OpenSecureChannelRequest request;
                using (var decoder = Decode(open, 8))
                {
                    decoder.ReadUInt32(null);
                    Assert.Equal(SecurityPolicyUris.None, decoder.ReadString(null));
                    decoder.ReadByteString(null);
                    decoder.ReadByteString(null);
                    decoder.ReadUInt32(null);
                    openRequest = decoder.ReadUInt32(null);
                    Assert.Equal(NodeId.Parse(ObjectIds.OpenSecureChannelRequest_Encoding_DefaultBinary), decoder.ReadNodeId(null));
                    request = decoder.ReadEncodable<OpenSecureChannelRequest>(null);
                    Assert.NotNull(request.RequestHeader);
                }
                await Send(stream, "OPNF", encoder => {
                    encoder.WriteUInt32(null, 1);
                    encoder.WriteString(null, SecurityPolicyUris.None);
                    encoder.WriteByteString(null, null);
                    encoder.WriteByteString(null, null);
                    encoder.WriteUInt32(null, 1);
                    encoder.WriteUInt32(null, openRequest);
                    encoder.WriteNodeId(null, NodeId.Parse(ObjectIds.OpenSecureChannelResponse_Encoding_DefaultBinary));
                    encoder.WriteEncodable(null, new OpenSecureChannelResponse {
                        ResponseHeader = Header(request.RequestHeader!.RequestHandle),
                        SecurityToken = new ChannelSecurityToken {
                            ChannelId = 1, TokenId = 1, CreatedAt = DateTime.UtcNow, RevisedLifetime = 600000 } });
                });
                var query = await ReadFrame(stream);
                Assert.Equal("MSGF", Encoding.ASCII.GetString(query, 0, 4));
                uint queryRequest;
                GetEndpointsRequest endpoints;
                using (var decoder = Decode(query, 8))
                {
                    decoder.ReadUInt32(null);
                    decoder.ReadUInt32(null);
                    decoder.ReadUInt32(null);
                    queryRequest = decoder.ReadUInt32(null);
                    Assert.Equal(NodeId.Parse(ObjectIds.GetEndpointsRequest_Encoding_DefaultBinary), decoder.ReadNodeId(null));
                    endpoints = decoder.ReadEncodable<GetEndpointsRequest>(null);
                    Assert.NotNull(endpoints.RequestHeader);
                    Assert.Equal(CheckedUrl, endpoints.EndpointUrl);
                }
                await Send(stream, "MSGF", encoder => {
                    encoder.WriteUInt32(null, 1);
                    encoder.WriteUInt32(null, 1);
                    encoder.WriteUInt32(null, 2);
                    encoder.WriteUInt32(null, queryRequest);
                    encoder.WriteNodeId(null, NodeId.Parse(ObjectIds.GetEndpointsResponse_Encoding_DefaultBinary));
                    encoder.WriteEncodable(null, new GetEndpointsResponse {
                        ResponseHeader = Header(endpoints.RequestHeader!.RequestHandle),
                        Endpoints = new[] { new EndpointDescription {
                            EndpointUrl = AdvertisedUrl,
                            Server = new ApplicationDescription { ApplicationName = "Offline fixture", ApplicationType = ApplicationType.Server },
                            SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicyUris.None,
                            TransportProfileUri = "http://opcfoundation.org/UA-Profile/Transport/uatcp-uasc-uabinary",
                            UserIdentityTokens = new[] { new UserTokenPolicy { PolicyId = "anonymous", TokenType = UserTokenType.Anonymous } }
                        } } });
                });
                DiscoveryReplies++;
                var close = await ReadFrame(stream);
                Assert.Equal("CLOF", Encoding.ASCII.GetString(close, 0, 4));
            }
        }

        private static ResponseHeader Header(uint handle) => new() {
            Timestamp = DateTime.UtcNow, RequestHandle = handle, ServiceResult = StatusCodes.Good };

        private BinaryDecoder Decode(byte[] frame, int offset)
            => new(new MemoryStream(frame, offset, frame.Length - offset, false), context, false);

        private async Task<byte[]> ReadFrame(NetworkStream stream)
        {
            var header = new byte[8];
            await stream.ReadExactlyAsync(header, Token);
            int length = BitConverter.ToInt32(header, 4);
            Assert.InRange(length, 8, 1024 * 1024);
            var frame = new byte[length];
            header.CopyTo(frame, 0);
            await stream.ReadExactlyAsync(frame.AsMemory(8), Token);
            return frame;
        }

        private async Task Send(NetworkStream stream, string type, Action<BinaryEncoder> body)
        {
            using var buffer = new MemoryStream();
            using var encoder = new BinaryEncoder(buffer, context, true);
            encoder.WriteUInt32(null, BitConverter.ToUInt32(Encoding.ASCII.GetBytes(type)));
            encoder.WriteUInt32(null, 0);
            body(encoder);
            byte[] frame = buffer.ToArray();
            BitConverter.GetBytes(frame.Length).CopyTo(frame, 4);
            await stream.WriteAsync(frame, Token);
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            RedirectListener.Stop();
            try { await Completion; }
            catch (OperationCanceledException) { /* swallow(teardown): cancelling this fixture ends its pending loopback accept/read. */ }
            finally { stop.Dispose(); }
        }
    }

    private sealed class EncodingContext : IEncodingContext
    {
        public IReadOnlyList<string> NamespaceUris => new[] { "http://opcfoundation.org/UA/" };
        public IReadOnlyList<string> ServerUris => Array.Empty<string>();
        public int MaxStringLength => 1024 * 1024;
        public int MaxArrayLength => 1024 * 1024;
        public int MaxByteStringLength => 1024 * 1024;
    }
}
