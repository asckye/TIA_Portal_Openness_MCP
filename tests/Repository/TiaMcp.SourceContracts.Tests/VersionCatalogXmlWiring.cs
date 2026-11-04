using System.Xml.Linq;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed partial class VersionCatalogWiring
{
    [Fact]
    public void FaultFixtureDoesNotPropagateSdkFixturePropertyToWorkerChannel()
    {
        var project = XDocument.Load(Path.Combine(ROOT, "tests/WorkerChannel/TiaMcp.PlcWorker.FaultFixture/TiaMcp.PlcWorker.FaultFixture.csproj"));
        var reference = Assert.Single(project.Descendants("ProjectReference"), node =>
            (node.Attribute("Include")?.Value ?? "").Replace('\\', '/').EndsWith("/WorkerChannel/TiaMcp.WorkerChannel.csproj", StringComparison.Ordinal));
        Assert.Equal("TiaMcpPlcWorkerSdkFixture", reference.Attribute("GlobalPropertiesToRemove")?.Value);
    }

    [Fact]
    public void ConfiguratorUsesKeysNotIndices()
    {
        var source = Read(Path.Combine(ROOT, "src/Studio/Gui/Configuration/ConfigurationView.xaml.cs"));
        var selected = Split(Split(source, "private string SelectedVersion", 1)[1], "private string StatePath", 1)[0];
        Assert.Contains("selected.Key", selected); Assert.DoesNotContain("SelectedIndex", selected);
        Assert.Contains("versions.ItemsSource = TiaVersionCatalog.Runnable", source);
        var version = XDocument.Load(Path.Combine(ROOT, "src/Studio/Gui/Configuration/ConfigurationView.xaml")).Descendants().Single(n => n.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "Version");
        Assert.Equal("Key", version.Attribute("SelectedValuePath")?.Value); Assert.Empty(version.Elements());
    }
    [Fact]
    public void SharedSourceBuildAndPackageInputs()
    {
        var build = Read(Path.Combine(ROOT, "build-tools/release/MergeCheck.cs"));
        Assert.Contains("TiaOpenness.Configuration.Tests.csproj", build);
        Assert.DoesNotContain("/main:TiaMcpConfigurator.Tests", build);
        var project = XDocument.Load(Path.Combine(ROOT, "tests/KeptCode/TiaMcp.KeptCode.Tests/TiaMcp.KeptCode.Tests.csproj"));
        var linked = project.Descendants("Compile").Select(n => (n.Attribute("Include")?.Value ?? "").Replace('\\', '/')).ToArray();
        var fixture = XDocument.Load(Path.Combine(ROOT, "tests/Fixtures/TiaMcp.Offline.Fixtures/OfflineFixtureSources.props"));
        var references = project.Descendants("ProjectReference").Concat(fixture.Descendants("ProjectReference"))
            .Select(n => (n.Attribute("Include")?.Value ?? "").Replace('\\', '/')).ToArray();
        Assert.Contains(references, p => p.EndsWith("/Logic/TiaMcp.Logic.csproj", StringComparison.Ordinal));
        foreach (var suffix in new[] { "Siemens/TiaVersionCatalog.cs", "Siemens/Capability.cs", "CliOptions.cs" })
        { Assert.DoesNotContain(linked, p => p.EndsWith(suffix, StringComparison.Ordinal)); Assert.True(File.Exists(Path.Combine(LOGIC, suffix)), suffix); }
        Assert.Contains("CheckSuite.Run(nameof(TiaVersionCatalogTests), Run)", Read(Path.Combine(ROOT, "tests/KeptCode/TiaMcp.KeptCode.Tests/TiaVersionCatalogTests.cs")));
    }
}
