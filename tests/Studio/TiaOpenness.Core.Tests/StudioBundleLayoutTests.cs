using System;
using System.Collections.Generic;
using System.IO;
using TiaMcp.Versioning;
using TiaOpenness.Shared;
using Xunit;

namespace TiaOpenness.Core.Tests;

public sealed class StudioBundleLayoutTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "studio core 中文 " + Guid.NewGuid().ToString("N"));
    private static string At(string root, string path) => Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
    private static void Put(string root, string path)
    {
        string file = At(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "fixture");
    }

    public static IEnumerable<object[]> Anchors()
    {
        yield return new object[] { "" };
        yield return new object[] { "runtime/studio" };
        yield return new object[] { "runtime/studio/bridge" };
        foreach (var configuration in new[] { "Release", "Debug" })
        foreach (var variant in new[] { "", "shared-adapter/" })
        {
            yield return new object[] { "src/Studio/Gui/bin/" + configuration + "/" + variant + "net10.0-windows" };
            yield return new object[] { "src/Studio/Gui/bin/" + configuration + "/" + variant + "net10.0-windows/bridge" };
            yield return new object[] { "src/Studio/Bridge/bin/" + configuration + "/" + variant + "net48" };
        }
    }

    [Theory]
    [MemberData(nameof(Anchors))]
    public void Formal_anchors_select_the_bundle_without_cwd_or_ancestor_probes(string anchor)
    {
        Put(scratch, "manifest/package-manifest.json");
        string output = Directory.CreateDirectory(At(scratch, anchor)).FullName;
        foreach (string suffix in new[] { "", "\\", "/" })
            Assert.Equal(scratch, BundleLayout.ResolveWorkbenchRoot(output + suffix, null!, null!));
        string unknown = Directory.CreateDirectory(At(scratch, "arbitrary/bin/Release/net48")).FullName;
        Assert.Null(BundleLayout.ResolveWorkbenchRoot(unknown, null!, null!));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void Workbench_uses_cli_then_environment_then_anchor(int inputs)
    {
        string anchor = At(scratch, "anchor"), cli = At(scratch, "CLI 中文"), env = At(scratch, "environment");
        Directory.CreateDirectory(anchor);
        if ((inputs & 1) != 0) Put(anchor, "manifest/package-manifest.json");
        if ((inputs & 2) != 0) Put(env, "manifest/package-manifest.json");
        if ((inputs & 4) != 0) Put(cli, "manifest/package-manifest.json");
        string? expected = (inputs & 4) != 0 ? cli : (inputs & 2) != 0 ? env : (inputs & 1) != 0 ? anchor : null;
        Assert.Equal(expected, BundleLayout.ResolveWorkbenchRoot(anchor, (inputs & 4) != 0 ? cli : null!, (inputs & 2) != 0 ? env : null!));
        Assert.Throws<ArgumentException>(() => BundleLayout.ResolveWorkbenchRoot(anchor, "relative", env));
        Assert.Throws<BundleResourceUnavailableException>(() => BundleLayout.ResolveWorkbenchRoot(anchor, At(scratch, "bad"), env));
        Assert.Throws<BundleResourceUnavailableException>(() => BundleLayout.ResolveWorkbenchRoot(anchor, null!, At(scratch, "bad")));
    }

    [Theory]
    [MemberData(nameof(Anchors))]
    public void Products_bridge_and_adapters_have_one_path_per_selected_layout(string anchor)
    {
        Put(scratch, "manifest/package-manifest.json");
        string output = Directory.CreateDirectory(At(scratch, anchor)).FullName;
        string bridge = BundleLayout.WorkbenchBridgePath(scratch, output);
        string expectedBridge = anchor.StartsWith("src/Studio/Gui/", StringComparison.Ordinal)
            ? Path.Combine(output, anchor.EndsWith("/bridge", StringComparison.Ordinal) ? "" : "bridge", "TiaOpenness.Bridge.exe")
            : anchor.StartsWith("src/Studio/Bridge/", StringComparison.Ordinal) ? Path.Combine(output, "TiaOpenness.Bridge.exe")
            : At(scratch, "runtime/studio/bridge/TiaOpenness.Bridge.exe");
        Assert.Equal(expectedBridge, bridge);
        Assert.Throws<BundleResourceUnavailableException>(() => BundleLayout.RequireWorkbenchBridge(output, scratch));
        Put(scratch, Path.GetRelativePath(scratch, bridge));
        Assert.Equal(bridge, BundleLayout.RequireWorkbenchBridge(output, scratch));
        foreach (var release in TiaVersionCatalog.Runnable)
        {
            var product = BundleLayout.GetProduct(release.Key);
            Assert.Equal("v" + release.Key, product.RuntimeDirectory);
            Assert.Equal("TiaMcp.FoundationHost.exe", product.Executable);
            Assert.Equal("--release-key", product.VersionOption);
            string configuration = anchor.Contains("/Debug/", StringComparison.Ordinal) ? "Debug" : "Release";
            string expectedEngine = anchor.StartsWith("src/Studio/", StringComparison.Ordinal)
                ? At(scratch, "src/FoundationHost/bin/" + configuration + "/net10.0/" + product.Executable)
                : At(scratch, "runtime/v" + release.Key + "/" + product.Executable);
            Assert.Equal(expectedEngine, BundleLayout.WorkbenchEnginePath(scratch, release.Key, output));
            Assert.Equal(At(scratch, "runtime/v" + release.Key + "/worker/" + product.WorkerExecutable), BundleLayout.WorkerPath(scratch, release.Key));
            Assert.Equal(Path.Combine(Path.GetDirectoryName(bridge)!, "adapters", "v" + release.Key, "TiaOpenness.Openness.dll"),
                BundleLayout.WorkbenchAdapterPath(output, release.Key, "TiaOpenness.Openness.dll", scratch));
            Assert.Equal(Path.Combine(Path.GetDirectoryName(bridge)!, "adapters", "v" + release.Key, "TiaMcp.Adapter." + release.Key + ".dll"),
                BundleLayout.WorkbenchAdapterPath(output, release.Key, "TiaMcp.Adapter." + release.Key + ".dll", scratch));
        }
        Assert.Throws<ArgumentException>(() => BundleLayout.GetProduct("15"));
    }

    [Fact]
    public void Nested_incomplete_bundle_never_borrows_outer_bridge()
    {
        Put(scratch, "manifest/package-manifest.json");
        Put(scratch, "runtime/studio/bridge/TiaOpenness.Bridge.exe");
        string nested = At(scratch, "bin-build/staging");
        Put(nested, "manifest/package-manifest.json");
        string output = Directory.CreateDirectory(At(nested, "runtime/studio")).FullName;
        var error = Assert.Throws<BundleResourceUnavailableException>(() => BundleLayout.RequireWorkbenchBridge(output, nested));
        Assert.Equal(At(nested, "runtime/studio/bridge/TiaOpenness.Bridge.exe"), error.Resource);
    }

    [Fact]
    public void Unwritable_bundle_data_keeps_user_directory_fallback()
    {
        Put(scratch, "manifest/package-manifest.json");
        Put(scratch, "data"); // deterministic IO refusal, without changing the user's ACLs
        string local = At(scratch, "user");
        var data = DataLocations.Resolve(scratch, null!, local, scratch);
        Assert.Null(data.Root);
        Assert.Equal(Path.Combine(local, "TiaPortalMcp"), data.ConfigDirectory);
        Assert.Equal(Path.Combine(local, "TiaOpennessStudio", "ui.settings"), data.UiFilePath);
    }

    public void Dispose() { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
}
