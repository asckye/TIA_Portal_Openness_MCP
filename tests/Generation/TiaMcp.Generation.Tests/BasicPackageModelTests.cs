using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TiaMcp.Logic.Generation;
using Xunit;

namespace TiaMcp.Generation.Tests
{
    public sealed class BasicPackageModelTests
    {
        private static string PackagePath => Path.Combine(AppContext.BaseDirectory, "BasicPackage");
        public static IEnumerable<object[]> Releases => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }.Select(r => new object[] { r });

        [Fact]
        public void BasicPackageInventoryAndLicenceSurviveCanonicalZipRoundtrip()
        {
            var package = StandardPackageLoader.LoadDirectory(PackagePath);
            Assert.Equal("MIT", package.Manifest.License);
            Assert.Equal(Directory.GetFiles(PackagePath, "*", SearchOption.AllDirectories).Length - 1, package.Manifest.Files.Count);
            Assert.Contains("Copyright (c) 2026 bulaofen0036-coder", Encoding.UTF8.GetString(package.ReadFile("LICENSE")));
            using var archive = new MemoryStream(); package.WriteCanonicalZip(archive); archive.Position = 0;
            var copy = StandardPackageLoader.LoadZip(archive);
            Assert.Equal(package.ContentHash, copy.ContentHash);
            Assert.All(package.Manifest.Files, file => Assert.Equal(package.ReadFile(file.Path), copy.ReadFile(file.Path)));
        }

        [Theory]
        [MemberData(nameof(Releases))]
        public void BasicExampleValidatesForEveryTargetRelease(string release)
        {
            var package = StandardPackageLoader.LoadDirectory(PackagePath);
            var machine = GenerationDocuments.Load<MachineDescription>(File.ReadAllText(Path.Combine(PackagePath, "examples", "machine.json")));
            machine.Target.Release = release;
            Assert.Equal(release, package.ValidateMachine(GenerationDocuments.Canonical(machine)).Target.Release);
        }
    }
}
