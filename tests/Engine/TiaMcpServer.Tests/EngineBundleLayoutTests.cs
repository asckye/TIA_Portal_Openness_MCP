using System;
using System.IO;
using System.Text.Json.Nodes;
using TiaMcpServer.Cli;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;
using TiaOpenness.Shared;
using TiaOpenness.Shared.Tests;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class EngineBundleLayoutTests : IDisposable
    {
        private readonly string scratch = Path.Combine(Path.GetTempPath(), "engine layout 中文 " + Guid.NewGuid().ToString("N"));
        private static string At(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        private static void Put(string root, string relative)
        {
            string path = At(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
        }

        [Theory]
        [MemberData(nameof(BundleLayoutTests.Anchors), MemberType = typeof(BundleLayoutTests))]
        public void Callers_use_only_the_selected_root(string anchor)
        {
            Put(scratch, "manifest/package-manifest.json"); Put(scratch, "manifest/delivery.json");
            Directory.CreateDirectory(At(scratch, "templates"));
            string output = Directory.CreateDirectory(At(scratch, anchor)).FullName;
            Assert.Equal(anchor.StartsWith("runtime/v", StringComparison.Ordinal) ? scratch : null, McpServer.FindInstallRoot(output));
            Assert.Equal(scratch.Replace('\\', '/'), SpecLoader.FindBundleRoot(output));
            Assert.Equal(scratch, EcosystemFiles.RepositoryRoot(output, null));
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
        public void Sibling_routes_use_the_product_names_and_fail_closed(string release)
        {
            Put(scratch, "manifest/package-manifest.json");
            string directory = Directory.CreateDirectory(At(scratch, "runtime/v21")).FullName;
            string own = Path.Combine(directory, "TiaMcp.Engine.V21.exe");
            string expected = BundleLayout.EngineExecutablePath(scratch, release, directory);
            Assert.Equal(expected, Assert.Throws<BundleResourceUnavailableException>(() => BundleLayout.RequireEngine(release, directory)).Resource);
            Put(scratch, expected.Substring(scratch.Length + 1).Replace('\\', '/'));
            Assert.Equal(expected, BundleLayout.RequireEngine(release, directory));
            if (release == "14sp1" || release == "15.1") return;
            int version = int.Parse(release);
            Assert.Equal(release == "21" ? null : expected, EngineRouter.FindSiblingExe(version, () => own));
            File.Delete(expected);
            Assert.Null(EngineRouter.FindSiblingExe(version, () => own));
        }

        [Theory]
        [InlineData("Release", "bin", "20")]
        [InlineData("Release", "bin-v20", "21")]
        [InlineData("Debug", "bin", "20")]
        [InlineData("Debug", "bin-v20", "21")]
        [InlineData("Release", "bin", "19")]
        [InlineData("Debug", "bin-v20", "19")]
        public void Development_routing_keeps_the_formal_configuration(string configuration, string bin, string release)
        {
            Put(scratch, "manifest/package-manifest.json");
            string directory = Directory.CreateDirectory(At(scratch, "src/Engine/" + bin + "/" + configuration + "/net48")).FullName;
            string expected = At(scratch, release == "19" ? "src/FoundationHost/bin/" + configuration + "/net10.0/TiaMcp.FoundationHost.exe"
                : "src/Engine/" + (release == "20" ? "bin-v20" : "bin") + "/" + configuration + "/net48/TiaMcp.Engine.V" + release + ".exe");
            Assert.Equal(expected, BundleLayout.EngineExecutablePath(scratch, release, directory));
            string spelled = scratch + Path.DirectorySeparatorChar + "." + Path.DirectorySeparatorChar;
            Assert.Equal(expected, Path.GetFullPath(BundleLayout.EngineExecutablePath(spelled, release, directory)));
        }

        [Theory]
        [InlineData("relative")]
        [InlineData("")]
        [InlineData(" ")]
        public void Caller_overrides_cannot_silently_select_an_anchor(string invalid)
        {
            Put(scratch, "manifest/package-manifest.json");
            string directory = Directory.CreateDirectory(At(scratch, "runtime/v21")).FullName;
            Assert.Throws<ArgumentException>(() => SpecLoader.FindBundleRoot(directory, invalid));
            Assert.Throws<ArgumentException>(() => McpServer.FindInstallRoot(directory, invalid));
            Assert.Throws<ArgumentException>(() => EcosystemFiles.RepositoryRoot(directory, invalid));
            Assert.Throws<ArgumentException>(() => EngineRouter.FindSiblingExe(20, () => Path.Combine(directory, "engine.exe"), invalid));
        }

        [Fact]
        public void Missing_resources_do_not_reach_an_outer_bundle()
        {
            Put(scratch, "manifest/package-manifest.json"); Put(scratch, "manifest/delivery.json");
            Directory.CreateDirectory(At(scratch, "templates"));
            string stage = At(scratch, "stage"); Put(stage, "manifest/package-manifest.json");
            string directory = Directory.CreateDirectory(At(stage, "runtime/v21")).FullName;
            Assert.Equal(At(stage, "templates"), Assert.Throws<BundleResourceUnavailableException>(() => SpecLoader.FindBundleRoot(directory)).Resource);
            Assert.Equal(At(stage, "manifest/delivery.json"), Assert.Throws<BundleResourceUnavailableException>(() => McpServer.FindInstallRoot(directory)).Resource);
            Assert.Null(EngineRouter.FindSiblingExe(20, () => Path.Combine(directory, "engine.exe")));
        }

        [Fact]
        public void Unknown_outputs_cannot_route_without_an_explicit_bundle()
        {
            Put(scratch, "manifest/package-manifest.json");
            Put(scratch, "runtime/v20/TiaMcp.Engine.V20.exe");
            string directory = Directory.CreateDirectory(At(scratch, "custom/bin/Release/net48")).FullName;
            string own = Path.Combine(directory, "engine.exe");
            Assert.Null(EngineRouter.FindSiblingExe(20, () => own));
            Assert.Equal(At(scratch, "runtime/v20/TiaMcp.Engine.V20.exe"), EngineRouter.FindSiblingExe(20, () => own, scratch));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Spec_tokens_validate_the_selected_resource_in_json_and_yaml(bool json)
        {
            Put(scratch, "manifest/package-manifest.json"); Put(scratch, "templates/A 空格.scl");
            string directory = Directory.CreateDirectory(At(scratch, "runtime/v21")).FullName;
            string text = json ? "{\"source\":\"__BUNDLE__\\\\templates\\\\A 空格.scl\"}" : "source: '__BUNDLE__/templates/A 空格.scl'";
            string resolved = SpecLoader.ResolveBundleToken(text, directory);
            string value = json ? JsonNode.Parse(resolved)!["source"]!.GetValue<string>() : resolved;
            Assert.Contains(scratch.Replace('\\', '/') + "/templates/A 空格.scl", value.Replace('\\', '/'));
            File.Delete(At(scratch, "templates/A 空格.scl"));
            Assert.Equal(At(scratch, "templates/A 空格.scl"), Path.GetFullPath(Assert.Throws<BundleResourceUnavailableException>(
                () => SpecLoader.ResolveBundleToken(text, directory)).Resource));
            Assert.Throws<ArgumentException>(() => SpecLoader.ResolveBundleToken("\"__BUNDLE__/../outside\"", directory));
        }

        public void Dispose() { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }
}
