using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using TiaMcpServer.Cli;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class EngineBundleLayoutTests : IDisposable
    {
        private readonly string scratch = Path.Combine(Path.GetTempPath(), "engine layout 中文 " + Guid.NewGuid().ToString("N"));

        public EngineBundleLayoutTests() { Directory.CreateDirectory(scratch); }

        private static string At(string root, string relative)
            => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

        private static void Put(string root, string relative)
        {
            string file = At(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "fixture");
        }

        private static void Bundle(string root, bool manifest = true, bool delivery = true, bool templates = true, bool tools = true)
        {
            Directory.CreateDirectory(root);
            if (manifest) Put(root, "manifest/package-manifest.json");
            if (delivery) Put(root, "manifest/delivery.json");
            if (templates) Directory.CreateDirectory(At(root, "templates"));
            if (tools) Directory.CreateDirectory(At(root, "src"));
            foreach (string version in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
                Put(root, "runtime/v" + version + "/TiaMcpServer.exe");
        }

        private static void DevelopmentEngines(string root)
        {
            foreach (string bin in new[] { "bin", "bin-v20" })
            foreach (string configuration in new[] { "Release", "Debug" })
                Put(root, "src/Engine/" + bin + "/" + configuration + "/net48/TiaMcpServer.exe");
        }

        private static void EqualLookup(Func<string?> before, Func<string?> after)
        {
            string? oldValue = null, newValue = null;
            var oldError = Record.Exception(() => oldValue = before());
            var newError = Record.Exception(() => newValue = after());
            Assert.Equal(oldError?.GetType(), newError?.GetType());
            Assert.Equal(oldError?.Message, newError?.Message);
            Assert.Equal(oldValue, newValue);
        }

        private void Compare(string root, string anchor)
        {
            string output = Directory.CreateDirectory(At(root, anchor)).FullName;
            string configured = At(scratch, "override root 中文");
            Bundle(configured);
            DevelopmentEngines(configured);
            string invalid = At(scratch, "invalid override");
            Directory.CreateDirectory(invalid);

            // These three probes historically ignore TIA_MCP_REPOSITORY_ROOT. Pass it as an
            // input to both implementations, without changing the test process environment.
            foreach (string? repositoryRoot in new[] { null, "", " ", configured, configured + "/", invalid, "relative", "\0" })
            {
                foreach (string spelling in new[] { output, output.Replace('\\', '/'), At(root, anchor.ToUpperInvariant()) })
                foreach (string suffix in new[] { "", Path.DirectorySeparatorChar.ToString(), "/" })
                {
                    string directory = spelling + suffix;
                    EqualLookup(() => OldInstallRoot(directory, repositoryRoot), () => McpServer.FindInstallRoot(directory, repositoryRoot));
                    string? bundle = TiaOpenness.Shared.BundleLayout.FindRoot(directory);
                    if (bundle != null && Directory.Exists(At(bundle, "templates")))
                        Assert.Equal(bundle.Replace('\\', '/'), SpecLoader.FindBundleRoot(directory, repositoryRoot));
                    else
                        EqualLookup(() => OldBundleRoot(directory, repositoryRoot), () => SpecLoader.FindBundleRoot(directory, repositoryRoot));
                    foreach (string name in new[] { "TiaMcpServer.exe", "tIaMcPsErVeR.ExE", "missing.exe" })
                    foreach (int version in new[] { 14, 15, 16, 17, 18, 19, 20, 21, 22 })
                    {
                        string own = Path.Combine(directory, name);
                        EqualLookup(() => OldSiblingExe(version, () => own, repositoryRoot),
                            () => EngineRouter.FindSiblingExe(version, () => own, repositoryRoot));
                    }
                }

                foreach (string? directory in new[] { null, "", " ", "\0", "relative" })
                {
                    EqualLookup(() => OldInstallRoot(directory!, repositoryRoot), () => McpServer.FindInstallRoot(directory!, repositoryRoot));
                    EqualLookup(() => OldBundleRoot(directory!, repositoryRoot), () => SpecLoader.FindBundleRoot(directory!, repositoryRoot));
                    EqualLookup(() => OldSiblingExe(20, () => directory!, repositoryRoot),
                        () => EngineRouter.FindSiblingExe(20, () => directory!, repositoryRoot));
                }
                foreach (int version in new[] { 20, 22 })
                {
                    int oldCalls = 0, newCalls = 0;
                    EqualLookup(() => OldSiblingExe(version, () => { oldCalls++; throw new IOException("Process path unavailable."); }, repositoryRoot),
                        () => EngineRouter.FindSiblingExe(version, () => { newCalls++; throw new IOException("Process path unavailable."); }, repositoryRoot));
                    Assert.Equal(version == 20 ? 1 : 0, oldCalls);
                    Assert.Equal(oldCalls, newCalls);
                }
            }
        }

        [Theory]
        [InlineData("runtime/v14sp1")]
        [InlineData("runtime/v15.1")]
        [InlineData("runtime/v16")]
        [InlineData("runtime/v17")]
        [InlineData("runtime/v18")]
        [InlineData("runtime/v19")]
        [InlineData("runtime/v20")]
        [InlineData("runtime/v21")]
        [InlineData("runtime/studio")]
        [InlineData("runtime/studio/bridge")]
        [InlineData("src/Engine/bin/Release/net48")]
        [InlineData("src/Engine/bin/Debug/net48")]
        [InlineData("src/Engine/bin-v20/Release/net48")]
        [InlineData("src/Engine/bin-v20/Debug/net48")]
        [InlineData("src/Studio/Gui/bin/Release/net10.0-windows")]
        [InlineData("src/Studio/Gui/bin/Debug/net10.0-windows")]
        [InlineData("src/Studio/Gui/bin/Release/net10.0-windows/bridge")]
        [InlineData("src/Studio/Gui/bin/Debug/net10.0-windows/bridge")]
        [InlineData("src/Studio/Bridge/bin/Release/net48")]
        [InlineData("src/Studio/Bridge/bin/Debug/net48")]
        public void Supported_anchors_match_the_original_probes(string anchor)
        {
            string root = At(scratch, "bundle");
            Bundle(root);
            DevelopmentEngines(root);
            Compare(root, anchor);
        }

        [Theory]
        [InlineData("bundle")]
        [InlineData("repository-runtime")]
        [InlineData("worktree")]
        [InlineData("ci")]
        public void Repository_layouts_match_the_original_probes(string layout)
        {
            string root = At(scratch, layout);
            Bundle(root);
            DevelopmentEngines(root);
            if (layout == "repository-runtime") Directory.CreateDirectory(At(root, ".git"));
            if (layout == "worktree") Put(root, ".git");
            Compare(root, "runtime/v21");
            Compare(root, "src/Engine/bin/Release/net48");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        [InlineData(10)]
        [InlineData(11)]
        [InlineData(12)]
        [InlineData(13)]
        [InlineData(14)]
        [InlineData(15)]
        public void Runtime_only_and_missing_resources_keep_the_original_result(int resources)
        {
            string root = At(scratch, "missing resources");
            Bundle(root, (resources & 1) != 0, (resources & 2) != 0, (resources & 4) != 0, (resources & 8) != 0);
            Compare(root, "runtime/v21");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        public void Nested_staging_keeps_the_original_fallback(int resources)
        {
            string root = At(scratch, "repository");
            Bundle(root);
            string staging = At(root, "bin-build/staging");
            Bundle(staging, (resources & 1) != 0, (resources & 2) != 0, (resources & 4) != 0, (resources & 4) != 0);
            Compare(staging, "runtime/v21");
        }

        [Theory]
        [InlineData("elsewhere/runtime/v21")]
        [InlineData("runtime/v22")]
        [InlineData("runtime/v21/plugins")]
        [InlineData("src/Engine/bin/Custom/net48")]
        [InlineData("src/Engine/bin/Release/net10.0")]
        [InlineData("tools/other/bin/Release/net48")]
        [InlineData("custom/a/b")]
        [InlineData("custom/a/b/c/d/e/f/g/h/i/j/k/l")]
        public void Unrecognized_outputs_keep_the_original_probe_depths(string anchor)
        {
            string root = At(scratch, "repository");
            Bundle(root);
            DevelopmentEngines(root);
            Compare(root, anchor);
        }

        [Fact]
        public void Spec_templates_resolve_in_a_delivery_without_tools_or_ancestor_resources()
        {
            string outer = At(scratch, "repository");
            Bundle(outer);
            string delivery = At(outer, "stage");
            Bundle(delivery, tools: false);
            Assert.False(Directory.Exists(At(delivery, "src")));
            foreach (string anchor in new[] { "runtime/v20", "runtime/v21" })
                Assert.Equal(delivery.Replace('\\', '/'), SpecLoader.FindBundleRoot(At(delivery, anchor), outer));
            // Unknown layouts still use the original upward probe.
            string custom = Directory.CreateDirectory(At(delivery, "custom/bin")).FullName;
            Assert.Equal(outer.Replace('\\', '/'), SpecLoader.FindBundleRoot(custom));
            Directory.Delete(At(delivery, "templates"));
            Assert.Equal(outer.Replace('\\', '/'), SpecLoader.FindBundleRoot(At(delivery, "runtime/v21")));
        }

        // Frozen pre-G7-4 bodies; only signatures and process-global inputs are parameters.
        private static string? OldInstallRoot(string baseDirectory, string? repositoryRoot)
        {
            try
            {
                var dir = new DirectoryInfo(baseDirectory);
                for (int i = 0; i < 4 && dir != null; i++, dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, "manifest", "delivery.json"))) return dir.FullName;
            }
            catch { }
            return null;
        }

        private static string? OldBundleRoot(string baseDirectory, string? repositoryRoot)
        {
            var dir = new DirectoryInfo(baseDirectory);
            for (int i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "templates")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "src")))
                    return dir.FullName.Replace('\\', '/');
            return null;
        }

        private static string? OldSiblingExe(int version, Func<string> ownExePath, string? repositoryRoot)
        {
            var target = TiaMcp.Versioning.TiaVersionCatalog.RequireRunnable(version);
            try
            {
                string own = ownExePath();
                string exeName = Path.GetFileName(own);
                string dir = Path.GetDirectoryName(own) ?? "";
                var candidates = new List<string>();

                var m = Regex.Match(dir, @"^(.*)[\\/]bin(-v20)?[\\/]Release[\\/]net48$", RegexOptions.IgnoreCase);
                if (m.Success && target.IsFullEngine)
                {
                    string binDir = target.EngineOutputDirectory;
                    candidates.Add(Path.Combine(m.Groups[1].Value, binDir, "Release", "net48", exeName));
                }

                var parent = new DirectoryInfo(dir);
                if (parent.Parent != null && Regex.IsMatch(parent.Name, @"^v\d+$", RegexOptions.IgnoreCase))
                {
                    candidates.Add(Path.Combine(parent.Parent.FullName, target.RuntimeDirectory, exeName));
                }

                foreach (var c in candidates)
                {
                    if (File.Exists(c) &&
                        !string.Equals(Path.GetFullPath(c), Path.GetFullPath(own), StringComparison.OrdinalIgnoreCase))
                    {
                        return c;
                    }
                }
            }
            catch
            {
                // Lookup failure is returned to the caller, which must fail closed.
            }
            return null;
        }

        public void Dispose()
        {
            string full = Path.GetFullPath(scratch);
            Assert.Equal(new DirectoryInfo(Path.GetTempPath()).FullName.TrimEnd(Path.DirectorySeparatorChar),
                new DirectoryInfo(full).Parent!.FullName.TrimEnd(Path.DirectorySeparatorChar));
            Directory.Delete(full, true);
        }
    }
}
