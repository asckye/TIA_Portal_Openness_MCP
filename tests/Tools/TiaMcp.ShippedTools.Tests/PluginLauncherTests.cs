using System.Diagnostics;
using System.Text.Json;
using System.Runtime.Versioning;
using TiaMcp.PluginLauncher;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcp.ShippedTools.Tests;

[SupportedOSPlatform("windows")]
public sealed class PluginLauncherTests
{
    [Theory]
    [InlineData("relative")]
    [InlineData("drive-relative")]
    [InlineData("missing")]
    [InlineData("no-manifest")]
    [InlineData("wrong-product")]
    [InlineData("invalid-json")]
    public async Task Invalid_explicit_root_refuses_without_falling_back_to_plugin_copy(string kind)
    {
        using var fixture = new LauncherFixture();
        string root = fixture.InvalidRoot(kind);
        using var process = fixture.Start(root, "19");
        process.StandardInput.Close();
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotEqual(0, process.ExitCode); Assert.Equal("", output);
        Assert.Contains("TIA MCP plugin launcher:", error);
        Assert.Contains("TIA_MCP_BUNDLE_ROOT", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("V21")]
    [InlineData("21.0")]
    [InlineData("14")]
    [InlineData("15")]
    [InlineData("22")]
    [InlineData(" 19 ")]
    public void Invalid_release_keys_are_not_normalized_or_replaced(string key)
    {
        using var fixture = new LauncherFixture();
        Assert.Throws<ArgumentException>(() => fixture.Resolve(null, key));
    }

    [Fact]
    public async Task Invalid_release_key_emits_stderr_and_nonzero_exit()
    {
        using var fixture = new LauncherFixture();
        using var process = fixture.Start(null, "V19"); process.StandardInput.Close();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotEqual(0, process.ExitCode); Assert.Contains("TIA_MCP_RELEASE_KEY", error);
        Assert.Equal("", await process.StandardOutput.ReadToEndAsync());
    }

    [Theory]
    [InlineData("host")]
    [InlineData("worker")]
    public void Selected_release_requires_its_host_and_worker(string missing)
    {
        using var fixture = new LauncherFixture();
        File.Delete(missing == "host" ? fixture.Host : fixture.Worker);
        Assert.Throws<BundleResourceUnavailableException>(() => fixture.Resolve(null, "19"));
    }

    [Theory]
    [InlineData("14sp1", "14.0.1.0")]
    [InlineData("15.1", "15.1.0.0")]
    [InlineData("16", "16.0.0.0")]
    [InlineData("17", "17.0.0.0")]
    [InlineData("18", "18.0.0.0")]
    [InlineData("19", "19.0.0.0")]
    [InlineData("20", "20.0.0.0")]
    [InlineData("21", "21.0.0.0")]
    public void Default_selection_uses_installed_exact_identity_before_bundled_fallback(string key, string installed)
    {
        using var fixture = new LauncherFixture(); fixture.AddHost(key);
        Assert.Equal(key, fixture.Resolve(null, null, [installed]).Key);
    }

    [Fact]
    public void Default_is_newest_installed_then_newest_runnable_host_worker_pair()
    {
        using var fixture = new LauncherFixture(); fixture.AddHost("16"); fixture.AddHost("21");
        Assert.Equal("19", fixture.Resolve(null, null, ["16", "19"]).Key);
        Assert.Equal("21", fixture.Resolve(null, null, []).Key);
        File.Delete(Path.Combine(fixture.Bundle, "runtime/v21/worker/TiaMcp.Engine.V21.exe"));
        Assert.Equal("19", fixture.Resolve(null, null, []).Key);
        // A detected installation whose host is missing is an error, never a different release.
        Assert.Throws<BundleResourceUnavailableException>(() => fixture.Resolve(null, null, ["18"]));
    }

    [Fact]
    public async Task Unset_release_variable_runs_the_shared_default_selection()
    {
        using var fixture = new LauncherFixture();
        foreach (string key in new[] { "14sp1", "15.1", "16", "17", "18", "20", "21" }) fixture.AddHost(key);
        string expected = TiaOpenness.Core.Environment.WorkbenchReleaseSelection.DefaultKey(fixture.Bundle);
        using var process = fixture.Start(null, null); process.StandardInput.Close();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, process.ExitCode);
        using var receipt = JsonDocument.Parse(error);
        Assert.Equal(new[] { "--release-key", expected }, receipt.RootElement.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task Selected_installed_bundle_inherits_stdio_and_extra_arguments_byte_for_byte()
    {
        using var fixture = new LauncherFixture();
        string installed = Path.Combine(fixture.Work, "installed bundle"); fixture.AddBundle(installed);
        string[] extra = ["--fixture-exit-code", "0", "space and 中文", "quote\"slash\\", "", "tail\\"];
        using var process = fixture.Start(installed, "19", extra);
        byte[] payload = [0, 1, 10, 13, 34, 127, 128, 255, .. System.Text.Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1}\n中文")];
        await process.StandardInput.BaseStream.WriteAsync(payload); process.StandardInput.Close();
        using var output = new MemoryStream(); await process.StandardOutput.BaseStream.CopyToAsync(output);
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, process.ExitCode); Assert.Equal(payload, output.ToArray());
        using var receipt = JsonDocument.Parse(error);
        Assert.Equal(new[] { "--release-key", "19" }.Concat(extra), receipt.RootElement.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(installed, receipt.RootElement.GetProperty("root").GetString());
    }

    [Theory]
    [InlineData(0)] [InlineData(7)] [InlineData(73)] [InlineData(255)]
    public async Task Plugin_copy_is_used_without_override_and_host_exit_code_is_forwarded(int exitCode)
    {
        using var fixture = new LauncherFixture();
        using var process = fixture.Start(null, "19", "--fixture-exit-code", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        process.StandardInput.Close();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(exitCode, process.ExitCode);
        using var receipt = JsonDocument.Parse(error);
        Assert.Equal(fixture.Bundle, receipt.RootElement.GetProperty("root").GetString());
        Assert.Equal("", await process.StandardOutput.ReadToEndAsync());
    }

    [Fact]
    public async Task Terminating_launcher_closes_job_and_stops_only_its_fake_host()
    {
        using var fixture = new LauncherFixture();
        using var launcher = fixture.Start(null, "19", "--fixture-wait");
        string? line = await launcher.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        using var child = Process.GetProcessById(int.Parse(line!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(Path.GetFullPath(fixture.Host), child.MainModule!.FileName, StringComparer.OrdinalIgnoreCase);
        fixture.StopOwned(launcher);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(child.HasExited);
    }

    private sealed class LauncherFixture : IDisposable
    {
        private static string RepositoryRoot
        {
            get
            {
                foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
                {
                    var directory = new DirectoryInfo(start);
                    while (directory != null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md"))) directory = directory.Parent;
                    if (directory != null) return directory.FullName;
                }
                throw new IOException("Cannot locate repository.");
            }
        }
        internal string Work { get; } = Path.GetFullPath(Path.Combine(RepositoryRoot, "bin-build/plugin-launcher-tests", Guid.NewGuid().ToString("N")));
        internal string Bundle => Path.Combine(Work, "plugin copy");
        internal string Launcher => Path.Combine(Bundle, "runtime/tools/TiaMcp.PluginLauncher.exe");
        internal string Host => Path.Combine(Bundle, "runtime/v19/TiaMcp.FoundationHost.exe");
        internal string Worker => Path.Combine(Bundle, "runtime/v19/worker/TiaMcp.PlcWorker.19.exe");
        private readonly List<int> processes = [];

        internal LauncherFixture()
        {
            AddBundle(Bundle);
            CopyDirectory(Path.Combine(AppContext.BaseDirectory, "plugin-fixtures/launcher"), Path.GetDirectoryName(Launcher)!);
        }
        internal LauncherSelection Resolve(string? root, string? key, IEnumerable<string>? installed = null) =>
            LauncherSelection.Resolve(Path.GetDirectoryName(Launcher)!, root, key, installed ?? []);
        internal void AddBundle(string root)
        {
            Directory.CreateDirectory(Path.Combine(root, "manifest"));
            File.WriteAllText(Path.Combine(root, "manifest/package-manifest.json"), """{"packageType":"tia-portal-mcp-complete-offline-delivery-bundle"}""");
            AddHost("19", root);
        }
        internal void AddHost(string key, string? root = null)
        {
            string directory = Path.Combine(root ?? Bundle, "runtime/v" + key);
            CopyDirectory(Path.Combine(AppContext.BaseDirectory, "plugin-fixtures/host"), directory);
            File.Copy(Path.Combine(directory, "PluginHostFixture.exe"), Path.Combine(directory, "TiaMcp.FoundationHost.exe"), true);
            Directory.CreateDirectory(Path.Combine(directory, "worker"));
            string worker = key is "20" or "21" ? "TiaMcp.Engine.V" + key : "TiaMcp.PlcWorker." + key;
            File.WriteAllText(Path.Combine(directory, "worker", worker + ".exe"), "fixture worker marker");
        }
        internal string InvalidRoot(string kind)
        {
            if (kind == "relative") return "relative";
            if (kind == "drive-relative") return "D:relative";
            string path = Path.Combine(Work, kind);
            if (kind != "missing") Directory.CreateDirectory(path);
            if (kind is "wrong-product" or "invalid-json")
            {
                Directory.CreateDirectory(Path.Combine(path, "manifest"));
                File.WriteAllText(Path.Combine(path, "manifest/package-manifest.json"), kind == "invalid-json" ? "{" : """{"packageType":"another-product"}""");
            }
            return path;
        }
        internal Process Start(string? root, string? key, params string[] extra)
        {
            var start = new ProcessStartInfo(Launcher) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = RepositoryRoot };
            start.Environment.Remove("TIA_MCP_BUNDLE_ROOT");
            if (root != null) start.Environment["TIA_MCP_BUNDLE_ROOT"] = root;
            start.Environment.Remove("TIA_MCP_RELEASE_KEY");
            if (key != null) start.Environment["TIA_MCP_RELEASE_KEY"] = key;
            foreach (string argument in extra) start.ArgumentList.Add(argument);
            var process = Process.Start(start) ?? throw new IOException("Cannot start fixture launcher.");
            processes.Add(process.Id); return process;
        }
        internal void StopOwned(Process process)
        {
            if (process.HasExited) return;
            Assert.Equal(Path.GetFullPath(Launcher), process.MainModule!.FileName, StringComparer.OrdinalIgnoreCase);
            Assert.StartsWith(Path.GetFullPath(RepositoryRoot) + Path.DirectorySeparatorChar, process.MainModule.FileName, StringComparison.OrdinalIgnoreCase);
            process.Kill(); process.WaitForExit(15000);
        }
        public void Dispose()
        {
            foreach (int id in processes)
            {
                Process owned;
                try { owned = Process.GetProcessById(id); }
                catch (ArgumentException) /* swallow(teardown): this exact fixture PID has already exited */ { continue; }
                using (owned) StopOwned(owned);
            }
            Assert.StartsWith(Path.GetFullPath(RepositoryRoot) + Path.DirectorySeparatorChar, Path.GetFullPath(Work), StringComparison.OrdinalIgnoreCase);
            // Retain synthetic bundles under bin-build for process and stream evidence.
        }
        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        }
    }
}
