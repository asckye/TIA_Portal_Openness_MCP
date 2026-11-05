using TiaMcp.Tests.Shared;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

// P8-81d (API audit 2026-10-19, PLC software): the native refusals run before the first native mutation of each path.
public sealed class PlcSoftwareAuditWiring
{
    private static readonly string ROOT = Repository.FindRoot(AppContext.BaseDirectory);
    private static string Read(string path) => SourceFamily.Read(Path.Combine(ROOT, path));
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

    [Fact]
    public void Technology_batch_only_marks_file_effects_at_the_native_export_boundary()
    {
        var source = Read("src/Adapters/Native/Plc/PlcTechnologyExchange.cs");
        var body = Member(source, "public PlcSeedHmiReply ExportTechnologyObjectsToDirectory(");
        Assert.Contains("[\"mayHaveWrittenFiles\"] = false", body, StringComparison.Ordinal);
        Assert.Contains("() => meta[\"mayHaveWrittenFiles\"] = true", body, StringComparison.Ordinal);
        string handler = body.Substring(Find(body, "catch (Exception error)"));
        Assert.DoesNotContain("[\"mayHaveWrittenFiles\"] = true", handler, StringComparison.Ordinal);
        var export = Read("src/Adapters/Native/Plc/EngineeringFileExport.cs");
        Assert.True(Find(export, "Directory.CreateDirectory") < Find(export, "exportStarting?.Invoke()"));
        Assert.True(Find(export, "exportStarting?.Invoke()") < Find(export, "method.Invoke(target, args)"));
    }

    [Fact]
    public void Native_technology_batch_keeps_directory_bounds_and_no_write_preview_on_the_uniform_route()
    {
        var source = Read("src/Adapters/Native/Plc/PlcTechnologyExchange.cs");
        var body = Member(source, "public PlcSeedHmiReply ExportTechnologyObjectsToDirectory(");
        Assert.Contains("part == \"..\"", body, StringComparison.Ordinal);
        Assert.Contains("part.IndexOf('/') >= 0", body, StringComparison.Ordinal);
        Assert.Contains("part.IndexOf('\\\\') >= 0", body, StringComparison.Ordinal);
        Assert.Contains("path.StartsWith(root, StringComparison.OrdinalIgnoreCase)", body, StringComparison.Ordinal);
        Assert.True(Find(body, "if (dryRun) return") < Find(body, "Directory.CreateDirectory"));
        Assert.True(Find(body, "if (hash != expectedPlanHash)") < Find(body, "TechnologyExport(selected[i].To"));
        Assert.DoesNotContain("Documented(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Safe_move_has_no_release_selected_execution_route()
    {
        var body = Member(ORGANISATION, "public HardwareAddressingReply MoveBlockToGroup(");
        Assert.DoesNotContain("ReleaseKey", body, StringComparison.Ordinal);
        Assert.DoesNotContain("#if", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Direct_special_exports_cannot_enable_unbounded_parent_creation()
    {
        var source = Read("src/Adapters/Native/Plc/PlcSpecialExport.cs");
        Assert.DoesNotContain("allowPlannedDirectories:true", source, StringComparison.Ordinal);
        Assert.Contains("plannedExportRoot", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_directory_creation_has_a_refusal_boundary_before_native_publication()
    {
        var source = Read("src/Adapters/Native/Plc/PlcSpecialExportPolicy.cs");
        Assert.Contains("Cannot create the export directory", source, StringComparison.Ordinal);
        Assert.True(Find(source, "Cannot create the export directory") < Find(source, "var recovery=(publish"));
    }

    [Fact]
    public void Move_parent_verification_uses_sdk_proxy_equality_without_weakening_identity()
    {
        var body = Member(ORGANISATION, "public HardwareAddressingReply MoveBlockToGroup(");
        Assert.Contains("object.Equals(PlcOrganisationPrimitives.Parent(candidate), expectedParent)", body, StringComparison.Ordinal);
        Assert.Contains("object.Equals(parent, target)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ReferenceEquals", body, StringComparison.Ordinal);
        Assert.Contains("PlcOrganisationPrimitives.Name(candidate) != name", body, StringComparison.Ordinal);
        Assert.Contains("PlcOrganisationPrimitives.Number(candidate) != number", body, StringComparison.Ordinal);
        Assert.Contains("PlcOrganisationPrimitives.Language(candidate).ToString() != language", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public bool ImportBlock(", "ExistingBlocks(plcSoftware)")]
    [InlineData("public bool ImportType(", "ExistingTypes(plcSoftware.TypeGroup)")]
    [InlineData("public void ImportPlcTagTable(", "ExistingTagTables(plc.TagTableGroup)")]
    public void Single_imports_check_the_window_and_overwrite_before_the_native_import(string signature, string existing)
    {
        var body = Member(EXCHANGE, signature);
        int window = Find(body, "PrepareXmlForImport(importPath)"), target = Find(body, "RequireImportTarget("), mutation =
            Find(body, "MutationStarted = true;");
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
    public void Compatibility_names_share_the_foundation_bridge_approval_boundary()
    {
        var bridge = Read("src/Shared/Host/McpServer.ToolBridge.cs");
        Assert.Contains("PortedFamilies.CompatibilityTargets.ContainsKey(name)", bridge, StringComparison.Ordinal);
        var declarations = Read("src/Adapters/Native/Plc/PlcTagDefinitions.cs");
        Assert.DoesNotContain("table.Tags.Create(", declarations, StringComparison.Ordinal);
        Assert.DoesNotContain("table.UserConstants.Create(", declarations, StringComparison.Ordinal);
        Assert.Contains("Foundation CreatePlcTag/CreatePlcUserConstant route", declarations, StringComparison.Ordinal);
    }

    [Fact]
    public void A_move_verifies_the_export_deletes_imports_and_restores_to_the_original_group()
    {
        var body = Member(ORGANISATION, "public HardwareAddressingReply MoveBlockToGroup(");
        int refusal = Find(body, "PlcMovePolicy.Refusal(block.IsKnowHowProtected, block.IsConsistent)");
        Assert.True(refusal < Find(body, "PlcMovePolicy.ExportDeleteImportRestore("));
        Assert.Contains("PlcOrganisationPrimitives.Import(", body, StringComparison.Ordinal);
        int policy = Find(body, "PlcMovePolicy.ExportDeleteImportRestore(");
        int export = Find(body, "PlcOrganisationPrimitives.Export(block", policy);
        int delete = Find(body, "PlcOrganisationPrimitives.Delete(block)", export);
        int import = Find(body, "PlcOrganisationPrimitives.Import(PlcOrganisationPrimitives.Blocks(target!)", delete);
        int restore = Find(body, "PlcOrganisationPrimitives.Import(PlcOrganisationPrimitives.Blocks((PlcBlockGroup)parent)", import);
        Assert.True(export < delete && delete < import && import < restore);
        Assert.True(Find(body, "if (dryRun) return") < policy);
        Assert.Contains("() => Verify(imported, target!)", body, StringComparison.Ordinal);
        Assert.Contains("() => Verify(restored, parent)", body, StringComparison.Ordinal);
        Assert.Contains("PlcOrganisationPrimitives.Number(candidate) != number", body, StringComparison.Ordinal);
        Assert.Contains("PlcOrganisationPrimitives.Language(candidate).ToString() != language", body, StringComparison.Ordinal);
        Assert.Contains("expectedPlanHash != hash", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Delete(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ExportDocuments", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ImportOptions.Override", body, StringComparison.Ordinal);
        Assert.Contains("ExportOptions.WithDefaults", body, StringComparison.Ordinal);
        Assert.Contains("_session.GetBlock(softwarePath, blockName)", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("inconsistent")][InlineData("cleanup")][InlineData("cleanup-failure")]
    public void A_move_accepts_import_consistency_and_cleans_up_before_restoration(string scenario)
    {
        var body = Member(ORGANISATION, "public HardwareAddressingReply MoveBlockToGroup(");
        var policy = Read("src/Adapters.Contracts/PlcMovePolicy.cs");
        if (scenario == "inconsistent")
        {
            Assert.DoesNotContain("!candidate.IsConsistent", body, StringComparison.Ordinal);
            Assert.Contains("return imported.IsConsistent;", body, StringComparison.Ordinal);
            Assert.Contains("evidence[\"importedConsistent\"] = importTarget();", policy, StringComparison.Ordinal);
            Assert.Contains("Compile the imported block", body, StringComparison.Ordinal);
        }
        else if (scenario == "cleanup")
        {
            int cleanup = Find(policy, "deleteTarget();");
            Assert.True(cleanup < Find(policy, "restoreSource();", cleanup));
            Assert.Contains("evidence[\"targetCleanup\"] = \"deleted\"", policy, StringComparison.Ordinal);
            Assert.Contains("PlcOrganisationPrimitives.Delete(imported!)", body, StringComparison.Ordinal);
        }
        else
        {
            int failed = Find(policy, "evidence[\"targetCleanup\"] = \"failed\"");
            Assert.True(failed < Find(policy, "if (canRestore)", failed));
            Assert.Contains("canRestore = false;", policy, StringComparison.Ordinal);
            Assert.Contains("evidence[\"recoveryOnlyCopy\"] = true", policy, StringComparison.Ordinal);
        }
    }
}
