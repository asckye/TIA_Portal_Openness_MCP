using System;
using System.IO;
using System.Reflection;
using TiaOpenness.Client;
using TiaOpenness.Core.Abstractions;
using Xunit;

namespace TiaOpenness.Core.Tests;

public sealed class StudioBundleLayoutTests : IDisposable
{
    private static readonly Func<string, string?> LocateBridge = typeof(BridgeClient)
        .GetMethod("LocateBridge", BindingFlags.Static | BindingFlags.NonPublic, new[] { typeof(string) })!
        .CreateDelegate<Func<string, string?>>();
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "studio core 中文 " + Guid.NewGuid().ToString("N"));

    public StudioBundleLayoutTests() { Directory.CreateDirectory(scratch); }

    private static string At(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static void Put(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture");
    }

    [Theory]
    [InlineData("runtime/studio")]
    [InlineData("runtime/studio/bridge")]
    [InlineData("src/Studio/Gui/bin/Release/net10.0-windows")]
    [InlineData("src/Studio/Gui/bin/Debug/net10.0-windows")]
    [InlineData("src/Studio/Gui/bin/Release/net10.0-windows/bridge")]
    [InlineData("src/Studio/Gui/bin/Debug/net10.0-windows/bridge")]
    [InlineData("src/Studio/Bridge/bin/Release/net48")]
    [InlineData("src/Studio/Bridge/bin/Debug/net48")]
    [InlineData("custom/a/b/c/d")]
    [InlineData("src/Studio/Gui/bin/Custom/net10.0-windows")]
    public void Bridge_and_adapters_match_original_lookups(string anchor)
    {
        // Marker absent/present, competing local candidates and both source fallbacks.
        // Ancestor bundles must not make a bridge select another installation's adapters.
        foreach (bool marker in new[] { false, true })
        foreach (int payload in new[] { 0, 1, 2, 3, 4, 8, 12, 15 })
        {
            string root = At(scratch, marker + "/" + payload + "/repository/bin-build/staging");
            string output = Directory.CreateDirectory(At(root, anchor)).FullName;
            Put(At(scratch, marker + "/" + payload + "/repository/manifest/package-manifest.json"));
            if (marker) Put(At(root, "manifest/package-manifest.json"));
            if ((payload & 1) != 0) Put(Path.Combine(output, "TiaOpenness.Bridge.exe"));
            if ((payload & 2) != 0) Put(Path.Combine(output, "bridge", "TiaOpenness.Bridge.exe"));
            if ((payload & 4) != 0) Put(Path.GetFullPath(Path.Combine(output, @"..\..\..\..\Bridge\bin\Debug\net48\TiaOpenness.Bridge.exe")));
            if ((payload & 8) != 0) Put(Path.GetFullPath(Path.Combine(output, @"..\..\..\..\Bridge\bin\Release\net48\TiaOpenness.Bridge.exe")));
            foreach (string key in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
            {
                if ((payload & 1) != 0) Put(Path.Combine(output, "adapters", "v" + key, "TiaOpenness.Openness.dll"));
                foreach (string spelling in new[] { output, output.Replace('\\', '/'), output.ToUpperInvariant() })
                foreach (string suffix in new[] { "", "\\", "/" })
                {
                    string directory = spelling + suffix;
                    Assert.Equal(StudioLookupBaseline.LocateBridge(directory), LocateBridge(directory));
                    var expected = StudioLookupBaseline.AdapterPath(directory, key);
#if TIA_SHARED_ADAPTER_PATHS
                    expected = Path.Combine(directory, "adapters", "v" + key, "TiaMcp.Adapter." + key + ".dll");
#endif
                    Assert.Equal(expected, SessionFactoryLoader.AdapterPath(directory, key));
                }
            }
        }
    }

    public void Dispose()
    {
        Assert.Equal(new DirectoryInfo(Path.GetTempPath()).FullName.TrimEnd(Path.DirectorySeparatorChar),
            new DirectoryInfo(scratch).Parent!.FullName.TrimEnd(Path.DirectorySeparatorChar));
        Directory.Delete(scratch, true);
    }
}

// Frozen pre-G7-5 lookup bodies; process-global inputs are parameters.
internal static class StudioLookupBaseline
{
    internal static string? LocateBridge(string baseDir)
    {
        var candidates = new[]
        {
            Path.Combine(baseDir, "TiaOpenness.Bridge.exe"),
            Path.Combine(baseDir, "bridge", "TiaOpenness.Bridge.exe"),
            Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\Bridge\bin\Debug\net48\TiaOpenness.Bridge.exe")),
            Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\Bridge\bin\Release\net48\TiaOpenness.Bridge.exe")),
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }

    internal static string AdapterPath(string baseDirectory, string key)
    {
        string path = Path.Combine(baseDirectory, "adapters", "v" + key, "TiaOpenness.Openness.dll");
        return path;
    }
}
