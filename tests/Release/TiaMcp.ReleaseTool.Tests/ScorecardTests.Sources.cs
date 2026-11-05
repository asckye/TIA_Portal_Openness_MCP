using System.Text.Json.Nodes;
using Mono.Cecil;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed partial class ScorecardTests
{
    [Fact]
    public void OfficialCoverageUsesTheSharedFiltersAndMergesOverloads()
    {
        using var tree = new StaticTree();
        string xml = tree.Write("api.xml", """
            <doc><members>
              <member name="M:Siemens.Engineering.Foo.Run(System.Int32)"/>
              <member name="M:Siemens.Engineering.Foo.Run(System.String)"/>
              <member name="P:Siemens.Engineering.Foo.Name"/>
              <member name="M:Siemens.Engineering.Foo.Missing"/>
              <member name="M:Siemens.Engineering.Foo.GetService"/>
              <member name="M:Siemens.Engineering.Private.Foo.Run"/>
              <member name="M:Siemens.Engineering.Compiler.CompileProvider.Compile"/>
              <member name="M:Siemens.Engineering.Foo.#ctor"/>
              <member name="T:Siemens.Engineering.Foo"/>
            </members></doc>
            """);
        var inventory = JsonNode.Parse("""
            {"sites":[{"member":"System.Void Siemens.Engineering.Foo::Run(System.Int32)"},
            {"member":"System.String Siemens.Engineering.Foo::get_Name()"},
            {"member":"System.Void Siemens.Engineering.Foo::Unadvertised()"}]}
            """)!;
        var result = ScorecardCoverage.Count([xml], inventory);
        Assert.Equal(2, result.Called); Assert.Equal(3, result.Documented);
        Assert.Equal(4, CatalogAudits.DocumentedMembers([xml]).Signatures.Count);
    }

    [Fact]
    public void CoverageReadsEmbeddedInventoryAndDistinguishesMissingDllFromMissingXml()
    {
        using var tree = new StaticTree();
        string api = Path.Combine(tree.Root, "sdk");
        tree.Write("sdk/TIA_V21_PublicAPI/V21/net48/Siemens.Engineering.xml", """
            <doc><members><member name="M:Siemens.Engineering.Foo.Run"/></members></doc>
            """);
        tree.Write("sdk/TIA_V21_PublicAPI/V21/net48/Siemens.Engineering.AddIn.xml", "not XML; must be excluded");
        var context = new Scorecard(tree.Root, tree.Root, api); ScorecardCoverage.Collect(context);
        Assert.Contains("No compiled adapter DLL", Row(context, "official-api-share", "21").Detail);
        Assert.Equal("not measured", Row(context, "official-api-share", "20").State);
        string assembly = Path.Combine(tree.Root, "src/PlcWorker/bin/21/Release/net48/TiaMcp.Adapter.21.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(assembly)!);
        using (var module = ModuleDefinition.CreateModule("fixture", ModuleKind.Dll)) module.Write(assembly);
        context = new(tree.Root, tree.Root, api); ScorecardCoverage.Collect(context);
        Assert.Equal("not measured", Row(context, "official-api-share", "21").State);
        using (var module = ModuleDefinition.CreateModule("fixture", ModuleKind.Dll))
        {
            module.Resources.Add(new EmbeddedResource("TiaMcp.NativeCallCoverage.json", ManifestResourceAttributes.Private,
                System.Text.Encoding.UTF8.GetBytes("{\"sites\":[{\"member\":\"System.Void Siemens.Engineering.Foo::Run()\"}]}")));
            module.Write(assembly);
        }
        context = new(tree.Root, tree.Root, api); ScorecardCoverage.Collect(context);
        Assert.Equal(1, Row(context, "official-api-share", "21").Raw);
        Assert.Equal(7, context.Report.Rows.Count(row => row.State == "not measured"));
    }

    [Fact]
    public void SourceMetricsIncludeAllBranchesAndRespectPublicVisibility()
    {
        string source = """
            public class Visible
            {
                /// <summary>Documented.</summary>
                public void Named() { }
                #if OMITTED
                public void Deep(int a, int b, int c, int d, int e, int f)
                { if (true) { while (true) { if (true) { if (true) { break; } } } } }
                #endif
                private void Hidden() { }
            }
            internal class Hidden { public void AlsoHidden() { } }
            """;
        var metrics = ScorecardSource.Measure("src/Visible.cs", source);
        Assert.Equal(4, metrics.Methods); Assert.Equal(1, metrics.Deep); Assert.Equal(1, metrics.Parameters);
        Assert.Equal(3, metrics.Public); Assert.Equal(2, metrics.Undocumented);
        Assert.Equal(2, metrics.Maintainability.Length);
        Assert.All(metrics.Maintainability, value => Assert.InRange(value.Estimate, 0, 100));
        Assert.Contains(metrics.Maintainability, row => row.Type == "Visible");
    }

    [Fact]
    public void DuplicateBlocksMergeOverlappingWindowsAndIgnoreSameFileRepeats()
    {
        string[] lines = Enumerable.Range(0, 13).Select(index => "Call(" + index + ");").ToArray();
        Assert.Equal(1, ScorecardSource.DuplicateBlocks(new Dictionary<string, string[]> { ["a"] = lines, ["b"] = lines }));
        Assert.Equal(0, ScorecardSource.DuplicateBlocks(new Dictionary<string, string[]> { ["a"] = [.. lines, .. lines] }));
    }

    [Fact]
    public void ProjectReferenceCyclesAreCountedWithoutRecursingForever()
    {
        Assert.Equal(1, ScorecardSource.ProjectCycles(new Dictionary<string, string[]>
        {
            ["a"] = ["b"], ["b"] = ["c"], ["c"] = ["a"]
        }));
        Assert.Equal(0, ScorecardSource.ProjectCycles(new Dictionary<string, string[]> { ["a"] = ["b"], ["b"] = [] }));
    }

    [Fact]
    public void ProjectReferencesExpandLocalReleaseVariantsWithoutRunningBuilds()
    {
        using var tree = new StaticTree();
        string first = tree.Write("V20/Adapter.20.csproj", "<Project/>");
        string second = tree.Write("V21/Adapter.21.csproj", "<Project/>");
        string path = tree.Write("Worker.csproj", """
            <Project><PropertyGroup>
              <AdapterFolder Condition="'$(Key)' == '20'">V$(Key)</AdapterFolder>
              <AdapterFolder Condition="'$(Key)' == '21'">V$(Key)</AdapterFolder>
              <AdapterProject>$(AdapterFolder)/Adapter.$(Key).csproj</AdapterProject>
            </PropertyGroup><ItemGroup><ProjectReference Include="$(AdapterProject)"/></ItemGroup></Project>
            """);
        Assert.Equal(new[] { Path.GetFullPath(first), Path.GetFullPath(second) }, ScorecardSource.ProjectReferences(path).Order().ToArray());
        tree.Write("Unresolved.csproj", "<Project><ItemGroup><ProjectReference Include=\"$(Missing)\"/></ItemGroup></Project>");
        Assert.Throws<IOException>(() => ScorecardSource.ProjectReferences(Path.Combine(tree.Root, "Unresolved.csproj")));
    }

    [Fact]
    public void SampleIsDeterministicSelectsTenAndUsesSeedAndSrcFilter()
    {
        string[] paths = [.. Enumerable.Range(0, 40).Select(index => "src/File" + index + ".cs"), "tests/Ignore.cs", "src/Ignore.md"];
        string sample = Scorecard.Sample(paths, "release");
        Assert.Equal(sample, Scorecard.Sample(paths.Reverse(), "release"));
        Assert.NotEqual(sample, Scorecard.Sample(paths, "other"));
        Assert.Equal(10, sample.Split('\n').Count(line => line.StartsWith("src/", StringComparison.Ordinal)));
        Assert.DoesNotContain("Ignore", sample); Assert.Contains("comments explain why", sample);
    }

    [Theory]
    [InlineData("-ReviewedSample", "6")]
    [InlineData("-ReviewedSample", "NaN")]
    [InlineData("-Unknown", "value")]
    public void BadScorecardOptionsExit64(string option, string value)
    {
        Assert.Equal(64, Program.Main(["scorecard", option, value]));
    }

}
