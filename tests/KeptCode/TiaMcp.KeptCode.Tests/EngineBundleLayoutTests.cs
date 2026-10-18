using System;
using System.IO;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;
using TiaOpenness.Shared;
using TiaOpenness.Shared.Tests;
using Xunit;

namespace TiaMcp.Engine.Tests
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
            string expected = At(scratch, "src/FoundationHost/bin/" + configuration + "/net10.0/TiaMcp.FoundationHost.exe");
            Assert.Equal(expected, BundleLayout.EngineExecutablePath(scratch, release, directory));
            string spelled = scratch + Path.DirectorySeparatorChar + "." + Path.DirectorySeparatorChar;
            Assert.Equal(expected, Path.GetFullPath(BundleLayout.EngineExecutablePath(spelled, release, directory)));
        }

        public void Dispose() { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }
}
