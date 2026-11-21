extern alias toolpipeline;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer;
using Xunit;

public sealed class UpdateFallbackTests
{
    [Fact]
    public async Task Prerelease_install_rate_limited_reports_latest_unknown()
    {
        using var handler = new RecordingHandler(false);
        using var http = new HttpClient(handler);
        var result = await Host.CheckForUpdate("asckye/TIA_Portal_Openness_MCP", 15, "4.0.0-beta.3", http);
        Assert.False((bool?)result.Meta!["success"]);
        Assert.Null(result.Meta["latestVersion"]);
        Assert.NotNull(result.Meta["apiProblem"]);
        Assert.Contains("latest release is unknown", result.Message);
        Assert.Contains("pre-release install needs the GitHub releases list", result.Message);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(handler.Requests, url => url.EndsWith("/releases/latest", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plain_install_rate_limited_still_uses_the_release_page()
    {
        using var handler = new RecordingHandler(true);
        using var http = new HttpClient(handler);
        var result = await Host.CheckForUpdate("asckye/TIA_Portal_Openness_MCP", 15, "3.3.0", http);
        Assert.True((bool?)result.Meta!["success"]);
        Assert.Equal("3.3.0", (string?)result.Meta["latestVersion"]);
        Assert.Contains(handler.Requests, url => url.EndsWith("/releases/latest", StringComparison.Ordinal));
        Assert.Contains("512.0 MB", result.Items!.First());
    }

    [Fact]
    public async Task Release_page_without_a_size_prints_no_zero_megabytes()
    {
        using var handler = new RecordingHandler(false);
        using var http = new HttpClient(handler);
        var result = await Host.CheckForUpdate("asckye/TIA_Portal_Openness_MCP", 15, "3.3.0", http);
        Assert.True((bool?)result.Meta!["success"]);
        Assert.DoesNotContain(" MB", result.Items!.First());
    }

    [Theory]
    [InlineData("v4.0.0-rc.1", "TIA_MCP_Delivery_v4.0.0-rc.1_20261103")]
    [InlineData("v4.0.0", "TIA_MCP_v4.0.0_20261103")]
    public async Task UpdateCheckReportsBothDeliveryAssets(string tag, string package)
    {
        var names = new[] { "TIA_MCP_Debug_v4.0.0_20261103.zip", "TIA_MCP_Debug_v4.0.0_20261103.sha256", package + ".zip", package + ".sha256" };
        var release = new { tag_name = tag, assets = names.Select(name => new { name, browser_download_url = "https://fixture.invalid/" + name }) };
        using var handler = new RecordingHandler(false, JsonSerializer.Serialize(new[] { release }));
        using var http = new HttpClient(handler);
        var result = await Host.CheckForUpdate("owner/repo", 15, "4.0.0-beta.6", http);
        Assert.True((bool?)result.Meta!["success"]);
        Assert.Equal(package + ".zip", (string?)result.Meta["zip"]!["name"]);
        Assert.Equal(package + ".sha256", (string?)result.Meta["sha256"]!["name"]);
        Assert.Single(handler.Requests);
    }

    private sealed class RecordingHandler(bool size, string? releases = null) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (releases != null)
            {
                Assert.EndsWith("/releases?per_page=100", url, StringComparison.Ordinal);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releases) });
            }
            if (request.RequestUri.Host == "api.github.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request,
                    Content = new StringContent("{\"message\":\"API rate limit exceeded for 203.0.113.5.\","
                        + "\"documentation_url\":\"https://docs.github.com/rest/overview/resources-in-the-rest-api#rate-limiting\"}") });
            if (url.EndsWith("/releases/latest", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                        "https://github.com/asckye/TIA_Portal_Openness_MCP/releases/tag/v3.3.0"), Content = new StringContent("") });
            Assert.Contains("expanded_assets/v3.3.0", url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request,
                Content = new StringContent("<a href=\"/asckye/TIA_Portal_Openness_MCP/releases/download/v3.3.0/"
                    + "TIA_MCP_Delivery_v3.3.0_20260101.zip\">ZIP</a>" + (size ? "<span>512 MB</span>" : "")) });
        }
    }
}
