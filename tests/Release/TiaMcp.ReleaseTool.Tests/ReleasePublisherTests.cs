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
    public void ComposeBodyPreservesNotesLinksAndPackageFacts()
    {
        var body = ReleasePublisher.ComposeBody("Notes [guide](../guides/guide.md)", "https://example.invalid", "owner/repo", "commit", "package.zip", 12, 34, "digest");
        Assert.Contains("https://example.invalid/owner/repo/blob/commit/docs/releases/../guides/guide.md", body);
        Assert.Contains("Package: `package.zip` (12 files, 34 bytes).", body);
        Assert.Contains("Source commit: [commit](https://example.invalid/owner/repo/commit/commit)", body);
        Assert.Contains("```text\ndigest\n```", body);
    }
}
