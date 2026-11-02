extern alias foundationhost;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Boundary = foundationhost::TiaMcp.FoundationHost.HttpRequestBoundary;
using Options = foundationhost::TiaMcp.FoundationHost.HostOptions;

public sealed class HttpRequestBoundaryTests(ITestOutputHelper output)
{
    private static Options Options(string prefix = "http://127.0.0.1:8765/") => new() { BundleRoot = ".", ReleaseKey = "21", WorkerExe = "unused", ApiDirectory = "", HttpPrefix = prefix, ApiKey = "fixture",
        HttpAllowedOrigins = ["https://trusted.example.test:8443"] };

    [Theory]
    [InlineData("127.0.0.1:8765", null, null)]
    [InlineData("localhost:8765", "http://localhost:8765", null)]
    [InlineData("[::1]:8765", "http://[::1]:8765", null)]
    [InlineData("trusted.example.test:8443", "https://trusted.example.test:8443", null)]
    [InlineData("evil.test:8765", null, "FORBIDDEN_HOST")]
    [InlineData("127.0.0.1:8765", "https://evil.test", "FORBIDDEN_ORIGIN")]
    [InlineData("127.0.0.1:8765", "null", "FORBIDDEN_ORIGIN")]
    [InlineData("127.0.0.1:8765", "http://127.0.0.1:8766", "FORBIDDEN_ORIGIN")]
    [InlineData("127.0.0.1:8765", "http://user:secret@127.0.0.1:8765", "FORBIDDEN_ORIGIN")]
    [InlineData("127.0.0.1:8765", "http://127.0.0.1:8765/path", "FORBIDDEN_ORIGIN")]
    [InlineData("127.0.0.1:8765", "http://127.0.0.1:8765, http://evil.test", "FORBIDDEN_ORIGIN")]
    public void Checks_host_and_origin_authorities(string host, string? origin, string? expected)
    {
        var request = new DefaultHttpContext().Request; request.Host = HostString.FromUriComponent(host);
        if (origin != null) request.Headers.Origin = origin;
        Assert.Equal(expected, new Boundary(Options()).Rejection(request));
    }

    [Fact]
    public async Task Forged_headers_are_forbidden_and_audited_even_on_health_and_unknown_routes()
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        await using var app = builder.Build();
        var audit = new ConcurrentBag<string>(); var boundary = new Boundary(Options("http://127.0.0.1:0/"), audit.Add);
        app.Use(boundary.Invoke); app.Run(context => { context.Response.StatusCode = 401; return Task.CompletedTask; });
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        foreach (string route in new[] { "/mcp", "/mcp/health", "/unknown" })
        {
            using var forgedOrigin = new HttpRequestMessage(HttpMethod.Get, app.Urls.Single() + route);
            forgedOrigin.Headers.Add("Origin", "http://evil.test");
            using var originResponse = await client.SendAsync(forgedOrigin); Assert.Equal(HttpStatusCode.Forbidden, originResponse.StatusCode);
            using var forgedHost = new HttpRequestMessage(HttpMethod.Get, app.Urls.Single() + route); forgedHost.Headers.Host = "evil.test";
            using var hostResponse = await client.SendAsync(forgedHost); Assert.Equal(HttpStatusCode.Forbidden, hostResponse.StatusCode);
            using var validResponse = await client.GetAsync(app.Urls.Single() + route); Assert.Equal(HttpStatusCode.Unauthorized, validResponse.StatusCode);
        }
        Assert.Equal(3, audit.Count(value => value == "FORBIDDEN_HOST")); Assert.Equal(3, audit.Count(value => value == "FORBIDDEN_ORIGIN"));
    }

    [Fact]
    public void Report_per_request_boundary_timing_against_previous_authentication_path()
    {
        var request = new DefaultHttpContext().Request; request.Host = new HostString("127.0.0.1", 8765);
        var options = Options(); var boundary = new Boundary(options);
        const int count = 100000;
        byte[] expected = System.Text.Encoding.UTF8.GetBytes("Bearer fixture"); request.Headers.Authorization = "Bearer fixture";
        bool Authenticate() => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(request.Headers.Authorization.ToString()), System.Text.Encoding.UTF8.GetBytes("Bearer " + options.ApiKey));
        for (int i = 0; i < 1000; i++) { Authenticate(); boundary.Rejection(request); }
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < count; i++) Authenticate();
        double before = timer.Elapsed.TotalNanoseconds / count; timer.Restart();
        for (int i = 0; i < count; i++) { boundary.Rejection(request); System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(request.Headers.Authorization.ToString()), expected); }
        double after = timer.Elapsed.TotalNanoseconds / count;
        output.WriteLine($"HTTP hot path, {count} iterations: before={before:F1} ns/request; after={after:F1} ns/request (no timing assertion; machine load is uncontrolled).");
        Assert.Null(boundary.Rejection(request)); Assert.True(Authenticate());
    }
}
