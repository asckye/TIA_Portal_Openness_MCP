using System.Net;
using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class ReleasePublisherTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishUsesOnlyTheLoopbackFakeAndVerifiesBothAssets(bool draftOnly)
    {
        using var server = new LoopbackGitHubApi("new");
        using var fixture = new PackageFixture();
        var options = Options(server, draftOnly, deleteDraft: false);

        var result = await ReleasePublisher.PublishAsync(fixture.Root, options);

        Assert.Equal(0, result);
        Assert.Equal(2, server.UploadedAssets.Count);
        Assert.Contains("pkg.zip", server.UploadedAssets.Select(asset => asset.Name));
        Assert.Contains("pkg.sha256", server.UploadedAssets.Select(asset => asset.Name));
        Assert.Equal(draftOnly ? 0 : 1, server.Requests.Count(request => request.Method == "PATCH"));
        if (!draftOnly) Assert.True(File.Exists(Path.Combine(fixture.Output, "published-release.json")));
    }

    [Fact]
    public async Task DeleteDraftRemovesOnlyTheLoopbackDraft()
    {
        using var server = new LoopbackGitHubApi("draft");
        using var fixture = new PackageFixture();

        var result = await ReleasePublisher.PublishAsync(fixture.Root, Options(server, draftOnly: false, deleteDraft: true));

        Assert.Equal(0, result);
        Assert.Contains(server.Requests, request => request.Method == "DELETE" && request.Path.EndsWith("/releases/42", StringComparison.Ordinal));
        Assert.Empty(server.UploadedAssets);
    }

    [Fact]
    public async Task ReusedDraftReplacesAStaleAssetBeforeUploading()
    {
        using var server = new LoopbackGitHubApi("stale-draft");
        using var fixture = new PackageFixture();

        var result = await ReleasePublisher.PublishAsync(fixture.Root, Options(server, draftOnly: true, deleteDraft: false));

        Assert.Equal(0, result);
        Assert.DoesNotContain(server.Requests, request => request.Method == "POST" && request.Path == "/api/repos/acme/project/releases");
        Assert.Contains(server.Requests, request => request.Method == "DELETE" && request.Path.EndsWith("/releases/assets/99", StringComparison.Ordinal));
        Assert.Equal(2, server.UploadedAssets.Count);
        Assert.All(server.UploadedAssets, asset => Assert.Equal("uploaded", asset.State));
    }

    [Fact]
    public async Task UploadRetriesAfterALoopbackApiFailure()
    {
        using var server = new LoopbackGitHubApi("upload-fail-once");
        using var fixture = new PackageFixture();

        var result = await ReleasePublisher.PublishAsync(fixture.Root, Options(server, draftOnly: false, deleteDraft: false));

        Assert.Equal(0, result);
        Assert.Equal(3, server.Requests.Count(request => request.Method == "POST" && request.Path.StartsWith("/api/upload/42?name=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task PublishedReleaseIsNeverTouched()
    {
        using var server = new LoopbackGitHubApi("published");
        using var fixture = new PackageFixture();

        var error = await Assert.ThrowsAsync<ReleaseException>(() => ReleasePublisher.PublishAsync(fixture.Root, Options(server, draftOnly: false, deleteDraft: false)));

        Assert.Contains("already published", error.Message);
        Assert.Empty(server.UploadedAssets);
        Assert.DoesNotContain(server.Requests, request => request.Method is "POST" or "PATCH" or "DELETE");
    }

    [Theory]
    [InlineData("branch-mismatch", "master HEAD differs from the packaged sourceCommit")]
    [InlineData("tag-mismatch", "does not point at the packaged commit")]
    public async Task SourceBranchAndTagMustMatchThePackageCommit(string mode, string expected)
    {
        using var server = new LoopbackGitHubApi(mode);
        using var fixture = new PackageFixture();

        var error = await Assert.ThrowsAsync<ReleaseException>(() => ReleasePublisher.PublishAsync(fixture.Root, Options(server, draftOnly: false, deleteDraft: false)));

        Assert.Contains(expected, error.Message);
        Assert.DoesNotContain(server.Requests, request => request.Method is "POST" or "PATCH" or "DELETE");
    }

    [Fact]
    public async Task PackageHashIsCheckedBeforeContactingTheApi()
    {
        using var server = new LoopbackGitHubApi("new");
        using var fixture = new PackageFixture();
        File.AppendAllText(Path.Combine(fixture.Output, "pkg.zip"), "changed");

        var error = await Assert.ThrowsAsync<ReleaseException>(() => ReleasePublisher.PublishAsync(fixture.Root, Options(server, draftOnly: false, deleteDraft: false)));

        Assert.Contains("differs from package-result.json", error.Message);
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData("package")]
    [InlineData("quick")]
    public async Task NonFullPackageRefusedBeforeAnyRequest(string tier)
    {
        using var fixture = new PackageFixture();
        var path = Path.Combine(fixture.Output, "package-result.json");
        var result = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        result["tier"] = tier;
        File.WriteAllText(path, result.ToJsonString());
        var options = new PublishOptions("4.0.0", "", "test/repo", "invalid", false, false, GitExecutable: "must-not-run");
        Assert.Contains("tier=full", (await Assert.ThrowsAsync<ReleaseException>(() => ReleasePublisher.PublishAsync(fixture.Root, options))).Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task CachedOrUnknownFullBuildRefusedBeforeCredentialsOrAnyRequest(bool? cacheEnabled)
    {
        using var fixture = new PackageFixture(cacheEnabled);
        var options = new PublishOptions("4.0.0", "", "test/repo", "invalid", false, false, GitExecutable: "must-not-run");
        Assert.Contains("-NoBuildCache", (await Assert.ThrowsAsync<ReleaseException>(() => ReleasePublisher.PublishAsync(fixture.Root, options))).Message);
    }

    private static PublishOptions Options(LoopbackGitHubApi server, bool draftOnly, bool deleteDraft) =>
        new("4.0.0", "test-token", "acme/project", server.ApiBaseUrl, draftOnly, deleteDraft, RetryCount: 2, RetryDelaySeconds: 0);

    private sealed class PackageFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "release-publish-fixture-" + Guid.NewGuid().ToString("N"));
        internal string Output => Path.Combine(Root, "bin-build/releases/v4.0.0");

        internal PackageFixture(bool? buildCacheEnabled = false)
        {
            Directory.CreateDirectory(Output);
            Directory.CreateDirectory(Path.Combine(Root, "manifest"));
            Directory.CreateDirectory(Path.Combine(Root, "docs/releases"));
            var zip = Path.Combine(Output, "pkg.zip");
            Directory.CreateDirectory(Path.Combine(Root, "build-tools/release"));
            File.WriteAllText(Path.Combine(Root, "build-tools/release/release-checks.json"), JsonSerializer.Serialize(new { checks = new[] { "test" }, always = new[] { "test" }, rules = new[] { new { path = "test/", checks = new[] { "test" } } }, selfTests = Array.Empty<object>() }));
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("bundle/manifest/package-manifest.json").Open()))
                writer.Write(JsonSerializer.Serialize(new { tier = "full", checkStatus = "passed", buildCacheEnabled, checksRan = new[] { "test" }, checksSkipped = Array.Empty<string>() }));
            var sidecar = Path.ChangeExtension(zip, ".sha256");
            File.WriteAllText(sidecar, Hash(zip) + "  pkg.zip\n");
            var digest = Hash(zip);
            File.WriteAllText(Path.Combine(Output, "package-result.json"), JsonSerializer.Serialize(new
            {
                tier = "full", checkStatus = "passed", path = zip, sha256 = digest, size = new FileInfo(zip).Length, sourceCommit = new string('a', 40), files = 3
            }));
            File.WriteAllText(Path.Combine(Root, "manifest/delivery.json"), JsonSerializer.Serialize(new { release = "4.0.0", package = "pkg" }));
            File.WriteAllText(Path.Combine(Root, "docs/releases/v4.0.0.md"), "# Test release\n\nSee [guide](../guides/test.md).\n");
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }

        private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private sealed class LoopbackGitHubApi : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task loop;
        private readonly string releaseMode;
        private readonly List<Asset> assets = [];
        private int nextAssetId = 100;
        private int uploadAttempts;

        internal string ApiBaseUrl { get; }
        internal List<(string Method, string Path)> Requests { get; } = [];
        internal IReadOnlyList<Asset> UploadedAssets => assets;

        internal LoopbackGitHubApi(string releaseMode)
        {
            this.releaseMode = releaseMode;
            if (releaseMode == "stale-draft") assets.Add(new Asset(99, "pkg.zip", 1, "sha256:stale", "uploaded"));
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            ApiBaseUrl = $"http://127.0.0.1:{port}/api/";
            loop = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                    await HandleAsync(client, cancellation.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            var stream = client.GetStream();
            var requestBytes = new List<byte>();
            while (true)
            {
                var one = new byte[1];
                if (await stream.ReadAsync(one, cancellationToken) == 0) return;
                requestBytes.Add(one[0]);
                if (requestBytes.Count >= 4)
                {
                    var count = requestBytes.Count;
                    if (requestBytes[count - 4] == 13 && requestBytes[count - 3] == 10 && requestBytes[count - 2] == 13 && requestBytes[count - 1] == 10) break;
                }
            }
            var headerText = Encoding.ASCII.GetString(requestBytes.ToArray());
            var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var first = lines[0].Split(' ');
            var method = first[0];
            var path = first[1];
            var contentLength = lines.Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2 && parts[0].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                .Select(parts => int.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture)).FirstOrDefault();
            var body = new byte[contentLength];
            var read = 0;
            while (read < body.Length)
            {
                var count = await stream.ReadAsync(body.AsMemory(read), cancellationToken);
                if (count == 0) throw new IOException("Loopback HTTP request ended early");
                read += count;
            }
            Requests.Add((method, path));
            var response = Respond(method, path, body);
            var payload = Encoding.UTF8.GetBytes(response.Body);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {response.Status} {response.Text}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, cancellationToken);
            if (payload.Length != 0) await stream.WriteAsync(payload, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        private (int Status, string Text, string Body) Respond(string method, string path, byte[] body)
        {
            if (path == "/api/user") return Json(200, "OK", new { login = "loopback-test" });
            if (path == "/api/repos/acme/project/branches/master") return Json(200, "OK", new { commit = new { sha = new string(releaseMode == "branch-mismatch" ? 'b' : 'a', 40) } });
            if (path == "/api/repos/acme/project/git/ref/tags/v4.0.0") return Json(200, "OK", new { @object = new { type = "commit", sha = new string(releaseMode == "tag-mismatch" ? 'b' : 'a', 40) } });
            if (path == "/api/repos/acme/project/releases/tags/v4.0.0")
            {
                if (releaseMode is "new" or "upload-fail-once") return (404, "Not Found", "{}");
                return Json(200, "OK", new { id = 42, tag_name = "v4.0.0", draft = releaseMode is "draft" or "stale-draft", upload_url = ApiBaseUrl + "upload/42{?name,label}" });
            }
            if (path == "/api/repos/acme/project/releases?per_page=100") return Json(200, "OK", Array.Empty<object>());
            if (method == "POST" && path == "/api/repos/acme/project/releases")
                return Json(201, "Created", new { id = 42, tag_name = "v4.0.0", draft = true, upload_url = ApiBaseUrl + "upload/42{?name,label}" });
            if (method == "DELETE" && path == "/api/repos/acme/project/releases/42") return (204, "No Content", "");
            if (method == "DELETE" && path.StartsWith("/api/repos/acme/project/releases/assets/", StringComparison.Ordinal))
            {
                var id = int.Parse(path.Split('/').Last(), System.Globalization.CultureInfo.InvariantCulture);
                assets.RemoveAll(asset => asset.Id == id);
                return (204, "No Content", "");
            }
            if (method == "PATCH" && path == "/api/repos/acme/project/releases/42") return Json(200, "OK", new { id = 42, draft = false, html_url = "http://127.0.0.1/fake-release" });
            if (path == "/api/repos/acme/project/releases/42/assets?per_page=100") return Json(200, "OK", assets.Select(asset => asset.ToJson()).ToArray());
            if (path.StartsWith("/api/repos/acme/project/releases/assets/", StringComparison.Ordinal))
            {
                var id = int.Parse(path.Split('/').Last(), System.Globalization.CultureInfo.InvariantCulture);
                var asset = assets.Single(item => item.Id == id);
                return Json(200, "OK", asset.ToJson());
            }
            if (method == "POST" && path.StartsWith("/api/upload/42?name=", StringComparison.Ordinal))
            {
                uploadAttempts++;
                if (releaseMode == "upload-fail-once" && uploadAttempts == 1) return Json(502, "Bad Gateway", new { error = "synthetic upload failure" });
                var name = Uri.UnescapeDataString(path[(path.IndexOf("name=", StringComparison.Ordinal) + 5)..]);
                var asset = new Asset(++nextAssetId, name, body.LongLength, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(body)), "uploaded");
                assets.Add(asset);
                return Json(201, "Created", asset.ToJson());
            }
            return Json(404, "Not Found", new { error = "unhandled fake route", method, path });
        }

        private static (int Status, string Text, string Body) Json(int status, string text, object value) =>
            (status, text, JsonSerializer.Serialize(value));

        public void Dispose()
        {
            cancellation.Cancel();
            listener.Stop();
            try { loop.GetAwaiter().GetResult(); } catch { }
            cancellation.Dispose();
        }

        internal sealed record Asset(int Id, string Name, long Size, string Digest, string State = "uploaded")
        {
            internal object ToJson() => new { id = Id, name = Name, state = State, size = Size, digest = Digest };
        }
    }
}
