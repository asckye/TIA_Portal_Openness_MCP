extern alias foundationhost;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Relay = foundationhost::TiaMcp.FoundationHost.StdioHttpRelay;
using Policy = foundationhost::TiaOpenness.Shared.RemoteProxyPolicy;
using Keys = foundationhost::TiaOpenness.Shared.HttpAccessKeyStore;

public sealed class HttpRelayTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("127.0.0.1", true)][InlineData("127.22.3.4", true)][InlineData("10.0.0.1", true)]
    [InlineData("172.16.0.1", true)][InlineData("172.31.255.254", true)][InlineData("172.15.1.1", false)]
    [InlineData("172.32.1.1", false)][InlineData("192.168.1.1", true)][InlineData("169.254.3.4", true)]
    [InlineData("192.0.2.1", false)][InlineData("169.253.3.4", false)][InlineData("::1", true)]
    [InlineData("fc00::1", true)][InlineData("fdff::1", true)][InlineData("fe80::1", true)]
    [InlineData("febf::1", true)][InlineData("fec0::1", false)][InlineData("2001:db8::1", false)]
    [InlineData("::ffff:192.168.1.1", true)][InlineData("::ffff:192.0.2.1", false)]
    public void Address_ranges_have_exact_boundaries(string address, bool expected)
        => Assert.Equal(expected, Policy.PrivateAddress(IPAddress.Parse(address)));

    [Fact]
    public void Private_hosts_bypass_even_a_capturing_proxy_and_public_hosts_follow_environment()
    {
        var env = new Dictionary<string, string> { ["HTTP_PROXY"] = "http://127.0.0.1:7897", ["HTTPS_PROXY"] = "http://127.0.0.1:7898", ["NO_PROXY"] = "localhost,127.0.0.1,::1,.local,.example.test,public.test:8443" };
        var policy = new Policy(name => env.GetValueOrDefault(name), new WebProxy("http://127.0.0.1:7899"),
            name => name == "vm.test" ? [IPAddress.Parse("192.168.1.1")] : [IPAddress.Parse("192.0.2.1")]);
        foreach (string host in new[] { "127.0.0.1", "192.168.1.1", "vm.test", "localhost", "[fd00::1]", "[fe80::1]" })
        {
            var uri = new Uri("http://" + host + ":8765/mcp");
            Assert.True(policy.IsBypassed(uri)); Assert.Equal(uri, policy.GetProxy(uri));
        }
        Assert.Equal(new Uri(env["HTTP_PROXY"]), policy.GetProxy(new Uri("http://public.test/mcp")));
        Assert.Equal(new Uri(env["HTTPS_PROXY"]), policy.GetProxy(new Uri("https://public.test/mcp")));
        foreach (string url in new[] { "http://x.local/mcp", "http://sub.example.test/mcp", "http://example.test/mcp", "https://public.test:8443/mcp" })
            Assert.True(policy.IsBypassed(new Uri(url)));
        Assert.False(policy.IsBypassed(new Uri("http://badexample.test/mcp")));
        env["NO_PROXY"] = "*"; Assert.True(policy.IsBypassed(new Uri("http://public.test/mcp")));
        env.Clear(); Assert.Equal(new Uri("http://127.0.0.1:7899"), policy.GetProxy(new Uri("http://public.test/mcp")));
    }

    [Fact]
    public void Proxy_credentials_are_resolved_without_exposing_them_in_diagnostics()
    {
        var policy = new Policy(name => name == "HTTPS_PROXY" ? "http://user:synthetic%2Dpassword@127.0.0.1:7897" : null,
            new WebProxy(), _ => [IPAddress.Parse("192.0.2.1")]);
        var credentials = policy.Credentials!.GetCredential(new Uri("http://127.0.0.1:7897"), "Basic");
        Assert.NotNull(credentials); Assert.Equal("user", credentials.UserName); Assert.Equal("synthetic-password", credentials.Password);
    }

    [Fact]
    public async Task Relay_preserves_sessions_notifications_cancellation_errors_and_large_paged_results()
    {
        await using var fake = await FakeEndpoint.Start();
        var input = new Input(); var output = new Output();
        using var client = Relay.CreateClient();
        var run = Relay.Run(fake.Endpoint, FakeEndpoint.Key, input, output, client);
        await input.Send(Initialize);
        Assert.Equal("2025-03-26", (string?)(await output.Read())["result"]?["protocolVersion"]);
        await input.Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        await input.Send("{\"jsonrpc\":\"2.0\",\"id\":\"large\",\"method\":\"tools/call\",\"params\":{\"name\":\"large\",\"arguments\":{\"cursor\":\"page-2\"}}}");
        var progress = await output.Read(); Assert.Equal("notifications/progress", (string?)progress["method"]);
        var large = await output.Read();
        Assert.Equal("large", (string?)large["id"]); Assert.Equal(new string('x', 2 * 1024 * 1024), (string?)large["result"]?["content"]?[0]?["text"]);
        Assert.Equal("page-3", (string?)large["result"]?["nextCursor"]);
        Assert.Contains(fake.Messages, m => (string?)m["params"]?["arguments"]?["cursor"] == "page-2");
        await input.Send("{\"jsonrpc\":\"2.0\",\"id\":\"client-request\",\"method\":\"tools/call\",\"params\":{\"name\":\"server-request\"}}");
        var serverRequest = await output.Read(); Assert.Equal("sampling/createMessage", (string?)serverRequest["method"]);
        await input.Send("{\"jsonrpc\":\"2.0\",\"id\":\"server-request\",\"result\":{\"role\":\"assistant\",\"content\":{\"type\":\"text\",\"text\":\"fixture reply\"},\"model\":\"fixture\"}}");
        Assert.Equal("client-request", (string?)(await output.Read())["id"]);
        await input.Send("{\"jsonrpc\":\"2.0\",\"id\":\"pending\",\"method\":\"tools/call\",\"params\":{\"name\":\"pending\"}}");
        await fake.Pending.Task;
        await input.Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":\"pending\",\"reason\":\"client cancelled\"}}");
        var cancelled = await output.Read(); Assert.Equal(-32800, (int?)cancelled["error"]?["code"]);
        Assert.Equal("client cancelled", (string?)cancelled["error"]?["data"]?["reason"]);
        input.Close(); Assert.Equal(0, await run);
        Assert.Contains(fake.Messages, m => (string?)m["method"] == "notifications/initialized");
        Assert.Contains(fake.Messages, m => (string?)m["method"] == "notifications/cancelled");
        Assert.All(fake.SessionHeaders, value => Assert.Equal("relay-fixture-session", value));
    }

    [Fact]
    public async Task Relay_receives_standalone_server_notifications_from_the_session_GET_stream()
    {
        await using var fake = await FakeEndpoint.Start(true);
        var input = new Input(); var output = new Output();
        var run = Relay.Run(fake.Endpoint, FakeEndpoint.Key, input, output);
        await input.Send(Initialize); Assert.Equal(1, (int?)(await output.Read())["id"]);
        await input.Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        await fake.GetConnected.Task;
        await fake.Notifications.Writer.WriteAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/resources/list_changed\"}");
        Assert.Equal("notifications/resources/list_changed", (string?)(await output.Read())["method"]);
        input.Close(); Assert.Equal(0, await run);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Bundled_relay_command_reads_DPAPI_key_and_never_prints_it(bool rejectedKey)
    {
        await using var fake = await FakeEndpoint.Start();
        string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        string data = Path.Combine(root, "bin-build", "relay-tests", Guid.NewGuid().ToString("N"));
        string key = rejectedKey ? "wrong-synthetic-relay-key" : FakeEndpoint.Key;
        Keys.Save(Path.Combine(data, "config"), fake.Endpoint, key);
        Assert.DoesNotContain(key, File.ReadAllText(Keys.PathFor(Path.Combine(data, "config"), fake.Endpoint)));
        string executable = Path.Combine(root, "src", "FoundationHost", "bin", "Release", "net10.0", "TiaMcp.FoundationHost.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "--bundle-root", root, "--relay", fake.Endpoint.AbsoluteUri }) start.ArgumentList.Add(argument);
        start.Environment["TIA_MCP_DATA_DIRECTORY"] = data;
        start.Environment["HTTP_PROXY"] = "http://127.0.0.1:1"; start.Environment["HTTPS_PROXY"] = "http://127.0.0.1:1";
        start.Environment["NO_PROXY"] = "";
        Assert.DoesNotContain(key, string.Join(" ", start.ArgumentList));
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteLineAsync(Initialize); await process.StandardInput.FlushAsync();
            string? reply = await process.StandardOutput.ReadLineAsync();
            if (!rejectedKey)
            {
                Assert.True(reply != null, process.HasExited ? await errors : "Relay closed stdout before initialization.");
                Assert.Equal(1, (int?)JsonNode.Parse(reply!)?["id"]);
            }
            process.StandardInput.Close();
            string remaining = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            string diagnostics = await errors;
            Assert.Equal(rejectedKey ? 2 : 0, process.ExitCode); Assert.DoesNotContain(key, reply + remaining + diagnostics);
            if (rejectedKey) { Assert.Contains("RELAY_CONNECTION_FAILED", diagnostics); Assert.Equal(1, fake.Attempts); }
        }
        finally
        {
            if (!process.HasExited)
            {
                string image = process.MainModule!.FileName;
                Assert.Equal(Path.GetFullPath(executable), Path.GetFullPath(image), ignoreCase: true);
                Assert.StartsWith(root + Path.DirectorySeparatorChar, image, StringComparison.OrdinalIgnoreCase);
                process.Kill(); await process.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task Report_direct_HTTP_and_resident_relay_timing_without_load_sensitive_assertions()
    {
        await using var fake = await FakeEndpoint.Start();
        using var direct = Relay.CreateClient();
        direct.DefaultRequestHeaders.Authorization = new("Bearer", FakeEndpoint.Key);
        direct.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        async Task Direct(string message)
        {
            using var body = new StringContent(message, Encoding.UTF8, "application/json");
            using var response = await direct.PostAsync(fake.Endpoint, body); response.EnsureSuccessStatusCode();
            Assert.NotNull(JsonNode.Parse(await response.Content.ReadAsStringAsync()));
        }
        var timer = Stopwatch.StartNew(); await Direct(Initialize); double directSession = timer.Elapsed.TotalMilliseconds;
        direct.DefaultRequestHeaders.Add("Mcp-Session-Id", "relay-fixture-session");
        var input = new Input(); var result = new Output();
        timer.Restart(); var run = Relay.Run(fake.Endpoint, FakeEndpoint.Key, input, result);
        await input.Send(Initialize); await result.Read(); double relaySession = timer.Elapsed.TotalMilliseconds;
        const string ping = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}";
        for (int i = 0; i < 8; i++) { await Direct(ping); await input.Send(ping); await result.Read(); }
        const int count = 32;
        timer.Restart(); for (int i = 0; i < count; i++) await Direct(ping);
        double before = timer.Elapsed.TotalMilliseconds / count;
        timer.Restart(); for (int i = 0; i < count; i++) { await input.Send(ping); await result.Read(); }
        double after = timer.Elapsed.TotalMilliseconds / count;
        output.WriteLine($"Loopback fake HTTP, {count} warmed sequential pings: direct={before:F3} ms/call; relay={after:F3} ms/call. Initialize: direct={directSession:F3} ms; resident relay={relaySession:F3} ms. No timing assertion; CPU load is uncontrolled; process startup and Siemens work are excluded.");
        input.Close(); Assert.Equal(0, await run);
    }

    [Fact]
    public async Task Http_host_reads_its_saved_protected_key_without_a_command_line_key()
    {
        string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        string data = Path.Combine(root, "bin-build", "relay-tests", Guid.NewGuid().ToString("N"));
        string config = Path.Combine(data, "config"); Directory.CreateDirectory(config);
        var endpoint = new Uri("http://127.0.0.1:8765/mcp");
        Keys.Save(config, endpoint, "a-different-client-key");
        File.WriteAllText(Path.Combine(config, "http-v21.json"), System.Text.Json.JsonSerializer.Serialize(new {
            Version = 21, ReleaseKey = "21", Address = "127.0.0.1", Port = 8765, TiaPath = "", ProtectedKey = Keys.Protect(FakeEndpoint.Key)
        }));
        string executable = Path.Combine(root, "src", "FoundationHost", "bin", "Release", "net10.0", "TiaMcp.FoundationHost.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "--bundle-root", root, "--release-key", "21", "--transport", "http", "--http-prefix", "http://127.0.0.1:8765/", "--offline", "--catalog" }) start.ArgumentList.Add(argument);
        start.Environment["TIA_MCP_DATA_DIRECTORY"] = data;
        Assert.DoesNotContain(FakeEndpoint.Key, string.Join(" ", start.ArgumentList));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        string catalog = await output, diagnostics = await error;
        Assert.Equal("21", (string?)JsonNode.Parse(catalog)?["releaseKey"]); Assert.DoesNotContain(FakeEndpoint.Key, catalog + diagnostics);
        Assert.Equal(FakeEndpoint.Key, Keys.ReadHost(config, "21", endpoint));
        Assert.Equal("a-different-client-key", Keys.Read(config, endpoint));
    }

    private const string Initialize = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"relay-fixture\",\"version\":\"1\"}}}";
    private sealed class Input : TextReader
    {
        private readonly Channel<string> lines = Channel.CreateUnbounded<string>();
        internal ValueTask Send(string line) => lines.Writer.WriteAsync(line);
        public override void Close() => lines.Writer.Complete();
        public override async ValueTask<string?> ReadLineAsync(CancellationToken token)
            => await lines.Reader.WaitToReadAsync(token) ? await lines.Reader.ReadAsync(token) : null;
    }
    private sealed class Output : StringWriter
    {
        private readonly Channel<string> lines = Channel.CreateUnbounded<string>();
        public override Task WriteLineAsync(string? value) { lines.Writer.TryWrite(value!); return Task.CompletedTask; }
        internal async Task<JsonNode> Read() => JsonNode.Parse(await lines.Reader.ReadAsync())!;
    }

    private sealed class FakeEndpoint(WebApplication app, Uri endpoint, bool standaloneNotifications) : IAsyncDisposable
    {
        internal const string Key = "relay-synthetic-fixture-key";
        internal Uri Endpoint => endpoint;
        internal readonly ConcurrentBag<JsonNode> Messages = [];
        internal readonly ConcurrentBag<string> SessionHeaders = [];
        internal readonly TaskCompletionSource Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource GetConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Channel<string> Notifications = Channel.CreateUnbounded<string>();
        internal int Attempts;
        private readonly TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource clientReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal static async Task<FakeEndpoint> Start(bool standaloneNotifications = false)
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
            var app = builder.Build(); FakeEndpoint? fake = null;
            app.Map("/mcp", async context => await fake!.Handle(context));
            await app.StartAsync(); fake = new FakeEndpoint(app, new Uri(app.Urls.Single() + "/mcp"), standaloneNotifications); return fake;
        }
        private async Task Handle(HttpContext context)
        {
            Interlocked.Increment(ref Attempts);
            if (context.Request.Headers.Authorization != "Bearer " + Key)
            {
                context.Response.StatusCode = 401; await context.Response.WriteAsync("Untrusted reflection: " + context.Request.Headers.Authorization); return;
            }
            if (context.Request.Method == "GET" && standaloneNotifications)
            {
                context.Response.ContentType = "text/event-stream"; await context.Response.StartAsync();
                GetConnected.TrySetResult();
                try
                {
                    await foreach (string notification in Notifications.Reader.ReadAllAsync(context.RequestAborted))
                    {
                        await context.Response.WriteAsync("event: message\ndata: " + notification + "\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                return;
            }
            if (context.Request.Method != "POST") { context.Response.StatusCode = context.Request.Method == "DELETE" ? 200 : 405; return; }
            var message = (await JsonNode.ParseAsync(context.Request.Body))!; Messages.Add(message);
            string? method = (string?)message["method"];
            if (method != "initialize") SessionHeaders.Add(context.Request.Headers["Mcp-Session-Id"].ToString());
            if (method == null) { clientReply.TrySetResult(); context.Response.StatusCode = 202; return; }
            if (message["id"] == null)
            {
                if (method == "notifications/cancelled") cancelled.TrySetResult();
                context.Response.StatusCode = 202; return;
            }
            var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone() };
            if (method == "initialize")
            {
                context.Response.Headers["Mcp-Session-Id"] = "relay-fixture-session";
                reply["result"] = new JsonObject { ["protocolVersion"] = "2025-03-26", ["capabilities"] = new JsonObject(), ["serverInfo"] = new JsonObject { ["name"] = "relay-fake", ["version"] = "1" } };
                context.Response.ContentType = "application/json"; await context.Response.WriteAsync(reply.ToJsonString()); return;
            }
            if (method == "ping")
            {
                reply["result"] = new JsonObject(); context.Response.ContentType = "application/json"; await context.Response.WriteAsync(reply.ToJsonString()); return;
            }
            context.Response.ContentType = "text/event-stream";
            if ((string?)message["params"]?["name"] == "pending")
            {
                Pending.TrySetResult(); await cancelled.Task;
                reply["error"] = new JsonObject { ["code"] = -32800, ["message"] = "Cancelled", ["data"] = new JsonObject { ["reason"] = "client cancelled" } };
            }
            else if ((string?)message["params"]?["name"] == "server-request")
            {
                await context.Response.WriteAsync("event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":\"server-request\",\"method\":\"sampling/createMessage\",\"params\":{\"messages\":[],\"maxTokens\":1}}\n\n");
                await context.Response.Body.FlushAsync();
                await clientReply.Task;
                reply["result"] = new JsonObject { ["content"] = new JsonArray() };
            }
            else
            {
                await context.Response.WriteAsync("event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{\"progressToken\":\"large\",\"progress\":1}}\n\n");
                reply["result"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = new string('x', 2 * 1024 * 1024) }), ["nextCursor"] = "page-3" };
            }
            await context.Response.WriteAsync("event: message\ndata: " + reply.ToJsonString() + "\n\n");
        }
        public async ValueTask DisposeAsync() { Notifications.Writer.TryComplete(); cancelled.TrySetResult(); clientReply.TrySetResult(); await app.DisposeAsync(); }
    }
}
