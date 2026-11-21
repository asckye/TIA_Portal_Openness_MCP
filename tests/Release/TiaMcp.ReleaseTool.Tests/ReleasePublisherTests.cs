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

    [Fact]
    public async Task StalledUploadRetriesFromTheBeginningAndVerifiesTheAsset()
    {
        using var tree = new StaticTree();
        var path = tree.Write("asset.zip", new string('z', 8 * 1024 * 1024));
        var digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        var asset = JsonSerializer.Serialize(new { id = 1, state = "uploaded", size = new FileInfo(path).Length, digest = "sha256:" + digest });
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var uri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        async Task<TcpClient> Accept(string method)
        {
            var connection = await listener.AcceptTcpClientAsync(token);
            var stream = connection.GetStream();
            using var headers = new MemoryStream();
            var one = new byte[1];
            while (!headers.GetBuffer().AsSpan(0, (int)headers.Length).EndsWith("\r\n\r\n"u8))
            {
                await stream.ReadExactlyAsync(one, token);
                headers.WriteByte(one[0]);
            }
            Assert.StartsWith(method + " ", Encoding.ASCII.GetString(headers.ToArray()));
            return connection;
        }
        async Task Reply(TcpClient connection, string json)
        {
            var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: {json.Length}\r\n\r\n" + json);
            await connection.GetStream().WriteAsync(bytes, token);
        }
        async Task Serve()
        {
            using (var list = await Accept("GET")) await Reply(list, "[]");
            using var stalled = await Accept("POST");
            using (var list = await Accept("GET")) await Reply(list, "[]");
            using (var upload = await Accept("POST"))
            {
                var body = new byte[new FileInfo(path).Length];
                await upload.GetStream().ReadExactlyAsync(body, token);
                Assert.Equal(digest, Convert.ToHexStringLower(SHA256.HashData(body)));
                await Reply(upload, asset);
            }
            using var check = await Accept("GET");
            await Reply(check, asset);
        }
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var api = new ReleasePublisher.GitHubApi(client, uri, "repos/test/loopback", "fixture");
        var options = new PublishOptions("4.0.0-rc.1", "fixture", "test/loopback", uri.ToString(), false, false, RetryCount: 2, RetryDelaySeconds: 0);
        await Task.WhenAll(Serve(), ReleasePublisher.UploadAndVerifyAsync(api, "assets", new Uri(uri, "upload").ToString(),
            path, digest, "application/zip", options, TimeSpan.FromSeconds(2), token));
    }

    [Fact]
    public void ComposeBodyPreservesNotesLinksAndPackageFacts()
    {
        var body = ReleasePublisher
            .ComposeBody("## 中文\n说明。\n\n## English\nNotes [guide](../guides/guide.md)", "https://example.invalid", "owner/repo", "commit", "package.zip", 12,
                34, "digest");
        Assert.Contains("https://example.invalid/owner/repo/blob/commit/docs/releases/../guides/guide.md", body);
        Assert.Contains("交付包：`package.zip`（12 个文件，34 字节）。", body[..body.IndexOf("## English", StringComparison.Ordinal)]);
        Assert.Contains("Package: `package.zip` (12 files, 34 bytes).", body);
        Assert.Contains("Source commit: [commit](https://example.invalid/owner/repo/commit/commit)", body);
        Assert.Contains("```text\ndigest\n```", body);
    }
}
