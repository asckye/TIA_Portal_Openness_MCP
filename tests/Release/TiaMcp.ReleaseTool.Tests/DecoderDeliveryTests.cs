using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class DecoderDeliveryTests
{
    [Fact]
    public void Decoder_extensions_and_license_bytes_are_bound_to_the_release_inventory()
    {
        string root = Path.Combine(Repository.FindRoot(), "bin-build", "decoder-inventory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "runtime/decoder-python"));
        try
        {
            string[] names = { "stdlib.zip", "python._pth", "_elementtree.pyd", "THIRD-PARTY-LICENSES.html" };
            foreach (string name in names) File.WriteAllText(Path.Combine(root, "runtime/decoder-python", name), name);
            var before = ReleaseRecords.GetRuntimeFiles(root);
            Assert.Equal(names.Order(), before.Select(row => Path.GetFileName(row.Path)).Order());
            File.WriteAllText(Path.Combine(root, "runtime/decoder-python/stdlib.zip"), "changed archive");
            var after = ReleaseRecords.GetRuntimeFiles(root);
            Assert.NotEqual(before.Single(row => row.Path.EndsWith("stdlib.zip")).Sha256,
                after.Single(row => row.Path.EndsWith("stdlib.zip")).Sha256);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Decoder_runtime_and_original_license_are_required_delivery_outputs()
    {
        var rules = DeliveryRules.Load(Repository.FindRoot());
        foreach (string file in new[] { "python.exe", "python._pth", "stdlib.zip", "LICENSE.txt", "runtime.json", "THIRD-PARTY-LICENSES.html" })
        {
            string path = "runtime/decoder-python/" + file;
            Assert.True(rules.Delivered(path));
            Assert.Contains(path, DeliveryRules.Strings(rules.Data["requiredFiles"]));
        }
        Assert.Contains("prepare-decoder-python", CommandLine.Commands);
    }
}
