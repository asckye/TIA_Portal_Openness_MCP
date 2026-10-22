extern alias foundationhost;
using TiaMcp.FoundationHost;
using TiaOpenness.Shared;
using System.Runtime.CompilerServices;
using Xunit;

namespace TiaMcp.FoundationHost.Tests;

[Collection(BundleSelectionCollection.Name)]
public sealed class BundleRootTests : IDisposable
{
    // The P8-04j2 private engine cross-check variable; the host no longer reads it (P8-04l).
    private const string RetiredEngineCrossCheckVariable = "TIA_MCP_ENGINE_CROSS_CHECK_DIRECTORY";
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "foundation bundle 中文 " + Guid.NewGuid().ToString("N"));
    private string At(string path) => Path.Combine(scratch, path.Replace('/', Path.DirectorySeparatorChar));
    private void Put(string path, string value)
    {
        string file = At(path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, value);
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void Release_file_and_worker_override_are_preserved_under_the_selected_root(string release)
    {
        Put("manifest/package-manifest.json", "fixture");
        Put("runtime/v" + release + "/release-key.txt", release);
        string directory = At("runtime/v" + release);
        var options = HostOptions.Parse(new[] { "--offline", "--public-api", scratch }, directory);
        Assert.Equal(release, options.ReleaseKey);
        Assert.Equal(scratch, options.BundleRoot);
        Assert.Equal(At("runtime/v" + release + "/worker/TiaMcp.PlcWorker." + release + ".exe"), options.WorkerExe);
        Assert.True(options.BundledWorker);
        string worker = At("explicit worker 中文.exe");
        options = HostOptions.Parse(new[] { "--bundle-root", scratch, "--worker-exe", worker, "--release-key", release, "--offline" }, directory);
        Assert.Equal(worker, options.WorkerExe);
        Assert.False(options.BundledWorker);
        Assert.Throws<InvalidOperationException>(() => HostOptions.Parse(new[] { "--release-key", release == "19" ? "18" : "19" }, directory));
    }

    [Fact]
    public void Explicit_bundle_changes_worker_location_but_keeps_the_adjacent_release_file()
    {
        Put("manifest/package-manifest.json", "fixture"); Put("host/release-key.txt", "19");
        Put("selected 中文/manifest/package-manifest.json", "fixture");
        string selected = At("selected 中文");
        var options = HostOptions.Parse(new[] { "--bundle-root", selected, "--offline" }, At("host"));
        Assert.Equal("19", options.ReleaseKey);
        Assert.Equal(Path.Combine(selected, "runtime", "v19", "worker", "TiaMcp.PlcWorker.19.exe"), options.WorkerExe);
        Assert.Throws<BundleResourceUnavailableException>(() => HostOptions.Parse(new[] { "--bundle-root", At("missing"), "--release-key", "19" }, At("host")));
    }

    [Theory]
    [InlineData("20")]
    [InlineData("21")]
    public void Full_catalog_host_discovers_plc_worker_and_refuses_retired_options(string release)
    {
        Put("manifest/package-manifest.json", "fixture");
        Put("runtime/v" + release + "/release-key.txt", release);
        var options = HostOptions.Parse(new[] { "--profile", "full" }, At("runtime/v" + release));
        Assert.Equal(At("runtime/v" + release + "/worker/TiaMcp.PlcWorker." + release + ".exe"), options.WorkerExe);
        Assert.Null(options.EngineWorkerExe);
        Assert.Null(options.EngineCatalog);
        Assert.Equal(scratch, BundleLayout.ResolveRoot(Path.GetDirectoryName(options.WorkerExe)!, null, null));
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(new[] { "--engine-worker", At("worker.exe") }, At("runtime/v" + release)));
        var error = Assert.Throws<ArgumentException>(() => HostOptions.Parse(new[] { "--engine-worker", At("custom.exe"), "--engine-catalog", At("custom.json") }, At("runtime/v" + release)));
        Assert.Contains("--release-key", error.Message);
        Assert.Contains("--worker-exe", error.Message);
    }

    [Theory, InlineData("20"), InlineData("21")]
    public void Standalone_plc_worker_uses_the_selected_bundle_and_explicit_override(string release)
    {
        Put("manifest/package-manifest.json", "fixture");
        Put("runtime/v" + release + "/release-key.txt", release);
        var options = HostOptions.Parse(new[] { "--plc-worker", "--profile", "full", "--offline" }, At("runtime/v" + release));
        Assert.True(options.PlcWorker); Assert.Null(options.EngineWorkerExe); Assert.Null(options.EngineCatalog);
        Assert.Equal(At("runtime/v" + release + "/worker/TiaMcp.PlcWorker." + release + ".exe"), options.WorkerExe);
        Assert.True(options.BundledWorker); Assert.Equal(scratch, options.BundleRoot);
        options = HostOptions.Parse(new[] { "--plc-worker", "--worker-exe", At("custom worker.exe"), "--offline" }, At("runtime/v" + release));
        Assert.Equal(At("custom worker.exe"), options.WorkerExe); Assert.False(options.BundledWorker);
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(new[] { "--plc-worker", "--engine-worker", At("custom.exe"), "--engine-catalog", At("custom.json") }, At("runtime/v" + release)));
    }

    [Fact]
    public void Development_test_host_reads_resources_without_an_override()
    {
        string root = BundleLayout.RequireRoot(TiaMcp.Engine.Tests.Shared.TestPaths.RegisteredOutput(AppContext.BaseDirectory));
        Assert.Equal(RepositoryRoot(), root);
        Assert.True(Directory.Exists(BundleLayout.RequirePath(root, "templates", true)));
        Assert.True(File.Exists(BundleLayout.RequirePath(root, "reference/siemens-openness/UPSTREAM.json")));
    }

    [Theory]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Packaged_host_refuses_engine_options_with_the_private_variable_set(string release)
    {
        Put("manifest/package-manifest.json", "fixture");
        Put("runtime/v" + release + "/release-key.txt", release);
        string packaged = At("runtime/v" + release);
        var host = typeof(foundationhost::TiaMcp.FoundationHost.HostOptions).Assembly.Location;
        foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(host)!))
            File.Copy(file, Path.Combine(packaged, Path.GetFileName(file)), true);
        // A real checkout's private output with real engine-shaped files: only the host's own location differs.
        string checkoutPrivate = Path.Combine(RepositoryRoot(), "bin-build", "engine-compat");
        string probe = Path.Combine(checkoutPrivate, "packaged-refusal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probe);
        try
        {
            File.WriteAllText(Path.Combine(probe, "TiaMcp.Engine.V" + release + ".exe"), "engine identity");
            File.WriteAllText(Path.Combine(probe, "tool-catalog.json"), "{}");
            foreach (var (variable, worker, catalog) in new[] {
                // The packaged layout has no private output: variable set, nothing behind it.
                (At("bin-build/engine-compat"), At("bin-build/engine-compat/v" + release + "/worker/TiaMcp.Engine.V" + release + ".exe"),
                    At("bin-build/engine-compat/v" + release + "/worker/tool-catalog.json")),
                (checkoutPrivate, Path.Combine(probe, "TiaMcp.Engine.V" + release + ".exe"), Path.Combine(probe, "tool-catalog.json")) })
            {
                var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_EXE") ?? "dotnet")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var arg in new[] { Path.Combine(packaged, Path.GetFileName(host)), "--bundle-root", scratch,
                    "--release-key", release, "--engine-worker", worker, "--engine-catalog", catalog })
                    start.ArgumentList.Add(arg);
                start.Environment[RetiredEngineCrossCheckVariable] = variable;
                using var process = System.Diagnostics.Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.Equal(64, process.ExitCode);
                Assert.Empty(await output);
                Assert.Contains("Engine worker options are retired", await errors);
                Assert.Contains("--worker-exe", await errors);
            }
            string? previous = Environment.GetEnvironmentVariable(RetiredEngineCrossCheckVariable);
            Environment.SetEnvironmentVariable(RetiredEngineCrossCheckVariable, checkoutPrivate);
            try
            {
                Assert.Throws<ArgumentException>(() => foundationhost::TiaMcp.FoundationHost.HostOptions.Parse(["--bundle-root", scratch,
                    "--release-key", release, "--engine-worker", Path.Combine(probe, "TiaMcp.Engine.V" + release + ".exe"),
                    "--engine-catalog", Path.Combine(probe, "tool-catalog.json")], packaged));
            }
            finally { Environment.SetEnvironmentVariable(RetiredEngineCrossCheckVariable, previous); }
        }
        finally { Directory.Delete(probe, true); }
    }

    [Theory]
    [InlineData("20")]
    [InlineData("21")]
    public void Repository_cross_check_route_is_retired_and_never_selects_an_engine(string release)
    {
        Put("manifest/package-manifest.json", "fixture");
        Put("src/FoundationHost/TiaMcp.FoundationHost.csproj", "fixture");
        Put("bin-build/engine-compat/v" + release + "/worker/TiaMcp.Engine.V" + release + ".exe", "engine identity");
        Put("bin-build/engine-compat/v" + release + "/worker/tool-catalog.json", "fixture");
        string directory = At("src/FoundationHost/bin/Release/net10.0");
        string worker = At("bin-build/engine-compat/v" + release + "/worker/TiaMcp.Engine.V" + release + ".exe");
        string catalog = Path.Combine(Path.GetDirectoryName(worker)!, "tool-catalog.json");
        string[] args = ["--bundle-root", scratch, "--release-key", release, "--engine-worker", worker, "--engine-catalog", catalog];
        string? previous = Environment.GetEnvironmentVariable(RetiredEngineCrossCheckVariable);
        try
        {
            // P8-04l: the private engine cross-check route is gone; the former variable, a source host and private
            // engine output together no longer reach an engine worker.
            foreach (var variable in new[] { null, At("bin-build/engine-compat") })
            {
                Environment.SetEnvironmentVariable(RetiredEngineCrossCheckVariable, variable);
                foreach (var host in new[] { directory, At("bin-build/engine-compat/v" + release), At("runtime/v" + release) })
                {
                    var error = Assert.Throws<ArgumentException>(() => foundationhost::TiaMcp.FoundationHost.HostOptions.Parse(args, host));
                    Assert.Contains("Engine worker options are retired", error.Message);
                    Assert.Contains("--worker-exe", error.Message);
                }
                Assert.Throws<ArgumentException>(() => foundationhost::TiaMcp.FoundationHost.HostOptions.Parse([.. args, "--plc-worker"], directory));
            }
        }
        finally { Environment.SetEnvironmentVariable(RetiredEngineCrossCheckVariable, previous); }
    }

    private static string RepositoryRoot([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../../.."));

    public void Dispose() { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
}
