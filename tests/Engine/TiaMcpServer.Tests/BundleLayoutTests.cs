using System;
using System.Collections.Generic;
using System.IO;
using TiaOpenness.Shared;
using Xunit;

namespace TiaOpenness.Shared.Tests
{
    // Linked into both test projects; all selection inputs are explicit.
    public sealed class BundleLayoutTests : IDisposable
    {
        private readonly string scratch = Path.Combine(Path.GetTempPath(), "bundle 空格 " + Guid.NewGuid().ToString("N"));
        public BundleLayoutTests() { Directory.CreateDirectory(scratch); }
        private static string At(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        private static void Put(string root, string relative)
        {
            string file = At(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "fixture");
        }
        private static string Output(string root, string relative) => Directory.CreateDirectory(At(root, relative)).FullName;
        private static void Bundle(string root) => Put(root, "manifest/package-manifest.json");

        public static IEnumerable<object[]> Anchors()
        {
            foreach (string key in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
                yield return new object[] { "runtime/v" + key };
            yield return new object[] { "" };
            foreach (string configuration in new[] { "Release", "Debug" })
            {
                foreach (string output in new[] { "bin", "bin-v20" })
                    yield return new object[] { "src/Engine/" + output + "/" + configuration + "/net48" };
                yield return new object[] { "src/FoundationHost/bin/" + configuration + "/net10.0" };
                yield return new object[] { "tests/Engine/TiaMcpServer.HttpTests/bin/" + configuration + "/net48" };
                yield return new object[] { "tests/Engine/TiaMcpServer.LegacyHostTests/bin/" + configuration + "/net10.0" };
                yield return new object[] { "tests/Engine/TiaMcpServer.Tests/bin/" + configuration + "/net10.0" };
                yield return new object[] { "src/Studio/Gui/bin/" + configuration + "/net10.0-windows" };
                yield return new object[] { "src/Studio/Gui/bin/" + configuration + "/net10.0-windows/bridge" };
                yield return new object[] { "src/Studio/Bridge/bin/" + configuration + "/net48" };
            }
            yield return new object[] { "runtime/studio" };
            yield return new object[] { "runtime/studio/bridge" };
        }

        [Theory]
        [MemberData(nameof(Anchors))]
        public void Only_formal_anchors_establish_a_root(string anchor)
        {
            Bundle(scratch);
            string output = Output(scratch, anchor);
            foreach (string suffix in new[] { "", Path.DirectorySeparatorChar.ToString(), "/" })
                Assert.Equal(scratch, BundleLayout.ResolveRoot(output + suffix, null!, null!));
        }

        public static IEnumerable<object[]> Matrix()
        {
            for (int inputs = 0; inputs < 8; inputs++) yield return new object[] { inputs };
        }

        [Theory]
        [MemberData(nameof(Matrix))]
        public void Cli_then_environment_then_anchor_for_every_combination(int inputs)
        {
            string anchor = Output(scratch, "anchor"), cli = Output(scratch, "CLI 中文"), environment = Output(scratch, "environment");
            if ((inputs & 1) != 0) Bundle(anchor);
            Bundle(cli); Bundle(environment);
            string output = Output(anchor, "runtime/v21");
            Assert.Equal((inputs & 4) != 0 ? cli : (inputs & 2) != 0 ? environment : (inputs & 1) != 0 ? anchor : null,
                BundleLayout.ResolveRoot(output, (inputs & 4) != 0 ? cli : null!, (inputs & 2) != 0 ? environment : null!));
        }

        [Theory]
        [InlineData("relative", true)]
        [InlineData("", true)]
        [InlineData(" ", true)]
        [InlineData("missing", false)]
        [InlineData("no-marker", false)]
        public void Invalid_cli_or_environment_never_falls_back(string value, bool syntax)
        {
            Bundle(scratch);
            string valid = Output(scratch, "valid"); Bundle(valid);
            string output = Output(scratch, "runtime/v20");
            string invalid = syntax ? value : At(scratch, value);
            if (value == "no-marker") Directory.CreateDirectory(invalid);
            foreach (bool cli in new[] { false, true })
            {
                var error = Record.Exception(() => BundleLayout.ResolveRoot(output, cli ? invalid : null!, cli ? valid : invalid));
                if (syntax) Assert.IsType<ArgumentException>(error);
                else Assert.Contains(At(invalid, "manifest/package-manifest.json"), Assert.IsType<BundleResourceUnavailableException>(error).Message);
            }
            Assert.Equal(valid, BundleLayout.ResolveRoot(output, valid, invalid));
        }

        [Theory]
        [InlineData("runtime/v22")]
        [InlineData("runtime/v21/plugins")]
        [InlineData("src/Engine/bin/Custom/net48")]
        [InlineData("src/Engine/bin/Release/net10.0")]
        [InlineData("tools/other/bin/Release/net48")]
        [InlineData("custom/a/b")]
        [InlineData("tests/Other/HttpTests/bin/Release/net48")]
        [InlineData("tests/Engine/TiaMcpServer.HttpTests/bin/Custom/net48")]
        [InlineData("tests/Engine/TiaMcpServer.HttpTests/bin/Release/net10.0")]
        [InlineData("tests/Engine/TiaMcpServer.LegacyHostTests/bin/Release/net48")]
        [InlineData("tests/Engine/TiaMcpServer.Tests/bin-v20/Release/net10.0")]
        public void Unknown_layouts_cannot_use_an_ancestor_bundle(string anchor)
        {
            Bundle(scratch);
            Assert.Null(BundleLayout.ResolveRoot(Output(scratch, anchor), null!, null!));
        }

        [Fact]
        public void Nested_staging_does_not_use_outer_resources()
        {
            Bundle(scratch); Put(scratch, "scripts/ecosystem/plc_tools_bridge.py");
            string stage = Output(scratch, "stage"); Bundle(stage);
            string output = Output(stage, "runtime/v21");
            Assert.Equal(stage, BundleLayout.ResolveRoot(output, null!, null!));
            var error = Assert.Throws<BundleResourceUnavailableException>(() => BundleLayout.RequireResource(BundleResource.PlcToolsBridge, output));
            Assert.Equal(At(stage, "scripts/ecosystem/plc_tools_bridge.py"), error.Resource);
        }

        [Fact]
        public void Every_resource_requires_the_expected_path_in_the_selected_root()
        {
            Bundle(scratch);
            string output = Output(scratch, "runtime/v20");
            foreach (BundleResource resource in Enum.GetValues(typeof(BundleResource)))
            {
                string relative = BundleLayout.RelativePath(resource);
                bool directory = resource == BundleResource.OpennessGuides || resource == BundleResource.Templates;
                if (directory) Output(scratch, relative); else Put(scratch, relative);
                Assert.Equal(At(scratch, relative), BundleLayout.RequireResource(resource, output));
                if (resource == BundleResource.PackageManifest) continue;
                if (directory) Directory.Delete(At(scratch, relative)); else File.Delete(At(scratch, relative));
                Assert.Equal(At(scratch, relative), Assert.Throws<BundleResourceUnavailableException>(
                    () => BundleLayout.RequireResource(resource, output)).Resource);
            }
            Assert.Throws<ArgumentException>(() => BundleLayout.RequirePath(scratch, "../outside"));
        }

        [Theory]
        [InlineData("help", "--bundle-root", "relative")]
        [InlineData("--bundle-root")]
        [InlineData("--bundle-root", "")]
        [InlineData("--bundle-root", "--profile", "full")]
        public void Root_option_rejects_syntax_before_selection(params string[] args)
            => Assert.Throws<ArgumentException>(() => BundleLayout.ExtractRootOption(args, out _));

        [Fact]
        public void Root_option_can_precede_or_follow_the_command_and_rejects_duplicates()
        {
            foreach (var args in new[] { new[] { "--bundle-root", scratch, "gen", "spec.json" }, new[] { "gen", "spec.json", "--bundle-root", scratch } })
            {
                Assert.Equal(scratch, BundleLayout.ExtractRootOption(args, out var remaining));
                Assert.Equal(new[] { "gen", "spec.json" }, remaining);
            }
            Assert.Throws<ArgumentException>(() => BundleLayout.ExtractRootOption(new[] { "--bundle-root", scratch, "--bundle-root", scratch }, out _));
        }

        [Fact]
        public void Resource_resolution_needs_only_read_access()
        {
            Bundle(scratch); Put(scratch, "manifest/delivery.json");
            string output = Output(scratch, "runtime/v21");
            string manifest = At(scratch, "manifest/package-manifest.json");
            File.SetAttributes(manifest, FileAttributes.ReadOnly);
            try
            {
                using (File.Open(manifest, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Assert.Equal(scratch, BundleLayout.ResolveRoot(output, null!, null!));
                    Assert.Equal(At(scratch, "manifest/delivery.json"), BundleLayout.RequirePath(scratch, "manifest/delivery.json"));
                    Assert.False(Directory.Exists(At(scratch, "data")));
                }
            }
            finally { File.SetAttributes(manifest, FileAttributes.Normal); }
        }

        [Fact]
        public void Studio_entry_point_keeps_the_original_optional_root_and_resource_policy()
        {
            Bundle(scratch);
            string output = Output(scratch, "runtime/studio");
            Assert.Equal(scratch, BundleLayout.FindRootForStudio(output));
            Assert.Equal(scratch, BundleLayout.FindRootForStudio(output, " "));
            Assert.Null(BundleLayout.FindRootForStudio(scratch));
            Assert.Null(BundleLayout.FindRootForStudio(output, At(scratch, "unmarked")));
            Assert.Null(BundleLayout.FindRootForStudio(output, "relative"));
            Assert.Null(BundleLayout.FindResourceForStudio(BundleResource.DeliveryManifest, output));
            Put(scratch, "manifest/delivery.json");
            Assert.Equal(At(scratch, "manifest/delivery.json"), BundleLayout.FindResourceForStudio(BundleResource.DeliveryManifest, output));
            foreach (string anchor in new[] { "src/FoundationHost/bin/Release/net10.0", "tests/Engine/TiaMcpServer.HttpTests/bin/Release/net48" })
                Assert.Null(BundleLayout.FindRootForStudio(Output(scratch, anchor)));
        }

        public void Dispose() => Directory.Delete(scratch, true);
    }
}
