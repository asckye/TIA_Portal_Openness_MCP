using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

// P8-81d (API audit 2026-10-19, PLC software): the native refusals run before the first native mutation of each path.
public sealed class PlcSoftwareAuditWiring
{
    private static readonly string ROOT = Repository.FindRoot(AppContext.BaseDirectory);
    private static string Read(string path) => Repository.ReadSource(Path.Combine(ROOT, path));
    private static readonly string EXCHANGE = Read("src/Adapters/Native/Plc/PlcArtifactExchange.cs");
    private static readonly string ORGANISATION = Read("src/Adapters/Native/Plc/PlcOrganisationAdapter.cs");
    private static int Find(string source, string value, int start = 0)
    {
        var index = source.IndexOf(value, start, StringComparison.Ordinal);
        Assert.True(index >= 0, "Missing source fragment: " + value); return index;
    }
    private static string Member(string source, string signature)
    {
        int start = Find(source, signature);
        int next = source.IndexOf("\n        public ", start + signature.Length, StringComparison.Ordinal);
        return next < 0 ? source.Substring(start) : source.Substring(start, next - start);
    }

    [Theory]
    [InlineData("public bool ImportBlock(", "ExistingBlocks(plcSoftware)")]
    [InlineData("public bool ImportType(", "ExistingTypes(plcSoftware.TypeGroup)")]
    [InlineData("public void ImportPlcTagTable(", "ExistingTagTables(plc.TagTableGroup)")]
    public void Single_imports_check_the_window_and_overwrite_before_the_native_import(string signature, string existing)
    {
        var body = Member(EXCHANGE, signature);
        int window = Find(body, "PrepareXmlForImport(importPath)"), target = Find(body, "RequireImportTarget("), mutation = Find(body, "MutationStarted = true;");
        Assert.True(window < target && target < mutation);
        Assert.Contains(existing, body, StringComparison.Ordinal);
        Assert.Contains("overwrite ? ImportOptions.Override : ImportOptions.None", body.Substring(mutation), StringComparison.Ordinal);
        Assert.DoesNotContain("ImportOptions.Override)", body.Replace("overwrite ? ImportOptions.Override : ImportOptions.None", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void The_engineering_version_is_never_rewritten()
    {
        Assert.Contains("SimaticMlImportWindow.Require(_session.ReleaseKey, bytes, parameter);", EXCHANGE, StringComparison.Ordinal);
        Assert.DoesNotContain("Regex.Replace", EXCHANGE, StringComparison.Ordinal);
        Assert.DoesNotContain("EngineeringVersion", EXCHANGE, StringComparison.Ordinal);
        var engine = Read("src/Engine/Siemens/Portal/Portal.Blocks.cs");
        Assert.Contains("SimaticMlImportWindow.Require(", engine, StringComparison.Ordinal);
        Assert.DoesNotContain("<Engineering version=\\\"V{major}\\\" />", engine, StringComparison.Ordinal);
    }

    [Fact]
    public void A_directory_import_checks_every_file_before_the_first_native_import()
    {
        var body = Member(EXCHANGE, "public PlcSeedHmiReply ImportBlocksFromDirectory(");
        int plan = Find(body, "planned.Add((file, prepared));"), mutation = Find(body, "MutationStarted = true;");
        Assert.True(Find(body, "RequireImportTarget(") < plan && plan < mutation);
        Assert.Contains("PlcImportOrder.BlockRank(p.Prepared.Kind)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unresolved_tag_table_group_is_refused_not_redirected_to_the_root()
    {
        var body = Member(EXCHANGE, "public void ImportPlcTagTable(");
        Assert.Contains("group.Groups.Find(part) ?? throw new PlcSoftwareException(\"NotFound\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("?? root", body, StringComparison.Ordinal);
        Assert.DoesNotContain("TryResolveChildGroupByPath", EXCHANGE, StringComparison.Ordinal);
    }

    [Fact]
    public void A_move_refuses_protected_and_inconsistent_blocks_before_delete_and_compares_the_number()
    {
        var body = Member(ORGANISATION, "public string MoveBlockToGroup(");
        int refusal = Find(body, "PlcMovePolicy.Refusal(block.IsKnowHowProtected, block.IsConsistent)");
        int before = Find(body, "int? numberBefore = PlcMovePolicy.Number(");
        int delete = Find(body, "PlcOrganisationPrimitives.Delete(block);");
        int import = Find(body, "ImportOptions.None);", delete);
        int compare = Find(body, "PlcMovePolicy.NumberChange(numberBefore, numberAfter)", import);
        Assert.True(refusal < before && before < delete && delete < import && import < compare
            && compare < Find(body, "moveVerified = verifyGroup != null && moved != null && numberChange == null;", compare));
        Assert.DoesNotContain("ExportDocuments", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ImportOptions.Override", body, StringComparison.Ordinal);
        Assert.Contains("ExportOptions.WithDefaults", body, StringComparison.Ordinal);
    }
}
