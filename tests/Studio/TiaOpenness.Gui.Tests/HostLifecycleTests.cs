using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TiaOpenness.Gui.Services;
using Xunit;

namespace TiaOpenness.Gui.Tests;

public sealed class HostLifecycleTests
{
    [Theory]
    [InlineData(null, true)] [InlineData("4.0.0-beta.1", true)]
    [InlineData("4.0.0-beta.2", false)] [InlineData("4.0.0", false)]
    public void Older_and_legacy_hosts_are_flagged(string? version, bool expected)
        => Assert.Equal(expected, HostLifecycleService.IsOlder(version, "v4.0.0-beta.2"));

    [Fact]
    public void Process_identity_must_match_before_any_stop()
    {
        using var process = Process.GetCurrentProcess();
        var host = new VisibleHost(process.Id, process.StartTime.ToUniversalTime().Ticks + 1, "21", process.MainModule!.FileName,
            "unused", "http", null, null, false, true);
        Assert.Throws<UnauthorizedAccessException>(() => HostLifecycleService.VerifiedProcess(host));
        host = host with { StartTicks = process.StartTime.ToUniversalTime().Ticks, ImagePath = "C:/wrong-host.exe" };
        Assert.Throws<UnauthorizedAccessException>(() => HostLifecycleService.VerifiedProcess(host));
    }

    [Fact]
    public async Task Real_local_http_host_without_sessions_can_be_restarted_and_stopped_after_identity_verification()
    {
        string root = McpOperatorFixture.RepositoryRoot;
        string data = Path.Combine(root, "bin-build", "des1-host-" + Guid.NewGuid().ToString("N"));
        string image = Path.Combine(AppContext.BaseDirectory, "host-lifecycle", "TiaMcp.FoundationHost.exe");
        Assert.True(File.Exists(image), "Build FoundationHost before running the Workbench lifecycle test.");
        var start = new ProcessStartInfo(image) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
        // Port zero binds a private loopback listener; no external service is contacted.
        foreach (string argument in new[] { "--bundle-root", root, "--release-key", "21", "--transport", "http",
            "--http-prefix", "http://127.0.0.1:0/", "--http-api-key", "des1-synthetic-local-host", "--offline" }) start.ArgumentList.Add(argument);
        start.Environment["TIA_MCP_DATA_DIRECTORY"] = data;
        using var process = Process.Start(start) ?? throw new IOException("Host fixture did not start.");
        var expected = new VisibleHost(process.Id, process.StartTime.ToUniversalTime().Ticks, "21", image, root,
            "http", "http://127.0.0.1:0/", null, false, true);
        Process? replacement = null;
        VisibleHost? replacementIdentity = null;
        try
        {
            var service = new HostLifecycleService(data, "4.0.0-beta.2");
            VisibleHost? host = null;
            for (int attempt = 0; attempt < 100 && !process.HasExited; attempt++)
            {
                host = service.Discover().SingleOrDefault(candidate => candidate.Pid == process.Id);
                if (host != null) break;
                await Task.Delay(50);
            }
            Assert.NotNull(host);
            Assert.Equal(expected.StartTicks, host.StartTicks);
            Assert.Equal("http", host.Transport);
            Assert.Equal(expected.HttpPrefix, host.HttpPrefix);
            Assert.False(string.IsNullOrEmpty(host.Version));
            string recordPath = Directory.GetFiles(Path.Combine(data, "run", "hosts"), "*.json").Single();
            var legacyRecord = JsonNode.Parse(File.ReadAllText(recordPath))!.AsObject();
            legacyRecord.Remove("hostVersion"); legacyRecord.Remove("httpPrefix");
            File.WriteAllText(recordPath, legacyRecord.ToJsonString());
            VisibleHost? legacy = null;
            for (int attempt = 0; attempt < 100 && !process.HasExited; attempt++)
            {
                legacy = service.Discover().Single(candidate => candidate.Pid == process.Id);
                if (legacy.HttpPrefix != null) break;
                await Task.Delay(50);
            }
            Assert.NotNull(legacy);
            Assert.True(legacy.OlderVersion);
            Assert.True(Uri.TryCreate(legacy.HttpPrefix, UriKind.Absolute, out var endpoint));
            Assert.NotNull(endpoint);
            Assert.Equal("127.0.0.1", endpoint.Host);
            Assert.InRange(endpoint.Port, 1, 65535);
            replacement = await HostLifecycleService.Restart(host, start);
            replacementIdentity = expected with { Pid = replacement.Id, StartTicks = replacement.StartTime.ToUniversalTime().Ticks };
            Assert.True(process.HasExited);
            for (int attempt = 0; attempt < 100 && !replacement.HasExited; attempt++)
            {
                if (service.Discover().Any(candidate => candidate.Pid == replacement.Id)) break;
                await Task.Delay(50);
            }
            Assert.Contains(service.Discover(), candidate => candidate.Pid == replacement.Id);
            await HostLifecycleService.Stop(replacementIdentity);
            Assert.Empty(service.Discover());
        }
        finally
        {
            if (!process.HasExited) await HostLifecycleService.Stop(expected);
            if (replacement != null && !replacement.HasExited && replacementIdentity != null) await HostLifecycleService.Stop(replacementIdentity);
            replacement?.Dispose();
            Assert.StartsWith(Path.Combine(root, "bin-build") + Path.DirectorySeparatorChar, Path.GetFullPath(data), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }
}
