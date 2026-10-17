using System.Xml.Linq;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class CheckAllFixtureRootsTests
{
    // A worker fixture referenced with ReferenceOutputAssembly=false is compiled transitively, but only a traversal
    // root reaches the ordinary bin path its tests open in a fresh checkout (CI runs test-suites without build-solutions).
    [Fact]
    public void EveryFoundationHostWorkerFixtureIsATraversalRootWithItsReferenceProperties()
    {
        string root = Repository.FindRoot();
        const string project = "tests/FoundationHost/TiaMcp.FoundationHost.Tests/TiaMcp.FoundationHost.Tests.csproj";
        var units = CheckAllBuild.WithTestFixtures([new CheckBuild(project, "", [])]).ToArray();
        var fixtures = XDocument.Load(Path.Combine(root, project)).Descendants("ProjectReference")
            .Where(reference => (string?)reference.Attribute("ReferenceOutputAssembly") == "false")
            .Select(reference => (Path: Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(root, Path.GetDirectoryName(project)!, (string)reference.Attribute("Include")!))).Replace('\\', '/'),
                Properties: ((string?)reference.Attribute("AdditionalProperties") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(property => "-p:" + property).ToArray()))
            .Where(reference => reference.Path.StartsWith("tests/WorkerChannel/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, fixtures.Length);
        foreach (var fixture in fixtures)
            Assert.Contains(units, unit => unit.Project == fixture.Path && unit.Properties.SequenceEqual(fixture.Properties));
    }
}
