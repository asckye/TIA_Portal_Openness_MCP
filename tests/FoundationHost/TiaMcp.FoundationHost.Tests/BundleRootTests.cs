using TiaMcp.FoundationHost;
using TiaOpenness.Shared;
using System.Runtime.CompilerServices;
using Xunit;

namespace TiaMcp.FoundationHost.Tests;

public sealed class BundleRootTests : IDisposable
{
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
    public void Full_catalog_host_discovers_worker_and_catalog_without_path_options(string release)
    {
        Put("manifest/package-manifest.json", "fixture");
        Put("runtime/v" + release + "/release-key.txt", release);
        var options = HostOptions.Parse(new[] { "--profile", "full" }, At("runtime/v" + release));
        Assert.Equal(At("runtime/v" + release + "/worker/TiaMcp.Engine.V" + release + ".exe"), options.EngineWorkerExe);
        Assert.Equal(At("runtime/v" + release + "/worker/tool-catalog.json"), options.EngineCatalog);
        Assert.Equal(scratch, BundleLayout.ResolveRoot(Path.GetDirectoryName(options.EngineWorkerExe)!, null, null));
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(new[] { "--engine-worker", At("worker.exe") }, At("runtime/v" + release)));
        options = HostOptions.Parse(new[] { "--engine-worker", At("custom.exe"), "--engine-catalog", At("custom.json") }, At("runtime/v" + release));
        Assert.Equal(At("custom.exe"), options.EngineWorkerExe);
        Assert.Equal(At("custom.json"), options.EngineCatalog);
    }

    [Fact]
    public void Development_test_host_reads_resources_without_an_override()
    {
        string root = BundleLayout.RequireRoot(AppContext.BaseDirectory);
        Assert.Equal(RepositoryRoot(), root);
        Assert.True(Directory.Exists(BundleLayout.RequirePath(root, "templates", true)));
        Assert.True(File.Exists(BundleLayout.RequirePath(root, "reference/siemens-openness/UPSTREAM.json")));
    }

    private static string RepositoryRoot([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../../.."));

    public void Dispose() { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
}
