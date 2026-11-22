extern alias toolpipeline;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Engine.Tests.Shared;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed class RuntimeChannelDiagnosticsTests
{
    public static IEnumerable<object[]> ReadFailures() => NativeFailureDiagnosticsTests.Releases()
        .SelectMany(release => new[] { "GetPlcLiveValuesS7", "GetPlcWebVars", "GetUnifiedRuntimeTags", "GetPlcLiveValuesOpcUa" }
            .SelectMany(tool => new[] { false, true }.Select(top => new object[] { release[0], tool, top })));

    public static IEnumerable<object[]> WebReleases() => NativeFailureDiagnosticsTests.Releases();

    [Theory]
    [InlineData("All pipe instances are busy.")]
    [InlineData("The pipe has been ended.")]
    public void Open_pipe_io_failure_preserves_native_evidence_and_suggests_what_to_check(string message)
    {
        var error = new IOException(message);
        string diagnostic = TiaMcpServer.Runtime.UnifiedOpenPipeChannel.DescribeFailure(error, "FixturePipe");
        Assert.Contains("FixturePipe", diagnostic);
        Assert.Contains("WinCC Unified Runtime is busy", diagnostic);
        Assert.Contains("pipe is in use", diagnostic);
        Assert.Contains(nameof(IOException) + ": " + message, diagnostic);
    }

    [Theory, MemberData(nameof(ReadFailures))]
    public void Runtime_read_failure_carries_the_redacted_native_line(string release, string tool, bool topLevel)
    {
        string previous = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey;
        toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = release;
        try
        {
            using var call = Host.InvocationJournal.Observe("runtime-diagnostic", tool, "host", release, false,
                () => JsonSerializer.Serialize(new { password = "fixture-secret" }));
            const string reason = "Read refused fixture-secret at C:\\private\\project.ap21; password=credential-echo\n   at Private.Stack.Frame()";
            var data = new JsonObject {
                ["error"] = topLevel ? reason : " ",
                ["items"] = new JsonArray(new JsonObject { ["error"] = null }, new JsonObject { ["error"] = reason }),
                ["identity"] = new JsonObject { ["szlError"] = reason },
                ["beforeError"] = reason
            };
            var result = Host.RuntimeToolContract.Map(tool, new Host.ResponseJsonReport { Ok = false, Data = data }, true, false);
            var body = result.StructuredContent!;
            string native = body["error"]!["details"]!["nativeMessage"]!.GetValue<string>();
            Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
            Assert.Equal("read-failed", (string?)body["meta"]?["outcome"]);
            Assert.Equal(release, (string?)body["meta"]?["releaseKey"]);
            Assert.StartsWith("Read refused", native);
            Assert.Equal(native, (string?)body["data"]?["items"]?[1]?["error"]);
            Assert.Equal(native, (string?)body["data"]?["identity"]?["szlError"]);
            Assert.Equal(native, (string?)body["data"]?["beforeError"]);
            if (topLevel) Assert.Equal(native, (string?)body["data"]?["error"]);
            else Assert.Equal("The native observation failed; consult the local diagnostic log.", (string?)body["data"]?["error"]);
            string wire = body.ToJsonString() + string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));
            foreach (string secret in new[] { "fixture-secret", "credential-echo", "private", "project.ap21", "Private.Stack" })
                Assert.DoesNotContain(secret, wire);
        }
        finally { toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = previous; }
    }

    [Fact]
    public void Runtime_success_preserves_live_values_and_tokens_while_redacting_diagnostics()
    {
        const string token = "preview-1012";
        var structuredValue = new JsonObject { ["message"] = "100", ["error"] = "10" };
        using var call = Host.InvocationJournal.Observe("runtime-values", "WritePlcWebVars", "host", "21", false,
            () => JsonSerializer.Serialize(new { password = "10", previewToken = token, writes = new[] { new { name = "MotorKey", value = "12" } } }));
        var report = new Host.ResponseJsonReport { Ok = true, Message = "Preview using 10", Data = new JsonObject {
            ["items"] = new JsonArray(new JsonObject { ["name"] = "MotorKey", ["before"] = "100", ["requested"] = "12" },
                new JsonObject { ["name"] = "MotorState", ["before"] = structuredValue.DeepClone() }),
            ["previewToken"] = token, ["diagnostic"] = "Device echoed 10", ["nativeMessages"] = new JsonArray("Device echoed 10"),
            ["readError"] = "Previous failure using 10"
        } };
        var result = Host.RuntimeToolContract.Map("WritePlcWebVars", report, true, false);
        var text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(result.Content));
        foreach (var body in new[] { result.StructuredContent!, JsonNode.Parse(text.Text)! })
        {
            Assert.True((bool?)body["ok"]);
            var data = body["data"]!;
            Assert.Equal("100", (string?)data["items"]?[0]?["before"]);
            Assert.Equal("12", (string?)data["items"]?[0]?["requested"]);
            Assert.True(JsonNode.DeepEquals(structuredValue, data["items"]?[1]?["before"]));
            Assert.Equal(token, (string?)data["previewToken"]);
            Assert.Equal("Preview using ••••", (string?)data["summary"]);
            Assert.Equal("Device echoed ••••", (string?)data["diagnostic"]);
            Assert.Equal("Device echoed ••••", (string?)data["nativeMessages"]?[0]);
            Assert.Equal("Previous failure using ••••", (string?)data["readError"]);
        }
    }

    [Fact(Timeout = TestDeadlines.HangMilliseconds)]
    public async Task Web_read_preserves_user_fields_in_a_structured_live_value()
    {
        var value = new JsonObject { ["message"] = "100", ["error"] = "10" };
        await using var fixture = new HttpsFixture { ReadValue = value };
        using var call = Host.InvocationJournal.Observe("runtime-read", "GetPlcWebVars", "host", "21", false,
            () => JsonSerializer.Serialize(new { password = "10" }));
        var tools = new Host.RuntimeChannelTools();
        var read = await Task.Run(() => tools.ReadPlcWebVarsV4(fixture.Host, "fixture", "10", new[] { "MotorKey" }, true));
        var body = read.StructuredContent!;
        Assert.True((bool?)body["ok"], body.ToJsonString());
        Assert.True(JsonNode.DeepEquals(value, body["data"]?["items"]?[0]?["value"]));
        Assert.Equal(new[] { "Api.Login", "PlcProgram.Read" }, fixture.Methods.ToArray());
    }

    [Fact]
    public void Runtime_failure_prefers_the_top_level_reason_and_preserves_write_uncertainty()
    {
        var report = new Host.ResponseJsonReport { Ok = false, Data = new JsonObject {
            ["error"] = "Connection refused.", ["items"] = new JsonArray(new JsonObject { ["error"] = "Item refused." }) } };
        var read = Host.RuntimeToolContract.Map("GetPlcWebVars", report, true, false).StructuredContent!;
        Assert.Equal("Connection refused.", (string?)read["error"]?["details"]?["nativeMessage"]);
        report.Data!["writeAttempted"] = true;
        var write = Host.RuntimeToolContract.Map("WritePlcWebVars", report, false, true).StructuredContent!;
        Assert.Equal("OUTCOME_UNKNOWN", (string?)write["error"]?["code"]);
        Assert.Equal("unknown", (string?)write["meta"]?["outcome"]);
        Assert.Equal("Connection refused.", (string?)write["data"]?["error"]);
        report.Data["writeOutcomeKnown"] = true;
        var known = Host.RuntimeToolContract.Map("WritePlcWebVars", report, false, true).StructuredContent!;
        Assert.Equal("failed", (string?)known["meta"]?["outcome"]);
        Assert.Equal("Connection refused.", (string?)known["error"]?["details"]?["nativeMessage"]);
    }

    [Theory, InlineData(false, false), InlineData(false, true), InlineData(true, false), InlineData(true, true)]
    public void Certificate_validation_requires_a_connection_and_certificate_checks(bool connected, bool ignore)
    {
        var safety = Host.RuntimeChannelTools.S7WebSafety(false, false, connected, ignore);
        Assert.Equal(connected, (bool?)safety["connected"]);
        Assert.Equal(connected ? (bool?)!ignore : null, (bool?)safety["certificateValidated"]);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Certificate_is_not_claimed_validated_without_a_connection(bool ignore)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string host = "127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var tools = new Host.RuntimeChannelTools();
        var reports = await Task.Run(() => new[] {
            tools.ReadPlcWebVars(host, "fixture", "", "[\"Tag\"]", ignore),
            tools.WritePlcWebVars(host, "fixture", "", "{\"Tag\":1}", ignore),
            tools.ReadPlcWebDiagnostics(host, "fixture", "", ignore),
            tools.SetPlcWebOperatingMode(host, "fixture", "", "run", ignore) });
        foreach (var report in reports)
        {
            Assert.False(report.Ok);
            Assert.False((bool?)report.Data?["safety"]?["connected"]);
            Assert.Null(report.Data?["safety"]?["certificateValidated"]);
            Assert.NotNull(report.Data?["error"]);
        }
        var names = new[] { "GetPlcWebVars", "WritePlcWebVars", "GetPlcWebDiagnostics", "SetPlcWebOperatingMode" };
        for (int i = 0; i < reports.Length; i++)
        {
            var mapped = Host.RuntimeToolContract.Map(names[i], reports[i], true, false).StructuredContent!;
            Assert.Equal("PRECONDITION_FAILED", (string?)mapped["error"]?["code"]);
            Assert.Equal(host, (string?)mapped["error"]?["details"]?["target"]);
        }
        Assert.False((bool?)reports[1].Data?["contactedDevice"]);
        Assert.False((bool?)reports[3].Data?["contactedDevice"]);
    }

    [Theory(Timeout = TestDeadlines.HangMilliseconds), MemberData(nameof(WebReleases))]
    public async Task Web_previews_disclose_device_contact_and_only_issue_reads(string release)
    {
        using var scope = new ReleaseScope(release);
        await using var fixture = new HttpsFixture();
        var tools = new Host.RuntimeChannelTools();
        var write = await Task.Run(() => tools.WritePlcWebVars(fixture.Host, "fixture", "", "{\"Tag\":1}", true));
        var mode = await Task.Run(() => tools.SetPlcWebOperatingMode(fixture.Host, "fixture", "", "run", true));
        foreach (var (tool, report) in new[] { ("WritePlcWebVars", write), ("SetPlcWebOperatingMode", mode) })
        {
            Assert.True(report.Ok, report.Message);
            Assert.True((bool?)report.Data?["contactedDevice"]);
            Assert.True((bool?)report.Data?["safety"]?["connected"]);
            Assert.False((bool?)report.Data?["safety"]?["certificateValidated"]);
            Assert.True((bool?)report.Meta?["dryRun"]);
            Assert.False((bool?)report.Meta?["mayHaveChanged"]);
            var mapped = Host.RuntimeToolContract.Map(tool, report, true, true).StructuredContent!;
            Assert.True((bool?)mapped["data"]?["contactedDevice"]);
            Assert.Equal(release, (string?)mapped["meta"]?["releaseKey"]);
        }
        Assert.Equal(7L, (long?)write.Data?["items"]?[0]?["before"]);
        Assert.Equal("Run", (string?)mode.Data?["before"]);
        Assert.True((bool?)mode.Data?["reusedSession"]);
        Assert.Equal(new[] { "Api.Login", "PlcProgram.Read", "Plc.ReadOperatingMode" }, fixture.Methods.ToArray());
    }

    [Fact(Timeout = TestDeadlines.HangMilliseconds)]
    public async Task Https_login_refusal_still_establishes_tls_but_a_rejected_certificate_does_not()
    {
        await using var fixture = new HttpsFixture { RefuseLogin = true };
        var tools = new Host.RuntimeChannelTools();
        var refused = await Task.Run(() => tools.ReadPlcWebVars(fixture.Host, "fixture", "", "[\"Tag\"]", true));
        Assert.False(refused.Ok);
        Assert.True((bool?)refused.Data?["safety"]?["connected"]);
        Assert.False((bool?)refused.Data?["safety"]?["certificateValidated"]);
        fixture.ExpectCertificateRejection = true;
        var rejected = await Task.Run(() => tools.ReadPlcWebVars(fixture.Host, "fixture", "", "[\"Tag\"]", false));
        Assert.False(rejected.Ok);
        Assert.False((bool?)rejected.Data?["safety"]?["connected"]);
        Assert.Null(rejected.Data?["safety"]?["certificateValidated"]);
        Assert.Equal(new[] { "Api.Login" }, fixture.Methods.ToArray());
        await fixture.ServerCompletion.WaitAsync(TestDeadlines.Hang);
        Assert.True(fixture.CertificateRejection is AuthenticationException or IOException, fixture.CertificateRejection?.ToString());
    }

    [Theory(Timeout = TestDeadlines.HangMilliseconds), MemberData(nameof(WebReleases))]
    public async Task Web_reads_record_tls_per_call_including_a_disconnected_cached_session(string release)
    {
        using var scope = new ReleaseScope(release);
        await using var fixture = new HttpsFixture();
        var tools = new Host.RuntimeChannelTools();
        var read = await Task.Run(() => tools.ReadPlcWebVars(fixture.Host, "fixture", "", "[\"Tag\"]", true));
        var diagnostics = await Task.Run(() => tools.ReadPlcWebDiagnostics(fixture.Host, "fixture", "", true));
        foreach (var report in new[] { read, diagnostics })
        {
            Assert.True(report.Ok, report.Message);
            Assert.True((bool?)report.Data?["safety"]?["connected"]);
            Assert.False((bool?)report.Data?["safety"]?["certificateValidated"]);
        }
        Assert.Equal(7L, (long?)read.Data?["items"]?[0]?["value"]);
        Assert.Equal("Run", (string?)diagnostics.Data?["operatingMode"]);
        fixture.Stop();
        var disconnected = await Task.Run(() => tools.ReadPlcWebVars(fixture.Host, "fixture", "", "[\"Tag\"]", true));
        Assert.False(disconnected.Ok);
        Assert.True((bool?)disconnected.Data?["reusedSession"]);
        Assert.False((bool?)disconnected.Data?["safety"]?["connected"]);
        Assert.Null(disconnected.Data?["safety"]?["certificateValidated"]);
        var mapped = Host.RuntimeToolContract.Map("GetPlcWebVars", disconnected, true, false).StructuredContent!;
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)mapped["error"]?["code"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)mapped["error"]?["details"]?["nativeMessage"]));
        Assert.Equal(release, (string?)mapped["meta"]?["releaseKey"]);
    }

    private sealed class ReleaseScope : IDisposable
    {
        private readonly string previous = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey;
        internal ReleaseScope(string release) => toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = release;
        public void Dispose() => toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = previous;
    }

    private sealed class HttpsFixture : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new(TestDeadlines.Hang);
        private readonly X509Certificate2 certificate;
        private readonly Task serve;
        internal readonly ConcurrentQueue<string> Methods = new();
        internal bool RefuseLogin;
        internal bool ExpectCertificateRejection;
        internal JsonNode ReadValue { get; init; } = JsonValue.Create(7)!;
        internal Exception? CertificateRejection { get; private set; }
        internal Task ServerCompletion => serve;
        internal string Host { get; }

        internal HttpsFixture()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=runtime-fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var selfSignedCertificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            certificate = X509CertificateLoader.LoadPkcs12(selfSignedCertificate.Export(X509ContentType.Pfx), null);
            listener.Start();
            Host = "127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            serve = Serve();
        }

        private async Task Serve()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    using var tls = new SslStream(client.GetStream());
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, stop.Token);
                    var header = new List<byte>();
                    var one = new byte[1];
                    while (!Encoding.ASCII.GetString(header.ToArray()).EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        await tls.ReadExactlyAsync(one, stop.Token);
                        header.Add(one[0]);
                    }
                    string lengthLine = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n")
                        .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    var body = new byte[int.Parse(lengthLine.Split(':')[1])];
                    await tls.ReadExactlyAsync(body, stop.Token);
                    var rpc = JsonNode.Parse(body)!;
                    JsonNode Reply(JsonNode item)
                    {
                        string method = item["method"]!.GetValue<string>();
                        Methods.Enqueue(method);
                        var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = item["id"]!.DeepClone() };
                        if (RefuseLogin || method is not ("Api.Login" or "PlcProgram.Read" or "Plc.ReadOperatingMode"))
                            reply["error"] = new JsonObject { ["code"] = 2, ["message"] = "Fixture API refusal." };
                        else reply["result"] = method switch {
                            "Api.Login" => new JsonObject { ["token"] = "fixture-session-token" },
                            "PlcProgram.Read" => ReadValue.DeepClone(),
                            "Plc.ReadOperatingMode" => JsonValue.Create("run"),
                            _ => throw new InvalidOperationException("Unexpected fixture method: " + method)
                        };
                        return reply;
                    }
                    var response = rpc is JsonArray batch ? new JsonArray(batch.Select(item => Reply(item!)).ToArray()) : Reply(rpc);
                    byte[] payload = Encoding.UTF8.GetBytes(response.ToJsonString());
                    byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
                        + payload.Length + "\r\nConnection: close\r\n\r\n");
                    await tls.WriteAsync(headers, stop.Token);
                    await tls.WriteAsync(payload, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error) when (ExpectCertificateRejection && (error is AuthenticationException or IOException))
            { CertificateRejection = error; }
        }

        internal void Stop()
        {
            stop.Cancel();
            listener.Stop();
        }

        public async ValueTask DisposeAsync()
        {
            Stop();
            try { await serve.WaitAsync(TestDeadlines.Hang); }
            finally { certificate.Dispose(); stop.Dispose(); }
        }
    }
}
