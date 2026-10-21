using System;
using System.Collections.Generic;
using System.IO;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcp.Updater.Tests
{
    // CheckProductUpdate and the updater describe an installed bundle only. A checkout's own runtime/vK copy (release
    // chain, check-all) answers like the bin layout; a package, with or without Version.props, keeps its install root.
    public sealed class InstallRootLayoutTests : IDisposable
    {
        private readonly string scratch = Path.Combine(Path.GetTempPath(), "install root 中文 " + Guid.NewGuid().ToString("N"));
        private static string At(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        private static void Put(string root, string relative)
        {
            string path = At(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "fixture");
        }
        private string Host(string release) => Directory.CreateDirectory(At(scratch, "runtime/v" + release)).FullName;

        public static IEnumerable<object[]> Releases()
        {
            foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
                yield return new object[] { release };
        }

        [Theory]
        [MemberData(nameof(Releases))]
        public void Packaged_root_keeps_its_install_root_and_updater(string release)
        {
            Put(scratch, "manifest/package-manifest.json"); Put(scratch, "manifest/delivery.json"); Put(scratch, "runtime/tools/TiaMcp.Updater.exe");
            string host = Host(release);
            Assert.Equal(scratch, BundleLayout.FindInstallRoot(host, scratch));
            Assert.Equal(At(scratch, "runtime/tools/TiaMcp.Updater.exe"), BundleLayout.RequirePath(BundleLayout.FindInstallRoot(host, scratch), "runtime/tools/TiaMcp.Updater.exe"));
            // The packaged updater identifies a package even where a Version.props is present.
            Put(scratch, "Version.props");
            Assert.Equal(scratch, BundleLayout.FindInstallRoot(host, scratch));
        }

        [Theory]
        [MemberData(nameof(Releases))]
        public void Package_without_its_updater_still_reports_the_missing_resource(string release)
        {
            Put(scratch, "manifest/package-manifest.json"); Put(scratch, "manifest/delivery.json");
            string host = Host(release);
            Assert.Equal(scratch, BundleLayout.FindInstallRoot(host, scratch));
            string updater = At(scratch, "runtime/tools/TiaMcp.Updater.exe");
            Assert.Equal(updater, Assert.Throws<BundleResourceUnavailableException>(() => BundleLayout.RequirePath(scratch, "runtime/tools/TiaMcp.Updater.exe")).Resource);
            File.Delete(At(scratch, "manifest/delivery.json"));
            Assert.Equal(At(scratch, "manifest/delivery.json"), Assert.Throws<BundleResourceUnavailableException>(() => BundleLayout.FindInstallRoot(host, scratch)).Resource);
        }

        [Theory]
        [MemberData(nameof(Releases))]
        public void Checkout_runtime_layout_answers_like_the_bin_layout(string release)
        {
            Put(scratch, "manifest/package-manifest.json"); Put(scratch, "manifest/delivery.json"); Put(scratch, "Version.props");
            Put(scratch, "runtime/tools/TiaMcp.WriteGuard.exe"); Put(scratch, "runtime/tools/TiaMcp.PluginLauncher.exe");
            string host = Host(release);
            Assert.Null(BundleLayout.FindInstallRoot(host, scratch));
            Assert.Null(BundleLayout.FindInstallRoot(Directory.CreateDirectory(At(scratch, "src/FoundationHost/bin/Release/net10.0")).FullName, scratch));
            File.Delete(At(scratch, "manifest/delivery.json"));
            Assert.Null(BundleLayout.FindInstallRoot(host, scratch));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Bin_layout_and_worker_directories_never_report_an_install_root(bool packaged)
        {
            Put(scratch, "manifest/package-manifest.json"); Put(scratch, "manifest/delivery.json");
            if (packaged) Put(scratch, "runtime/tools/TiaMcp.Updater.exe"); else Put(scratch, "Version.props");
            foreach (string configuration in new[] { "Release", "Debug" })
                Assert.Null(BundleLayout.FindInstallRoot(Directory.CreateDirectory(At(scratch, "src/FoundationHost/bin/" + configuration + "/net10.0")).FullName, scratch));
            Assert.Null(BundleLayout.FindInstallRoot(Directory.CreateDirectory(At(scratch, "runtime/v21/worker")).FullName, scratch));
        }

        public void Dispose() { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }
}
