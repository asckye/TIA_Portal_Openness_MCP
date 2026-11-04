using System.IO.Compression;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using TiaMcp.Logic.ModelContextProtocol;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class Round2DecoderRuntimeTests
{
    private static Options Args(params string[] args) => Options.Parse(args, new HashSet<string>());

    [Fact]
    public void Delivered_runtime_contains_only_traced_imports_and_original_licenses()
    {
        string directory = TiaMcp.Engine.Tests.Shared.DecoderRuntimeFixture.Ensure();
        Assert.Equal(DecoderRuntime.RequiredFiles.Append("runtime.json").Order(), Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).Order());
        using var archive = ZipFile.OpenRead(Path.Combine(directory, "stdlib.zip"));
        Assert.Equal(DecoderRuntime.StandardLibraryFiles, archive.Entries.Select(e => e.FullName));
        Assert.Contains("Microsoft", File.ReadAllText(Path.Combine(directory, "LICENSE.txt")));
        Assert.Contains("Expat", File.ReadAllText(Path.Combine(directory, "THIRD-PARTY-LICENSES.html")));
        Assert.Contains("zlib", File.ReadAllText(Path.Combine(directory, "THIRD-PARTY-LICENSES.html")));
    }

    [Fact]
    public void Delivery_validation_checks_an_existing_runtime_receipt()
    {
        string root = Path.Combine(Repository.FindRoot(), "bin-build", "decoder-receipt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, DecoderRuntime.RelativeDirectory));
        try
        {
            File.WriteAllText(Path.Combine(root, DecoderRuntime.RelativeDirectory, "runtime.json"), "{}");
            Directory.CreateDirectory(Path.Combine(root, "runtime"));
            foreach (var relative in new[] { "manifest/package-manifest.json", "templates/project-blueprints/full_plc_hmi_project.json" })
            {
                var path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(Path.Combine(Repository.FindRoot(), relative), path);
            }
            Prepare.WriteJson(Path.Combine(root, Prepare.InventoryPath), new { files = Prepare.Inventory(root).Select(row => new { path = row.Key, sha256 = row.Value }) });
            var errors = Prepare.DeliveryErrors(root, DeliveryRules.Load(Repository.FindRoot()));
            Assert.Contains("Bundled decoder runtime is missing or invalid. Repair the installation.", errors);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Decoder_preparation_requires_explicit_absolute_pinned_input()
    {
        Assert.Throws<ReleaseException>(() => ReleaseCommands.PrepareDecoderPython(Args()));
        Assert.Throws<ReleaseException>(() => ReleaseCommands.PrepareDecoderPython(Args("-Python", "python.exe")));
        string root = Path.Combine(Repository.FindRoot(), "bin-build", "decoder-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string wrong = Path.Combine(root, "python.exe"); File.WriteAllText(wrong, "untrusted executable must never run");
            Assert.Contains("SHA-256", Assert.Throws<ReleaseException>(() => ReleaseCommands.PrepareDecoderPython(Args("-Python", wrong))).Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Runtime_preparation_is_clean_reproducible_and_contains_only_traced_imports()
    {
        string root = Path.Combine(Repository.FindRoot(), "bin-build", "decoder-repro-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source"), bundle = Path.Combine(root, "bundle");
        Directory.CreateDirectory(source); Directory.CreateDirectory(bundle);
        try
        {
            string installed = TiaMcp.Engine.Tests.Shared.DecoderRuntimeFixture.Ensure();
            foreach (string file in Directory.EnumerateFiles(installed)) File.Copy(file, Path.Combine(source, Path.GetFileName(file)));
            Directory.CreateDirectory(Path.Combine(source, "Doc/html"));
            File.Copy(Path.Combine(source, "THIRD-PARTY-LICENSES.html"), Path.Combine(source, "Doc/html/license.html"));
            ZipFile.ExtractToDirectory(Path.Combine(source, "stdlib.zip"), Path.Combine(source, "Lib"));
            Directory.CreateDirectory(Path.Combine(source, "DLLs"));
            foreach (string file in Directory.EnumerateFiles(source, "*.pyd")) File.Move(file, Path.Combine(source, "DLLs", Path.GetFileName(file)));
            foreach (string name in new[] { "_tkinter.pyd", "_testcapi.pyd", "_msi.pyd", "winsound.pyd", "_sqlite3.pyd", "tcl86t.dll", "tk86t.dll" })
                File.WriteAllText(Path.Combine(source, "DLLs", name), "forbidden source extra");
            var options = Args("-Python", Path.Combine(source, "python.exe"));
            Assert.Equal(0, ReleaseCommands.PrepareDecoderPython(options, bundle));
            string output = Path.Combine(bundle, DecoderRuntime.RelativeDirectory);
            var first = Directory.GetFiles(output).ToDictionary(Path.GetFileName, DecoderRuntime.Hash);
            File.WriteAllText(Path.Combine(output, "stale.pyd"), "stale output");
            Directory.CreateDirectory(Path.Combine(output, "stale-directory"));
            File.WriteAllText(Path.Combine(output, "stale-directory/extra"), "stale output");
            Assert.Equal(0, ReleaseCommands.PrepareDecoderPython(options, bundle));
            var second = Directory.GetFiles(output).ToDictionary(Path.GetFileName, DecoderRuntime.Hash);
            Assert.Equal(first.OrderBy(p => p.Key), second.OrderBy(p => p.Key));
            Assert.Equal(DecoderRuntime.RequiredFiles.Append("runtime.json").Order(), second.Keys.Order());
            Assert.Empty(Directory.GetDirectories(output));
            using (var archive = ZipFile.OpenRead(Path.Combine(output, "stdlib.zip")))
                Assert.Equal(DecoderRuntime.StandardLibraryFiles, archive.Entries.Select(e => e.FullName));
            Assert.Equal(File.ReadAllBytes(Path.Combine(source, "LICENSE.txt")), File.ReadAllBytes(Path.Combine(output, "LICENSE.txt")));
            Assert.Equal(File.ReadAllBytes(Path.Combine(source, "Doc/html/license.html")), File.ReadAllBytes(Path.Combine(output, "THIRD-PARTY-LICENSES.html")));
            Assert.Equal(Path.Combine(output, "python.exe"), DecoderRuntime.Require(bundle));
            var receipt = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "runtime.json")))!;
            receipt["version"] = "3.12.9";
            File.WriteAllText(Path.Combine(output, "runtime.json"), receipt.ToJsonString());
            Assert.Throws<DecoderRuntimeException>(() => DecoderRuntime.Require(bundle));
            receipt["version"] = DecoderRuntime.Version;
            File.WriteAllText(Path.Combine(output, "runtime.json"), receipt.ToJsonString());
            File.AppendAllText(Path.Combine(output, "stdlib.zip"), "tampered");
            Assert.Throws<DecoderRuntimeException>(() => DecoderRuntime.Require(bundle));
        }
        finally { Directory.Delete(root, true); }
    }
}
