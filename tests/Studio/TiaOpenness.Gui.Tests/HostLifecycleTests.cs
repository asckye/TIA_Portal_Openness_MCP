using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TiaOpenness.Shared;
using TiaOpenness.Gui.Services;
using Xunit;

namespace TiaOpenness.Gui.Tests;

public sealed class HostLifecycleTests
{
    internal static readonly HostLifecycleTiming ShortTiming = new(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(2));
    // Windows timer granularity can signal the grace deadline slightly early.
    private static readonly TimeSpan GraceTimerTolerance = TimeSpan.FromMilliseconds(20);
    private static readonly HostLifecycleTiming CooperativeTiming = ShortTiming with { Grace = TimeSpan.FromSeconds(15), ConfirmationWait = TimeSpan.FromSeconds(15) };
    private static Task Stop(VisibleHost host, Func<VisibleHost, Task>? requestShutdown = null,
        Func<VisibleHost, Task<bool>>? confirmForce = null, HostLifecycleTiming? timing = null)
        => HostLifecycleService.Stop(host, requestShutdown, confirmForce, timing ?? CooperativeTiming);
    private static Task<Process> Restart(VisibleHost host, ProcessStartInfo start, Func<VisibleHost, Task>? requestShutdown = null,
        Func<VisibleHost, Task<bool>>? confirmForce = null) => HostLifecycleService.Restart(host, start, requestShutdown, confirmForce, CooperativeTiming);

    private static async Task KillLeakedHost(Process process, VisibleHost identity)
    {
        if (process.HasExited) return;
        Assert.Equal(process.Id, identity.Pid);
        Assert.StartsWith(McpOperatorFixture.RepositoryRoot + Path.DirectorySeparatorChar, Path.GetFullPath(identity.ImagePath), StringComparison.OrdinalIgnoreCase);
        using var verified = HostLifecycleService.VerifiedProcess(identity);
        verified.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }

    [Fact]
    public async Task Production_shutdown_timing_preserves_the_existing_defaults()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), McpSessionClient.OwnedHostExitTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(500), McpSessionClient.RegistrationPollInterval);
        Assert.Equal(McpSessionClient.OwnedHostExitTimeout, HostLifecycleTiming.Default.Grace);
        Assert.Equal(McpSessionClient.RegistrationPollInterval, HostLifecycleTiming.Default.PollInterval);
        Assert.Equal(System.Threading.Timeout.InfiniteTimeSpan, HostLifecycleTiming.Default.ConfirmationWait);
        await using var fixture = new McpOperatorFixture();
        await using var client = fixture.Client();
        Assert.Same(HostLifecycleTiming.Default, client.ShutdownTiming);
    }

    private sealed class SyntheticHost : IAsyncDisposable
    {
        internal readonly string Root = McpOperatorFixture.RepositoryRoot;
        internal readonly string Data;
        internal readonly Process Process;
        internal readonly VisibleHost Identity;
        internal SyntheticHost()
        {
            Data = Path.Combine(Root, "bin-build", "fix-r1-host-" + Guid.NewGuid().ToString("N"));
            string image = Path.Combine(AppContext.BaseDirectory, "host-lifecycle", "TiaMcp.FoundationHost.exe");
            Assert.StartsWith(Root + Path.DirectorySeparatorChar, Path.GetFullPath(image), StringComparison.OrdinalIgnoreCase);
            var start = new ProcessStartInfo(image) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Root, RedirectStandardInput = true };
            foreach (string argument in new[] { "--bundle-root", Root, "--release-key", "21", "--transport", "http", "--http-prefix", "http://127.0.0.1:0/", "--http-api-key", "fix-r1-synthetic", "--offline" }) start.ArgumentList.Add(argument);
            start.Environment["TIA_MCP_DATA_DIRECTORY"] = Data;
            start.Environment["TIA_MCP_WORKBENCH_STARTED"] = "1";
            Process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Synthetic host did not start");
            Identity = new(Process.Id, Process.StartTime.ToUniversalTime().Ticks, "21", image, Root, "http", null, null, true, false);
        }
        internal async Task Ready()
        {
            var service = new HostLifecycleService(Data, "4.0.0-beta.2");
            for (int attempt = 0; attempt < 100 && !Process.HasExited; attempt++)
            {
                if (service.Discover().Any(host => host.Pid == Process.Id)) break;
                await Task.Delay(ShortTiming.PollInterval);
            }
            Assert.Contains(service.Discover(), host => host.Pid == Process.Id);
        }
        internal async Task Shutdown(VisibleHost _)
        {
            await Process.StandardInput.WriteLineAsync("tiamcp.host-control/1 shutdown"); await Process.StandardInput.FlushAsync();
            await Process.WaitForExitAsync().WaitAsync(CooperativeTiming.Grace);
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!Process.HasExited) await Stop(Identity, Shutdown, _ => Task.FromResult(true));
            }
            finally
            {
                try { await KillLeakedHost(Process, Identity); }
                finally
                {
                    Process.Dispose();
                    Assert.StartsWith(Path.Combine(Root, "bin-build") + Path.DirectorySeparatorChar, Path.GetFullPath(Data), StringComparison.OrdinalIgnoreCase);
                    if (Directory.Exists(Data)) Directory.Delete(Data, true);
                }
            }
        }
    }

    [Fact]
    public async Task Client_and_Workbench_hosts_share_leases_when_bundle_data_is_read_only()
    {
        await Task.CompletedTask;
        string root = Path.Combine(McpOperatorFixture.RepositoryRoot, "bin-build", "fix-r1-data-" + Guid.NewGuid().ToString("N"));
        string bundle = Path.Combine(root, "bundle"), local = Path.Combine(root, "local"), temp = Path.Combine(root, "temp");
        string output = Path.Combine(bundle, "runtime", "v21");
        Directory.CreateDirectory(output); Directory.CreateDirectory(Path.Combine(bundle, "manifest"));
        File.WriteAllText(Path.Combine(bundle, "manifest", "package-manifest.json"), "{}");
        File.WriteAllText(Path.Combine(bundle, "data"), "blocked");
        try
        {
            var client = DataLocations.Resolve(output, null!, local, temp);
            var start = new ProcessStartInfo("unused-fixture-host");
            start.Environment.Remove("TIA_MCP_DATA_DIRECTORY");
            HostLifecycleService.PrepareWorkbenchHost(start);
            start.Environment.TryGetValue("TIA_MCP_DATA_DIRECTORY", out string? configured);
            var workbench = DataLocations.Resolve(output, configured!, local, temp);
            Assert.Null(configured);
            Assert.Equal(Path.Combine(local, "TiaMcp", "instance-leases"), client.LeasesDirectory);
            Assert.Equal(client.LeasesDirectory, workbench.LeasesDirectory);
            Assert.Equal(client.WorkbenchDataDirectory, workbench.WorkbenchDataDirectory);
            Assert.Equal("1", start.Environment["TIA_MCP_WORKBENCH_STARTED"]);
        }
        finally
        {
            Assert.StartsWith(Path.Combine(McpOperatorFixture.RepositoryRoot, "bin-build") + Path.DirectorySeparatorChar, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("pid")] [InlineData("startTicks")] [InlineData("imagePath")] [InlineData("bundleRoot")]
    [InlineData("releaseKey")] [InlineData("transport")]
    public async Task A_malformed_registration_is_reported_without_hiding_valid_hosts(string missing)
    {
        await Task.CompletedTask;
        string root = Path.Combine(McpOperatorFixture.RepositoryRoot, "bin-build", "fix-r1-registration-" + Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(root, "run", "hosts"); Directory.CreateDirectory(directory);
        using var process = Process.GetCurrentProcess();
        var record = new JsonObject { ["version"] = 1, ["userSid"] = LocalPipeSecurity.CurrentSid,
            ["pid"] = process.Id, ["startTicks"] = process.StartTime.ToUniversalTime().Ticks,
            ["releaseKey"] = "21", ["imagePath"] = process.MainModule!.FileName, ["bundleRoot"] = McpOperatorFixture.RepositoryRoot,
            ["transport"] = "stdio", ["sessions"] = new JsonArray(new JsonObject { ["worker"] = new JsonObject { ["state"] = "busy" } }) };
        File.WriteAllText(Path.Combine(directory, "valid.json"), record.ToJsonString());
        record.Remove(missing); File.WriteAllText(Path.Combine(directory, "malformed.json"), record.ToJsonString());
        using var messages = new StringWriter(); using var listener = new TextWriterTraceListener(messages);
        Trace.Listeners.Add(listener);
        try
        {
            var host = Assert.Single(new HostLifecycleService(root, "4.0.0-beta.2").Discover());
            Assert.Equal(process.Id, host.Pid); Assert.True(host.CallInProgress);
            listener.Flush(); Assert.Contains("malformed.json", messages.ToString());
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            Assert.StartsWith(Path.Combine(McpOperatorFixture.RepositoryRoot, "bin-build") + Path.DirectorySeparatorChar, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Busy_host_refuses_stop_and_restart_before_sending_a_shutdown_request()
    {
        await using var fixture = new SyntheticHost(); await fixture.Ready();
        int requests = 0;
        var host = fixture.Identity with { CallInProgress = true };
        Task Request(VisibleHost _) { requests++; return Task.CompletedTask; }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Stop(host, Request));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Restart(host,
            new ProcessStartInfo(host.ImagePath) { UseShellExecute = false }, Request));
        Assert.Equal(0, requests); Assert.False(fixture.Process.HasExited);
    }

    [Fact]
    public async Task A_call_started_after_discovery_still_blocks_the_stop_request()
    {
        await using var fixture = new SyntheticHost(); await fixture.Ready();
        var host = Assert.Single(new HostLifecycleService(fixture.Data, "4.0.0-beta.2").Discover());
        Assert.False(host.CallInProgress); Assert.NotNull(host.RegistrationPath);
        var record = JsonNode.Parse(File.ReadAllText(host.RegistrationPath))!.AsObject();
        record["sessions"] = new JsonArray(new JsonObject { ["callInProgress"] = true });
        File.WriteAllText(host.RegistrationPath, record.ToJsonString());
        int requests = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Stop(host, _ => { requests++; return Task.CompletedTask; }));
        Assert.Equal(0, requests);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Unresponsive_synthetic_host_is_killed_only_after_the_existing_grace_and_explicit_confirmation(bool force)
    {
        string root = McpOperatorFixture.RepositoryRoot;
        string data = Path.Combine(root, "bin-build", "fix-r1-force-" + Guid.NewGuid().ToString("N"));
        string image = Path.Combine(AppContext.BaseDirectory, "host-lifecycle", "TiaMcp.FoundationHost.exe");
        var start = new ProcessStartInfo(image) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root, RedirectStandardInput = true };
        foreach (string argument in new[] { "--bundle-root", root, "--release-key", "21", "--transport", "http", "--http-prefix", "http://127.0.0.1:0/", "--http-api-key", "fix-r1-synthetic", "--offline" }) start.ArgumentList.Add(argument);
        start.Environment["TIA_MCP_DATA_DIRECTORY"] = data;
        start.Environment["TIA_MCP_WORKBENCH_STARTED"] = "1";
        using var process = Process.Start(start) ?? throw new IOException("Synthetic host did not start");
        var identity = new VisibleHost(process.Id, process.StartTime.ToUniversalTime().Ticks, "21", image, root, "http", null, null, true, false);
        async Task Graceful(VisibleHost _)
        {
            await process.StandardInput.WriteLineAsync("tiamcp.host-control/1 shutdown"); await process.StandardInput.FlushAsync();
            await process.WaitForExitAsync().WaitAsync(CooperativeTiming.Grace);
        }
        var confirmationRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var service = new HostLifecycleService(data, "4.0.0-beta.2");
            for (int attempt = 0; attempt < 100 && !process.HasExited; attempt++)
            {
                if (service.Discover().Any(host => host.Pid == process.Id)) break;
                await Task.Delay(ShortTiming.PollInterval);
            }
            Assert.Contains(service.Discover(), host => host.Pid == process.Id);
            int requests = 0, confirmations = 0; var elapsed = Stopwatch.StartNew();
            var stopping = Stop(identity, _ => { requests++; return Task.CompletedTask; }, async _ => {
                confirmations++;
                Assert.Equal(1, requests);
                Assert.True(elapsed.Elapsed >= ShortTiming.Grace - GraceTimerTolerance);
                Assert.False(process.HasExited);
                confirmationRequested.TrySetResult(true);
                return await confirmation.Task;
            }, ShortTiming);
            await Task.WhenAny(confirmationRequested.Task, stopping).WaitAsync(TimeSpan.FromSeconds(5));
            if (stopping.IsFaulted) await stopping;
            Assert.False(stopping.IsCompleted); Assert.False(process.HasExited);
            await Task.Delay(ShortTiming.PollInterval);
            Assert.False(stopping.IsCompleted); Assert.False(process.HasExited);
            confirmation.TrySetResult(force);
            if (force) { await stopping; Assert.True(process.HasExited); Assert.NotEqual(0, process.ExitCode); }
            else
            {
                var refusal = await Assert.ThrowsAsync<TimeoutException>(() => stopping);
                Assert.Equal("The host did not stop gracefully. TIA may need a restart if the host is forcibly terminated.", refusal.Message);
                Assert.False(process.HasExited);
            }
            Assert.Equal(1, confirmations);
        }
        finally
        {
            confirmation.TrySetResult(false);
            try { if (!process.HasExited) await Stop(identity, Graceful); }
            finally
            {
                await KillLeakedHost(process, identity);
                Assert.StartsWith(Path.Combine(root, "bin-build") + Path.DirectorySeparatorChar, Path.GetFullPath(data), StringComparison.OrdinalIgnoreCase);
                if (Directory.Exists(data)) Directory.Delete(data, true);
            }
        }
    }

    [Fact]
    public async Task A_call_reported_during_force_confirmation_still_prevents_termination()
    {
        await using var fixture = new SyntheticHost(); await fixture.Ready();
        var host = Assert.Single(new HostLifecycleService(fixture.Data, "4.0.0-beta.2").Discover());
        int confirmations = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Stop(host, _ => Task.CompletedTask, async _ => {
            confirmations++;
            var record = JsonNode.Parse(File.ReadAllText(host.RegistrationPath!))!.AsObject();
            record["sessions"] = new JsonArray(new JsonObject { ["callInProgress"] = true });
            File.WriteAllText(host.RegistrationPath!, record.ToJsonString());
            await Task.Delay(ShortTiming.PollInterval);
            Assert.False(fixture.Process.HasExited);
            return true;
        }, ShortTiming));
        Assert.Equal(1, confirmations); Assert.False(fixture.Process.HasExited);
    }

    [Fact]
    public async Task An_unanswered_force_confirmation_never_terminates_the_host()
    {
        await using var fixture = new SyntheticHost(); await fixture.Ready();
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int confirmations = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => HostLifecycleService.Stop(fixture.Identity, _ => Task.CompletedTask,
            _ => { confirmations++; return confirmation.Task; }, ShortTiming with { ConfirmationWait = TimeSpan.FromMilliseconds(80) }));
        Assert.Equal(1, confirmations); Assert.False(fixture.Process.HasExited);
    }

    [Fact]
    public async Task A_host_exiting_during_force_confirmation_is_reverified_before_termination()
    {
        await using var fixture = new SyntheticHost(); await fixture.Ready();
        int confirmations = 0;
        var error = await Record.ExceptionAsync(() => Stop(fixture.Identity, _ => Task.CompletedTask, async host => {
            confirmations++;
            await fixture.Shutdown(host);
            return true;
        }, ShortTiming with { ConfirmationWait = CooperativeTiming.ConfirmationWait }));
        Assert.True(error is ArgumentException or UnauthorizedAccessException, error?.ToString());
        Assert.Equal(1, confirmations); Assert.True(fixture.Process.HasExited);
        Assert.Equal(0, fixture.Process.ExitCode);
    }

    [Theory]
    [InlineData(null, true)] [InlineData("4.0.0-beta.1", true)]
    [InlineData("4.0.0-beta.2", false)] [InlineData("4.0.0", false)]
    public async Task Older_and_legacy_hosts_are_flagged(string? version, bool expected)
    {
        await Task.CompletedTask;
        Assert.Equal(expected, HostLifecycleService.IsOlder(version, "v4.0.0-beta.2"));
    }

    [Fact]
    public async Task Process_identity_must_match_before_any_stop()
    {
        await Task.CompletedTask;
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
        var start = new ProcessStartInfo(image) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root, RedirectStandardInput = true };
        // Port zero binds a private loopback listener; no external service is contacted.
        foreach (string argument in new[] { "--bundle-root", root, "--release-key", "21", "--transport", "http",
            "--http-prefix", "http://127.0.0.1:0/", "--http-api-key", "des1-synthetic-local-host", "--offline" }) start.ArgumentList.Add(argument);
        start.Environment["TIA_MCP_DATA_DIRECTORY"] = data;
        start.Environment["TIA_MCP_WORKBENCH_STARTED"] = "1";
        using var process = Process.Start(start) ?? throw new IOException("Host fixture did not start.");
        var expected = new VisibleHost(process.Id, process.StartTime.ToUniversalTime().Ticks, "21", image, root,
            "http", "http://127.0.0.1:0/", null, false, true);
        Process? replacement = null;
        VisibleHost? replacementIdentity = null;
        async Task RequestShutdown(VisibleHost host)
        {
            var owned = host.Pid == process.Id ? process : replacement!;
            await owned.StandardInput.WriteLineAsync("tiamcp.host-control/1 shutdown");
            await owned.StandardInput.FlushAsync();
            await owned.WaitForExitAsync().WaitAsync(CooperativeTiming.Grace);
        }
        try
        {
            var service = new HostLifecycleService(data, "4.0.0-beta.2");
            VisibleHost? host = null;
            for (int attempt = 0; attempt < 100 && !process.HasExited; attempt++)
            {
                host = service.Discover().SingleOrDefault(candidate => candidate.Pid == process.Id);
                if (host != null) break;
                await Task.Delay(ShortTiming.PollInterval);
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
                await Task.Delay(ShortTiming.PollInterval);
            }
            Assert.NotNull(legacy);
            Assert.True(legacy.OlderVersion);
            Assert.True(Uri.TryCreate(legacy.HttpPrefix, UriKind.Absolute, out var endpoint));
            Assert.NotNull(endpoint);
            Assert.Equal("127.0.0.1", endpoint.Host);
            Assert.InRange(endpoint.Port, 1, 65535);
            replacement = await Restart(host, start, RequestShutdown);
            replacementIdentity = expected with { Pid = replacement.Id, StartTicks = replacement.StartTime.ToUniversalTime().Ticks };
            Assert.True(process.HasExited);
            Assert.Equal(0, process.ExitCode);
            for (int attempt = 0; attempt < 100 && !replacement.HasExited; attempt++)
            {
                if (service.Discover().Any(candidate => candidate.Pid == replacement.Id)) break;
                await Task.Delay(ShortTiming.PollInterval);
            }
            Assert.Contains(service.Discover(), candidate => candidate.Pid == replacement.Id);
            await Stop(replacementIdentity, RequestShutdown);
            Assert.Equal(0, replacement.ExitCode);
            Assert.Empty(service.Discover());
        }
        finally
        {
            try
            {
                try { if (!process.HasExited) await Stop(expected, RequestShutdown); }
                finally { await KillLeakedHost(process, expected); }
            }
            finally
            {
                try
                {
                    if (replacement != null && !replacement.HasExited && replacementIdentity != null) await Stop(replacementIdentity, RequestShutdown);
                }
                finally
                {
                    try { if (replacement != null && replacementIdentity != null) await KillLeakedHost(replacement, replacementIdentity); }
                    finally
                    {
                        replacement?.Dispose();
                        Assert.StartsWith(Path.Combine(root, "bin-build") + Path.DirectorySeparatorChar, Path.GetFullPath(data), StringComparison.OrdinalIgnoreCase);
                        if (Directory.Exists(data)) Directory.Delete(data, true);
                    }
                }
            }
        }
    }
}
