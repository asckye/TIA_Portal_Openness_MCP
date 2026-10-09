using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class ImportSelectionWiring
{
    private static readonly string ROOT = Repository.FindRoot(AppContext.BaseDirectory);
    private static readonly string SRC = Path.Combine(ROOT, "src/Engine");
    private static readonly string LOGIC = Path.Combine(ROOT, "src/Logic");
    private static readonly EngineSources Sources = new(ROOT);
    private static string Read(string path) => Repository.ReadSource(path);
    private static bool Has(string source, string value) => source.Contains(value, StringComparison.Ordinal);
    private static int Count(string source, string value) => Regex.Matches(source, Regex.Escape(value)).Count;
    private static int Find(string source, string value, int start = 0)
    {
        var index = source.IndexOf(value, start, StringComparison.Ordinal);
        Assert.True(index >= 0, "Missing source fragment: " + value); return index;
    }
    private static readonly string MCP = Sources.TypeText("PlcBlocksTools");
    private static readonly string SHARED = Sources.Member("BuildPlcProgramImportResponse", owner: "PlcProgramImport");
    private static readonly string BATCH = new EngineSources(ROOT, nativeDirectory: "src/Adapters/Native/Plc").Member("ImportBlocksFromDirectory", owner: "PlcOrganisationAdapter", tool: false);
    private static readonly string PROGRAM = Sources.Member("ImportPlcProgramFromDirectory", owner: "PlcBlocksTools", tool: false);

    [Fact]
    public void NativeOverwriteFlag()
    {
        Assert.Contains("PlcBlockPrimitives.Import(group.Blocks, fi, overwrite ? ImportOptions.Override : ImportOptions.None)", BATCH, StringComparison.Ordinal);
        Assert.DoesNotContain("group.Blocks.Find", BATCH, StringComparison.Ordinal);
        Assert.DoesNotContain("false=Rename", MCP, StringComparison.Ordinal);
        Assert.Contains("bool overwrite = true", BATCH, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectBeforeEveryNativeActionAndDryRun()
    {
        var gate = Find(PROGRAM, "if (conflicts.Count > 0)");
        var returned = Find(PROGRAM, "return PlcProgramImport.BuildPlcProgramImportResponse", gate);
        foreach (var action in new[] { "_session.ImportType", "_session.ImportPlcTagTable", "_session.ImportTechnologyObject", "_session.ImportBlock", "_session.CompileSoftware", "if (dryRun)" })
        {
            Assert.True(returned < Find(PROGRAM, action));
        }
        Assert.DoesNotContain(".GroupBy(", PROGRAM, StringComparison.Ordinal);
        Assert.Contains("x => x.Kind, x => x.ObjectName", PROGRAM, StringComparison.Ordinal);
    }

    [Fact]
    public void RelativeBoundedDiagnostics()
    {
        Assert.Contains("x => x.File.Substring(sourceRoot.Length)", PROGRAM, StringComparison.Ordinal);
        Assert.Contains("conflicts.Take(16)", PROGRAM, StringComparison.Ordinal);
        Assert.Contains("conflictCount={conflicts.Count}", PROGRAM, StringComparison.Ordinal);
        Assert.Contains("truncated=true", PROGRAM, StringComparison.Ordinal);
    }

    [Fact]
    public void FiltersAndDefaultsPreserved()
    {
        Assert.Contains("regex.IsMatch(Path.GetFileNameWithoutExtension(f))", PROGRAM, StringComparison.Ordinal);
        foreach (var parameterDefault in new[] { "bool compileAfter = true", "bool stopOnImportFailure = false", "bool dryRun = false" })
        {
            Assert.Contains(parameterDefault, PROGRAM, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OrderingHonestAndDeterministic()
    {
        Assert.DoesNotContain("correct dependency order", MCP, StringComparison.Ordinal);
        Assert.Contains(new[] { "[\"dependencyResolution\"] = false", "(\"dependencyResolution\", false)" }, form => Has(SHARED, form));
        Assert.Contains(new[] { "[\"importOrdering\"] = ImportSelectionPolicy.OrderingDescription", "(\"importOrdering\", ImportSelectionPolicy.OrderingDescription)" }, form => Has(SHARED, form));
        Assert.Equal(5, Count(PROGRAM, ".ThenBy(x => x.File, StringComparer.Ordinal)"));
    }

    [Fact]
    public void BothProductionProjectsReferencePolicyLibrary()
    {
        Assert.True(File.Exists(Path.Combine(LOGIC, "ModelContextProtocol/Tools/ImportSelectionPolicy.cs")));
        Assert.False(File.Exists(Path.Combine(SRC, "ModelContextProtocol/Tools/ImportSelectionPolicy.cs")));
        foreach (var version in new[] { 20, 21 })
        {
            var project = Read(Path.Combine(SRC, string.Concat("TiaMcp.Engine.V", version, ".csproj")));
            Assert.Contains("<ProjectReference Include=\"../Logic/TiaMcp.Logic.csproj\"", project, StringComparison.Ordinal);
        }
    }
}
