using System;
using System.IO;
using TiaOpenness.Shared;
using Xunit;

namespace TiaOpenness.Shared.Tests
{
    // Linked into both test projects: exercises Logic/net10 and Core/net10 assemblies.
    public sealed class BundleLayoutTests : IDisposable
    {
        private readonly string scratch = Path.Combine(Path.GetTempPath(), "bundle 空格 " + Guid.NewGuid().ToString("N"));

        public BundleLayoutTests() { Directory.CreateDirectory(scratch); }

        private static string At(string root, string relative)
        {
            return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        private static void Put(string root, string relative)
        {
            string file = At(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "fixture");
        }

        private static string Output(string root, string relative)
        {
            return Directory.CreateDirectory(At(root, relative)).FullName;
        }

        private static void Bundle(string root)
        {
            Put(root, "manifest/package-manifest.json");
            Put(root, "scripts/ecosystem/plc_tools_bridge.py");
            Put(root, "reference/siemens-openness/skills/blocks/SKILL.md");
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
        public void Supported_anchors_preserve_old_root_and_document_IDs(string anchor)
        {
            Bundle(scratch);
            string output = Output(scratch, anchor);
            foreach (string suffix in new[] { "", Path.DirectorySeparatorChar.ToString(), "/" })
            {
                string root = BundleLayout.FindRoot(output + suffix);
                Assert.Equal(OldRepositoryRoot(output + suffix, null), root);
                string folder = BundleLayout.FindResource(BundleResource.OpennessGuides, output + suffix);
                Assert.Equal(Path.Combine(root, "reference", "siemens-openness", "skills"), folder);
                string document = Assert.Single(Directory.GetFiles(folder, "*.md", SearchOption.AllDirectories));
                Assert.Equal("blocks/SKILL.md", document.Substring(folder.Length + 1).Replace('\\', '/'));
            }
        }

        [Theory]
        [InlineData("bundle")]
        [InlineData("repository-runtime")]
        [InlineData("worktree")]
        [InlineData("ci")]
        public void Repository_metadata_and_runtime_directory_are_not_markers(string layout)
        {
            Bundle(scratch);
            if (layout == "repository-runtime") Directory.CreateDirectory(At(scratch, ".git"));
            if (layout == "worktree") Put(scratch, ".git");
            if (layout != "ci") Output(scratch, "runtime/v21");
            string output = Output(scratch, "src/Engine/bin/Release/net48");
            Assert.Equal(new DirectoryInfo(scratch).FullName, BundleLayout.FindRoot(output));
            if (layout == "ci") Assert.False(Directory.Exists(At(scratch, "runtime")));
        }

        [Fact]
        public void Explicit_override_has_priority_and_keeps_its_spelling()
        {
            Bundle(scratch);
            string output = Output(scratch, "runtime/v21");
            string configured = Output(scratch, "other root 中文");
            Bundle(configured);
            configured += Path.DirectorySeparatorChar + "." + Path.DirectorySeparatorChar;
            Assert.Equal(configured, BundleLayout.FindRoot(output, configured));
            Assert.Equal(Path.Combine(configured, "scripts", "ecosystem", "plc_tools_bridge.py"),
                BundleLayout.FindResource(BundleResource.PlcToolsBridge, output, configured));
            Assert.Equal(configured, BundleLayout.FindRoot(null!, configured));
            Assert.Null(BundleLayout.FindRoot(output, At(scratch, "missing")));
            Assert.Null(BundleLayout.FindRoot(output, "relative"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Staging_never_uses_enclosing_repository_resources(bool marker)
        {
            Bundle(scratch);
            string staging = Output(scratch, "bin-build/staging");
            string output = Output(staging, "runtime/v21");
            if (marker) Put(staging, "manifest/package-manifest.json");
            Assert.Equal(marker ? staging : null, BundleLayout.FindRoot(output));
            Assert.Null(BundleLayout.FindResource(BundleResource.PlcToolsBridge, output));
            // The frozen compatibility probe still reaches the outer repository;
            // the new resolver leaves that decision to its future caller.
            Assert.Equal(scratch, OldRepositoryRoot(output, null));
            if (marker)
            {
                Bundle(staging);
                Assert.Equal(OldRepositoryRoot(output, null), BundleLayout.FindRoot(output));
                Assert.Equal(At(staging, "scripts/ecosystem/plc_tools_bridge.py"),
                    BundleLayout.FindResource(BundleResource.PlcToolsBridge, output));
            }
        }

        [Fact]
        public void Runtime_only_copy_is_not_a_bundle()
        {
            string output = Output(scratch, "runtime/studio/bridge");
            Put(output, "TiaOpenness.Bridge.exe");
            Assert.Null(BundleLayout.FindRoot(output));
            Assert.Null(BundleLayout.FindResource(BundleResource.OpennessGuides, output));
        }

        [Theory]
        [InlineData("elsewhere/runtime/v21")]
        [InlineData("runtime/v22")]
        [InlineData("runtime/v21/plugins")]
        [InlineData("src/Engine/bin/Custom/net48")]
        [InlineData("src/Engine/bin/Release/net10.0")]
        [InlineData("tools/other/bin/Release/net48")]
        public void Stray_ancestor_marker_does_not_authorize_unknown_layouts(string anchor)
        {
            Bundle(scratch);
            string output = Output(scratch, anchor);
            Assert.Null(BundleLayout.FindRoot(output));
            Assert.Equal(scratch, OldRepositoryRoot(output, null));
        }

        [Fact]
        public void Each_resource_resolves_only_in_the_selected_root()
        {
            Bundle(scratch);
            string output = Output(scratch, "runtime/v20");
            foreach (BundleResource resource in Enum.GetValues(typeof(BundleResource)))
            {
                string relative = BundleLayout.RelativePath(resource);
                bool directory = resource == BundleResource.OpennessGuides || resource == BundleResource.Templates;
                if (directory) Output(scratch, relative); else Put(scratch, relative);
                Assert.Equal(At(scratch, relative), BundleLayout.FindResource(resource, output));
            }
            File.Delete(At(scratch, "reference/v21-ecosystem.json"));
            Output(scratch, "reference/v21-ecosystem.json");
            Assert.Null(BundleLayout.FindResource(BundleResource.V21EcosystemCatalog, output));
            Directory.Delete(At(scratch, "templates"));
            Put(scratch, "templates");
            Assert.Null(BundleLayout.FindResource(BundleResource.Templates, output));
        }

        [Fact]
        public void Missing_marker_and_invalid_inputs_leave_compatibility_fallback_available()
        {
            string output = Output(scratch, "runtime/v21");
            Put(scratch, "scripts/ecosystem/plc_tools_bridge.py");
            Assert.Null(BundleLayout.FindRoot(output));
            Assert.Equal(OldRepositoryRoot(output, null), BundleLayout.FindRoot(output) ?? OldRepositoryRoot(output, null));
            File.Delete(At(scratch, "scripts/ecosystem/plc_tools_bridge.py"));
            Assert.Equal("Companion files missing. Set TIA_MCP_REPOSITORY_ROOT to the source/distribution root.",
                Assert.Throws<DirectoryNotFoundException>(() => BundleLayout.FindRoot(output) ?? OldRepositoryRoot(output, null)).Message);
            Assert.Equal("TIA_MCP_REPOSITORY_ROOT must point to this source/distribution root.",
                Assert.Throws<DirectoryNotFoundException>(() => BundleLayout.FindRoot(output, scratch) ?? OldRepositoryRoot(output, scratch)).Message);
            Assert.Null(BundleLayout.FindRoot(null!));
            Assert.Null(BundleLayout.FindRoot(""));
            Assert.Null(BundleLayout.FindRoot("relative"));
        }

        // Frozen EcosystemFiles.RepositoryRoot body; only the process-global inputs
        // are parameters so temporary layouts do not modify the test host environment.
        private static string OldRepositoryRoot(string baseDirectory, string? configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (!Path.IsPathRooted(configured) || !File.Exists(Path.Combine(configured, "scripts", "ecosystem", "plc_tools_bridge.py")))
                    throw new DirectoryNotFoundException("TIA_MCP_REPOSITORY_ROOT must point to this source/distribution root.");
                return Path.GetFullPath(configured);
            }
            for (var dir = new DirectoryInfo(baseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "scripts", "ecosystem", "plc_tools_bridge.py"))) return dir.FullName;
            throw new DirectoryNotFoundException("Companion files missing. Set TIA_MCP_REPOSITORY_ROOT to the source/distribution root.");
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
